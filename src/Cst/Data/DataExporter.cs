using System.Text.Json.Nodes;
using Cst.Creatio;
using Cst.Transfer;

namespace Cst.Data;

/// <summary>
/// db-export: reads table rows through CstGate by a <see cref="DataPlan"/>, pulls referenced rows until the selection is
/// closed under the "pull" references (docs/db-transfer.md, 5.2), adds children and companion tables (record rights,
/// translations) and writes a table data archive.
/// </summary>
public sealed class DataExporter(CreatioClient client, DataSettings settings)
{
    private readonly GateClient _gate = new(client);
    private readonly EntityCatalog _entities = new(client);
    private SqlDialect _sql = null!;
    private Dictionary<string, List<DbColumn>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TableState> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    private sealed class TableState(string name)
    {
        public string Name { get; } = name;
        public TableMode Mode { get; set; } = TableMode.Full;
        public string ModeText { get; set; } = "full";
        public string? CompanionOf { get; set; }
        public List<string> Columns { get; set; } = [];
        public List<GateColumn> Types { get; set; } = [];
        public int IdIndex { get; set; } = -1;
        public List<JsonArray> Rows { get; } = [];
        public HashSet<Guid> Ids { get; } = [];
        /// <summary>Ids asked for by references that the source does not have (reported, not asked again).</summary>
        public HashSet<Guid> Missing { get; } = [];
        public long Selected { get; set; }
        public List<DataEdge> Edges { get; } = [];

        public int Add(IEnumerable<JsonArray> rows)
        {
            int added = 0;
            foreach (var r in rows)
            {
                if (IdIndex >= 0 && Guid.TryParse(DbCatalog.Str(r[IdIndex]), out var id) && !Ids.Add(id)) continue;
                Rows.Add(r);
                added++;
            }
            return added;
        }
    }

