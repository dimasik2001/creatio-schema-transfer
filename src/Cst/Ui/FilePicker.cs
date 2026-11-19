using Spectre.Console;
using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>
/// Picks an archive (.zip) and/or a schema list (.txt): the recent ones from cst.ui-state.json plus those in
/// archives\, lists\, the settings folder and the current folder, newest first; or a typed path.
/// </summary>
public static class FilePicker
{
    public static string? Pick(string title, UiState state, string baseDir, bool archives, bool lists)
    {
        const string typed = "\u0000typed", back = "\u0000back";
        var files = Candidates(state, baseDir, archives, lists);
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(title)
            .PageSize(15)
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type a path…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Describe(c)
            })
            .AddChoices(files.Append(typed).Append(back)));
        if (choice == back) return null;
        if (choice == typed)
        {
            var what = archives && lists ? T("Archive (.zip) or list (.txt)") : archives ? T("Archive (.zip)") : T("List (.txt)");
            choice = AnsiConsole.Prompt(L.Text(Markup.Escape($"{what}:")).Validate(p =>
                File.Exists(p.Trim().Trim('"')) ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("No such file."))))).Trim().Trim('"');
        }
        return Path.GetFullPath(choice);
    }

    /// <summary>Recent and nearby archives and lists, newest first (at most 20).</summary>
    public static List<string> Candidates(UiState state, string baseDir, bool archives, bool lists)
    {
        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        bool Wanted(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? archives
            : path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && lists;
        void Add(string path)
        {
            try { if (Wanted(path) && File.Exists(path)) found[Path.GetFullPath(path)] = File.GetLastWriteTime(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach (var p in state.RecentArchives.Concat(state.RecentLists)) Add(p);
        foreach (var dir in new[] { Path.Combine(baseDir, "archives"), Path.Combine(baseDir, "lists"), baseDir, Environment.CurrentDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.zip").Concat(Directory.EnumerateFiles(dir, "*.txt"))) Add(f);
        }
        return found.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key).ToList();
    }

    private static string Describe(string path)
    {
        var kinds = new[] { T("list"), T("archive") };
        int pad = kinds.Max(k => k.Length);
        var kind = (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? kinds[0] : kinds[1]).PadRight(pad);
        string when = "";
        try { when = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); } catch (IOException) { }
        return Markup.Escape(UiApp.Fit($"{kind}  {when}  {Path.GetFileName(path)}  ({Path.GetDirectoryName(path)})", 6));
    }
}
