namespace MoneyCounter.Core.Backup;

public sealed record BackupDetail(string FileName, long ByteCount, string Sha256, int SchemaVersion, DateTimeOffset CreatedAtUtc);
public sealed record BackupRestoreResult(BackupDetail Backup, bool Restored, string Message);

public interface IBackupService
{
    Task<Result<BackupDetail>> CreateAsync(CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<BackupDetail>>> ListAsync(CancellationToken cancellationToken = default);
    Task<Result<BackupRestoreResult>> RestoreAsync(string fileName, CancellationToken cancellationToken = default);
}
