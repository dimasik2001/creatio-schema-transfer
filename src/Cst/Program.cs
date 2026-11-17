using System.Text.Json;
using System.Text.Json.Nodes;
using Cst;
using Cst.Config;
using Cst.Creatio;
using Cst.Transfer;

// Exit codes: 0 ok, 1 usage/config, 2 validation (export or import checks), 3 missing dependencies,
// 4 installation failed, 5 compilation errors, 10 unexpected error.
return await Cli.RunAsync(args);

static class Cli
{
    const string Usage = """
        cst — transfer selected Creatio schemas, SQL scripts and data bindings between environments.

        Usage:
          cst export  --env <src> (--config <list.txt> | --schemas "Pkg->Target:A,B;Pkg2:C") --out <transfer.zip>
          cst import  --env <dst> --archive <transfer.zip> [--dry-run] [--no-compile] [--rebuild] [--data-designer]
                      [--work-dir <dir>]
          cst inspect --archive <transfer.zip>
          cst restore --env <dst> --backup <work-dir> [--no-compile]

        Import: target packages must already be installed (cst never creates packages or changes their
          dependencies; anything missing is listed in <archive>.missing.md). Elements are installed one by one
          through Creatio's designer services, no package zip: SQL BeforePackage → schemas → DB structure →
          compile → SQL AfterPackage → data bindings → SQL AfterSchemaData. SQL runs only on its own DB engine.

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
                "restore" => await RestoreAsync(a, json),
                "call" => await CallAsync(a),
                _ => Fail(1, $"Unknown command '{argv[0]}'. Run cst --help.")
            };
        }
        catch (ArgumentException ex) { return Fail(1, ex.Message, json); }
        catch (FormatException ex) { return Fail(1, ex.Message, json); }
        catch (TransferException ex) { return Fail(2, ex.Errors, json); }
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
        if (a.Value("config") is { } cfg) { list = SchemaList.ParseFile(cfg); files = SchemaList.ParseFilesSection(cfg); }
        if (a.Value("schemas") is { } inline) SchemaList.Merge(list, SchemaList.ParseInline(inline));
        var output = a.Required("out");
        using var client = await ConnectAsync(a);
        var manifest = await new Exporter(client).ExportAsync(list, output, files);

        if (json) { PrintJson(new { ok = true, archive = Path.GetFullPath(output), manifest }); return 0; }
        PrintPlan(manifest);
        Console.WriteLine($"Archive: {Path.GetFullPath(output)}");
        return 0;
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

    /// <summary>Diagnostics: cst call --env x --path DataService/json/SyncReply/SelectQuery --body-file q.json</summary>
    static async Task<int> CallAsync(Args a)
    {
        var body = a.Value("body-file") is { } f ? await File.ReadAllTextAsync(f) : a.Value("body") ?? "{}";
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

    static async Task<int> RestoreAsync(Args a, bool json)
    {
        var dir = a.Required("backup");
        var infoPath = Path.Combine(dir, "restore.json");
        if (!File.Exists(infoPath)) throw new ArgumentException($"{infoPath} not found.");
        var info = JsonNode.Parse(File.ReadAllText(infoPath))!;
        JsonObject[] Arr(string key) => info[key]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var schemaBackups = Arr("schemaBackups");
        var sqlBackups = Arr("sqlBackups");
        var dataBackups = Arr("dataBackups");
        var leftovers = info["createdSchemas"]?.AsArray().Select(n => "schema " + n).ToList() ?? [];
        var restored = new List<string>();
        using var client = await ConnectAsync(a);
        var installer = new ElementInstaller(client, client.DbEngineType);

        foreach (var b in dataBackups.Reverse())
        {
            var backup = b["backup"].Deserialize<ElementInstaller.DataBackup>(Manifest.Json)!;
            Log.Info($"[data] restoring previous values of {b["name"]}...");
            await installer.RestoreDataAsync(backup, dir);
            restored.Add($"data {b["name"]} ({backup.Updated.Count} row(s){(backup.BindingBackupDir != null ? ", binding content" : "")})");
            if (backup.Inserted.Count > 0) leftovers.Add($"{backup.Inserted.Count} row(s) inserted into {backup.Entity} by {b["name"]}: {string.Join(", ", backup.Inserted)}");
            if (b["isNew"]?.GetValue<bool>() == true) leftovers.Add($"data binding {b["name"]}");
        }
        var entities = new List<Guid>();
        foreach (var b in schemaBackups)
        {
            var md = await File.ReadAllTextAsync(Path.Combine(dir, b["file"]!.ToString()));
            Log.Info($"[{b["package"]}] re-importing previous {b["name"]}...");
            await client.ImportSchemaAsync(Guid.Parse(b["packageUId"]!.ToString()), b["name"]!.ToString(), md);
            if (b["manager"]?.ToString() == "EntitySchemaManager" && Guid.TryParse(b["uId"]?.ToString(), out var uid))
                entities.Add(uid);
            if (b["manager"]?.ToString() == "SourceCodeSchemaManager" && Guid.TryParse(b["uId"]?.ToString(), out var codeUId)
                && await client.EnsureSourceCodeBodyAsync(codeUId, b["name"]!.ToString(), md))
                Log.Info($"  {b["name"]}: import kept the code; restored through the source code designer");
            restored.Add($"schema {b["package"]}/{b["name"]}");
        }
        if (entities.Count > 0 && await client.UpdateDbStructureAsync(entities) is { } msg) Log.Warn($"UpdateDBStructure: {msg}");
        foreach (var b in sqlBackups)
        {
            if (b["isNew"]?.GetValue<bool>() == true) { leftovers.Add($"SQL script {b["name"]}"); continue; }
            Log.Info($"[sql] restoring previous body of {b["name"]} (not executed)...");
            await client.SaveSqlScriptAsync(Guid.Parse(b["uId"]!.ToString()), b["name"]!.ToString(), Guid.Parse(b["packageUId"]!.ToString()),
                b["previousBody"]?.ToString() ?? "", b["installType"]!.GetValue<int>(), b["dbEngineType"]!.GetValue<int>(), [], false);
            restored.Add($"SQL {b["name"]}");
        }
        var errors = new List<string>();
        if (!a.Flag("no-compile") && (schemaBackups.Length > 0 || sqlBackups.Length > 0 || dataBackups.Length > 0))
        {
            Log.Info("Compiling configuration...");
            errors = await client.BuildAsync(false);
        }
        // Files last, as in import (a compilation would rewrite Terrasoft.Configuration\bin).
        if (info["fileBackups"] is JsonArray fileBackups && fileBackups.Count > 0)
        {
            Log.Info($"Restoring {fileBackups.Count} file(s)...");
            restored.AddRange(FileTransfer.Restore(fileBackups, dir));
        }
        foreach (var w in installer.Warnings) Log.Warn(w);
        if (json) PrintJson(new { ok = errors.Count == 0, restored, notRemoved = leftovers, compileErrors = errors });
        else
        {
            Console.WriteLine($"Restored: {string.Join(", ", restored)}");
            Console.WriteLine("Executed SQL scripts are not undone.");
            if (leftovers.Count > 0)
            {
                Console.WriteLine("Created by the import and NOT removed (delete manually if needed):");
                foreach (var l in leftovers) Console.WriteLine("  - " + l);
            }
            foreach (var e in errors) Console.WriteLine("  " + e);
        }
        return errors.Count > 0 ? 5 : 0;
    }

    static void PrintPlan(Manifest m)
    {
        Console.WriteLine($"Source: {m.SourceUrl}, created {m.CreatedUtc:u}");
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
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "verbose", "dry-run", "no-compile", "rebuild", "data-designer" };

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
