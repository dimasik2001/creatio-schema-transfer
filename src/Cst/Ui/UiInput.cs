using System.Text;
using Spectre.Console;

namespace Cst.Ui;

/// <summary>
/// Console input and size for the UI. Everything goes through <see cref="AnsiConsole.Console"/>, so a scripted
/// console (tests\Cst.UiScenario) can drive the UI with prepared keys and record what it draws.
/// </summary>
public static class UiInput
{
    /// <summary>
    /// True when cst runs in a real interactive console (keys can be read, output is not redirected).
    /// False for redirected input/output and hosts without a console (PowerShell ISE).
    /// </summary>
    public static bool IsInteractive()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        try { _ = Console.KeyAvailable; return true; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>True when the UI draws into the real console window (not a scripted/recording console).</summary>
    public static bool IsRealConsole => AnsiConsole.Profile.Out.IsTerminal;

    public static int Width
    {
        get
        {
            if (!IsRealConsole) return AnsiConsole.Profile.Width;
            try { return Console.WindowWidth; } catch (IOException) { return 100; }
        }
    }

    public static int Height
    {
        get
        {
            if (!IsRealConsole) return AnsiConsole.Profile.Height;
            try { return Console.WindowHeight; } catch (IOException) { return 30; }
        }
    }

    /// <summary>Reads one key without echo.</summary>
    public static ConsoleKeyInfo ReadKey() =>
        AnsiConsole.Console.Input.ReadKey(intercept: true) ?? throw new EndOfStreamException("No more console input.");

    /// <summary>Reads a password without echo (shows '*'). Esc returns null; Enter on empty input returns "".</summary>
    public static string? ReadSecret(string prompt)
    {
        AnsiConsole.Markup(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = ReadKey();
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    AnsiConsole.WriteLine();
                    return sb.ToString();
                case ConsoleKey.Escape:
                    AnsiConsole.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (sb.Length > 0) { sb.Length--; AnsiConsole.Write("\b \b"); }
                    break;
                default:
                    if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); AnsiConsole.Write("*"); }
                    break;
            }
        }
    }

    /// <summary>Waits for any key (used after messages the user must read).</summary>
    public static void Pause(string? text = null)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(text ?? L.T("Press any key to continue..."))}[/]");
        ReadKey();
    }

    /// <summary>
    /// A yes/no question answered with Y or N and Enter (Enter alone takes the default). Keys, not characters, are
    /// read, so the answer works in any keyboard layout (Y is "н" in the Ukrainian one).
    /// </summary>
    public static bool Confirm(string question, bool defaultValue)
    {
        AnsiConsole.Markup($"{Markup.Escape(question)} [blue]{(defaultValue ? "[[Y/n]]" : "[[y/N]]")}[/] ");
        bool? answer = null;
        while (true)
        {
            var k = ReadKey();
            switch (k.Key)
            {
                case ConsoleKey.Y or ConsoleKey.N:
                    if (answer != null) AnsiConsole.Write("\b \b");
                    answer = k.Key == ConsoleKey.Y;
                    AnsiConsole.Write(answer.Value ? "y" : "n");
                    break;
                case ConsoleKey.Backspace when answer != null:
                    AnsiConsole.Write("\b \b");
                    answer = null;
                    break;
                case ConsoleKey.Enter:
                    AnsiConsole.WriteLine();
                    return answer ?? defaultValue;
            }
        }
    }
}
