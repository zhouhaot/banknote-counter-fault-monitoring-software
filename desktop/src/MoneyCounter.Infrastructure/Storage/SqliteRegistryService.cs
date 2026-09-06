using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Registry;

namespace MoneyCounter.Infrastructure.Storage;

public sealed class SqliteRegistryService(DbStore store) : IRegistryService
{
    public Task<Result<ModelDetail>> CreateModelAsync(CreateModelCommand command, CancellationToken ct = default) =>
        ExecuteAsync(command.OperationId, command, "Model", async (db, tx, token) =>
        {
            if (string.IsNullOrWhiteSpace(command.Manufacturer)) return Invalid<ModelDetail>("Manufacturer");
            if (string.IsNullOrWhiteSpace(command.ModelName)) return Invalid<ModelDetail>("ModelName");
            if (command.RatedCountLife is <= 0) return Invalid<ModelDetail>("RatedCountLife");
            var now = DateTime.UtcNow;
            try
            {
                var id = await InsertAsync(db, tx, "INSERT INTO Model(Manufacturer,ModelName,RatedCountLife,Notes,IsActive,Revision,Source,CreatedAtUtc,UpdatedAtUtc) VALUES($manufacturer,$name,$life,$notes,1,0,'MANUAL',$now,$now); SELECT last_insert_rowid();", token,
                    ("$manufacturer", command.Manufacturer.Trim()), ("$name", command.ModelName.Trim()), ("$life", command.RatedCountLife), ("$notes", command.Notes ?? ""), ("$now", now.ToString("O")));
                return await GetModelAsync(db, tx, id, token);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Result<ModelDetail>.Failure(ErrorCodes.DuplicateRecord, "型号已存在。", "ModelName"); }
        }, GetModelAsync, ct);

    public Task<Result<ModelDetail>> UpdateModelAsync(UpdateModelCommand command, CancellationToken ct = default) =>
        ExecuteAsync(command.OperationId, command, "Model", async (db, tx, token) =>
        {
            if (string.IsNullOrWhiteSpace(command.Manufacturer)) return Invalid<ModelDetail>("Manufacturer");
            if (string.IsNullOrWhiteSpace(command.ModelName) || command.RatedCountLife is <= 0) return Invalid<ModelDetail>("ModelName");
            try
            {
                var affected = await NonQueryAsync(db, tx, "UPDATE Model SET Manufacturer=$manufacturer,ModelName=$name,RatedCountLife=$life,Notes=$notes,Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision", token,
                    ("$manufacturer", command.Manufacturer.Trim()), ("$name", command.ModelName.Trim()), ("$life", command.RatedCountLife), ("$notes", command.Notes ?? ""), ("$now", DateTime.UtcNow.ToString("O")), ("$id", command.Id), ("$revision", command.ExpectedRevision));
                return affected == 1 ? await GetModelAsync(db, tx, command.Id, token) : await RevisionFailureAsync<ModelDetail>(db, tx, command.Id, "Model", token);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Result<ModelDetail>.Failure(ErrorCodes.DuplicateRecord, "型号已存在。", "ModelName"); }
        }, GetModelAsync, ct);

    public Task<Result<ModelDetail>> GetModelAsync(long id, CancellationToken ct = default) => store.ReadAsync((db, token) => GetModelAsync(db, null, id, token), ct);

    public Task<Result<PagedResult<ModelDetail>>> ListModelsAsync(ModelQuery query, CancellationToken ct = default) => store.ReadAsync((db, token) => ListModelsAsync(db, query, token), ct);

    public Task<Result<ModelDetail>> DeactivateModelAsync(DeactivateCommand command, CancellationToken ct = default) =>
        ExecuteAsync(command.OperationId, command, "Model", async (db, tx, token) =>
        {
            if (await ScalarLongAsync(db, tx, "SELECT COUNT(*) FROM Device WHERE ModelId=$id", token, ("$id", command.Id)) != 0)
                return Result<ModelDetail>.Failure(ErrorCodes.InvalidRecord, "型号仍被设备引用。", "ModelId");
            var affected = await NonQueryAsync(db, tx, "UPDATE Model SET IsActive=0,Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision", token, ("$now", DateTime.UtcNow.ToString("O")), ("$id", command.Id), ("$revision", command.ExpectedRevision));
            return affected == 1 ? await GetModelAsync(db, tx, command.Id, token) : await RevisionFailureAsync<ModelDetail>(db, tx, command.Id, "Model", token);
        }, GetModelAsync, ct);

