using System.Net.Http;
using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;
using Cst.Transfer;
using Spectre.Console;

namespace Cst.Ui;

public enum SummaryResult { BackToSelection, MainMenu }

/// <summary>
/// Export summary: the list exactly as it will be saved, folders with their current file counts, elements the
/// selection references but does not contain, the package mapping (chosen from the target stand's packages), and
/// the actions Save list / Export archive / Import to a stand / Back / Main menu.
/// </summary>
public sealed class SummaryScreen(
    StandSession source, IReadOnlyList<PackageInfo> sourcePackages, PackageSource packages, FileSource? files,
    EnvPicker envs, UiState state, UiLog log, string baseDir)
{
    private StandSession? _target;
    private List<PackageInfo>? _targetPackages;
    private readonly Dictionary<string, string> _mapping = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private List<ExternalDependency>? _refs;
    private string? _refsError;
    private enum RefsState { NotChecked, Checked, SelectionChanged }
    /// <summary>The selection (as list text) the last reference check ran on; null = never checked.</summary>
    private string? _checkedSelection;
    private string SelectionSnapshot() => SchemaList.Write(packages.SelectedAsLists());
    private RefsState _refsState => _checkedSelection == null ? RefsState.NotChecked
        : _checkedSelection == SelectionSnapshot() ? RefsState.Checked : RefsState.SelectionChanged;
    private bool _kept; // saved or exported at least once
    private Dictionary<string, string>? _folderNotes;
    /// <summary>Source packages downloaded in this export session (reference checks reuse them).</summary>
    private readonly Dictionary<string, PackageContent> _packageCache = new(StringComparer.OrdinalIgnoreCase);

    private const string CheckRefs = "check", AddRefs = "add", Mapping = "map", Save = "save", Export = "export", Import = "import", Back = "back", Menu = "menu";
    private const int NameColumn = 18;

    public async Task<SummaryResult> RunAsync()
    {
        _folderNotes ??= FolderNotes();
        while (true)
        {
            Draw();
            var (elements, _) = Lists();
            var choices = new List<string> { CheckRefs };
            if (_refsState == RefsState.Checked && _refs is { Count: > 0 }) choices.Add(AddRefs);
            if (elements.Count > 0) choices.Add(Mapping);
            choices.AddRange([Save, Export, Import, Back, Menu]);
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("What next?")
                .PageSize(10)
                .UseConverter(c => c switch
                {
                    CheckRefs => UiApp.MenuLine("Check references", _refsState == RefsState.Checked
                        ? "check again" : "find elements the selection needs but does not contain", NameColumn),
                    AddRefs => UiApp.MenuLine("Add…", $"add the referenced elements ({_refs!.Count})", NameColumn),
                    Mapping => UiApp.MenuLine("Mapping…", _target == null ? "choose the target stand and map packages" : $"map packages to {_target.Name}", NameColumn),
                    Save => UiApp.MenuLine("Save", "save the list to a file", NameColumn),
                    Export => UiApp.MenuLine("Export", "write the transfer archive", NameColumn),
                    Import => UiApp.MenuLine("Import", _target == null ? "export, then import into a stand" : $"export, then import into {_target.Name}", NameColumn),
                    Back => UiApp.MenuLine("Back", "change the selection", NameColumn),
                    _ => UiApp.MenuLine("Menu", "back to the main menu", NameColumn)
                })
                .AddChoices(choices));
            try
            {
                switch (choice)
                {
                    case CheckRefs: await AnalyzeAsync(); break;
                    case AddRefs: await AddReferencesAsync(); break;
                    case Mapping: await EditMappingAsync(); break;
                    case Save: SaveList(); break;
                    case Export: await ExportArchiveAsync(); break;
                    case Import: await ImportAsync(); break;
                    case Back: return SummaryResult.BackToSelection;
                    default:
                        if (_kept || AnsiConsole.Confirm("Leave without saving the list or exporting it?", false)) return SummaryResult.MainMenu;
                        break;
                }
            }
            catch (TransferException ex)
            {
                foreach (var e in ex.Errors) AnsiConsole.MarkupLine($"[red]{Markup.Escape(e)}[/]");
                UiInput.Pause();
            }
            catch (Exception ex) when (ex is CreatioException or ArgumentException or IOException or UnauthorizedAccessException or FormatException)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                UiInput.Pause();
            }
        }
    }

    /// <summary>Called after the user went back to the selection: folder counts are recomputed.</summary>
    public void SelectionChanged() => _folderNotes = null;

    /// <summary>Warning line while references are not known for the current selection; null when checked.</summary>
    private string? RefsWarning => _refsState switch
    {
        RefsState.NotChecked => "References are not checked: the target may lack an element the selection needs (run Check references).",
        RefsState.SelectionChanged => "The selection changed after the reference check: run Check references again.",
        _ => _refsError != null ? $"The reference check failed: {_refsError}" : null
    };

    /// <summary>Repeats the reference warning (or the found references) after Save / Export, where it matters.</summary>
    private void WarnRefs()
    {
        if (RefsWarning is { } w) AnsiConsole.MarkupLine($"  [yellow]⚠ {Markup.Escape(w)}[/]");
        else if (_refs is { Count: > 0 })
            AnsiConsole.MarkupLine($"  [yellow]⚠ {_refs.Count} referenced element(s) are not in the list; they must already exist on the target.[/]");
    }

    // ---------- the list ----------

    private string Map(string package) => _mapping.TryGetValue(package, out var t) ? t : package;

    private (List<SchemaListPackage> Elements, List<string> Whole) Lists()
    {
        var (elements, whole) = packages.ToSelection();
        foreach (var p in elements) p.Target = Map(p.Source);
        return (elements, whole);
    }

    private List<string> FileEntries() => files?.Selection.Entries() ?? [];

    private string ListText()
    {
        var (elements, whole) = Lists();
        var header = new List<string> { $"cst list from {source.Name} ({source.Client.Url}), {DateTime.Now:yyyy-MM-dd HH:mm}" };
        if (_target != null) header.Add($"mapped for target {_target.Name}");
        return SchemaList.Write(elements, FileEntries(), header, whole);
    }

    /// <summary>"folder, N files now" for every selected folder (counting stops at 20000).</summary>
    private Dictionary<string, string> FolderNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (files == null) return notes;
        foreach (var e in FileEntries())
        {
            var full = Path.Combine(source.Settings.Path!, e);
            if (!Directory.Exists(full)) continue;
            int n = 0;
            try { foreach (var _ in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) if (++n >= 20000) break; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
            notes[e] = n >= 20000 ? "folder, 20000+ files now" : $"folder, {n} file(s) now";
        }
        return notes;
    }

    private void Draw()
    {
        AnsiConsole.Clear();
        var to = _target == null ? "[grey]target not chosen[/]" : Markup.Escape(_target.Header);
        AnsiConsole.Write(new Rule($"[bold]Export · summary[/] [grey]·[/] {Markup.Escape(source.Header)} [grey]→[/] {to}").LeftJustified());

        AnsiConsole.Write(new Panel(new Text(ListText().TrimEnd())).Header("List (as it will be saved)").Expand());
        if (_folderNotes is { Count: > 0 })
            foreach (var (path, note) in _folderNotes) AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(path)}: {Markup.Escape(note)}[/]");

        var (elements, whole) = Lists();
        if (elements.Count + whole.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).Title("Package mapping")
                .AddColumn("Source").AddColumn("Target").AddColumn("Note");
            foreach (var w in whole) t.AddRow(Markup.Escape(w), Markup.Escape(w), "[grey]whole package: own name (zip install)[/]");
            foreach (var p in elements)
            {
                string note;
                if (_targetPackages != null && !_targetPackages.Any(x => x.Name.Equals(p.Target, StringComparison.OrdinalIgnoreCase)))
                    note = $"[yellow]not found on {Markup.Escape(_target!.Name)}[/]";
                else if (_remembered.Contains(p.Source)) note = "[grey](remembered)[/]";
                else note = _target == null ? "[grey]target stand not chosen[/]" : "";
                t.AddRow(Markup.Escape(p.Source), Markup.Escape(p.Target), note);
            }
            AnsiConsole.Write(t);
        }

        if (RefsWarning is { } warning) AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(warning)}[/]");
        else if (_refs is { Count: > 0 })
        {
            var r = new Table().Border(TableBorder.Rounded).Title("[yellow]Referenced but not selected[/]")
                .AddColumn("Element").AddColumn("Type").AddColumn("Required by");
            foreach (var e in _refs)
                r.AddRow(Markup.Escape($"{e.SourcePackage}/{e.Name}"), Markup.Escape(TypeOf(e)), Markup.Escape(string.Join(", ", e.RequiredBy)));
            AnsiConsole.Write(r);
        }
        else if (_refs != null) AnsiConsole.MarkupLine("[green]Every referenced element of your own packages is selected.[/]");
        AnsiConsole.WriteLine();
    }

    private static string TypeOf(ExternalDependency e) => ElementFolders.Of(e.Kind, e.Manager) switch
    {
        ElementFolder.SqlScripts => "SQL script",
        ElementFolder.DataBindings => "data binding",
        ElementFolder.Objects => "object",
        ElementFolder.SourceCode => "source code",
        ElementFolder.ClientModules => "client module",
        ElementFolder.UserTasks => "user task",
        ElementFolder.BusinessProcesses => "business process",
        ElementFolder.AddOns => "add-on",
        _ => (e.Manager ?? "schema").Replace("SchemaManager", "")
    };

    // ---------- references ----------

    private async Task AnalyzeAsync()
    {
        _refsError = null;
        var own = sourcePackages.Where(p => p.IsOwn).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lists = packages.SelectedAsLists();
        var snapshot = SelectionSnapshot();
        _checkedSelection = snapshot; // a failed check still counts as run; RefsWarning shows the error
        if (lists.Count == 0) { _refs = []; return; }
        try
        {
            await WithProgressAsync("Checking references of the selection...",
                async () => _refs = await new Exporter(source.Client) { PackageCache = _packageCache }.FindReferencesAsync(lists, own.Contains));
        }
        catch (TransferException ex) { _refs = null; _refsError = string.Join("; ", ex.Errors); }
        catch (Exception ex) when (ex is CreatioException or IOException or HttpRequestException or TaskCanceledException) { _refs = null; _refsError = ex.Message; }
    }

    private async Task AddReferencesAsync()
    {
        var prompt = new MultiSelectionPrompt<ExternalDependency>()
            .Title("Add which elements? [grey](Space toggles, Enter adds)[/]")
            .PageSize(15)
            .NotRequired()
            .UseConverter(e => Markup.Escape(UiApp.Fit($"{e.SourcePackage}/{e.Name}  [{TypeOf(e)}]  ← {string.Join(", ", e.RequiredBy)}", 8)));
        foreach (var e in _refs!) prompt.AddChoice(e).Select();
        var chosen = AnsiConsole.Prompt(prompt);
        int added = 0;
        foreach (var e in chosen)
            if (await packages.SelectAsync(e.SourcePackage, e.Kind, e.UId)) added++;
        // The added elements may reference more: the selection now differs from the checked one (RefsWarning says so).
        if (added > 0) _refs = _refs!.Where(r => !chosen.Contains(r)).ToList();
    }

    // ---------- mapping ----------

    private async Task<bool> EnsureTargetAsync(string title)
    {
        if (_target != null) return true;
        var t = await envs.PickAsync(StandPurpose.Target, title, exclude: source.Name);
        if (t == null) return false;
        await SetTargetAsync(t);
        return true;
    }

    private async Task SetTargetAsync(StandSession t)
    {
        _target = t;
        await WithProgressAsync($"Loading packages of {t.Name}...", async () => _targetPackages = await new Catalog(t.Client).LoadPackagesAsync());
        // Remembered mapping for this pair of stands (only where the user has not mapped in this session).
        _remembered.Clear();
        if (state.Mappings.TryGetValue(UiState.MappingKey(source.Name, t.Name), out var saved))
            foreach (var (src, dst) in saved)
                if (!_mapping.ContainsKey(src)) { _mapping[src] = dst; _remembered.Add(src); }
    }

    private async Task EditMappingAsync()
    {
        if (!await EnsureTargetAsync("Map packages for which target stand?")) return;
        const string done = "\u0000done", change = "\u0000change";
        while (true)
        {
            var (elements, _) = Lists();
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"Map packages to [bold]{Markup.Escape(_target!.Name)}[/]:")
                .PageSize(15)
                .UseConverter(c => c == done ? "[green]Done[/]" : c == change ? "[grey]Change the target stand…[/]"
                    : Markup.Escape(UiApp.Fit($"{c}  →  {Map(c)}", 8)))
                .AddChoices(new[] { done }.Concat(elements.Select(p => p.Source)).Append(change)));
            if (choice == done) return;
            if (choice == change)
            {
                var t = await envs.PickAsync(StandPurpose.Target, "Map packages for which target stand?", exclude: source.Name);
                if (t != null) { _mapping.Clear(); await SetTargetAsync(t); }
                continue;
            }
            var target = PickTargetPackage(choice);
            if (target == null) continue;
            _mapping[choice] = target;
            _remembered.Remove(choice);
            var key = UiState.MappingKey(source.Name, _target.Name);
            if (!state.Mappings.TryGetValue(key, out var saved)) state.Mappings[key] = saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            saved[choice] = target;
        }
    }

    private string? PickTargetPackage(string sourcePackage)
    {
        const string same = "\u0000same", typed = "\u0000typed", back = "\u0000back";
        var names = (_targetPackages ?? []).OrderBy(p => p.IsOwn ? 0 : 1).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => p.Name);
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"Target package for [bold]{Markup.Escape(sourcePackage)}[/] [grey](type to search)[/]:")
            .PageSize(15)
            .EnableSearch()
            .UseConverter(c => c switch
            {
                same => $"[grey]same name:[/] {Markup.Escape(sourcePackage)}",
                typed => "[grey]Type a name…[/]",
                back => "[grey]← Back[/]",
                _ => Markup.Escape(c)
            })
            .AddChoices(new[] { same, typed }.Concat(names).Append(back)));
        return choice switch
        {
            back => null,
            same => sourcePackage,
            typed => AnsiConsole.Prompt(new TextPrompt<string>("Package name:").Validate(n =>
                System.Text.RegularExpressions.Regex.IsMatch(n, @"^[\w.]+$") ? ValidationResult.Success() : ValidationResult.Error("Letters, digits, '_' and '.' only."))),
            _ => choice
        };
    }

    // ---------- actions ----------

    private string DefaultPath(string folder, string ext) =>
        Path.Combine(baseDir, folder, $"{source.Name}-{DateTime.Now:yyyyMMdd-HHmm}{ext}");

    private static string AskPath(string question, string def) =>
        AnsiConsole.Prompt(new TextPrompt<string>(question).DefaultValue(def).ShowDefaultValue(true)).Trim().Trim('"');

    private void SaveList()
    {
        var path = AskPath("Save the list as", DefaultPath("lists", ".txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ListText());
        UiState.Remember(state.RecentLists, Path.GetFullPath(path));
        _kept = true;
        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(Path.GetFullPath(path))}");
        WarnRefs();
        UiInput.Pause();
    }

    /// <summary>Writes the archive; returns its full path, or null when the export failed (errors shown).</summary>
    private async Task<string?> ExportArchiveAsync(bool pause = true)
    {
        var path = Path.GetFullPath(AskPath("Write the archive to", DefaultPath("archives", ".zip")));
        var (elements, whole) = Lists();
        Manifest? manifest = null;
        await WithProgressAsync("Exporting...", async () =>
            manifest = await new Exporter(source.Client) { PackageCache = _packageCache }.ExportAsync(elements, path, FileEntries(), whole));
        UiState.Remember(state.RecentArchives, path);
        _kept = true;
        var m = manifest!;
        AnsiConsole.MarkupLine($"[green]Archive written:[/] {Markup.Escape(path)}");
        WarnRefs();
        AnsiConsole.MarkupLine($"  {m.WholePackages.Count} whole package(s), {m.Items.Count} element(s), {m.Files.Count} file(s); "
            + $"{m.External.Count} required on the target");
        foreach (var w in m.Warnings) AnsiConsole.MarkupLine($"  [yellow]Warning:[/] {Markup.Escape(w)}");
        if (pause) UiInput.Pause();
        return path;
    }

    private async Task ImportAsync()
    {
        if (!await EnsureTargetAsync("Import into which stand?")) return;
        var archive = await ExportArchiveAsync(pause: false);
        if (archive == null) return;
        AnsiConsole.MarkupLine($"[yellow]The import wizard is not implemented yet[/] (next step). The archive for {Markup.Escape(_target!.Name)} is ready.");
        UiInput.Pause();
    }

    /// <summary>Runs work under a spinner that shows the latest progress line of cst.</summary>
    private async Task WithProgressAsync(string title, Func<Task> work)
    {
        await AnsiConsole.Status().StartAsync(title, async ctx =>
        {
            void OnInfo(string line) => ctx.Status(Markup.Escape(UiApp.Fit(line, 6)));
            log.InfoWritten += OnInfo;
            var quiet = log.Quiet;
            log.Quiet = true; // warnings printed under a spinner garble it; they stay in the log buffer
            try { await work(); }
            finally { log.InfoWritten -= OnInfo; log.Quiet = quiet; }
        });
    }
}
