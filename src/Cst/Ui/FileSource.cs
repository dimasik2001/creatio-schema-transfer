using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>
/// Selected paths under the Creatio root, as the files: section wants them: a fully selected folder is ONE entry
/// (recursive, later files included); deselecting something inside it splits the folder into its remaining
/// children along the way. Paths are relative, backslash-separated, without a trailing separator.
/// </summary>
public sealed class FileSelection(Func<string, IEnumerable<(string Name, bool IsDir)>> listChildren)
{
    private readonly HashSet<string> _roots = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Roots => _roots;

    public static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "\\" + name;

    private static bool IsUnder(string path, string ancestor) =>
        path.Length > ancestor.Length && path.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);

    private string? SelectedAncestorOrSelf(string path) =>
        _roots.FirstOrDefault(r => r.Equals(path, StringComparison.OrdinalIgnoreCase) || IsUnder(path, r));

    public Mark StateOf(string path)
    {
        if (SelectedAncestorOrSelf(path) != null) return Mark.All;
        return _roots.Any(r => IsUnder(r, path)) ? Mark.Partial : Mark.None;
    }

    public void Toggle(string path)
    {
        var holder = SelectedAncestorOrSelf(path);
        if (holder == null)
        {
            // Select: the item replaces anything selected below it.
            _roots.RemoveWhere(r => IsUnder(r, path));
            _roots.Add(path);
            return;
        }
        _roots.Remove(holder);
        if (holder.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        // Deselect inside a selected folder: keep its other children, level by level down to the item.
        var rest = path.Substring(holder.Length + 1).Split('\\');
        var dir = holder;
        foreach (var part in rest)
        {
            foreach (var (name, _) in listChildren(dir))
                if (!name.Equals(part, StringComparison.OrdinalIgnoreCase)) _roots.Add(Join(dir, name));
            dir = Join(dir, part);
        }
    }

    /// <summary>
    /// Adds an entry from a loaded list as it is (a file, a folder or a mask like bin\Utk*.dll): it replaces entries
    /// below it, and is skipped when a selected folder already covers it.
    /// </summary>
    public void Add(string entry)
    {
        var path = entry.Trim().Replace('/', '\\').TrimEnd('\\');
        if (path.Length == 0 || SelectedAncestorOrSelf(path) != null) return;
        _roots.RemoveWhere(r => IsUnder(r, path));
        _roots.Add(path);
    }

    /// <summary>Entries for the files: section, sorted.</summary>
    public List<string> Entries() => _roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Files tab: the file system under the stand's "Path", folders first, then files.</summary>
public sealed class FileSource : IBrowserSource
{
    private readonly string _root;
    private readonly Dictionary<string, List<(string Name, bool IsDir)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string _dir = "";

    public FileSource(string root)
    {
        _root = root;
        Selection = new FileSelection(Children);
    }

    public FileSelection Selection { get; }
    public string TabTitle => T("Files");
    public string? KeyHint => null;
    public IReadOnlyList<string> Path => _dir.Length == 0 ? [] : _dir.Split('\\');
    public bool CanGoUp => _dir.Length > 0;
    public bool NeedsLoad(BrowserItem? toggle = null) => false;

    /// <summary>Children of a relative folder (cached). An unreadable folder lists as empty.</summary>
    public List<(string Name, bool IsDir)> Children(string dir)
    {
        if (_cache.TryGetValue(dir, out var list)) return list;
        list = [];
        var full = dir.Length == 0 ? _root : System.IO.Path.Combine(_root, dir);
        try
        {
            var info = new DirectoryInfo(full);
            list.AddRange(info.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d => (d.Name, true)));
            list.AddRange(info.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => (f.Name, false)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        _cache[dir] = list;
        return list;
    }

    public Task<IReadOnlyList<BrowserItem>> ItemsAsync()
    {
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException(F("The stand's Path does not exist on this machine: {0}", _root));
        IReadOnlyList<BrowserItem> items = Children(_dir).Select(c =>
        {
            var rel = FileSelection.Join(_dir, c.Name);
            string? detail = null;
            if (!c.IsDir)
            {
                try { detail = FormatSize(new FileInfo(System.IO.Path.Combine(_root, rel)).Length); }
                catch (IOException) { }
            }
            return new BrowserItem { Id = c.Name, Label = c.Name, IsContainer = c.IsDir, Mark = Selection.StateOf(rel), Detail = detail };
        }).ToList();
        return Task.FromResult(items);
    }

    public void Enter(BrowserItem item) => _dir = FileSelection.Join(_dir, item.Id);

    public string? Up()
    {
        var i = _dir.LastIndexOf('\\');
        var left = i < 0 ? _dir : _dir.Substring(i + 1);
        _dir = i < 0 ? "" : _dir.Substring(0, i);
        return left;
    }

    public Task ToggleAsync(BrowserItem item)
    {
        Selection.Toggle(FileSelection.Join(_dir, item.Id));
        return Task.CompletedTask;
    }

    public string? HandleKey(ConsoleKeyInfo key) => null;

    public bool HasSelection => Selection.Roots.Count > 0;

    public string? SelectionSummary => Selection.Roots.Count switch
    {
        0 => null,
        var n => F("file entries: {0}", n)
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };
}