    public Task<Result<DeviceDetail>> CreateDeviceAsync(CreateDeviceCommand command, CancellationToken ct = default) =>
        ExecuteAsync(command.OperationId, command, "Device", async (db, tx, token) =>
        {
            if (string.IsNullOrWhiteSpace(command.AssetCode)) return Invalid<DeviceDetail>("AssetCode");
            if (!await IsActiveModelAsync(db, tx, command.ModelId, token)) return Result<DeviceDetail>.Failure(ErrorCodes.RecordNotFound, "型号不存在或已停用。", "ModelId");
            try
            {
                var id = await InsertAsync(db, tx, "INSERT INTO Device(AssetCode,ModelId,CommissionedOn,PurchasedOn,Location,ResponsiblePerson,Notes,IsActive,Revision,Source,CreatedAtUtc,UpdatedAtUtc) VALUES($asset,$model,$commissioned,$purchased,$location,$person,$notes,1,0,'MANUAL',$now,$now); SELECT last_insert_rowid();", token,
                    ("$asset", command.AssetCode.Trim()), ("$model", command.ModelId), ("$commissioned", ToDbDate(command.CommissionedOn)), ("$purchased", ToDbDate(command.PurchasedOn)), ("$location", command.Location ?? ""), ("$person", command.ResponsiblePerson ?? ""), ("$notes", command.Notes ?? ""), ("$now", DateTime.UtcNow.ToString("O")));
                return await GetDeviceAsync(db, tx, id, token);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Result<DeviceDetail>.Failure(ErrorCodes.DuplicateRecord, "资产编号已存在。", "AssetCode"); }
        }, GetDeviceAsync, ct);

    public Task<Result<DeviceDetail>> UpdateDeviceAsync(UpdateDeviceCommand command, CancellationToken ct = default) =>
        ExecuteAsync(command.OperationId, command, "Device", async (db, tx, token) =>
        {
            if (string.IsNullOrWhiteSpace(command.AssetCode)) return Invalid<DeviceDetail>("AssetCode");
            if (!await IsActiveModelAsync(db, tx, command.ModelId, token)) return Result<DeviceDetail>.Failure(ErrorCodes.RecordNotFound, "型号不存在或已停用。", "ModelId");
            try
            {
                var affected = await NonQueryAsync(db, tx, "UPDATE Device SET AssetCode=$asset,ModelId=$model,CommissionedOn=$commissioned,PurchasedOn=$purchased,Location=$location,ResponsiblePerson=$person,Notes=$notes,Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision", token,
                    ("$asset", command.AssetCode.Trim()), ("$model", command.ModelId), ("$commissioned", ToDbDate(command.CommissionedOn)), ("$purchased", ToDbDate(command.PurchasedOn)), ("$location", command.Location ?? ""), ("$person", command.ResponsiblePerson ?? ""), ("$notes", command.Notes ?? ""), ("$now", DateTime.UtcNow.ToString("O")), ("$id", command.Id), ("$revision", command.ExpectedRevision));
                return affected == 1 ? await GetDeviceAsync(db, tx, command.Id, token) : await RevisionFailureAsync<DeviceDetail>(db, tx, command.Id, "Device", token);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Result<DeviceDetail>.Failure(ErrorCodes.DuplicateRecord, "资产编号已存在。", "AssetCode"); }
        }, GetDeviceAsync, ct);

