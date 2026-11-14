using System.Text;
using System.Text.Json.Nodes;
using Cst.Packaging;

namespace Cst.Transfer;

/// <summary>
/// Builds an SQL script that stores a data binding exactly as a package install does: the binding row in
/// SysPackageSchemaData (Data = data.json bytes, FilterData = filter.json bytes), one SysPackageSchemaDataColumn per
/// descriptor column and one SysPackageDataLcz per culture file. Creatio's InstallData then creates the rows from it,
/// binary columns included. An existing binding (same UId in the package) keeps its row Id and is updated in place.
/// Only PostgreSQL for now.
/// </summary>
public static class BindingSql
{
    public sealed record Result(string Sql, List<string> SkippedCultures);

    /// <param name="folder">"Data/&lt;Name&gt;/" inside <paramref name="source"/>.</param>
    /// <param name="cultures">Culture names that exist on the target.</param>
    public static Result PostgreSql(PackageContent source, string folder, Guid bindingUId, string bindingName,
        Guid packageUId, string login, IReadOnlySet<string> cultures)
    {
        var descriptor = source.ReadJson(folder + "descriptor.json")?["Descriptor"] as JsonObject
                         ?? throw new InvalidDataException($"{bindingName}: descriptor.json missing.");
        var entityUId = Guid.Parse(descriptor["Schema"]!["UId"]!.ToString());
        var entityName = descriptor["Schema"]!["Name"]!.ToString();
        var installType = descriptor["InstallType"]?.GetValue<int>() ?? 0;
        var data = source.Get(folder + "data.json") ?? throw new InvalidDataException($"{bindingName}: data.json missing.");
        var filter = source.Get(folder + "filter.json") ?? [];

        var sb = new StringBuilder();
        sb.AppendLine($"-- cst: data binding {bindingName} ({bindingUId}) on {entityName}, stored as a package install does.");
        sb.AppendLine("-- Temporary element: cst runs it once and deletes it.");
        sb.AppendLine("DO $cst$");
        sb.AppendLine("DECLARE");
        sb.AppendLine($"  v_user uuid := (SELECT \"ContactId\" FROM \"SysAdminUnit\" WHERE \"Name\" = {Text(login)} LIMIT 1);");
        sb.AppendLine($"  v_pkg uuid := (SELECT \"Id\" FROM \"SysPackage\" WHERE \"UId\" = '{packageUId}');");
        sb.AppendLine($"  v_schema uuid := (SELECT \"Id\" FROM \"SysSchema\" WHERE \"UId\" = '{entityUId}' AND \"ExtendParent\" = false ORDER BY \"CreatedOn\" LIMIT 1);");
        sb.AppendLine("  v_id uuid;");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"  IF v_pkg IS NULL THEN RAISE EXCEPTION 'cst: package {packageUId} not found'; END IF;");
        sb.AppendLine($"  IF v_schema IS NULL THEN RAISE EXCEPTION 'cst: entity schema {Esc(entityName)} ({entityUId}) not found'; END IF;");
        sb.AppendLine($"  SELECT \"Id\" INTO v_id FROM \"SysPackageSchemaData\" WHERE \"UId\" = '{bindingUId}' AND \"SysPackageId\" = v_pkg;");
        sb.AppendLine($"  IF v_id IS NULL AND EXISTS (SELECT 1 FROM \"SysPackageSchemaData\" WHERE \"SysPackageId\" = v_pkg AND \"Name\" = {Text(bindingName)}) THEN");
        sb.AppendLine($"    RAISE EXCEPTION 'cst: the package already has a data binding named {Esc(bindingName)} with another UId';");
        sb.AppendLine("  END IF;");
        sb.AppendLine("  IF v_id IS NULL THEN");
        sb.AppendLine($"    v_id := '{Guid.NewGuid()}';");
        sb.AppendLine("    INSERT INTO \"SysPackageSchemaData\" (\"Id\", \"CreatedOn\", \"CreatedById\", \"ModifiedOn\", \"ModifiedById\", \"UId\", \"Name\",");
        sb.AppendLine("      \"SysPackageId\", \"IsLocked\", \"IsChanged\", \"SysSchemaId\", \"FilterData\", \"Data\", \"InstallType\", \"NeedInstall\", \"ProcessListeners\")");
        sb.AppendLine($"    VALUES (v_id, {Now}, v_user, {Now}, v_user, '{bindingUId}', {Text(bindingName)}, v_pkg, true, false, v_schema,");
        sb.AppendLine($"      {Bytes(filter)}, {Bytes(data)}, {installType}, true, 0);");
        sb.AppendLine("  ELSE");
        sb.AppendLine($"    UPDATE \"SysPackageSchemaData\" SET \"Name\" = {Text(bindingName)}, \"SysSchemaId\" = v_schema, \"FilterData\" = {Bytes(filter)},");
        sb.AppendLine($"      \"Data\" = {Bytes(data)}, \"InstallType\" = {installType}, \"NeedInstall\" = true, \"ModifiedOn\" = {Now}, \"ModifiedById\" = v_user");
        sb.AppendLine("    WHERE \"Id\" = v_id;");
        sb.AppendLine("  END IF;");
        sb.AppendLine("  DELETE FROM \"SysPackageSchemaDataColumn\" WHERE \"SysPackageSchemaDataId\" = v_id;");
        foreach (var c in (descriptor["Columns"] as JsonArray ?? []).OfType<JsonObject>())
        {
            sb.AppendLine("  INSERT INTO \"SysPackageSchemaDataColumn\" (\"Id\", \"CreatedOn\", \"CreatedById\", \"ModifiedOn\", \"ModifiedById\", \"SysPackageSchemaDataId\",");
            sb.AppendLine("    \"PackageSchemaColumnUId\", \"IsForceUpdate\", \"IsKey\", \"ColumnName\", \"DataValueTypeUId\", \"ProcessListeners\")");
            sb.AppendLine($"  VALUES ('{Guid.NewGuid()}', {Now}, v_user, {Now}, v_user, v_id, '{Guid.Parse(c["ColumnUId"]!.ToString())}',");
            sb.AppendLine($"    {Bool(c["IsForceUpdate"])}, {Bool(c["IsKey"])}, {Text(c["ColumnName"]!.ToString())}, '{Guid.Parse(c["DataTypeValueUId"]!.ToString())}', 0);");
        }
        sb.AppendLine("  DELETE FROM \"SysPackageDataLcz\" WHERE \"SysPackageSchemaDataId\" = v_id;");
        var skipped = new List<string>();
        var lczPrefix = folder + "Localization/data.";
        foreach (var file in source.Files.Keys.Where(k => k.StartsWith(lczPrefix, StringComparison.OrdinalIgnoreCase)
                                                          && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var culture = file[lczPrefix.Length..^".json".Length];
            if (!cultures.Contains(culture)) { skipped.Add(culture); continue; }
            sb.AppendLine("  INSERT INTO \"SysPackageDataLcz\" (\"Id\", \"CreatedOn\", \"CreatedById\", \"ModifiedOn\", \"ModifiedById\", \"SysPackageSchemaDataId\",");
            sb.AppendLine("    \"SysCultureId\", \"Data\", \"ProcessListeners\")");
            sb.AppendLine($"  SELECT '{Guid.NewGuid()}', {Now}, v_user, {Now}, v_user, v_id, \"Id\", {Bytes(source.Get(file)!)}, 0");
            sb.AppendLine($"  FROM \"SysCulture\" WHERE \"Name\" = {Text(culture)};");
        }
        sb.AppendLine("END");
        sb.AppendLine("$cst$;");
        return new Result(sb.ToString(), skipped);
    }

    private const string Now = "(now() at time zone 'utc')";
    private static string Esc(string s) => s.Replace("'", "''");
    private static string Text(string s) => "'" + Esc(s) + "'";
    private static string Bool(JsonNode? n) => n?.GetValue<bool>() == true ? "true" : "false";
    private static string Bytes(byte[] b) => b.Length == 0 ? "''::bytea" : "decode('" + Convert.ToBase64String(b) + "', 'base64')";
}
