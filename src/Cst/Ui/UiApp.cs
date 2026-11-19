using System.Text;
using Cst.Config;
using Cst.Creatio;
using Spectre.Console;

namespace Cst.Ui;

/// <summary>
/// The console UI: cst started without arguments (or only with --settings) in an interactive console.
/// Main menu: Export · Import · Restore · Quit. Uses the same classes as the CLI, never cst as a process.
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
        UiApp? app = null;
        try
        {
            AppSettings settings;
            try { settings = AppSettings.Load(settingsPath); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]Cannot read {Markup.Escape(settingsPath)}:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
            var statePath = UiState.PathNextTo(settingsPath);
            var state = UiState.Load(statePath, out var warning);
            app = new UiApp(settingsPath, settings, statePath, state);
            Log.Sink = app._log.Write;
            if (warning != null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
            return await app.MainLoopAsync();
        }
        finally
        {
            Log.Sink = previousSink;
            app?._envs.DisposeAll();
            if (previousEncoding != null)
            {
                try { Console.OutputEncoding = previousEncoding; } catch (IOException) { }
            }
        }
    }

    private const string Export = "Export", Import = "Import", Restore = "Restore", Quit = "Quit";

    private async Task<int> MainLoopAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            Title();
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("What do you want to do?")
                .UseConverter(c => c switch
                {
                    Export => MenuLine("Export", "select schemas and files, save a list or an archive"),
                    Import => MenuLine("Import", "install an archive or a list into a stand"),
                    Restore => MenuLine("Restore", "roll a stand back from an import's backup"),
                    _ => "Quit"
                })
                .AddChoices(Export, Import, Restore, Quit));
            try
            {
                switch (choice)
                {
                    case Export: await ExportAsync(); break;
                    case Import: await ImportAsync(); break;
                    case Restore: await RestoreAsync(); break;
                    default:
                        SaveState();
                        return 0;
                }
            }
            catch (Exception ex)
            {
                // A failure in one flow returns to the menu instead of ending the session.
                AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {Markup.Escape(ex.Message)}");
                Log.Error(ex.ToString());
                UiInput.Pause();
            }
            SaveState();
        }
    }

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
            ? $"[grey]Settings: {Markup.Escape(_settingsPath)}[/]"
            : $"[grey]Connected: {Markup.Escape(string.Join("  |  ", connected))}[/]");
        AnsiConsole.WriteLine();
    }

    private void Header(StandSession s, string flow)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(flow)}[/] [grey]·[/] {Markup.Escape(s.Header)}").LeftJustified());
    }

    private async Task ExportAsync()
    {
        var source = await _envs.PickAsync(StandPurpose.Source, "Export from which stand?");
        if (source == null) return;
        Header(source, "Export");
        var catalog = new Catalog(source.Client);
        List<PackageInfo> packages = [];
        await AnsiConsole.Status().StartAsync("Loading packages...", async _ => packages = await catalog.LoadPackagesAsync());

        var packageTab = new PackageSource(catalog, packages);
        var tabs = new List<IBrowserSource> { packageTab };
        FileSource? fileTab = source.HasPath ? new FileSource(source.Settings.Path!) : null;
        if (fileTab != null) tabs.Add(fileTab);

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(_settingsPath)) ?? AppContext.BaseDirectory;
        var summary = new SummaryScreen(source, packages, packageTab, fileTab, _envs, _state, _log, baseDir);
        while (true)
        {
            var result = await new SelectionBrowser(source.Header, tabs).RunAsync();
            if (result == BrowserResult.Back) return;
            if (await summary.RunAsync() == SummaryResult.MainMenu) return;
            summary.SelectionChanged();
        }
    }

    private string BaseDir => Path.GetDirectoryName(Path.GetFullPath(_settingsPath)) ?? AppContext.BaseDirectory;

    private Task ImportAsync() => new ImportWizard(_envs, _state, _log, BaseDir).RunAsync();

    private async Task RestoreAsync()
    {
        var target = await _envs.PickAsync(StandPurpose.Target, "Restore which stand?");
        if (target == null) return;
        Header(target, "Restore");
        const string typed = "\u0000typed", back = "\u0000back";
        var dirs = _state.RecentWorkDirs.Where(d => File.Exists(Transfer.Restorer.InfoPath(d))).ToList();
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Restore from which import work dir?")
            .PageSize(12)
            .UseConverter(c => c switch
            {
                typed => "[grey]Type a path…[/]",
                back => "[grey]← Back[/]",
                _ => Markup.Escape(Fit($"{Directory.GetLastWriteTime(c):yyyy-MM-dd HH:mm}  {c}", 6))
            })
            .AddChoices(dirs.Append(typed).Append(back)));
        if (choice == back) return;
        if (choice == typed)
            choice = AnsiConsole.Prompt(new TextPrompt<string>("Work dir (holds restore.json):").Validate(p =>
                File.Exists(Transfer.Restorer.InfoPath(p.Trim().Trim('"'))) ? ValidationResult.Success() : ValidationResult.Error("No restore.json there."))).Trim().Trim('"');
        await new RestoreFlow(_log).RunAsync(target, choice);
    }

    private void SaveState()
    {
        if (_state.Save(_statePath) is { } error) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(error)}[/]");
    }
}
