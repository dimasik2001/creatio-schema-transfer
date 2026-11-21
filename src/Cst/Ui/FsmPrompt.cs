using System.Net.Http;
using Cst.Creatio;
using Cst.Transfer;
using Spectre.Console;
using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>
/// Before an export from a stand in file system development mode (FSM): asks whether to load the export's packages
/// from the files into the DB first (the CLI's export --load-to-db), shows what the load would delete or skip, and
/// loads. Stands without FSM are not asked.
/// </summary>
public static class FsmPrompt
{
    /// <summary>Returns false when the export should not go on (cancelled, or the load failed).</summary>
    public static async Task<bool> AskAndLoadAsync(StandSession source, IReadOnlyList<string> packages, UiLog log)
    {
        if (source.IsFsm != true || packages.Count == 0) return true;
        FsmLoadPlan plan = null!;
        try { plan = await Progress.RunAsync(log, T("Comparing the packages on disk with the DB..."), () => FsmLoader.PlanAsync(source.Client, packages)); }
        catch (Exception ex) when (ex is CreatioException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(T("Cannot check the packages on disk:"))}[/] {Markup.Escape(ex.Message)}");
            return Choose(new[] { Skip, Cancel }) == Skip;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(F("{0} is in file system development mode (FSM).", source.Name))}[/]");
        AnsiConsole.MarkupLine(Markup.Escape(T("Schema code is always exported from disk. Loading the packages into the DB first also brings whole packages, SQL scripts, data bindings, translations and metadata changed on disk.")));
        if (plan.Packages.Count > 0)
            AnsiConsole.MarkupLine(Markup.Escape(F("Packages to load: {0}", string.Join(", ", plan.Packages))));
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(T("Creatio writes every element whose files differ from the DB; depending on its Web.config it may also update the DB structure, run changed SQL scripts and install changed data bindings."))}[/]");
        foreach (var p in plan.NotOnDisk)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(F("{0} has no folder on disk; it is not loaded.", p))}[/]");
        if (!plan.DiskChecked)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("The stand has no \"Path\" in appsettings.json: cst cannot check what the load would delete from the DB."))}[/]");
        if (plan.WillDelete.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(F("The load would DELETE from the DB elements that have no folder on disk ({0}):", plan.WillDelete.Count))}[/]");
            foreach (var d in plan.WillDelete.Take(30)) AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(d)}");
            if (plan.WillDelete.Count > 30) AnsiConsole.MarkupLine($"  [red]{Markup.Escape(F("... {0} more", plan.WillDelete.Count - 30))}[/]");
        }
        if (plan.Packages.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("No package of the export has a folder on disk: nothing to load."))}[/]");
            return true;
        }

        bool risky = plan.WillDelete.Count > 0 || !plan.DiskChecked;
        var choice = Choose(risky ? new[] { Skip, LoadAnyway, Cancel } : new[] { Load, Skip, Cancel });
        if (choice == Cancel) return false;
        if (choice == Skip) return true;

        FsmLoadResult result;
        try { result = await Progress.RunAsync(log, T("Loading the packages from the file system into the DB..."), () => FsmLoader.LoadAsync(source.Client, plan.Packages)); }
        catch (Exception ex) when (ex is CreatioException or HttpRequestException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(T("Loading into the DB failed; no archive was written:"))}[/] {Markup.Escape(ex.Message)}");
            UiInput.Pause();
            return false;
        }
        if (result.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(F("Loading into the DB failed for {0} element(s); no archive was written:", result.Errors.Count))}[/]");
            foreach (var e in result.Errors.Take(20)) AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(e)}");
            UiInput.Pause();
            return false;
        }
        AnsiConsole.MarkupLine(result.Changes.Count == 0
            ? $"[green]{Markup.Escape(T("Nothing to load: the DB already matches the files."))}[/]"
            : $"[green]{Markup.Escape(F("Loaded into the DB: {0} change(s).", result.Changes.Count))}[/]");
        foreach (var c in result.Changes.Take(15)) AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(c)}[/]");
        if (result.Changes.Count > 15) AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(F("... {0} more", result.Changes.Count - 15))}[/]");
        return true;
    }

    private const string Load = "load", LoadAnyway = "anyway", Skip = "skip", Cancel = "cancel";

    private static string Choose(string[] choices) => AnsiConsole.Prompt(Select<string>()
        .Title(T("Load the packages into the DB before the export?"))
        .UseConverter(c => c switch
        {
            Load => Markup.Escape(T("Load into the DB, then export")),
            LoadAnyway => $"[red]{Markup.Escape(T("Load anyway, then export"))}[/]",
            Skip => Markup.Escape(T("Export without loading")),
            _ => Markup.Escape(T("Cancel"))
        })
        .AddChoices(choices));
}
