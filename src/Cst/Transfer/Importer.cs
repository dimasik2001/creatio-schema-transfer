using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Transfer;

public sealed class ImportOptions
{
    public bool DryRun { get; init; }
    public bool NoCompile { get; init; }
    public bool FullRebuild { get; init; }
    /// <summary>Use the data designer for bindings even where the SQL path is available (PostgreSQL).</summary>
    public bool DataDesigner { get; init; }
    /// <summary>Folder for backups and restore.json.</summary>
    public required string WorkDir { get; init; }
    /// <summary>
    /// Decides what to do when a data binding's Name already exists on the target under another UId.
    /// Null: ask on the console (abort when the input is redirected).
    /// </summary>
    public Func<ElementInstaller.BindingConflict, ElementInstaller.ConflictDecision>? ResolveBindingConflict { get; init; }
}

/// <summary>Stage names reported through <see cref="Log.Stage"/> during an import, in order.</summary>
public static class ImportStages
{
    public const string Check = "Check", Packages = "Whole packages", SqlBefore = "SQL BeforePackage", Schemas = "Schemas", DbStructure = "DB structure",
        Compile = "Compile", SqlAfter = "SQL AfterPackage", Data = "Data bindings", SqlAfterData = "SQL AfterSchemaData", Files = "Files";
}

