using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cst.Packaging;

namespace Cst.Transfer;

public sealed class PackageRef
{
    public Guid UId { get; set; }
    public string Name { get; set; } = "";
    public string? PackageVersion { get; set; }
}

public sealed class ManifestPackage
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public Guid SourceUId { get; set; }
    public string? Maintainer { get; set; }
    /// <summary>Install order of the target package (0 first).</summary>
    public int Order { get; set; }
    /// <summary>Dependencies of the source package, used when the target package has to be created.</summary>
    public List<PackageRef> SourceDependsOn { get; set; } = [];
}

public sealed class ManifestItem
{
    public ItemKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Guid UId { get; set; }
    public string? Manager { get; set; }
    public string SourcePackage { get; set; } = "";
    public string TargetPackage { get; set; } = "";
    /// <summary>Install order; items in one dependency cycle share the same order.</summary>
    public int Order { get; set; }
    /// <summary>UIds of other archive items this item references.</summary>
    public List<Guid> DependsOn { get; set; } = [];
    /// <summary>UIds of elements outside the archive this item references.</summary>
    public List<Guid> External { get; set; } = [];
    /// <summary>Paths relative to the package root.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>
    /// Schemas only: archive path of the Configuration-section export (.md). Used when the target package has
    /// locally modified elements, which block the zip installer.
    /// </summary>
    public string? SchemaExport { get; set; }
}

public sealed class ExternalDependency
{
    public Guid UId { get; set; }
    public string Name { get; set; } = "";
    public ItemKind Kind { get; set; }
    public string? Manager { get; set; }
    public string SourcePackage { get; set; } = "";
    public List<string> RequiredBy { get; set; } = [];
}

public sealed class Manifest
{
    public int FormatVersion { get; set; } = 1;
    public string Tool { get; set; } = "cst";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SourceUrl { get; set; } = "";
    public List<ManifestPackage> Packages { get; set; } = [];
    public List<ManifestItem> Items { get; set; } = [];
    public List<ExternalDependency> External { get; set; } = [];
    /// <summary>Plain files of the Creatio folder (files: section), stored under files/ in the archive.</summary>
    public List<ManifestFile> Files { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Transfer archive: manifest.json + packages/&lt;SourcePackage&gt;/&lt;path&gt; for every transferred file
/// and each source package descriptor.json.
/// </summary>
public static class TransferArchive
{
    public const string ManifestName = "manifest.json";

    public static void Write(string path, Manifest manifest, IReadOnlyDictionary<string, PackageContent> sources,
        IReadOnlyDictionary<string, string> exports, IReadOnlyDictionary<string, byte[]>? plainFiles = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var m = zip.CreateEntry(ManifestName);
        using (var s = m.Open()) JsonSerializer.Serialize(s, manifest, Manifest.Json);

        foreach (var pkg in manifest.Packages)
        {
            var content = sources[pkg.Source];
            var files = manifest.Items.Where(i => i.SourcePackage == pkg.Source).SelectMany(i => i.Files).Append("descriptor.json");
            foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bytes = content.Get(f) ?? throw new InvalidOperationException($"{pkg.Source}: file {f} is missing.");
                var e = zip.CreateEntry($"packages/{pkg.Source}/{f}");
                using var s = e.Open();
                s.Write(bytes, 0, bytes.Length);
            }
        }
        foreach (var (entryPath, text) in exports)
        {
            var e = zip.CreateEntry(entryPath);
            using var s = e.Open();
            { var __b = System.Text.Encoding.UTF8.GetBytes(text); s.Write(__b, 0, __b.Length); }
        }
        foreach (var (entryPath, bytes) in plainFiles ?? new Dictionary<string, byte[]>())
        {
            var e = zip.CreateEntry(entryPath);
            using var s = e.Open();
            s.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Reads a binary entry (such as a transferred file) from the archive.</summary>
    public static byte[] ReadBytes(string archivePath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var e = zip.GetEntry(entryPath) ?? throw new InvalidDataException($"Archive is missing {entryPath}.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads a text entry (such as a schema export) from the archive.</summary>
    public static string? ReadText(string archivePath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var e = zip.GetEntry(entryPath);
        if (e == null) return null;
        using var r = new StreamReader(e.Open(), System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static (Manifest Manifest, Dictionary<string, PackageContent> Sources) Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var me = zip.GetEntry(ManifestName) ?? throw new InvalidDataException($"{path}: no {ManifestName}.");
        Manifest manifest;
        using (var s = me.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(s, Manifest.Json) ?? throw new InvalidDataException("Empty manifest.");
        if (manifest.FormatVersion != 1)
            throw new InvalidDataException($"Unsupported archive format version {manifest.FormatVersion}.");

        var sources = new Dictionary<string, PackageContent>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("packages/", StringComparison.Ordinal) || e.FullName.EndsWith("/")) continue;
            var rest = e.FullName["packages/".Length..];
            var slash = rest.IndexOf('/');
            var pkg = rest[..slash];
            if (!sources.TryGetValue(pkg, out var content)) sources[pkg] = content = new PackageContent();
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            content.Set(rest[(slash + 1)..], ms.ToArray());
        }
        return (manifest, sources);
    }
}
