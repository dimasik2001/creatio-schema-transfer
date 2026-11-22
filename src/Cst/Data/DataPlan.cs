using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cst.Data;

public enum TableModeKind { Full, Skip, Top, Where, Ids, Children }

/// <summary>How rows of one table are chosen (docs/db-transfer.md, 4).</summary>
public sealed record TableMode(TableModeKind Kind, long Top = 0, string? OrderColumn = null, bool Descending = true, string? Where = null, List<Guid>? Ids = null)
{
    public static TableMode Full { get; } = new(TableModeKind.Full);

    public override string ToString() => Kind switch
    {
        TableModeKind.Full => "full",
        TableModeKind.Skip => "skip",
        TableModeKind.Children => "children",
        TableModeKind.Top => $"top {Top} by {OrderColumn} {(Descending ? "desc" : "asc")}",
        TableModeKind.Where => "where " + Where,
        TableModeKind.Ids => "ids " + string.Join(",", Ids ?? []),
        _ => Kind.ToString()
    };

    private static readonly Regex TopRe = new(@"^top\s+(\d+)(?:\s+by\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+(asc|desc))?)?$", RegexOptions.IgnoreCase);

    public static TableMode Parse(string text, DataSettings settings)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Equals("full", StringComparison.OrdinalIgnoreCase)) return Full;
        if (t.Equals("skip", StringComparison.OrdinalIgnoreCase)) return new(TableModeKind.Skip);
        if (t.Equals("children", StringComparison.OrdinalIgnoreCase)) return new(TableModeKind.Children);
        if (t.StartsWith("where ", StringComparison.OrdinalIgnoreCase)) return new(TableModeKind.Where, Where: t[6..].Trim());
        if (t.StartsWith("ids ", StringComparison.OrdinalIgnoreCase))
        {
            var ids = t[4..].Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : throw new FormatException($"Not a Guid in 'ids': {s}")).ToList();
            return new(TableModeKind.Ids, Ids: ids);
        }
        var m = TopRe.Match(t);
        if (m.Success)
        {
            var order = settings.DefaultTopOrder.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var column = m.Groups[2].Success ? m.Groups[2].Value : order[0];
            var dir = m.Groups[3].Success ? m.Groups[3].Value : (order.Length > 1 ? order[1] : "desc");
            return new(TableModeKind.Top, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), column, dir.Equals("desc", StringComparison.OrdinalIgnoreCase));
        }
        throw new FormatException($"Unknown table mode '{text}' (full | skip | children | top N [by Column asc|desc] | where <SQL> | ids <Guid,...>).");
    }
}

/// <summary>Reference policies (docs/db-transfer.md, 5.2).</summary>
public static class RefPolicy
{
    public const string Pull = "pull", Expect = "expect", Author = "author", Null = "null", Drop = "drop";
    public static readonly string[] All = [Pull, Expect, Author, Null, Drop];
}

/// <summary>
/// The selection of a table data export, saved as text:
/// <code>
/// db packages:
///  - UtkCore
/// tables:
///  - UtkContract       full
///  - UtkPayment        top 500 by CreatedOn desc
///  - UtkLog            where CreatedOn > '2026-11-01'
/// children:
///  - UtkPayment.ContractId
/// refs:
///  - UtkPayment.ContactId   expect
///  - *.CreatedById          author
/// options:
///  - rights: yes
///  - translations: yes
/// </code>
/// With no tables: section, the tables are the entities created in the packages (large ones get the default top).
/// </summary>
public sealed class DataPlan
{
    public List<string> Packages { get; } = [];
    /// <summary>Table → mode, in the order of the file.</summary>
    public List<(string Table, TableMode Mode)> Tables { get; } = [];
    /// <summary>"Child.Column" entries: rows of Child whose Column points at exported rows of the referenced table.</summary>
    public List<(string Table, string Column)> Children { get; } = [];
    /// <summary>"Table.Column" (Table may be *) → policy.</summary>
    public List<(string Table, string Column, string Policy)> Refs { get; } = [];
    public bool Rights { get; set; } = true;
    public bool Translations { get; set; } = true;

    public string? PolicyFor(string table, string column)
    {
        foreach (var (t, c, p) in Refs)
            if ((t == "*" || t.Equals(table, StringComparison.OrdinalIgnoreCase)) && SameColumn(c, column)) return p;
        return null;
    }

    /// <summary>A plan column name matches the physical name or the lookup name without "Id".</summary>
    public static bool SameColumn(string planColumn, string physical) =>
        planColumn.Equals(physical, StringComparison.OrdinalIgnoreCase) ||
        (planColumn + "Id").Equals(physical, StringComparison.OrdinalIgnoreCase);

