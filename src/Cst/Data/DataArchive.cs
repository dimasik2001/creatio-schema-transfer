using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cst.Data;

/// <summary>A lookup reference stored in the archive: Column (physical) → RefTable.Id with its policy.</summary>
public sealed record DataEdge(string Column, string RefTable, string Policy, bool Required);

public sealed class DataTableEntry
{
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "full";
    /// <summary>For Sys&lt;T&gt;Right / Sys&lt;T&gt;Lcz: the main table T; their RecordId points at it.</summary>
    public string? CompanionOf { get; set; }
    public long Rows { get; set; }
    /// <summary>Rows chosen by the mode; the rest were pulled by references or taken as children.</summary>
    public long SelectedRows { get; set; }
    public List<GateColumn> Columns { get; set; } = [];
    public List<DataEdge> Edges { get; set; } = [];
    public List<string> Files { get; set; } = [];
}

public sealed class DataManifest
{
    public int Format { get; set; } = 3;
    public string Kind { get; set; } = "db-data";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceUrl { get; set; } = "";
    public string SourceEngine { get; set; } = "";
    public string? SourceDatabase { get; set; }
    public string GateVersion { get; set; } = "";
    public List<DataTableEntry> Tables { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// The table data archive (docs/db-transfer.md, 6): manifest.json, plan.txt and data/&lt;Table&gt;/NNNNNN.ndjson — one JSON
/// array per row in the column order of the manifest (types: guid, string, int, long, decimal as string, double, bool,
/// datetime ISO, binary base64). Engine-neutral: the target builds its own SQL.
/// </summary>
public static class DataArchive
{
    public const int RowsPerFile = 50000;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(string path, DataManifest manifest, string planText, IReadOnlyDictionary<string, List<JsonArray>> rows)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var t in manifest.Tables)
        {
            t.Files.Clear();
            var list = rows.TryGetValue(t.Name, out var r) ? r : [];
            t.Rows = list.Count;
            for (int i = 0, n = 1; i < list.Count; i += RowsPerFile, n++)
            {
                var name = $"data/{t.Name}/{n:D6}.ndjson";
                t.Files.Add(name);
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                using var w = new StreamWriter(s, new UTF8Encoding(false));
                foreach (var row in list.Skip(i).Take(RowsPerFile)) w.WriteLine(row.ToJsonString());
            }
        }
        using (var w = new StreamWriter(zip.CreateEntry("plan.txt").Open(), new UTF8Encoding(false))) w.Write(planText);
        using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            w.Write(JsonSerializer.Serialize(manifest, Json));
    }

    public static DataManifest ReadManifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("manifest.json") ?? throw new FormatException($"{path}: no manifest.json.");
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        var m = JsonSerializer.Deserialize<DataManifest>(r.ReadToEnd(), Json) ?? throw new FormatException($"{path}: bad manifest.");
        if (m.Kind != "db-data") throw new FormatException($"{path} is not a table data archive (kind '{m.Kind}'). Use import/inspect for schema archives.");
        if (m.Format > 3) throw new FormatException($"{path}: archive format {m.Format} is newer than this cst understands (3).");
        return m;
    }

    public static string ReadPlan(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("plan.txt");
        if (entry == null) return "";
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static List<JsonArray> ReadRows(string path, DataTableEntry table)
    {
        using var zip = ZipFile.OpenRead(path);
        var rows = new List<JsonArray>();
        foreach (var file in table.Files)
        {
            var entry = zip.GetEntry(file) ?? throw new FormatException($"{path}: {file} is missing.");
            using var r = new StreamReader(entry.Open(), Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
                if (line.Length > 0) rows.Add((JsonArray)JsonNode.Parse(line)!);
        }
        return rows;
    }
}
