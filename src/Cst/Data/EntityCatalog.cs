using System.Text.Json.Nodes;
using Cst.Creatio;

namespace Cst.Data;

/// <summary>How a column's values can be read through the services.</summary>
public enum ColumnAccess
{
    /// <summary>Read with DataService SelectQuery.</summary>
    Select,
    /// <summary>Binary: SelectQuery returns it mangled; read per row from img/entity/hash/{Schema}/{Column}/{Id}.</summary>
    Binary,
    /// <summary>Secure/hash text: the services never return the stored value; not transferred.</summary>
    Unreadable
}

public sealed record ColumnInfo(string Name, int DataValueType, string? ReferenceSchema, bool IsRequired, bool IsWeakReference, bool IsCascade)
{
    public bool IsLookup => !string.IsNullOrEmpty(ReferenceSchema);

    /// <summary>
    /// Creatio DataValueType: 13 Blob, 14 Image, 26 File are binary; 24 HashText, 25 SecureText are never returned.
    /// </summary>
    public ColumnAccess Access => DataValueType switch
    {
        13 or 14 or 26 => ColumnAccess.Binary,
        24 or 25 => ColumnAccess.Unreadable,
        _ => ColumnAccess.Select
    };

    /// <summary>Text-like or binary: its size is measured with LENGTH; other types have a fixed size.</summary>
    public bool HasVariableSize => !IsLookup && DataValueType is 1 or 13 or 14 or 26 or 28 or 29 or 30 or 31 or 37 or 42 or 43 or 44 or 45;

    /// <summary>Storage bytes of a fixed-size value (approximate).</summary>
    public int FixedBytes => IsLookup ? 16 : DataValueType switch
    {
        0 => 16,                 // Guid
        12 => 1,                 // Boolean
        4 => 4,                  // Integer
        7 or 8 or 9 => 8,        // DateTime, Date, Time
        _ => 9                   // floats/money (decimal)
    };
}

/// <summary>An entity schema as the runtime sees it (all replacements applied).</summary>
public sealed record EntityInfo(string Name, Guid UId, bool IsDBView, bool IsVirtual, bool AdministratedByRecords, List<ColumnInfo> Columns);

/// <summary>An entity found in the selected packages.</summary>
public sealed record PackageEntity(string Name, string Package, bool OwnRoot)
{
    /// <summary>Packages (among the selected) with a replacement of this entity.</summary>
    public List<string> ExtendedIn { get; } = [];
}

