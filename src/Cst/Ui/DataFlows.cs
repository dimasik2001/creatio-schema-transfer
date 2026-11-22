using Cst.Config;
using Cst.Creatio;
using Cst.Data;
using Cst.Transfer;
using Spectre.Console;
using static Cst.Ui.L;

namespace Cst.Ui;

/// <summary>
/// Main menu → Table data: export rows of tables (by packages, with modes, reference policies and children) into a
/// data archive, import an archive (dry run first, one transaction), restore an import. Goes through CstGate, which is
/// installed on a stand only when the user agrees (docs/db-transfer.md).
/// </summary>
public sealed class DataFlows(EnvPicker envs, UiState state, UiLog log, string baseDir, DataSettings settings)
{
    private const string ExportItem = "export", ImportItem = "import", RestoreItem = "restore", BackItem = "back";

    public async Task RunAsync()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(T("Table data"))}[/]").LeftJustified());
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(T("Rows of tables between stands, through CstGate (installed on a stand when you agree)."))}[/]");
            AnsiConsole.WriteLine();
            var col = UiApp.NameColumn(T("Export data"), T("Import data"), T("Restore data"));
            var choice = AnsiConsole.Prompt(Select<string>()
                .Title(T("What do you want to do?"))
                .UseConverter(c => c switch
                {
                    ExportItem => UiApp.MenuLine(T("Export data"), T("choose packages and tables, save a plan or a data archive"), col),
                    ImportItem => UiApp.MenuLine(T("Import data"), T("write a data archive into a stand (dry run first)"), col),
                    RestoreItem => UiApp.MenuLine(T("Restore data"), T("undo a data import from its backup"), col),
                    _ => $"[grey]{Markup.Escape(T("← Back"))}[/]"
                })
                .AddChoices(ExportItem, ImportItem, RestoreItem, BackItem));
            switch (choice)
            {
                case ExportItem: await ExportAsync(); break;
                case ImportItem: await ImportAsync(null); break;
                case RestoreItem: await RestoreAsync(); break;
                default: return;
            }
        }
    }

    private static void Header(StandSession s, string flow)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(flow)}[/] [grey]·[/] {Markup.Escape(s.Header)}").LeftJustified());
    }

    private static void Error(string text) => AnsiConsole.MarkupLine($"[red]{Markup.Escape(text)}[/]");

    private async Task<T?> WithStatusAsync<T>(string title, Func<Action<string>, Task<T>> work)
    {
        T? result = default;
        log.Quiet = true;
        try
        {
            await AnsiConsole.Status().StartAsync(Markup.Escape(title), async ctx =>
                result = await work(line => ctx.Status(Markup.Escape(UiApp.Fit(line, 6)))));
        }
        finally { log.Quiet = false; }
        return result;
    }

    // ------------------------------------------------------------------ gate

    /// <summary>Checks CstGate on the stand; offers to install or update it. False = cannot go on.</summary>
    private async Task<bool> EnsureGateAsync(StandSession stand)
    {
        var gate = new GateClient(stand.Client);
        var embedded = GateInstaller.EmbeddedVersion;
        GateInfo? info = null;
        try { info = await gate.InfoAsync(); }
        catch (GateMissingException) { }
        catch (Exception ex) when (ex is CreatioException or GateException) { Error(ex.Message); UiInput.Pause(); return false; }

        if (info == null || info.Version != embedded)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(Markup.Escape(info == null
                ? F("Table data goes through CstGate: a package with one DLL and no schemas. It is not installed on {0}.", stand.Name)
                : F("CstGate {0} is installed on {1}; this cst carries {2}.", info.Version, stand.Name, embedded)));
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("Installing it restarts the Creatio application (about a minute, users are disconnected)."))}[/]");
            var question = info == null ? F("Install CstGate on {0} now?", stand.Name) : F("Update CstGate on {0} now?", stand.Name);
            if (UiInput.Confirm(question, false))
            {
                try
                {
                    info = await WithStatusAsync(T("Installing CstGate..."), progress => GateInstaller.InstallAsync(stand.Client, true, progress));
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(F("CstGate {0} answers on {1}.", info!.Version, stand.Name))}[/]");
                }
                catch (Exception ex) when (ex is CreatioException or GateException or GateMissingException or InstallException)
                {
                    Error(T("CstGate was not installed:") + " " + ex.Message);
                    UiInput.Pause();
                    return false;
                }
            }
            else if (info == null) return false;
        }
        if (!info!.CanUse)
        {
            Error(F("User {0} has no CanManageSolution operation: CstGate refuses SQL.", info.User));
            UiInput.Pause();
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ export

    private async Task ExportAsync()
    {
        var source = await envs.PickAsync(StandPurpose.Source, T("Export table data from which stand?"));
        if (source == null) return;
        Header(source, T("Export data"));
        if (!await EnsureGateAsync(source)) return;

        const string select = "select", load = "load";
        var col = UiApp.NameColumn(T("Select"), T("Load a plan"));
        var how = AnsiConsole.Prompt(Select<string>()
            .Title(T("What to export?"))
            .UseConverter(c => c == select
                ? UiApp.MenuLine(T("Select"), T("mark packages, then choose their tables"), col)
                : UiApp.MenuLine(T("Load a plan"), T("take a saved data plan (tables, references, children)"), col))
            .AddChoices(select, load));

        var session = new ExportSession(source);
        if (how == load)
        {
            var path = PickFile(T("Which plan to load?"), state.RecentPlans, "plans", "*.txt");
            if (path == null) return;
            try { session.Plan = DataPlan.Parse(await Io.ReadAllTextAsync(path), settings); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                Error(T("Cannot read the plan:") + " " + ex.Message);
                UiInput.Pause();
                return;
            }
            UiState.Remember(state.RecentPlans, path);
            if (session.Plan.Packages.Count > 0) await LoadReportAsync(session, session.Plan.Packages);
            if (session.Report != null) session.SyncTableSourceFromPlan(settings);
        }
        else
        {
            var packages = await WithStatusAsync(T("Loading packages..."), _ => new Catalog(source.Client).LoadPackagesAsync()) ?? [];
            var packageTab = new DataPackageSource(packages);
            if (await new SelectionBrowser(source.Header, [packageTab]).RunAsync() == BrowserResult.Back) return;
            await LoadReportAsync(session, packageTab.Selected.ToList());
            if (session.Report == null) return;
            session.Plan.Packages.AddRange(packageTab.Selected.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            if (!await ChooseTablesAsync(session)) return;
        }
        await SummaryAsync(session);
    }

    private sealed class ExportSession(StandSession source)
    {
        public StandSession Source { get; } = source;
        public DataPlan Plan { get; set; } = new();
        public TableReportResult? Report { get; set; }
        public DataTableSource? Tables { get; set; }

        /// <summary>Plan tables from the browser's modes (references, children and options stay).</summary>
        public void SyncPlanFromTableSource()
        {
            if (Tables == null) return;
            Plan.Tables.Clear();
            foreach (var t in Report!.Tables)
                if (Tables.Modes[t.Name].Kind != TableModeKind.Skip) Plan.Tables.Add((t.Name, Tables.Modes[t.Name]));
        }

        public void SyncTableSourceFromPlan(DataSettings settings)
        {
            Tables ??= new DataTableSource(Report!.Tables, settings);
            foreach (var t in Report!.Tables)
            {
                var planned = Plan.Tables.FirstOrDefault(p => p.Table.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
                Tables.SetMode(t.Name, planned.Table != null ? planned.Mode : new TableMode(TableModeKind.Skip));
            }
        }
    }

    private async Task LoadReportAsync(ExportSession session, List<string> packageNames)
    {
        try
        {
            session.Report = await WithStatusAsync(T("Reading the tables of the packages..."), async progress =>
            {
                var all = await new Catalog(session.Source.Client).LoadPackagesAsync();
                var pkgs = packageNames.Select(n => all.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    .Where(p => p != null).Select(p => p!).ToList();
                return await new TableReport(session.Source.Client, settings, session.Source.Settings.DbEngine ?? "MSSql")
                    .BuildAsync(pkgs, t => progress(F("Reading {0}...", t)));
            });
        }
        catch (Exception ex) when (ex is CreatioException)
        {
            Error(ex.Message);
            UiInput.Pause();
        }
    }

    private async Task<bool> ChooseTablesAsync(ExportSession session)
    {
        session.Tables ??= new DataTableSource(session.Report!.Tables, settings);
        if (session.Report!.Tables.Count == 0)
        {
            Error(T("The chosen packages have no tables (entity schemas)."));
            UiInput.Pause();
            return false;
        }
        if (await new SelectionBrowser(session.Source.Header, [session.Tables]).RunAsync() == BrowserResult.Back) return false;
        session.SyncPlanFromTableSource();
        return true;
    }

    private const string SExport = "export", SSave = "save", SMode = "mode", SRefs = "refs", SChildren = "children",
        SOptions = "options", STables = "tables", SMenu = "menu";

    private async Task SummaryAsync(ExportSession session)
    {
        while (true)
        {
            Header(session.Source, T("Export data"));
            AnsiConsole.Write(new Panel(Markup.Escape(session.Plan.ToText().TrimEnd())).Header(Markup.Escape(T("Plan"))).Border(BoxBorder.Rounded));
            if (session.Plan.Tables.Count == 0) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(session.Plan.Packages.Count > 0 ? T("No tables listed: every table created in the packages is exported in full.") : T("Nothing to export yet."))}[/]");
            var items = new List<string> { SExport, SSave, SMode, SRefs, SChildren, SOptions };
            if (session.Report != null) items.Add(STables);
            items.Add(SMenu);
            var col = UiApp.NameColumn(T("Export archive"), T("Save plan"), T("Table mode"), T("References"), T("Children"), T("Options"), T("Tables"));
            var choice = AnsiConsole.Prompt(Select<string>()
                .Title(T("Next step:"))
                .PageSize(12)
                .UseConverter(c => c switch
                {
                    SExport => UiApp.MenuLine(T("Export archive"), T("read the rows and write a data archive"), col),
                    SSave => UiApp.MenuLine(T("Save plan"), T("keep the plan as a text file (CLI: db-export --plan)"), col),
                    SMode => UiApp.MenuLine(T("Table mode"), T("full, top N by a column, WHERE condition, Ids, children, skip"), col),
                    SRefs => UiApp.MenuLine(T("References"), T("pull / expect / author / null / drop per lookup column"), col),
                    SChildren => UiApp.MenuLine(T("Children"), T("also take rows that point at the chosen rows"), col),
                    SOptions => UiApp.MenuLine(T("Options"), F("record rights: {0}, translations: {1}", YesNo(session.Plan.Rights), YesNo(session.Plan.Translations)), col),
                    STables => UiApp.MenuLine(T("Tables"), T("back to the table list"), col),
                    _ => $"[grey]{Markup.Escape(T("Main menu"))}[/]"
                })
                .AddChoices(items));
            switch (choice)
            {
                case SExport:
                    if (session.Plan.Tables.Count == 0 && session.Plan.Packages.Count == 0) break;
                    var archive = await RunExportAsync(session);
                    if (archive != null && AfterExport(archive)) { await ImportAsync(archive); return; }
                    break;
                case SSave: SavePlan(session); break;
                case SMode: EditMode(session); break;
                case SRefs: EditRefs(session); break;
                case SChildren: EditChildren(session); break;
                case SOptions:
                    session.Plan.Rights = UiInput.Confirm(T("Export record rights (Sys<Table>Right)?"), session.Plan.Rights);
                    session.Plan.Translations = UiInput.Confirm(T("Export translations (Sys<Table>Lcz)?"), session.Plan.Translations);
                    break;
                case STables:
                    session.SyncTableSourceFromPlan(settings);
                    await ChooseTablesAsync(session);
                    break;
                default: return;
            }
        }
    }

    private static string YesNo(bool v) => v ? T("yes") : T("no");

    private async Task<string?> RunExportAsync(ExportSession session)
    {
        var dir = Path.Combine(baseDir, "data");
        var def = Path.Combine(dir, $"{session.Source.Name}_{DateTime.Now:yyyyMMdd_HHmm}.zip");
        var path = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Archive file:"))).DefaultValue(def).ShowDefaultValue(true)).Trim().Trim('"');
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) path += ".zip";
        try
        {
            var manifest = await WithStatusAsync(T("Exporting..."), progress =>
                new DataExporter(session.Source.Client, settings).ExportAsync(session.Plan, path, progress));
            UiState.Remember(state.RecentDataArchives, Path.GetFullPath(path));
            ShowManifest(manifest!);
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(F("Archive: {0}", Path.GetFullPath(path)))}[/]");
            return Path.GetFullPath(path);
        }
        catch (TransferException ex)
        {
            foreach (var e in ex.Errors) Error(e);
        }
        catch (Exception ex) when (ex is CreatioException or GateException or GateMissingException or IOException or FormatException)
        {
            Error(ex.Message);
        }
        UiInput.Pause();
        return null;
    }

    /// <summary>After a successful export: true = import the archive into another stand now.</summary>
    private static bool AfterExport(string archive)
    {
        const string import = "import", back = "back";
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(T("What next?"))
            .UseConverter(c => c == import ? Markup.Escape(T("Import it into a stand now")) : Markup.Escape(T("Back to the plan")))
            .AddChoices(back, import));
        return choice == import;
    }

    private static void ShowManifest(DataManifest m)
    {
        var t = new Table().Border(TableBorder.Rounded)
            .AddColumn(Markup.Escape(T("Table"))).AddColumn(new TableColumn(Markup.Escape(T("Rows"))).RightAligned()).AddColumn(Markup.Escape(T("How")));
        foreach (var e in m.Tables)
        {
            var pulled = e.Rows - e.SelectedRows;
            var how = e.CompanionOf != null ? F("{0} of {1}", e.Mode, e.CompanionOf)
                : e.Mode + (pulled > 0 ? " " + F("(+{0} by references/children)", pulled) : "");
            t.AddRow(Markup.Escape(e.Name), e.Rows.ToString(), Markup.Escape(how));
        }
        AnsiConsole.Write(t);
        foreach (var w in m.Warnings) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("Warning:"))}[/] {Markup.Escape(w)}");
    }

    private void SavePlan(ExportSession session)
    {
        var def = Path.Combine(baseDir, "plans", $"{session.Source.Name}_{DateTime.Now:yyyyMMdd_HHmm}.txt");
        var path = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Plan file:"))).DefaultValue(def).ShowDefaultValue(true)).Trim().Trim('"');
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, session.Plan.ToText());
            UiState.Remember(state.RecentPlans, Path.GetFullPath(path));
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(F("Saved: {0}", Path.GetFullPath(path)))}[/]");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error(ex.Message);
        }
        UiInput.Pause();
    }

    private void EditMode(ExportSession session)
    {
        var names = session.Report?.Tables.Where(t => t.Excluded == null).Select(t => t.Name).ToList()
                    ?? session.Plan.Tables.Select(t => t.Table).ToList();
        const string typed = "\u0000typed", back = "\u0000back";
        var table = AnsiConsole.Prompt(Select<string>()
            .Title(T("Which table?"))
            .PageSize(15)
            .EnableSearch()
            .SearchPlaceholderText(T("(Type to search)"))
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type a name…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Markup.Escape($"{c}  —  {DataTableSource.Describe(ModeOf(session, c))}")
            })
            .AddChoices(names.Append(typed).Append(back)));
        if (table == back) return;
        if (table == typed)
            table = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Table name:"))).Validate(n =>
                SqlDialect.IsSafeName(n.Trim()) ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("Letters, digits and _ only."))))).Trim();

        var kinds = new[] { TableModeKind.Full, TableModeKind.Top, TableModeKind.Where, TableModeKind.Ids, TableModeKind.Children, TableModeKind.Skip };
        var kind = AnsiConsole.Prompt(Select<TableModeKind>()
            .Title(F("Mode of {0}:", table))
            .UseConverter(k => Markup.Escape(k switch
            {
                TableModeKind.Full => T("full — every row"),
                TableModeKind.Top => T("top N — the first N rows by a column"),
                TableModeKind.Where => T("where — rows matching an SQL condition (source dialect)"),
                TableModeKind.Ids => T("ids — the rows with the given Ids"),
                TableModeKind.Children => T("children — only rows pulled by references or children"),
                _ => T("skip — not exported")
            }))
            .AddChoices(kinds));
        TableMode mode;
        try
        {
            mode = kind switch
            {
                TableModeKind.Top => TableMode.Parse("top " + AnsiConsole.Prompt(L.Text(Markup.Escape(T("How many rows, and by which column? (e.g. 500 by CreatedOn desc)")))
                    .DefaultValue($"{settings.DefaultTop} by {settings.DefaultTopOrder}")), settings),
                TableModeKind.Where => TableMode.Parse("where " + AnsiConsole.Prompt(L.Text(Markup.Escape(T("SQL condition (e.g. \"CreatedOn\" > '2026-01-01'):")))), settings),
                TableModeKind.Ids => TableMode.Parse("ids " + AnsiConsole.Prompt(L.Text(Markup.Escape(T("Ids, separated by commas or spaces:")))), settings),
                _ => new TableMode(kind)
            };
        }
        catch (FormatException ex) { Error(ex.Message); UiInput.Pause(); return; }

        int i = session.Plan.Tables.FindIndex(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
        if (mode.Kind == TableModeKind.Skip) { if (i >= 0) session.Plan.Tables.RemoveAt(i); }
        else if (i >= 0) session.Plan.Tables[i] = (session.Plan.Tables[i].Table, mode);
        else session.Plan.Tables.Add((table, mode));
        session.Tables?.SetMode(table, mode);
    }

    private static TableMode ModeOf(ExportSession session, string table)
    {
        var p = session.Plan.Tables.FirstOrDefault(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
        return p.Table != null ? p.Mode : new TableMode(TableModeKind.Skip);
    }

    private void EditRefs(ExportSession session)
    {
        var chosen = new HashSet<string>(session.Plan.Tables.Where(t => t.Mode.Kind != TableModeKind.Skip).Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
        var edges = session.Report?.Refs.Where(r => chosen.Contains(r.Table) && r.Column is not ("CreatedBy" or "ModifiedBy")).ToList() ?? [];
        const string typed = "\u0000typed", back = "\u0000back";
        string Current(string table, string column) => session.Plan.PolicyFor(table, column) is { } p ? p
            : chosen.Contains(edges.First(e => e.Table == table && e.Column == column).RefTable) || table == edges.First(e => e.Table == table && e.Column == column).RefTable
                ? RefPolicy.Pull : RefPolicy.Expect;
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(T("Which reference? (default: pull when the referenced table is exported, else expect)"))
            .PageSize(15)
            .EnableSearch()
            .SearchPlaceholderText(T("(Type to search)"))
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type Table.Column…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Markup.Escape(UiApp.Fit(Describe(c), 6))
            })
            .AddChoices(edges.Select(e => e.Table + "." + e.Column).Append(typed).Append(back)));
        string Describe(string key)
        {
            var e = edges.First(x => x.Table + "." + x.Column == key);
            var p = session.Plan.PolicyFor(e.Table, e.Column);
            return $"{key} → {e.RefTable}   {p ?? Current(e.Table, e.Column)}{(p != null ? "" : " " + T("(default)"))}";
        }
        if (choice == back) return;
        if (choice == typed)
            choice = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Table.Column (Table may be *):"))).Validate(s =>
                s.Trim().Split('.') is [var tt, var cc] && (tt == "*" || SqlDialect.IsSafeName(tt)) && SqlDialect.IsSafeName(cc)
                    ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("Expected Table.Column."))))).Trim();
        var parts = choice.Split('.');
        const string def = "default";
        var policy = AnsiConsole.Prompt(Select<string>()
            .Title(F("Policy for {0}:", choice))
            .UseConverter(p => Markup.Escape(p switch
            {
                RefPolicy.Pull => T("pull — take the referenced rows into the archive"),
                RefPolicy.Expect => T("expect — they must exist on the target (checked before writing)"),
                RefPolicy.Author => T("author — missing on the target: the importing user's contact"),
                RefPolicy.Null => T("null — missing on the target: empty"),
                RefPolicy.Drop => T("drop — missing on the target: the row is not written"),
                _ => T("default")
            }))
            .AddChoices(RefPolicy.All.Append(def)));
        session.Plan.Refs.RemoveAll(r => r.Table.Equals(parts[0], StringComparison.OrdinalIgnoreCase) && DataPlan.SameColumn(r.Column, parts[1]));
        if (policy != def) session.Plan.Refs.Add((parts[0], parts[1], policy));
    }

    private void EditChildren(ExportSession session)
    {
        var chosen = new HashSet<string>(session.Plan.Tables.Select(t => t.Table), StringComparer.OrdinalIgnoreCase);
        var candidates = session.Report?.Refs
            .Where(r => chosen.Contains(r.RefTable) && !r.Table.Equals(r.RefTable, StringComparison.OrdinalIgnoreCase) && r.Column is not ("CreatedBy" or "ModifiedBy"))
            .Select(r => r.Table + "." + r.Column).Distinct().ToList() ?? [];
        foreach (var (t, c) in session.Plan.Children) if (!candidates.Contains(t + "." + c)) candidates.Add(t + "." + c);
        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("No table of the packages points at the chosen tables. A plan file can name other children (children: Table.Column)."))}[/]");
            UiInput.Pause();
            return;
        }
        var prompt = Multi<string>()
            .Title(T("Which rows that point at the chosen rows should go too?"))
            .PageSize(15)
            .NotRequired()
            .UseConverter(c => Markup.Escape(c))
            .AddChoices(candidates);
        foreach (var (t, c) in session.Plan.Children) prompt.Select(t + "." + c);
        var picked = AnsiConsole.Prompt(prompt);
        session.Plan.Children.Clear();
        foreach (var p in picked)
        {
            var parts = p.Split('.');
            session.Plan.Children.Add((parts[0], parts[1]));
            if (!session.Plan.Tables.Any(t => t.Table.Equals(parts[0], StringComparison.OrdinalIgnoreCase)))
            {
                session.Plan.Tables.Add((parts[0], new TableMode(TableModeKind.Children)));
                session.Tables?.SetMode(parts[0], new TableMode(TableModeKind.Children));
            }
        }
    }

    // ------------------------------------------------------------------ import

    private async Task ImportAsync(string? archive)
    {
        var target = await envs.PickAsync(StandPurpose.Target, T("Import table data into which stand?"));
        if (target == null) return;
        Header(target, T("Import data"));
        archive ??= PickFile(T("Which data archive?"), state.RecentDataArchives, "data", "*.zip");
        if (archive == null) return;
        DataManifest manifest;
        try { manifest = DataArchive.ReadManifest(archive); }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Error(T("Not a table data archive:") + " " + ex.Message);
            UiInput.Pause();
            return;
        }
        UiState.Remember(state.RecentDataArchives, archive);
        ShowManifest(manifest);
        if (!await EnsureGateAsync(target)) return;

        var mode = AnsiConsole.Prompt(Select<DataImportMode>()
            .Title(T("Rows that already exist on the target:"))
            .UseConverter(m => Markup.Escape(m == DataImportMode.Insert
                ? T("insert — add only new rows, leave existing ones as they are")
                : T("upsert — add new rows and overwrite existing ones (old versions are backed up)")))
            .AddChoices(DataImportMode.Insert, DataImportMode.Upsert));
        var onUnique = AnsiConsole.Prompt(Select<UniqueConflictAction>()
            .Title(T("A new row whose unique values the target already has (other Id):"))
            .UseConverter(u => Markup.Escape(u == UniqueConflictAction.Stop ? T("stop — write nothing and show them") : T("skip — leave such rows out")))
            .AddChoices(UniqueConflictAction.Stop, UniqueConflictAction.Skip));

        var importer = new DataImporter(target.Client);
        DataImportResult? dry;
        try { dry = await WithStatusAsync(T("Dry run (everything is rolled back)..."), p => importer.ImportAsync(archive, mode, true, null, p, onUnique)); }
        catch (Exception ex) when (ex is CreatioException or GateException or GateMissingException or FormatException)
        {
            Error(ex.Message);
            UiInput.Pause();
            return;
        }
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(T("Dry run (rolled back):"))}[/]");
        if (!ShowImportResult(dry!))
        {
            UiInput.Pause();
            return;
        }
        if (!UiInput.Confirm(F("Write into {0}?", target.Name), false)) return;

        DataImportResult? real;
        try { real = await WithStatusAsync(T("Importing..."), p => importer.ImportAsync(archive, mode, false, null, p, onUnique)); }
        catch (Exception ex) when (ex is CreatioException or GateException or GateMissingException or FormatException)
        {
            Error(T("The import failed and was rolled back:") + " " + ex.Message);
            UiInput.Pause();
            return;
        }
        if (ShowImportResult(real!) && real!.Committed)
        {
            if (real.WorkDir != null)
            {
                UiState.Remember(state.RecentDataWorkDirs, real.WorkDir);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(F("Written. Backup for Restore data: {0}", real.WorkDir))}[/]");
            }
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(T("Creatio caches lookups and settings: clear Redis or restart the application if the imported tables are cached."))}[/]");
        }
        UiInput.Pause();
    }

    /// <summary>Prints an import result; false when it has blocking problems (nothing written).</summary>
    private static bool ShowImportResult(DataImportResult r)
    {
        foreach (var e in r.Errors) Error(e);
        foreach (var m in r.Missing)
            Error(F("Missing: {0}.{1} → {2} ({3}): {4} ids on neither the target nor the archive, e.g. {5}", m.Table, m.Column, m.RefTable, m.Policy, m.Count, string.Join(", ", m.Samples)));
        foreach (var u in r.Unique)
            AnsiConsole.MarkupLine((u.Skipped ? "[yellow]" : "[red]") + Markup.Escape(F("{0}: index {1} ({2}): {3} new rows have values the target already has, e.g. {4}",
                u.Skipped ? T("Skipped") : T("Unique"), u.Index, string.Join(", ", u.Columns), u.Count, string.Join("; ", u.Samples))) + "[/]");
        foreach (var w in r.Warnings) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(T("Warning:"))}[/] {Markup.Escape(w)}");
        bool ok = r.Errors.Count == 0 && r.Missing.Count == 0 && !r.Unique.Any(u => !u.Skipped);
        if (!ok)
        {
            AnsiConsole.MarkupLine(Markup.Escape(T("Nothing was written. Change the reference policies in the plan (null, drop, author), pull the rows, or leave conflicting rows out.")));
            return false;
        }
        var t = new Table().Border(TableBorder.Rounded)
            .AddColumn(Markup.Escape(T("Table"))).AddColumn(new TableColumn(Markup.Escape(T("Archive"))).RightAligned())
            .AddColumn(new TableColumn(Markup.Escape(T("New"))).RightAligned()).AddColumn(new TableColumn(Markup.Escape(T("Updated"))).RightAligned())
            .AddColumn(Markup.Escape(T("Notes")));
        foreach (var x in r.Tables)
        {
            var notes = new List<string>();
            if (x.AuthorReplaced > 0) notes.Add(F("author replaced: {0}", x.AuthorReplaced));
            if (x.Nulled > 0) notes.Add(F("references emptied: {0}", x.Nulled));
            if (x.Dropped > 0) notes.Add(F("rows dropped: {0}", x.Dropped));
            if (x.DeferredColumns.Count > 0) notes.Add(F("set after insert: {0}", string.Join(", ", x.DeferredColumns)));
            t.AddRow(Markup.Escape(x.Name), x.ArchiveRows.ToString(), x.Inserted.ToString(), x.Updated.ToString(), Markup.Escape(string.Join("; ", notes)));
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(F("{0:0.0} s", r.Ms / 1000.0))}[/]");
        return true;
    }

    // ------------------------------------------------------------------ restore

    private async Task RestoreAsync()
    {
        var target = await envs.PickAsync(StandPurpose.Target, T("Restore table data on which stand?"));
        if (target == null) return;
        Header(target, T("Restore data"));
        const string typed = "\u0000typed", back = "\u0000back";
        // Only backups made on this stand: a backup holds Ids of one stand (Restore on another would delete its rows).
        var dirs = state.RecentDataWorkDirs.Where(d => DataImporter.BackupStand(d) is { } url && DataImporter.SameStand(url, target.Client.Url)).ToList();
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(F("Undo which data import made on {0}?", target.Name))
            .PageSize(12)
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type a path…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Markup.Escape(UiApp.Fit($"{Directory.GetLastWriteTime(c):yyyy-MM-dd HH:mm}  {c}", 6))
            })
            .AddChoices(dirs.Append(typed).Append(back)));
        if (choice == back) return;
        if (choice == typed)
            choice = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Work dir (holds restore-db.json):"))).Validate(p =>
                File.Exists(DataRestoreInfo.PathIn(p.Trim().Trim('"'))) ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("No restore-db.json there."))))).Trim().Trim('"');
        AnsiConsole.MarkupLine(Markup.Escape(T("Rows the import inserted are deleted, rows it updated get their previous values back. Rows deleted by FK cascades are not brought back.")));
        if (!UiInput.Confirm(F("Undo that import on {0}?", target.Name), false)) return;
        if (!await EnsureGateAsync(target)) return;
        try
        {
            var lines = await WithStatusAsync(T("Restoring..."), p => new DataImporter(target.Client).RestoreAsync(choice, p));
            foreach (var l in lines!) AnsiConsole.MarkupLine("  " + Markup.Escape(l));
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(T("Restored."))}[/]");
        }
        catch (TransferException ex)
        {
            foreach (var e in ex.Errors) Error(e);
        }
        catch (Exception ex) when (ex is CreatioException or GateException or GateMissingException or IOException or FormatException)
        {
            Error(T("Restore failed and was rolled back:") + " " + ex.Message);
        }
        UiInput.Pause();
    }

    // ------------------------------------------------------------------ files

    /// <summary>Recent files plus those in &lt;settings folder&gt;\&lt;folder&gt;, newest first; or a typed path.</summary>
    private string? PickFile(string title, List<string> recent, string folder, string mask)
    {
        const string typed = "\u0000typed", back = "\u0000back";
        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        void Add(string p) { try { if (File.Exists(p)) found[Path.GetFullPath(p)] = File.GetLastWriteTime(p); } catch (Exception ex) when (ex is IOException or ArgumentException) { } }
        foreach (var p in recent) Add(p);
        var dir = Path.Combine(baseDir, folder);
        if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, mask)) Add(f);
        var files = found.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key).ToList();
        var choice = AnsiConsole.Prompt(Select<string>()
            .Title(title)
            .PageSize(15)
            .UseConverter(c => c switch
            {
                typed => $"[grey]{Markup.Escape(T("Type a path…"))}[/]",
                back => $"[grey]{Markup.Escape(T("← Back"))}[/]",
                _ => Markup.Escape(UiApp.Fit($"{File.GetLastWriteTime(c):yyyy-MM-dd HH:mm}  {Path.GetFileName(c)}  ({Path.GetDirectoryName(c)})", 6))
            })
            .AddChoices(files.Append(typed).Append(back)));
        if (choice == back) return null;
        if (choice == typed)
            choice = AnsiConsole.Prompt(L.Text(Markup.Escape(T("Path:"))).Validate(p =>
                File.Exists(p.Trim().Trim('"')) ? ValidationResult.Success() : ValidationResult.Error(Markup.Escape(T("No such file."))))).Trim().Trim('"');
        return Path.GetFullPath(choice);
    }
}
