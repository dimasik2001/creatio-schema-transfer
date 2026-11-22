using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst;
using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;
using Cst.Transfer;

static class Cli
{
    const string Usage = """
        cst — transfer selected Creatio schemas, SQL scripts and data bindings between environments.

        Interactive UI: run cst without arguments (or with only --settings <file>) in a console window
          (Windows Terminal, powershell.exe; not PowerShell ISE): Export · Import · Restore with menus.

        Usage:
          cst export  --env <src> (--config <list.txt> | --schemas "Pkg->Target:A,B;Pkg2:C") --out <transfer.zip>
                      [--load-to-db [--allow-delete]]
          cst import  --env <dst> --archive <transfer.zip> [--dry-run] [--no-compile] [--rebuild] [--data-designer]
                      [--work-dir <dir>]
          cst inspect --archive <transfer.zip>
          cst restore --env <dst> --backup <work-dir> [--no-compile]
          cst db-tables --env <src> --packages "Pkg1,Pkg2"   (read-only: tables of the packages for the table
                      data transfer — rows, approximate size, default mode, lookup references; thresholds in
                      the "DbData" section of appsettings.json)

        CstGate (SQL gateway, needed ONLY for the table data transfer; everything else uses standard services):
          cst gate-status  --env <e>     is it installed, version, DB engine, may the user use it (CanManageSolution)
          cst gate-install --env <e> [--force] [--no-restart]
                      installs the CstGate package (one DLL, no schemas) with the zip installer and restarts
                      the application if the service is not loaded yet
          cst db-export  --env <src> --plan <plan.txt> --out <data.zip>
                      table rows by a plan (tables: full | top N by Col desc | where <SQL> | ids … | skip | children;
                      children:, refs: pull|expect|author|null|drop, options: rights/translations), pulling
                      referenced rows ("pull") until the selection is closed; adds record rights and translations
          cst db-import  --env <dst> --archive <data.zip> [--dry-run] [--mode insert|upsert] [--on-unique stop|skip] [--work-dir <dir>]
                      one transaction: staging → reference checks/policies → INSERT new rows (upsert: also UPDATE)
                      in dependency order; missing references stop it (exit 3); --dry-run rolls everything back
          cst db-inspect --archive <data.zip>
          cst db-restore --env <dst> --backup <work-dir>   deletes inserted rows, puts back updated ones
          cst db-sql --env <e> (--sql "..." | --file q.sql) [--params-file p.json] [--max-rows N] [--execute [--rollback]]
                      runs SQL through the gate: SELECT rows as a table; --execute = statements in one
                      transaction, --rollback = run and roll back (dry run)

        Import: target packages must already be installed (cst never creates packages or changes their
          dependencies; anything missing is listed in <archive>.missing.md). Elements are installed one by one
          through Creatio's designer services, no package zip: SQL BeforePackage → schemas → DB structure →
          compile → SQL AfterPackage → data bindings → SQL AfterSchemaData. SQL runs only on its own DB engine.

        Export --load-to-db (source in file system development mode, FSM): first loads the export's packages from
          the source's files into its DB (Creatio's LoadPackagesToDB, as clio pkg-to-db), so whole packages, SQL
          scripts, data bindings and translations are exported as they are on disk (schema code is always taken
          from disk). It writes every element of those packages whose files differ, DELETES from the DB elements
          that have no folder on disk, and may update the DB structure, run SQL and install data (Web.config
          AutoUpdateDBStructure / AutoInstallSqlScript / AutoInstallPackageData). cst stops before loading if
          something would be deleted, or cannot check it ("Path" not set), unless --allow-delete.

        Connection (instead of or on top of --env from appsettings.json next to cst.exe):
          --url <http://host:port> --login <user> --password <pwd>   (or CST_PASSWORD / CST_PASSWORD_<ENV>)
          --db-engine MSSql|PostgreSql|Oracle   overrides "DbEngine" of the environment; required for import
            and restore (decides which SQL scripts run), not used by export
          --root <folder>   overrides "Path" of the environment: the Creatio installation root folder as seen from
            this machine (like clio's EnvironmentPath); needed only for the files: section
          --settings <path to appsettings.json>

        Common: --json (machine-readable result on stdout), --verbose

        Schema list format:
          # comment
          packages:                      (whole packages, installed first by Creatio's zip installer: the package
           - UsrWholePackage              is replaced on the target or created; no mapping; stops before installing
                                          if the target package has locally modified elements)
          package LVBase -> UsrBase:
           - SchemaName
           - SomeDataBinding [data]     (optional type: [schema] [sql] [data])
          files:                         (needs "Path" of both environments)
           - Terrasoft.WebApp\Terrasoft.Configuration\bin\Terrasoft.Configuration.dll
           - Terrasoft.WebApp\SomeFolder              (a folder: all files below it)
           - Terrasoft.WebApp\bin\Utk*.dll            (a mask in the file name)
          Files are written last on import (after compilation); identical files are skipped, replaced ones
          are backed up; restore puts them back and deletes files the import created.
        """;

