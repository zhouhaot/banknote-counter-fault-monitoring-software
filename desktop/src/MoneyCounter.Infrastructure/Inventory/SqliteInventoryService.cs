using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Inventory;
using MoneyCounter.Infrastructure.Storage;
namespace MoneyCounter.Infrastructure.Inventory;

public sealed class SqliteInventoryService(DbStore store) : IInventoryService
{
    private const string ItemSelect = "SELECT c.Id,c.Name,c.Unit,c.Notes,c.IsActive,c.Revision,c.Source,COALESCE((SELECT SUM(m.QuantityMinor) FROM InventoryMovement m WHERE m.ConsumableId=c.Id),0) FROM Consumable c";
    private const string MoveSelect = "SELECT m.Id,m.ConsumableId,c.Name,c.Unit,m.MovementType,m.QuantityMinor,m.OccurredAt,m.Reason,m.DeviceId,d.AssetCode,m.FaultId,m.ReversesId,EXISTS(SELECT 1 FROM InventoryMovement r WHERE r.ReversesId=m.Id),m.Source FROM InventoryMovement m JOIN Consumable c ON c.Id=m.ConsumableId LEFT JOIN Device d ON d.Id=m.DeviceId";
    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static Result<T> Fail<T>(string message, string field = "", string code = ErrorCodes.InvalidRecord) => Result<T>.Failure(code, message, field);
    private static bool TextOk(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max;
    public Task<Result<ConsumableDetail>> CreateConsumableAsync(CreateConsumableCommand c, CancellationToken cancellationToken = default) => ItemWrite(c.OperationId, c, "ConsumableCreate", null, null, c.Name, c.Unit, c.Notes, false, cancellationToken);
    public Task<Result<ConsumableDetail>> UpdateConsumableAsync(UpdateConsumableCommand c, CancellationToken cancellationToken = default) => ItemWrite(c.OperationId, c, "ConsumableUpdate", c.Id, c.ExpectedRevision, c.Name, c.Unit, c.Notes, false, cancellationToken);
    public Task<Result<ConsumableDetail>> DeactivateConsumableAsync(DeactivateConsumableCommand c, CancellationToken cancellationToken = default) => ItemWrite(c.OperationId, c, "ConsumableDeactivate", c.Id, c.ExpectedRevision, "", "", "", true, cancellationToken);
    private Task<Result<ConsumableDetail>> ItemWrite(Guid op, object payload, string kind, long? id, long? revision, string name, string unit, string notes, bool deactivate, CancellationToken token) => Execute(op, payload, kind, async (db, tx, ct) =>
    {
        if (!deactivate)
        {
            if (!TextOk(name, 120)) return Fail<ConsumableDetail>("名称必填且最多120字。", "Name");
            if (!TextOk(unit, 32)) return Fail<ConsumableDetail>("单位必填且最多32字。", "Unit");
            if (notes is null || notes.Length > 2000) return Fail<ConsumableDetail>("备注最多2000字。", "Notes");
        }
        if (id.HasValue)
        {
            var prior = (await Items(db, tx, " WHERE c.Id=$id", ct, ("$id", id))).SingleOrDefault();
            if (prior is null) return Fail<ConsumableDetail>("耗材不存在。", "Id", ErrorCodes.RecordNotFound);
            if (prior.Revision != revision) return Fail<ConsumableDetail>("耗材已修改，请刷新。", "Revision", ErrorCodes.ConcurrentChange);
            if (!deactivate && prior.Unit != unit.Trim())
            {
                using var count = Cmd(db, tx, "SELECT COUNT(*) FROM InventoryMovement WHERE ConsumableId=$id", ("$id", id));
                if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) > 0) return Fail<ConsumableDetail>("已有流水的耗材不能更换单位。", "Unit");
            }
        }
        if (!deactivate)
        {
            using var duplicate = Cmd(db, tx, "SELECT COUNT(*) FROM Consumable WHERE Name=$name AND ($id IS NULL OR Id<>$id)", ("$name", name.Trim()), ("$id", id));
            if (Convert.ToInt64(await duplicate.ExecuteScalarAsync(ct)) > 0) return Fail<ConsumableDetail>("耗材名称已存在。", "Name", ErrorCodes.DuplicateRecord);
        }
        using var q = Cmd(db, tx, deactivate ? "UPDATE Consumable SET IsActive=0,Revision=Revision+1 WHERE Id=$id" : id.HasValue ? "UPDATE Consumable SET Name=$name,Unit=$unit,Notes=$notes,Revision=Revision+1 WHERE Id=$id" : "INSERT INTO Consumable(Name,Unit,Notes,Source) VALUES($name,$unit,$notes,'MANUAL');", ("$id", id), ("$name", name.Trim()), ("$unit", unit.Trim()), ("$notes", notes.Trim()));
        await q.ExecuteNonQueryAsync(ct);
        if (!id.HasValue) { using var last = Cmd(db, tx, "SELECT last_insert_rowid()"); id = Convert.ToInt64(await last.ExecuteScalarAsync(ct)); }
        return Result<ConsumableDetail>.Success((await Items(db, tx, " WHERE c.Id=$id", ct, ("$id", id))).Single());
    }, x => x.Id, token);
    public Task<Result<MovementDetail>> PostMovementAsync(PostMovementCommand c, CancellationToken cancellationToken = default) => Execute(c.OperationId, c, "MovementPost", async (db, tx, ct) =>
    {
        if (!InventoryValues.MovementTypes.Contains(c.MovementType)) return Fail<MovementDetail>("流水类型无效。", "MovementType");
        if (c.Quantity == 0 || c.Quantity > InventoryValues.MaximumQuantity || c.Quantity < -InventoryValues.MaximumQuantity || decimal.Round(c.Quantity, 2) != c.Quantity || c.MovementType != "ADJUST" && c.Quantity < 0) return Fail<MovementDetail>("数量最多两位小数且绝对值不超过9999999999.99；普通流水必须为正数。", "Quantity");
        var minor = checked((long)(c.Quantity * 100)); if (c.MovementType == "ISSUE") minor = -minor;
        if (c.MovementType == "ISSUE" && c.DeviceId is null || c.FaultId is not null && c.DeviceId is null) return Fail<MovementDetail>("领用或关联故障时必须指定设备。", "DeviceId");
        return await Insert(db, tx, c.ConsumableId, c.MovementType, minor, c.OccurredAt, c.Reason, c.DeviceId, c.FaultId, null, ct);
    }, x => x.Id, cancellationToken);
    public Task<Result<MovementDetail>> ReverseMovementAsync(ReverseMovementCommand c, CancellationToken cancellationToken = default) => Execute(c.OperationId, c, "MovementReverse", async (db, tx, ct) =>
    {
        var old = (await Movements(db, tx, " WHERE m.Id=$id", ct, ("$id", c.Id))).SingleOrDefault();
        if (old is null) return Fail<MovementDetail>("流水不存在。", "Id", ErrorCodes.RecordNotFound);
        if (old.HasDirectReversal) return Fail<MovementDetail>("该流水已有直接反向更正。", "Id", ErrorCodes.InvalidTransition);
        if (c.OccurredAt < old.OccurredAt) return Fail<MovementDetail>("更正时间不能早于原流水。", "OccurredAt");
        if (old.Source == "SIMULATED") return Fail<MovementDetail>("模拟流水不能创建人工更正。", "Source", ErrorCodes.SourceMismatch);
        return await Insert(db, tx, old.ConsumableId, "REVERSAL", checked(-old.QuantityMinor), c.OccurredAt, c.Reason, old.DeviceId, old.FaultId, old.Id, ct);
    }, x => x.Id, cancellationToken);
    private static async Task<Result<MovementDetail>> Insert(SqliteConnection db, SqliteTransaction tx, long item, string kind, long minor, DateTimeOffset at, string reason, long? device, long? fault, long? reverses, CancellationToken ct)
    {
        if (!TextOk(reason, 500)) return Fail<MovementDetail>("原因必填且最多500字。", "Reason");
        var consumable = (await Items(db, tx, " WHERE c.Id=$id", ct, ("$id", item))).SingleOrDefault();
        if (consumable is null) return Fail<MovementDetail>("耗材不存在。", "ConsumableId", ErrorCodes.RecordNotFound);
        if (!consumable.IsActive && reverses is null) return Fail<MovementDetail>("耗材已停用。", "ConsumableId");
        if (consumable.Source == "SIMULATED") return Fail<MovementDetail>("模拟耗材不能关联人工流水。", "Source", ErrorCodes.SourceMismatch);
        foreach (var reference in new[] { ("Device", device), ("Fault", fault) })
        {
            if (reference.Item2 is null) continue;
            using var q = Cmd(db, tx, "SELECT Source" + (reference.Item1 == "Fault" ? ",DeviceId" : "") + " FROM " + reference.Item1 + " WHERE Id=$id", ("$id", reference.Item2)); using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Fail<MovementDetail>("关联设备或故障不存在。", reference.Item1 + "Id", ErrorCodes.RecordNotFound);
            if (r.GetString(0) == "SIMULATED") return Fail<MovementDetail>("数据来源不一致。", "Source", ErrorCodes.SourceMismatch);
            if (reference.Item1 == "Fault" && r.GetInt64(1) != device) return Fail<MovementDetail>("故障不属于所选设备。", "FaultId");
        }
        long balance; try { balance = checked(consumable.StockMinor + minor); } catch (OverflowException) { return Fail<MovementDetail>("累计库存超出整数范围。", "Quantity"); }
        if (balance < 0) return Fail<MovementDetail>("库存不足。", "Quantity");
        using var insert = Cmd(db, tx, "INSERT INTO InventoryMovement(ConsumableId,MovementType,QuantityMinor,OccurredAt,Reason,DeviceId,FaultId,ReversesId,Source,CreatedAtUtc) VALUES($item,$kind,$minor,$at,$reason,$device,$fault,$reverses,'MANUAL',$now); SELECT last_insert_rowid();", ("$item", item), ("$kind", kind), ("$minor", minor), ("$at", Stamp(at)), ("$reason", reason.Trim()), ("$device", device), ("$fault", fault), ("$reverses", reverses), ("$now", Stamp(DateTimeOffset.UtcNow)));
        var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        return Result<MovementDetail>.Success((await Movements(db, tx, " WHERE m.Id=$id", ct, ("$id", id))).Single());
    }
    public Task<Result<PagedResult<ConsumableDetail>>> ListConsumablesAsync(ConsumableQuery q, CancellationToken cancellationToken = default)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200 || q.Search?.Length > 200) return Task.FromResult(Fail<PagedResult<ConsumableDetail>>("查询条件无效。"));
        return store.ReadAsync(async (db, ct) =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var where = " WHERE ($active IS NULL OR c.IsActive=$active) AND ($search IS NULL OR instr(c.Name,$search)>0)";
            (string, object?)[] p = [("$active", q.IsActive.HasValue ? q.IsActive.Value ? 1 : 0 : null), ("$search", q.Search), ("$limit", q.PageSize), ("$offset", ((long)q.Page - 1) * q.PageSize)];
            using var count = Cmd(db, tx, "SELECT COUNT(*) FROM Consumable c" + where, p); var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            var rows = await Items(db, tx, where + " ORDER BY c.Name,c.Id LIMIT $limit OFFSET $offset", ct, p); tx.Commit(); return Result<PagedResult<ConsumableDetail>>.Success(new(rows, q.Page, q.PageSize, total));
        }, cancellationToken);
    }
    public Task<Result<PagedResult<MovementDetail>>> ListMovementsAsync(MovementQuery q, CancellationToken cancellationToken = default)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200 || q.ConsumableId is <= 0 || q.DeviceId is <= 0 || q.FaultId is <= 0 || q.From > q.To) return Task.FromResult(Fail<PagedResult<MovementDetail>>("查询条件无效。"));
        return store.ReadAsync(async (db, ct) =>
        {
            using var tx = db.BeginTransaction(deferred: true);
            var where = " WHERE ($item IS NULL OR m.ConsumableId=$item) AND ($device IS NULL OR m.DeviceId=$device) AND ($fault IS NULL OR m.FaultId=$fault) AND ($from IS NULL OR m.OccurredAt>=$from) AND ($to IS NULL OR m.OccurredAt<=$to)";
            (string, object?)[] p = [("$item", q.ConsumableId), ("$device", q.DeviceId), ("$fault", q.FaultId), ("$from", q.From.HasValue ? Stamp(q.From.Value) : null), ("$to", q.To.HasValue ? Stamp(q.To.Value) : null), ("$limit", q.PageSize), ("$offset", ((long)q.Page - 1) * q.PageSize)];
            using var count = Cmd(db, tx, "SELECT COUNT(*) FROM InventoryMovement m" + where, p); var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            var rows = await Movements(db, tx, where + " ORDER BY m.OccurredAt DESC,m.Id DESC LIMIT $limit OFFSET $offset", ct, p); tx.Commit(); return Result<PagedResult<MovementDetail>>.Success(new(rows, q.Page, q.PageSize, total));
        }, cancellationToken);
    }
    private Task<Result<T>> Execute<T>(Guid op, object payload, string kind, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, Func<T, long> id, CancellationToken token)
    {
        if (op == Guid.Empty) return Task.FromResult(Fail<T>("操作标识不能为空。", "OperationId"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + JsonSerializer.Serialize(payload))));
        return store.WriteAsync(async (db, tx, ct) =>
        {
            using (var q = Cmd(db, tx, "SELECT PayloadHash,ResultKind,ResultJson FROM OperationReceipt WHERE OperationId=$op", ("$op", op.ToString("N"))))
            using (var r = await q.ExecuteReaderAsync(ct)) if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) != hash || r.GetString(1) != kind) return Fail<T>("操作标识不能复用于不同内容。", "OperationId");
                return Result<T>.Success(JsonSerializer.Deserialize<T>(r.GetString(2)) ?? throw new InvalidDataException("Invalid receipt"));
            }
            var result = await action(db, tx, ct); if (!result.IsSuccess) return result;
            using var receipt = Cmd(db, tx, "INSERT INTO OperationReceipt(OperationId,PayloadHash,ResultKind,ResultId,ResultJson,CreatedAtUtc) VALUES($op,$hash,$kind,$id,$json,$now)", ("$op", op.ToString("N")), ("$hash", hash), ("$kind", kind), ("$id", id(result.Value!)), ("$json", JsonSerializer.Serialize(result.Value)), ("$now", Stamp(DateTimeOffset.UtcNow))); await receipt.ExecuteNonQueryAsync(ct); return result;
        }, token);
    }
    private static SqliteCommand Cmd(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] p)
    { var q = db.CreateCommand(); q.Transaction = tx; q.CommandText = sql; foreach (var (k, v) in p) q.Parameters.AddWithValue(k, v ?? DBNull.Value); return q; }
    private static async Task<List<ConsumableDetail>> Items(SqliteConnection db, SqliteTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] p)
    {
        using var q = Cmd(db, tx, ItemSelect + suffix, p); using var r = await q.ExecuteReaderAsync(ct); var rows = new List<ConsumableDetail>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetString(6), r.GetInt64(7))); return rows;
    }
    private static async Task<List<MovementDetail>> Movements(SqliteConnection db, SqliteTransaction? tx, string suffix, CancellationToken ct, params (string, object?)[] p)
    {
        using var q = Cmd(db, tx, MoveSelect + suffix, p); using var r = await q.ExecuteReaderAsync(ct); var rows = new List<MovementDetail>();
        while (await r.ReadAsync(ct)) rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.GetInt64(12) != 0, r.GetString(13))); return rows;
    }
}
