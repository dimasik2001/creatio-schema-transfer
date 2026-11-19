namespace Cst.Ui;

public enum Mark { None, Partial, All }

/// <summary>One row of the current level.</summary>
public sealed class BrowserItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Grey text after the label (a count, a type).</summary>
    public string? Detail { get; init; }
    public bool IsContainer { get; init; }
    public Mark Mark { get; init; }
}

/// <summary>
/// One tab of the selection browser (Packages, Files): a tree walked one level at a time.
/// Methods may block on the network (the browser shows "Loading..." first when <see cref="NeedsLoad"/> says so).
/// </summary>
public interface IBrowserSource
{
    string TabTitle { get; }
    /// <summary>Breadcrumb labels below the stand name, e.g. ["UtkIntegration", "User tasks"].</summary>
    IReadOnlyList<string> Path { get; }
    bool CanGoUp { get; }
    /// <summary>True when showing the current level (or toggling <paramref name="toggle"/>) has to load data first.</summary>
    bool NeedsLoad(BrowserItem? toggle = null);
    Task<IReadOnlyList<BrowserItem>> ItemsAsync();
    void Enter(BrowserItem item);
    /// <summary>Goes one level up; returns the Id of the item that was left (to put the cursor on it).</summary>
    string? Up();
    Task ToggleAsync(BrowserItem item);
    /// <summary>Source-specific keys (Ctrl+A on Packages). Returns a status message or null when not handled.</summary>
    string? HandleKey(ConsoleKeyInfo key);
    /// <summary>"12 schemas" / "3 files" for the footer; null when nothing is selected.</summary>
    string? SelectionSummary { get; }
    bool HasSelection { get; }
    /// <summary>Hint shown in the footer for this tab (e.g. "Ctrl+A all packages").</summary>
    string? KeyHint { get; }
}

public enum BrowserResult { Done, Back }

/// <summary>
/// File-manager style selection over one or more tabs: one level on screen, breadcrumbs on top, key hints at the
/// bottom, type-to-filter, Space to mark, three-state marks, F2 to finish.
/// </summary>
public sealed class SelectionBrowser(string standHeader, IReadOnlyList<IBrowserSource> tabs)
{
    private readonly Screen _screen = new();
    private int _tab;
    private string _filter = "";
    private int _cursor, _top;
    private string? _status;
    private string? _restoreId;

