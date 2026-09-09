using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoneyCounter.Core.Imports;

namespace MoneyCounter.Infrastructure.Imports;

internal sealed record CsvRow(int Number, string[] Values);
internal sealed record CsvDocument(List<CsvRow> Rows, List<CsvIssue> Errors, int RowCount);
internal static class CsvParser
{
    internal const int MaximumBytes = 5 * 1024 * 1024;
    internal static readonly string[][] Headers = [
        ["manufacturer", "model_name", "rated_count_life", "notes"],
        ["asset_code", "manufacturer", "model_name", "commissioned_on", "purchased_on", "location", "responsible_person", "notes"],
        ["asset_code", "recorded_at", "status", "cumulative_count", "notes"]];

    internal static CsvDocument Parse(ImportKind kind, byte[] bytes, CancellationToken ct)
    {
        var errors = new List<CsvIssue>(); var rows = new List<CsvRow>();
        void Error(int row, string field, string code, string message) => errors.Add(new(row, field, code, message));
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { Error(0, "file", "INVALID_ENCODING", "CSV 必须使用 UTF-8 编码。"); return new(rows, errors, 0); }
        var records = new List<string[]>(); var fields = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, closed = false, started = false;
        void Cell() { fields.Add(cell.ToString()); cell.Clear(); closed = false; started = false; }
        void Record() { Cell(); records.Add(fields.ToArray()); fields.Clear(); }
        for (int i = 0; i < text.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else { quoted = false; closed = true; } }
                else cell.Append(ch);
            }
            else if (ch == ',') Cell();
            else if (ch is '\r' or '\n') { if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; Record(); }
            else if (ch == '"' && !started && !closed && cell.Length == 0) { quoted = true; started = true; }
            else if (closed || ch == '"') { Error(records.Count + 1, "row", "MALFORMED_CSV", "CSV 引号格式无效。"); return new(rows, errors, Math.Max(0, records.Count - 1)); }
            else { cell.Append(ch); started = true; }
            if (cell.Length > 2000) { Error(records.Count + 1, "row", "CELL_TOO_LONG", "单元格不能超过 2000 个字符。"); return new(rows, errors, Math.Max(0, records.Count - 1)); }
            if (records.Count > 10001) { Error(10002, "row", "ROW_LIMIT_EXCEEDED", "数据不能超过 10000 行。"); return new(rows, errors, 10001); }
        }
        if (quoted) { Error(records.Count + 1, "row", "MALFORMED_CSV", "CSV 引号未闭合。"); return new(rows, errors, Math.Max(0, records.Count - 1)); }
        if (started || closed || cell.Length > 0 || fields.Count > 0) Record();
        if (records.Count == 0) { Error(1, "header", "MISSING_HEADER", "CSV 必须包含表头。"); return new(rows, errors, 0); }
        var headers = Headers[(int)kind];
        if (!records[0].SequenceEqual(headers)) { Error(1, "header", "INVALID_HEADER", "CSV 表头名称及顺序必须与模板完全一致。"); return new(rows, errors, Math.Max(0, records.Count - 1)); }
        if (records.Count > 10001) Error(10002, "row", "ROW_LIMIT_EXCEEDED", "数据不能超过 10000 行。");
        if (records.Count == 1) Error(2, "row", "EMPTY_DATA", "CSV 没有数据行。");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 1; r < Math.Min(records.Count, 10001); r++)
        {
            ct.ThrowIfCancellationRequested(); var raw = records[r]; int row = r + 1, before = errors.Count;
            if (raw.Length != headers.Length) { Error(row, "row", "COLUMN_COUNT", "该行列数与表头不一致。"); continue; }
            if (raw.Any(x => x.Contains('\0'))) { Error(row, "row", "NUL_CHARACTER", "单元格不能包含 NUL 字符。"); continue; }
            var v = raw.Select(x => x.Trim()).ToArray();
            void Required(int i) { if (v[i].Length == 0) Error(row, headers[i], "REQUIRED", "字段不能为空。"); }
            void Length(int i, int max) { if (v[i].Length > max) Error(row, headers[i], "FIELD_TOO_LONG", $"字段不能超过 {max} 字符。"); }
            void Integer(int i, bool positive, bool optional) { if (optional && v[i].Length == 0) return; if (!long.TryParse(v[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < (positive ? 1 : 0)) Error(row, headers[i], "INVALID_INTEGER", positive ? "必须是 Int64 范围内的正整数。" : "必须是 Int64 范围内的非负整数。"); }
            Required(0);
            if (kind == ImportKind.Model) { Required(1); Length(0, 100); Length(1, 100); Integer(2, true, true); }
            else if (kind == ImportKind.Device)
            {
                Required(1); Required(2); Length(0, 64); Length(1, 100); Length(2, 100); Length(5, 120); Length(6, 100);
                foreach (var i in new[] { 3, 4 }) if (v[i].Length > 0 && !DateOnly.TryParseExact(v[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Error(row, headers[i], "INVALID_DATE", "日期必须为 YYYY-MM-DD。");
            }
            else
            {
                Length(0, 64); Required(1); Required(2); Integer(3, false, false);
                if (!Regex.IsMatch(v[1], @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) || !DateTimeOffset.TryParse(v[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) Error(row, "recorded_at", "INVALID_DATETIME", "时间必须为包含明确偏移的 ISO 8601 格式（精度最多 7 位）。");
                else v[1] = instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                if (!new[] { "RUNNING", "STOPPED", "FAULT", "MAINTENANCE", "RETIRED" }.Contains(v[2])) Error(row, "status", "INVALID_ENUM", "运行状态无效。");
            }
            if (errors.Count != before) continue;
            var identity = kind == ImportKind.Device ? v[0] : System.Text.Json.JsonSerializer.Serialize(new[] { v[0], v[1] });
            if (!identities.Add(identity)) Error(row, "row", "DUPLICATE_IN_FILE", "文件中存在重复业务标识。");
            rows.Add(new(row, v));
        }
        return new(rows, errors, records.Count - 1);
    }
}
