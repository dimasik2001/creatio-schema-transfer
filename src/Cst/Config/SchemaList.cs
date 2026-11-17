using System.Text.RegularExpressions;
using Cst.Packaging;

namespace Cst.Config;

public sealed class SchemaListEntry
{
    public required string Name { get; init; }
    /// <summary>Optional type hint: [schema], [sql] or [data].</summary>
    public ItemKind? Kind { get; init; }
}

public sealed class SchemaListPackage
{
    public required string Source { get; init; }
    public required string Target { get; set; }
    public List<SchemaListEntry> Entries { get; } = [];
}

/// <summary>
/// Parses the schema list:
/// <code>
/// # comment
/// package LVBase -> UsrBase:
///  - SchemaName
///  - SomeBinding [data]
/// files:
///  - Terrasoft.WebApp\Terrasoft.Configuration\bin\Terrasoft.Configuration.dll
/// </code>
/// The files section needs the environment's root folder (Path).
/// Also accepts the command-line form "LVBase->UsrBase:A,B;LVJS:C".
/// </summary>
public static partial class SchemaList
{
    [GeneratedRegex(@"^package\s+(?<src>[\w.]+)\s*(->\s*(?<dst>[\w.]+))?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageLine();

    [GeneratedRegex(@"^-\s*(?<name>[\w.]+)\s*(\[(?<kind>\w+)\])?\s*$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"^files\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesLine();

    [GeneratedRegex(@"^-\s*(?<path>.+?)\s*$")]
    private static partial Regex FileEntryLine();

    public static List<SchemaListPackage> ParseFile(string path) => Parse(File.ReadAllLines(path), path);

    /// <summary>Entries of the `files:` section (paths relative to the Creatio root folder).</summary>
    public static List<string> ParseFilesSection(string path)
    {
        var files = new List<string>();
        ParseCore(File.ReadAllLines(path), path, files);
        return files;
    }

    public static List<SchemaListPackage> Parse(IEnumerable<string> lines, string source = "<input>") => ParseCore(lines, source, null);

    private static List<SchemaListPackage> ParseCore(IEnumerable<string> lines, string source, List<string>? files)
    {
        var result = new List<SchemaListPackage>();
        SchemaListPackage? current = null;
        bool inFiles = false;
        int n = 0;
        foreach (var raw in lines)
        {
            n++;
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (FilesLine().IsMatch(line)) { inFiles = true; current = null; continue; }
            var pm = PackageLine().Match(line);
            if (pm.Success) inFiles = false;
            if (inFiles)
            {
                var fm = FileEntryLine().Match(line);
                if (!fm.Success) throw new FormatException($"{source}:{n}: expected ' - <path>' in the files section, got '{raw.Trim()}'.");
                var p = fm.Groups["path"].Value;
                if (files != null && !files.Contains(p, StringComparer.OrdinalIgnoreCase)) files.Add(p);
                continue;
            }
            if (pm.Success)
            {
                var src = pm.Groups["src"].Value;
                var dst = pm.Groups["dst"].Success ? pm.Groups["dst"].Value : src;
                current = result.FirstOrDefault(p => p.Source.Equals(src, StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    current = new SchemaListPackage { Source = src, Target = dst };
                    result.Add(current);
                }
                else if (pm.Groups["dst"].Success && !current.Target.Equals(dst, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"{source}:{n}: package {src} is mapped to both {current.Target} and {dst}.");
                continue;
            }
            var em = EntryLine().Match(line);
            if (em.Success)
            {
                if (current == null) throw new FormatException($"{source}:{n}: schema listed before any 'package' line.");
                AddEntry(current, em.Groups["name"].Value, em.Groups["kind"].Success ? em.Groups["kind"].Value : null, $"{source}:{n}");
                continue;
            }
            throw new FormatException($"{source}:{n}: cannot parse line '{raw.Trim()}'.");
        }
        return result;
    }

    /// <summary>Parses "LVBase->UsrBase:A,B;LVJS:C[sql]".</summary>
    public static List<SchemaListPackage> ParseInline(string spec)
    {
        var lines = new List<string>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon < 0) throw new FormatException($"--schemas: '{part}' must look like Package:Schema1,Schema2.");
            lines.Add("package " + part[..colon].Trim() + ":");
            lines.AddRange(part[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => "- " + s));
        }
        return Parse(lines, "--schemas");
    }

    public static void Merge(List<SchemaListPackage> into, List<SchemaListPackage> from)
    {
        foreach (var p in from)
        {
            var existing = into.FirstOrDefault(x => x.Source.Equals(p.Source, StringComparison.OrdinalIgnoreCase));
            if (existing == null) { into.Add(p); continue; }
            foreach (var e in p.Entries) AddEntry(existing, e.Name, e.Kind?.ToString(), "--schemas");
        }
    }

    private static void AddEntry(SchemaListPackage pkg, string name, string? kind, string where)
    {
        ItemKind? k = kind?.ToLowerInvariant() switch
        {
            null => null,
            "schema" => ItemKind.Schema,
            "sql" or "sqlscript" => ItemKind.SqlScript,
            "data" => ItemKind.Data,
            _ => throw new FormatException($"{where}: unknown type [{kind}], use [schema], [sql] or [data].")
        };
        if (pkg.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.Kind == k)) return; // duplicate
        pkg.Entries.Add(new SchemaListEntry { Name = name, Kind = k });
    }

    private static string StripComment(string line)
    {
        var i = line.IndexOf('#');
        return i >= 0 ? line[..i] : line;
    }
}
