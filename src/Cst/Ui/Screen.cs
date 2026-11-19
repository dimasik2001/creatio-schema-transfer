using Spectre.Console;

namespace Cst.Ui;

/// <summary>A piece of a screen line: plain text plus an optional Spectre style ("green", "bold yellow", ...).</summary>
public readonly record struct Seg(string Text, string? Style = null);

/// <summary>
/// Full-screen redraw without flicker or scrolling: lines are cut to the window width, padded, and written from the
/// top-left corner; the last window row stays empty so the console never scrolls. Works in conhost (PowerShell 5.1)
/// and Windows Terminal alike.
/// </summary>
public sealed class Screen
{
    private int _width = -1, _height = -1;

    public int Width => Math.Max(20, SafeWidth() - 1);
    public int Height => Math.Max(8, SafeHeight() - 1);

    public void Render(IReadOnlyList<IReadOnlyList<Seg>> lines)
    {
        int w = SafeWidth(), h = SafeHeight();
        if (w != _width || h != _height)
        {
            AnsiConsole.Clear();
            _width = w; _height = h;
        }
        int width = Math.Max(20, w - 1), rows = Math.Max(8, h - 1);
        try { Console.CursorVisible = false; } catch (IOException) { }
        try { Console.SetCursorPosition(0, 0); } catch (ArgumentOutOfRangeException) { AnsiConsole.Clear(); }
        for (int i = 0; i < rows; i++)
        {
            IReadOnlyList<Seg> line = i < lines.Count ? lines[i] : Array.Empty<Seg>();
            AnsiConsole.Markup(ToMarkup(line, width));
            if (i < rows - 1) Console.WriteLine();
        }
    }

    /// <summary>Restores the cursor after the browser ends.</summary>
    public void Leave()
    {
        try { Console.CursorVisible = true; } catch (IOException) { }
        AnsiConsole.Clear();
        _width = _height = -1;
    }

    private static string ToMarkup(IReadOnlyList<Seg> line, int width)
    {
        var sb = new System.Text.StringBuilder();
        int used = 0;
        foreach (var s in line)
        {
            if (used >= width) break;
            var text = s.Text.Replace('\t', ' ');
            if (used + text.Length > width)
                text = width - used > 1 ? text.Substring(0, width - used - 1) + "…" : text.Substring(0, width - used);
            used += text.Length;
            var esc = Markup.Escape(text);
            sb.Append(s.Style == null ? esc : $"[{s.Style}]{esc}[/]");
        }
        if (used < width) sb.Append(' ', width - used);
        return sb.ToString();
    }

    private static int SafeWidth() { try { return Console.WindowWidth; } catch (IOException) { return 100; } }
    private static int SafeHeight() { try { return Console.WindowHeight; } catch (IOException) { return 30; } }
}