    public Task<Result<DeviceDetail>> GetDeviceAsync(long id, CancellationToken ct = default) => store.ReadAsync((db, token) => GetDeviceAsync(db, null, id, token), ct);
    public Task<Result<PagedResult<DeviceDetail>>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default) => store.ReadAsync((db, token) => ListDevicesAsync(db, query, token), ct);
    public Task<Result<DeviceDetail>> DeactivateDeviceAsync(DeactivateCommand command, CancellationToken ct = default) => ExecuteAsync(command.OperationId, command, "Device", async (db, tx, token) =>
    {
        var affected = await NonQueryAsync(db, tx, "UPDATE Device SET IsActive=0,Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision", token, ("$now", DateTime.UtcNow.ToString("O")), ("$id", command.Id), ("$revision", command.ExpectedRevision));
        return affected == 1 ? await GetDeviceAsync(db, tx, command.Id, token) : await RevisionFailureAsync<DeviceDetail>(db, tx, command.Id, "Device", token);
    }, GetDeviceAsync, ct);

    private async Task<Result<T>> ExecuteAsync<T>(Guid operationId, object payload, string kind, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, Func<SqliteConnection, SqliteTransaction?, long, CancellationToken, Task<Result<T>>> load, CancellationToken ct)
    {
        if (operationId == Guid.Empty) return Result<T>.Failure(ErrorCodes.InvalidRecord, "操作标识不能为空。", "OperationId");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
        return await store.WriteAsync(async (db, tx, token) =>
        {
            await using var receipt = db.CreateCommand(); receipt.Transaction = tx; receipt.CommandText = "SELECT PayloadHash,ResultKind,ResultId,ResultJson FROM OperationReceipt WHERE OperationId=$id"; receipt.Parameters.AddWithValue("$id", operationId.ToString("N"));
            await using var reader = await receipt.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal)) return Result<T>.Failure(ErrorCodes.InvalidRecord, "操作标识不能用于不同内容。", "OperationId");
                if (!string.Equals(reader.GetString(1), kind, StringComparison.Ordinal)) return Result<T>.Failure(ErrorCodes.InvalidRecord, "操作标识类型不匹配。", "OperationId");
                var original = JsonSerializer.Deserialize<T>(reader.GetString(3));
                return original is null ? Result<T>.Failure(ErrorCodes.StorageUnavailable, "操作回执已损坏。") : Result<T>.Success(original);
            }
            var result = await action(db, tx, token);
            if (!result.IsSuccess) return result;
            var id = result.Value switch { ModelDetail model => model.Id, DeviceDetail device => device.Id, _ => throw new InvalidOperationException("Receipt needs an entity id.") };
            await NonQueryAsync(db, tx, "INSERT INTO OperationReceipt(OperationId,PayloadHash,ResultKind,ResultId,ResultJson,CreatedAtUtc) VALUES($op,$hash,$kind,$entity,$result,$now)", token, ("$op", operationId.ToString("N")), ("$hash", hash), ("$kind", kind), ("$entity", id), ("$result", JsonSerializer.Serialize(result.Value)), ("$now", DateTime.UtcNow.ToString("O")));
            return result;
        }, ct);
    }

    private static async Task<Result<ModelDetail>> GetModelAsync(SqliteConnection db, SqliteTransaction? tx, long id, CancellationToken ct)
    {
        await using var c = Command(db, tx, "SELECT Id,Manufacturer,ModelName,RatedCountLife,Notes,Source,IsActive,Revision,CreatedAtUtc,UpdatedAtUtc FROM Model WHERE Id=$id", ("$id", id));
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Result<ModelDetail>.Success(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetInt64(6) != 0, r.GetInt64(7), ParseUtc(r.GetString(8)), ParseUtc(r.GetString(9)))) : Result<ModelDetail>.Failure(ErrorCodes.RecordNotFound, "型号不存在。");
    }

    private static async Task<Result<DeviceDetail>> GetDeviceAsync(SqliteConnection db, SqliteTransaction? tx, long id, CancellationToken ct)
    {
        await using var c = Command(db, tx, "SELECT d.Id,d.AssetCode,d.ModelId,m.Manufacturer,m.ModelName,d.CommissionedOn,d.PurchasedOn,d.Location,d.ResponsiblePerson,d.Notes,d.Source,d.IsActive,d.Revision,d.CreatedAtUtc,d.UpdatedAtUtc FROM Device d JOIN Model m ON m.Id=d.ModelId WHERE d.Id=$id", ("$id", id));
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Result<DeviceDetail>.Success(new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), ReadDate(r, 5), ReadDate(r, 6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetInt64(11) != 0, r.GetInt64(12), ParseUtc(r.GetString(13)), ParseUtc(r.GetString(14)))) : Result<DeviceDetail>.Failure(ErrorCodes.RecordNotFound, "设备不存在。");
    }

    private static async Task<Result<PagedResult<ModelDetail>>> ListModelsAsync(SqliteConnection db, ModelQuery q, CancellationToken ct)
    {
        if (!ValidPage(q.Page, q.PageSize)) return Invalid<PagedResult<ModelDetail>>("Page");
        var where = " WHERE ($search='' OR Manufacturer LIKE $pattern OR ModelName LIKE $pattern) AND ($active IS NULL OR IsActive=$active)";
        var total = await ScalarLongAsync(db, null, "SELECT COUNT(*) FROM Model" + where, ct, QueryParameters(q.Search, q.IsActive));
        await using var c = Command(db, null, "SELECT Id,Manufacturer,ModelName,RatedCountLife,Notes,Source,IsActive,Revision,CreatedAtUtc,UpdatedAtUtc FROM Model" + where + " ORDER BY Id LIMIT $take OFFSET $skip", QueryParameters(q.Search, q.IsActive).Append(("$take", q.PageSize)).Append(("$skip", (q.Page - 1) * q.PageSize)).ToArray());
        await using var r = await c.ExecuteReaderAsync(ct); var items = new List<ModelDetail>(); while (await r.ReadAsync(ct)) items.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetInt64(6)!=0,r.GetInt64(7),ParseUtc(r.GetString(8)),ParseUtc(r.GetString(9))));
        return Result<PagedResult<ModelDetail>>.Success(new(items, q.Page, q.PageSize, total));
    }

    private static async Task<Result<PagedResult<DeviceDetail>>> ListDevicesAsync(SqliteConnection db, DeviceQuery q, CancellationToken ct)
    {
        if (!ValidPage(q.Page, q.PageSize)) return Invalid<PagedResult<DeviceDetail>>("Page");
        const string select = " FROM Device d JOIN Model m ON m.Id=d.ModelId WHERE ($search='' OR d.AssetCode LIKE $pattern OR m.Manufacturer LIKE $pattern OR m.ModelName LIKE $pattern) AND ($active IS NULL OR d.IsActive=$active) AND ($model IS NULL OR d.ModelId=$model)";
        var p = QueryParameters(q.Search, q.IsActive).Append(("$model", (object?)q.ModelId)).ToArray(); var total = await ScalarLongAsync(db, null, "SELECT COUNT(*)" + select, ct, p);
        await using var c = Command(db, null, "SELECT d.Id,d.AssetCode,d.ModelId,m.Manufacturer,m.ModelName,d.CommissionedOn,d.PurchasedOn,d.Location,d.ResponsiblePerson,d.Notes,d.Source,d.IsActive,d.Revision,d.CreatedAtUtc,d.UpdatedAtUtc" + select + " ORDER BY d.Id LIMIT $take OFFSET $skip", p.Append(("$take",q.PageSize)).Append(("$skip",(q.Page-1)*q.PageSize)).ToArray());
        await using var r = await c.ExecuteReaderAsync(ct); var items = new List<DeviceDetail>(); while (await r.ReadAsync(ct)) items.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetString(4),ReadDate(r,5),ReadDate(r,6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetInt64(11)!=0,r.GetInt64(12),ParseUtc(r.GetString(13)),ParseUtc(r.GetString(14))));
        return Result<PagedResult<DeviceDetail>>.Success(new(items,q.Page,q.PageSize,total));
    }

    private static async Task<bool> IsActiveModelAsync(SqliteConnection db, SqliteTransaction tx, long id, CancellationToken ct) => await ScalarLongAsync(db, tx, "SELECT COUNT(*) FROM Model WHERE Id=$id AND IsActive=1", ct, ("$id",id)) == 1;
    private static async Task<Result<T>> RevisionFailureAsync<T>(SqliteConnection db, SqliteTransaction tx, long id, string table, CancellationToken ct) => await ScalarLongAsync(db, tx, $"SELECT COUNT(*) FROM {table} WHERE Id=$id", ct, ("$id",id)) == 0 ? Result<T>.Failure(ErrorCodes.RecordNotFound, "记录不存在。") : Result<T>.Failure(ErrorCodes.ConcurrentChange, "记录已被其他操作更新。", "Revision");
    private static Result<T> Invalid<T>(string field) => Result<T>.Failure(ErrorCodes.InvalidRecord, "字段无效。", field);
    private static bool ValidPage(int page, int size) => page > 0 && size is > 0 and <= 200;
    private static (string,object?)[] QueryParameters(string? search, bool? active) => [("$search", search?.Trim() ?? ""), ("$pattern", $"%{search?.Trim() ?? ""}%"), ("$active", active is null ? null : active.Value ? 1 : 0)];
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] p) { var c=db.CreateCommand(); c.Transaction=tx; c.CommandText=sql; foreach(var (n,v) in p)c.Parameters.AddWithValue(n,v??DBNull.Value); return c; }
    private static async Task<long> InsertAsync(SqliteConnection db, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name,object? Value)[] p) => await ScalarLongAsync(db,tx,sql,ct,p);
    private static async Task<long> ScalarLongAsync(SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name,object? Value)[] p) { await using var c=Command(db,tx,sql,p); return Convert.ToInt64(await c.ExecuteScalarAsync(ct)); }
    private static async Task<int> NonQueryAsync(SqliteConnection db, SqliteTransaction tx, string sql, CancellationToken ct, params (string Name,object? Value)[] p) { await using var c=Command(db,tx,sql,p); return await c.ExecuteNonQueryAsync(ct); }
    private static string? ToDbDate(DateOnly? value) => value?.ToString("yyyy-MM-dd"); private static DateOnly? ReadDate(SqliteDataReader r,int i) => r.IsDBNull(i)?null:DateOnly.Parse(r.GetString(i)); private static DateTime ParseUtc(string value) => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
