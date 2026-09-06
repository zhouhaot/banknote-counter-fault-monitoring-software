using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Operations;
using MoneyCounter.Infrastructure.Storage;
namespace MoneyCounter.Infrastructure.Operations;

public sealed class SqliteOperationsService(DbStore store) : IOperationsService
{
    private const string StatusSelect = "SELECT s.Id,s.DeviceId,d.AssetCode,s.RecordedAt,s.Status,s.CumulativeCount,s.CumulativeCount-(SELECT p.CumulativeCount FROM StatusRecord p WHERE p.DeviceId=s.DeviceId AND p.RecordedAt<s.RecordedAt ORDER BY p.RecordedAt DESC LIMIT 1),s.Notes,s.Source FROM StatusRecord s JOIN Device d ON d.Id=s.DeviceId";
    private const string AnomalySelect = "SELECT a.Id,a.DeviceId,d.AssetCode,a.DiscoveredAt,a.Description,a.Status,a.HandlingNotes,a.ClosedAt,a.Revision,a.Source FROM Anomaly a JOIN Device d ON d.Id=a.DeviceId";
    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static Result<T> Invalid<T>(string field, string message) => Result<T>.Failure(ErrorCodes.InvalidRecord, message, field);

    public Task<Result<StatusDetail>> RecordStatusAsync(RecordStatusCommand command, CancellationToken cancellationToken = default) => Execute(command.OperationId, command, "StatusRecord", async (db, tx, ct) =>
    {
        if (!DeviceStates.All.Contains(command.Status)) return Invalid<StatusDetail>("Status", "运行状态无效。");
        if (command.CumulativeCount < 0) return Invalid<StatusDetail>("CumulativeCount", "累计读数必须是非负整数。");
        if (command.Notes?.Length > 2000) return Invalid<StatusDetail>("Notes", "备注不能超过 2000 个字符。");
        var error = await ValidateDevice(db, tx, command.DeviceId, ct);
        if (error is not null) return Result<StatusDetail>.Failure(error.Code, error.Message, error.Field);
        var timestamp = Stamp(command.RecordedAt);
        foreach (var relation in new[] { "=", "<", ">" })
        {
            using var query = Cmd(db, tx, $"SELECT Id,RecordedAt,CumulativeCount FROM StatusRecord WHERE DeviceId=$device AND RecordedAt{relation}$time ORDER BY RecordedAt {(relation == "<" ? "DESC" : "ASC")} LIMIT 1", ("$device", command.DeviceId), ("$time", timestamp));
            using var reader = await query.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var count = reader.GetInt64(2);
                if (relation == "=" || relation == "<" && command.CumulativeCount < count || relation == ">" && command.CumulativeCount > count)
                    return Result<StatusDetail>.Failure(relation == "=" ? ErrorCodes.DuplicateRecord : ErrorCodes.MonotonicityViolation, $"{(relation == "=" ? "记录时间重复" : relation == "<" ? "累计读数小于前一条记录" : "累计读数大于后一条记录")}（冲突记录 #{reader.GetInt64(0)}：{Time(reader.GetString(1)).ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}，累计读数 {count}）。", relation == "=" ? "RecordedAt" : "CumulativeCount");
            }
        }
        using var insert = Cmd(db, tx, "INSERT INTO StatusRecord(DeviceId,RecordedAt,Status,CumulativeCount,Notes,Source,CreatedAtUtc) VALUES($device,$time,$status,$count,$notes,'MANUAL',$now); SELECT last_insert_rowid();", ("$device", command.DeviceId), ("$time", timestamp), ("$status", command.Status), ("$count", command.CumulativeCount), ("$notes", command.Notes ?? ""), ("$now", Stamp(DateTimeOffset.UtcNow)));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        return Result<StatusDetail>.Success((await StatusRows(db, tx, " WHERE s.Id=$id", ct, ("$id", id))).Single());
    }, cancellationToken);

    public Task<Result<AnomalyDetail>> CreateAnomalyAsync(CreateAnomalyCommand command, CancellationToken cancellationToken = default) => Execute(command.OperationId, command, "AnomalyCreate", async (db, tx, ct) =>
    {
        if (string.IsNullOrWhiteSpace(command.Description) || command.Description.Trim().Length > 2000) return Invalid<AnomalyDetail>("Description", "异常现象不能为空且不能超过 2000 个字符。");
        var error = await ValidateDevice(db, tx, command.DeviceId, ct);
        if (error is not null) return Result<AnomalyDetail>.Failure(error.Code, error.Message, error.Field);
        using var insert = Cmd(db, tx, "INSERT INTO Anomaly(DeviceId,DiscoveredAt,Description,Status,Source,CreatedAtUtc) VALUES($device,$time,$description,'OPEN','MANUAL',$now); SELECT last_insert_rowid();", ("$device", command.DeviceId), ("$time", Stamp(command.DiscoveredAt)), ("$description", command.Description.Trim()), ("$now", Stamp(DateTimeOffset.UtcNow)));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        return Result<AnomalyDetail>.Success((await AnomalyRows(db, tx, " WHERE a.Id=$id", ct, ("$id", id))).Single());
    }, cancellationToken);

    public Task<Result<AnomalyDetail>> CloseAnomalyAsync(CloseAnomalyCommand command, CancellationToken cancellationToken = default) => Execute(command.OperationId, command, "AnomalyClose", async (db, tx, ct) =>
    {
        if (string.IsNullOrWhiteSpace(command.HandlingNotes) || command.HandlingNotes.Length > 2000) return Invalid<AnomalyDetail>("HandlingNotes", "处理说明不能为空且不能超过 2000 个字符。");
        var row = (await AnomalyRows(db, tx, " WHERE a.Id=$id", ct, ("$id", command.Id))).SingleOrDefault();
        if (row is null) return Result<AnomalyDetail>.Failure(ErrorCodes.RecordNotFound, "异常记录不存在。");
        if (row.Revision != command.ExpectedRevision) return Result<AnomalyDetail>.Failure(ErrorCodes.ConcurrentChange, "异常已被修改，请刷新后重试。");
        if (row.Status != "OPEN") return Result<AnomalyDetail>.Failure(ErrorCodes.InvalidTransition, "异常已关闭或已转换，不能再次关闭。", "Status");
        if (command.ClosedAt < row.DiscoveredAt) return Invalid<AnomalyDetail>("ClosedAt", "关闭时间不能早于发现时间。");
        using var update = Cmd(db, tx, "UPDATE Anomaly SET Status='CLOSED',ClosedAt=$time,HandlingNotes=$notes,Revision=Revision+1 WHERE Id=$id AND Revision=$revision AND Status='OPEN'", ("$time", Stamp(command.ClosedAt)), ("$notes", command.HandlingNotes.Trim()), ("$id", command.Id), ("$revision", command.ExpectedRevision));
        if (await update.ExecuteNonQueryAsync(ct) != 1) return Result<AnomalyDetail>.Failure(ErrorCodes.ConcurrentChange, "异常已被修改，请刷新后重试。");
        return Result<AnomalyDetail>.Success((await AnomalyRows(db, tx, " WHERE a.Id=$id", ct, ("$id", command.Id))).Single());
    }, cancellationToken);

    public Task<Result<AnomalyDetail>> GetAnomalyAsync(long id, CancellationToken cancellationToken = default) => store.ReadAsync(async (db, ct) =>
    {
        var row = (await AnomalyRows(db, null, " WHERE a.Id=$id", ct, ("$id", id))).SingleOrDefault();
        return row is null ? Result<AnomalyDetail>.Failure(ErrorCodes.RecordNotFound, "异常记录不存在。") : Result<AnomalyDetail>.Success(row);
    }, cancellationToken);
    public Task<Result<StatusDetail?>> LatestStatusAsync(long deviceId, CancellationToken cancellationToken = default) => store.ReadAsync(async (db, ct) =>
    {
        using var exists = Cmd(db, null, "SELECT COUNT(*) FROM Device WHERE Id=$id", ("$id", deviceId));
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return Result<StatusDetail?>.Failure(ErrorCodes.RecordNotFound, "设备不存在。", "DeviceId");
        return Result<StatusDetail?>.Success((await StatusRows(db, null, " WHERE s.DeviceId=$id ORDER BY s.RecordedAt DESC LIMIT 1", ct, ("$id", deviceId))).SingleOrDefault());
    }, cancellationToken);
    public Task<Result<PagedResult<StatusDetail>>> ListStatusesAsync(StatusQuery query, CancellationToken cancellationToken = default) => List(query.Page, query.PageSize, query.DeviceId, query.Status, query.From, query.To, false, StatusRows, cancellationToken);
    public Task<Result<PagedResult<AnomalyDetail>>> ListAnomaliesAsync(AnomalyQuery query, CancellationToken cancellationToken = default) => List(query.Page, query.PageSize, query.DeviceId, query.Status, query.From, query.To, true, AnomalyRows, cancellationToken);

    private Task<Result<PagedResult<T>>> List<T>(int page, int size, long? device, string? status, DateTimeOffset? from, DateTimeOffset? to, bool anomaly, Func<SqliteConnection, SqliteTransaction?, string, CancellationToken, (string, object?)[], Task<List<T>>> read, CancellationToken cancellationToken)
    {
        if (page < 1 || size is < 1 or > 200 || device is <= 0 || from > to) return Task.FromResult(Invalid<PagedResult<T>>("Query", "分页或筛选条件无效。"));
        if (status is not null && !(anomaly ? new[] { "OPEN", "CLOSED", "CONVERTED" }.Contains(status) : DeviceStates.All.Contains(status))) return Task.FromResult(Invalid<PagedResult<T>>("Status", "状态筛选无效。"));
        return store.ReadAsync(async (db, ct) =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var alias = anomaly ? "a" : "s"; var date = anomaly ? "DiscoveredAt" : "RecordedAt";
            var where = $" WHERE ($device IS NULL OR {alias}.DeviceId=$device) AND ($status IS NULL OR {alias}.Status=$status) AND ($from IS NULL OR {alias}.{date}>=$from) AND ($to IS NULL OR {alias}.{date}<=$to)";
            (string, object?)[] values = [("$device", device), ("$status", status), ("$from", from.HasValue ? Stamp(from.Value) : null), ("$to", to.HasValue ? Stamp(to.Value) : null), ("$limit", size), ("$offset", ((long)page - 1) * size)];
            using var count = Cmd(db, tx, $"SELECT COUNT(*) FROM {(anomaly ? "Anomaly" : "StatusRecord")} {alias}" + where, values);
            var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            var rows = await read(db, tx, where + $" ORDER BY {alias}.{date} DESC,{alias}.Id DESC LIMIT $limit OFFSET $offset", ct, values);
            tx.Commit(); return Result<PagedResult<T>>.Success(new(rows, page, size, total));
        }, cancellationToken);
    }
    private static async Task<Error?> ValidateDevice(SqliteConnection db, SqliteTransaction tx, long device, CancellationToken ct)
    {
        using var command = Cmd(db, tx, "SELECT IsActive,Source FROM Device WHERE Id=$id", ("$id", device)); using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(ErrorCodes.RecordNotFound, "设备不存在。", "DeviceId");
        if (reader.GetInt64(0) == 0) return new(ErrorCodes.InvalidRecord, "设备已停用。", "DeviceId");
        if (reader.GetString(1) == "SIMULATED") return new(ErrorCodes.SourceMismatch, "模拟设备不能关联人工记录。", "DeviceId");
        return null;
    }
    private async Task<Result<T>> Execute<T>(Guid operationId, object payload, string kind, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return Invalid<T>("OperationId", "操作标识不能为空。");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + JsonSerializer.Serialize(payload))));
        return await store.WriteAsync(async (db, tx, ct) =>
        {
            using (var receipt = Cmd(db, tx, "SELECT PayloadHash,ResultKind,ResultJson FROM OperationReceipt WHERE OperationId=$id", ("$id", operationId.ToString("N"))))
            using (var reader = await receipt.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    if (reader.GetString(0) != hash || reader.GetString(1) != kind) return Invalid<T>("OperationId", "操作标识不能用于不同内容。");
                    var value = JsonSerializer.Deserialize<T>(reader.GetString(2));
                    return value is null ? Result<T>.Failure(ErrorCodes.StorageUnavailable, "操作回执已损坏。") : Result<T>.Success(value);
                }
            }
            var result = await action(db, tx, ct); if (!result.IsSuccess) return result;
            var id = result.Value switch { StatusDetail value => value.Id, AnomalyDetail value => value.Id, _ => throw new InvalidOperationException("Unsupported receipt type") };
            using var save = Cmd(db, tx, "INSERT INTO OperationReceipt(OperationId,PayloadHash,ResultKind,ResultId,ResultJson,CreatedAtUtc) VALUES($op,$hash,$kind,$id,$json,$time)", ("$op", operationId.ToString("N")), ("$hash", hash), ("$kind", kind), ("$id", id), ("$json", JsonSerializer.Serialize(result.Value)), ("$time", Stamp(DateTimeOffset.UtcNow)));
            await save.ExecuteNonQueryAsync(ct); return result;
        }, cancellationToken);
    }
    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static async Task<List<StatusDetail>> StatusRows(SqliteConnection db, SqliteTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] values)
    {
        using var command = Cmd(db, tx, StatusSelect + suffix, values); using var reader = await command.ExecuteReaderAsync(ct); var result = new List<StatusDetail>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), Time(reader.GetString(3)), reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7), reader.GetString(8)));
        return result;
    }
    private static async Task<List<AnomalyDetail>> AnomalyRows(SqliteConnection db, SqliteTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] values)
    {
        using var command = Cmd(db, tx, AnomalySelect + suffix, values); using var reader = await command.ExecuteReaderAsync(ct); var result = new List<AnomalyDetail>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), Time(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : Time(reader.GetString(7)), reader.GetInt64(8), reader.GetString(9)));
        return result;
    }
}
