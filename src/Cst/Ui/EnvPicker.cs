using System.Net.Http;
using Cst.Config;
using Cst.Creatio;
using Spectre.Console;

namespace Cst.Ui;

/// <summary>A stand the UI is logged in to. The password lives only in <see cref="Settings"/> in memory.</summary>
public sealed class StandSession(string name, EnvironmentSettings settings, CreatioClient client, string coreVersion) : IDisposable
{
    public string Name { get; } = name;
    public EnvironmentSettings Settings { get; } = settings;
    public CreatioClient Client { get; } = client;
    public string CoreVersion { get; } = coreVersion;
    public string DbEngine => string.IsNullOrWhiteSpace(Settings.DbEngine) ? "DbEngine not set" : Settings.DbEngine!;
    public bool HasPath => !string.IsNullOrWhiteSpace(Settings.Path);

    /// <summary>Header text: "dev · 10.0.0.801 · MSSql".</summary>
    public string Header => $"{Name} · {CoreVersion} · {DbEngine}";

    public void Dispose() => Client.Dispose();
}

public enum StandPurpose { Source, Target }

/// <summary>Chooses a stand from appsettings.json and logs in (asking for the password when needed).</summary>
public sealed class EnvPicker(AppSettings settings, UiState state)
{
    private readonly Dictionary<string, StandSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<StandSession> Sessions => _sessions.Values;

    /// <summary>
    /// Shows the stands (the last used one first) and connects to the chosen one. Target stands need DbEngine.
    /// Returns null when the user goes back.
    /// </summary>
    public async Task<StandSession?> PickAsync(StandPurpose purpose, string title, string? exclude = null)
    {
        if (settings.Environments.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No environments in appsettings.json.[/] Add one under \"Environments\" (see appsettings.example.json).");
            UiInput.Pause();
            return null;
        }
        var last = purpose == StandPurpose.Source ? state.LastSource : state.LastTarget;
        var names = settings.Environments.Keys
            .Where(n => exclude == null || !n.Equals(exclude, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Equals(last, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        const string back = "\u0000back";
        while (true)
        {
            var prompt = new SelectionPrompt<string>()
                .Title(title)
                .PageSize(15)
                .UseConverter(n => n == back ? "[grey]← Back[/]" : Describe(n, purpose))
                .AddChoices(names.Append(back));
            var name = AnsiConsole.Prompt(prompt);
            if (name == back) return null;

            if (purpose == StandPurpose.Target && !HasDbEngine(settings.Environments[name], out var why))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(name)} cannot be an import target:[/] {Markup.Escape(why)}");
                continue;
            }
            var session = await ConnectAsync(name);
            if (session == null) continue;
            if (purpose == StandPurpose.Source) state.LastSource = name; else state.LastTarget = name;
            return session;
        }
    }

    private string Describe(string name, StandPurpose purpose)
    {
        var e = settings.Environments[name];
        var db = HasDbEngine(e, out _) ? e.DbEngine! : "[red]no DbEngine[/]";
        var path = string.IsNullOrWhiteSpace(e.Path) ? "" : " · [green]files[/]";
        var connected = _sessions.ContainsKey(name) ? " [green]●[/]" : "";
        var line = $"[bold]{Markup.Escape(name)}[/]{connected}  [grey]{Markup.Escape(e.Url)}[/] · {db}{path}";
        if (purpose == StandPurpose.Target && !HasDbEngine(e, out _)) line = $"[grey]{Markup.Escape(Markup.Remove(line))}[/] [red](no DbEngine)[/]";
        // A wrapped choice breaks the prompt's redraw: fall back to plain text cut to the window width.
        var plain = Markup.Remove(line);
        return plain.Length > UiApp.Fit(plain).Length ? Markup.Escape(UiApp.Fit(plain)) : line;
    }

    private static bool HasDbEngine(EnvironmentSettings e, out string why)
    {
        try { EnvironmentSettings.ParseDbEngine(e.DbEngine, "this environment"); why = ""; return true; }
        catch (ArgumentException ex) { why = ex.Message; return false; }
    }

    /// <summary>Logs in to the stand (reusing an open session). Asks for the password while it is wrong; Esc goes back.</summary>
    public async Task<StandSession?> ConnectAsync(string name)
    {
        if (_sessions.TryGetValue(name, out var open)) return open;
        EnvironmentSettings env;
        bool cancelled = false;
        try
        {
            env = settings.Resolve(name, null, null, null, askPassword: n =>
            {
                var p = UiInput.ReadSecret($"Password for [bold]{Markup.Escape(n ?? "?")}[/] [grey](Esc: back)[/]: ");
                cancelled = p == null;
                return p;
            });
        }
        catch (ArgumentException ex)
        {
            if (cancelled) return null;
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return null;
        }

        int failures = 0;
        while (true)
        {
            var client = new CreatioClient(env);
            try
            {
                string core = "";
                await AnsiConsole.Status().StartAsync($"Connecting to {Markup.Escape(env.Url)} as {Markup.Escape(env.Login)}...", async _ =>
                {
                    await client.LoginAsync();
                    core = (await client.GetAppInfoAsync()).CoreVersion;
                });
                var session = new StandSession(name, env, client, core);
                _sessions[name] = session;
                AnsiConsole.MarkupLine($"[green]Connected:[/] {Markup.Escape(session.Header)}");
                return session;
            }
            catch (LoginException ex) when (ex.Reason == LoginFailure.InvalidCredentials)
            {
                client.Dispose();
                failures++;
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                if (failures >= 2)
                    AnsiConsole.MarkupLine("[yellow]Creatio may lock the user after several failed logins.[/]");
                var p = UiInput.ReadSecret($"Password for [bold]{Markup.Escape(env.Login)}[/] on [bold]{Markup.Escape(name)}[/] [grey](Esc: back)[/]: ");
                if (p == null) return null;
                env.Password = p;
            }
            catch (LoginException ex) when (ex.Reason is LoginFailure.Locked or LoginFailure.PasswordExpired)
            {
                client.Dispose();
                var what = ex.Reason == LoginFailure.Locked ? "The user is locked on this stand." : "The password has expired; change it in Creatio first.";
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(what)}[/] [grey]{Markup.Escape(ex.Message)}[/]");
                UiInput.Pause();
                return null;
            }
            catch (Exception ex) when (ex is CreatioException or HttpRequestException or TaskCanceledException or UriFormatException)
            {
                client.Dispose();
                AnsiConsole.MarkupLine($"[red]Cannot connect to {Markup.Escape(name)}:[/] {Markup.Escape(ex.Message)}");
                var again = AnsiConsole.Prompt(new SelectionPrompt<string>().AddChoices("Retry", "Back"));
                if (again == "Back") return null;
            }
        }
    }

    public void DisposeAll()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
