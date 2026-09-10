using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Backup;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Infrastructure.Backup;

public sealed class SqliteBackupService : IBackupService
{
    private const int SupportedSchemaVersion = 6;
    private const string SupportedChecksum = "simulation-v6";
    private readonly DbStore _store;
    private readonly string _backupDirectory;
    private readonly Func<CancellationToken, Task>? _afterReplace;

    public SqliteBackupService(DbStore store, string backupDirectory, Func<CancellationToken, Task>? afterReplace = null)
    {
        _store = store;
        _backupDirectory = Path.GetFullPath(backupDirectory);
        _afterReplace = afterReplace;
    }

    public async Task<Result<BackupDetail>> CreateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Result<BackupDetail>.Success(await _store.RunMaintenanceAsync(async (databasePath, ct) =>
            {
                Directory.CreateDirectory(_backupDirectory);
                var stamp = DateTimeOffset.UtcNow;
                var name = $"MoneyCounter-{stamp:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite3";
                var finalPath = InDirectory(name);
                var temporaryPath = finalPath + ".creating";
                try
                {
                    await SnapshotAsync(databasePath, temporaryPath, ct).ConfigureAwait(false);
                    await ValidateAsync(temporaryPath, ct).ConfigureAwait(false);
                    File.Move(temporaryPath, finalPath);
                    var detail = new BackupDetail(name, new FileInfo(finalPath).Length, await HashAsync(finalPath, ct).ConfigureAwait(false), SupportedSchemaVersion, stamp);
                    await WriteManifestAsync(detail, ct).ConfigureAwait(false);
                    return detail;
                }
                finally { DeleteIfExists(temporaryPath); }
            }, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Result<BackupDetail>.Failure(ErrorCodes.StorageUnavailable, "备份未完成，原数据库未被修改。"); }
    }

