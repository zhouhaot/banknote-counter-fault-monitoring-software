using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Maintenance;
using MoneyCounter.Infrastructure.Storage;
namespace MoneyCounter.Infrastructure.Maintenance;

public sealed class SqliteMaintenanceService(DbStore store) : IMaintenanceService
{
    private const string Select = "SELECT f.Id,f.FaultNo,f.DeviceId,d.AssetCode,f.SourceAnomalyId,f.RegisteredAt,f.FaultType,f.Severity,f.Description,f.Status,f.StartedAt,f.ClosedAt,f.FinalResult,f.Revision,f.Source FROM Fault f JOIN Device d ON d.Id=f.DeviceId";
    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static Result<FaultDetail> Fail(string message, string field = "", string code = ErrorCodes.InvalidRecord) => Result<FaultDetail>.Failure(code, message, field);
    private static bool TextValid(string? text, int max = 2000) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= max;
    private static Result<FaultDetail>? Validate(string type, string severity, string description)
    {
        if (!FaultValues.Types.Contains(type)) return Fail("故障类型无效。", "FaultType");
        if (!FaultValues.Severities.Contains(severity)) return Fail("严重程度无效。", "Severity");
        return TextValid(description) ? null : Fail("故障描述不能为空且不能超过 2000 字。", "Description");
    }
    public Task<Result<FaultDetail>> CreateFaultAsync(CreateFaultCommand c, CancellationToken cancellationToken = default) => Execute(c.OperationId, c, "FaultCreate", async (db, tx, ct) =>
    {
        var invalid = Validate(c.FaultType, c.Severity, c.Description); if (invalid is not null) return invalid;
        using (var q = Cmd(db, tx, "SELECT IsActive,Source FROM Device WHERE Id=$id", ("$id", c.DeviceId)))
        using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return Fail("设备不存在。", "DeviceId", ErrorCodes.RecordNotFound);
            if (r.GetInt64(0) == 0) return Fail("设备已停用。", "DeviceId");
            if (r.GetString(1) == "SIMULATED") return Fail("模拟设备不能关联人工故障。", "DeviceId", ErrorCodes.SourceMismatch);
        }
        return await Insert(db, tx, c.DeviceId, null, c.RegisteredAt, c.FaultType, c.Severity, c.Description, "MANUAL", null, null, ct);
    }, cancellationToken);
    public Task<Result<FaultDetail>> ConvertAnomalyAsync(ConvertAnomalyCommand c, CancellationToken cancellationToken = default) => Execute(c.OperationId, c, "FaultConvert", async (db, tx, ct) =>
    {
        var invalid = Validate(c.FaultType, c.Severity, c.Description); if (invalid is not null) return invalid;
        long device; string source; object? batch, dataset;
        using (var q = Cmd(db, tx, "SELECT a.DeviceId,a.Status,a.Revision,a.DiscoveredAt,a.Source,a.ImportBatchId,a.SimulationDatasetId,d.IsActive,d.Source,d.SimulationDatasetId FROM Anomaly a JOIN Device d ON d.Id=a.DeviceId WHERE a.Id=$id", ("$id", c.AnomalyId)))
        using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return Fail("异常不存在。", "AnomalyId", ErrorCodes.RecordNotFound);
            if (r.GetInt64(2) != c.ExpectedRevision) return Fail("异常已修改，请刷新。", "Revision", ErrorCodes.ConcurrentChange);
            if (r.GetString(1) != "OPEN") return Fail("异常已关闭或转换。", "Status", ErrorCodes.InvalidTransition);
            if (c.RegisteredAt < Time(r.GetString(3))) return Fail("登记时间不能早于异常发现时间。", "RegisteredAt");
            if (r.GetInt64(7) == 0) return Fail("设备已停用。", "DeviceId");
            device = r.GetInt64(0); source = r.GetString(4); batch = r.IsDBNull(5) ? null : r.GetInt64(5); dataset = r.IsDBNull(6) ? null : r.GetInt64(6);
            if ((source == "SIMULATED") != (r.GetString(8) == "SIMULATED") || source == "SIMULATED" && (r.IsDBNull(9) || !Equals(dataset, r.GetInt64(9)))) return Fail("异常与设备的数据来源不一致。", "Source", ErrorCodes.SourceMismatch);
        }
        var result = await Insert(db, tx, device, c.AnomalyId, c.RegisteredAt, c.FaultType, c.Severity, c.Description, source, batch, dataset, ct);
        using var update = Cmd(db, tx, "UPDATE Anomaly SET Status='CONVERTED',Revision=Revision+1 WHERE Id=$id", ("$id", c.AnomalyId));
        await update.ExecuteNonQueryAsync(ct); return result;
    }, cancellationToken);
    private static async Task<Result<FaultDetail>> Insert(SqliteConnection db, SqliteTransaction tx, long device, long? anomaly, DateTimeOffset time, string type, string severity, string description, string source, object? batch, object? dataset, CancellationToken ct)
    {
        var number = $"GZ-{time:yyyyMMdd}-{Guid.NewGuid():N}";
        using var q = Cmd(db, tx, "INSERT INTO Fault(FaultNo,DeviceId,SourceAnomalyId,RegisteredAt,FaultType,Severity,Description,Status,Source,ImportBatchId,SimulationDatasetId,CreatedAtUtc) VALUES($no,$device,$anomaly,$time,$type,$severity,$description,'PENDING',$source,$batch,$dataset,$now);SELECT last_insert_rowid();", ("$no", number), ("$device", device), ("$anomaly", anomaly), ("$time", Stamp(time)), ("$type", type), ("$severity", severity), ("$description", description.Trim()), ("$source", source), ("$batch", batch), ("$dataset", dataset), ("$now", Stamp(DateTimeOffset.UtcNow)));
        var id = Convert.ToInt64(await q.ExecuteScalarAsync(ct)); return Result<FaultDetail>.Success((await Rows(db, tx, " WHERE f.Id=$id", ct, ("$id", id))).Single());
    }
    private static Result<FaultDetail>? Guard(FaultDetail? f, long revision)
    {
        if (f is null) return Fail("故障不存在。", "Id", ErrorCodes.RecordNotFound);
        if (f.Revision != revision) return Fail("故障已修改，请刷新后重试。", "Revision", ErrorCodes.ConcurrentChange);
        return null;
    }
    public Task<Result<FaultDetail>> StartFaultAsync(StartFaultCommand c, CancellationToken cancellationToken = default) => Change(c.OperationId, c, "FaultStart", c.Id, c.ExpectedRevision, async (db, tx, f, ct) =>
    {
        if (f.Status != "PENDING") return Fail("只有待处理故障可以开始处理。", "Status", ErrorCodes.InvalidTransition);
        if (c.StartedAt < f.RegisteredAt) return Fail("开始时间不能早于登记时间。", "StartedAt");
        using var q = Cmd(db, tx, "UPDATE Fault SET Status='IN_PROGRESS',StartedAt=$time WHERE Id=$id", ("$time", Stamp(c.StartedAt)), ("$id", f.Id)); await q.ExecuteNonQueryAsync(ct); return null;
    }, cancellationToken);
    public Task<Result<FaultDetail>> CloseFaultAsync(CloseFaultCommand c, CancellationToken cancellationToken = default) => Change(c.OperationId, c, "FaultClose", c.Id, c.ExpectedRevision, async (db, tx, f, ct) =>
    {
        if (f.Status != "IN_PROGRESS") return Fail("只有处理中故障可以关闭。", "Status", ErrorCodes.InvalidTransition);
        if (!TextValid(c.FinalResult)) return Fail("最终处理结果不能为空且不能超过 2000 字。", "FinalResult");
        using (var q = Cmd(db, tx, "SELECT MAX(RepairedAt) FROM Repair WHERE FaultId=$id", ("$id", f.Id)))
        {
            var latest = await q.ExecuteScalarAsync(ct);
            if (latest is null or DBNull) return Fail("关闭前必须登记维修记录。", "Repairs", ErrorCodes.InvalidTransition);
            if (c.ClosedAt < f.StartedAt || c.ClosedAt < Time((string)latest)) return Fail("关闭时间不能早于开始处理或维修时间。", "ClosedAt");
        }
        using var update = Cmd(db, tx, "UPDATE Fault SET Status='CLOSED',ClosedAt=$time,FinalResult=$result WHERE Id=$id", ("$time", Stamp(c.ClosedAt)), ("$result", c.FinalResult.Trim()), ("$id", f.Id)); await update.ExecuteNonQueryAsync(ct); return null;
    }, cancellationToken);
    public Task<Result<FaultDetail>> AddRepairAsync(AddRepairCommand c, CancellationToken cancellationToken = default) => RepairChange(c.OperationId, c, "RepairAdd", c.FaultId, null, c.ExpectedFaultRevision, c.RepairedAt, c.Action, c.Result, c.Technician, c.Notes, cancellationToken);
    public Task<Result<FaultDetail>> UpdateRepairAsync(UpdateRepairCommand c, CancellationToken cancellationToken = default) => RepairChange(c.OperationId, c, "RepairUpdate", null, c.Id, c.ExpectedFaultRevision, c.RepairedAt, c.Action, c.Result, c.Technician, c.Notes, cancellationToken);
    public Task<Result<FaultDetail>> DeleteRepairAsync(DeleteRepairCommand c, CancellationToken cancellationToken = default) => RepairChange(c.OperationId, c, "RepairDelete", null, c.Id, c.ExpectedFaultRevision, default, "", "", "", "", cancellationToken);
    private Task<Result<FaultDetail>> RepairChange(Guid op, object payload, string kind, long? fault, long? repair, long revision, DateTimeOffset at, string action, string result, string technician, string notes, CancellationToken token) => Execute(op, payload, kind, async (db, tx, ct) =>
    {
        if (repair.HasValue)
        {
            using var q = Cmd(db, tx, "SELECT FaultId FROM Repair WHERE Id=$id", ("$id", repair)); var value = await q.ExecuteScalarAsync(ct);
            if (value is null) return Fail("维修记录不存在。", "Id", ErrorCodes.RecordNotFound); fault = Convert.ToInt64(value);
        }
        var f = (await Rows(db, tx, " WHERE f.Id=$id", ct, ("$id", fault))).SingleOrDefault(); var error = Guard(f, revision); if (error is not null) return error;
        if (f!.Status == "CLOSED") return Fail("已关闭故障的维修记录不可修改。", "Status", ErrorCodes.InvalidTransition);
        if (kind == "RepairAdd" && f.Source == "SIMULATED") return Fail("模拟故障不能关联人工维修记录。", "Source", ErrorCodes.SourceMismatch);
        if (kind != "RepairDelete")
        {
            if (!TextValid(action)) return Fail("维修动作不能为空且不能超过 2000 字。", "Action");
            if (!TextValid(result)) return Fail("维修结果不能为空且不能超过 2000 字。", "Result");
            if (technician is null || technician.Trim().Length > 100) return Fail("维修人员不能超过 100 字。", "Technician");
            if (notes is null || notes.Length > 2000) return Fail("备注不能超过 2000 字。", "Notes");
            if (at < f.RegisteredAt) return Fail("维修时间不能早于登记时间。", "RepairedAt");
        }
        var sql = kind == "RepairDelete" ? "DELETE FROM Repair WHERE Id=$repair" : kind == "RepairUpdate" ? "UPDATE Repair SET RepairedAt=$time,Action=$action,Result=$result,Technician=$technician,Notes=$notes WHERE Id=$repair" : "INSERT INTO Repair(FaultId,RepairedAt,Action,Result,Technician,Notes,Source,CreatedAtUtc) VALUES($fault,$time,$action,$result,$technician,$notes,'MANUAL',$now)";
        using var change = Cmd(db, tx, sql, ("$repair", repair), ("$fault", f.Id), ("$time", Stamp(at)), ("$action", action.Trim()), ("$result", result.Trim()), ("$technician", technician.Trim()), ("$notes", notes.Trim()), ("$now", Stamp(DateTimeOffset.UtcNow))); await change.ExecuteNonQueryAsync(ct);
        return await Revised(db, tx, f.Id, ct);
    }, token);
    private Task<Result<FaultDetail>> Change(Guid op, object payload, string kind, long id, long revision, Func<SqliteConnection, SqliteTransaction, FaultDetail, CancellationToken, Task<Result<FaultDetail>?>> action, CancellationToken token) => Execute(op, payload, kind, async (db, tx, ct) =>
    {
        var f = (await Rows(db, tx, " WHERE f.Id=$id", ct, ("$id", id))).SingleOrDefault(); var error = Guard(f, revision); if (error is not null) return error;
        error = await action(db, tx, f!, ct); return error ?? await Revised(db, tx, id, ct);
    }, token);
    private static async Task<Result<FaultDetail>> Revised(SqliteConnection db, SqliteTransaction tx, long id, CancellationToken ct)
    {
        using var q = Cmd(db, tx, "UPDATE Fault SET Revision=Revision+1 WHERE Id=$id", ("$id", id)); await q.ExecuteNonQueryAsync(ct);
        return Result<FaultDetail>.Success((await Rows(db, tx, " WHERE f.Id=$id", ct, ("$id", id))).Single());
    }
    public Task<Result<FaultDetail>> GetFaultAsync(long id, CancellationToken cancellationToken = default) => store.ReadAsync(async (db, ct) =>
    {
        var row = (await Rows(db, null, " WHERE f.Id=$id", ct, ("$id", id))).SingleOrDefault(); return row is null ? Fail("故障不存在。", "Id", ErrorCodes.RecordNotFound) : Result<FaultDetail>.Success(row);
    }, cancellationToken);
    public Task<Result<PagedResult<FaultDetail>>> ListFaultsAsync(FaultQuery q, CancellationToken cancellationToken = default)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200 || q.DeviceId is <= 0 || q.From > q.To || q.Status is not null && !FaultValues.Statuses.Contains(q.Status) || q.FaultType is not null && !FaultValues.Types.Contains(q.FaultType) || q.Severity is not null && !FaultValues.Severities.Contains(q.Severity)) return Task.FromResult(Result<PagedResult<FaultDetail>>.Failure(ErrorCodes.InvalidRecord, "筛选或分页条件无效。", "Query"));
        return store.ReadAsync(async (db, ct) =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var where = " WHERE ($device IS NULL OR f.DeviceId=$device) AND ($status IS NULL OR f.Status=$status) AND ($type IS NULL OR f.FaultType=$type) AND ($severity IS NULL OR f.Severity=$severity) AND ($from IS NULL OR f.RegisteredAt>=$from) AND ($to IS NULL OR f.RegisteredAt<=$to)";
            (string, object?)[] values = [("$device", q.DeviceId), ("$status", q.Status), ("$type", q.FaultType), ("$severity", q.Severity), ("$from", q.From.HasValue ? Stamp(q.From.Value) : null), ("$to", q.To.HasValue ? Stamp(q.To.Value) : null), ("$limit", q.PageSize), ("$offset", ((long)q.Page - 1) * q.PageSize)];
            using var count = Cmd(db, tx, "SELECT count(*) FROM Fault f" + where, values); var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            var rows = await Rows(db, tx, where + " ORDER BY f.RegisteredAt DESC,f.Id DESC LIMIT $limit OFFSET $offset", ct, values); tx.Commit(); return Result<PagedResult<FaultDetail>>.Success(new(rows, q.Page, q.PageSize, total));
        }, cancellationToken);
    }
    public Task<Result<PagedResult<RepairDetail>>> ListRepairsAsync(long faultId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (faultId < 1 || page < 1 || pageSize is < 1 or > 200) return Task.FromResult(Result<PagedResult<RepairDetail>>.Failure(ErrorCodes.InvalidRecord, "分页条件无效。", "Query"));
        return store.ReadAsync(async (db, ct) =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            using var exists = Cmd(db, tx, "SELECT COUNT(*) FROM Fault WHERE Id=$id", ("$id", faultId));
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct)) == 0) return Result<PagedResult<RepairDetail>>.Failure(ErrorCodes.RecordNotFound, "故障不存在。");
            using var count = Cmd(db, tx, "SELECT count(*) FROM Repair WHERE FaultId=$id", ("$id", faultId)); var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            var rows = new List<RepairDetail>();
            using (var cmd = Cmd(db, tx, "SELECT Id,FaultId,RepairedAt,Action,Result,Technician,Notes,Source FROM Repair WHERE FaultId=$id ORDER BY RepairedAt,Id LIMIT $limit OFFSET $offset", ("$id", faultId), ("$limit", pageSize), ("$offset", ((long)page - 1) * pageSize)))
            using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), r.GetInt64(1), Time(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
            tx.Commit(); return Result<PagedResult<RepairDetail>>.Success(new(rows, page, pageSize, total));
        }, cancellationToken);
    }
    private Task<Result<FaultDetail>> Execute(Guid op, object payload, string kind, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<FaultDetail>>> action, CancellationToken token)
    {
        if (op == Guid.Empty) return Task.FromResult(Fail("操作标识不能为空。", "OperationId"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + JsonSerializer.Serialize(payload))));
        return store.WriteAsync(async (db, tx, ct) =>
        {
            using (var q = Cmd(db, tx, "SELECT PayloadHash,ResultKind,ResultJson FROM OperationReceipt WHERE OperationId=$op", ("$op", op.ToString("N"))))
            using (var r = await q.ExecuteReaderAsync(ct)) if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) != hash || r.GetString(1) != kind) return Fail("操作标识不能用于不同内容。", "OperationId");
                return Result<FaultDetail>.Success(JsonSerializer.Deserialize<FaultDetail>(r.GetString(2)) ?? throw new InvalidDataException("Invalid operation receipt"));
            }
            var result = await action(db, tx, ct); if (!result.IsSuccess) return result;
            using var save = Cmd(db, tx, "INSERT INTO OperationReceipt(OperationId,PayloadHash,ResultKind,ResultId,ResultJson,CreatedAtUtc) VALUES($op,$hash,$kind,$id,$json,$time)", ("$op", op.ToString("N")), ("$hash", hash), ("$kind", kind), ("$id", result.Value!.Id), ("$json", JsonSerializer.Serialize(result.Value)), ("$time", Stamp(DateTimeOffset.UtcNow))); await save.ExecuteNonQueryAsync(ct); return result;
        }, token);
    }
    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value ?? DBNull.Value); return q;
    }
    private static async Task<List<FaultDetail>> Rows(SqliteConnection db, SqliteTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] values)
    {
        using var q = Cmd(db, tx, Select + suffix, values); using var r = await q.ExecuteReaderAsync(ct); var rows = new List<FaultDetail>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), Time(r.GetString(5)), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : Time(r.GetString(10)), r.IsDBNull(11) ? null : Time(r.GetString(11)), r.GetString(12), r.GetInt64(13), r.GetString(14)));
        return rows;
    }
}
