using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Creatio;

namespace Cst.Transfer;

public sealed class RestoreResult
{
    public List<string> Restored { get; } = [];
    /// <summary>Things the import created that restore does not remove (new elements, inserted rows, new packages).</summary>
    public List<string> NotRemoved { get; } = [];
    public List<string> CompileErrors { get; set; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Rolls a stand back from an import's work dir (restore.json + backup/): data, schemas, SQL bodies, whole packages,
/// then compile, then files — the reverse of the import. Executed SQL is not undone; created things are listed.
/// </summary>
public sealed class Restorer(CreatioClient client)
{
    public static string InfoPath(string workDir) => Path.Combine(workDir, "restore.json");

    public async Task<RestoreResult> RestoreAsync(string dir, bool noCompile)
    {
        var infoPath = InfoPath(dir);
        if (!File.Exists(infoPath)) throw new ArgumentException($"{infoPath} not found.");
        var info = JsonNode.Parse(File.ReadAllText(infoPath))!;
        JsonObject[] Arr(string key) => info[key]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var schemaBackups = Arr("schemaBackups");
        var sqlBackups = Arr("sqlBackups");
        var dataBackups = Arr("dataBackups");
        var wholeBackups = Arr("wholeBackups");
        var r = new RestoreResult();
        r.NotRemoved.AddRange(info["createdSchemas"]?.AsArray().Select(n => "schema " + n) ?? []);
        var installer = new ElementInstaller(client, client.DbEngineType);

        if (dataBackups.Length > 0) Log.Stage("Data bindings");
        foreach (var b in Enumerable.Reverse(dataBackups))
        {
            var backup = b["backup"].Deserialize<ElementInstaller.DataBackup>(Manifest.Json)!;
            Log.Info($"[data] restoring previous values of {b["name"]}...");
            await installer.RestoreDataAsync(backup, dir);
            r.Restored.Add($"data {b["name"]} ({backup.Updated.Count} row(s){(backup.BindingBackupDir != null ? ", binding content" : "")})");
            if (backup.Inserted.Count > 0) r.NotRemoved.Add($"{backup.Inserted.Count} row(s) inserted into {backup.Entity} by {b["name"]}: {string.Join(", ", backup.Inserted)}");
            if (b["isNew"]?.GetValue<bool>() == true) r.NotRemoved.Add($"data binding {b["name"]}");
        }
        var entities = new List<Guid>();
        if (schemaBackups.Length > 0) Log.Stage("Schemas");
        foreach (var b in schemaBackups)
        {
            var md = await Io.ReadAllTextAsync(Path.Combine(dir, b["file"]!.ToString()));
            Log.Info($"[{b["package"]}] re-importing previous {b["name"]}...");
            await client.ImportSchemaAsync(Guid.Parse(b["packageUId"]!.ToString()), b["name"]!.ToString(), md);
            if (b["manager"]?.ToString() == "EntitySchemaManager" && Guid.TryParse(b["uId"]?.ToString(), out var uid))
                entities.Add(uid);
            if (b["manager"]?.ToString() == "SourceCodeSchemaManager" && Guid.TryParse(b["uId"]?.ToString(), out var codeUId)
                && await client.EnsureSourceCodeBodyAsync(codeUId, b["name"]!.ToString(), md))
                Log.Info($"  {b["name"]}: import kept the code; restored through the source code designer");
            r.Restored.Add($"schema {b["package"]}/{b["name"]}");
        }
        if (entities.Count > 0 && await client.UpdateDbStructureAsync(entities) is { } msg) r.Warnings.Add($"UpdateDBStructure: {msg}");
        if (sqlBackups.Length > 0) Log.Stage("SQL scripts");
        foreach (var b in sqlBackups)
        {
            if (b["isNew"]?.GetValue<bool>() == true) { r.NotRemoved.Add($"SQL script {b["name"]}"); continue; }
            Log.Info($"[sql] restoring previous body of {b["name"]} (not executed)...");
            await client.SaveSqlScriptAsync(Guid.Parse(b["uId"]!.ToString()), b["name"]!.ToString(), Guid.Parse(b["packageUId"]!.ToString()),
                b["previousBody"]?.ToString() ?? "", b["installType"]!.GetValue<int>(), b["dbEngineType"]!.GetValue<int>(), [], false);
            r.Restored.Add($"SQL {b["name"]}");
        }
        // Whole packages last (they were installed first): reinstall the backed-up package zip.
        if (wholeBackups.Length > 0) Log.Stage("Whole packages");
        foreach (var b in Enumerable.Reverse(wholeBackups))
        {
            var name = b["name"]!.ToString();
            if (b["existed"]?.GetValue<bool>() != true || b["file"] is null) { r.NotRemoved.Add($"package {name} (created by the zip install)"); continue; }
            Log.Info($"[{name}] reinstalling the backed-up package (zip installer)...");
            var zipName = $"cst_restore_{DateTime.Now:yyyyMMdd_HHmmss}_{name}.zip";
            await client.UploadPackageAsync(zipName, File.ReadAllBytes(Path.Combine(dir, b["file"]!.ToString())));
            await client.InstallPackageAsync(zipName);
            r.Restored.Add($"package {name}");
        }
        if (!noCompile && (schemaBackups.Length > 0 || sqlBackups.Length > 0 || dataBackups.Length > 0 || wholeBackups.Length > 0))
        {
            Log.Stage("Compile");
            Log.Info("Compiling configuration...");
            r.CompileErrors = await client.BuildAsync(false);
        }
        // Files last, as in import (a compilation would rewrite Terrasoft.Configuration\bin).
        if (info["fileBackups"] is JsonArray fileBackups && fileBackups.Count > 0)
        {
            Log.Stage("Files");
            Log.Info($"Restoring {fileBackups.Count} file(s)...");
            r.Restored.AddRange(FileTransfer.Restore(fileBackups, dir));
        }
        r.Warnings.AddRange(installer.Warnings);
        return r;
    }
}
