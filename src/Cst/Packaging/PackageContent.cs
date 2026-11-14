using System.Text;
using System.Text.Json.Nodes;

namespace Cst.Packaging;

/// <summary>Kinds of package elements the tool transfers.</summary>
public enum ItemKind { Schema, SqlScript, Data }

/// <summary>In-memory package content: relative path (forward slashes) → bytes.</summary>
public sealed class PackageContent
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public void Set(string path, byte[] bytes) => _files[Normalize(path)] = bytes;

    public bool Remove(string path) => _files.Remove(Normalize(path));

    public byte[]? Get(string path) => _files.TryGetValue(Normalize(path), out var b) ? b : null;

    public static string Folder(ItemKind kind) => kind switch
    {
        ItemKind.Schema => "Schemas",
        ItemKind.SqlScript => "SqlScripts",
        ItemKind.Data => "Data",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Names of elements of the given kind (folder names under Schemas/, SqlScripts/ or Data/).</summary>
    public IEnumerable<string> ElementNames(ItemKind kind)
    {
        var prefix = Folder(kind) + "/";
        return _files.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Substring(prefix.Length).Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All files that belong to one element: its own folder plus, for schemas,
    /// the localization folders Resources/&lt;Name&gt;.&lt;Kind&gt;/.
    /// </summary>
    public List<string> ElementFiles(ItemKind kind, string name)
    {
        var own = Folder(kind) + "/" + name + "/";
        var res = "Resources/" + name + ".";
        return _files.Keys.Where(k =>
                k.StartsWith(own, StringComparison.OrdinalIgnoreCase) ||
                (kind == ItemKind.Schema && k.StartsWith(res, StringComparison.OrdinalIgnoreCase)
                    && k.IndexOf('/', res.Length) > 0
                    && !k.Substring(res.Length, k.IndexOf('/', res.Length) - res.Length).Contains('.')))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    public JsonObject? ReadJson(string path)
    {
        var bytes = Get(path);
        if (bytes == null) return null;
        return JsonNode.Parse(DecodeText(bytes)) as JsonObject;
    }

    /// <summary>The element descriptor (Descriptor or SqlScript object inside descriptor.json).</summary>
    public JsonObject? ElementDescriptor(ItemKind kind, string name)
    {
        var root = ReadJson(Folder(kind) + "/" + name + "/descriptor.json");
        if (root == null) return null;
        return (root["Descriptor"] ?? root["SqlScript"]) as JsonObject;
    }

    public JsonObject? PackageDescriptor() => ReadJson("descriptor.json")?["Descriptor"] as JsonObject;

    public static string DecodeText(byte[] bytes)
    {
        // Strip UTF-8 BOM if present.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    public static bool IsTextFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".json" or ".xml" or ".cs" or ".js" or ".sql" or ".less" or ".css" or ".txt" or ".md";
    }
}
