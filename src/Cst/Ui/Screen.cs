using Spectre.Console;

namespace Cst.Ui;

/// <summary>A piece of a screen line: plain text plus an optional Spectre style ("green", "bold yellow", ...).</summary>
public readonly record struct Seg(string Text, string? Style = null);

/// <summary>
/// Full-screen redraw without flicker or scrolling: lines are cut to the window width, padded, and written from the
/// top-left corner; the last window row stays empty so the console never scrolls. Works in conhost (PowerShell 5.1)
/// and Windows Terminal alike. On a scripted console (no real window) each redraw is written as a new frame.
/// </summary>
public sealed class Screen
{
    private int _width = -1, _height = -1;

    public int Width => Math.Max(20, UiInput.Width - 1);
    public int Height => Math.Max(8, UiInput.Height - 1);

    public void Render(IReadOnlyList<IReadOnlyList<Seg>> lines)
    {
        int w = UiInput.Width, h = UiInput.Height;
        int width = Math.Max(20, w - 1), rows = Math.Max(8, h - 1);
        if (!UiInput.IsRealConsole)
        {
            AnsiConsole.WriteLine("──── frame ────");
            foreach (var line in lines.Take(rows)) AnsiConsole.MarkupLine(ToMarkup(line, width).TrimEnd());
            return;
        }
        if (w != _width || h != _height)
        {
            AnsiConsole.Clear();
            _width = w; _height = h;
        }
        try { Console.CursorVisible = false; } catch (IOException) { }
        try { Console.SetCursorPosition(0, 0); } catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException) { AnsiConsole.Clear(); }
        for (int i = 0; i < rows; i++)
        {
            IReadOnlyList<Seg> line = i < lines.Count ? lines[i] : Array.Empty<Seg>();
            AnsiConsole.Markup(ToMarkup(line, width));
            if (i < rows - 1) AnsiConsole.WriteLine();
        }
    }

    /// <summary>Restores the cursor after the browser ends.</summary>
    public void Leave()
    {
        if (UiInput.IsRealConsole)
        {
            try { Console.CursorVisible = true; } catch (IOException) { }
        }
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
}