    public static DataPlan Parse(string text, DataSettings settings)
    {
        var plan = new DataPlan();
        string? section = null;
        int lineNo = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            lineNo++;
            var line = raw;
            if (line.TrimStart().StartsWith("#")) continue;                         // a comment line
            var comment = line.IndexOf(" #", StringComparison.Ordinal);            // a trailing " # comment"
            if (comment >= 0) line = line[..comment];
            if (line.Trim().Length == 0) continue;
            var trimmed = line.Trim();
            if (trimmed.EndsWith(":") && !trimmed.StartsWith("-"))
            {
                section = trimmed.TrimEnd(':').Trim().ToLowerInvariant();
                if (section is not ("db packages" or "packages" or "tables" or "children" or "refs" or "options"))
                    throw new FormatException($"Line {lineNo}: unknown section '{trimmed}'.");
                continue;
            }
            if (!trimmed.StartsWith("-")) throw new FormatException($"Line {lineNo}: expected '- item' or 'section:'.");
            var item = trimmed[1..].Trim();
            switch (section)
            {
                case "db packages" or "packages":
                    plan.Packages.Add(item);
                    break;
                case "tables":
                {
                    var (name, rest) = Split(item);
                    Check(name, lineNo);
                    plan.Tables.Add((name, TableMode.Parse(rest, settings)));
                    break;
                }
                case "children":
                {
                    var (t, c) = Dotted(item, lineNo);
                    plan.Children.Add((t, c));
                    break;
                }
                case "refs":
                {
                    var (target, policy) = Split(item);
                    var (t, c) = Dotted(target, lineNo, allowStar: true);
                    policy = policy.Trim().ToLowerInvariant();
                    if (!RefPolicy.All.Contains(policy)) throw new FormatException($"Line {lineNo}: policy '{policy}' (pull | expect | author | null | drop).");
                    plan.Refs.Add((t, c, policy));
                    break;
                }
                case "options":
                {
                    var parts = item.Split(new[] { ':' }, 2);
                    var value = parts.Length > 1 && parts[1].Trim().ToLowerInvariant() is "yes" or "true" or "on" or "1";
                    switch (parts[0].Trim().ToLowerInvariant())
                    {
                        case "rights": plan.Rights = value; break;
                        case "translations": plan.Translations = value; break;
                        default: throw new FormatException($"Line {lineNo}: unknown option '{parts[0].Trim()}' (rights, translations).");
                    }
                    break;
                }
                default: throw new FormatException($"Line {lineNo}: item outside a section.");
            }
        }
        return plan;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        if (Packages.Count > 0) { sb.AppendLine("db packages:"); foreach (var p in Packages) sb.AppendLine(" - " + p); }
        if (Tables.Count > 0)
        {
            sb.AppendLine("tables:");
            int w = Tables.Max(t => t.Table.Length) + 2;
            foreach (var (t, m) in Tables) sb.AppendLine(" - " + t.PadRight(w) + m);
        }
        if (Children.Count > 0) { sb.AppendLine("children:"); foreach (var (t, c) in Children) sb.AppendLine($" - {t}.{c}"); }
        if (Refs.Count > 0) { sb.AppendLine("refs:"); foreach (var (t, c, p) in Refs) sb.AppendLine($" - {t}.{c}  {p}"); }
        sb.AppendLine("options:");
        sb.AppendLine(" - rights: " + (Rights ? "yes" : "no"));
        sb.AppendLine(" - translations: " + (Translations ? "yes" : "no"));
        return sb.ToString();
    }

    private static (string, string) Split(string item)
    {
        var i = item.IndexOfAny([' ', '\t']);
        return i < 0 ? (item, "") : (item[..i], item[(i + 1)..].Trim());
    }

    private static (string, string) Dotted(string item, int lineNo, bool allowStar = false)
    {
        var parts = item.Trim().Split('.');
        if (parts.Length != 2 || (!(allowStar && parts[0] == "*") && !SqlDialect.IsSafeName(parts[0])) || !SqlDialect.IsSafeName(parts[1]))
            throw new FormatException($"Line {lineNo}: expected Table.Column, got '{item}'.");
        return (parts[0], parts[1]);
    }

    private static void Check(string name, int lineNo)
    {
        if (!SqlDialect.IsSafeName(name)) throw new FormatException($"Line {lineNo}: bad table name '{name}'.");
    }
}
