using System.Net.Http;
using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;
using Cst.Transfer;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cst.Ui;

/// <summary>
/// Import wizard: target stand → archive (or a list, exported first) → preview → data binding conflicts →
/// options → dry run (always) → confirm → import with stage progress → result (Restore on failure).
/// </summary>
public sealed class ImportWizard(EnvPicker envs, UiState state, UiLog log, string baseDir)
{
    public async Task RunAsync(StandSession? target = null, string? archive = null)
    {
        target ??= await envs.PickAsync(StandPurpose.Target, "Import into which stand?");
        if (target == null) return;
        while (true)
        {
            archive ??= await PickArchiveAsync(target);
            if (archive == null) return;
            var next = await RunForArchiveAsync(target, archive);
            if (next != Next.OtherArchive) return;
            archive = null;
        }
    }

    private enum Next { Done, OtherArchive }

    private void Header(StandSession target, string step)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]Import · {Markup.Escape(step)}[/] [grey]→[/] {Markup.Escape(target.Header)}").LeftJustified());
    }

    // ---------- 2. archive ----------

    private async Task<string?> PickArchiveAsync(StandSession target)
    {
        Header(target, "archive");
        var choice = FilePicker.Pick("Which archive (or list) to import?", state, baseDir, archives: true, lists: true);
        if (choice == null) return null;
        if (choice.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return await ExportListAsync(target, choice);
        return choice;
    }

    /// <summary>A list file is exported first (asks the source stand), then imported.</summary>
    private async Task<string?> ExportListAsync(StandSession target, string listPath)
    {
        var source = await envs.PickAsync(StandPurpose.Source, $"Export {Path.GetFileName(listPath)} from which stand?", exclude: target.Name);
        if (source == null) return null;
        var list = SchemaList.ParseFile(listPath);
        var files = SchemaList.ParseFilesSection(listPath);
        var whole = SchemaList.ParseWholePackagesSection(listPath);
        var archive = Path.Combine(baseDir, "archives", $"{source.Name}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        try
        {
            await Progress.RunAsync(log, "Exporting the list...", () => new Exporter(source.Client).ExportAsync(list, archive, files, whole));
        }
        catch (TransferException ex)
        {
            foreach (var e in ex.Errors) AnsiConsole.MarkupLine($"[red]{Markup.Escape(e)}[/]");
            UiInput.Pause();
            return null;
        }
        UiState.Remember(state.RecentLists, listPath);
        UiState.Remember(state.RecentArchives, archive);
        AnsiConsole.MarkupLine($"[green]Exported:[/] {Markup.Escape(archive)}");
        return archive;
    }

    // ---------- 3..8 ----------

    private async Task<Next> RunForArchiveAsync(StandSession target, string archive)
    {
        // 3. Preview
        Manifest manifest;
        try { (manifest, _) = TransferArchive.Read(archive); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]Cannot read {Markup.Escape(archive)}:[/] {Markup.Escape(ex.Message)}");
            UiInput.Pause();
            return Next.OtherArchive;
        }
        Header(target, "preview");
        bool blocked = Preview(manifest, target);
        const string go = "go", other = "other", cancel = "cancel";
        var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .UseConverter(c => c switch { go => "Continue", other => "Choose another archive", _ => "Cancel" })
            .AddChoices(blocked ? [other, cancel] : [go, other, cancel]));
        if (choice == other) return Next.OtherArchive;
        if (choice == cancel) return Next.Done;
        UiState.Remember(state.RecentArchives, archive);

        // 4. Data binding conflicts
        if (manifest.Items.Any(i => i.Kind == ItemKind.Data))
        {
            var resolved = await ResolveBindingsAsync(target, archive, manifest);
            if (resolved == null) return Next.Done;
            archive = resolved;
        }

        // 5. Options
        Header(target, "options");
        var optNoCompile = "No compile [grey](schemas unchanged since the last successful compile)[/]";
        var optRebuild = "Rebuild [grey](full compilation instead of an incremental one)[/]";
        var optDesigner = "Data designer [grey](old path for data bindings)[/]";
        var chosen = AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
            .Title("Options [grey](Space toggles, Enter continues)[/]")
            .NotRequired()
            .AddChoices(optNoCompile, optRebuild, optDesigner));
        var defaultWorkDir = Path.Combine(Path.GetDirectoryName(archive)!,
            Path.GetFileNameWithoutExtension(archive) + "_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var workDir = AnsiConsole.Prompt(new TextPrompt<string>("Work dir (backups, restore.json, log):").DefaultValue(defaultWorkDir)).Trim().Trim('"');
        ImportOptions Options(bool dry) => new()
        {
            DryRun = dry, NoCompile = chosen.Contains(optNoCompile), FullRebuild = chosen.Contains(optRebuild),
            DataDesigner = chosen.Contains(optDesigner), WorkDir = workDir,
            // Conflicts were resolved above; one left now means the target changed meanwhile: stop safely.
            ResolveBindingConflict = c =>
            {
                Log.Warn($"Data binding {c.Name} in {c.Package} conflicts on the target (UId {c.TargetUId}); import aborted.");
                return new ElementInstaller.ConflictDecision(ElementInstaller.ConflictAction.Abort, null);
            }
        };

        // 6. Dry run (always)
        Header(target, "dry run");
        var plan = new List<string>();
        void Collect(string line) => plan.Add(line);
        ImportResult dry;
        log.InfoWritten += Collect;
        try { dry = await Progress.RunAsync(log, "Checking the target (dry run)...", () => new Importer(target.Client).ImportAsync(archive, Options(true))); }
        catch (TransferException ex)
        {
            foreach (var e in ex.Errors) AnsiConsole.MarkupLine($"[red]{Markup.Escape(e)}[/]");
            AnsiConsole.MarkupLine("[red]Nothing was installed.[/]");
            UiInput.Pause();
            return Next.Done;
        }
        finally { log.InfoWritten -= Collect; }
        if (dry.MissingDependencies.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]Missing on {Markup.Escape(target.Name)} ({dry.MissingDependencies.Count}); nothing can be installed:[/]");
            foreach (var m in dry.MissingDependencies) AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(m.Replace("**", ""))}");
            if (dry.MissingReportPath != null) AnsiConsole.MarkupLine($"[grey]Report: {Markup.Escape(dry.MissingReportPath)}[/]");
            UiInput.Pause();
            return Next.Done;
        }
        AnsiConsole.Write(new Panel(new Text(string.Join(Environment.NewLine, plan.Where(l => l.StartsWith("  ") || l.StartsWith("Dry run")))))
            .Header("Plan").Expand());
        foreach (var n in dry.Notes) AnsiConsole.MarkupLine($"[grey]Note:[/] {Markup.Escape(n)}");
        foreach (var w in dry.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(w)}");

        // 7. Confirm → import
        if (!AnsiConsole.Confirm($"Install into {target.Header}?", false)) return Next.Done;
        Directory.CreateDirectory(workDir);
        UiState.Remember(state.RecentWorkDirs, Path.GetFullPath(workDir));
        log.FilePath = Path.Combine(workDir, "cst-ui.log");
        Header(target, "installing");
        ImportResult? result = null;
        int code;
        string? failure = null;
        try
        {
            result = await Progress.RunStagesAsync(log, $"Importing into {target.Name}", () => new Importer(target.Client).ImportAsync(archive, Options(false)));
            code = result.MissingDependencies.Count > 0 ? 3 : result.CompileErrors.Count > 0 ? 5 : 0;
        }
        catch (TransferException ex) { code = 2; failure = string.Join(Environment.NewLine, ex.Errors); }
        catch (InstallException ex) { code = 4; failure = ex.Message; }
        catch (Exception ex) when (ex is CreatioException or IOException or HttpRequestException or TaskCanceledException) { code = 4; failure = ex.Message; }
        finally { log.FilePath = null; }

        // 8. Result
        await ResultAsync(target, workDir, code, result, failure);
        return Next.Done;
    }

    /// <summary>Shows the archive content; returns true when the import cannot go on (files without a target Path).</summary>
    private static bool Preview(Manifest m, StandSession target)
    {
        AnsiConsole.MarkupLine($"[grey]From {Markup.Escape(m.SourceUrl)}, created {m.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}[/]");
        var t = new Table().Border(TableBorder.Rounded).AddColumn("What").AddColumn("Details");
        foreach (var w in m.WholePackages)
            t.AddRow("[bold]whole package[/]", Markup.Escape($"{w.Name} {w.Version} — zip installer, installed first ({w.Schemas.Count} schemas)"));
        foreach (var p in m.Packages)
            t.AddRow("package", Markup.Escape(p.Source == p.Target ? p.Source : $"{p.Source} → {p.Target}"));
        foreach (var g in m.Items.GroupBy(i => ElementFolders.Of(i.Kind, i.Manager)).OrderBy(g => g.Key))
            t.AddRow(Markup.Escape(ElementFolders.Title(g.Key)), Markup.Escape(UiApp.Fit(string.Join(", ", g.Select(i => i.Name)), 30)));
        if (m.Files.Count > 0)
            t.AddRow("files", Markup.Escape($"{m.Files.Count} file(s), {m.Files.Sum(f => f.Size) / 1024} KB"));
        if (m.External.Count > 0)
            t.AddRow("required on target", Markup.Escape($"{m.External.Count} element(s) not in the archive"));
        AnsiConsole.Write(t);
        foreach (var w in m.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(w)}");
        if (m.Files.Count > 0 && !target.HasPath)
        {
            AnsiConsole.MarkupLine($"[red]The archive carries files, but {Markup.Escape(target.Name)} has no \"Path\" (Creatio folder) in appsettings.json: it cannot be imported there.[/]");
            return true;
        }
        return false;
    }

    // ---------- 4. data binding conflicts ----------

    /// <summary>Returns the archive to import (the original or a resolved copy), or null when the user aborts.</summary>
    private async Task<string?> ResolveBindingsAsync(StandSession target, string archive, Manifest manifest)
    {
        Header(target, "data bindings");
        List<BindingCheckRow> rows = [];
        await Progress.RunAsync(log, "Comparing data bindings with the target packages...", async () => rows = await BindingCheck.CheckAsync(target.Client, manifest));
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Binding").AddColumn("Target package").AddColumn("State");
        foreach (var r in rows)
            t.AddRow(Markup.Escape(r.Item.Name), Markup.Escape(r.Item.TargetPackage), r.State switch
            {
                BindingState.New => "[green]new[/]",
                BindingState.Same => "update (same UId)",
                BindingState.Conflict => "[yellow]CONFLICT: same name, another UId[/]",
                _ => $"[grey]unknown: {Markup.Escape(r.Error ?? "")}[/]"
            });
        AnsiConsole.Write(t);
        var conflicts = rows.Where(r => r.State == BindingState.Conflict).ToList();
        if (conflicts.Count == 0) return archive;

        const string useUid = "uid", rename = "rename", skip = "skip", abort = "abort";
        var fixes = new List<BindingFix>();
        foreach (var c in conflicts)
        {
            var name = c.Item.Name;
            var answer = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"[yellow]{Markup.Escape(name)}[/] already exists in {Markup.Escape(c.Item.TargetPackage)} under another UId. What to do?")
                .UseConverter(a => a switch
                {
                    useUid => "Use target UId [grey](update the target's binding in place)[/]",
                    rename => $"Rename [grey](save as {Markup.Escape(BindingFix.DefaultRename(name))})[/]",
                    skip => "Skip [grey](leave the target's binding as it is)[/]",
                    _ => "[red]Abort import[/]"
                })
                .AddChoices(useUid, rename, skip, abort));
            if (answer == abort) return null;
            fixes.Add(answer switch
            {
                useUid => new BindingFix { Package = c.Item.SourcePackage, Name = name, Action = BindingFixAction.UseTargetUId, TargetUId = c.TargetUId },
                rename => new BindingFix { Package = c.Item.SourcePackage, Name = name, Action = BindingFixAction.Rename, NewName = BindingFix.DefaultRename(name) },
                _ => new BindingFix { Package = c.Item.SourcePackage, Name = name, Action = BindingFixAction.Skip }
            });
        }
        var resolved = Path.Combine(Path.GetDirectoryName(archive)!, Path.GetFileNameWithoutExtension(archive) + "-resolved.zip");
        BindingCheck.WriteResolvedArchive(archive, resolved, fixes);
        AnsiConsole.MarkupLine($"[green]Conflicts resolved into[/] {Markup.Escape(resolved)}");
        return resolved;
    }

    // ---------- 8. result ----------

    private async Task ResultAsync(StandSession target, string workDir, int code, ImportResult? r, string? failure)
    {
        Header(target, "result");
        var (text, color) = code switch
        {
            0 => ("Imported successfully.", "green"),
            2 => ("Stopped by a check; nothing was installed.", "red"),
            3 => ("Missing dependencies; nothing was installed.", "red"),
            4 => ("The installation failed part-way. Backups are in the work dir; Restore rolls the stand back.", "red"),
            _ => ("Imported, but the imported schemas do not compile.", "red")
        };
        AnsiConsole.MarkupLine($"[bold {color}]{Markup.Escape(text)}[/]");
        if (failure != null) AnsiConsole.MarkupLine($"[red]{Markup.Escape(failure)}[/]");
        if (r != null)
        {
            if (r.Installed.Count > 0) AnsiConsole.MarkupLine($"Installed into: {Markup.Escape(string.Join(", ", r.Installed))}");
            foreach (var n in r.Notes) AnsiConsole.MarkupLine($"[grey]Note:[/] {Markup.Escape(n)}");
            foreach (var w in r.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(w)}");
            foreach (var e in r.CompileErrors.Take(20)) AnsiConsole.MarkupLine($"  [red]{Markup.Escape(e)}[/]");
            if (r.CompileErrors.Count > 20) AnsiConsole.MarkupLine($"  [red]... {r.CompileErrors.Count - 20} more[/]");
            if (r.StoppedAfterCompile) AnsiConsole.MarkupLine("[red]SQL AfterPackage/AfterSchemaData scripts and data bindings were NOT installed.[/]");
        }
        AnsiConsole.MarkupLine($"[grey]Work dir: {Markup.Escape(workDir)}[/]");
        await AfterRunAsync(target, workDir, offerRestore: code is 4 or 5 && File.Exists(Restorer.InfoPath(workDir)));
    }

    private async Task AfterRunAsync(StandSession target, string workDir, bool offerRestore)
    {
        const string restore = "restore", showLog = "log", done = "done";
        while (true)
        {
            var choices = new List<string>();
            if (offerRestore) choices.Add(restore);
            choices.Add(showLog);
            choices.Add(done);
            var c = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .UseConverter(x => x switch { restore => "[yellow]Restore[/] [grey](roll the stand back from this work dir)[/]", showLog => "Show the log", _ => "Done" })
                .AddChoices(choices));
            if (c == done) return;
            if (c == showLog)
            {
                foreach (var (level, line) in log.Tail(40))
                    AnsiConsole.MarkupLine(level switch
                    {
                        LogLevel.Warn => $"[yellow]{Markup.Escape(line)}[/]",
                        LogLevel.Error => $"[red]{Markup.Escape(line)}[/]",
                        LogLevel.Stage => $"[aqua]── {Markup.Escape(line)}[/]",
                        _ => $"[grey]{Markup.Escape(line)}[/]"
                    });
                continue;
            }
            await new RestoreFlow(log).RunAsync(target, workDir);
            return;
        }
    }
}

