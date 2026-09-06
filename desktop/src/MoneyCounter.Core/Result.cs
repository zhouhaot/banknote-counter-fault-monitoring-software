namespace MoneyCounter.Core;

public sealed record Error(string Code, string Message, string? Field = null);

public sealed class Result<T>
{
    private Result(T? value, Error? error) { Value = value; Error = error; }
    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(string code, string message, string? field = null) => new(default, new Error(code, message, field));
}

public static class ErrorCodes
{
    public const string DuplicateRecord = nameof(DuplicateRecord);
    public const string InvalidRecord = nameof(InvalidRecord);
    public const string RecordNotFound = nameof(RecordNotFound);
    public const string ConcurrentChange = nameof(ConcurrentChange);
    public const string StorageUnavailable = nameof(StorageUnavailable);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
