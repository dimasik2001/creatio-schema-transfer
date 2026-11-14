using System.Text.Json.Nodes;
using Cst.Packaging;

namespace Cst.Creatio;

public sealed record PackageInfo(Guid Id, Guid UId, string Name, string Version, string Maintainer, int InstallType)
{
    /// <summary>Custom packages (InstallType 0) can be changed; repository/base packages cannot.</summary>
    public bool IsEditable => InstallType == 0;
}

/// <summary>An element (schema, SQL script or data binding) as registered in the target/source DB.</summary>
public sealed record LocallyModified(Guid Id, string Package, string Name, string Table);

public sealed record ElementInfo(Guid UId, string Name, ItemKind Kind, string? ManagerName, bool ExtendParent, Guid PackageId, string PackageName);

/// <summary>Reads package and element metadata through DataService.</summary>
public sealed class Catalog(CreatioClient client)
{
    private const int ChunkSize = 200;

    public async Task<List<PackageInfo>> LoadPackagesAsync()
    {
        var q = Query("SysPackage", ("Id", "Id"), ("UId", "UId"), ("Name", "Name"), ("Version", "Version"),
            ("Maintainer", "Maintainer"), ("InstallType", "InstallType"));
        return (await client.SelectAsync(q)).Select(r => new PackageInfo(
            G(r, "Id"), G(r, "UId"), S(r, "Name"), S(r, "Version"), S(r, "Maintainer"),
            r["InstallType"]?.GetValue<int>() ?? 0)).ToList();
    }

    /// <summary>Direct dependencies: package Id → Ids of packages it depends on.</summary>
    public async Task<Dictionary<Guid, HashSet<Guid>>> LoadPackageDependenciesAsync()
    {
        var q = Query("SysPackageDependency", ("Pkg", "SysPackage.Id"), ("Dep", "DependOnPackage.Id"));
        var result = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var r in await client.SelectAsync(q))
        {
            var pkg = G(r, "Pkg");
            if (!result.TryGetValue(pkg, out var set)) result[pkg] = set = [];
            set.Add(G(r, "Dep"));
        }
        return result;
    }

    /// <summary>Finds schemas, SQL scripts and data bindings with the given UIds.</summary>
    public async Task<List<ElementInfo>> FindElementsAsync(IEnumerable<Guid> uids)
    {
        var list = uids.Distinct().ToList();
        var result = new List<ElementInfo>();
        for (int i = 0; i < list.Count; i += ChunkSize)
        {
            var chunk = list.Skip(i).Take(ChunkSize).ToList();
            var schemas = Query("SysSchema", ("UId", "UId"), ("Name", "Name"), ("ManagerName", "ManagerName"),
                ("ExtendParent", "ExtendParent"), ("PkgId", "SysPackage.Id"), ("PkgName", "SysPackage.Name"));
            schemas["Filters"] = InFilter("UId", chunk);
            result.AddRange((await client.SelectAsync(schemas)).Select(r => new ElementInfo(
                G(r, "UId"), S(r, "Name"), ItemKind.Schema, S(r, "ManagerName"),
                r["ExtendParent"]?.GetValue<bool>() ?? false, G(r, "PkgId"), S(r, "PkgName"))));

            foreach (var (table, kind) in new[] { ("SysPackageSqlScript", ItemKind.SqlScript), ("SysPackageSchemaData", ItemKind.Data) })
            {
                var q = Query(table, ("UId", "UId"), ("Name", "Name"), ("PkgId", "SysPackage.Id"), ("PkgName", "SysPackage.Name"));
                q["Filters"] = InFilter("UId", chunk);
                result.AddRange((await client.SelectAsync(q)).Select(r => new ElementInfo(
                    G(r, "UId"), S(r, "Name"), kind, null, false, G(r, "PkgId"), S(r, "PkgName"))));
            }
        }
        return result;
    }

    /// <summary>
    /// Elements the zip installer refuses to overwrite: IsLocked and IsChanged ("modified locally").
    /// Every element of the package counts, because the whole package is reinstalled.
    /// </summary>
    public async Task<List<LocallyModified>> FindLocallyModifiedAsync(IEnumerable<Guid> packageIds)
    {
        var ids = packageIds.Distinct().ToList();
        var result = new List<LocallyModified>();
        if (ids.Count == 0) return result;
        foreach (var table in new[] { "SysSchema", "SysPackageSqlScript", "SysPackageSchemaData" })
        {
            var q = Query(table, ("Id", "Id"), ("Name", "Name"), ("PkgName", "SysPackage.Name"));
            var filters = InFilter("SysPackage.Id", ids);
            var items = (JsonObject)filters["Items"]!;
            items["locked"] = BoolFilter("IsLocked");
            items["changed"] = BoolFilter("IsChanged");
            q["Filters"] = filters;
            List<JsonObject> rows;
            try { rows = await client.SelectAsync(q); }
            catch (CreatioException ex) { Log.Debug($"{table}: {ex.Message}"); continue; } // table without these columns
            result.AddRange(rows.Select(r => new LocallyModified(G(r, "Id"), S(r, "PkgName"), S(r, "Name"), table)));
        }
        return result;
    }

    /// <summary>SysSchema row Ids by schema UId (needed by WorkspaceExplorerService/ExportSchema).</summary>
    public async Task<Dictionary<Guid, Guid>> GetSchemaIdsAsync(IEnumerable<Guid> uids)
    {
        var list = uids.Distinct().ToList();
        var result = new Dictionary<Guid, Guid>();
        for (int i = 0; i < list.Count; i += ChunkSize)
        {
            var q = Query("SysSchema", ("Id", "Id"), ("UId", "UId"));
            q["Filters"] = InFilter("UId", list.Skip(i).Take(ChunkSize));
            foreach (var r in await client.SelectAsync(q)) result[G(r, "UId")] = G(r, "Id");
        }
        return result;
    }

    private static JsonObject BoolFilter(string column) => new()
    {
        ["FilterType"] = 1,
        ["ComparisonType"] = 3,
        ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = column },
        ["RightExpression"] = new JsonObject
        {
            ["ExpressionType"] = 2,
            ["Parameter"] = new JsonObject { ["DataValueType"] = 12, ["Value"] = true }
        }
    };

    private static JsonObject Query(string root, params (string Alias, string Path)[] columns)
    {
        var items = new JsonObject();
        foreach (var (alias, path) in columns)
            items[alias] = new JsonObject { ["Expression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = path } };
        return new JsonObject
        {
            ["RootSchemaName"] = root,
            ["OperationType"] = 0,
            ["AllColumns"] = false,
            ["RowCount"] = -1,
            ["Columns"] = new JsonObject { ["Items"] = items }
        };
    }

    private static JsonObject InFilter(string column, IEnumerable<Guid> values)
    {
        var right = new JsonArray(values.Select(v => (JsonNode)new JsonObject
        {
            ["ExpressionType"] = 2,
            ["Parameter"] = new JsonObject { ["DataValueType"] = 0, ["Value"] = v.ToString() }
        }).ToArray());
        return new JsonObject
        {
            ["FilterType"] = 6,
            ["LogicalOperation"] = 0,
            ["Items"] = new JsonObject
            {
                ["f"] = new JsonObject
                {
                    ["FilterType"] = 4,
                    ["ComparisonType"] = 3,
                    ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = column },
                    ["RightExpressions"] = right
                }
            }
        };
    }

    private static Guid G(JsonObject r, string key) => Guid.TryParse(r[key]?.ToString(), out var g) ? g : Guid.Empty;
    private static string S(JsonObject r, string key) => r[key]?.ToString() ?? "";
}