/// <summary>Restore from an import's work dir (main menu Restore, and after a failed import).</summary>
public sealed class RestoreFlow(UiLog log)
{
    public async Task RunAsync(StandSession target, string workDir)
    {
        AnsiConsole.MarkupLine($"Restore [bold]{Markup.Escape(target.Header)}[/] from [bold]{Markup.Escape(workDir)}[/].");
        AnsiConsole.MarkupLine("[grey]Executed SQL scripts are not undone; elements, rows and packages the import created are listed, not removed.[/]");
        var noCompile = AnsiConsole.Confirm("Skip the compilation afterwards?", false);
        if (!AnsiConsole.Confirm("Start the restore?", false)) return;
        RestoreResult r;
        try { r = await Progress.RunStagesAsync(log, $"Restoring {target.Name}", () => new Restorer(target.Client).RestoreAsync(workDir, noCompile)); }
        catch (Exception ex) when (ex is ArgumentException or CreatioException or IOException or HttpRequestException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Restore failed:[/] {Markup.Escape(ex.Message)}");
            UiInput.Pause();
            return;
        }
        AnsiConsole.MarkupLine(r.CompileErrors.Count == 0 ? "[bold green]Restored.[/]" : "[bold red]Restored, but the configuration does not compile.[/]");
        if (r.Restored.Count > 0) AnsiConsole.MarkupLine($"Restored: {Markup.Escape(string.Join(", ", r.Restored))}");
        if (r.NotRemoved.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Created by the import and NOT removed (delete manually if needed):[/]");
            foreach (var n in r.NotRemoved) AnsiConsole.MarkupLine($"  • {Markup.Escape(n)}");
        }
        foreach (var w in r.Warnings) AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(w)}");
        foreach (var e in r.CompileErrors.Take(20)) AnsiConsole.MarkupLine($"  [red]{Markup.Escape(e)}[/]");
        UiInput.Pause();
    }
}

