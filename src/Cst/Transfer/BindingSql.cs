using System.Text;
using System.Text.Json.Nodes;
using Cst.Packaging;

namespace Cst.Transfer;

/// <summary>
/// Builds an SQL script that stores a data binding exactly as a package install does: the binding row in
/// SysPackageSchemaData (Data = data.json bytes, FilterData = filter.json bytes), one SysPackageSchemaDataColumn per
/// descriptor column and one SysPackageDataLcz per culture file. Creatio's InstallData then creates the rows from it,
/// binary columns included. An existing binding (same UId in the package) keeps its row Id and is updated in place.
/// Dialects: PostgreSQL (DBEngineType 2) and MSSql (0).
/// </summary>
public static class BindingSql
{
    public const int MsSql = 0, PostgreSql = 2;

    public sealed record Result(string Sql, List<string> SkippedCultures);

    public static bool Supports(int dbEngineType) => dbEngineType is MsSql or PostgreSql;

    /// <param name="folder">"Data/&lt;Name&gt;/" inside <paramref name="source"/>.</param>
    /// <param name="cultures">Culture names that exist on the target.</param>
    public static Result Build(int dbEngineType, PackageContent source, string folder, Guid bindingUId, string bindingName,
        Guid packageUId, string login, IReadOnlySet<string> cultures)
    {
        Dialect sql = dbEngineType switch
        {
            PostgreSql => new Pg(),
            MsSql => new Ms(),
            _ => throw new NotSupportedException($"SQL bindings are not implemented for DB engine {dbEngineType}.")
        };
        var descriptor = source.ReadJson(folder + "descriptor.json")?["Descriptor"] as JsonObject
                         ?? throw new InvalidDataException($"{bindingName}: descriptor.json missing.");
        var entityUId = Guid.Parse(descriptor["Schema"]!["UId"]!.ToString());
        var entityName = descriptor["Schema"]!["Name"]!.ToString();
        var installType = descriptor["InstallType"]?.GetValue<int>() ?? 0;
        var data = source.Get(folder + "data.json") ?? throw new InvalidDataException($"{bindingName}: data.json missing.");
        var filter = source.Get(folder + "filter.json") ?? [];
        string T(string name) => sql.Name(name);

        var sb = new StringBuilder();
        sb.AppendLine($"-- cst: data binding {bindingName} ({bindingUId}) on {entityName}, stored as a package install does.");
        sb.AppendLine("-- Temporary element: cst runs it once and deletes it.");
        sb.Append(sql.Begin(
            ("user", $"(SELECT {sql.Top1Start}{T("ContactId")} FROM {T("SysAdminUnit")} WHERE {T("Name")} = {sql.Text(login)}{sql.Top1End})"),
            ("pkg", $"(SELECT {T("Id")} FROM {T("SysPackage")} WHERE {T("UId")} = '{packageUId}')"),
            ("schema", $"(SELECT {sql.Top1Start}{T("Id")} FROM {T("SysSchema")} WHERE {T("UId")} = '{entityUId}' AND {T("ExtendParent")} = {sql.False} ORDER BY {T("CreatedOn")}{sql.Top1End})"),
            ("id", null)));
        var user = sql.Var("user"); var pkg = sql.Var("pkg"); var schema = sql.Var("schema"); var id = sql.Var("id");
        sb.AppendLine(sql.Fail($"{pkg} IS NULL", $"cst: package {packageUId} not found"));
        sb.AppendLine(sql.Fail($"{schema} IS NULL", $"cst: entity schema {entityName} ({entityUId}) not found"));
        sb.AppendLine(sql.SelectInto(id, $"{T("Id")}", $"{T("SysPackageSchemaData")} WHERE {T("UId")} = '{bindingUId}' AND {T("SysPackageId")} = {pkg}"));
        sb.AppendLine(sql.Fail($"{id} IS NULL AND EXISTS (SELECT 1 FROM {T("SysPackageSchemaData")} WHERE {T("SysPackageId")} = {pkg} AND {T("Name")} = {sql.Text(bindingName)})",
            $"cst: the package already has a data binding named {bindingName} with another UId"));
        sb.AppendLine(sql.If($"{id} IS NULL",
            sql.Assign(id, $"'{Guid.NewGuid()}'") + "\n" +
            $"    INSERT INTO {T("SysPackageSchemaData")} ({Cols(T, "Id", "CreatedOn", "CreatedById", "ModifiedOn", "ModifiedById", "UId", "Name", "SysPackageId", "IsLocked", "IsChanged", "SysSchemaId", "FilterData", "Data", "InstallType", "NeedInstall", "ProcessListeners")})\n" +
            $"    VALUES ({id}, {sql.Now}, {user}, {sql.Now}, {user}, '{bindingUId}', {sql.Text(bindingName)}, {pkg}, {sql.True}, {sql.False}, {schema},\n" +
            $"      {sql.Bytes(filter)}, {sql.Bytes(data)}, {installType}, {sql.True}, 0);",
            $"    UPDATE {T("SysPackageSchemaData")} SET {T("Name")} = {sql.Text(bindingName)}, {T("SysSchemaId")} = {schema}, {T("FilterData")} = {sql.Bytes(filter)},\n" +
            $"      {T("Data")} = {sql.Bytes(data)}, {T("InstallType")} = {installType}, {T("NeedInstall")} = {sql.True}, {T("ModifiedOn")} = {sql.Now}, {T("ModifiedById")} = {user}\n" +
            $"    WHERE {T("Id")} = {id};"));
        sb.AppendLine($"  DELETE FROM {T("SysPackageSchemaDataColumn")} WHERE {T("SysPackageSchemaDataId")} = {id};");
        foreach (var c in (descriptor["Columns"] as JsonArray ?? []).OfType<JsonObject>())
        {
            sb.AppendLine($"  INSERT INTO {T("SysPackageSchemaDataColumn")} ({Cols(T, "Id", "CreatedOn", "CreatedById", "ModifiedOn", "ModifiedById", "SysPackageSchemaDataId", "PackageSchemaColumnUId", "IsForceUpdate", "IsKey", "ColumnName", "DataValueTypeUId", "ProcessListeners")})");
            sb.AppendLine($"  VALUES ('{Guid.NewGuid()}', {sql.Now}, {user}, {sql.Now}, {user}, {id}, '{Guid.Parse(c["ColumnUId"]!.ToString())}',");
            sb.AppendLine($"    {sql.Bool(c["IsForceUpdate"])}, {sql.Bool(c["IsKey"])}, {sql.Text(c["ColumnName"]!.ToString())}, '{Guid.Parse(c["DataTypeValueUId"]!.ToString())}', 0);");
        }
        sb.AppendLine($"  DELETE FROM {T("SysPackageDataLcz")} WHERE {T("SysPackageSchemaDataId")} = {id};");
        var skipped = new List<string>();
        var lczPrefix = folder + "Localization/data.";
        foreach (var file in source.Files.Keys.Where(k => k.StartsWith(lczPrefix, StringComparison.OrdinalIgnoreCase)
                                                          && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var culture = file[lczPrefix.Length..^".json".Length];
            if (!cultures.Contains(culture)) { skipped.Add(culture); continue; }
            sb.AppendLine($"  INSERT INTO {T("SysPackageDataLcz")} ({Cols(T, "Id", "CreatedOn", "CreatedById", "ModifiedOn", "ModifiedById", "SysPackageSchemaDataId", "SysCultureId", "Data", "ProcessListeners")})");
            sb.AppendLine($"  SELECT '{Guid.NewGuid()}', {sql.Now}, {user}, {sql.Now}, {user}, {id}, {T("Id")}, {sql.Bytes(source.Get(file)!)}, 0");
            sb.AppendLine($"  FROM {T("SysCulture")} WHERE {T("Name")} = {sql.Text(culture)};");
        }
        sb.AppendLine(sql.End);
        return new Result(sb.ToString(), skipped);
    }

    private static string Cols(Func<string, string> t, params string[] names) => string.Join(", ", names.Select(t));

    private abstract class Dialect
    {
        public abstract string Name(string n);
        public abstract string Var(string n);
        public abstract string Begin(params (string Name, string? Value)[] vars);
        public abstract string End { get; }
        public abstract string Now { get; }
        public abstract string True { get; }
        public abstract string False { get; }
        public virtual string Top1Start => "";
        public virtual string Top1End => "";
        public abstract string Bytes(byte[] b);
        public abstract string Fail(string condition, string message);
        public abstract string If(string condition, string then, string @else);
        public abstract string Assign(string variable, string value);
        public abstract string SelectInto(string variable, string column, string fromWhere);
        public virtual string Text(string s) => "'" + s.Replace("'", "''") + "'";
        public string Bool(JsonNode? n) => n?.GetValue<bool>() == true ? True : False;
    }

    /// <summary>PostgreSQL: one anonymous DO block.</summary>
    private sealed class Pg : Dialect
    {
        public override string Name(string n) => "\"" + n + "\"";
        public override string Var(string n) => "v_" + n;
        public override string Begin(params (string Name, string? Value)[] vars)
        {
            var sb = new StringBuilder("DO $cst$\nDECLARE\n");
            foreach (var (n, v) in vars) sb.AppendLine($"  {Var(n)} uuid{(v == null ? "" : " := " + v)};");
            return sb.AppendLine("BEGIN").ToString();
        }
        public override string End => "END\n$cst$;";
        public override string Now => "(now() at time zone 'utc')";
        public override string True => "true";
        public override string False => "false";
        public override string Top1End => " LIMIT 1";
        public override string Bytes(byte[] b) => b.Length == 0 ? "''::bytea" : "decode('" + Convert.ToBase64String(b) + "', 'base64')";
        public override string Fail(string condition, string message) => $"  IF {condition} THEN RAISE EXCEPTION '{message.Replace("'", "''")}'; END IF;";
        public override string If(string condition, string then, string @else) => $"  IF {condition} THEN\n{then}\n  ELSE\n{@else}\n  END IF;";
        public override string Assign(string variable, string value) => $"    {variable} := {value};";
        public override string SelectInto(string variable, string column, string fromWhere) => $"  SELECT {column} INTO {variable} FROM {fromWhere};";
    }

    /// <summary>MSSql: one T-SQL batch (no GO); THROW stops it.</summary>
    private sealed class Ms : Dialect
    {
        public override string Name(string n) => "[" + n + "]";
        public override string Var(string n) => "@" + n;
        public override string Begin(params (string Name, string? Value)[] vars)
        {
            var sb = new StringBuilder("SET NOCOUNT ON;\n");
            foreach (var (n, v) in vars) sb.AppendLine($"DECLARE {Var(n)} uniqueidentifier{(v == null ? "" : " = " + v)};");
            return sb.ToString();
        }
        public override string End => "";
        public override string Now => "GETUTCDATE()";
        public override string True => "1";
        public override string False => "0";
        public override string Top1Start => "TOP 1 ";
        public override string Bytes(byte[] b) => "0x" + Convert.ToHexString(b);
        public override string Text(string s) => "N'" + s.Replace("'", "''") + "'";
        public override string Fail(string condition, string message) => $"IF {condition} THROW 50001, N'{message.Replace("'", "''")}', 1;";
        public override string If(string condition, string then, string @else) => $"IF {condition}\nBEGIN\n{then}\nEND\nELSE\nBEGIN\n{@else}\nEND;";
        public override string Assign(string variable, string value) => $"    SET {variable} = {value};";
        public override string SelectInto(string variable, string column, string fromWhere) => $"SELECT {variable} = {column} FROM {fromWhere};";
    }
}