    public async Task<Result<IReadOnlyList<BackupDetail>>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(_backupDirectory)) return Result<IReadOnlyList<BackupDetail>>.Success([]);
            List<BackupDetail> backups = [];
            foreach (var manifest in Directory.EnumerateFiles(_backupDirectory, "*.manifest.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detail = await ReadManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
                if (detail is not null && IsSafeFileName(detail.FileName) && File.Exists(InDirectory(detail.FileName))) backups.Add(detail);
            }
            return Result<IReadOnlyList<BackupDetail>>.Success(backups.OrderByDescending(x => x.CreatedAtUtc).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Result<IReadOnlyList<BackupDetail>>.Failure(ErrorCodes.StorageUnavailable, "无法读取备份目录。"); }
    }

    public async Task<Result<BackupRestoreResult>> RestoreAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (!IsSafeFileName(fileName)) return Result<BackupRestoreResult>.Failure(ErrorCodes.InvalidRecord, "备份文件名无效。", "FileName");
        try
        {
            var manifest = await ReadManifestAsync(ManifestPath(fileName), cancellationToken).ConfigureAwait(false);
            if (manifest is null || !string.Equals(manifest.FileName, fileName, StringComparison.Ordinal)) return Result<BackupRestoreResult>.Failure(ErrorCodes.RecordNotFound, "备份清单不存在或不完整。");
            var backupPath = InDirectory(fileName);
            if (!File.Exists(backupPath)) return Result<BackupRestoreResult>.Failure(ErrorCodes.RecordNotFound, "备份数据库不存在。");
            if (!string.Equals(manifest.Sha256, await HashAsync(backupPath, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)) return Result<BackupRestoreResult>.Failure(ErrorCodes.InvalidRecord, "备份校验值不匹配，恢复已拒绝。");
            await ValidateAsync(backupPath, cancellationToken).ConfigureAwait(false);
            return await _store.RunMaintenanceAsync((databasePath, ct) => RestoreCoreAsync(databasePath, manifest, backupPath, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Result<BackupRestoreResult>.Failure(ErrorCodes.StorageUnavailable, "恢复未完成；系统已尝试保留或回退到原数据库。"); }
    }

    private async Task<Result<BackupRestoreResult>> RestoreCoreAsync(string databasePath, BackupDetail manifest, string backupPath, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var replacement = Path.Combine(directory, $".restore-{Guid.NewGuid():N}.sqlite3");
        var rollback = Path.Combine(directory, $".rollback-{Guid.NewGuid():N}.sqlite3");
        var replaced = false;
        try
        {
            EnsureSpace(databasePath, backupPath);
            await CheckpointAsync(databasePath, ct).ConfigureAwait(false);
            await SnapshotAsync(databasePath, rollback, ct).ConfigureAwait(false);
            await ValidateAsync(rollback, ct).ConfigureAwait(false);
            File.Copy(backupPath, replacement, overwrite: false);
            await ValidateAsync(replacement, ct).ConfigureAwait(false);
            DeleteSidecars(databasePath);
            File.Move(replacement, databasePath, overwrite: true);
            replaced = true;
            if (_afterReplace is not null) await _afterReplace(ct).ConfigureAwait(false);
            await ValidateAsync(databasePath, ct).ConfigureAwait(false);
            return Result<BackupRestoreResult>.Success(new(manifest, true, "恢复完成；已校验数据库结构、完整性和外键。"));
        }
        catch (OperationCanceledException) when (!replaced) { throw; }
        catch
        {
            if (replaced && File.Exists(rollback))
            {
                try
                {
                    DeleteSidecars(databasePath);
                    File.Copy(rollback, replacement, overwrite: true);
                    File.Move(replacement, databasePath, overwrite: true);
                    await ValidateAsync(databasePath, CancellationToken.None).ConfigureAwait(false);
                    return Result<BackupRestoreResult>.Failure(ErrorCodes.StorageUnavailable, "恢复失败，已自动回退到恢复前数据库。");
                }
                catch { return Result<BackupRestoreResult>.Failure(ErrorCodes.StorageUnavailable, "恢复和自动回退均未完成，请保留数据库目录并联系维护人员。"); }
            }
            return Result<BackupRestoreResult>.Failure(ErrorCodes.StorageUnavailable, "恢复未开始替换原数据库。" );
        }
        finally
        {
            DeleteIfExists(replacement);
            DeleteIfExists(rollback);
        }
    }

    private static void EnsureSpace(string databasePath, string backupPath)
    {
        var required = new FileInfo(databasePath).Length + new FileInfo(backupPath).Length * 2;
        var root = Path.GetPathRoot(Path.GetFullPath(databasePath))!;
        if (new DriveInfo(root).AvailableFreeSpace < required) throw new IOException("Insufficient free space for rollback.");
    }

    private static async Task SnapshotAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        await source.OpenAsync(ct).ConfigureAwait(false);
        await destination.OpenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
    }

    private static async Task CheckpointAsync(string databasePath, CancellationToken ct)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ValidateAsync(string path, CancellationToken ct)
    {
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        using (var integrity = db.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite integrity check failed.");
        }
        using (var foreign = db.CreateCommand())
        {
            foreign.CommandText = "PRAGMA foreign_key_check";
            await using var rows = await foreign.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await rows.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("SQLite foreign key check failed.");
        }
        using var schema = db.CreateCommand();
        schema.CommandText = "SELECT Version,Checksum FROM SchemaMigration ORDER BY Version DESC LIMIT 1";
        await using var reader = await schema.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) != SupportedSchemaVersion || !string.Equals(reader.GetString(1), SupportedChecksum, StringComparison.Ordinal)) throw new InvalidDataException("Backup schema is unsupported.");
    }

    private async Task WriteManifestAsync(BackupDetail detail, CancellationToken ct)
    {
        var path = ManifestPath(detail.FileName);
        var temp = path + ".writing";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(detail, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            File.Move(temp, path);
        }
        finally { DeleteIfExists(temp); }
    }

    private static async Task<BackupDetail?> ReadManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<BackupDetail>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    private string InDirectory(string name)
    {
        if (!IsSafeFileName(name)) throw new InvalidDataException("Unsafe backup file name.");
        var candidate = Path.GetFullPath(Path.Combine(_backupDirectory, name));
        if (!candidate.StartsWith(_backupDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup path escapes its directory.");
        return candidate;
    }

    private string ManifestPath(string fileName) => InDirectory(fileName) + ".manifest.json";

    private static bool IsSafeFileName(string? name) => !string.IsNullOrWhiteSpace(name) && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) && name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) && name.Length <= 180;
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
    private static void DeleteSidecars(string databasePath) { DeleteIfExists(databasePath + "-wal"); DeleteIfExists(databasePath + "-shm"); }
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
}
