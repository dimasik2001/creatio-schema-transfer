using Cst.Creatio;

namespace Cst.Data;

public enum TableGroup { Own, Extended }

/// <summary>One table offered for the data transfer (db-tables, the Tables screen later).</summary>
public sealed class TableEntry
{
    public required string Name { get; init; }
    public required string Package { get; init; }
    public TableGroup Group { get; init; }
    public bool Selected { get; set; }
    public long? Rows { get; set; }
    public long? Bytes { get; set; }
    public bool Large { get; set; }
    /// <summary>"full", "top N by ..." or "skip".</summary>
    public string Mode { get; set; } = "full";
    /// <summary>Why the table cannot be transferred (a view, a virtual object, no runtime entity); null = it can.</summary>
    public string? Excluded { get; set; }
    public List<string> BinaryColumns { get; } = [];
    public List<string> UnreadableColumns { get; } = [];
    public bool AdministratedByRecords { get; set; }
    public List<string> ExtendedIn { get; } = [];
}

/// <summary>A lookup reference between tables (docs/db-transfer.md, 5.1).</summary>
public sealed record RefEdge(string Table, string Column, string RefTable, bool Required, bool Weak, bool InSelection, string Policy);

public sealed record TableReportResult(List<string> Packages, List<TableEntry> Tables, List<RefEdge> Refs);

/// <summary>Builds the read-only report of db-tables: entities of packages, their size and references.</summary>
public sealed class TableReport(CreatioClient client, DataSettings settings, string dbEngine)
{
    private readonly EntityCatalog _catalog = new(client);

    public async Task<TableReportResult> BuildAsync(IReadOnlyList<PackageInfo> packages, Action<string>? progress = null)
    {
        var tables = new List<TableEntry>();
        foreach (var pe in await _catalog.FindEntitiesAsync(packages))
        {
            var t = new TableEntry
            {
                Name = pe.Name, Package = pe.Package,
                Group = pe.OwnRoot ? TableGroup.Own : TableGroup.Extended,
                Selected = pe.OwnRoot
            };
            t.ExtendedIn.AddRange(pe.ExtendedIn);
            tables.Add(t);
        }

        // Record rights tables (Sys<X>Right) are not entities: DataService answers "not found" for them on 10.0, so
        // they are only flagged (AdministratedByRecords) until a service route for them is found.
        foreach (var t in tables)
        {
            progress?.Invoke(t.Name);
            await DescribeAsync(t, await _catalog.GetEntityAsync(t.Name));
        }

        var selected = new HashSet<string>(tables.Where(t => t.Selected && t.Excluded == null).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var refs = new List<RefEdge>();
        foreach (var t in tables.Where(t => t.Excluded == null))
        {
            var info = await _catalog.GetEntityAsync(t.Name);
            foreach (var c in info!.Columns.Where(c => c.IsLookup))
            {
                bool inSel = selected.Contains(c.ReferenceSchema!);
                refs.Add(new RefEdge(t.Name, c.Name, c.ReferenceSchema!, c.IsRequired, c.IsWeakReference, inSel, DefaultPolicy(t.Name, c, inSel)));
            }
        }
        return new TableReportResult(packages.Select(p => p.Name).ToList(), tables, refs);
    }

    /// <summary>Default reference policy (docs/db-transfer.md, 5.2).</summary>
    public static string DefaultPolicy(string table, ColumnInfo c, bool refInSelection)
    {
        if (c.Name is "CreatedBy" or "ModifiedBy") return "author";
        if (refInSelection || c.ReferenceSchema!.Equals(table, StringComparison.OrdinalIgnoreCase)) return "pull";
        return "expect";
    }

    private async Task DescribeAsync(TableEntry t, EntityInfo? info)
    {
        if (info == null) { t.Excluded = "no runtime entity"; t.Selected = false; return; }
        if (info.IsDBView) { t.Excluded = "DB view"; t.Selected = false; return; }
        if (info.IsVirtual) { t.Excluded = "virtual object"; t.Selected = false; return; }
        t.AdministratedByRecords = info.AdministratedByRecords;
        t.BinaryColumns.AddRange(info.Columns.Where(c => c.Access == ColumnAccess.Binary).Select(c => c.Name));
        t.UnreadableColumns.AddRange(info.Columns.Where(c => c.Access == ColumnAccess.Unreadable).Select(c => c.Name));
        try
        {
            var (rows, bytes) = await _catalog.MeasureAsync(info, dbEngine);
            t.Rows = rows; t.Bytes = bytes;
            t.Large = settings.IsLarge(rows, bytes ?? 0);
            if (t.Large) t.Mode = $"top {settings.DefaultTop} by {settings.DefaultTopOrder}";
        }
        catch (CreatioException ex)
        {
            t.Excluded = "cannot read: " + ex.Message;
            t.Selected = false;
        }
    }

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null => "?",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };
}