    public async Task<BrowserResult> RunAsync()
    {
        try
        {
            while (true)
            {
                var source = tabs[_tab];
                if (source.NeedsLoad()) { _status = "Loading..."; Draw(source, []); }
                IReadOnlyList<BrowserItem> all;
                try { all = await source.ItemsAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _status = "Cannot load: " + ex.Message;
                    all = [];
                    if (source.CanGoUp) { _restoreId = source.Up(); Draw(source, all); WaitKey(); continue; }
                }
                if (_status == "Loading...") _status = null;
                var items = Filter(all);
                if (_restoreId != null)
                {
                    var i = items.ToList().FindIndex(x => x.Id == _restoreId);
                    _cursor = Math.Max(0, i);
                    _restoreId = null;
                }
                _cursor = items.Count == 0 ? 0 : Math.Min(Math.Max(0, _cursor), items.Count - 1);
                Draw(source, items);

                var key = UiInput.ReadKey();
                var current = items.Count > 0 ? items[_cursor] : null;
                _status = null;
                bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: _cursor--; break;
                    case ConsoleKey.DownArrow: _cursor++; break;
                    case ConsoleKey.PageUp: _cursor -= PageSize; break;
                    case ConsoleKey.PageDown: _cursor += PageSize; break;
                    case ConsoleKey.Home: _cursor = 0; break;
                    case ConsoleKey.End: _cursor = items.Count - 1; break;
                    case ConsoleKey.Enter or ConsoleKey.RightArrow when current != null && current.IsContainer:
                        source.Enter(current); ResetLevel(); break;
                    case ConsoleKey.Enter when current != null:
                        await ToggleAsync(source, current, items); break;
                    case ConsoleKey.LeftArrow when source.CanGoUp:
                        _restoreId = source.Up(); ResetLevel(); break;
                    case ConsoleKey.Backspace when _filter.Length > 0:
                        _filter = _filter.Substring(0, _filter.Length - 1); _cursor = 0; break;
                    case ConsoleKey.Backspace when source.CanGoUp:
                        _restoreId = source.Up(); ResetLevel(); break;
                    case ConsoleKey.Spacebar when current != null:
                        await ToggleAsync(source, current, items); _cursor++; break;
                    case ConsoleKey.Tab when tabs.Count > 1:
                        _tab = (_tab + ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? tabs.Count - 1 : 1)) % tabs.Count;
                        _filter = ""; _cursor = 0; _top = 0; break;
                    case ConsoleKey.F2:
                    case ConsoleKey.S when ctrl:
                        if (tabs.Any(t => t.HasSelection)) return BrowserResult.Done;
                        _status = "Nothing is selected yet (Space marks an item)."; break;
                    case ConsoleKey.Escape when _filter.Length > 0:
                        _filter = ""; _cursor = 0; break;
                    case ConsoleKey.Escape when source.CanGoUp:
                        _restoreId = source.Up(); ResetLevel(); break;
                    case ConsoleKey.Escape:
                        if (!tabs.Any(t => t.HasSelection) || Confirm(source, items, "Discard the selection and go back? [y/N]")) return BrowserResult.Back;
                        break;
                    default:
                        if (ctrl || (key.Modifiers & ConsoleModifiers.Alt) != 0)
                        {
                            _status = source.HandleKey(key);
                            if (_status != null) { _cursor = 0; _top = 0; }
                        }
                        else if (!char.IsControl(key.KeyChar))
                        {
                            _filter += key.KeyChar; _cursor = 0;
                        }
                        break;
                }
            }
        }
        finally { _screen.Leave(); }
    }

    private int PageSize => Math.Max(3, _screen.Height - 6);

    private void ResetLevel() { _filter = ""; _cursor = 0; _top = 0; }

    private List<BrowserItem> Filter(IReadOnlyList<BrowserItem> all) => _filter.Length == 0
        ? all.ToList()
        : all.Where(i => i.Label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

    private async Task ToggleAsync(IBrowserSource source, BrowserItem item, IReadOnlyList<BrowserItem> items)
    {
        if (source.NeedsLoad(item)) { _status = $"Loading {item.Label}..."; Draw(source, items); }
        try { await source.ToggleAsync(item); _status = null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status = $"Cannot mark {item.Label}: {ex.Message}"; }
    }

    private bool Confirm(IBrowserSource source, IReadOnlyList<BrowserItem> items, string question)
    {
        _status = question;
        Draw(source, items);
        var k = UiInput.ReadKey();
        _status = null;
        return k.Key == ConsoleKey.Y;
    }

    private static void WaitKey() => UiInput.ReadKey();

    private void Draw(IBrowserSource source, IReadOnlyList<BrowserItem> items)
    {
        var lines = new List<IReadOnlyList<Seg>>();
        // Tabs and stand.
        var head = new List<Seg>();
        for (int i = 0; i < tabs.Count; i++)
        {
            // Brackets as well as colour: the active tab stays visible without colours (and in recordings).
            head.Add(i == _tab ? new Seg($"[{tabs[i].TabTitle}]", "black on aqua") : new Seg($" {tabs[i].TabTitle} ", "grey"));
            head.Add(new Seg(" "));
        }
        head.Add(new Seg("  " + standHeader, "grey"));
        lines.Add(head);
        // Breadcrumbs and filter.
        var crumbs = new List<Seg> { new(string.Join(" / ", new[] { standHeader.Split(' ')[0] }.Concat(source.Path)), "bold") };
        if (_filter.Length > 0) crumbs.Add(new Seg($"   filter: {_filter}", "yellow"));
        lines.Add(crumbs);
        lines.Add([new Seg(new string('─', Math.Max(0, _screen.Width)), "grey")]);

        int rows = PageSize;
        if (_cursor < _top) _top = _cursor;
        if (_cursor >= _top + rows) _top = _cursor - rows + 1;
        _top = Math.Max(0, Math.Min(_top, Math.Max(0, items.Count - rows)));
        if (items.Count == 0)
            lines.Add([new Seg(_filter.Length > 0 ? "  (nothing matches the filter)" : "  (empty)", "grey")]);
        for (int i = _top; i < Math.Min(items.Count, _top + rows); i++)
        {
            var it = items[i];
            bool sel = i == _cursor;
            var (box, color) = it.Mark switch
            {
                Mark.All => ("[x]", "green"),
                Mark.Partial => ("[~]", "yellow"),
                _ => ("[ ]", (string?)null)
            };
            string? Style(string? baseStyle) => sel ? ((baseStyle ?? "default") + " on grey23") : baseStyle;
            var row = new List<Seg>
            {
                new(sel ? "> " : "  ", Style("aqua")),
                new(box + " ", Style(color)),
                new(it.Label + (it.IsContainer ? "/" : ""), Style(color == null ? (it.IsContainer ? "bold" : null) : "bold " + color))
            };
            if (it.Detail != null) row.Add(new Seg("  " + it.Detail, Style("grey")));
            lines.Add(row);
        }
        while (lines.Count < 3 + Math.Max(rows, 1)) lines.Add([]);

        // Footer: position, selection, status, keys.
        var selection = string.Join(", ", tabs.Select(t => t.SelectionSummary).Where(s => s != null));
        var info = new List<Seg>
        {
            new(items.Count == 0 ? "" : $"{_cursor + 1}/{items.Count}   ", "grey"),
            new("Selected: " + (selection.Length == 0 ? "nothing" : selection), selection.Length == 0 ? "grey" : "green")
        };
        lines.Add([new Seg(new string('─', Math.Max(0, _screen.Width)), "grey")]);
        lines.Add(info);
        if (_status != null) lines.Add([new Seg(_status, "yellow")]);
        else
        {
            var keys = "↑↓ move  Enter/→ open  ←/Backspace up  Space mark  type to filter  Esc back  F2 done";
            if (tabs.Count > 1) keys += "  Tab switch";
            if (source.KeyHint != null) keys += "  " + source.KeyHint;
            lines.Add([new Seg(keys, "grey")]);
        }
        _screen.Render(lines);
    }
}