/// <summary>Entity schemas of packages and their runtime metadata, read through DataService.</summary>
public sealed class EntityCatalog(CreatioClient client)
{
    private readonly Dictionary<string, EntityInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Entity schemas registered in the packages. OwnRoot = the entity is created in one of them (a schema that is
    /// not a replacement); otherwise the packages only replace an entity of another package.
    /// </summary>
    public async Task<List<PackageEntity>> FindEntitiesAsync(IEnumerable<PackageInfo> packages)
    {
        var found = new List<(string Name, string Package, bool Own)>();
        foreach (var p in packages)
        {
            var q = new JsonObject
            {
                ["RootSchemaName"] = "SysSchema",
                ["OperationType"] = 0,
                ["AllColumns"] = false,
                ["RowCount"] = -1,
                ["Columns"] = new JsonObject
                {
                    ["Items"] = new JsonObject
                    {
                        ["Name"] = Col("Name"),
                        ["ExtendParent"] = Col("ExtendParent")
                    }
                },
                ["Filters"] = new JsonObject
                {
                    ["FilterType"] = 6,
                    ["LogicalOperation"] = 0,
                    ["Items"] = new JsonObject
                    {
                        ["pkg"] = Eq("SysPackage.Id", 0, p.Id.ToString()),
                        ["mgr"] = Eq("ManagerName", 1, "EntitySchemaManager")
                    }
                }
            };
            foreach (var r in await client.SelectAsync(q))
                found.Add((r["Name"]?.ToString() ?? "", p.Name, r["ExtendParent"]?.GetValue<bool>() != true));
        }
        var result = new List<PackageEntity>();
        foreach (var g in found.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var root = g.FirstOrDefault(f => f.Own);
            var e = new PackageEntity(g.Key, root.Own ? root.Package : g.First().Package, root.Own);
            e.ExtendedIn.AddRange(g.Where(f => !f.Own).Select(f => f.Package).Distinct());
            result.Add(e);
        }
        return result.OrderBy(e => e.OwnRoot ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Runtime entity schema (DataService RuntimeEntitySchemaRequest); null when there is no such entity.</summary>
    public async Task<EntityInfo?> GetEntityAsync(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        JsonNode? json;
        try { json = await client.PostJsonAsync("DataService/json/SyncReply/RuntimeEntitySchemaRequest", new JsonObject { ["Name"] = name }); }
        catch (CreatioException ex) { Log.Debug($"RuntimeEntitySchemaRequest {name}: {ex.Message}"); json = null; }
        EntityInfo? info = null;
        if (json?["schema"] is JsonObject s && json["success"]?.GetValue<bool>() != false)
        {
            var columns = new List<ColumnInfo>();
            foreach (var kv in s["columns"]?["Items"] as JsonObject ?? new JsonObject())
            {
                if (kv.Value is not JsonObject c) continue;
                if (c["isVirtual"]?.GetValue<bool>() == true) continue;
                var reference = c["referenceSchemaName"]?.ToString();
                columns.Add(new ColumnInfo(
                    c["name"]?.ToString() ?? "",
                    c["dataValueType"]?.GetValue<int>() ?? 1,
                    string.IsNullOrEmpty(reference) ? null : reference,
                    c["isRequired"]?.GetValue<bool>() == true,
                    c["isWeakReference"]?.GetValue<bool>() == true,
                    c["isCascade"]?.GetValue<bool>() == true));
            }
            info = new EntityInfo(
                s["name"]?.ToString() ?? name,
                Guid.TryParse(s["uId"]?.ToString(), out var uid) ? uid : Guid.Empty,
                s["isDBView"]?.GetValue<bool>() == true,
                s["isVirtual"]?.GetValue<bool>() == true,
                s["administratedByRecords"]?.GetValue<bool>() == true,
                columns);
        }
        _cache[name] = info;
        return info;
    }

    /// <summary>Row count and approximate stored size: SUM(LENGTH) of variable columns plus fixed sizes, one query.</summary>
    public async Task<(long Rows, long? Bytes)> MeasureAsync(EntityInfo entity, string dbEngine)
    {
        var items = new JsonObject
        {
            ["cnt"] = Aggregate(1, new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = "Id" })
        };
        var variable = entity.Columns.Where(c => c.HasVariableSize && c.Access != ColumnAccess.Unreadable).ToList();
        for (int i = 0; i < variable.Count; i++)
            items["l" + i] = Aggregate(2, new JsonObject
            {
                ["ExpressionType"] = 1,
                ["FunctionType"] = 4, // LENGTH
                ["FunctionArgument"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = variable[i].Name }
            });
        var q = new JsonObject
        {
            ["RootSchemaName"] = entity.Name,
            ["OperationType"] = 0,
            ["AllColumns"] = false,
            ["Columns"] = new JsonObject { ["Items"] = items }
        };
        try
        {
            var row = (await client.SelectAsync(q)).FirstOrDefault();
            long rows = Num(row?["cnt"]);
            // MSSQL LEN counts characters of nvarchar (2 bytes each); PostgreSQL text is UTF-8 (~1 byte for Latin).
            int charBytes = string.Equals(dbEngine, "PostgreSql", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            long bytes = rows * entity.Columns.Where(c => !c.HasVariableSize).Sum(c => (long)c.FixedBytes);
            for (int i = 0; i < variable.Count; i++)
                bytes += Num(row?["l" + i]) * (variable[i].Access == ColumnAccess.Binary ? 1 : charBytes);
            return (rows, bytes);
        }
        catch (CreatioException ex)
        {
            Log.Debug($"Size of {entity.Name}: {ex.Message}");
            var row = (await client.SelectAsync(new JsonObject
            {
                ["RootSchemaName"] = entity.Name,
                ["OperationType"] = 0,
                ["AllColumns"] = false,
                ["Columns"] = new JsonObject { ["Items"] = new JsonObject { ["cnt"] = Aggregate(1, new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = "Id" }) } }
            })).FirstOrDefault();
            return (Num(row?["cnt"]), null);
        }
    }

    private static long Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : n is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : 0;

    private static JsonObject Aggregate(int aggregationType, JsonObject argument) => new()
    {
        ["Expression"] = new JsonObject
        {
            ["ExpressionType"] = 1,
            ["FunctionType"] = 2, // aggregation
            ["AggregationType"] = aggregationType, // 1 Count, 2 Sum
            ["FunctionArgument"] = argument
        }
    };

    private static JsonObject Col(string path) => new() { ["Expression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = path } };

    private static JsonObject Eq(string column, int dataValueType, string value) => new()
    {
        ["FilterType"] = 1,
        ["ComparisonType"] = 3,
        ["LeftExpression"] = new JsonObject { ["ExpressionType"] = 0, ["ColumnPath"] = column },
        ["RightExpression"] = new JsonObject
        {
            ["ExpressionType"] = 2,
            ["Parameter"] = new JsonObject { ["DataValueType"] = dataValueType, ["Value"] = value }
        }
    };
}