    public static async Task<int> RunAsync(string[] argv)
    {
        // Hosts can pass empty arguments (PowerShell: @($null)); they mean nothing.
        argv = argv.Where(a => !string.IsNullOrEmpty(a)).ToArray();
        // Console UI: no arguments, or only --settings (run.ps1 and wrappers always pass it), in an interactive console.
        bool uiArgs = argv.Length == 0 || (argv.Length == 2 && argv[0].Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (uiArgs && Cst.Ui.UiInput.IsInteractive())
            return await Cst.Ui.UiApp.RunAsync(argv.Length == 2 ? argv[1] : null);
        if (uiArgs) argv = [];

        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return argv.Length == 0 ? 1 : 0;
        }
        var cmd = argv[0].ToLowerInvariant();
        Args a;
        try { a = Args.Parse(argv.Skip(1)); }
        catch (ArgumentException ex) { Log.Error(ex.Message); return 1; }
        Log.Verbose = a.Flag("verbose");
        var json = a.Flag("json");

        try
        {
            return cmd switch
            {
                "export" => await ExportAsync(a, json),
                "import" => await ImportAsync(a, json),
                "inspect" => Inspect(a, json),
                "binding-sql" => BindingSqlDump(a),
                "binding-check" => await BindingCheckAsync(a),
                "restore" => await RestoreAsync(a, json),
                "call" => await CallAsync(a),
                "db-tables" => await DbTablesAsync(a, json),
                "gate-status" => await GateStatusAsync(a, json),
                "gate-install" => await GateInstallAsync(a, json),
                "db-sql" => await DbSqlAsync(a, json),
                "gate-bench" => await GateBenchAsync(a),
                "db-export" => await DbExportAsync(a, json),
                "db-import" => await DbImportAsync(a, json),
                "db-inspect" => DbInspect(a, json),
                "db-restore" => await DbRestoreAsync(a, json),
                _ => Fail(1, $"Unknown command '{argv[0]}'. Run cst --help.")
            };
        }
        catch (ArgumentException ex) { return Fail(1, ex.Message, json); }
        catch (FormatException ex) { return Fail(1, ex.Message, json); }
        catch (TransferException ex) { return Fail(2, ex.Errors, json); }
        catch (Cst.Data.GateMissingException ex) { return Fail(1, ex.Message, json); }
        catch (Cst.Data.GateException ex) { return Fail(4, (ex.Batch != null ? $"batch {ex.Batch}: " : "") + ex.Message + (ex.ErrorNumber != null ? $" [{ex.ErrorType} {ex.ErrorNumber}]" : ""), json); }
        catch (InstallException ex) { return Fail(4, ex.Message, json); }
        catch (CreatioException ex) { return Fail(10, ex.Message, json); }
        catch (Exception ex) { return Fail(10, ex.ToString(), json); }
    }

    static async Task<CreatioClient> ConnectAsync(Args a)
    {
        var settings = AppSettings.Load(a.Value("settings"));
        var env = settings.Resolve(a.Value("env"), a.Value("url"), a.Value("login"), a.Value("password"), a.Value("db-engine"), a.Value("root"));
        var client = new CreatioClient(env);
        Log.Info($"Connecting to {client.Url} as {env.Login}...");
        await client.LoginAsync();
        return client;
    }

    static async Task<int> ExportAsync(Args a, bool json)
    {
        var list = new List<SchemaListPackage>();
        var files = new List<string>();
        var whole = new List<string>();
        if (a.Value("config") is { } cfg)
        {
            list = SchemaList.ParseFile(cfg); files = SchemaList.ParseFilesSection(cfg); whole = SchemaList.ParseWholePackagesSection(cfg);
        }
        if (a.Value("schemas") is { } inline) SchemaList.Merge(list, SchemaList.ParseInline(inline));
        var output = a.Required("out");
        using var client = await ConnectAsync(a);
        FsmLoadResult? loaded = null;
        if (a.Flag("load-to-db")) loaded = await LoadToDbAsync(client, FsmLoader.PackagesOf(list, whole), a.Flag("allow-delete"));
        var manifest = await new Exporter(client).ExportAsync(list, output, files, whole);

        if (json) { PrintJson(new { ok = true, archive = Path.GetFullPath(output), loadedToDb = loaded, manifest }); return 0; }
        PrintPlan(manifest);
        Console.WriteLine($"Archive: {Path.GetFullPath(output)}");
        return 0;
    }

    /// <summary>
    /// export --load-to-db: loads the export's packages from the source's file system into its DB first (FSM stands).
    /// Stops before loading when the load would delete elements, or cannot be checked (no "Path"), unless --allow-delete.
    /// Returns null when there is nothing to load (FSM off, no package folder).
    /// </summary>
    static async Task<FsmLoadResult?> LoadToDbAsync(CreatioClient client, List<string> packages, bool allowDelete)
    {
        var plan = await FsmLoader.PlanAsync(client, packages);
        if (plan.IsFsm == false)
        {
            Log.Warn("--load-to-db: file system development mode is off on the source; nothing to load, the export takes the DB as it is.");
            return null;
        }
        if (plan.IsFsm == null) Log.Warn("--load-to-db: the source does not report its FSM state (GetIsFileDesignMode); trying to load anyway.");
        foreach (var p in plan.NotOnDisk) Log.Warn($"--load-to-db: package {p} has no folder in {plan.PkgPath}; it is not loaded.");
        if (!plan.DiskChecked && !allowDelete)
            throw new TransferException([
                "--load-to-db: the source has no \"Path\" (Creatio folder) in appsettings.json, so cst cannot check what the load would delete. "
                + "Loading deletes from the DB every element of these packages that has no folder on disk: " + string.Join(", ", plan.Packages)
                + ". Set \"Path\" or pass --allow-delete to load anyway. Nothing was loaded."]);
        if (plan.WillDelete.Count > 0 && !allowDelete)
            throw new TransferException(new[] { $"--load-to-db would DELETE {plan.WillDelete.Count} element(s) from the source DB (in the DB, not on disk). Nothing was loaded; pass --allow-delete to load anyway:" }
                .Concat(plan.WillDelete.Select(d => "  - " + d)));
        if (plan.Packages.Count == 0)
        {
            Log.Warn("--load-to-db: no package of the export has a folder on disk; nothing loaded.");
            return null;
        }
        var r = await FsmLoader.LoadAsync(client, plan.Packages);
        if (r.Errors.Count > 0)
            throw new InstallException($"Loading into the source DB failed for {r.Errors.Count} element(s); no archive was written. "
                + $"Elements without errors were loaded ({r.Changes.Count} change(s)).{Environment.NewLine}  - "
                + string.Join(Environment.NewLine + "  - ", r.Errors));
        foreach (var c in r.Changes.Take(40)) Console.WriteLine("  loaded " + c);
        if (r.Changes.Count > 40) Console.WriteLine($"  ... {r.Changes.Count - 40} more");
        if (r.Changes.Count == 0) Console.WriteLine("  nothing to load: the DB already matches the files.");
        return r;
    }

