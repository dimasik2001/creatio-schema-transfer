using Cst.Creatio;
using Cst.Data;
using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>Table data export, step 1: a flat list of packages (own ones until Ctrl+A); Space marks a package.</summary>
public sealed class DataPackageSource(IReadOnlyList<PackageInfo> packages) : IBrowserSource
{
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _showAll;

    public string TabTitle => T("Packages");
    public IReadOnlyList<string> Path => [];
    public bool CanGoUp => false;
    public bool NeedsLoad(BrowserItem? toggle = null) => false;
    public string? KeyHint => _showAll ? T("Ctrl+A own packages") : T("Ctrl+A all packages");
    public bool HasSelection => _selected.Count > 0;
    public string? SelectionSummary => _selected.Count == 0 ? null : F("packages: {0}", _selected.Count);
    public IReadOnlyCollection<string> Selected => _selected;

    public Task<IReadOnlyList<BrowserItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<BrowserItem>>(packages
        .Where(p => _showAll || p.IsOwn || _selected.Contains(p.Name))
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .Select(p => new BrowserItem
        {
            Id = p.Name, Label = p.Name, Detail = p.Maintainer,
            Mark = _selected.Contains(p.Name) ? Mark.All : Mark.None
        }).ToList());

    public void Enter(BrowserItem item) { }
    public string? Up() => null;

    public Task ToggleAsync(BrowserItem item)
    {
        if (!_selected.Remove(item.Id)) _selected.Add(item.Id);
        return Task.CompletedTask;
    }

    public string? HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key != ConsoleKey.A || (key.Modifiers & ConsoleModifiers.Control) == 0) return null;
        _showAll = !_showAll;
        return _showAll ? F("Showing all packages ({0}).", packages.Count) : F("Showing own packages ({0}).", packages.Count(p => p.IsOwn));
    }

    public void Select(IEnumerable<string> names) { foreach (var n in names) _selected.Add(n); }
}

/// <summary>
/// Table data export, step 2: the tables of the chosen packages with rows and approximate size. Space switches the
/// mode of a table: skip → full → top N → children → skip (the exact N, a WHERE condition or Ids are set on the
/// summary screen).
/// </summary>
public sealed class DataTableSource(IReadOnlyList<TableEntry> tables, DataSettings settings) : IBrowserSource
{
    private readonly Dictionary<string, TableMode> _modes = tables.ToDictionary(t => t.Name, t => Initial(t, settings), StringComparer.OrdinalIgnoreCase);

    private static TableMode Initial(TableEntry t, DataSettings settings) =>
        !t.Selected || t.Excluded != null ? new TableMode(TableModeKind.Skip)
        : t.Large ? TableMode.Parse($"top {settings.DefaultTop} by {settings.DefaultTopOrder}", settings)
        : TableMode.Full;

    public string TabTitle => T("Tables");
    public IReadOnlyList<string> Path => [];
    public bool CanGoUp => false;
    public bool NeedsLoad(BrowserItem? toggle = null) => false;
    public string? KeyHint => T("Space: skip → full → top → children");
    public bool HasSelection => _modes.Values.Any(m => m.Kind != TableModeKind.Skip);
    public string? SelectionSummary
    {
        get
        {
            int n = _modes.Values.Count(m => m.Kind != TableModeKind.Skip);
            return n == 0 ? null : F("tables: {0}", n);
        }
    }

    public IReadOnlyDictionary<string, TableMode> Modes => _modes;

    public void SetMode(string table, TableMode mode) => _modes[table] = mode;

    public Task<IReadOnlyList<BrowserItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<BrowserItem>>(tables.Select(t =>
    {
        var mode = _modes[t.Name];
        var group = t.Group == TableGroup.Own ? T("own") : T("replaced");
        var size = t.Excluded != null ? T("excluded") + ": " + t.Excluded
            : $"{t.Rows?.ToString() ?? "?"} {T("rows")} · ~{TableReport.FormatBytes(t.Bytes)}{(t.Large ? " · " + T("large") : "")}";
        return new BrowserItem
        {
            Id = t.Name, Label = t.Name,
            Detail = $"{group} · {size} · {Describe(mode)}",
            Mark = mode.Kind == TableModeKind.Skip ? Mark.None : mode.Kind == TableModeKind.Full ? Mark.All : Mark.Partial
        };
    }).ToList());

    public static string Describe(TableMode mode) => mode.Kind switch
    {
        TableModeKind.Skip => T("skip"),
        TableModeKind.Full => T("full"),
        TableModeKind.Children => T("children only"),
        _ => mode.ToString()
    };

    public void Enter(BrowserItem item) { }
    public string? Up() => null;

    public Task ToggleAsync(BrowserItem item)
    {
        var t = tables.First(x => x.Name == item.Id);
        if (t.Excluded != null) return Task.CompletedTask;
        _modes[item.Id] = _modes[item.Id].Kind switch
        {
            TableModeKind.Skip => TableMode.Full,
            TableModeKind.Full => TableMode.Parse($"top {settings.DefaultTop} by {settings.DefaultTopOrder}", settings),
            TableModeKind.Top or TableModeKind.Where or TableModeKind.Ids => new TableMode(TableModeKind.Children),
            _ => new TableMode(TableModeKind.Skip)
        };
        return Task.CompletedTask;
    }

    public string? HandleKey(ConsoleKeyInfo key) => null;
}