    public async Task<DataManifest> ExportAsync(DataPlan plan, string output, Action<string>? progress = null)
    {
        progress ??= _ => { };
        var info = await _gate.InfoAsync();
        if (!info.CanUse) throw new GateException($"User {info.User} has no CanManageSolution: CstGate refuses SQL.");
        _sql = new SqlDialect(info.Engine ?? "MSSql");

        // 1. Which tables: the plan's list, or the entities created in its packages.
        var chosen = plan.Tables.ToList();
        if (chosen.Count == 0)
        {
            if (plan.Packages.Count == 0) throw new ArgumentException("The plan has neither tables: nor db packages:.");
            var all = await new Catalog(client).LoadPackagesAsync();
            var pkgs = new List<PackageInfo>();
            foreach (var name in plan.Packages)
                pkgs.Add(all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? throw new TransferException([$"Package '{name}' is not on the stand."]));
            foreach (var e in (await _entities.FindEntitiesAsync(pkgs)).Where(e => e.OwnRoot))
                chosen.Add((e.Name, TableMode.Full));
        }
        var names = chosen.Select(c => c.Table).Concat(plan.Children.Select(c => c.Table)).ToList();
        await LoadColumnsAsync(names.SelectMany(n => new[] { n, "Sys" + n + "Right", "Sys" + n + "Lcz" }));
        var missing = chosen.Where(c => !_columns.ContainsKey(c.Table)).Select(c => $"Table '{c.Table}' is not on the source.").ToList();
        if (missing.Count > 0) throw new TransferException(missing);

        // 2. Initial rows by mode (a full table above the size threshold is NOT cut silently: the plan decides).
        foreach (var (table, mode) in chosen)
        {
            var t = State(table);
            t.Mode = mode;
            t.ModeText = mode.ToString();
            if (mode.Kind is TableModeKind.Skip or TableModeKind.Children) continue;
            progress($"Reading {table} ({t.ModeText})...");
            t.Selected = t.Add(await FetchByModeAsync(t, mode));
        }
        foreach (var (child, _) in plan.Children)
            if (!_tables.ContainsKey(child)) { var t = State(child); t.Mode = new(TableModeKind.Children); t.ModeText = "children"; }

        // 3. References from metadata (+ plan overrides), then the closure.
        foreach (var t in _tables.Values.ToList()) await AddEdgesAsync(t, plan);
        await CloseAsync(plan, progress);

        // 4. Companions: record rights and translations of every exported main table.
        foreach (var t in _tables.Values.Where(t => t.CompanionOf == null && t.Ids.Count > 0).ToList())
        {
            if (plan.Rights) await AddCompanionAsync(t, "Sys" + t.Name + "Right", progress);
            if (plan.Translations) await AddCompanionAsync(t, "Sys" + t.Name + "Lcz", progress);
        }

        // 5. Archive.
        var manifest = new DataManifest
        {
            SourceUrl = client.Url, SourceEngine = _sql.Engine, SourceDatabase = info.Database, GateVersion = info.Version
        };
        var rows = new Dictionary<string, List<JsonArray>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tables.Values.Where(t => t.Rows.Count > 0 || t.Mode.Kind != TableModeKind.Skip))
        {
            if (t.Mode.Kind == TableModeKind.Skip) continue;
            manifest.Tables.Add(new DataTableEntry
            {
                Name = t.Name, Mode = t.ModeText, CompanionOf = t.CompanionOf, SelectedRows = t.Selected,
                Columns = t.Types, Edges = t.Edges
            });
            rows[t.Name] = t.Rows;
        }
        manifest.Warnings.AddRange(_warnings);
        progress($"Writing {output}...");
        DataArchive.Write(output, manifest, plan.ToText(), rows);
        return manifest;
    }

    private async Task LoadColumnsAsync(IEnumerable<string> tables)
    {
        var need = tables.Where(t => !_columns.ContainsKey(t)).ToList();
        if (need.Count == 0) return;
        foreach (var (k, v) in await new DbCatalog(_gate, _sql).LoadColumnsAsync(need)) _columns[k] = v;
    }

    private TableState State(string table)
    {
        if (_tables.TryGetValue(table, out var t)) return t;
        t = new TableState(table);
        var cols = _columns.TryGetValue(table, out var c) ? c : throw new TransferException([$"Table '{table}' is not on the source."]);
        t.Columns = cols.Where(x => x.Writable).Select(x => x.Name).ToList();
        t.IdIndex = t.Columns.FindIndex(x => x.Equals("Id", StringComparison.OrdinalIgnoreCase));
        _tables[table] = t;
        return t;
    }

    private void SetTypes(TableState t, GateResult res)
    {
        if (t.Types.Count == 0 && res.Columns.Count > 0) t.Types = res.Columns;
    }

    private async Task<List<JsonArray>> FetchByModeAsync(TableState t, TableMode mode)
    {
        switch (mode.Kind)
        {
            case TableModeKind.Full: return await FetchPagedAsync(t, null);
            case TableModeKind.Where: return await FetchPagedAsync(t, "(" + mode.Where + ")");
            case TableModeKind.Ids: return await FetchByIdsAsync(t, "Id", mode.Ids!);
            case TableModeKind.Top:
            {
                if (!t.Columns.Contains(mode.OrderColumn!, StringComparer.OrdinalIgnoreCase))
                    throw new TransferException([$"{t.Name}: no column '{mode.OrderColumn}' to order the top rows by."]);
                var res = await _gate.QueryAsync(_sql.Select(t.Name, t.Columns, null, _sql.Q(mode.OrderColumn!) + (mode.Descending ? " DESC" : " ASC"), mode.Top));
                SetTypes(t, res);
                return res.Rows;
            }
            default: return [];
        }
    }

    /// <summary>All rows (optionally filtered), keyset pages by Id.</summary>
    private async Task<List<JsonArray>> FetchPagedAsync(TableState t, string? where)
    {
        if (t.IdIndex < 0)
        {
            var all = await _gate.QueryAsync(_sql.Select(t.Name, t.Columns, where, null, null));
            SetTypes(t, all);
            _warnings.Add($"{t.Name}: no Id column, read in one query.");
            return all.Rows;
        }
        var result = new List<JsonArray>();
        Guid? after = null;
        while (true)
        {
            var cond = after == null ? where : (where != null ? where + " AND " : "") + $"{_sql.Q("Id")} > @after";
            var p = after == null ? null : new JsonObject { ["after"] = new JsonObject { ["type"] = "guid", ["value"] = after.Value.ToString() } };
            var res = await _gate.QueryAsync(_sql.Select(t.Name, t.Columns, cond, _sql.Q("Id"), settings.PageSize), p);
            SetTypes(t, res);
            result.AddRange(res.Rows);
            if (res.Rows.Count < settings.PageSize) break;
            after = Guid.Parse(DbCatalog.Str(res.Rows[^1][t.IdIndex]));
        }
        return result;
    }

    private async Task<List<JsonArray>> FetchByIdsAsync(TableState t, string column, IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        var result = new List<JsonArray>();
        for (int i = 0; i < list.Count; i += 1000)
        {
            var res = await _gate.QueryAsync(_sql.Select(t.Name, t.Columns, $"{_sql.Q(column)} IN ({SqlDialect.GuidList(list.Skip(i).Take(1000))})", null, null));
            SetTypes(t, res);
            result.AddRange(res.Rows);
        }
        return result;
    }

    /// <summary>Lookup references of a table from the runtime entity schema (physical column = lookup name + "Id").</summary>
    private async Task AddEdgesAsync(TableState t, DataPlan plan)
    {
        if (t.CompanionOf != null || t.Edges.Count > 0) return;
        var entity = await _entities.GetEntityAsync(t.Name);
        if (entity == null) { _warnings.Add($"{t.Name}: no runtime entity — its references are not followed."); return; }
        foreach (var c in entity.Columns.Where(c => c.IsLookup))
        {
            var physical = t.Columns.FirstOrDefault(x => x.Equals(c.Name + "Id", StringComparison.OrdinalIgnoreCase))
                           ?? t.Columns.FirstOrDefault(x => x.Equals(c.Name, StringComparison.OrdinalIgnoreCase));
            if (physical == null) continue;
            var refTable = c.ReferenceSchema!;
            var policy = plan.PolicyFor(t.Name, physical) ?? DefaultPolicy(t.Name, physical, refTable);
            t.Edges.Add(new DataEdge(physical, refTable, policy, c.IsRequired));
        }
    }

    private string DefaultPolicy(string table, string column, string refTable)
    {
        if (column is "CreatedById" or "ModifiedById") return RefPolicy.Author;
        if (refTable.Equals(table, StringComparison.OrdinalIgnoreCase)) return RefPolicy.Pull;
        if (_tables.TryGetValue(refTable, out var r) && r.Mode.Kind != TableModeKind.Skip) return RefPolicy.Pull;
        return RefPolicy.Expect;
    }

    /// <summary>Pulls referenced rows and children until nothing new appears.</summary>
    private async Task CloseAsync(DataPlan plan, Action<string> progress)
    {
        var childSeen = new Dictionary<(string, string), HashSet<Guid>>();
        bool changed = true;
        for (int pass = 1; changed; pass++)
        {
            changed = false;
            foreach (var t in _tables.Values.ToList())
            {
                foreach (var e in t.Edges.Where(e => e.Policy == RefPolicy.Pull))
                {
                    int col = t.Columns.FindIndex(x => x.Equals(e.Column, StringComparison.OrdinalIgnoreCase));
                    if (col < 0) continue;
                    if (!_columns.ContainsKey(e.RefTable)) await LoadColumnsAsync([e.RefTable]);
                    if (!_columns.ContainsKey(e.RefTable)) { _warnings.Add($"{t.Name}.{e.Column} → {e.RefTable}: no such table on the source (DB view?), not pulled."); continue; }
                    var r = State(e.RefTable);
                    if (r.Mode.Kind == TableModeKind.Skip) continue;
                    var needed = t.Rows.Select(row => DbCatalog.Str(row[col])).Where(s => s.Length > 0)
                        .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                        .Where(g => g != Guid.Empty && !r.Ids.Contains(g) && !r.Missing.Contains(g)).Distinct().ToList();
                    if (needed.Count == 0) continue;
                    if (needed.Count > settings.MaxPullRows)
                        throw new TransferException([
                            $"{t.Name}.{e.Column} → {e.RefTable} would pull {needed.Count} rows (more than MaxPullRows {settings.MaxPullRows}).",
                            $"Set a policy for it in the plan (refs: - {t.Name}.{e.Column} expect | drop | null), narrow {t.Name}, or raise DbData.MaxPullRows."]);
                    if (r.ModeText == "full" && !plan.Tables.Any(p => p.Table.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
                    { r.Mode = new(TableModeKind.Children); r.ModeText = "pulled"; await AddEdgesAsync(r, plan); }
                    progress($"Pulling {needed.Count} {e.RefTable} rows referenced by {t.Name}.{e.Column}...");
                    var got = await FetchByIdsAsync(r, "Id", needed);
                    int added = r.Add(got);
                    foreach (var id in needed.Where(id => !r.Ids.Contains(id))) r.Missing.Add(id);
                    if (added > 0) changed = true;
                }
            }
            foreach (var (child, column) in plan.Children)
            {
                var c = _tables[child];
                int col = c.Columns.FindIndex(x => DataPlan.SameColumn(column, x));
                if (col < 0) throw new TransferException([$"children: {child} has no column {column}."]);
                var edge = c.Edges.FirstOrDefault(e => e.Column.Equals(c.Columns[col], StringComparison.OrdinalIgnoreCase))
                           ?? throw new TransferException([$"children: {child}.{column} is not a lookup column."]);
                if (!_tables.TryGetValue(edge.RefTable, out var parent)) continue;
                var key = (child, c.Columns[col]);
                if (!childSeen.TryGetValue(key, out var seen)) childSeen[key] = seen = [];
                var fresh = parent.Ids.Where(seen.Add).ToList();
                if (fresh.Count == 0) continue;
                progress($"Reading {child} rows of {fresh.Count} {parent.Name} rows...");
                if (c.Add(await FetchByIdsAsync(c, c.Columns[col], fresh)) > 0) changed = true;
            }
            if (pass > 50) throw new TransferException(["The reference closure did not settle after 50 passes."]);
        }
        foreach (var t in _tables.Values.Where(t => t.Missing.Count > 0))
            _warnings.Add($"{t.Name}: {t.Missing.Count} referenced rows are not on the source (dangling references), e.g. {t.Missing.First()}.");
    }

    private async Task AddCompanionAsync(TableState main, string table, Action<string> progress)
    {
        if (!_columns.ContainsKey(table) || _tables.ContainsKey(table)) return;
        var c = State(table);
        c.CompanionOf = main.Name;
        c.ModeText = table.EndsWith("Right", StringComparison.Ordinal) ? "rights" : "translations";
        if (!c.Columns.Contains("RecordId", StringComparer.OrdinalIgnoreCase)) { _tables.Remove(table); return; }
        progress($"Reading {table} of {main.Ids.Count} {main.Name} rows...");
        c.Selected = c.Add(await FetchByIdsAsync(c, "RecordId", main.Ids));
        if (c.Rows.Count == 0) { _tables.Remove(table); return; }
        c.Edges.Add(new DataEdge("RecordId", main.Name, RefPolicy.Drop, true));
        if (c.Columns.Contains("SysAdminUnitId", StringComparer.OrdinalIgnoreCase)) c.Edges.Add(new DataEdge("SysAdminUnitId", "SysAdminUnit", RefPolicy.Drop, true));
        if (c.Columns.Contains("SysCultureId", StringComparer.OrdinalIgnoreCase)) c.Edges.Add(new DataEdge("SysCultureId", "SysCulture", RefPolicy.Drop, true));
        foreach (var a in new[] { "CreatedById", "ModifiedById" })
            if (c.Columns.Contains(a, StringComparer.OrdinalIgnoreCase)) c.Edges.Add(new DataEdge(a, "Contact", RefPolicy.Author, false));
    }
}
