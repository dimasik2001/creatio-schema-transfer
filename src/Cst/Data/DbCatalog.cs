using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cst.Data;

/// <summary>SQL differences between MSSql and PostgreSql for the table data transfer.</summary>
public sealed class SqlDialect(string engine)
{
    public string Engine { get; } = engine;
    public bool Pg => Engine == "PostgreSql";

    public string Q(string name) => Pg ? "\"" + name.Replace("\"", "\"\"") + "\"" : "[" + name.Replace("]", "]]") + "]";

    /// <summary>A temporary table name of the session (#name on MSSql).</summary>
    public string Temp(string name) => Pg ? name : "#" + name;

    public string Cols(IEnumerable<string> names, string? alias = null) =>
        string.Join(", ", names.Select(n => (alias != null ? alias + "." : "") + Q(n)));

    /// <summary>SELECT cols FROM table [WHERE] ORDER BY ... with a row limit.</summary>
    public string Select(string table, IEnumerable<string> cols, string? where, string? orderBy, long? limit)
    {
        var c = Cols(cols);
        var w = where != null ? " WHERE " + where : "";
        var o = orderBy != null ? " ORDER BY " + orderBy : "";
        if (limit == null) return $"SELECT {c} FROM {Q(table)}{w}{o}";
        return Pg ? $"SELECT {c} FROM {Q(table)}{w}{o} LIMIT {limit}" : $"SELECT TOP ({limit}) {c} FROM {Q(table)}{w}{o}";
    }

    /// <summary>Guid literals for IN (...): validated Guids only, so no injection.</summary>
    public static string GuidList(IEnumerable<Guid> ids) => string.Join(",", ids.Select(g => "'" + g.ToString("D") + "'"));

    /// <summary>An empty staging table with the target's own column types (identity and computed columns left out by the caller).</summary>
    public string CreateStage(string stage, string table, IEnumerable<string> cols) => Pg
        ? $"CREATE TEMP TABLE {Temp(stage)} ON COMMIT DROP AS SELECT {Cols(cols)} FROM {Q(table)} WITH NO DATA"
        : $"SELECT TOP 0 {Cols(cols)} INTO {Temp(stage)} FROM {Q(table)}";

    public string CreateIds(string name) => Pg
        ? $"CREATE TEMP TABLE {Temp(name)} (\"Id\" uuid PRIMARY KEY) ON COMMIT DROP"
        : $"CREATE TABLE {Temp(name)} (Id uniqueidentifier PRIMARY KEY)";

    /// <summary>UPDATE target t from staging s matched by <paramref name="matchCols"/> (default Id).</summary>
    public string UpdateFrom(string table, string stage, IEnumerable<string> setCols, string extraWhere, IEnumerable<string>? matchCols = null)
    {
        var sets = string.Join(", ", setCols.Select(c => Pg ? $"{Q(c)} = s.{Q(c)}" : $"t.{Q(c)} = s.{Q(c)}"));
        var match = string.Join(" AND ", (matchCols ?? ["Id"]).Select(c => $"s.{Q(c)} = t.{Q(c)}"));
        return Pg
            ? $"UPDATE {Q(table)} AS t SET {sets} FROM {Temp(stage)} s WHERE {match}{(extraWhere.Length > 0 ? " AND " + extraWhere : "")}"
            : $"UPDATE t SET {sets} FROM {Q(table)} t JOIN {Temp(stage)} s ON {match}{(extraWhere.Length > 0 ? " WHERE " + extraWhere : "")}";
    }

    private static readonly Regex SafeName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    public static bool IsSafeName(string name) => SafeName.IsMatch(name);
}

/// <summary>A physical column of a table on a stand (read through CstGate).</summary>
public sealed record DbColumn(string Name, string DataType, bool Nullable, bool Identity, bool Computed, bool HasDefault)
{
    /// <summary>Written by the transfer: not identity, not computed, not rowversion.</summary>
    public bool Writable => !Identity && !Computed && DataType is not ("timestamp" or "rowversion");
}

/// <summary>Physical tables and columns of a stand, through CstGate's Query.</summary>
public sealed class DbCatalog(GateClient gate, SqlDialect dialect)
{
    /// <summary>Columns of the given tables that exist (base tables of the current schema); table name → columns in order.</summary>
    public async Task<Dictionary<string, List<DbColumn>>> LoadColumnsAsync(IEnumerable<string> tables)
    {
        var names = tables.Where(SqlDialect.IsSafeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, List<DbColumn>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i += 300)
        {
            var list = string.Join(",", names.Skip(i).Take(300).Select(n => "'" + n + "'"));
            var sql = dialect.Pg
                ? "SELECT c.table_name, c.column_name, c.data_type, c.is_nullable, CASE WHEN c.is_identity = 'YES' THEN 1 ELSE 0 END, " +
                  "CASE WHEN c.is_generated = 'ALWAYS' THEN 1 ELSE 0 END, CASE WHEN c.column_default IS NULL THEN 0 ELSE 1 END " +
                  "FROM information_schema.columns c JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name " +
                  $"WHERE t.table_type = 'BASE TABLE' AND c.table_schema = current_schema() AND c.table_name IN ({list}) ORDER BY c.table_name, c.ordinal_position"
                : "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, " +
                  "COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity'), " +
                  "COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed'), " +
                  "CASE WHEN c.COLUMN_DEFAULT IS NULL THEN 0 ELSE 1 END " +
                  "FROM INFORMATION_SCHEMA.COLUMNS c JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME " +
                  $"WHERE t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_SCHEMA = SCHEMA_NAME() AND c.TABLE_NAME IN ({list}) ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION";
            var res = await gate.QueryAsync(sql);
            foreach (var r in res.Rows)
            {
                var table = Str(r[0]);
                if (!result.TryGetValue(table, out var cols)) result[table] = cols = [];
                cols.Add(new DbColumn(Str(r[1]), Str(r[2]).ToLowerInvariant(), Str(r[3]) == "YES", Int(r[4]) == 1, Int(r[5]) == 1, Int(r[6]) == 1));
            }
        }
        return result;
    }

    /// <summary>Which of the given names are views of the current schema with an "Id" column
    /// (lookups like VwSysSchemaInfo): rows can be looked up there, but nothing is ever written to them.</summary>
    public async Task<HashSet<string>> LoadViewsWithIdAsync(IEnumerable<string> names)
    {
        var list = names.Where(SqlDialect.IsSafeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Count; i += 300)
        {
            var inList = string.Join(",", list.Skip(i).Take(300).Select(n => "'" + n + "'"));
            var sql = dialect.Pg
                ? "SELECT t.table_name FROM information_schema.tables t JOIN information_schema.columns c " +
                  "ON c.table_schema = t.table_schema AND c.table_name = t.table_name AND c.column_name = 'Id' " +
                  $"WHERE t.table_type = 'VIEW' AND t.table_schema = current_schema() AND t.table_name IN ({inList})"
                : "SELECT t.TABLE_NAME FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c " +
                  "ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME AND c.COLUMN_NAME = 'Id' " +
                  $"WHERE t.TABLE_TYPE = 'VIEW' AND t.TABLE_SCHEMA = SCHEMA_NAME() AND t.TABLE_NAME IN ({inList})";
            var res = await gate.QueryAsync(sql);
            foreach (var r in res.Rows) result.Add(Str(r[0]));
        }
        return result;
    }

    internal static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "";
    internal static long Int(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) ? p : 0) : 0;
}
