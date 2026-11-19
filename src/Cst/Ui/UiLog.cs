using Spectre.Console;

namespace Cst.Ui;

/// <summary>
/// Log sink of the console UI: keeps the recent lines in memory (for the log view) and, when a file is set
/// (an import's work dir), appends every line there. Warnings and errors are printed at once unless
/// <see cref="Quiet"/> (a live progress view draws them itself).
/// </summary>
public sealed class UiLog
{
    private const int MaxLines = 2000;
    private readonly object _lock = new();
    private readonly LinkedList<(LogLevel Level, string Text)> _lines = new();

    public string? FilePath { get; set; }
    public bool Quiet { get; set; }
    /// <summary>Last stage reported by <see cref="Log.Stage"/> and when it started.</summary>
    public (string Name, DateTime Started)? Stage { get; private set; }
    public event Action<string>? StageChanged;
    /// <summary>Every Info line (a progress view shows the latest one).</summary>
    public event Action<string>? InfoWritten;

    public void Write(LogLevel level, string text)
    {
        lock (_lock)
        {
            if (level == LogLevel.Stage)
            {
                Stage = (text, DateTime.Now);
            }
            _lines.AddLast((level, text));
            if (_lines.Count > MaxLines) _lines.RemoveFirst();
            if (FilePath != null)
            {
                try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss} {level,-5} {text}{Environment.NewLine}"); }
                catch (IOException) { /* the log file is a convenience; never stop the work for it */ }
            }
        }
        if (level == LogLevel.Stage) StageChanged?.Invoke(text);
        if (level == LogLevel.Info) InfoWritten?.Invoke(text);
        if (Quiet) return;
        if (level == LogLevel.Warn) AnsiConsole.MarkupLine($"[yellow]WARN:[/] {Markup.Escape(text)}");
        else if (level == LogLevel.Error) AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(text)}");
    }

    public List<(LogLevel Level, string Text)> Tail(int count)
    {
        lock (_lock) return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
    }
}
