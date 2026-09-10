using System.Security.Cryptography;
using System.Text.Json;
using MoneyCounter.Core.Backup;
using MoneyCounter.Core.Registry;
using MoneyCounter.Infrastructure.Backup;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Tests.Backup;

public sealed class BackupTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moneycounter-backup-" + Guid.NewGuid().ToString("N"));
    private DbStore _store = null!;
    private SqliteRegistryService _registry = null!;
    private SqliteBackupService _service = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new DbStore(Path.Combine(_root, "app.sqlite3"));
        await _store.InitializeAsync(Ct);
        _registry = new(_store);
        _service = new(_store, Path.Combine(_root, "backups"));
        Assert.True((await _registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "原始型号", 1000, ""), Ct)).IsSuccess);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CreateAsync_WritesValidatedManifestAndListsBackup()
    {
        var created = await _service.CreateAsync(Ct);
        Assert.True(created.IsSuccess);
        Assert.True(File.Exists(Path.Combine(_root, "backups", created.Value!.FileName)));
        Assert.True(File.Exists(Path.Combine(_root, "backups", created.Value.FileName + ".manifest.json")));
        Assert.Equal(created.Value, Assert.Single((await _service.ListAsync(Ct)).Value!));
        Assert.Equal(await HashAsync(Path.Combine(_root, "backups", created.Value.FileName)), created.Value.Sha256);
    }

    [Fact]
    public async Task RestoreAsync_ReplacesChangedDatabaseWithVerifiedSnapshot()
    {
        var backup = (await _service.CreateAsync(Ct)).Value!;
        Assert.True((await _registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "临时型号", 1000, ""), Ct)).IsSuccess);

        var restored = await _service.RestoreAsync(backup.FileName, Ct);

        Assert.True(restored.IsSuccess);
        var models = (await _registry.ListModelsAsync(new(), Ct)).Value!;
        Assert.Single(models.Items);
        Assert.Equal("原始型号", models.Items[0].ModelName);
    }

    [Fact]
    public async Task RestoreAsync_RejectsHashMismatch_WithoutChangingCurrentDatabase()
    {
        var backup = (await _service.CreateAsync(Ct)).Value!;
        var path = Path.Combine(_root, "backups", backup.FileName);
        await File.AppendAllTextAsync(path, "corrupted", Ct);
        Assert.True((await _registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "当前型号", 1000, ""), Ct)).IsSuccess);

        var restored = await _service.RestoreAsync(backup.FileName, Ct);

        Assert.False(restored.IsSuccess);
        Assert.Equal(2, (await _registry.ListModelsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task RestoreAsync_RollsBackWhenPostReplaceValidationPathFails()
    {
        var backup = (await _service.CreateAsync(Ct)).Value!;
        Assert.True((await _registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "恢复前新增", 1000, ""), Ct)).IsSuccess);
        var faulting = new SqliteBackupService(_store, Path.Combine(_root, "backups"), _ => Task.FromException(new IOException("injected failure")));

        var restored = await faulting.RestoreAsync(backup.FileName, Ct);

        Assert.False(restored.IsSuccess);
        var models = (await _registry.ListModelsAsync(new(), Ct)).Value!;
        Assert.Equal(2, models.TotalCount);
        Assert.Contains(models.Items, x => x.ModelName == "恢复前新增");
    }

    [Fact]
    public async Task RestoreAsync_RejectsUnsupportedSchemaBeforeReplacement()
    {
        var backup = (await _service.CreateAsync(Ct)).Value!;
        var backupPath = Path.Combine(_root, "backups", backup.FileName);
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backupPath};Mode=ReadWrite;Pooling=False"))
        {
            await db.OpenAsync(Ct);
            using var command = db.CreateCommand(); command.CommandText = "UPDATE SchemaMigration SET Version=7 WHERE Version=6";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var altered = backup with { Sha256 = await HashAsync(backupPath) };
        await File.WriteAllTextAsync(backupPath + ".manifest.json", JsonSerializer.Serialize(altered), Ct);
        Assert.True((await _registry.CreateModelAsync(new(Guid.NewGuid(), "厂商", "当前型号", 1000, ""), Ct)).IsSuccess);

        var restored = await _service.RestoreAsync(backup.FileName, Ct);

        Assert.False(restored.IsSuccess);
        Assert.Equal(2, (await _registry.ListModelsAsync(new(), Ct)).Value!.TotalCount);
    }

    [Fact]
    public async Task CreateAsync_WaitsForCurrentReadAndRejectsNewDatabaseWorkDuringMaintenance()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = _store.ReadAsync(async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return true;
        }, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);

        var backup = _service.CreateAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.ReadAsync((_, _) => Task.FromResult(true), Ct));
        release.TrySetResult();

        Assert.True(await reader);
        Assert.True((await backup).IsSuccess);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, Ct));
    }
}
