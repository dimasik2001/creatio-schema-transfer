namespace Cst;

/// <summary>Progress goes to stderr so stdout stays clean for the result (text or --json).</summary>
public static class Log
{
    public static bool Verbose { get; set; }

    public static void Info(string message) => Console.Error.WriteLine(message);

    public static void Debug(string message)
    {
        if (Verbose) Console.Error.WriteLine("  " + message);
    }

    public static void Warn(string message)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine("WARN: " + message);
        Console.ForegroundColor = c;
    }

    public static void Error(string message)
    {
        var c = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: " + message);
        Console.ForegroundColor = c;
    }
}