public sealed class ImportResult
{
    public List<string> Installed { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> MissingDependencies { get; } = [];
    public List<string> CompileErrors { get; } = [];
    public string? MissingReportPath { get; set; }
    public string WorkDir { get; set; } = "";
    public Dictionary<string, string> Placement { get; } = [];
    /// <summary>True when compilation failed and the steps after it (AfterPackage SQL, data) were not run.</summary>
    public bool StoppedAfterCompile { get; set; }
}

/// <summary>
/// Installs a transfer archive element by element, the way Creatio's own designers save them, without a package
/// zip (the zip installer deletes elements missing from the zip and skips locally modified ones).
/// Order, as in Creatio's package installer: SQL BeforePackage → schemas → DB structure → (compile) →
/// SQL AfterPackage → data bindings → SQL AfterSchemaData.
/// Target packages must already exist; cst never creates packages or changes their dependencies.
/// </summary>
public sealed class Importer(CreatioClient client)
{
    private sealed class TargetPackage
    {
        public required string Name { get; init; }
        public required PackageInfo Existing { get; init; }
        public Guid UId => Existing.UId;
        public List<ManifestItem> Items { get; } = [];
        /// <summary>Names of packages (on target) this package must depend on.</summary>
        public HashSet<string> Requires { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ImportResult> ImportAsync(string archivePath, ImportOptions options)
    {
        Log.Stage(ImportStages.Check);
        var (manifest, sources) = TransferArchive.Read(archivePath);
        var result = new ImportResult { WorkDir = options.WorkDir };
        // From the environment's settings (DbEngine / --db-engine); checked before anything else.
        var dbEngine = client.DbEngineType;
        Log.Info($"Archive: {manifest.Items.Count} element(s), {manifest.Files.Count} file(s) from {manifest.SourceUrl}, created {manifest.CreatedUtc:u}; target DB engine: {DbEngineName(dbEngine)}");
        // Files need the target's Creatio root folder; checked before anything else, like DbEngine.
        var fileRoot = manifest.Files.Count == 0 ? null : client.RootPath ?? throw new ArgumentException(
            $"The archive carries {manifest.Files.Count} file(s), but the target environment has no Creatio root folder: set \"Path\" for it in appsettings.json (like clio's EnvironmentPath) or pass --root.");

        var catalog = new Catalog(client);
        var packages = await catalog.LoadPackagesAsync();
        var pkgById = packages.ToDictionary(p => p.Id);
        var pkgByName = packages.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pkgDeps = await catalog.LoadPackageDependenciesAsync();

        var lookup = manifest.Items.Select(i => i.UId).Concat(manifest.External.Select(e => e.UId));
        var onTarget = (await catalog.FindElementsAsync(lookup)).GroupBy(e => e.UId).ToDictionary(g => g.Key, g => g.ToList());

        var errors = new List<string>();
        var missing = result.MissingDependencies;

        // 0. Whole packages (zip installer): their dependencies must be on the target or in the archive, and no
        //    element of an existing target package may be modified locally (the installer would skip it). Decided:
        //    stop before installing anything instead of a partial install.
        var whole = manifest.WholePackages;
        var wholeNames = whole.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in whole)
            foreach (var dep in w.DependsOn.Where(d => !pkgByName.ContainsKey(d.Name) && !wholeNames.Contains(d.Name)))
                missing.Add($"Package **{w.Name}** depends on package **{dep.Name}**, which does not exist on the target. Install it first or add it to the packages: section.");
        var wholeExisting = whole.Where(w => pkgByName.ContainsKey(w.Name)).Select(w => pkgByName[w.Name]).ToList();
        if (wholeExisting.Count > 0)
        {
            string Kind(string table) => table switch { "SysPackageSqlScript" => "sql", "SysPackageSchemaData" => "data", _ => "schema" };
            foreach (var g in (await catalog.FindLocallyModifiedAsync(wholeExisting.Select(p => p.Id))).GroupBy(m => m.Package))
                errors.Add($"Package {g.Key} (packages: section) has {g.Count()} locally modified element(s) on the target, which Creatio's zip installer "
                    + $"would not overwrite: {string.Join(", ", g.OrderBy(m => m.Name).Select(m => $"{m.Name} [{Kind(m.Table)}]"))}. Nothing was installed. "
                    + "Transfer such a package element by element (a 'package X:' section) instead.");
        }

        // Elements of a whole package that live in ANOTHER package on the target (e.g. transferred there element by
        // element earlier): the zip installer would move them or fail. Stop and list them.
        foreach (var w in whole.Where(w => w.ElementUIds.Count > 0))
        {
            var elsewhere = (await catalog.FindElementsAsync(w.ElementUIds))
                .Where(e => !e.PackageName.Equals(w.Name, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.PackageName).ToList();
            foreach (var g in elsewhere)
                errors.Add($"Package {w.Name} (packages: section): {g.Count()} of its element(s) already exist on the target in package {g.Key} "
                    + $"(same UId): {string.Join(", ", g.Select(e => e.Name).Distinct().OrderBy(n => n))}. Installing the package zip would move them "
                    + "out of that package or fail. Nothing was installed.");
        }

        // 1. External dependencies must already exist on the target (or come with a whole package).
        foreach (var ext in manifest.External.Where(e => !wholeNames.Contains(e.SourcePackage)))
            if (!onTarget.ContainsKey(ext.UId))
                missing.Add($"{ext.Kind} **{ext.Name}** ({ext.UId}, package {ext.SourcePackage} on source) — required by {string.Join(", ", ext.RequiredBy)}");

        // 2. Where each item goes: where it already lives on the target, otherwise the mapped package.
        var targets = new Dictionary<string, TargetPackage>(StringComparer.OrdinalIgnoreCase);
        var absentPackages = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var itemTarget = new Dictionary<Guid, TargetPackage>();
        foreach (var item in manifest.Items)
        {
            var existingEl = onTarget.GetValueOrDefault(item.UId)?.FirstOrDefault(e => e.Kind == item.Kind);
            string targetName = item.TargetPackage;
            if (existingEl != null)
            {
                var pkg = pkgById[existingEl.PackageId];
                if (!pkg.IsEditable)
                {
                    errors.Add($"{item.Name} already exists on the target in package {pkg.Name}, which cannot be changed (installed from repository).");
                    continue;
                }
                if (!pkg.Name.Equals(item.TargetPackage, StringComparison.OrdinalIgnoreCase))
                    result.Notes.Add($"{item.Name} already exists in {pkg.Name} on the target; it is updated there instead of {item.TargetPackage}.");
                targetName = pkg.Name;
            }
            if (!targets.TryGetValue(targetName, out var target))
            {
                if (!pkgByName.TryGetValue(targetName, out var p) && whole.FirstOrDefault(w => w.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) is { } wp)
                    p = new PackageInfo(Guid.Empty, wp.UId, wp.Name, wp.Version ?? "", wp.Maintainer ?? "", 0); // created by the zip install first
                if (p == null)
                {
                    if (!absentPackages.TryGetValue(targetName, out var list)) absentPackages[targetName] = list = [];
                    list.Add(item.Name);
                    continue;
                }
                targets[targetName] = target = new TargetPackage { Name = p.Name, Existing = p };
            }
            if (!target.Existing.IsEditable)
            {
                errors.Add($"Target package {target.Name} cannot be changed (installed from repository).");
                continue;
            }
            target.Items.Add(item);
            itemTarget[item.UId] = target;
            result.Placement[item.Kind == ItemKind.Schema ? item.Name : $"{item.Name} [{item.Kind}]"] = target.Name;
        }
        if (errors.Count > 0) throw new TransferException(errors);
        foreach (var (name, items) in absentPackages)
            missing.Add($"Package **{name}** does not exist on the target — needed for {string.Join(", ", items)}. Install it first.");

        // 3. Package dependencies each target package needs; they must already be in its hierarchy.
        foreach (var t in targets.Values)
            foreach (var item in t.Items)
            {
                foreach (var dep in item.DependsOn)
                    if (itemTarget.TryGetValue(dep, out var depPkg) && depPkg != t) t.Requires.Add(depPkg.Name);
                foreach (var ext in item.External)
                {
                    if (!onTarget.TryGetValue(ext, out var els)) continue;
                    var el = els.OrderBy(e => e.ExtendParent).First();
                    var pkgName = pkgById[el.PackageId].Name;
                    if (!pkgName.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) t.Requires.Add(pkgName);
                }
            }
        HashSet<string> Closure(string name, HashSet<string>? seen = null)
        {
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!seen.Add(name)) return seen;
            if (pkgByName.TryGetValue(name, out var p) && pkgDeps.TryGetValue(p.Id, out var deps))
                foreach (var d in deps) if (pkgById.TryGetValue(d, out var dp)) Closure(dp.Name, seen);
            return seen;
        }
        foreach (var t in targets.Values)
        {
            var closure = Closure(t.Name);
            foreach (var r in t.Requires.Where(r => !closure.Contains(r)))
                missing.Add($"Package **{t.Name}** must depend on **{r}** (its elements reference {r}). Add the dependency on the target.");
        }

        if (missing.Count > 0)
        {
            result.MissingReportPath = WriteMissingReport(archivePath, missing);
            return result;
        }

        // Files: compare with the target folder and make sure nothing to replace is locked, before any change.
        var filePlan = fileRoot == null ? [] : FileTransfer.Plan(fileRoot, manifest.Files);
        if (filePlan.Any(p => p.State != "identical" && FileTransfer.NeedsRestart(p.File.Path)))
            result.Notes.Add("Files under a bin folder or *.config are picked up only after the Creatio application is restarted (IIS app pool recycle / clio restart-web-app). A later compilation rewrites Terrasoft.Configuration\\bin.");

        // Bindings: SQL path (exact copy of the source binding, binary columns included) on PostgreSQL and MSSql.
        var installer = new ElementInstaller(client, dbEngine)
        {
            UseSqlBindings = BindingSql.Supports(dbEngine) && !options.DataDesigner, WorkDir = options.WorkDir,
            ResolveConflict = options.ResolveBindingConflict
        };
        var ordered = manifest.Items.Where(i => itemTarget.ContainsKey(i.UId)).OrderBy(i => i.Order).ThenBy(i => i.Name).ToList();
        var sqlScripts = ordered.Where(i => i.Kind == ItemKind.SqlScript)
            .Select(i => ElementInstaller.ReadSqlScript(i, sources[i.SourcePackage], itemTarget[i.UId].UId)).ToList();
        var schemas = ordered.Where(i => i.Kind == ItemKind.Schema).ToList();
        var data = ordered.Where(i => i.Kind == ItemKind.Data).ToList();
        foreach (var s in sqlScripts.Where(s => s.InstallType == ElementInstaller.SqlUninstallApp))
            result.Notes.Add($"{s.Item.Name} is an UninstallApp script: saved, never executed by cst.");

        if (options.DryRun)
        {
            Log.Info($"Dry run (target DB engine {DbEngineName(dbEngine)}). Planned steps:");
            foreach (var w in whole)
            {
                var state = pkgByName.TryGetValue(w.Name, out var ex) ? $"replaces the target package (version {ex.Version} → {w.Version})" : "new package";
                Log.Info($"  whole package (zip installer): {w.Name} — {state}, {w.Schemas.Count} schema(s)");
            }
            foreach (var (phase, label) in new[] { (ElementInstaller.SqlBeforePackage, "SQL BeforePackage") })
                foreach (var s in sqlScripts.Where(s => s.InstallType == phase)) Log.Info($"  {label}: {s.Item.Name}");
            foreach (var s in schemas) Log.Info($"  schema step {s.Order + 1}: {s.Name} ({ShortManager(s.Manager)}) → {itemTarget[s.UId].Name}");
            foreach (var s in sqlScripts.Where(s => s.InstallType == ElementInstaller.SqlAfterPackage)) Log.Info($"  SQL AfterPackage: {s.Item.Name}");
            foreach (var d in data) Log.Info($"  data: {d.Name} → {itemTarget[d.UId].Name}");
            foreach (var s in sqlScripts.Where(s => s.InstallType == ElementInstaller.SqlAfterSchemaData)) Log.Info($"  SQL AfterSchemaData: {s.Item.Name}");
            if (filePlan.Count > 0) Log.Info($"  files into {fileRoot} (written last):");
            foreach (var line in FileTransfer.Describe(filePlan)) Log.Info(line);
            return result;
        }

        Directory.CreateDirectory(options.WorkDir);
        var backupDir = Path.Combine(options.WorkDir, "backup");
        Directory.CreateDirectory(backupDir);
        var restore = new JsonObject
        {
            ["targetUrl"] = client.Url, ["createdUtc"] = DateTime.UtcNow,
            ["schemaBackups"] = new JsonArray(), ["createdSchemas"] = new JsonArray(),
            ["sqlBackups"] = new JsonArray(), ["dataBackups"] = new JsonArray(), ["fileBackups"] = new JsonArray(),
            ["wholeBackups"] = new JsonArray()
        };
        var restorePath = Path.Combine(options.WorkDir, "restore.json");
        async Task SaveRestore() => await Io.WriteAllTextAsync(restorePath, restore.ToJsonString(Manifest.Json));

        try
        {
            // Whole packages first (single elements may reference them), as Creatio installs a package zip.
            if (whole.Count > 0)
            {
                Log.Stage(ImportStages.Packages);
                foreach (var w in whole)
                {
                    var entry = new JsonObject { ["name"] = w.Name, ["existed"] = pkgByName.ContainsKey(w.Name) };
                    if (pkgByName.ContainsKey(w.Name))
                    {
                        Log.Info($"[{w.Name}] backing up the target package...");
                        var file = Path.Combine("backup", "whole", w.Name + ".zip");
                        Directory.CreateDirectory(Path.Combine(options.WorkDir, "backup", "whole"));
                        File.WriteAllBytes(Path.Combine(options.WorkDir, file), await client.GetZipPackagesAsync([w.Name]));
                        entry["file"] = file;
                    }
                    ((JsonArray)restore["wholeBackups"]!).Add(entry);
                }
                await SaveRestore();
                var zipName = $"cst_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                Log.Info($"Installing {whole.Count} whole package(s) with Creatio's zip installer: {string.Join(", ", whole.Select(w => w.Name))}...");
                await client.UploadPackageAsync(zipName, TransferArchive.ReadBytes(archivePath, Manifest.WholePackagesEntry));
                try { await client.InstallPackageAsync(zipName); }
                catch (CreatioException ex)
                {
                    throw new CreatioException($"zip installer: {ex.Message} (log: {Path.Combine(options.WorkDir, "whole-packages-install.log")})");
                }
                finally
                {
                    try { await Io.WriteAllTextAsync(Path.Combine(options.WorkDir, "whole-packages-install.log"), await client.GetInstallLogAsync()); }
                    catch (Exception ex) when (ex is CreatioException or IOException or HttpRequestException) { Log.Debug($"install log not saved: {ex.Message}"); }
                }
                result.Installed.AddRange(whole.Select(w => w.Name + " (whole)"));
            }

            // SQL scripts are executed from the stored element, so they are saved first. The SQL designer validates
            // a script for the current DB engine against the DB when saving it (seen on 8.3/PostgreSQL), so
            // AfterPackage/AfterSchemaData scripts for this engine are saved only once the tables exist.
            async Task SaveScripts(IEnumerable<ElementInstaller.SqlScript> scripts)
            {
                foreach (var s in scripts)
                {
                    var previous = await installer.SaveSqlAsync(s);
                    ((JsonArray)restore["sqlBackups"]!).Add(new JsonObject
                    {
                        ["uId"] = s.Item.UId.ToString(), ["name"] = s.Item.Name, ["packageUId"] = s.PackageUId.ToString(),
                        ["installType"] = s.InstallType, ["dbEngineType"] = s.DbEngineType,
                        ["previousBody"] = previous, ["isNew"] = previous == null
                    });
                    await SaveRestore();
                }
            }
            bool SaveLate(ElementInstaller.SqlScript s) => s.DbEngineType == dbEngine && s.InstallType != ElementInstaller.SqlBeforePackage;
            if (sqlScripts.Count > 0)
            {
                Log.Stage(ImportStages.SqlBefore);
                var early = sqlScripts.Where(s => !SaveLate(s)).ToList();
                Log.Info($"Saving {early.Count} SQL script(s) (BeforePackage and other DB engines)...");
                await SaveScripts(early);
                Log.Info("SQL BeforePackage:");
                await installer.ExecuteSqlAsync(sqlScripts, ElementInstaller.SqlBeforePackage);
            }

            // Schemas through the Configuration import, strictly in dependency order across all packages
            // (a user task before the processes that call it, a parent before its children).
            var entities = new List<Guid>();
            if (schemas.Count > 0) Log.Stage(ImportStages.Schemas);
            foreach (var t in targets.Values)
                await BackupSchemasAsync(t, schemas.Where(s => itemTarget[s.UId] == t).ToList(), onTarget, backupDir, restore, options);
            await SaveRestore();
            // Import validates lookup columns: their reference schema must exist. Entities in one dependency cycle
            // (A → B → A) that are new on the target therefore go in two passes: first a stub whose lookups to the
            // not-yet-created members point to the entity itself, then the full schema.
            var mdByItem = new Dictionary<Guid, string>();
            foreach (var item in schemas)
            {
                if (item.SchemaExport == null) throw new TransferException([$"{item.Name}: the archive has no Configuration export; export it again with this cst version."]);
                mdByItem[item.UId] = TransferArchive.ReadText(archivePath, item.SchemaExport) ?? throw new InvalidDataException($"Archive is missing {item.SchemaExport}.");
            }
            bool IsNew(ManifestItem i) => onTarget.GetValueOrDefault(i.UId)?.Any(e => e.Kind == ItemKind.Schema) != true;
            foreach (var group in schemas.GroupBy(s => s.Order))
            {
                // Pending = members of this group that do not exist on the target yet; any member referencing one
                // of them (new itself or not, e.g. left half-saved by an earlier failed run) needs a stub first.
                var groupEntities = group.Where(i => i.Manager == "EntitySchemaManager").ToList();
                var pending = groupEntities.Where(IsNew).Select(i => i.UId).ToHashSet();
                var done = new HashSet<Guid>();
                if (groupEntities.Count > 1 && pending.Count > 0)
                {
                    // Pass 1: make every member exist (stub where a lookup points to a pending member).
                    foreach (var item in groupEntities)
                    {
                        var stub = StubLookups(mdByItem[item.UId], item.UId, pending);
                        if (stub == null && !pending.Contains(item.UId)) continue;
                        Log.Info($"[{itemTarget[item.UId].Name}] importing {item.Name}{(stub != null ? " stub" : "")} (cycle, step {item.Order + 1})...");
                        await client.ImportSchemaAsync(itemTarget[item.UId].UId, item.Name, stub ?? mdByItem[item.UId]);
                        if (stub == null) { done.Add(item.UId); entities.Add(item.UId); }
                    }
                }
                // Pass 2 (or the only pass): full schemas.
                foreach (var item in group.Where(i => !done.Contains(i.UId)))
                {
                    var t = itemTarget[item.UId];
                    Log.Info($"[{t.Name}] importing {item.Name} ({ShortManager(item.Manager)}, step {item.Order + 1})...");
                    await client.ImportSchemaAsync(t.UId, item.Name, mdByItem[item.UId]);
                    if (item.Manager == "EntitySchemaManager") entities.Add(item.UId);
                    if (item.Manager == "SourceCodeSchemaManager"
                        && await client.EnsureSourceCodeBodyAsync(item.UId, item.Name, mdByItem[item.UId]))
                        Log.Info($"  {item.Name}: import kept the old code; saved through the source code designer");
                }
            }
            if (entities.Count > 0)
            {
                Log.Stage(ImportStages.DbStructure);
                Log.Info($"Updating DB structure of {entities.Count} entity schema(s)...");
                var msg = await client.UpdateDbStructureAsync(entities);
                if (msg != null) result.Notes.Add($"UpdateDBStructure: {msg}");
            }
            foreach (var t in targets.Values) result.Installed.Add(t.Name);

            if (!options.NoCompile && (schemas.Count > 0 || whole.Count > 0))
            {
                Log.Stage(ImportStages.Compile);
                Log.Info(options.FullRebuild ? "Compiling configuration (full rebuild)..." : "Compiling configuration...");
                // Only errors in files of the imported schemas stop the import; a target may already have
                // compile errors of its own (seen on the 8.3 test stand), which are reported as warnings.
                var buildErrors = await client.BuildAsync(options.FullRebuild);
                var names = schemas.Select(s => s.Name).Concat(whole.SelectMany(w => w.Schemas)).ToList();
                bool Ours(string e) => names.Any(n => e.StartsWith(n + ".", StringComparison.OrdinalIgnoreCase)
                                                     || e.Contains("\\" + n + ".", StringComparison.OrdinalIgnoreCase)
                                                     || e.Contains("/" + n + ".", StringComparison.OrdinalIgnoreCase));
                result.CompileErrors.AddRange(buildErrors.Where(Ours));
                var foreign = buildErrors.Where(e => !Ours(e)).ToList();
                if (foreign.Count > 0)
                    result.Warnings.Add($"The target has {foreign.Count} compile error(s) in schemas not touched by this import (first: {foreign[0]}). They are not caused by cst.");
                if (result.CompileErrors.Count > 0 && (data.Count > 0 || sqlScripts.Any(s => s.InstallType != ElementInstaller.SqlBeforePackage)))
                {
                    result.StoppedAfterCompile = true;
                    return result;
                }
            }

            if (sqlScripts.Count > 0)
            {
                Log.Stage(ImportStages.SqlAfter);
                var late = sqlScripts.Where(SaveLate).ToList();
                if (late.Count > 0)
                {
                    Log.Info($"Saving {late.Count} SQL script(s) for this DB engine...");
                    await SaveScripts(late);
                }
                Log.Info("SQL AfterPackage:");
                await installer.ExecuteSqlAsync(sqlScripts, ElementInstaller.SqlAfterPackage);
            }

            if (data.Count > 0) Log.Stage(ImportStages.Data);
            if (data.Count > 0 && installer.UseSqlBindings)
            {
                // As the package installer: store every binding, then one InstallData call for all of them.
                Log.Info($"Checking {data.Count} data binding(s)...");
                var codes = ElementInstaller.ArchiveSettingCodes(data.Select(d => (d, sources[d.SourcePackage])));
                var prepared = new List<ElementInstaller.SqlBinding>();
                foreach (var d in data)
                {
                    var exists = onTarget.GetValueOrDefault(d.UId)?.Any(e => e.Kind == ItemKind.Data) == true;
                    prepared.Add(await installer.PrepareBindingBySqlAsync(d, sources[d.SourcePackage], itemTarget[d.UId].UId, itemTarget[d.UId].Name, exists, codes));
                }
                if (installer.Conflicts.Count > 0)
                    throw new CreatioException("data bindings not written: " + string.Join(" | ", installer.Conflicts));

                Log.Info("Writing data bindings by SQL...");
                var toInstall = new List<Guid>();
                foreach (var b in prepared.Where(b => b.Install))
                {
                    var w = await installer.WriteBindingBySqlAsync(b);
                    if (w is not { } written) continue; // skipped on a name/UId conflict
                    ((JsonArray)restore["dataBackups"]!).Add(new JsonObject
                    {
                        ["name"] = written.Name, ["isNew"] = written.Created,
                        ["backup"] = JsonSerializer.SerializeToNode(b.Backup, Manifest.Json)
                    });
                    await SaveRestore();
                    toInstall.Add(written.InstallUId);
                }
                if (toInstall.Count > 0)
                {
                    Log.Info($"Installing {toInstall.Count} data binding(s) with one InstallData call...");
                    try { await client.InstallDataAsync(toInstall); }
                    catch (CreatioException ex)
                    {
                        // The installer keeps the reason per binding in SysPackageSchemaData.LastError.
                        var reasons = await installer.ReadBindingErrorsAsync(toInstall);
                        throw new CreatioException(reasons.Count == 0 ? ex.Message
                            : ex.Message + "; " + string.Join("; ", reasons.Select(r => $"{r.Key}: {r.Value}")));
                    }
                }
            }
            else if (data.Count > 0)
            {
                Log.Info($"Installing {data.Count} data binding(s)...");
                foreach (var d in data)
                {
                    var exists = onTarget.GetValueOrDefault(d.UId)?.Any(e => e.Kind == ItemKind.Data) == true;
                    var backup = await installer.InstallDataAsync(d, sources[d.SourcePackage], itemTarget[d.UId].UId, itemTarget[d.UId].Name, exists);
                    ((JsonArray)restore["dataBackups"]!).Add(new JsonObject
                    {
                        ["name"] = d.Name, ["isNew"] = !exists,
                        ["backup"] = JsonSerializer.SerializeToNode(backup, Manifest.Json)
                    });
                    await SaveRestore();
                }
            }

            if (sqlScripts.Count > 0)
            {
                Log.Stage(ImportStages.SqlAfterData);
                Log.Info("SQL AfterSchemaData:");
                await installer.ExecuteSqlAsync(sqlScripts, ElementInstaller.SqlAfterSchemaData);
            }

            // Files last: compilation above writes Terrasoft.Configuration\bin and would overwrite transferred files.
            if (filePlan.Count > 0)
            {
                Log.Stage(ImportStages.Files);
                var changed = filePlan.Count(p => p.State != "identical");
                Log.Info($"Files into {fileRoot}: {changed} to write, {filePlan.Count - changed} identical...");
                foreach (var line in FileTransfer.Describe(filePlan.Where(p => p.State != "identical").ToList())) Log.Info(line);
                var records = FileTransfer.Apply(filePlan, e => TransferArchive.ReadBytes(archivePath, e), backupDir, options.WorkDir);
                foreach (var r in records) ((JsonArray)restore["fileBackups"]!).Add(r!.DeepClone());
                await SaveRestore();
                result.Installed.Add($"{changed} file(s)");
            }
        }
        catch (IOException ex)
        {
            await SaveRestore();
            throw new InstallException($"File transfer failed: {ex.Message}. Backups are in {options.WorkDir}. Restore: cst restore --backup \"{options.WorkDir}\" --env <target>");
        }
        catch (CreatioException ex)
        {
            await SaveRestore();
            throw new InstallException($"{ex.Message}. Backups are in {options.WorkDir}. Restore: cst restore --backup \"{options.WorkDir}\" --env <target>");
        }
        finally
        {
            result.Warnings.AddRange(installer.Warnings);
        }
        await SaveRestore();
        return result;
    }

