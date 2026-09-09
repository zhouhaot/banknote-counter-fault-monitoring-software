namespace MoneyCounter.Core.Imports;

public enum ImportKind { Model, Device, Status }
public sealed record CsvIssue(int Row, string Field, string Code, string Message);
public sealed record CsvPreview(ImportKind Kind, string FileName, string Sha256, long ByteCount, int RowCount, IReadOnlyList<CsvIssue> Errors)
{
    public bool CanCommit => RowCount > 0 && Errors.Count == 0;
}
public sealed record ImportBatchDetail(long Id, ImportKind Kind, string FileName, string Sha256, int RowCount, DateTimeOffset ImportedAtUtc);
public sealed record CsvCommitResult(ImportBatchDetail? Batch, IReadOnlyList<CsvIssue> Errors)
{
    public bool IsCommitted => Batch is not null && Errors.Count == 0;
}
public interface ICsvImportService
{
    Task<Result<CsvPreview>> PreviewAsync(ImportKind kind, string absolutePath, CancellationToken ct = default);
    Task<Result<CsvCommitResult>> CommitAsync(Guid operationId, ImportKind kind, string absolutePath, string expectedHash, CancellationToken ct = default);
    Task<Result<PagedResult<ImportBatchDetail>>> ListHistoryAsync(int page = 1, int pageSize = 50, CancellationToken ct = default);
    string GetTemplate(ImportKind kind);
    string GetErrorCsv(IEnumerable<CsvIssue> issues);
}
