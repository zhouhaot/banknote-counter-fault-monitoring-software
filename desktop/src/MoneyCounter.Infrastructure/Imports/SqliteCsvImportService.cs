using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MoneyCounter.Core;
using MoneyCounter.Core.Imports;
using MoneyCounter.Infrastructure.Storage;

namespace MoneyCounter.Infrastructure.Imports;

public sealed class SqliteCsvImportService(DbStore store) : ICsvImportService
{
    public string GetTemplate(ImportKind kind) => Enum.IsDefined(kind) ? string.Join(',', CsvParser.Headers[(int)kind]) + "\r\n" : throw new ArgumentOutOfRangeException(nameof(kind));
    public string GetErrorCsv(IEnumerable<CsvIssue> issues)
    {
        static string Escape(string value) { if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        return "row,field,code,message\r\n" + string.Concat(issues.Select(x => string.Join(',', new[] { x.Row.ToString(CultureInfo.InvariantCulture), x.Field, x.Code, x.Message }.Select(Escape)) + "\r\n"));
    }

    public async Task<Result<CsvPreview>> PreviewAsync(ImportKind kind, string absolutePath, CancellationToken ct = default)
    {
        var file = await ReadAsync(kind, absolutePath, ct); if (!file.IsSuccess) return Result<CsvPreview>.Failure(file.Error!.Code, file.Error.Message, file.Error.Field);
        var bytes = file.Value!; var document = await Task.Run(() => CsvParser.Parse(kind, bytes, ct), ct).ConfigureAwait(false);
        if (document.Errors.Count == 0) await store.ReadAsync(async (db, token) => { using var tx = db.BeginTransaction(); await ValidateAsync(db, tx, kind, document, token); return true; }, ct);
        return Result<CsvPreview>.Success(new(kind, Path.GetFileName(absolutePath), Hash(bytes), bytes.Length, document.RowCount, document.Errors.AsReadOnly()));
    }

    public async Task<Result<CsvCommitResult>> CommitAsync(Guid operationId, ImportKind kind, string absolutePath, string expectedHash, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty || !Enum.IsDefined(kind) || expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) return Result<CsvCommitResult>.Failure(ErrorCodes.InvalidRecord, "操作标识或预览校验值无效。");
        string payload = Hash(Encoding.UTF8.GetBytes($"CSV:{kind}:{expectedHash.ToUpperInvariant()}"));
        var existing = await store.ReadAsync((db, token) => ReadReceiptAsync(db, null, operationId, payload, token), ct).ConfigureAwait(false);
        if (existing is not null) return existing;
        var file = await ReadAsync(kind, absolutePath, ct).ConfigureAwait(false); if (!file.IsSuccess) return Result<CsvCommitResult>.Failure(file.Error!.Code, file.Error.Message, file.Error.Field);
        var bytes = file.Value!; var hash = Hash(bytes);
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) return Result<CsvCommitResult>.Failure(ErrorCodes.ConcurrentChange, "文件内容已改变，请重新预览。");
        var document = await Task.Run(() => CsvParser.Parse(kind, bytes, ct), ct).ConfigureAwait(false);
        if (document.Errors.Count > 0) return Result<CsvCommitResult>.Success(new(null, document.Errors.AsReadOnly()));
        return await store.WriteAsync(async (db, tx, token) =>
        {
            var receipt = await ReadReceiptAsync(db, tx, operationId, payload, token);
            if (receipt is not null) return receipt;
            await ValidateAsync(db, tx, kind, document, token);
            if (document.Errors.Count > 0) return Result<CsvCommitResult>.Success(new(null, document.Errors.AsReadOnly()));
            var now = DateTimeOffset.UtcNow;
            using var batchCommand = Command(db, tx, "INSERT INTO ImportBatch(Kind,FileName,Sha256,RowCount,ImportedAtUtc) VALUES($kind,$name,$hash,$rows,$now); SELECT last_insert_rowid();", ("$kind", kind.ToString()), ("$name", Path.GetFileName(absolutePath)), ("$hash", hash), ("$rows", document.RowCount), ("$now", now.ToString("O")));
            long batchId = Convert.ToInt64(await batchCommand.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            foreach (var row in document.Rows)
            {
                token.ThrowIfCancellationRequested(); var v = row.Values;
                string sql = kind switch
                {
                    ImportKind.Model => "INSERT INTO Model(Manufacturer,ModelName,RatedCountLife,Notes,IsActive,Revision,Source,ImportBatchId,CreatedAtUtc,UpdatedAtUtc) VALUES($v0,$v1,$life,$v3,1,0,'CSV',$batch,$now,$now)",
                    ImportKind.Device => "INSERT INTO Device(AssetCode,ModelId,CommissionedOn,PurchasedOn,Location,ResponsiblePerson,Notes,IsActive,Revision,Source,ImportBatchId,CreatedAtUtc,UpdatedAtUtc) VALUES($v0,(SELECT Id FROM Model WHERE Manufacturer=$v1 AND ModelName=$v2),NULLIF($v3,''),NULLIF($v4,''),$v5,$v6,$v7,1,0,'CSV',$batch,$now,$now)",
                    _ => "INSERT INTO StatusRecord(DeviceId,RecordedAt,Status,CumulativeCount,Notes,Source,ImportBatchId,CreatedAtUtc) VALUES((SELECT Id FROM Device WHERE AssetCode=$v0),$v1,$v2,$count,$v4,'CSV',$batch,$now)"
                };
                using var insert = Command(db, tx, sql, ("$batch", batchId), ("$now", now.UtcDateTime.ToString("O")));
                for (int i = 0; i < v.Length; i++) insert.Parameters.AddWithValue("$v" + i, v[i]);
                if (kind == ImportKind.Model) insert.Parameters.AddWithValue("$life", v[2].Length == 0 ? DBNull.Value : long.Parse(v[2], CultureInfo.InvariantCulture));
                if (kind == ImportKind.Status) insert.Parameters.AddWithValue("$count", long.Parse(v[3], CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(token);
            }
            var batch = new ImportBatchDetail(batchId, kind, Path.GetFileName(absolutePath), hash, document.RowCount, now);
            using var saveReceipt = Command(db, tx, "INSERT INTO OperationReceipt VALUES($id,$hash,'ImportBatch',$batch,$json,$now)", ("$id", operationId.ToString("N")), ("$hash", payload), ("$batch", batchId), ("$json", JsonSerializer.Serialize(batch)), ("$now", now.ToString("O")));
            await saveReceipt.ExecuteNonQueryAsync(token); token.ThrowIfCancellationRequested();
            return Result<CsvCommitResult>.Success(new(batch, Array.Empty<CsvIssue>()));
        }, ct);
    }

    private static async Task<Result<CsvCommitResult>?> ReadReceiptAsync(SqliteConnection db, SqliteTransaction? tx, Guid operationId, string payload, CancellationToken ct)
    {
        using var command = Command(db, tx, "SELECT PayloadHash,ResultKind,ResultJson FROM OperationReceipt WHERE OperationId=$id", ("$id", operationId.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        if (reader.GetString(0) != payload || reader.GetString(1) != "ImportBatch")
            return Result<CsvCommitResult>.Failure(ErrorCodes.ConcurrentChange, "操作标识已用于其他内容。");
        var batch = JsonSerializer.Deserialize<ImportBatchDetail>(reader.GetString(2)) ?? throw new InvalidDataException("Invalid import receipt");
        return Result<CsvCommitResult>.Success(new(batch, Array.Empty<CsvIssue>()));
    }
    public Task<Result<PagedResult<ImportBatchDetail>>> ListHistoryAsync(int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > 200) return Task.FromResult(Result<PagedResult<ImportBatchDetail>>.Failure(ErrorCodes.InvalidRecord, "分页参数无效。"));
        return store.ReadAsync(async (db, token) =>
        {
            using var tx = db.BeginTransaction(); using var count = Command(db, tx, "SELECT COUNT(*) FROM ImportBatch WHERE Kind<>''");
            long total = Convert.ToInt64(await count.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            using var query = Command(db, tx, "SELECT Id,Kind,FileName,Sha256,RowCount,ImportedAtUtc FROM ImportBatch WHERE Kind<>'' ORDER BY Id DESC LIMIT $size OFFSET $offset", ("$size", pageSize), ("$offset", ((long)page - 1) * pageSize));
            using var reader = await query.ExecuteReaderAsync(token); var items = new List<ImportBatchDetail>();
            while (await reader.ReadAsync(token)) items.Add(new(reader.GetInt64(0), Enum.Parse<ImportKind>(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            return Result<PagedResult<ImportBatchDetail>>.Success(new(items.AsReadOnly(), page, pageSize, total));
        }, ct);
    }

    private static async Task ValidateAsync(SqliteConnection db, SqliteTransaction tx, ImportKind kind, CsvDocument document, CancellationToken ct)
    {
        var models = new Dictionary<(string, string), (long Id, bool Available)>(); var devices = new Dictionary<string, (long Id, bool Available)>(StringComparer.Ordinal);
        using (var command = Command(db, tx, "SELECT Id,Manufacturer,ModelName,Source,IsActive FROM Model"))
        using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) models.Add((reader.GetString(1), reader.GetString(2)), (reader.GetInt64(0), reader.GetString(3) != "SIMULATED" && reader.GetInt64(4) == 1));
        if (kind != ImportKind.Model)
        {
            using var command = Command(db, tx, "SELECT Id,AssetCode,Source,IsActive FROM Device"); using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) devices.Add(reader.GetString(1), (reader.GetInt64(0), reader.GetString(2) != "SIMULATED" && reader.GetInt64(3) == 1));
        }
        void Issue(CsvRow row, string field, string code, string message) => document.Errors.Add(new(row.Number, field, code, message));
        foreach (var row in document.Rows)
        {
            ct.ThrowIfCancellationRequested(); var v = row.Values;
            if (kind == ImportKind.Model) { if (models.ContainsKey((v[0], v[1]))) Issue(row, "model_name", "DUPLICATE_DATABASE", "制造商和型号已存在。"); }
            else if (kind == ImportKind.Device)
            {
                if (devices.ContainsKey(v[0])) Issue(row, "asset_code", "DUPLICATE_DATABASE", "资产编号已存在。");
                if (!models.TryGetValue((v[1], v[2]), out var model)) Issue(row, "model_name", "MISSING_DEPENDENCY", "引用型号不存在。");
                else if (!model.Available) Issue(row, "model_name", "SOURCE_OR_INACTIVE", "只能引用有效的人工或 CSV 型号。");
            }
            else if (!devices.TryGetValue(v[0], out var device)) Issue(row, "asset_code", "MISSING_DEPENDENCY", "引用设备不存在。");
            else if (!device.Available) Issue(row, "asset_code", "SOURCE_OR_INACTIVE", "只能引用有效的人工或 CSV 设备。");
        }
        if (kind != ImportKind.Status) return;
        foreach (var group in document.Rows.GroupBy(x => x.Values[0]))
        {
            if (!devices.TryGetValue(group.Key, out var device) || !device.Available) continue;
            var timeline = new List<(DateTimeOffset Time, long Count, CsvRow? Row)>();
            using (var command = Command(db, tx, "SELECT RecordedAt,CumulativeCount FROM StatusRecord WHERE DeviceId=$device", ("$device", device.Id)))
            using (var reader = await command.ExecuteReaderAsync(ct)) while (await reader.ReadAsync(ct)) timeline.Add((DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetInt64(1), null));
            var existing = timeline.Select(x => x.Time).ToHashSet();
            foreach (var row in group)
            {
                var time = DateTimeOffset.Parse(row.Values[1], CultureInfo.InvariantCulture);
                if (existing.Contains(time)) Issue(row, "recorded_at", "DUPLICATE_DATABASE", "同一时刻已存在记录。");
                timeline.Add((time, long.Parse(row.Values[3], CultureInfo.InvariantCulture), row));
            }
            timeline.Sort((a, b) => a.Time.CompareTo(b.Time));
            for (int i = 1; i < timeline.Count; i++) if (timeline[i].Count < timeline[i - 1].Count && (timeline[i].Row ?? timeline[i - 1].Row) is { } offending) Issue(offending, "cumulative_count", "MONOTONICITY", "累计读数必须与完整时间线保持单调不减。");
        }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    { var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value); return command; }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task<Result<byte[]>> ReadAsync(ImportKind kind, string path, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)) return Result<byte[]>.Failure(ErrorCodes.InvalidRecord, "请选择有效类型和绝对路径的 CSV 文件。");
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > CsvParser.MaximumBytes) return Result<byte[]>.Failure(ErrorCodes.InvalidRecord, "CSV 文件不能超过 5 MiB。");
            using var output = new MemoryStream(); var buffer = new byte[81920];
            while (true) { int read = await file.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > CsvParser.MaximumBytes) return Result<byte[]>.Failure(ErrorCodes.InvalidRecord, "CSV 文件不能超过 5 MiB。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
            return Result<byte[]>.Success(output.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Result<byte[]>.Failure(ErrorCodes.StorageUnavailable, "无法读取 CSV 文件，请检查路径、权限和文件占用。"); }
    }
}