    /// <summary>
    /// Entity .md: a lookup column's reference schema is "S4" inside its "+ MetaData.Schema.D2 {...}" block
    /// (quotes escaped inside the MetaData string). Points S4 values that are in <paramref name="pending"/> to the
    /// schema itself. Returns null when nothing had to change.
    /// </summary>
    private static string? StubLookups(string md, Guid self, HashSet<Guid> pending)
    {
        bool changed = false;
        var result = System.Text.RegularExpressions.Regex.Replace(md,
            @"(\\?""S4\\?""\s*:\s*\\?"")([0-9a-fA-F-]{36})",
            m =>
            {
                if (!Guid.TryParse(m.Groups[2].Value, out var g) || g == self || !pending.Contains(g)) return m.Value;
                changed = true;
                return m.Groups[1].Value + self;
            });
        return changed ? result : null;
    }

    public static string DbEngineName(int engine) => engine switch { 0 => "MSSql", 1 => "Oracle", 2 => "PostgreSql", _ => engine.ToString() };

    private static string ShortManager(string? manager) => (manager ?? "schema").Replace("SchemaManager", "");

    /// <summary>Exports the current target versions of the package's schemas that will be overwritten (Configuration format).</summary>
    private async Task BackupSchemasAsync(TargetPackage t, List<ManifestItem> items,
        Dictionary<Guid, List<ElementInfo>> onTarget, string backupDir, JsonObject restore, ImportOptions options)
    {
        if (items.Count == 0) return;
        var existing = items.Where(i => onTarget.GetValueOrDefault(i.UId)?.Any(e => e.Kind == ItemKind.Schema) == true).ToList();
        if (existing.Count > 0)
        {
            Log.Info($"[{t.Name}] backing up {existing.Count} schema(s) from target...");
            var ids = await new Catalog(client).GetSchemaIdsAsync(existing.Select(i => i.UId));
            var md = await client.ExportSchemasAsync(existing.Where(i => ids.ContainsKey(i.UId)).Select(i => (ids[i.UId], i.UId, i.Name)));
            var mdDir = Path.Combine(backupDir, "schemas", t.Name);
            Directory.CreateDirectory(mdDir);
            foreach (var item in existing)
            {
                if (!md.TryGetValue(item.Name, out var text)) throw new InstallException($"Could not back up {item.Name} from the target.");
                var file = Path.Combine(mdDir, item.Name + ".md");
                await Io.WriteAllTextAsync(file, text, new UTF8Encoding(false));
                ((JsonArray)restore["schemaBackups"]!).Add(new JsonObject
                {
                    ["package"] = t.Name, ["packageUId"] = t.UId.ToString(), ["name"] = item.Name,
                    ["uId"] = item.UId.ToString(), ["manager"] = item.Manager,
                    ["file"] = Io.GetRelativePath(options.WorkDir, file)
                });
            }
        }
        foreach (var i in items.Except(existing))
            ((JsonArray)restore["createdSchemas"]!).Add($"{t.Name}/{i.Name}");
    }

    private static string WriteMissingReport(string archivePath, List<string> missing)
    {
        var path = Path.ChangeExtension(Path.GetFullPath(archivePath), null) + ".missing.md";
        var sb = new StringBuilder();
        sb.AppendLine($"# Missing dependencies for {Path.GetFileName(archivePath)}");
        sb.AppendLine();
        sb.AppendLine($"Checked {DateTime.Now:yyyy-MM-dd HH:mm}. Nothing was installed. Transfer or create these first, then run the import again.");
        sb.AppendLine();
        foreach (var m in missing) sb.AppendLine("- " + m);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }
}

public sealed class InstallException(string message) : Exception(message);
