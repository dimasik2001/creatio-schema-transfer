namespace Cst;

public enum LogLevel { Debug, Info, Warn, Error, Stage }

/// <summary>
/// Progress goes to stderr so stdout stays clean for the result (text or --json).
/// A host (the console UI) can replace the output with <see cref="Sink"/>: every message, and every
/// <see cref="Stage"/> boundary of a long operation, then goes there instead of the console.
/// </summary>
public static class Log
{
    public static bool Verbose { get; set; }

    /// <summary>Replaces console output when set. Debug messages reach it only when <see cref="Verbose"/> is on.</summary>
    public static Action<LogLevel, string>? Sink { get; set; }

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Debug(string message)
    {
        if (Verbose) Write(LogLevel.Debug, message);
    }

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// Marks the start of a stage of a long operation (import: "SQL BeforePackage", "Schemas", "Compile", ...).
    /// Not printed on the console (the Info lines around it already say what happens); a sink can show progress.
    /// </summary>
    public static void Stage(string name)
    {
        if (Sink != null) Sink(LogLevel.Stage, name);
    }

    private static void Write(LogLevel level, string message)
    {
        if (Sink != null) { Sink(level, message); return; }
        switch (level)
        {
            case LogLevel.Debug: Console.Error.WriteLine("  " + message); break;
            case LogLevel.Warn: Colored(ConsoleColor.Yellow, "WARN: " + message); break;
            case LogLevel.Error: Colored(ConsoleColor.Red, "ERROR: " + message); break;
            default: Console.Error.WriteLine(message); break;
        }
    }

    private static void Colored(ConsoleColor color, string text)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Error.WriteLine(text);
        Console.ForegroundColor = c;
    }
}
