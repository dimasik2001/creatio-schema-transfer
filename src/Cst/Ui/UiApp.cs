using System.Text;
using Cst.Config;
using Cst.Creatio;
using Spectre.Console;
using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>
/// The console UI: cst started without arguments (or only with --settings) in an interactive console.
/// Main menu: Export · Import · Restore · Language · Quit. Uses the same classes as the CLI, never cst as a process.
/// </summary>
public sealed class UiApp
{
    private readonly string _settingsPath;
    private readonly AppSettings _settings;
    private readonly string _statePath;
    private readonly UiState _state;
    private readonly UiLog _log = new();
    private readonly EnvPicker _envs;

    private UiApp(string settingsPath, AppSettings settings, string statePath, UiState state)
    {
        _settingsPath = settingsPath;
        _settings = settings;
        _statePath = statePath;
        _state = state;
        _envs = new EnvPicker(settings, state);
    }

    public static async Task<int> RunAsync(string? settingsPath)
    {
        settingsPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Encoding? previousEncoding = null;
        try { previousEncoding = Console.OutputEncoding; Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { /* keep the console's encoding */ }

        var previousSink = Log.Sink;
        var previousLanguage = L.Language;
        UiApp? app = null;
        try
        {
            var statePath = UiState.PathNextTo(settingsPath);
            var state = UiState.Load(statePath, out var warning);
            L.Language = L.Parse(state.Language);
            AppSettings settings;
            try { settings = AppSettings.Load(settingsPath); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(F("Cannot read {0}:", settingsPath))}[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
            app = new UiApp(settingsPath, settings, statePath, state);
            Log.Sink = app._log.Write;
            Console.CancelKeyPress += app.OnCancel;
            if (warning != null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
            return await app.MainLoopAsync();
        }
        finally
        {
            if (app != null) Console.CancelKeyPress -= app.OnCancel;
            Log.Sink = previousSink;
            L.Language = previousLanguage;
            app?._envs.DisposeAll();
            if (previousEncoding != null)
            {
                try { Console.OutputEncoding = previousEncoding; } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Ctrl+C ends cst as before, but first makes the cursor visible again, keeps the remembered choices and says
    /// where an interrupted import left its backups. An import writes restore.json as it goes, so Restore works.
    /// </summary>
    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        try { Console.CursorVisible = true; } catch (IOException) { }
        _state.Save(_statePath);
        Console.WriteLine();
        Console.WriteLine(T("cst: interrupted."));
        if (_log.FilePath is { } logFile)
            Console.WriteLine(F("An import was running; its backups and restore.json are in {0} (main menu → Restore).", Path.GetDirectoryName(logFile)));
    }

    private const string Export = "Export", Import = "Import", Restore = "Restore", Language = "Language", Quit = "Quit";

    private async Task<int> MainLoopAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            Title();
            var col = NameColumn(T("Export"), T("Import"), T("Restore"), T("Language"));
            var choice = AnsiConsole.Prompt(Select<string>()
                .Title(T("What do you want to do?"))
                .UseConverter(c => c switch
                {
                    Export => MenuLine(T("Export"), T("select schemas and files, save a list or an archive"), col),
                    Import => MenuLine(T("Import"), T("install an archive or a list into a stand"), col),
                    Restore => MenuLine(T("Restore"), T("roll a stand back from an import's backup"), col),
                    Language => MenuLine(T("Language"), L.Language == UiLanguage.Ukrainian ? "Українська · English" : "English · Українська", col),
                    _ => Markup.Escape(T("Quit"))
                })
                .AddChoices(Export, Import, Restore, Language, Quit));
            try
            {
                switch (choice)
                {
                    case Export: await ExportAsync(); break;
                    case Import: await ImportAsync(); break;
                    case Restore: await RestoreAsync(); break;
                    case Language: ChooseLanguage(); break;
                    default:
                        SaveState();
                        return 0;
                }
            }
            catch (Exception ex)
            {
                // A failure in one flow returns to the menu instead of ending the session.
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(T("Unexpected error:"))}[/] {Markup.Escape(ex.Message)}");
                Log.Error(ex.ToString());
                UiInput.Pause();
            }
            SaveState();
        }
    }

    private void ChooseLanguage()
    {
        var choice = AnsiConsole.Prompt(Select<UiLanguage>()
            .Title(T("Language of the menus:"))
            .UseConverter(l => l == UiLanguage.Ukrainian ? "Українська" : "English")
            .AddChoices(L.Language, L.Language == UiLanguage.English ? UiLanguage.Ukrainian : UiLanguage.English));
        L.Language = choice;
        _state.Language = L.Code;
    }

    /// <summary>Width of the name column of a menu: the longest name plus two spaces.</summary>
    public static int NameColumn(params string[] names) => names.Max(n => n.Length) + 2;

    /// <summary>
    /// A menu choice that never wraps: a wrapped line breaks the prompt's redraw (the menu is printed again
    /// below itself in narrow windows). The grey description is cut to the window width.
    /// </summary>
    public static string MenuLine(string name, string description, int nameColumn = 10)
    {
        int width = UiInput.Width;
        int room = width - 2 /* "> " */ - nameColumn - 2;
        if (room < 8) return Markup.Escape(name);
        if (description.Length > room) description = description.Substring(0, room - 1) + "…";
        return $"{Markup.Escape(name.PadRight(nameColumn))}[grey]{Markup.Escape(description)}[/]";
    }

    /// <summary>Cuts plain text to fit one console line (for prompt choices and titles).</summary>
    public static string Fit(string text, int reserve = 4)
    {
        int width;
        width = UiInput.Width;
        int room = Math.Max(10, width - reserve);
        return text.Length <= room ? text : text.Substring(0, room - 1) + "…";
    }

    private void Title()
    {
        AnsiConsole.Write(new Rule("[bold]cst[/] [grey]· Creatio Schema Transfer[/]").LeftJustified());
        var connected = _envs.Sessions.Select(s => s.Header).ToList();
        AnsiConsole.MarkupLine(connected.Count == 0
            ? $"[grey]{Markup.Escape(F("Settings: {0}", _settingsPath))}[/]"
            : $"[grey]{Markup.Escape(F("Connected: {0}", string.Join("  |  ", connected)))}[/]");
        AnsiConsole.WriteLine();
    }

    private void Header(StandSession s, string flow)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(flow)}[/] [grey]·[/] {Markup.Escape(s.Header)}").LeftJustified());
    }