    static async Task<int> ImportAsync(Args a, bool json)
    {
        var archive = a.Required("archive");
        var workDir = a.Value("work-dir") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archive))!,
            Path.GetFileNameWithoutExtension(archive) + "_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        using var client = await ConnectAsync(a);
        var options = new ImportOptions
        {
            DryRun = a.Flag("dry-run"), NoCompile = a.Flag("no-compile"), FullRebuild = a.Flag("rebuild"),
            DataDesigner = a.Flag("data-designer"), WorkDir = workDir
        };
        var r = await new Importer(client).ImportAsync(archive, options);

        int code = r.MissingDependencies.Count > 0 ? 3 : r.CompileErrors.Count > 0 ? 5 : 0;
        if (json) { PrintJson(new { ok = code == 0, exitCode = code, result = r }); return code; }

        foreach (var n in r.Notes) Console.WriteLine("Note: " + n);
        foreach (var w in r.Warnings) Console.WriteLine("Warning: " + w);
        if (r.MissingDependencies.Count > 0)
        {
            Console.WriteLine($"Missing dependencies ({r.MissingDependencies.Count}), nothing installed. Report: {r.MissingReportPath}");
            foreach (var m in r.MissingDependencies) Console.WriteLine("  - " + m.Replace("**", ""));
            return code;
        }
        foreach (var (name, pkg) in r.Placement) Console.WriteLine($"  {name} → {pkg}");
        if (options.DryRun) Console.WriteLine("Dry run: nothing installed.");
        else
        {
            Console.WriteLine($"Installed into: {string.Join(", ", r.Installed)}");
            Console.WriteLine($"Backups: {workDir}");
        }
        if (r.CompileErrors.Count > 0)
        {
            Console.WriteLine($"Compilation errors ({r.CompileErrors.Count}):");
            foreach (var e in r.CompileErrors) Console.WriteLine("  " + e);
            if (r.StoppedAfterCompile) Console.WriteLine("SQL AfterPackage/AfterSchemaData scripts and data bindings were NOT installed. Fix the errors and import again.");
        }
        return code;
    }

    /// <summary>
    /// Read-only: downloads each target package and reports, per data binding in the archive, whether the target
    /// already has a binding of the same Name — and whether its UId matches. With --fix (or --on-conflict) writes a
    /// resolved copy of the archive.
    ///   cst binding-check --env &lt;e&gt; --archive x.zip [--fix] [--on-conflict uid|skip|rename] [--out resolved.zip]
    /// </summary>
    static async Task<int> BindingCheckAsync(Args a)
    {
        var archive = a.Required("archive");
        var (manifest, _) = TransferArchive.Read(archive);
        if (!manifest.Items.Any(i => i.Kind == ItemKind.Data)) { Console.WriteLine("No data bindings in the archive."); return 0; }
        bool fix = a.Flag("fix");
        var auto = a.Value("on-conflict")?.Trim().ToLowerInvariant(); // uid | skip | rename (non-interactive)
        if (auto != null) { fix = true; if (auto is not ("uid" or "skip" or "rename")) { Log.Error("--on-conflict must be uid, skip or rename."); return 1; } }
        if (fix && auto == null && Console.IsInputRedirected)
        {
            Log.Error("--fix needs an interactive console (stdin is redirected), or pass --on-conflict uid|skip|rename.");
            return 1;
        }
        using var client = await ConnectAsync(a);
        var rows = await BindingCheck.CheckAsync(client, manifest);
        int conflicts = 0;
        var fixes = new List<BindingFix>();
        foreach (var g in rows.GroupBy(r => r.Item.TargetPackage, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Package {g.Key}:");
            if (g.All(r => r.State == BindingState.Unavailable) && g.First().Error is { } err)
            {
                Console.WriteLine($"  cannot download package: {err}");
                continue;
            }
            foreach (var r in g)
            {
                var item = r.Item;
                switch (r.State)
                {
                    case BindingState.New: Console.WriteLine($"  NEW       {item.Name}"); break;
                    case BindingState.Same: Console.WriteLine($"  ok        {item.Name}  (same UId, will update)"); break;
                    case BindingState.Unavailable: Console.WriteLine($"  ?         {item.Name}  {r.Error}"); break;
                    case BindingState.Conflict:
                        Console.WriteLine($"  CONFLICT  {item.Name}  target UId {r.TargetUId}  !=  archive UId {item.UId}");
                        conflicts++;
                        if (!fix) break;
                        var f = auto switch
                        {
                            "uid" => new BindingFix { Package = item.SourcePackage, Name = item.Name, Action = BindingFixAction.UseTargetUId, TargetUId = r.TargetUId },
                            "skip" => new BindingFix { Package = item.SourcePackage, Name = item.Name, Action = BindingFixAction.Skip },
                            "rename" => new BindingFix { Package = item.SourcePackage, Name = item.Name, Action = BindingFixAction.Rename, NewName = BindingFix.DefaultRename(item.Name) },
                            _ => PromptBindingFix(item.SourcePackage, item.Name, r.TargetUId!.Value)
                        };
                        if (f != null) fixes.Add(f);
                        break;
                }
            }
        }

        if (conflicts == 0)
        {
            Console.WriteLine("No name/UId conflicts.");
            return 0;
        }
        if (!fix)
        {
            Console.WriteLine($"{conflicts} conflicting binding(s). Re-run with --fix to resolve them into a new archive, "
                + "or handle them during import (cst asks per conflict).");
            return 0;
        }
        if (fixes.Count == 0)
        {
            Console.WriteLine($"{conflicts} conflict(s), none resolved. No archive written.");
            return 0;
        }
        var outPath = a.Value("out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archive))!,
            Path.GetFileNameWithoutExtension(archive) + "-resolved.zip");
        BindingCheck.WriteResolvedArchive(archive, outPath, fixes);
        Console.WriteLine($"Resolved archive written: {outPath}");
        Console.WriteLine("Review it, then: cst import --env <e> --archive \"" + outPath + "\"");
        return 0;
    }

    /// <summary>Asks, on the console, how to resolve one binding conflict. Returns null to leave it unresolved.</summary>
    static BindingFix? PromptBindingFix(string pkg, string name, Guid targetUId)
    {
        while (true)
        {
            Console.Error.WriteLine($"    [R]ename  - give the incoming binding a new name (target keeps its own)");
            Console.Error.WriteLine($"    [U]se UId - take the target's UId {targetUId} (update that binding in place)");
            Console.Error.WriteLine($"    [S]kip    - drop this binding from the archive (target unchanged)");
            Console.Error.WriteLine($"    [K]eep    - leave the conflict as is");
            Console.Error.Write("  Resolve this conflict [R/U/S/K]: ");
            switch (Console.ReadLine()?.Trim().ToLowerInvariant())
            {
                case "u" or "use" or "uid": return new BindingFix { Package = pkg, Name = name, Action = BindingFixAction.UseTargetUId, TargetUId = targetUId };
                case "s" or "skip": return new BindingFix { Package = pkg, Name = name, Action = BindingFixAction.Skip };
                case "k" or "keep" or "" or null: return null;
                case "r" or "rename":
                    Console.Error.Write("  New name (empty to cancel): ");
                    var nn = Console.ReadLine()?.Trim();
                    if (string.IsNullOrEmpty(nn)) continue;
                    if (string.Equals(nn, name, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("  Same name; pick another."); continue; }
                    return new BindingFix { Package = pkg, Name = name, Action = BindingFixAction.Rename, NewName = nn };
                default: Console.Error.WriteLine("  Answer R, U, S or K."); continue;
            }
        }
    }

    /// <summary>
    /// Read-only: entities of packages offered for the table data transfer, with rows, size, default mode and
    /// references (docs/db-transfer.md). cst db-tables --env dev --packages "UtkCstSource,Other"
    /// </summary>
    static async Task<int> DbTablesAsync(Args a, bool json)
    {
        var names = a.Required("packages").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToList();
        var settings = AppSettings.Load(a.Value("settings"));
        var dbEngine = a.Value("db-engine") ?? (a.Value("env") is { } e && settings.Environments.TryGetValue(e, out var es) ? es.DbEngine : null) ?? "MSSql";
        using var client = await ConnectAsync(a);
        var all = await new Catalog(client).LoadPackagesAsync();
        var missing = names.Where(n => !all.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0) throw new TransferException(missing.Select(m => $"Package '{m}' is not on the stand."));
        var packages = names.Select(n => all.First(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();

        var report = await new Cst.Data.TableReport(client, settings.DbData, dbEngine)
            .BuildAsync(packages, t => Log.Debug($"Reading {t}..."));
        if (json) { PrintJson(report); return 0; }

        Console.WriteLine($"Tables of {string.Join(", ", report.Packages)} (sizes approximate, {dbEngine}; large ≥ {settings.DbData.LargeTableMb} MB):");
        int w = Math.Max(10, report.Tables.Select(t => t.Name.Length).DefaultIfEmpty(10).Max());
        Console.WriteLine($"  {"",1} {"Table".PadRight(w)}  {"Group",-10} {"Rows",10} {"~Size",10}  Mode / notes");
        foreach (var t in report.Tables)
        {
            var group = t.Group.ToString().ToLowerInvariant();
            var notes = new List<string>();
            if (t.Excluded != null) notes.Add("excluded: " + t.Excluded);
            else notes.Add(t.Mode + (t.Large ? " (large)" : ""));
            if (t.Group == Cst.Data.TableGroup.Extended) notes.Add("only replaced in " + string.Join(", ", t.ExtendedIn));
            else if (t.ExtendedIn.Count > 0) notes.Add("also replaced in " + string.Join(", ", t.ExtendedIn));
            if (t.BinaryColumns.Count > 0) notes.Add("binary: " + string.Join(", ", t.BinaryColumns));
            if (t.UnreadableColumns.Count > 0) notes.Add("not transferred (secure): " + string.Join(", ", t.UnreadableColumns));
            if (t.AdministratedByRecords) notes.Add("record rights (not readable yet)");
            Console.WriteLine($"  {(t.Selected ? "x" : " "),1} {t.Name.PadRight(w)}  {group,-10} {(t.Rows?.ToString() ?? "?"),10} {Cst.Data.TableReport.FormatBytes(t.Bytes),10}  {string.Join("; ", notes)}");
        }
        Console.WriteLine();
        Console.WriteLine("References (lookup columns of transferable tables):");
        foreach (var r in report.Refs.OrderBy(r => r.Table).ThenBy(r => r.Column))
            Console.WriteLine($"  {r.Table}.{r.Column} → {r.RefTable}   {(r.InSelection ? "in selection" : "outside")}   {r.Policy}" +
                              (r.Required ? "   required" : "") + (r.Weak ? "   no FK" : ""));
        return 0;
    }

    /// <summary>cst gate-status --env x: is CstGate installed, its version, DB engine, may the user use it.</summary>
    static async Task<int> GateStatusAsync(Args a, bool json)
    {
        using var client = await ConnectAsync(a);
        var embedded = Cst.Data.GateInstaller.EmbeddedVersion;
        Cst.Data.GateInfo? info = null;
        try { info = await new Cst.Data.GateClient(client).InfoAsync(); }
        catch (Cst.Data.GateMissingException) { }
        if (json) { PrintJson(new { installed = info != null, embeddedVersion = embedded, info }); return info == null ? 3 : 0; }
        if (info == null)
        {
            Console.WriteLine($"CstGate is not installed on {client.Url}. Install it with: cst gate-install --env <stand> (version {embedded}).");
            return 3;
        }
        PrintGateInfo(info, embedded);
        return 0;
    }

    static void PrintGateInfo(Cst.Data.GateInfo info, string embedded)
    {
        Console.WriteLine($"CstGate {info.Version}{(info.Version != embedded ? $" (this cst carries {embedded}: run gate-install to update)" : "")}");
        Console.WriteLine($"  runtime:  {info.Runtime}");
        Console.WriteLine($"  user:     {info.User}{(info.CanUse ? "" : " — NO CanManageSolution: the gate refuses SQL for this user")}");
        if (info.Engine != null) Console.WriteLine($"  database: {info.Engine} {info.ServerVersion}, {info.Database} ({info.Provider})");
    }

    /// <summary>
    /// cst gate-install --env x [--force] [--no-restart]: installs the CstGate package (one DLL, no schemas) with the
    /// zip installer, restarts the application when the service is not loaded yet, and waits until it answers.
    /// </summary>
    static async Task<int> GateInstallAsync(Args a, bool json)
    {
        using var client = await ConnectAsync(a);
        var gate = new Cst.Data.GateClient(client);
        var embedded = Cst.Data.GateInstaller.EmbeddedVersion;
        try
        {
            var current = await gate.InfoAsync();
            if (current.Version == embedded && !a.Flag("force"))
            {
                Console.WriteLine($"CstGate {current.Version} is already installed (use --force to reinstall).");
                PrintGateInfo(current, embedded);
                return 0;
            }
            Log.Info($"Updating CstGate {current.Version} → {embedded}...");
        }
        catch (Cst.Data.GateMissingException) { Log.Info($"Installing CstGate {embedded}..."); }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Cst.Data.GateInfo info;
        try { info = await Cst.Data.GateInstaller.InstallAsync(client, !a.Flag("no-restart"), s => Log.Info(s)); }
        catch (Cst.Data.GateMissingException) when (a.Flag("no-restart"))
        {
            Console.WriteLine("The package is installed; the service loads after an application restart (run without --no-restart, or restart Creatio).");
            return 0;
        }
        if (json) { PrintJson(new { ok = true, seconds = (int)sw.Elapsed.TotalSeconds, info }); return 0; }
        Console.WriteLine($"CstGate {info.Version} answers ({sw.Elapsed.TotalSeconds:0} s).");
        PrintGateInfo(info, embedded);
        return 0;
    }

    /// <summary>
    /// cst db-sql --env x (--sql "..." | --file q.sql) [--max-rows N] [--execute [--rollback]] [--json]: runs SQL through
    /// CstGate. Default: a SELECT, rows printed as a table. --execute: statements in one transaction (--rollback = dry run).
    /// </summary>
    static async Task<int> DbSqlAsync(Args a, bool json)
    {
        var sql = a.Value("sql") ?? (a.Value("file") is { } f ? await Io.ReadAllTextAsync(f) : throw new ArgumentException("--sql or --file is required."));
        var parameters = a.Value("params-file") is { } pf
            ? System.Text.Json.Nodes.JsonNode.Parse(await Io.ReadAllTextAsync(pf)) as System.Text.Json.Nodes.JsonObject
            : null;
        using var client = await ConnectAsync(a);
        var gate = new Cst.Data.GateClient(client);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string Sizes() => $"request {Cst.Data.TableReport.FormatBytes(gate.LastSizes.RequestBytes)}, response {Cst.Data.TableReport.FormatBytes(gate.LastSizes.ResponseBytes)} (unpacked {Cst.Data.TableReport.FormatBytes(gate.LastSizes.ResponseUnpacked)}), round trip {sw.ElapsedMilliseconds} ms";
        if (a.Flag("execute"))
        {
            var r = await gate.ExecuteAsync([new Cst.Data.GateBatch(sql, parameters)], rollback: a.Flag("rollback"));
            if (json) { PrintJson(r); return 0; }
            Console.WriteLine($"affected: {string.Join(", ", r.Affected)}; {(r.Committed ? "committed" : "rolled back")}; {r.Ms} ms on the server; {Sizes()}");
            return 0;
        }
        long maxRows = long.TryParse(a.Value("max-rows"), out var m) ? m : 200;
        var res = await gate.QueryAsync(sql, parameters, maxRows: maxRows);
        if (json) { PrintJson(res); return 0; }
        if (a.Flag("stats"))
        {
            Console.WriteLine($"{res.Rows.Count} rows{(res.Truncated ? " (truncated)" : "")}, {res.Ms} ms on the server; {Sizes()}");
            return 0;
        }
        PrintRows(res);
        return 0;
    }

    /// <summary>
    /// Diagnostics: cst gate-bench --env x [--rows N] [--table Lookup]: N generated rows → #temp staging (load batch) →
    /// INSERT into a lookup table (Id, Name, Description, CreatedOn) in one transaction that is ROLLED BACK. Measures
    /// the write path the data import uses. Nothing stays in the table.
    /// </summary>
    static async Task<int> GateBenchAsync(Args a)
    {
        int n = int.TryParse(a.Value("rows"), out var r) ? r : 20000;
        var table = a.Value("table") ?? "UtkCstStatus";
        using var client = await ConnectAsync(a);
        var gate = new Cst.Data.GateClient(client);
        var info = await gate.InfoAsync();
        bool pg = info.Engine == "PostgreSql";
        var stage = pg ? "cst_stage" : "#cst_stage";
        var create = pg
            ? "CREATE TEMP TABLE cst_stage (\"Id\" uuid, \"Name\" varchar(250), \"Description\" varchar(250), \"CreatedOn\" timestamp) ON COMMIT DROP"
            : "CREATE TABLE #cst_stage (Id uniqueidentifier, Name nvarchar(250), Description nvarchar(250), CreatedOn datetime2)";
        var insert = pg
            ? $"INSERT INTO \"{table}\" (\"Id\", \"Name\", \"Description\", \"CreatedOn\", \"ModifiedOn\") SELECT \"Id\", \"Name\", \"Description\", \"CreatedOn\", \"CreatedOn\" FROM cst_stage"
            : $"INSERT INTO [{table}] (Id, Name, Description, CreatedOn, ModifiedOn) SELECT Id, Name, Description, CreatedOn, CreatedOn FROM #cst_stage";
        var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
        var rows = new System.Text.Json.Nodes.JsonArray();
        for (int i = 0; i < n; i++)
            rows.Add(new System.Text.Json.Nodes.JsonArray(Guid.NewGuid().ToString(), $"cst bench row {i} Тест", $"Description of row number {i} with some text", now));
        var columns = new[] { new Cst.Data.GateColumn("Id", "guid"), new("Name", "string"), new("Description", "string"), new("CreatedOn", "datetime") };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await gate.ExecuteAsync([
            new Cst.Data.GateBatch(create),
            Cst.Data.GateBatch.LoadRows(stage, columns, rows, a.Value("method")),
            new Cst.Data.GateBatch(insert),
            new Cst.Data.GateBatch(pg ? $"SELECT COUNT(*) AS n FROM \"{table}\" WHERE \"Name\" LIKE 'cst bench row%'" : $"SELECT COUNT(*) AS n FROM [{table}] WHERE Name LIKE N'cst bench row%'", ReturnRows: true)
        ], rollback: true);
        var total = sw.ElapsedMilliseconds;
        Console.WriteLine($"{info.Engine}: {n} rows → {stage} → {table}, rolled back. Affected per batch: {string.Join(", ", res.Affected)}; " +
                          $"visible inside the transaction: {res.Results[3].Rows[0][0]}");
        Console.WriteLine($"ms per batch (create, load, insert, count+rollback): {string.Join(", ", res.BatchMs)}");
        foreach (var note in res.Notes) Console.WriteLine("  " + note);
        Console.WriteLine($"server {res.Ms} ms, round trip {total} ms ({n * 1000.0 / Math.Max(1, total):0} rows/s); request {Cst.Data.TableReport.FormatBytes(gate.LastSizes.RequestBytes)}");
        return 0;
    }

    /// <summary>cst db-export --env x --plan plan.txt --out data.zip: table rows by a plan, closed under references.</summary>
    static async Task<int> DbExportAsync(Args a, bool json)
    {
        var settings = AppSettings.Load(a.Value("settings"));
        var plan = Cst.Data.DataPlan.Parse(await Io.ReadAllTextAsync(a.Required("plan")), settings.DbData);
        var output = a.Required("out");
        using var client = await ConnectAsync(a);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var manifest = await new Cst.Data.DataExporter(client, settings.DbData).ExportAsync(plan, output, s => Log.Info(s));
        if (json) { PrintJson(new { ok = true, archive = Path.GetFullPath(output), manifest }); return 0; }
        PrintDataManifest(manifest);
        Console.WriteLine($"Archive: {Path.GetFullPath(output)} ({Cst.Data.TableReport.FormatBytes(new FileInfo(output).Length)}, {sw.Elapsed.TotalSeconds:0.0} s)");
        return 0;
    }

    static void PrintDataManifest(Cst.Data.DataManifest m)
    {
        Console.WriteLine($"Table data from {m.SourceUrl} ({m.SourceEngine}, {m.SourceDatabase}), {m.CreatedUtc:yyyy-MM-dd HH:mm} UTC:");
        int w = Math.Max(10, m.Tables.Select(t => t.Name.Length).DefaultIfEmpty(10).Max());
        foreach (var t in m.Tables)
        {
            var pulled = t.Rows - t.SelectedRows;
            var extra = t.CompanionOf != null ? $"{t.Mode} of {t.CompanionOf}" : t.Mode + (pulled > 0 ? $", +{pulled} by references/children" : "");
            Console.WriteLine($"  {t.Name.PadRight(w)} {t.Rows,9} rows   {extra}");
        }
        var refs = m.Tables.SelectMany(t => t.Edges.Where(e => e.Policy != Cst.Data.RefPolicy.Author).Select(e => $"{t.Name}.{e.Column} → {e.RefTable}: {e.Policy}")).ToList();
        if (refs.Count > 0) { Console.WriteLine("References (CreatedBy/ModifiedBy: author, not listed):"); foreach (var r in refs) Console.WriteLine("  " + r); }
        foreach (var warning in m.Warnings) Console.WriteLine("WARNING: " + warning);
    }

    static int DbInspect(Args a, bool json)
    {
        var m = Cst.Data.DataArchive.ReadManifest(a.Required("archive"));
        if (json) { PrintJson(m); return 0; }
        PrintDataManifest(m);
        Console.WriteLine();
        Console.WriteLine("Plan:");
        Console.WriteLine(Cst.Data.DataArchive.ReadPlan(a.Required("archive")));
        return 0;
    }

    /// <summary>cst db-import --env x --archive data.zip [--dry-run] [--mode insert|upsert] [--work-dir d]</summary>
    static async Task<int> DbImportAsync(Args a, bool json)
    {
        var mode = (a.Value("mode") ?? "insert").ToLowerInvariant() switch
        {
            "insert" => Cst.Data.DataImportMode.Insert,
            "upsert" => Cst.Data.DataImportMode.Upsert,
            var other => throw new ArgumentException($"--mode {other}: use insert or upsert.")
        };
        var onUnique = (a.Value("on-unique") ?? "stop").ToLowerInvariant() switch
        {
            "stop" => Cst.Data.UniqueConflictAction.Stop,
            "skip" => Cst.Data.UniqueConflictAction.Skip,
            var other => throw new ArgumentException($"--on-unique {other}: use stop or skip.")
        };
        using var client = await ConnectAsync(a);
        var r = await new Cst.Data.DataImporter(client).ImportAsync(a.Required("archive"), mode, a.Flag("dry-run"), a.Value("work-dir"), s => Log.Info(s), onUnique);
        bool uniqueStop = r.Unique.Any(u => !u.Skipped);
        int code = r.Errors.Count > 0 ? 2 : r.Missing.Count > 0 || uniqueStop ? 3 : 0;
        if (json) { PrintJson(new { ok = code == 0, exitCode = code, result = r }); return code; }
        foreach (var e in r.Errors) Console.WriteLine("ERROR: " + e);
        foreach (var m in r.Missing)
            Console.WriteLine($"MISSING: {m.Table}.{m.Column} → {m.RefTable} ({m.Policy}): {m.Count} ids on neither the target nor the archive, e.g. {string.Join(", ", m.Samples)}");
        if (r.Missing.Count > 0) Console.WriteLine("Nothing was written. Change the policy of those references in the plan (refs: … null | drop | author), add the rows, or install them first.");
        foreach (var u in r.Unique)
            Console.WriteLine($"{(u.Skipped ? "SKIPPED" : "UNIQUE")}: {u.Table} index {u.Index} ({string.Join(", ", u.Columns)}): {u.Count} new rows have values the target already has under other rows, e.g. {string.Join("; ", u.Samples)}");
        if (uniqueStop) Console.WriteLine("Nothing was written. Run with --on-unique skip to leave those rows out, or align the rows on the target.");
        foreach (var wn in r.Warnings) Console.WriteLine("WARNING: " + wn);
        if (code != 0) return code;
        int w = Math.Max(10, r.Tables.Select(t => t.Name.Length).DefaultIfEmpty(10).Max());
        Console.WriteLine($"{(r.DryRun ? "Dry run (rolled back)" : "Imported")} — {mode.ToString().ToLowerInvariant()} mode, {r.Ms / 1000.0:0.0} s:");
        foreach (var t in r.Tables)
        {
            var notes = new List<string>();
            if (t.AuthorReplaced > 0) notes.Add($"author replaced {t.AuthorReplaced}");
            if (t.Nulled > 0) notes.Add($"refs nulled {t.Nulled}");
            if (t.Dropped > 0) notes.Add($"dropped {t.Dropped}");
            if (t.DeferredColumns.Count > 0) notes.Add("set after insert: " + string.Join(", ", t.DeferredColumns));
            Console.WriteLine($"  {t.Name.PadRight(w)} archive {t.ArchiveRows,8}  new {t.Inserted,8}  updated {t.Updated,8}  {string.Join("; ", notes)}");
        }
        if (r.WorkDir != null) Console.WriteLine($"Backup for db-restore: {r.WorkDir}");
        Console.WriteLine("Creatio caches lookups and settings: clear Redis or restart the application if the imported tables are cached.");
        return 0;
    }

    static async Task<int> DbRestoreAsync(Args a, bool json)
    {
        using var client = await ConnectAsync(a);
        var log = await new Cst.Data.DataImporter(client).RestoreAsync(a.Required("backup"), s => Log.Info(s));
        if (json) { PrintJson(new { ok = true, log }); return 0; }
        foreach (var l in log) Console.WriteLine("  " + l);
        Console.WriteLine("Restored.");
        return 0;
    }

    static void PrintRows(Cst.Data.GateResult res)
    {
        static string Cell(System.Text.Json.Nodes.JsonNode? n)
        {
            var s = n is null ? "NULL" : n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var str) ? str : n.ToJsonString();
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length > 60 ? s[..57] + "..." : s;
        }
        var cells = res.Rows.Select(r => r.Select(Cell).ToArray()).ToList();
        var widths = res.Columns.Select((c, i) => Math.Max(c.Name.Length, cells.Select(r => i < r.Length ? r[i].Length : 0).DefaultIfEmpty(0).Max())).ToArray();
        Console.WriteLine(string.Join("  ", res.Columns.Select((c, i) => c.Name.PadRight(widths[i]))));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in cells) Console.WriteLine(string.Join("  ", r.Select((s, i) => s.PadRight(widths[i]))));
        Console.WriteLine($"({res.Rows.Count} rows{(res.Truncated ? ", truncated — use --max-rows" : "")}, {res.Ms} ms)");
    }

    /// <summary>Diagnostics: cst call --env x --path DataService/json/SyncReply/SelectQuery --body-file q.json</summary>
    static async Task<int> CallAsync(Args a)
    {
        var body = a.Value("body-file") is { } f ? await Io.ReadAllTextAsync(f) : a.Value("body") ?? "{}";
        using var client = await ConnectAsync(a);
        var (status, text) = await client.RawPostAsync(a.Required("path"), body);
        Console.WriteLine($"HTTP {status}");
        Console.WriteLine(text);
        return status is >= 200 and < 300 ? 0 : 4;
    }

    static int Inspect(Args a, bool json)
    {
        var (manifest, _) = TransferArchive.Read(a.Required("archive"));
        if (json) PrintJson(manifest);
        else PrintPlan(manifest);
        return 0;
    }

    /// <summary>
    /// Diagnostics: prints the SQL that cst would run to store each data binding (BindingSql), offline from the
    /// archive. Lets you see the exact statements when an install reports a failed CstTmp_* script.
    ///   cst binding-sql --archive x.zip [--db-engine MSSql|PostgreSql] [--name OneBinding] [--login Supervisor]
    /// </summary>
    static int BindingSqlDump(Args a)
    {
        var (manifest, sources) = TransferArchive.Read(a.Required("archive"));
        var engine = EnvironmentSettings.ParseDbEngine(a.Value("db-engine") ?? "MSSql", "binding-sql");
        var login = a.Value("login") ?? "Supervisor";
        var only = a.Value("name");
        var pkgUId = Guid.Parse("00000000-0000-0000-0000-000000000001"); // placeholder (offline dump)
        var items = manifest.Items.Where(i => i.Kind == ItemKind.Data && (only == null || i.Name.Equals(only, StringComparison.OrdinalIgnoreCase))).ToList();
        if (items.Count == 0) { Log.Error("No data bindings in the archive" + (only == null ? "." : $" named '{only}'.")); return 2; }
        foreach (var item in items)
        {
            var source = sources[item.SourcePackage];
            var folder = $"Data/{item.Name}/";
            // Offline: treat every culture present in the archive as existing on the target.
            var prefix = folder + "Localization/data.";
            var cultures = source.Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Substring(prefix.Length, k.Length - prefix.Length - ".json".Length))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"========== {item.SourcePackage}/{item.Name} ({item.UId}) ==========");
            try
            {
                var r = BindingSql.Build(engine, source, folder, item.UId, item.Name, pkgUId, login, cultures);
                Console.WriteLine(r.Sql);
            }
            catch (Exception ex) { Console.WriteLine($"-- could not build: {ex.Message}"); }
            Console.WriteLine();
        }
        return 0;
    }

    static async Task<int> RestoreAsync(Args a, bool json)
    {
        var dir = a.Required("backup");
        if (!File.Exists(Restorer.InfoPath(dir))) throw new ArgumentException($"{Restorer.InfoPath(dir)} not found.");
        using var client = await ConnectAsync(a);
        var r = await new Restorer(client).RestoreAsync(dir, a.Flag("no-compile"));
        foreach (var w in r.Warnings) Log.Warn(w);
        if (json) PrintJson(new { ok = r.CompileErrors.Count == 0, restored = r.Restored, notRemoved = r.NotRemoved, compileErrors = r.CompileErrors });
        else
        {
            Console.WriteLine($"Restored: {string.Join(", ", r.Restored)}");
            Console.WriteLine("Executed SQL scripts are not undone.");
            if (r.NotRemoved.Count > 0)
            {
                Console.WriteLine("Created by the import and NOT removed (delete manually if needed):");
                foreach (var l in r.NotRemoved) Console.WriteLine("  - " + l);
            }
            foreach (var e in r.CompileErrors) Console.WriteLine("  " + e);
        }
        return r.CompileErrors.Count > 0 ? 5 : 0;
    }

    static void PrintPlan(Manifest m)
    {
        Console.WriteLine($"Source: {m.SourceUrl}, created {m.CreatedUtc:u}");
        if (m.WholePackages.Count > 0)
        {
            Console.WriteLine("Whole packages (Creatio zip installer, installed first; replace the package on the target):");
            foreach (var w in m.WholePackages)
                Console.WriteLine($"  {w.Name} {w.Version}  ({w.Schemas.Count} schemas{(w.DependsOn.Count == 0 ? "" : "; depends on " + string.Join(", ", w.DependsOn.Select(d => d.Name)))})");
        }
        Console.WriteLine("Packages (install order):");
        foreach (var p in m.Packages) Console.WriteLine($"  {p.Order + 1}. {p.Source}" + (p.Source == p.Target ? "" : $" → {p.Target}"));
        Console.WriteLine("Elements (install order; equal numbers are one dependency cycle):");
        var byUId = m.Items.ToDictionary(i => i.UId);
        foreach (var i in m.Items)
        {
            var deps = i.DependsOn.Count == 0 ? "" : "  needs: " + string.Join(", ", i.DependsOn.Select(d => byUId[d].Name));
            Console.WriteLine($"  {i.Order + 1,3}. [{Short(i)}] {i.SourcePackage}/{i.Name}{deps}");
        }
        if (m.External.Count > 0)
        {
            Console.WriteLine($"Required on target (not in archive): {m.External.Count}");
            foreach (var e in m.External) Console.WriteLine($"  {e.SourcePackage}/{e.Name} ({e.Kind}) ← {string.Join(", ", e.RequiredBy)}");
        }
        if (m.Files.Count > 0)
        {
            Console.WriteLine($"Files (relative to the Creatio root folder, written last on import): {m.Files.Count}");
            var shown = Log.Verbose ? m.Files : m.Files.Take(20).ToList();
            foreach (var f in shown) Console.WriteLine($"  {f.Path}  ({f.Size:N0} bytes)");
            if (shown.Count < m.Files.Count) Console.WriteLine($"  ... {m.Files.Count - shown.Count} more (--verbose lists all)");
        }
        foreach (var w in m.Warnings) Console.WriteLine("Warning: " + w);
    }

    static string Short(ManifestItem i) => i.Kind switch
    {
        Cst.Packaging.ItemKind.SqlScript => "sql",
        Cst.Packaging.ItemKind.Data => "data",
        _ => (i.Manager ?? "schema").Replace("SchemaManager", "").Replace("Manager", "")
    };

    static void PrintJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Manifest.Json));

    static int Fail(int code, string message, bool json = false) => Fail(code, [message], json);

    static int Fail(int code, IEnumerable<string> messages, bool json)
    {
        var list = messages.ToList();
        if (json) PrintJson(new { ok = false, exitCode = code, errors = list });
        else foreach (var m in list) Log.Error(m);
        return code;
    }
}

sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "verbose", "dry-run", "no-compile", "rebuild", "data-designer", "fix", "load-to-db", "allow-delete",
        "execute", "rollback", "no-restart", "force", "stats" };

    public static Args Parse(IEnumerable<string> argv)
    {
        var a = new Args();
        var list = argv.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (!s.StartsWith("--")) throw new ArgumentException($"Unexpected argument '{s}'.");
            var key = s[2..];
            string? value = null;
            var eq = key.IndexOf('=');
            if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
            else if (!Flags.Contains(key))
            {
                if (i + 1 >= list.Count) throw new ArgumentException($"--{key} needs a value.");
                value = list[++i];
            }
            a._values[key] = value ?? "true";
        }
        return a;
    }

    public string? Value(string key) => _values.GetValueOrDefault(key);
    public bool Flag(string key) => _values.ContainsKey(key);
    public string Required(string key) => Value(key) ?? throw new ArgumentException($"--{key} is required.");
}
