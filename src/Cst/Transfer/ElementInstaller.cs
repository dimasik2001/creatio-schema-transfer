using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Transfer;

/// <summary>
/// Installs SQL scripts and data bindings element by element, following what Creatio's package installer does
/// (PackageDBStorage / PackageInstallUtilities, core 10.0.0.801), without installing a package zip:
/// <list type="bullet">
/// <item>SQL: saved through SqlScriptSchemaDesignerService/SaveSchema, executed through
/// WorkspaceExplorerService/InstallSqlScripts, and only when its DBEngineType matches the target DB.</item>
/// <item>Data: each row is matched by the key columns (else Id); missing rows are inserted, existing rows get
/// the columns the install type allows (Install: IsForceUpdate columns only). Then the binding is saved through
/// SchemaDataDesignerService/SaveSchema, which takes row values from the DB by record id.</item>
/// </list>
/// </summary>
public sealed partial class ElementInstaller(CreatioClient client, int dbEngineType)
{
    public const int SqlBeforePackage = 0, SqlAfterPackage = 1, SqlAfterSchemaData = 2, SqlUninstallApp = 3;
    private const int DataInstall = 0, DataFirstInstall = 1, DataUpdate = 2, DataInstallIfNotExists = 3;

    public List<string> Warnings { get; } = [];
    /// <summary>Problems found while preparing SQL bindings that must stop the import before anything is written.</summary>
    public List<string> Conflicts { get; } = [];

    // ---------- SQL ----------

    public sealed record SqlScript(ManifestItem Item, Guid PackageUId, string Body, int InstallType, int DbEngineType, List<Guid> DependsOn, bool BackwardCompatibilityConfirmed);