    private async Task ExportAsync()
    {
        var source = await _envs.PickAsync(StandPurpose.Source, T("Export from which stand?"));
        if (source == null) return;
        Header(source, T("Export"));
        var catalog = new Catalog(source.Client);
        List<PackageInfo> packages = [];
        await AnsiConsole.Status().StartAsync(Markup.Escape(T("Loading packages...")), async _ => packages = await catalog.LoadPackagesAsync());

        var packageTab = new PackageSource(catalog, packages);
        var tabs = new List<IBrowserSource> { packageTab };
        FileSource? fileTab = source.HasPath ? new FileSource(source.Settings.Path!) : null;
        if (fileTab != null) tabs.Add(fileTab);

        var baseDir = BaseDir;
        var summary = new SummaryScreen(source, packages, packageTab, fileTab, _envs, _state, _log, baseDir);

        // Manual selection, or a saved list loaded into the same selection (then straight to the summary).
        const string manual = "manual", fromList = "list";
        var col = NameColumn(T("Select"), T("Load a list"));
        var how = AnsiConsole.Prompt(Select<string>()
            .Title(T("What to export?"))
            .UseConverter(c => c == manual
                ? MenuLine(T("Select"), T("mark packages, schemas and files by hand"), col)
                : MenuLine(T("Load a list"), T("take a saved list (package/packages/files sections)"), col))
            .AddChoices(manual, fromList));
        bool skipBrowser = false;
        if (how == fromList)
        {
            skipBrowser = await LoadListAsync(source, packageTab, fileTab, summary);
            if (!skipBrowser && !UiInput.Confirm(T("Continue with manual selection?"), true)) return;
        }

        while (true)
        {
            if (!skipBrowser)
            {
                var result = await new SelectionBrowser(source.Header, tabs).RunAsync();
                if (result == BrowserResult.Back) return;
            }
            skipBrowser = false;
            if (await summary.RunAsync() == SummaryResult.MainMenu) return;
            summary.SelectionChanged();
        }
    }

