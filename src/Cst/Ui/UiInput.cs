using System.Text;
using Spectre.Console;

namespace Cst.Ui;

/// <summary>Small console input helpers the Spectre prompts do not cover.</summary>
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

    /// <summary>Reads a password without echo (shows '*'). Esc returns null; Enter on empty input returns "".</summary>
    public static string? ReadSecret(string prompt)
    {
        AnsiConsole.Markup(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return sb.ToString();
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                    break;
                default:
                    if (!char.IsControl(k.KeyChar)) { sb.Append(k.KeyChar); Console.Write('*'); }
                    break;
            }
        }
    }

    /// <summary>Waits for any key (used after messages the user must read).</summary>
    public static void Pause(string text = "Press any key to continue...")
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(text)}[/]");
        Console.ReadKey(intercept: true);
    }
}
