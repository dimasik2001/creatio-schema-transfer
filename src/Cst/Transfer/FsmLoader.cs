using System.Text.Json.Nodes;
using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Transfer;

/// <summary>What loading the export's packages from the file system into the DB would do (checked before loading).</summary>
public sealed class FsmLoadPlan
{
    /// <summary>FSM state of the source stand; null when it does not answer GetIsFileDesignMode.</summary>
    public bool? IsFsm { get; init; }
    /// <summary>Packages to load: the export's packages that have a folder in Pkg (all of them when the disk is not checked).</summary>
    public List<string> Packages { get; } = [];
    /// <summary>Packages of the export without a folder in Pkg: LoadPackagesToDB skips them.</summary>
    public List<string> NotOnDisk { get; } = [];
    /// <summary>Elements in the DB of a loaded package without their folder on disk: LoadPackagesToDB deletes them from the DB.</summary>
    public List<string> WillDelete { get; } = [];
    /// <summary>Pkg folder that was compared; null when the stand has no "Path" (nothing could be checked).</summary>
    public string? PkgPath { get; init; }
    public bool DiskChecked => PkgPath != null;
}

public sealed class FsmLoadResult
{
    /// <summary>"Package: Name (Type, State)" for every element the load wrote or deleted.</summary>
    public List<string> Changes { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Optional step before an export from an FSM stand: Creatio's own "load packages from the file system into the DB"
/// (AppInstallerService/LoadPackagesToDB, as clio pkg-to-db) for the packages of the export, so the whole-package
/// zip, SQL scripts, data bindings and translations come from the files. Element exports (.md) already take the code
/// of a schema from disk; metadata and everything else come from the DB.
/// What the load does (decompiled 10.0.0.801): reads the whole Pkg folder and the whole configuration, then for the
/// named packages writes every element whose files differ (schemas by MD5 of the folder, SQL/data by ModifiedOnUtc),
/// overwrites local changes and DELETES elements of those packages that have no folder on disk. With
/// AutoUpdateDBStructure / AutoInstallSqlScript / AutoInstallPackageData on in Web.config it also updates the DB
/// structure, runs changed SQL scripts and installs changed data bindings.
/// </summary>
public static class FsmLoader
{
    /// <summary>Source packages of an export: element sections and whole packages, in list order.</summary>
    public static List<string> PackagesOf(IEnumerable<SchemaListPackage> elements, IEnumerable<string> whole) =>
        elements.Select(p => p.Source).Concat(whole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The Pkg folder under a Creatio root (.NET Framework: Terrasoft.WebApp\Terrasoft.Configuration\Pkg; .NET: Terrasoft.Configuration\Pkg).</summary>
    public static string? FindPkgPath(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        foreach (var p in new[] { Path.Combine(root, "Terrasoft.WebApp", "Terrasoft.Configuration", "Pkg"), Path.Combine(root, "Terrasoft.Configuration", "Pkg") })
            if (Directory.Exists(p)) return p;
        return null;
    }

    private static string FolderOf(ItemKind kind) => kind switch
    {
        ItemKind.SqlScript => "SqlScripts",
        ItemKind.Data => "Data",
        _ => "Schemas"
    };

    /// <summary>Checks the stand and, when its Creatio folder is readable, what the load would skip or delete.</summary>
    public static async Task<FsmLoadPlan> PlanAsync(CreatioClient client, IReadOnlyList<string> packages)
    {
        var isFsm = await client.GetIsFileDesignModeAsync();
        var pkgPath = FindPkgPath(client.RootPath);
        var plan = new FsmLoadPlan { IsFsm = isFsm, PkgPath = pkgPath };
        if (pkgPath == null)
        {
            plan.Packages.AddRange(packages);
            return plan;
        }
        var catalog = new Catalog(client);
        var byName = (await catalog.LoadPackagesAsync()).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in packages)
        {
            var dir = Path.Combine(pkgPath, name);
            if (!Directory.Exists(dir)) { plan.NotOnDisk.Add(name); continue; }
            plan.Packages.Add(name);
            if (!byName.TryGetValue(name, out var pkg)) continue; // new package: nothing in the DB to delete
            foreach (var e in await catalog.LoadPackageElementsAsync(pkg.Id))
                if (!Directory.Exists(Path.Combine(dir, FolderOf(e.Kind), e.Name)))
                    plan.WillDelete.Add($"{name}: {e.Name} ({ElementFolders.Title(e.Folder)})");
        }
        return plan;
    }

    /// <summary>Loads the packages in one call (each call reads the whole Pkg folder and configuration).</summary>
    public static async Task<FsmLoadResult> LoadAsync(CreatioClient client, IReadOnlyList<string> packages)
    {
        var result = new FsmLoadResult();
        if (packages.Count == 0) return result;
        Log.Info($"Loading {packages.Count} package(s) from the file system into the DB: {string.Join(", ", packages)}...");
        var json = await client.LoadPackagesToDbAsync(packages);
        foreach (var p in json?["changes"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var pkg = p["name"]?.ToString();
            foreach (var i in p["items"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var culture = i["cultureName"]?.ToString();
                result.Changes.Add($"{pkg}: {i["name"]}{(string.IsNullOrEmpty(culture) ? "" : $" [{culture}]")} ({i["typeName"]}, {i["stateName"]})");
            }
        }
        foreach (var e in json?["errors"]?.AsArray().OfType<JsonObject>() ?? [])
            result.Errors.Add($"{e["workspaceItem"]?["name"]} ({e["workspaceItem"]?["typeName"]}): {e["errorInfo"]?["message"]}");
        foreach (var c in result.Changes) Log.Debug("  loaded " + c);
        Log.Info($"Loaded into the DB: {result.Changes.Count} change(s), {result.Errors.Count} error(s).");
        return result;
    }
}
