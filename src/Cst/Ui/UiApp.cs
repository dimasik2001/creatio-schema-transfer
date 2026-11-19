using System.Text;
using Cst.Config;
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
                    Export => "Export    [grey]select schemas and files on a stand, save a list or an archive[/]",
                    Import => "Import    [grey]install an archive or a list into a stand[/]",
                    Restore => "Restore   [grey]roll a stand back from an import's backup folder[/]",
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
        AnsiConsole.MarkupLine(source.HasPath
            ? "Tabs: [bold]Packages[/] · [bold]Files[/]"
            : "Tabs: [bold]Packages[/] [grey](no \"Path\" for this stand: no Files tab)[/]");
        NotYet("Selecting packages and files");
    }

    private async Task ImportAsync()
    {
        var target = await _envs.PickAsync(StandPurpose.Target, "Import into which stand?");
        if (target == null) return;
        Header(target, "Import");
        NotYet("The import wizard");
    }

    private async Task RestoreAsync()
    {
        var target = await _envs.PickAsync(StandPurpose.Target, "Restore which stand?");
        if (target == null) return;
        Header(target, "Restore");
        NotYet("Restore");
    }

    private static void NotYet(string what)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(what)} is not implemented yet.[/]");
        UiInput.Pause("Press any key to return to the menu...");
    }

    private void SaveState()
    {
        if (_state.Save(_statePath) is { } error) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(error)}[/]");
    }
}