/// <summary>Progress views over cst's log: a spinner with the latest line, or the list of stages with their times.</summary>
public static class Progress
{
    public static async Task<T> RunAsync<T>(UiLog log, string title, Func<Task<T>> work)
    {
        T result = default!;
        await AnsiConsole.Status().StartAsync(title, async ctx =>
        {
            void OnInfo(string line) => ctx.Status(Markup.Escape(UiApp.Fit(line, 6)));
            log.InfoWritten += OnInfo;
            var quiet = log.Quiet;
            log.Quiet = true;
            try { result = await work(); }
            finally { log.InfoWritten -= OnInfo; log.Quiet = quiet; }
        });
        return result;
    }

    public static Task RunAsync(UiLog log, string title, Func<Task> work) =>
        RunAsync<bool>(log, title, async () => { await work(); return true; });

    /// <summary>Long operations (import, restore): every stage reached with its duration, the current one ticking.</summary>
    public static async Task<T> RunStagesAsync<T>(UiLog log, string title, Func<Task<T>> work)
    {
        var started = DateTime.Now;
        var stages = new List<(string Name, DateTime Start)>();
        string last = "";
        void OnStage(string s) { lock (stages) stages.Add((s, DateTime.Now)); }
        void OnInfo(string line) => last = line;
        IRenderable Render()
        {
            var rows = new List<IRenderable> { new Markup($"[bold]{Markup.Escape(title)}[/]  [grey]{Elapsed(DateTime.Now - started)}[/]") };
            List<(string Name, DateTime Start)> copy;
            lock (stages) copy = stages.ToList();
            for (int i = 0; i < copy.Count; i++)
            {
                bool current = i == copy.Count - 1;
                var end = current ? DateTime.Now : copy[i + 1].Start;
                rows.Add(new Markup(current
                    ? $"  [aqua]▶ {Markup.Escape(copy[i].Name)}[/]  [grey]{Elapsed(end - copy[i].Start)}[/]"
                    : $"  [green]✓[/] {Markup.Escape(copy[i].Name)}  [grey]{Elapsed(end - copy[i].Start)}[/]"));
            }
            rows.Add(new Markup($"  [grey]{Markup.Escape(UiApp.Fit(last, 6))}[/]"));
            return new Rows(rows);
        }
        log.StageChanged += OnStage;
        log.InfoWritten += OnInfo;
        var quiet = log.Quiet;
        log.Quiet = true;
        try
        {
            var task = work();
            await AnsiConsole.Live(Render()).StartAsync(async ctx =>
            {
                while (!task.IsCompleted)
                {
                    ctx.UpdateTarget(Render());
                    await Task.WhenAny(task, Task.Delay(500));
                }
                ctx.UpdateTarget(Render());
            });
            return await task;
        }
        finally
        {
            log.StageChanged -= OnStage;
            log.InfoWritten -= OnInfo;
            log.Quiet = quiet;
        }
    }

    private static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