    public static SqlScript ReadSqlScript(ManifestItem item, PackageContent source, Guid targetPackageUId)
    {
        var d = source.ElementDescriptor(ItemKind.SqlScript, item.Name) ?? throw new InvalidDataException($"{item.Name}: SQL descriptor missing.");
        var bodyFile = item.Files.FirstOrDefault(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
        var body = bodyFile == null ? "" : PackageContent.DecodeText(source.Get(bodyFile) ?? []);
        var deps = (d["DependsOn"] as JsonArray ?? []).Select(x => Guid.TryParse((x as JsonObject)?["UId"]?.ToString() ?? x?.ToString(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        return new SqlScript(item, targetPackageUId, body,
            d["InstallType"]?.GetValue<int>() ?? SqlAfterPackage,
            d["DBEngineType"]?.GetValue<int>() ?? 0,
            deps,
            d["BackwardCompatibilityConfirmed"]?.GetValue<bool>() ?? false);
    }

    /// <summary>Saves the script into its package; returns the previous body (null if it is new).</summary>
    public async Task<string?> SaveSqlAsync(SqlScript s)
    {
        var previous = await client.GetSqlScriptAsync(s.Item.UId);
        await client.SaveSqlScriptAsync(s.Item.UId, s.Item.Name, s.PackageUId, s.Body, s.InstallType, s.DbEngineType, s.DependsOn, s.BackwardCompatibilityConfirmed);
        return previous?["body"]?.ToString();
    }

    /// <summary>Executes the scripts of one install phase that match the target DB engine.</summary>
    public async Task ExecuteSqlAsync(IEnumerable<SqlScript> scripts, int phase)
    {
        foreach (var s in scripts.Where(s => s.InstallType == phase))
        {
            if (s.DbEngineType != dbEngineType)
            {
                Log.Info($"  {s.Item.Name}: saved, not executed (written for DB engine {s.DbEngineType}, target is {dbEngineType})");
                continue;
            }
            Log.Info($"  executing {s.Item.Name}...");
            await client.InstallSqlScriptsAsync([s.Item.UId]);
        }
    }

    // ---------- Data ----------

    private sealed record DataColumn(Guid UId, string Name, bool IsKey, bool IsForceUpdate, Guid DataTypeUId, int? DsType);

    /// <summary>Previous values of the rows a data binding changed, for restore.</summary>
    public sealed class DataBackup
    {
        public string Entity { get; set; } = "";
        public List<Guid> Inserted { get; set; } = [];
        public List<JsonObject> Updated { get; set; } = [];
        /// <summary>True when the binding was written by SQL (BindingSql), so restore can put its old content back.</summary>
        public bool BySql { get; set; }
        public Guid BindingUId { get; set; }
        public string BindingName { get; set; } = "";
        public Guid PackageUId { get; set; }
        /// <summary>Folder (relative to the work dir) with the binding's files as they were on the target before.</summary>
        public string? BindingBackupDir { get; set; }
    }

    /// <summary>
    /// Write data bindings with a temporary SQL script straight into SysPackageSchemaData (as a package install
    /// stores them) instead of the data designer. PostgreSQL and MSSql targets (BindingSql).
    /// </summary>
    public bool UseSqlBindings { get; init; }
    /// <summary>Import work dir: binding backups go to backup/data/&lt;Name&gt;/.</summary>
    public string? WorkDir { get; init; }

    public async Task<DataBackup> InstallDataAsync(ManifestItem item, PackageContent source, Guid targetPackageUId, string targetPackageName, bool bindingExistsOnTarget)
    {
        var folder = $"Data/{item.Name}/";
        var d = source.ElementDescriptor(ItemKind.Data, item.Name) ?? throw new InvalidDataException($"{item.Name}: data descriptor missing.");
        var entity = d["Schema"]?["Name"]?.ToString() ?? throw new InvalidDataException($"{item.Name}: no Schema.Name in descriptor.");
        var entityUId = Guid.Parse(d["Schema"]!["UId"]!.ToString());
        var installType = d["InstallType"]?.GetValue<int>() ?? DataInstall;
        var columns = (d["Columns"] as JsonArray ?? []).OfType<JsonObject>().Select(c =>
        {
            var typeUId = Guid.Parse(c["DataTypeValueUId"]!.ToString());
            return new DataColumn(Guid.Parse(c["ColumnUId"]!.ToString()), c["ColumnName"]!.ToString(),
                c["IsKey"]?.GetValue<bool>() ?? false, c["IsForceUpdate"]?.GetValue<bool>() ?? false, typeUId, DsType(typeUId));
        }).ToList();
        // SysSettingsValue values are written by type through the settings service; its own column checks apply there.
        foreach (var c in columns.Where(c => c.DsType == null && entity != "SysSettingsValue"))
            Warnings.Add($"{item.Name}: column {c.Name} ({c.DataTypeUId}) is not transferred (binary, image, secure or hash value).");

        var byUId = columns.ToDictionary(c => c.UId);
        var idColumn = columns.FirstOrDefault(c => c.Name == "Id");
        var keys = columns.Where(c => c.IsKey && c.DsType != null).ToList();
        if (keys.Count == 0 && idColumn != null) keys.Add(idColumn);
        if (keys.Count == 0) throw new TransferException([$"{item.Name}: neither key columns nor Id in the binding."]);

        var rows = ReadRows(source.Get(folder + "data.json"), byUId);
        var backup = new DataBackup { Entity = entity };
        var boundIds = new List<Guid>();
        var lczFiles = source.Files.Keys
            .Where(k => k.StartsWith(folder + "Localization/data.", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // Localizable columns live only in Localization/data.<culture>.json. A temporary row needs them to pass
        // required-column checks; take them from the target's primary culture, else en-US, else any culture.
        var primaryCulture = (await client.GetAppInfoAsync()).PrimaryCulture;
        var lczValues = ReadLocalizedValues(lczFiles, source, byUId, primaryCulture);

        if (installType == DataFirstInstall && bindingExistsOnTarget)
        {
            Log.Info($"  {item.Name}: install type FirstInstall and the binding already exists, rows skipped");
            return backup;
        }

        // Rows must exist in the DB for the data designer to record them in the binding. Missing rows are
        // inserted temporarily and deleted again once the binding is saved; Creatio's data installer then creates
        // them from the binding exactly as a package install does (all columns, all cultures).
        var temporary = new List<Guid>();
        if (entity == "SysSettings")
            await PrepareSysSettingsAsync(item, rows, lczValues, columns, keys, installType, boundIds, backup);
        else if (entity == "SysSettingsValue")
            await PrepareSysSettingsValuesAsync(item, rows, installType, boundIds, backup);
        else
        foreach (var row in rows)
        {
            var existing = await FindRowAsync(entity, keys, row, columns);
            if (existing == null)
            {
                var id = row.TryGetValue("Id", out var v) && v is Guid g ? g : Guid.NewGuid();
                var values = columns.Where(c => c.DsType != null && row.ContainsKey(c.Name)).ToDictionary(c => c, c => row[c.Name]);
                if (lczValues.TryGetValue(id, out var localized))
                    foreach (var (name, value) in localized)
                    {
                        var col = columns.FirstOrDefault(c => c.Name == name && c.DsType != null);
                        if (col != null && !values.ContainsKey(col)) values[col] = value;
                    }
                values[idColumn ?? new DataColumn(Guid.Empty, "Id", false, false, Guid.Empty, 0)] = id;
                await client.ExecuteDataQueryAsync("InsertQuery", ColumnValuesQuery(entity, 1, values, null));
                temporary.Add(id);
                backup.Inserted.Add(id);
                boundIds.Add(id);
                continue;
            }
                var (existingId, previous) = existing.Value;
                boundIds.Add(existingId);
                if (installType == DataInstallIfNotExists) continue;
                bool all = installType == DataUpdate || installType == DataFirstInstall;
                var toSet = columns.Where(c => c.DsType != null && c.Name != "Id" && row.ContainsKey(c.Name) && (all || c.IsForceUpdate))
                    .ToDictionary(c => c, c => row[c.Name]);
                if (row.TryGetValue("Id", out var rid) && rid is Guid rowId && rowId != existingId)
                    Warnings.Add($"{item.Name}: row matched by key has Id {existingId} on the target, archive has {rowId}; the target Id is kept.");
            if (toSet.Count == 0) continue;
            previous["Id"] = existingId.ToString();
            backup.Updated.Add(previous);
            await client.ExecuteDataQueryAsync("UpdateQuery", ColumnValuesQuery(entity, 2, toSet, existingId));
        }

        var dto = new JsonObject
        {
            ["uId"] = item.UId.ToString(),
            ["name"] = item.Name,
            ["package"] = new JsonObject { ["uId"] = targetPackageUId.ToString() },
            ["entitySchemaUId"] = entityUId.ToString(),
            ["entitySchemaName"] = entity,
            ["installType"] = installType,
            ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject
            {
                ["uId"] = c.UId.ToString(), ["name"] = c.Name, ["isKey"] = c.IsKey,
                ["isForceUpdate"] = c.IsForceUpdate, ["dataValueTypeUId"] = c.DataTypeUId.ToString()
            }).ToArray()),
            ["boundRecordIds"] = new JsonArray(boundIds.Distinct().Select(g => (JsonNode)JsonValue.Create(g.ToString())!).ToArray())
        };
        await client.SaveSchemaDataAsync(dto, item.Name);

        if (temporary.Count > 0)
        {
            var del = new JsonObject
            {
                ["RootSchemaName"] = entity,
                ["OperationType"] = 3,
                ["Filters"] = new JsonObject
                {
                    ["FilterType"] = 6, ["LogicalOperation"] = 0,
                    ["Items"] = new JsonObject
                    {
                        ["ids"] = new JsonObject
                        {
                            ["FilterType"] = 4, ["ComparisonType"] = 3,
                            ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = "Id" },
                            ["RightExpressions"] = new JsonArray(temporary.Select(t => (JsonNode)Param(0, t)).ToArray())
                        }
                    }
                }
            };
            await client.ExecuteDataQueryAsync("DeleteQuery", del);
        }

        // Translations: Creatio keeps them per binding and culture in SysPackageDataLcz.Data, byte-for-byte the
        // package file Localization/data.<culture>.json. Store them there and let Creatio's data installer
        // (InstallData: rows from SysPackageSchemaData.Data, translations from SysPackageDataLcz) do the rest,
        // as package install and pkg-to-db do.
        var cultures = lczFiles.Count > 0 ? await SaveLocalizationsAsync(item, targetPackageUId, lczFiles, source) : 0;
        await client.InstallDataAsync([item.UId]);
        Log.Info($"  {item.Name}: {backup.Inserted.Count} new, {backup.Updated.Count} updated row(s), {cultures} culture(s) of translations; installed by InstallData");
        return backup;
    }

    // ---------- System settings (DataService may not write SysSettings / SysSettingsValue) ----------

    private static readonly Guid AllEmployees = Guid.Parse("a29a3ba5-4b0d-de11-9a51-005056c00008");

    private static readonly string[] SettingProps = ["Code", "Name", "Description", "ValueTypeName", "IsPersonal", "IsCacheable", "ReferenceSchemaUId", "IsSSPAvailable"];

    /// <summary>
    /// SysSettings rows go through the system settings service (InsertSysSettingRequest keeps the archive Id,
    /// UpdateSysSettingRequest), because SysSettings is in Web.config DataServiceRestrictedModifySchemas.
    /// The rows stay: Creatio's data installer then applies translations from the binding.
    /// </summary>
    private async Task PrepareSysSettingsAsync(ManifestItem item, List<Dictionary<string, object?>> rows, Dictionary<Guid, Dictionary<string, object?>> lczValues,
        List<DataColumn> columns, List<DataColumn> keys, int installType, List<Guid> boundIds, DataBackup backup)
    {
        foreach (var row in rows)
        {
            if (!(row.TryGetValue("Id", out var v) && v is Guid id)) { Warnings.Add($"{item.Name}: a SysSettings row without Id is skipped."); continue; }
            var archive = new Dictionary<string, object?>(row);
            if (lczValues.TryGetValue(id, out var localized))
                foreach (var (k, val) in localized) archive.TryAdd(k, val);

            var current = await SelectSettingAsync(id);
            if (current == null)
            {
                await client.InsertSysSettingAsync(SettingBody(id, archive, null, _ => true));
                backup.Inserted.Add(id);
                Log.Info($"  system setting {archive.GetValueOrDefault("Code")} created");
            }
            else if (installType != DataInstallIfNotExists)
            {
                bool all = installType == DataUpdate || installType == DataFirstInstall;
                var force = columns.Where(c => all || c.IsForceUpdate).Select(c => c.Name).ToHashSet();
                backup.Updated.Add(new JsonObject { ["setting"] = SettingBody(id, archive, current, _ => false) });
                await client.UpdateSysSettingAsync(SettingBody(id, archive, current, force.Contains));
                Log.Info($"  system setting {current["Code"]} updated");
            }
            boundIds.Add(id);
        }
    }

    /// <summary>
    /// SysSettingsValue rows: only the default value (SysAdminUnit = All employees) can be written through the
    /// standard service (PostSysSettingsValues), which keeps its own row Id. Values for other users or roles
    /// are skipped with a warning. The binding then records the target's value row.
    /// </summary>
    private async Task PrepareSysSettingsValuesAsync(ManifestItem item, List<Dictionary<string, object?>> rows, int installType, List<Guid> boundIds, DataBackup backup)
    {
        foreach (var row in rows)
        {
            var settingId = row.GetValueOrDefault("SysSettings") as Guid?;
            var unit = row.GetValueOrDefault("SysAdminUnit") as Guid?;
            if (settingId == null) { Warnings.Add($"{item.Name}: a SysSettingsValue row without SysSettings is skipped."); continue; }
            if (unit != AllEmployees)
            {
                Warnings.Add($"{item.Name}: value of setting {settingId} for SysAdminUnit {unit} is not transferred (only the All employees default value can be written).");
                continue;
            }
            var setting = await SelectSettingAsync(settingId.Value)
                          ?? throw new CreatioException($"{item.Name}: system setting {settingId} does not exist on the target; transfer its SysSettings binding too.");
            var code = setting["Code"]!.ToString();
            var type = setting["ValueTypeName"]?.ToString() ?? "";
            var existingValueId = await SelectDefaultValueIdAsync(settingId.Value);
            if (existingValueId != null && installType == DataInstallIfNotExists) { boundIds.Add(existingValueId.Value); continue; }

            var (column, node) = SettingValue(type, row);
            if (column == null)
            {
                Warnings.Add($"{item.Name}: value of {code} ({type}) is not transferred (secure, binary or unknown type).");
                if (existingValueId != null) boundIds.Add(existingValueId.Value);
                continue;
            }
            if (existingValueId != null)
                backup.Updated.Add(new JsonObject { ["code"] = code, ["value"] = await SelectDefaultValueAsync(existingValueId.Value, column) });
            await client.PostSysSettingsValuesAsync(new Dictionary<string, JsonNode?> { [code] = node });
            var valueId = await SelectDefaultValueIdAsync(settingId.Value)
                          ?? throw new CreatioException($"{item.Name}: value row of {code} not found after saving it.");
            boundIds.Add(valueId);
            Log.Info($"  default value of {code} set");
            if (row.GetValueOrDefault("Id") is Guid archiveId && archiveId != valueId)
                Log.Debug($"{code}: value row Id on the target is {valueId} (archive {archiveId})");
        }
    }

    private static (string? Column, JsonNode? Value) SettingValue(string valueTypeName, Dictionary<string, object?> row)
    {
        string? col = valueTypeName switch
        {
            "Boolean" => "BooleanValue",
            "Integer" => "IntegerValue",
            "DateTime" or "Date" or "Time" => "DateTimeValue",
            "Lookup" => "GuidValue",
            "SecureText" or "Binary" or "Image" or "" => null,
            _ when valueTypeName.StartsWith("Float", StringComparison.Ordinal) || valueTypeName.StartsWith("Money", StringComparison.Ordinal) || valueTypeName.StartsWith("Decimal", StringComparison.Ordinal) => "FloatValue",
            _ => "TextValue"
        };
        if (col == null) return (null, null);
        var v = row.GetValueOrDefault(col);
        JsonNode? node = v switch
        {
            null => null,
            Guid g => JsonValue.Create(g.ToString()),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            decimal m => JsonValue.Create(m),
            DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(v.ToString())
        };
        return (col, node);
    }

    private static JsonObject SettingBody(Guid id, Dictionary<string, object?> archive, JsonObject? current, Func<string, bool> takeArchive)
    {
        var body = new JsonObject { ["id"] = id.ToString() };
        foreach (var p in SettingProps)
        {
            object? value = takeArchive(p) && archive.ContainsKey(p) ? archive[p] : current?[p]?.GetValue<object>()?.ToString();
            var key = char.ToLowerInvariant(p[0]) + p[1..];
            body[key] = p switch
            {
                "IsPersonal" or "IsCacheable" or "IsSSPAvailable" => JsonValue.Create(value is bool b ? b : bool.TryParse(value?.ToString(), out var pb) && pb),
                "ReferenceSchemaUId" => JsonValue.Create(value is Guid g ? g.ToString() : Guid.TryParse(value?.ToString(), out var pg) ? pg.ToString() : Guid.Empty.ToString()),
                _ => JsonValue.Create(value?.ToString() ?? "")
            };
        }
        return body;
    }

    private async Task<JsonObject?> SelectSettingAsync(Guid id)
    {
        var q = Select("SysSettings", ["Id", .. SettingProps]);
        q["Filters"] = new JsonObject { ["FilterType"] = 6, ["LogicalOperation"] = 0, ["Items"] = new JsonObject { ["id"] = Compare("Id", 0, id) } };
        var rows = await client.SelectAsync(q);
        return rows.FirstOrDefault();
    }

    private async Task<JsonNode?> SelectDefaultValueAsync(Guid valueId, string column)
    {
        var q = Select("SysSettingsValue", column);
        q["Filters"] = new JsonObject { ["FilterType"] = 6, ["LogicalOperation"] = 0, ["Items"] = new JsonObject { ["id"] = Compare("Id", 0, valueId) } };
        var rows = await client.SelectAsync(q);
        var v = rows.FirstOrDefault()?[column];
        return v is JsonObject lookup ? lookup["value"]?.DeepClone() : v?.DeepClone();
    }

    private async Task<Guid?> SelectDefaultValueIdAsync(Guid settingId)
    {
        var q = Select("SysSettingsValue", "Id");
        q["Filters"] = new JsonObject
        {
            ["FilterType"] = 6, ["LogicalOperation"] = 0,
            ["Items"] = new JsonObject
            {
                ["s"] = Compare("SysSettings", 0, settingId),
                ["u"] = Compare("SysAdminUnit", 0, AllEmployees)
            }
        };
        var rows = await client.SelectAsync(q);
        return rows.Count > 0 && Guid.TryParse(rows[0]["Id"]?.ToString(), out var g) ? g : null;
    }

    /// <summary>Localized values by record Id from the preferred culture (else en-US, else the first culture with values).</summary>
    private static Dictionary<Guid, Dictionary<string, object?>> ReadLocalizedValues(List<string> files, PackageContent source, Dictionary<Guid, DataColumn> byUId, string preferredCulture)
    {
        var result = new Dictionary<Guid, Dictionary<string, object?>>();
        foreach (var f in files.OrderBy(f => f.EndsWith($"data.{preferredCulture}.json", StringComparison.OrdinalIgnoreCase) ? 0 : f.EndsWith("data.en-US.json", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
        {
            foreach (var row in ReadRows(source.Get(f), byUId))
            {
                if (!(row.TryGetValue("Id", out var v) && v is Guid id)) continue;
                if (!result.TryGetValue(id, out var values)) result[id] = values = [];
                foreach (var (name, value) in row)
                    if (name != "Id" && value is not null && value.ToString() != "" && !values.ContainsKey(name))
                        values[name] = value;
            }
        }
        return result;
    }

    // ---------- Data through SQL (BindingSql) ----------

    /// <summary>Result of <see cref="PrepareBindingBySqlAsync"/>: backup for restore and whether it is written and installed.</summary>
    public sealed record SqlBinding(ManifestItem Item, PackageContent Source, string TargetPackageName, DataBackup Backup, bool Install, bool Exists);

    /// <summary>
    /// Code of every setting in the archive's SysSettings bindings, by its archive Id. The data installer remaps
    /// SysSettingsValue.SysSettings to the target setting with the same code, so cst checks values the same way.
    /// </summary>
    public static Dictionary<Guid, string> ArchiveSettingCodes(IEnumerable<(ManifestItem Item, PackageContent Source)> bindings)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var (item, source) in bindings)
        {
            var d = source.ElementDescriptor(ItemKind.Data, item.Name);
            if (d?["Schema"]?["Name"]?.ToString() != "SysSettings") continue;
            var columns = ReadColumns(d);
            foreach (var row in ReadRows(source.Get($"Data/{item.Name}/data.json"), columns.ToDictionary(c => c.UId)))
                if (row.GetValueOrDefault("Id") is Guid id && row.GetValueOrDefault("Code") is { } code)
                    result[id] = code.ToString()!;
        }
        return result;
    }

    /// <summary>
    /// SQL path, step 1 of 2: records what restore needs (previous row values, previous binding files) and stores the
    /// binding with a temporary SQL script. Step 2 is one InstallData call for all bindings (Importer), which is how
    /// the package installer works: rows of all bindings first, then a separate pass for the lookup columns of all of
    /// them (so the order between bindings does not matter), SysSettingsValue after the rest, SysSettings matched by
    /// Code and SysSettingsValue.SysSettings remapped by code, settings cache cleared at the end.
    /// </summary>
    public async Task<SqlBinding> PrepareBindingBySqlAsync(ManifestItem item, PackageContent source, Guid targetPackageUId,
        string targetPackageName, bool bindingExistsOnTarget, IReadOnlyDictionary<Guid, string> archiveSettingCodes)
    {
        var folder = $"Data/{item.Name}/";
        var d = source.ElementDescriptor(ItemKind.Data, item.Name) ?? throw new InvalidDataException($"{item.Name}: data descriptor missing.");
        var entity = d["Schema"]?["Name"]?.ToString() ?? throw new InvalidDataException($"{item.Name}: no Schema.Name in descriptor.");
        var installType = d["InstallType"]?.GetValue<int>() ?? DataInstall;
        var backup = new DataBackup
        {
            Entity = entity, BySql = true, BindingUId = item.UId, BindingName = item.Name, PackageUId = targetPackageUId
        };
        if (installType == DataFirstInstall && bindingExistsOnTarget)
        {
            Log.Info($"  {item.Name}: install type FirstInstall and the binding already exists, skipped");
            return new SqlBinding(item, source, targetPackageName, backup, false, true);
        }

        var columns = ReadColumns(d);
        var rows = ReadRows(source.Get(folder + "data.json"), columns.ToDictionary(c => c.UId));
        if (entity == "SysSettings") await BackupSettingsAsync(item, rows, installType, backup);
        else if (entity == "SysSettingsValue") await BackupSettingValuesAsync(item, rows, installType, archiveSettingCodes, backup);
        else await BackupRowsAsync(item, entity, rows, columns, installType, backup);
        return new SqlBinding(item, source, targetPackageName, backup, true, bindingExistsOnTarget);
    }

    /// <summary>What WriteBindingBySqlAsync did: the UId InstallData must use, the stored Name, and whether a new
    /// binding row was created (vs an existing one updated). Null result means the binding was skipped.</summary>
    public readonly record struct BindingWrite(Guid InstallUId, string Name, bool Created);

    /// <summary>
    /// SQL path, step 2: backs up the binding files on the target and stores the binding with a temporary script.
    /// Returns null when the binding was skipped (a name/UId conflict the user chose not to install).
    /// If the target already has a binding of this Name under another UId, the user is asked, in the console, to
    /// rename the incoming binding, adopt the target's UId (update that binding in place), skip it, or abort —
    /// instead of the SQL failing opaquely on that collision.
    /// </summary>
    public async Task<BindingWrite?> WriteBindingBySqlAsync(SqlBinding b)
    {
        var name = b.Item.Name;
        var uId = b.Item.UId;
        var created = !b.Exists; // by default: new unless the same UId is already on the target
        var content = await GetTargetPackageAsync(b.TargetPackageName);
        var targetUId = TargetBindingUId(content, name);
        if (targetUId is { } tUId && tUId != b.Item.UId)
        {
            var decision = ResolveBindingConflict(b.TargetPackageName, name, tUId, b.Item.UId);
            switch (decision.Action)
            {
                case ConflictAction.Skip:
                    Log.Info($"  {b.Item.Name}: skipped (the target keeps its own binding under UId {tUId})");
                    return null;
                case ConflictAction.Abort:
                    throw new CreatioException(
                        $"{b.Item.Name}: the target package {b.TargetPackageName} already has a data binding named "
                        + $"'{name}' under another UId ({tUId}); import aborted at the user's choice.");
                case ConflictAction.Rename:
                    name = decision.NewName!;
                    created = TargetBindingUId(content, name) != b.Item.UId; // a new name is normally a new binding
                    Log.Info($"  {b.Item.Name}: incoming binding renamed to '{name}' to avoid the conflict");
                    break;
                case ConflictAction.UseTargetUId:
                    uId = tUId; // write under the target's UId: updates that binding in place
                    created = false;
                    b.Backup.BindingUId = tUId;
                    Log.Info($"  {b.Item.Name}: using the target's UId {tUId}; its existing binding is updated in place");
                    break;
            }
        }
        if (!created) b.Backup.BindingBackupDir = await SaveBindingBackupAsync(b.TargetPackageName, name);
        // Source files are under the archive's original name; the stored Name and UId are the effective ones.
        await WriteBindingAsync(b.Source, $"Data/{b.Item.Name}/", uId, name, b.Backup.PackageUId);
        Log.Info($"  {(name == b.Item.Name ? b.Item.Name : b.Item.Name + " -> " + name)}: binding written by SQL "
                 + $"({(created ? "new" : "updated")}); {b.Backup.Inserted.Count} new, {b.Backup.Updated.Count} existing row(s)");
        return new BindingWrite(uId, name, created);
    }

    public enum ConflictAction { Rename, UseTargetUId, Skip, Abort }
    public readonly record struct ConflictDecision(ConflictAction Action, string? NewName);

    /// <summary>
    /// Asks the user, on the console, what to do about a data binding whose Name already exists on the target under
    /// another UId. With no interactive console (input redirected), keeps the safe default: abort.
    /// </summary>
    private ConflictDecision ResolveBindingConflict(string package, string name, Guid targetUId, Guid archiveUId)
    {
        if (Console.IsInputRedirected)
            return new ConflictDecision(ConflictAction.Abort, null);
        while (true)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Conflict: package {package} already has a data binding named '{name}'");
            Console.Error.WriteLine($"            on the target under UId {targetUId} (the archive's UId is {archiveUId}).");
            Console.Error.WriteLine("    [R]ename  - store the incoming binding under a new name (keeps the target's one)");
            Console.Error.WriteLine("    [U]se UId - write the incoming data under the target's UId (updates it in place)");
            Console.Error.WriteLine("    [S]kip    - leave the target's binding as is");
            Console.Error.WriteLine("    [A]bort   - stop the import");
            Console.Error.Write("  Choice [R/U/S/A]: ");
            var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
            switch (choice)
            {
                case "u" or "use" or "uid":
                    return new ConflictDecision(ConflictAction.UseTargetUId, null);
                case "s" or "skip":
                    return new ConflictDecision(ConflictAction.Skip, null);
                case "a" or "abort" or "" or null:
                    return new ConflictDecision(ConflictAction.Abort, null);
                case "r" or "rename":
                    Console.Error.Write($"  New name for the incoming binding (empty to cancel): ");
                    var newName = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(newName)) continue;
                    if (string.Equals(newName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("  That is the conflicting name. Pick a different one.");
                        continue;
                    }
                    return new ConflictDecision(ConflictAction.Rename, newName);
                default:
                    Console.Error.WriteLine("  Please answer R, U, S or A.");
                    continue;
            }
        }
    }

    private static List<DataColumn> ReadColumns(JsonObject d) =>
        (d["Columns"] as JsonArray ?? []).OfType<JsonObject>().Select(c =>
        {
            var typeUId = Guid.Parse(c["DataTypeValueUId"]!.ToString());
            return new DataColumn(Guid.Parse(c["ColumnUId"]!.ToString()), c["ColumnName"]!.ToString(),
                c["IsKey"]?.GetValue<bool>() ?? false, c["IsForceUpdate"]?.GetValue<bool>() ?? false, typeUId, DsType(typeUId));
        }).ToList();

    /// <summary>Previous values of the rows InstallData is going to change. Binary columns are not read back.</summary>
    private async Task BackupRowsAsync(ManifestItem item, string entity, List<Dictionary<string, object?>> rows, List<DataColumn> columns,
        int installType, DataBackup backup)
    {
        var idColumn = columns.FirstOrDefault(c => c.Name == "Id");
        var keys = columns.Where(c => c.IsKey && c.DsType != null).ToList();
        if (keys.Count == 0 && idColumn != null) keys.Add(idColumn);
        if (keys.Count == 0) throw new TransferException([$"{item.Name}: neither key columns nor Id in the binding."]);
        foreach (var row in rows)
        {
            var existing = await FindRowAsync(entity, keys, row, columns);
            if (existing == null)
            {
                if (row.TryGetValue("Id", out var v) && v is Guid id) backup.Inserted.Add(id);
                continue;
            }
            if (installType == DataInstallIfNotExists) continue;
            var (existingId, previous) = existing.Value;
            previous["Id"] = existingId.ToString();
            backup.Updated.Add(previous);
        }
    }

    /// <summary>SysSettings rows as the installer matches them: by Code. Previous state kept as a settings-service body.</summary>
    private async Task BackupSettingsAsync(ManifestItem item, List<Dictionary<string, object?>> rows, int installType, DataBackup backup)
    {
        foreach (var row in rows)
        {
            var code = row.GetValueOrDefault("Code")?.ToString();
            if (string.IsNullOrEmpty(code)) { Warnings.Add($"{item.Name}: a SysSettings row without Code."); continue; }
            var current = await SelectSettingByCodeAsync(code);
            if (current == null)
            {
                if (row.GetValueOrDefault("Id") is Guid id) backup.Inserted.Add(id);
                continue;
            }
            if (row.GetValueOrDefault("Id") is Guid archiveId && current["Id"]?.ToString() is { } targetId
                && !string.Equals(archiveId.ToString(), targetId, StringComparison.OrdinalIgnoreCase))
                Log.Info($"  {item.Name}: setting {code} exists on the target with Id {targetId} (archive {archiveId}); it keeps the target Id");
            if (installType == DataInstallIfNotExists) continue;
            backup.Updated.Add(new JsonObject
            {
                ["setting"] = SettingBody(Guid.Parse(current["Id"]!.ToString()), new Dictionary<string, object?>(), current, _ => false)
            });
        }
    }

    /// <summary>
    /// SysSettingsValue rows as the installer matches them: by row Id, with SysSettings remapped by code. Warns where
    /// the result would surprise: a value row for the same setting and user/role under another Id (a second row is
    /// created — SysSettingsValue has no unique index), or a user/role missing on the target.
    /// </summary>
    private async Task BackupSettingValuesAsync(ManifestItem item, List<Dictionary<string, object?>> rows, int installType,
        IReadOnlyDictionary<Guid, string> archiveSettingCodes, DataBackup backup)
    {
        foreach (var row in rows)
        {
            if (row.GetValueOrDefault("Id") is not Guid valueId) { Warnings.Add($"{item.Name}: a SysSettingsValue row without Id."); continue; }
            var archiveSetting = row.GetValueOrDefault("SysSettings") as Guid?;
            var unit = row.GetValueOrDefault("SysAdminUnit") as Guid? ?? AllEmployees;
            JsonObject? setting = null;
            string? archiveCode = null;
            if (archiveSetting is { } s)
                setting = archiveSettingCodes.TryGetValue(s, out archiveCode) ? await SelectSettingByCodeAsync(archiveCode) : await SelectSettingAsync(s);
            if (setting == null)
            {
                if (archiveCode != null)
                {
                    // The setting is new: InstallData creates it from this archive, so its values cannot clash.
                    if (unit != AllEmployees && !await ExistsAsync("SysAdminUnit", unit))
                        Warnings.Add($"{item.Name}: value of {archiveCode} for SysAdminUnit {unit}, which does not exist on the target; InstallData will fail on it.");
                    backup.Inserted.Add(valueId);
                    continue;
                }
                Warnings.Add($"{item.Name}: value {valueId}: its system setting {archiveSetting} is neither on the target nor in this archive's SysSettings bindings; InstallData will fail on it.");
                continue;
            }
            var settingCode = setting["Code"]!.ToString();
            var settingId = Guid.Parse(setting["Id"]!.ToString());
            if (unit != AllEmployees && !await ExistsAsync("SysAdminUnit", unit))
                Warnings.Add($"{item.Name}: value of {settingCode} for SysAdminUnit {unit}, which does not exist on the target; InstallData will fail on it.");

            var sameUnit = await SelectValueIdsAsync(settingId, unit);
            if (sameUnit.Contains(valueId))
            {
                if (installType == DataInstallIfNotExists) continue;
                if (unit == AllEmployees)
                {
                    var (column, _) = SettingValue(setting["ValueTypeName"]?.ToString() ?? "", row);
                    if (column != null)
                        backup.Updated.Add(new JsonObject { ["code"] = settingCode, ["value"] = await SelectDefaultValueAsync(valueId, column) });
                }
                else Warnings.Add($"{item.Name}: previous value of {settingCode} for SysAdminUnit {unit} is not backed up (restore covers the All employees value only).");
                continue;
            }
            backup.Inserted.Add(valueId);
            if (sameUnit.Count > 0)
                Conflicts.Add($"{item.Name}: {settingCode} already has a value for SysAdminUnit {unit} on the target (row {sameUnit[0]}); "
                              + $"the archive row {valueId} has another Id, so InstallData would add a second value row (a package install does the same). "
                              + "Leave the binding out or make the Ids match.");
        }
    }

    /// <summary>Binding name → SysPackageSchemaData.LastError for the bindings that have one.</summary>
    public async Task<Dictionary<string, string>> ReadBindingErrorsAsync(IEnumerable<Guid> bindingUIds)
    {
        var q = Select("SysPackageSchemaData", "Name", "LastError");
        q["Filters"] = new JsonObject
        {
            ["FilterType"] = 6, ["LogicalOperation"] = 0,
            ["Items"] = new JsonObject
            {
                ["u"] = new JsonObject
                {
                    ["FilterType"] = 4, ["ComparisonType"] = 3,
                    ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = "UId" },
                    ["RightExpressions"] = new JsonArray(bindingUIds.Select(u => (JsonNode)Param(0, u)).ToArray())
                }
            }
        };
        try
        {
            return (await client.SelectAsync(q))
                .Where(r => !string.IsNullOrWhiteSpace(r["LastError"]?.ToString()))
                .GroupBy(r => r["Name"]!.ToString())
                .ToDictionary(g => g.Key, g => g.First()["LastError"]!.ToString());
        }
        catch (CreatioException) { return []; }
    }

    private async Task<JsonObject?> SelectSettingByCodeAsync(string code)
    {
        var q = Select("SysSettings", ["Id", .. SettingProps]);
        q["Filters"] = new JsonObject { ["FilterType"] = 6, ["LogicalOperation"] = 0, ["Items"] = new JsonObject { ["c"] = Compare("Code", 1, code) } };
        return (await client.SelectAsync(q)).FirstOrDefault();
    }

    private async Task<List<Guid>> SelectValueIdsAsync(Guid settingId, Guid unit)
    {
        var q = Select("SysSettingsValue", "Id");
        q["Filters"] = new JsonObject
        {
            ["FilterType"] = 6, ["LogicalOperation"] = 0,
            ["Items"] = new JsonObject { ["s"] = Compare("SysSettings", 0, settingId), ["u"] = Compare("SysAdminUnit", 0, unit) }
        };
        return (await client.SelectAsync(q)).Select(r => Guid.Parse(r["Id"]!.ToString())).ToList();
    }

    private async Task<bool> ExistsAsync(string entity, Guid id)
    {
        var q = Select(entity, "Id");
        q["Filters"] = new JsonObject { ["FilterType"] = 6, ["LogicalOperation"] = 0, ["Items"] = new JsonObject { ["id"] = Compare("Id", 0, id) } };
        return (await client.SelectAsync(q)).Count > 0;
    }

    /// <summary>Stores the binding (rows snapshot, columns, translations) with a temporary SQL script.</summary>
    private async Task WriteBindingAsync(PackageContent source, string folder, Guid bindingUId, string bindingName, Guid packageUId)
    {
        if (!BindingSql.Supports(dbEngineType)) throw new CreatioException($"{bindingName}: SQL bindings are not implemented for DB engine {dbEngineType}.");
        await LoadCulturesAsync();
        var r = BindingSql.Build(dbEngineType, source, folder, bindingUId, bindingName, packageUId, client.Login, _cultures!.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var c in r.SkippedCultures)
            Warnings.Add($"{bindingName}: culture {c} does not exist on the target, its translations are skipped.");
        await RunTemporarySqlAsync(packageUId, r.Sql, bindingName);
    }

    /// <summary>
    /// Saves <paramref name="sql"/> as a temporary SQL script element in the package, executes it and deletes it.
    /// The SQL designer already executes a script for the current engine when saving it (validation), so errors in the
    /// script come back from the save.
    /// </summary>
    private async Task RunTemporarySqlAsync(Guid packageUId, string sql, string label)
    {
        var uId = Guid.NewGuid();
        var name = "CstTmp_" + uId.ToString("N")[..12];
        try
        {
            await client.SaveSqlScriptAsync(uId, name, packageUId, sql, SqlAfterPackage, dbEngineType, [], false);
            await client.InstallSqlScriptsAsync([uId]);
        }
        finally
        {
            try { await client.DeleteWorkspaceItemAsync(uId, CreatioClient.WorkspaceSqlScript, name); }
            catch (Exception ex) { Warnings.Add($"{label}: temporary SQL script {name} ({uId}) was not deleted: {ex.Message}"); }
        }
    }

    private readonly Dictionary<string, PackageContent> _targetPackages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The target package content (downloaded once and cached).</summary>
    private async Task<PackageContent> GetTargetPackageAsync(string packageName)
    {
        if (!_targetPackages.TryGetValue(packageName, out var content))
        {
            var zip = PackageGz.ReadZip(await client.GetZipPackagesAsync([packageName]));
            _targetPackages[packageName] = content = zip.GetValueOrDefault(packageName) ?? zip.Values.FirstOrDefault() ?? new PackageContent();
        }
        return content;
    }

    /// <summary>UId of a data binding with this Name in the target package, or null if there is none.</summary>
    private static Guid? TargetBindingUId(PackageContent content, string name)
        => content.ElementDescriptor(ItemKind.Data, name)?["UId"]?.ToString() is { } s && Guid.TryParse(s, out var g) ? g : null;

    /// <summary>Copies the binding's files from the target package (as exported now) to backup/data/&lt;Name&gt;/.</summary>
    private async Task<string?> SaveBindingBackupAsync(string packageName, string bindingName)
    {
        if (WorkDir == null) return null;
        var content = await GetTargetPackageAsync(packageName);
        var prefix = $"Data/{bindingName}/";
        var files = content.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            Warnings.Add($"{bindingName}: not found in the exported target package {packageName}; its previous content is not backed up.");
            return null;
        }
        var rel = Path.Combine("backup", "data", bindingName);
        foreach (var (path, bytes) in files)
        {
            var target = Path.Combine(WorkDir, rel, path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await Io.WriteAllBytesAsync(target, bytes);
        }
        return rel;
    }

    /// <summary>Writes the binding back from its backup folder (no InstallData: rows are restored separately).</summary>
    private async Task RestoreBindingAsync(DataBackup backup, string workDir)
    {
        var dir = Path.Combine(workDir, backup.BindingBackupDir!);
        var content = new PackageContent();
        var folder = $"Data/{backup.BindingName}/";
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            content.Set(folder + Io.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'), await Io.ReadAllBytesAsync(f));
        await WriteBindingAsync(content, folder, backup.BindingUId, backup.BindingName, backup.PackageUId);
    }

    private async Task LoadCulturesAsync() =>
        _cultures ??= (await client.SelectAsync(Select("SysCulture", "Id", "Name")))
            .ToDictionary(r => r["Name"]!.ToString(), r => Guid.Parse(r["Id"]!.ToString()), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Guid>? _cultures;

    private async Task<int> SaveLocalizationsAsync(ManifestItem item, Guid targetPackageUId, List<string> files, PackageContent source)
    {
        _cultures ??= (await client.SelectAsync(Select("SysCulture", "Id", "Name")))
            .ToDictionary(r => r["Name"]!.ToString(), r => Guid.Parse(r["Id"]!.ToString()), StringComparer.OrdinalIgnoreCase);

        var q = Select("SysPackageSchemaData", "Id");
        q["Filters"] = new JsonObject
        {
            ["FilterType"] = 6, ["LogicalOperation"] = 0,
            ["Items"] = new JsonObject
            {
                ["u"] = Compare("UId", 0, item.UId),
                ["p"] = Compare("SysPackage.UId", 0, targetPackageUId)
            }
        };
        var rows = await client.SelectAsync(q);
        if (rows.Count == 0) throw new CreatioException($"{item.Name}: binding not found in SysPackageSchemaData after saving it.");
        var bindingId = Guid.Parse(rows[0]["Id"]!.ToString());

        await client.ExecuteDataQueryAsync("DeleteQuery", new JsonObject
        {
            ["RootSchemaName"] = "SysPackageDataLcz",
            ["OperationType"] = 3,
            ["Filters"] = new JsonObject
            {
                ["FilterType"] = 6, ["LogicalOperation"] = 0,
                ["Items"] = new JsonObject { ["b"] = Compare("SysPackageSchemaData", 0, bindingId) }
            }
        });

        int count = 0;
        foreach (var file in files)
        {
            var culture = Path.GetFileNameWithoutExtension(file)["data.".Length..];
            if (!_cultures.TryGetValue(culture, out var cultureId))
            {
                Warnings.Add($"{item.Name}: culture {culture} does not exist on the target, its translations are skipped.");
                continue;
            }
            var bytes = source.Get(file) ?? [];
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes.Skip(3).ToArray();
            await client.ExecuteDataQueryAsync("InsertQuery", new JsonObject
            {
                ["RootSchemaName"] = "SysPackageDataLcz",
                ["OperationType"] = 1,
                ["ColumnValues"] = new JsonObject
                {
                    ["Items"] = new JsonObject
                    {
                        ["Id"] = Value(0, Guid.NewGuid().ToString()),
                        ["SysPackageSchemaData"] = Value(10, bindingId.ToString()),
                        ["SysCulture"] = Value(10, cultureId.ToString()),
                        // DataService blob parameter: one string per byte (BlobFormatter.ConvertQueryParameterToByteArray).
                        ["Data"] = new JsonObject
                        {
                            ["ExpressionType"] = 2,
                            ["Parameter"] = new JsonObject
                            {
                                ["DataValueType"] = 13,
                                ["ArrayValue"] = new JsonArray(bytes.Select(b => (JsonNode)JsonValue.Create(((char)b).ToString())!).ToArray())
                            }
                        }
                    }
                }
            });
            count++;
        }
        return count;
    }

    private static JsonObject Select(string root, params string[] columns)
    {
        var items = new JsonObject();
        foreach (var c in columns) items[c] = Col(c);
        return new JsonObject
        {
            ["RootSchemaName"] = root, ["OperationType"] = 0, ["AllColumns"] = false, ["RowCount"] = -1,
            ["Columns"] = new JsonObject { ["Items"] = items }
        };
    }

    private static JsonObject Compare(string path, int type, object value) => new()
    {
        ["FilterType"] = 1, ["ComparisonType"] = 3,
        ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = path },
        ["RightExpression"] = Param(type, value)
    };

    private static JsonObject Value(int type, object value) => new() { ["ExpressionType"] = 2, ["Parameter"] = ParamObject(type, value) };

    /// <summary>
    /// Puts back previous values and reports inserted rows (they are not deleted). A binding written by SQL also
    /// gets its previous content back from <paramref name="workDir"/>.
    /// </summary>
    public async Task RestoreDataAsync(DataBackup backup, string workDir)
    {
        if (backup.BySql && backup.BindingBackupDir != null)
        {
            Log.Info($"  {backup.BindingName}: restoring the previous binding content");
            await RestoreBindingAsync(backup, workDir);
        }
        if (backup.Entity == "SysSettings")
        {
            foreach (var prev in backup.Updated)
                if (prev["setting"] is JsonObject body) await client.UpdateSysSettingAsync((JsonObject)body.DeepClone());
            return;
        }
        if (backup.Entity == "SysSettingsValue")
        {
            var values = backup.Updated.Where(p => p["code"] != null)
                .ToDictionary(p => p["code"]!.ToString(), p => p["value"]?.DeepClone());
            if (values.Count > 0) await client.PostSysSettingsValuesAsync(values);
            return;
        }
        foreach (var prev in backup.Updated)
        {
            var id = Guid.Parse(prev["Id"]!.ToString());
            var q = new JsonObject
            {
                ["RootSchemaName"] = backup.Entity,
                ["OperationType"] = 2,
                ["ColumnValues"] = new JsonObject { ["Items"] = new JsonObject() },
                ["Filters"] = IdFilter(id)
            };
            var items = (JsonObject)q["ColumnValues"]!["Items"]!;
            foreach (var (name, node) in prev.Where(p => p.Key != "Id"))
                items[name] = node!.DeepClone();
            if (items.Count > 0) await client.ExecuteDataQueryAsync("UpdateQuery", q);
        }
    }

    private async Task<(Guid Id, JsonObject Previous)?> FindRowAsync(string entity, List<DataColumn> keys, Dictionary<string, object?> row, List<DataColumn> columns)
    {
        var q = new JsonObject
        {
            ["RootSchemaName"] = entity, ["OperationType"] = 0, ["AllColumns"] = false, ["RowCount"] = 2,
            ["Columns"] = new JsonObject { ["Items"] = new JsonObject() }
        };
        var colItems = (JsonObject)q["Columns"]!["Items"]!;
        colItems["Id"] = Col("Id");
        // Aliases must not equal a column name: on 8.3 an image lookup selected as "Image32.Id" under the alias
        // "Image32" fails with "An item with the same key has already been added".
        foreach (var c in columns.Where(c => c.DsType != null && c.Name != "Id"))
            colItems[ColumnAlias(c)] = Col(c.DsType is 10 or 16 ? c.Name + ".Id" : c.Name);
        var filters = new JsonObject();
        int i = 0;
        foreach (var k in keys)
        {
            row.TryGetValue(k.Name, out var value);
            filters["k" + i++] = value == null
                ? new JsonObject { ["FilterType"] = 2, ["ComparisonType"] = 1, ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = k.Name } }
                : new JsonObject
                {
                    ["FilterType"] = 1, ["ComparisonType"] = 3,
                    ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = k.DsType is 10 or 16 ? k.Name + ".Id" : k.Name },
                    ["RightExpression"] = Param(k.DsType is 10 or 16 ? 0 : k.DsType!.Value, value)
                };
        }
        q["Filters"] = new JsonObject { ["FilterType"] = 6, ["LogicalOperation"] = 0, ["Items"] = filters };
        var rows = await client.SelectAsync(q);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var previous = new JsonObject();
        foreach (var c in columns.Where(c => c.DsType != null && c.Name != "Id"))
        {
            var v = r[ColumnAlias(c)];
            JsonNode? value = v is JsonObject lookup ? lookup["value"]?.DeepClone() : v?.DeepClone();
            if (c.DsType is 0 or 10 or 16 && (value == null || value.ToString() == "" || value.ToString() == Guid.Empty.ToString())) value = null;
            previous[c.Name] = new JsonObject
            {
                ["ExpressionType"] = 2,
                ["Parameter"] = new JsonObject { ["DataValueType"] = c.DsType!.Value, ["Value"] = value }
            };
        }
        return (Guid.Parse(r["Id"]!.ToString()), previous);
    }

    private static string ColumnAlias(DataColumn c) => "cst_" + c.Name;

    private static JsonObject ColumnValuesQuery(string entity, int operation, Dictionary<DataColumn, object?> values, Guid? id)
    {
        var items = new JsonObject();
        foreach (var (c, v) in values)
            items[c.Name] = new JsonObject { ["ExpressionType"] = 2, ["Parameter"] = ParamObject(c.DsType ?? 0, v) };
        var q = new JsonObject
        {
            ["RootSchemaName"] = entity,
            ["OperationType"] = operation,
            ["ColumnValues"] = new JsonObject { ["Items"] = items }
        };
        if (id != null) q["Filters"] = IdFilter(id.Value);
        return q;
    }

    private static JsonObject IdFilter(Guid id) => new()
    {
        ["FilterType"] = 6, ["LogicalOperation"] = 0,
        ["Items"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["FilterType"] = 1, ["ComparisonType"] = 3,
                ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = "Id" },
                ["RightExpression"] = Param(0, id)
            }
        }
    };

    private static JsonObject Col(string path) => new() { ["Expression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = path } };

    private static JsonObject Param(int type, object? value) => new() { ["ExpressionType"] = 2, ["Parameter"] = ParamObject(type, value) };

    private static JsonObject ParamObject(int type, object? value) => new()
    {
        ["DataValueType"] = type,
        ["Value"] = value switch
        {
            null => null,
            Guid g => g.ToString(),
            bool b => b,
            int n => n,
            long l => l,
            decimal m => m,
            double f => f,
            // DataService parses dates with Json.DeserializeJsonDate: the value is a JSON string literal.
            DateTime dt => "\"" + dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "\"",
            _ => JsonValue.Create(value.ToString())
        }
    };

    /// <summary>Reads data.json rows into column name → typed value.</summary>
    private static List<Dictionary<string, object?>> ReadRows(byte[]? bytes, Dictionary<Guid, DataColumn> byUId)
    {
        var result = new List<Dictionary<string, object?>>();
        if (bytes == null || bytes.Length == 0) return result;
        var root = JsonNode.Parse(PackageContent.DecodeText(bytes));
        foreach (var rowNode in root?["PackageData"]?.AsArray() ?? [])
        {
            var row = new Dictionary<string, object?>();
            foreach (var cell in rowNode?["Row"]?.AsArray() ?? [])
            {
                if (!Guid.TryParse(cell?["SchemaColumnUId"]?.ToString(), out var colUId) || !byUId.TryGetValue(colUId, out var col) || col.DsType == null) continue;
                row[col.Name] = Convert(cell!["Value"], col.DsType.Value);
            }
            result.Add(row);
        }
        return result;
    }

    private static readonly Regex _msDate = new(@"^/Date\((?<ms>-?\d+)(?<off>[+-]\d{4})?\)/$");
    private static Regex MsDate() => _msDate;

    private static object? Convert(JsonNode? node, int dsType)
    {
        if (node == null) return null;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (s == "null") return null;
            switch (dsType)
            {
                case 0 or 10 or 16:
                    return Guid.TryParse(s, out var g) ? (g == Guid.Empty ? null : g) : null;
                case 7 or 8 or 9:
                    var m = MsDate().Match(s);
                    if (!m.Success) return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
                    var utc = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups["ms"].Value));
                    if (m.Groups["off"].Success)
                    {
                        var off = m.Groups["off"].Value;
                        var span = new TimeSpan(int.Parse(off.Substring(1, 2)), int.Parse(off.Substring(3, 2)), 0);
                        return (off[0] == '-' ? utc.ToOffset(-span) : utc.ToOffset(span)).DateTime;
                    }
                    return utc.UtcDateTime;
                case 4: return int.TryParse(s, out var n) ? n : null;
                case 5 or 6: return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec) ? dec : null;
                case 12: return bool.TryParse(s, out var b) ? b : null;
                default: return s;
            }
        }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (dsType == 4 && v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<decimal>(out var d)) return dsType == 4 ? (int)d : d;
        }
        return node.ToJsonString();
    }

    /// <summary>Maps a Creatio data value type UId to the DataService DataValueType; null = not transferable.</summary>
    private static int? DsType(Guid uid) => uid.ToString().ToUpperInvariant() switch
    {
        "23018567-A13C-4320-8687-FD6F9E3699BD" => 0,  // Guid
        "B295071F-7EA9-4E62-8D1A-919BF3732FF2" => 10, // Lookup
        "B039FEB0-EE7C-4884-8AA6-D6D45D84316F" => 16, // ImageLookup
        "325A73B8-0F47-44A0-8412-7606F78003AC" or "DDB3A1EE-07E8-4D62-B7A9-D0E618B00FBD" or "5CA35F10-A101-4C67-A96A-383DA6AFACFC"
            or "C0F04627-4620-4BC0-84E5-9419DC8516B1" or "8B3F29BB-EA14-4CE5-A5C5-293A929B6BA2" or "26CBA63C-DAF1-4F36-B2EA-73C0D675D90C"
            or "26CBA64C-DAF1-4F36-B2EA-73C0D695D90C" or "66CBA64C-DAF1-4F36-B8EA-73C0D695D90C" or "79BCCFFA-8C8B-4863-B376-A69D2244182B"
            or "DAFB71F9-EE9F-4E0B-A4D7-37AA15987155" or "0EAAA70F-2A5A-444E-BDF1-98B37895C820" => 1, // text kinds, color
        "6B6B74E2-820D-490E-A017-2B73D4CCF2B0" => 4, // Integer
        "57EE4C31-5EC4-45FA-B95D-3A2868AA89A8" or "60532CFD-8645-4A54-BE81-BEF507AA2339" or "07BA84CE-0BF7-44B4-9F2C-7B15032EB98C"
            or "5CC8060D-6D10-4773-89FC-8C12D6F659A6" or "3F62414E-6C25-4182-BCEF-A73C9E396F31" or "FF22E049-4D16-46EE-A529-92D8808932DC"
            or "A4AAF398-3531-4A0D-9D75-A587F5B5B59E" => 5, // Float*
        "969093E2-2B4E-463B-883A-3D3B8C61F0CD" or "6CC9CBE3-A486-4ED3-BEA6-5C96F36D77FF" or "8842ED44-069A-4FAE-B1A2-EE17384689EB"
            or "85FF33CE-D57D-4197-9E9B-C2DCB1CFC35E" => 6, // Money*
        "D21E9EF4-C064-4012-B286-FA1A8171DA44" => 7, // DateTime
        "603D4960-A1A2-45E9-B232-206A54421B01" => 8, // Date
        "04CC757B-8F06-482C-8A1A-0C0E171D2410" => 9, // Time
        "90B65BF8-0FFC-4141-8779-2420877AF907" => 12, // Boolean
        _ => null // binary, image, secure text, hash text, localizable/meta types
    };
}
