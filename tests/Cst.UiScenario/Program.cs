// Runs the cst console UI against real stands with keys from a scenario file and writes what it drew to a
// transcript (ANSI codes removed). Usage: Cst.UiScenario <appsettings.json> <scenario.keys> <transcript.txt> [width] [height]
//
// Scenario file: one step per line, '#' starts a comment.
//   Up Down Left Right Enter Space Tab Esc Backspace Home End PageUp PageDown F2   - keys
//   Ctrl+A  Ctrl+S  Shift+Tab                                                      - with modifiers
//   text:some text                                                                 - types the characters
//   wait:Name                                                                      - marks the transcript (no key)
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Cst.UiScenario <appsettings.json> <scenario.keys> <transcript.txt> [width] [height]");
    return 1;
}
int width = args.Length > 3 ? int.Parse(args[3]) : 120, height = args.Length > 4 ? int.Parse(args[4]) : 40;

var writer = new StringWriter();
var inner = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.Yes,
    ColorSystem = ColorSystemSupport.NoColors,
    Interactive = InteractionSupport.Yes,
    Out = new AnsiConsoleOutput(writer)
});
inner.Profile.Width = width;
inner.Profile.Height = height;
var input = new ScriptedInput(File.ReadAllLines(args[1]), writer);
AnsiConsole.Console = new ScriptedConsole(inner, input);

int code;
string? failure = null;
try { code = await Cst.Ui.UiApp.RunAsync(args[0]); }
catch (EndOfStreamException) { code = -1; failure = "scenario ended (no more keys) while the UI waited for input"; }
catch (Exception ex) { code = -2; failure = ex.ToString(); }

var text = Regex.Replace(writer.ToString(), @"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07", "");
text = Regex.Replace(text, @"\r(?!\n)", "\n");
File.WriteAllText(args[2], text + $"\n==== exit: {code}{(failure != null ? " — " + failure : "")}\n", Encoding.UTF8);
Console.WriteLine($"exit {code}; keys used {input.Used}/{input.Total}; transcript {args[2]}{(failure != null ? "; " + failure.Split('\n')[0] : "")}");
return 0;

sealed class ScriptedConsole(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
{
    public Profile Profile => inner.Profile;
    public IAnsiConsoleCursor Cursor => inner.Cursor;
    public IAnsiConsoleInput Input => input;
    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
    public RenderPipeline Pipeline => inner.Pipeline;
    public void Clear(bool home) { inner.Clear(home); inner.WriteLine(); inner.WriteLine("════ clear ════"); }
    public void Write(IRenderable renderable) => inner.Write(renderable);
}

sealed class ScriptedInput : IAnsiConsoleInput
{
    private readonly Queue<(ConsoleKeyInfo? Key, string? Mark)> _keys = new();
    private readonly StringWriter _log;
    public int Total { get; }
    public int Used { get; private set; }

    public ScriptedInput(IEnumerable<string> lines, StringWriter log)
    {
        _log = log;
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("text:")) { foreach (var c in line[5..]) _keys.Enqueue((Char(c), null)); continue; }
            if (line.StartsWith("wait:")) { _keys.Enqueue((null, line[5..])); continue; }
            _keys.Enqueue((Parse(line), null));
        }
        Total = _keys.Count(k => k.Key != null);
    }

    private static ConsoleKeyInfo Char(char c) =>
        new(c, char.IsLetter(c) ? (ConsoleKey)char.ToUpperInvariant(c) : char.IsDigit(c) ? ConsoleKey.D0 + (c - '0') : c == ' ' ? ConsoleKey.Spacebar : ConsoleKey.Oem1,
            char.IsUpper(c), false, false);

    private static ConsoleKeyInfo Parse(string s)
    {
        bool ctrl = false, shift = false;
        foreach (var mod in new[] { "Ctrl+", "Shift+" })
            while (s.StartsWith(mod, StringComparison.OrdinalIgnoreCase)) { if (mod == "Ctrl+") ctrl = true; else shift = true; s = s[mod.Length..]; }
        var key = s.ToLowerInvariant() switch
        {
            "esc" => ConsoleKey.Escape,
            "space" => ConsoleKey.Spacebar,
            "up" => ConsoleKey.UpArrow, "down" => ConsoleKey.DownArrow, "left" => ConsoleKey.LeftArrow, "right" => ConsoleKey.RightArrow,
            _ => Enum.Parse<ConsoleKey>(s, ignoreCase: true)
        };
        char ch = key switch
        {
            ConsoleKey.Enter => '\r', ConsoleKey.Spacebar => ' ', ConsoleKey.Tab => '\t', ConsoleKey.Escape => '\u001b', ConsoleKey.Backspace => '\b',
            _ when ctrl && key >= ConsoleKey.A && key <= ConsoleKey.Z => (char)(key - ConsoleKey.A + 1),
            _ => '\0'
        };
        return new ConsoleKeyInfo(ch, key, shift, false, ctrl);
    }

    public bool IsKeyAvailable() => _keys.Count > 0;

    public ConsoleKeyInfo? ReadKey(bool intercept)
    {
        while (_keys.Count > 0)
        {
            var (key, mark) = _keys.Dequeue();
            if (mark != null) { _log.WriteLine(); _log.WriteLine($"▶▶ {mark}"); continue; }
            Used++;
            _log.WriteLine();
            _log.WriteLine($"▶ key {Describe(key!.Value)}");
            return key;
        }
        throw new EndOfStreamException();
    }

    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) => Task.FromResult(ReadKey(intercept));

    private static string Describe(ConsoleKeyInfo k) =>
        ((k.Modifiers & ConsoleModifiers.Control) != 0 ? "Ctrl+" : "") + ((k.Modifiers & ConsoleModifiers.Shift) != 0 ? "Shift+" : "")
        + (char.IsLetterOrDigit(k.KeyChar) ? $"'{k.KeyChar}'" : k.Key.ToString());
}
