using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Ui;

/// <summary>
/// Packages tab: packages → folders (Objects, Source code, ...) → elements. Own packages only until Ctrl+A.
/// Elements of a package are loaded when it is opened or marked as a whole, and cached for the session.
/// </summary>
public sealed class PackageSource(Catalog catalog, IReadOnlyList<PackageInfo> packages) : IBrowserSource
{
    private readonly Dictionary<string, List<PackageElement>> _elements = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Package name → selected elements (Kind, UId).</summary>
    private readonly Dictionary<string, HashSet<(ItemKind, Guid)>> _selected = new(StringComparer.OrdinalIgnoreCase);
    private bool _showAll;
    private string? _package;
    private ElementFolder? _folder;

    public string TabTitle => "Packages";
    public string? KeyHint => _package == null ? (_showAll ? "Ctrl+A own packages" : "Ctrl+A all packages") : null;

    public IReadOnlyList<string> Path
    {
        get
        {
            var p = new List<string>();
            if (_package != null) p.Add(_package);
            if (_folder is { } f) p.Add(ElementFolders.Title(f));
            return p;
        }
    }

    public bool CanGoUp => _package != null;

    public bool NeedsLoad(BrowserItem? toggle = null)
    {
        var pkg = toggle == null ? _package : (_package == null ? toggle.Id : null);
        return pkg != null && !_elements.ContainsKey(pkg);
    }

    private PackageInfo Package(string name) => packages.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private async Task<List<PackageElement>> ElementsAsync(string package)
    {
        if (!_elements.TryGetValue(package, out var list))
            _elements[package] = list = await catalog.LoadPackageElementsAsync(Package(package).Id);
        return list;
    }

    private HashSet<(ItemKind, Guid)> Selected(string package)
    {
        if (!_selected.TryGetValue(package, out var set)) _selected[package] = set = [];
        return set;
    }

    private static Mark MarkOf(int selected, int total) =>
        selected == 0 ? Mark.None : selected >= total && total > 0 ? Mark.All : Mark.Partial;

    public async Task<IReadOnlyList<BrowserItem>> ItemsAsync()
    {
        if (_package == null)
        {
            return packages
                .Where(p => _showAll || p.IsOwn || (_selected.TryGetValue(p.Name, out var s) && s.Count > 0))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    int sel = _selected.TryGetValue(p.Name, out var s) ? s.Count : 0;
                    var mark = _elements.TryGetValue(p.Name, out var els) ? MarkOf(sel, els.Count) : sel > 0 ? Mark.Partial : Mark.None;
                    var detail = (mark == Mark.All ? "whole package (zip install) · " : sel > 0 ? $"{sel} selected · " : "")
                                 + (p.IsOwn ? "" : "base · ") + p.Maintainer;
                    return new BrowserItem { Id = p.Name, Label = p.Name, IsContainer = true, Mark = mark, Detail = detail };
                }).ToList();
        }
        var elements = await ElementsAsync(_package);
        var selected = Selected(_package);
        if (_folder == null)
        {
            return elements.GroupBy(e => e.Folder).OrderBy(g => g.Key).Select(g =>
            {
                int sel = g.Count(e => selected.Contains((e.Kind, e.UId)));
                return new BrowserItem
                {
                    Id = g.Key.ToString(), Label = ElementFolders.Title(g.Key), IsContainer = true,
                    Mark = MarkOf(sel, g.Count()), Detail = sel > 0 ? $"({sel}/{g.Count()})" : $"({g.Count()})"
                };
            }).ToList();
        }
        return elements.Where(e => e.Folder == _folder).Select(e => new BrowserItem
        {
            Id = $"{e.Kind}:{e.UId}", Label = e.Name,
            Mark = selected.Contains((e.Kind, e.UId)) ? Mark.All : Mark.None,
            Detail = _folder == ElementFolder.Other ? e.Manager?.Replace("SchemaManager", "") : null
        }).ToList();
    }

    public void Enter(BrowserItem item)
    {
        if (_package == null) _package = item.Id;
        else if (_folder == null) _folder = (ElementFolder)Enum.Parse(typeof(ElementFolder), item.Id);
    }

    public string? Up()
    {
        if (_folder is { } f) { _folder = null; return f.ToString(); }
        var p = _package; _package = null; return p;
    }

    public async Task ToggleAsync(BrowserItem item)
    {
        IEnumerable<PackageElement> targets;
        string package;
        if (_package == null)
        {
            package = item.Id;
            targets = await ElementsAsync(package);
        }
        else
        {
            package = _package;
            var all = await ElementsAsync(package);
            targets = _folder == null
                ? all.Where(e => e.Folder.ToString() == item.Id)
                : all.Where(e => $"{e.Kind}:{e.UId}" == item.Id);
        }
        var keys = targets.Select(e => (e.Kind, e.UId)).ToList();
        var set = Selected(package);
        if (item.Mark == Mark.All) foreach (var k in keys) set.Remove(k);
        else foreach (var k in keys) set.Add(k);
    }

    public string? HandleKey(ConsoleKeyInfo key)
    {
        if (_package != null || key.Key != ConsoleKey.A || (key.Modifiers & ConsoleModifiers.Control) == 0) return null;
        _showAll = !_showAll;
        return _showAll ? $"Showing all {packages.Count} packages." : $"Showing own packages ({packages.Count(p => p.IsOwn)}).";
    }

    public bool HasSelection => _selected.Values.Any(s => s.Count > 0);

    public string? SelectionSummary
    {
        get
        {
            int n = _selected.Values.Sum(s => s.Count);
            return n == 0 ? null : n == 1 ? "1 element" : $"{n} elements";
        }
    }

    /// <summary>Every selected element as element sections, whole packages included (for the reference analysis).</summary>
    public List<SchemaListPackage> SelectedAsLists() =>
        _selected.Where(x => x.Value.Count > 0).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x =>
        {
            var all = _elements[x.Key];
            var name = Package(x.Key).Name;
            var chosen = all.Where(e => x.Value.Contains((e.Kind, e.UId))).Select(e => (e.Name, e.Kind));
            return SchemaList.Build(name, name, chosen, all.Select(e => (e.Name, e.Kind)));
        }).ToList();

    /// <summary>Adds one element (found by package, kind and UId) to the selection; false when the package does not have it.</summary>
    public async Task<bool> SelectAsync(string package, ItemKind kind, Guid uId)
    {
        if (!packages.Any(p => p.Name.Equals(package, StringComparison.OrdinalIgnoreCase))) return false;
        var all = await ElementsAsync(package);
        if (!all.Any(e => e.Kind == kind && e.UId == uId)) return false;
        Selected(package).Add((kind, uId));
        return true;
    }

    /// <summary>
    /// The selection for the list: packages marked as a whole (every element selected) go to the packages:
    /// section (Creatio zip install); the rest become element sections (target = source until mapped), with type
    /// hints where a name is not unique in its package. Selected packages are always loaded.
    /// </summary>
    public (List<SchemaListPackage> Elements, List<string> Whole) ToSelection()
    {
        var elements = new List<SchemaListPackage>();
        var whole = new List<string>();
        foreach (var (package, set) in _selected.Where(x => x.Value.Count > 0).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var all = _elements[package];
            var name = Package(package).Name;
            if (set.Count >= all.Count) { whole.Add(name); continue; }
            var chosen = all.Where(e => set.Contains((e.Kind, e.UId))).Select(e => (e.Name, e.Kind));
            elements.Add(SchemaList.Build(name, name, chosen, all.Select(e => (e.Name, e.Kind))));
        }
        return (elements, whole);
    }
}