    /// <summary>
    /// Loads a schema list into the selection: element sections (with their mapping), whole packages and files.
    /// Shows what could not be applied. Returns true when something was loaded (go to the summary).
    /// </summary>
    private async Task<bool> LoadListAsync(StandSession source, PackageSource packageTab, FileSource? fileTab, SummaryScreen summary)
    {
        var path = FilePicker.Pick(T("Which list to load?"), _state, BaseDir, archives: false, lists: true);
        if (path == null) return false;
        List<SchemaListPackage> sections;
        List<string> whole, files;
        try
        {
            sections = SchemaList.ParseFile(path);
            whole = SchemaList.ParseWholePackagesSection(path);
            files = SchemaList.ParseFilesSection(path);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(T("Cannot read the list:"))}[/] {Markup.Escape(ex.Message)}");
            UiInput.Pause();
            return false;
        }
        var problems = new List<string>();
        await AnsiConsole.Status().StartAsync(Markup.Escape(F("Loading {0}...", Path.GetFileName(path))), async _ =>
            problems = await packageTab.ApplyListAsync(sections, whole));
        if (files.Count > 0)
        {
            if (fileTab == null) problems.Add(F("files: section ({0} entries) skipped: {1} has no \"Path\" (Creatio folder) in appsettings.json", files.Count, source.Name));
            else foreach (var f in files) fileTab.Selection.Add(f);
        }
        summary.ApplyMapping(sections);
        UiState.Remember(_state.RecentLists, path);

        int elements = sections.Sum(s => s.Entries.Count);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(T("Loaded"))}[/] " + Markup.Escape(F("{0}: element entries: {1}, whole packages: {2}, file entries: {3}.",
            Path.GetFileName(path), elements, whole.Count, fileTab == null ? 0 : files.Count)));
        if (problems.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(F("Not applied ({0}):", problems.Count))}[/]");
            foreach (var p in problems) AnsiConsole.MarkupLine($"  [yellow]•[/] {Markup.Escape(p)}");
        }
        bool any = packageTab.HasSelection || (fileTab?.HasSelection ?? false);
        if (!any) AnsiConsole.MarkupLine($"[red]{Markup.Escape(T("Nothing from the list exists on this stand."))}[/]");
        UiInput.Pause(any ? T("Press any key to open the summary...") : T("Press any key to continue..."));
        return any;
    }

    private string BaseDir => Path.GetDirectoryName(Path.GetFullPath(_settingsPath)) ?? AppContext.BaseDirectory;

    private Task ImportAsync() => new ImportWizard(_envs, _state, _log, BaseDir).RunAsync();

    private async Task RestoreAsync()
    {
        var target = await _envs.PickAsync(StandPurpose.Target, T("Restore which stand?"));
        if (target == null) return;
        Header(target, T("Restore"));
        const string typed = "\u0000typed", back = "\u0000back";
        var dirs = _state.RecentWorkDirs.Where(d => File.Exists(Transfer.Restorer.InfoPath(d))).ToList();
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(T("Restore from which import work dir?"))
            .PageSize(12)
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type a path…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Markup.Escape(Fit($"{Directory.GetLastWriteTime(c):yyyy-MM-dd HH:mm}  {c}", 6))
            })
            .AddChoices(dirs.Append(typed).Append(back)));
        if (choice == back) return;
        if (choice == typed)
            choice = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Work dir (holds restore.json):"))).Validate(p =>
                File.Exists(Transfer.Restorer.InfoPath(p.Trim().Trim('"'))) ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("No restore.json there."))))).Trim().Trim('"');
        await new RestoreFlow(_log).RunAsync(target, choice);
    }

    private void SaveState()
    {
        if (_state.Save(_statePath) is { } error) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(error)}[/]");
    }
}
