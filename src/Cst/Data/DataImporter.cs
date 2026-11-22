using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Creatio;

namespace Cst.Data;

public enum DataImportMode { Insert, Upsert }

/// <summary>Per-table outcome of a table data import.</summary>
public sealed class DataTableResult
{
    public string Name { get; set; } = "";
    public long ArchiveRows { get; set; }
    public long Inserted { get; set; }
    public long Updated { get; set; }
    public long Dropped { get; set; }
    public long AuthorReplaced { get; set; }
    public long Nulled { get; set; }
    public List<string> SkippedColumns { get; set; } = [];
    public List<string> DeferredColumns { get; set; } = [];
}

/// <summary>A reference that cannot be resolved on the target (blocks the import).</summary>
public sealed record MissingRef(string Table, string Column, string RefTable, string Policy, long Count, List<string> Samples);

/// <summary>A unique index of a target table (other than the primary key).</summary>
public sealed record UniqueIndex(string Name, List<string> Columns);

/// <summary>New rows whose values of a unique index already exist on the target under another row.</summary>
public sealed record UniqueConflict(string Table, string Index, List<string> Columns, long Count, List<string> Samples, bool Skipped);

public enum UniqueConflictAction { Stop, Skip }

public sealed class DataImportResult
{
    public bool DryRun { get; set; }
    public bool Committed { get; set; }
    public List<DataTableResult> Tables { get; set; } = [];
    public List<MissingRef> Missing { get; set; } = [];
    public List<UniqueConflict> Unique { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? WorkDir { get; set; }
    public long Ms { get; set; }
}

/// <summary>What db-restore needs: per table (in import order) the inserted Ids and the previous versions of updated rows.</summary>
public sealed class DataRestoreInfo
{
    public string Kind { get; set; } = "db-data";
    public string TargetUrl { get; set; } = "";
    public string Archive { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<DataRestoreTable> Tables { get; set; } = [];

    public static string PathIn(string workDir) => Path.Combine(workDir, "restore-db.json");
}

public sealed class DataRestoreTable
{
    public string Name { get; set; } = "";
    public List<Guid> Inserted { get; set; } = [];
    public List<string> DeferredColumns { get; set; } = [];
    public List<GateColumn> UpdatedColumns { get; set; } = [];
    public string? UpdatedRowsFile { get; set; }
}

/// <summary>
/// db-import: writes a table data archive into the target through one CstGate session (one transaction): staging tables
/// with the target's own column types → bulk load → reference policies and checks → INSERT new rows (and UPDATE existing
/// ones in upsert mode) in dependency order; cyclic references go in with NULL and are set afterwards. Dry run = the same,
/// rolled back. Before committing, the inserted Ids and previous versions of updated rows go to the work dir.
/// </summary>
public sealed class DataImporter(CreatioClient client)
{
    private readonly GateClient _gate = new(client);
    private SqlDialect _sql = null!;

    private sealed class Plan
    {
        public required DataTableEntry Entry;
        public required int Index;
        public required List<string> InsertColumns;   // target names, in archive order
        public required List<int> ArchiveIndexes;     // archive column index of each insert column
        public required bool HasId;
        public List<string> Deferred { get; } = [];
        /// <summary>Columns a row is matched by on the target (Id; natural key for translations and rights).</summary>
        public List<string> Key { get; set; } = ["Id"];
        public List<UniqueIndex> Uniques { get; } = [];
        public string Stage => "cst_s" + Index;
        public string NewIds => "cst_n" + Index;
        public DataTableResult Result { get; } = new();
    }

    public async Task<DataImportResult> ImportAsync(string archive, DataImportMode mode, bool dryRun, string? workDir, Action<string>? progress = null,
        UniqueConflictAction onUnique = UniqueConflictAction.Stop)
    {
        progress ??= _ => { };
        var started = DateTime.UtcNow;
        var result = new DataImportResult { DryRun = dryRun };
        var manifest = DataArchive.ReadManifest(archive);
        var info = await _gate.InfoAsync();
        if (!info.CanUse) throw new GateException($"User {info.User} has no CanManageSolution: CstGate refuses SQL.");
        _sql = new SqlDialect(info.Engine ?? "MSSql");
        var me = info.ContactId;

        // 1. Target structure.
        var entries = manifest.Tables.Where(t => t.Rows > 0).ToList();
        var refTables = entries.SelectMany(t => t.Edges.Select(e => e.RefTable));
        var target = await new DbCatalog(_gate, _sql).LoadColumnsAsync(entries.Select(t => t.Name).Concat(refTables));
        var plans = new List<Plan>();
        foreach (var e in entries)
        {
            if (!target.TryGetValue(e.Name, out var cols)) { result.Errors.Add($"Table {e.Name} is not on the target (install its package first)."); continue; }
            var writable = cols.Where(c => c.Writable).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var insert = new List<string>();
            var idx = new List<int>();
            var skipped = new List<string>();
            for (int i = 0; i < e.Columns.Count; i++)
            {
                if (writable.TryGetValue(e.Columns[i].Name, out var tc)) { insert.Add(tc.Name); idx.Add(i); }
                else skipped.Add(e.Columns[i].Name);
            }
            foreach (var req in cols.Where(c => c.Writable && !c.Nullable && !c.HasDefault && !insert.Contains(c.Name, StringComparer.OrdinalIgnoreCase)))
                result.Errors.Add($"{e.Name}.{req.Name} is required on the target but not in the archive.");
            var p = new Plan { Entry = e, Index = plans.Count, InsertColumns = insert, ArchiveIndexes = idx, HasId = insert.Contains("Id", StringComparer.OrdinalIgnoreCase) };
            p.Result.Name = e.Name;
            p.Result.ArchiveRows = e.Rows;
            p.Result.SkippedColumns = skipped;
            if (skipped.Count > 0) result.Warnings.Add($"{e.Name}: columns not on the target, not written: {string.Join(", ", skipped)}.");
            if (!p.HasId) result.Warnings.Add($"{e.Name}: no Id column — rows are inserted as they are (duplicates possible, no restore).");
            // Translations and record rights are matched by their natural key: the target has its own rows (own Ids)
            // for the same record and culture / role and operation.
            string[] natural = e.CompanionOf == null ? [] : e.Name.EndsWith("Lcz", StringComparison.Ordinal)
                ? ["RecordId", "SysCultureId"] : ["RecordId", "SysAdminUnitId", "Operation"];
            if (natural.Length > 0 && natural.All(k => insert.Contains(k, StringComparer.OrdinalIgnoreCase)))
                p.Key = natural.Select(k => insert.First(c => c.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            plans.Add(p);
        }
        if (result.Errors.Count > 0) return Finish(result, started);
        var uniques = await LoadUniqueIndexesAsync(plans.Select(p => p.Entry.Name));
        foreach (var p in plans)
            if (uniques.TryGetValue(p.Entry.Name, out var list))
                p.Uniques.AddRange(list.Where(u => u.Columns.All(c => p.InsertColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                                                   && !u.Columns.SequenceEqual(p.Key, StringComparer.OrdinalIgnoreCase)));

        // 2. Order: referenced tables first; a cycle is broken at a nullable column (set after the inserts).
        plans = Order(plans, target, result);
        if (result.Errors.Count > 0) return Finish(result, started);
        result.Tables = plans.Select(p => p.Result).ToList();
        var byName = plans.ToDictionary(p => p.Entry.Name, StringComparer.OrdinalIgnoreCase);

        var session = new GateSession(_gate);
        try
        {
            // 3. Staging + load.
            foreach (var p in plans)
            {
                progress($"Loading {p.Entry.Name} ({p.Entry.Rows} rows)...");
                var rows = DataArchive.ReadRows(archive, p.Entry);
                var load = new JsonArray();
                foreach (var r in rows)
                {
                    var row = new JsonArray();
                    foreach (var i in p.ArchiveIndexes) row.Add(r[i]?.DeepClone());
                    load.Add(row);
                }
                var types = p.ArchiveIndexes.Select((ai, k) => new GateColumn(p.InsertColumns[k], p.Entry.Columns[ai].Type)).ToList();
                await session.RunAsync([
                    new GateBatch(_sql.CreateStage(p.Stage, p.Entry.Name, p.InsertColumns)),
                    GateBatch.LoadRows(_sql.Temp(p.Stage), types, load)]);
            }

            // 4. Reference policies, then checks.
            progress("Checking references...");
            var policyBatches = new List<(GateBatch Batch, Plan Plan, string Policy)>();
            var checkBatches = new List<(GateBatch Count, GateBatch Sample, Plan Plan, DataEdge Edge)>();
            foreach (var p in plans)
                foreach (var e in p.Entry.Edges)
                {
                    if (!p.InsertColumns.Contains(e.Column, StringComparer.OrdinalIgnoreCase)) continue;
                    var cond = MissingCondition(p, e, target, byName);
                    var meParam = new JsonObject { ["me"] = new JsonObject { ["type"] = "guid", ["value"] = me.ToString() } };
                    switch (e.Policy)
                    {
                        case RefPolicy.Author:
                            policyBatches.Add((new GateBatch(UpdateStage(p, $"{_sql.Q(e.Column)} = @me", cond), meParam), p, e.Policy)); break;
                        case RefPolicy.Null:
                            policyBatches.Add((new GateBatch(UpdateStage(p, $"{_sql.Q(e.Column)} = NULL", cond)), p, e.Policy)); break;
                        case RefPolicy.Drop:
                            policyBatches.Add((new GateBatch(DeleteStage(p, cond)), p, e.Policy)); break;
                        default:
                            var from = $"FROM {_sql.Temp(p.Stage)} s WHERE {cond}";
                            checkBatches.Add((
                                new GateBatch($"SELECT COUNT(DISTINCT s.{_sql.Q(e.Column)}) AS n {from}", ReturnRows: true),
                                new GateBatch(_sql.Pg
                                    ? $"SELECT DISTINCT s.{_sql.Q(e.Column)} {from} LIMIT 3"
                                    : $"SELECT DISTINCT TOP 3 s.{_sql.Q(e.Column)} {from}", ReturnRows: true),
                                p, e));
                            break;
                    }
                }
            if (policyBatches.Count > 0)
            {
                var r = await session.RunAsync(policyBatches.Select(b => b.Batch).ToList());
                for (int i = 0; i < policyBatches.Count; i++)
                {
                    var (_, p, policy) = policyBatches[i];
                    long n = Math.Max(0, r.Affected[i]);
                    if (policy == RefPolicy.Author) p.Result.AuthorReplaced += n;
                    else if (policy == RefPolicy.Null) p.Result.Nulled += n;
                    else p.Result.Dropped += n;
                }
            }
            if (checkBatches.Count > 0)
            {
                var list = checkBatches.SelectMany(c => new[] { c.Count, c.Sample }).ToList();
                var r = await session.RunAsync(list);
                for (int i = 0; i < checkBatches.Count; i++)
                {
                    long n = DbCatalog.Int(r.Results[2 * i].Rows[0][0]);
                    if (n == 0) continue;
                    var samples = r.Results[2 * i + 1].Rows.Select(x => DbCatalog.Str(x[0])).ToList();
                    var (_, _, p, e) = checkBatches[i];
                    result.Missing.Add(new MissingRef(p.Entry.Name, e.Column, e.RefTable, e.Policy, n, samples));
                }
            }
            if (result.Missing.Count > 0)
            {
                await session.RollbackAsync();
                return Finish(result, started);
            }

            // 5. Write in order.
            var restore = new DataRestoreInfo { TargetUrl = client.Url, Archive = Path.GetFullPath(archive) };
            foreach (var p in plans)
            {
                progress($"Writing {p.Entry.Name}...");
                var rt = new DataRestoreTable { Name = p.Entry.Name, DeferredColumns = p.Deferred.ToList() };
                restore.Tables.Add(rt);
                var t = _sql.Q(p.Entry.Name);
                var stage = _sql.Temp(p.Stage);
                // A deferred column goes in as NULL of the column's own type: a bare NULL in a SELECT list is text on
                // PostgreSQL 10+ and would not fit a uuid column.
                var sel = string.Join(", ", p.InsertColumns.Select(c => p.Deferred.Contains(c, StringComparer.OrdinalIgnoreCase)
                    ? $"(CASE WHEN 1 = 0 THEN s.{_sql.Q(c)} END)" : "s." + _sql.Q(c)));
                if (!p.HasId)
                {
                    var r0 = await session.RunAsync([new GateBatch($"INSERT INTO {t} ({_sql.Cols(p.InsertColumns)}) SELECT {sel} FROM {stage} s")]);
                    p.Result.Inserted = Math.Max(0, r0.Affected[0]);
                    continue;
                }
                var id = _sql.Q("Id");
                var newIds = _sql.Temp(p.NewIds);
                string Match(IEnumerable<string> cols) => string.Join(" AND ", cols.Select(k => $"x.{_sql.Q(k)} = s.{_sql.Q(k)}"));
                var notOnTarget = $"NOT EXISTS (SELECT 1 FROM {t} x WHERE {Match(p.Key)})" +
                                  (p.Key is ["Id"] ? "" : $" AND NOT EXISTS (SELECT 1 FROM {t} x WHERE x.{id} = s.{id})");

                // New rows, then unique indexes: a new row whose unique values already exist on the target under another row.
                var check = new List<GateBatch>
                {
                    new(_sql.CreateIds(p.NewIds)),
                    new($"INSERT INTO {newIds} ({id}) SELECT s.{id} FROM {stage} s WHERE {notOnTarget}")
                };
                foreach (var u in p.Uniques)
                {
                    var from = $"FROM {stage} s JOIN {newIds} nn ON nn.{id} = s.{id} WHERE EXISTS (SELECT 1 FROM {t} x WHERE {Match(u.Columns)})";
                    check.Add(new GateBatch($"SELECT COUNT(*) {from}", ReturnRows: true));
                    check.Add(new GateBatch(_sql.Pg ? $"SELECT {_sql.Cols(u.Columns, "s")} {from} LIMIT 3" : $"SELECT TOP 3 {_sql.Cols(u.Columns, "s")} {from}", ReturnRows: true));
                }
                var rc = await session.RunAsync(check);
                var batches = new List<GateBatch>();
                for (int ui = 0; ui < p.Uniques.Count; ui++)
                {
                    long n = DbCatalog.Int(rc.Results[2 + 2 * ui].Rows[0][0]);
                    if (n == 0) continue;
                    var u = p.Uniques[ui];
                    var samples = rc.Results[3 + 2 * ui].Rows.Select(x => string.Join(" / ", x.Select(DbCatalog.Str))).ToList();
                    bool skip = onUnique == UniqueConflictAction.Skip;
                    result.Unique.Add(new UniqueConflict(p.Entry.Name, u.Name, u.Columns, n, samples, skip));
                    if (skip)
                        batches.Add(new GateBatch($"DELETE FROM {newIds} WHERE {id} IN (SELECT s.{id} FROM {stage} s WHERE EXISTS (SELECT 1 FROM {t} x WHERE {Match(u.Columns)}))"));
                }
                if (result.Unique.Any(u => !u.Skipped))
                {
                    await session.RollbackAsync();
                    return Finish(result, started);
                }

                int oldRowsBatch = -1, updateBatch = -1;
                var updateCols = p.InsertColumns.Where(c => !c.Equals("Id", StringComparison.OrdinalIgnoreCase) && !p.Key.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (mode == DataImportMode.Upsert && updateCols.Count > 0)
                {
                    oldRowsBatch = batches.Count;
                    batches.Add(new GateBatch($"SELECT {_sql.Cols(p.InsertColumns, "x")} FROM {t} x JOIN {stage} s ON {Match(p.Key)}", ReturnRows: true));
                    updateBatch = batches.Count;
                    batches.Add(new GateBatch(_sql.UpdateFrom(p.Entry.Name, p.Stage, updateCols,
                        $"NOT EXISTS (SELECT 1 FROM {newIds} n WHERE n.{id} = s.{id})", p.Key)));
                }
                int insertBatch = batches.Count;
                batches.Add(new GateBatch($"INSERT INTO {t} ({_sql.Cols(p.InsertColumns)}) SELECT {sel} FROM {stage} s JOIN {_sql.Temp(p.NewIds)} n ON n.{id} = s.{id}"));
                int idsBatch = batches.Count;
                batches.Add(new GateBatch($"SELECT {id} FROM {_sql.Temp(p.NewIds)}", ReturnRows: true));
                var r = await session.RunAsync(batches);
                p.Result.Inserted = Math.Max(0, r.Affected[insertBatch]);
                if (updateBatch >= 0) p.Result.Updated = Math.Max(0, r.Affected[updateBatch]);
                rt.Inserted = r.Results[idsBatch].Rows.Select(x => Guid.Parse(DbCatalog.Str(x[0]))).ToList();
                if (oldRowsBatch >= 0 && r.Results[oldRowsBatch].Rows.Count > 0)
                {
                    rt.UpdatedColumns = r.Results[oldRowsBatch].Columns;
                    rt.UpdatedRowsFile = $"{p.Entry.Name}.updated.ndjson";
                    _pendingFiles[rt.UpdatedRowsFile] = r.Results[oldRowsBatch].Rows;
                }
            }
            // Cyclic references: now that every row is in, set the deferred columns of the inserted rows.
            var deferred = plans.Where(p => p.Deferred.Count > 0).ToList();
            if (deferred.Count > 0)
                await session.RunAsync(deferred.Select(p => new GateBatch(_sql.UpdateFrom(p.Entry.Name, p.Stage, p.Deferred,
                    $"EXISTS (SELECT 1 FROM {_sql.Temp(p.NewIds)} n WHERE n.{_sql.Q("Id")} = s.{_sql.Q("Id")})"))).ToList());

            if (dryRun)
            {
                await session.RollbackAsync();
                return Finish(result, started);
            }
            result.WorkDir = workDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archive)) ?? ".",
                Path.GetFileNameWithoutExtension(archive) + "_dbimport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            WriteRestore(result.WorkDir, restore);
            progress("Committing...");
            await session.CommitAsync();
            result.Committed = true;
            return Finish(result, started);
        }
        catch
        {
            try { await session.RollbackAsync(); } catch { /* the gate already rolled back */ }
            throw;
        }
    }

    private readonly Dictionary<string, List<JsonArray>> _pendingFiles = [];

    /// <summary>Unique indexes (not the primary key, no filter/expression) of target tables.</summary>
    private async Task<Dictionary<string, List<UniqueIndex>>> LoadUniqueIndexesAsync(IEnumerable<string> tables)
    {
        var names = tables.Where(SqlDialect.IsSafeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, List<UniqueIndex>>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        var list = string.Join(",", names.Select(n => "'" + n + "'"));
        var sql = _sql.Pg
            ? "SELECT t.relname, i.relname, a.attname FROM pg_index ix JOIN pg_class t ON t.oid = ix.indrelid JOIN pg_class i ON i.oid = ix.indexrelid " +
              "JOIN pg_namespace ns ON ns.oid = t.relnamespace JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY k(attnum, ord) ON true " +
              "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " +
              "WHERE ix.indisunique AND NOT ix.indisprimary AND ix.indpred IS NULL AND NOT (0 = ANY(ix.indkey::int2[])) " +
              $"AND ns.nspname = current_schema() AND t.relname IN ({list}) ORDER BY t.relname, i.relname, k.ord"
            : "SELECT t.name, i.name, c.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id " +
              "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 " +
              "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
              $"WHERE i.is_unique = 1 AND i.is_primary_key = 0 AND i.has_filter = 0 AND t.name IN ({list}) ORDER BY t.name, i.name, ic.key_ordinal";
        var res = await _gate.QueryAsync(sql);
        foreach (var g in res.Rows.GroupBy(r => (Table: DbCatalog.Str(r[0]), Index: DbCatalog.Str(r[1]))))
        {
            if (!result.TryGetValue(g.Key.Table, out var l)) result[g.Key.Table] = l = [];
            l.Add(new UniqueIndex(g.Key.Index, g.Select(r => DbCatalog.Str(r[2])).ToList()));
        }
        return result;
    }

    private void WriteRestore(string dir, DataRestoreInfo restore)
    {
        Directory.CreateDirectory(dir);
        foreach (var (file, rows) in _pendingFiles)
            File.WriteAllLines(Path.Combine(dir, file), rows.Select(r => r.ToJsonString()), new UTF8Encoding(false));
        File.WriteAllText(DataRestoreInfo.PathIn(dir), JsonSerializer.Serialize(restore, DataArchive.Json), new UTF8Encoding(false));
    }

    private static DataImportResult Finish(DataImportResult r, DateTime started)
    {
        r.Ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
        return r;
    }

    /// <summary>SQL condition on staging alias s: the reference is set and resolves neither on the target nor in the archive.</summary>
    private string MissingCondition(Plan p, DataEdge e, Dictionary<string, List<DbColumn>> target, Dictionary<string, Plan> byName)
    {
        var col = "s." + _sql.Q(e.Column);
        var exists = new List<string>();
        if (target.ContainsKey(e.RefTable))
            exists.Add($"EXISTS (SELECT 1 FROM {_sql.Q(e.RefTable)} r WHERE r.{_sql.Q("Id")} = {col})");
        if (byName.TryGetValue(e.RefTable, out var rp) && rp.HasId)
            exists.Add($"EXISTS (SELECT 1 FROM {_sql.Temp(rp.Stage)} x WHERE x.{_sql.Q("Id")} = {col})");
        var resolved = exists.Count == 0 ? "1 = 0" : string.Join(" OR ", exists);
        return $"{col} IS NOT NULL AND NOT ({resolved})";
    }

    private string UpdateStage(Plan p, string set, string cond) => _sql.Pg
        ? $"UPDATE {_sql.Temp(p.Stage)} AS s SET {set} WHERE {cond}"
        : $"UPDATE s SET s.{set} FROM {_sql.Temp(p.Stage)} s WHERE {cond}";

    private string DeleteStage(Plan p, string cond) => _sql.Pg
        ? $"DELETE FROM {_sql.Temp(p.Stage)} AS s WHERE {cond}"
        : $"DELETE s FROM {_sql.Temp(p.Stage)} s WHERE {cond}";

    /// <summary>Topological order by references between archive tables; cycles broken at nullable columns (deferred).</summary>
    private static List<Plan> Order(List<Plan> plans, Dictionary<string, List<DbColumn>> target, DataImportResult result)
    {
        var byName = plans.ToDictionary(p => p.Entry.Name, StringComparer.OrdinalIgnoreCase);
        bool Nullable(Plan p, string column) =>
            target.TryGetValue(p.Entry.Name, out var cols) && cols.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase) && c.Nullable);
        // Edges that order the inserts: referenced table in the archive (self references are deferred when nullable).
        var deps = plans.ToDictionary(p => p, p => p.Entry.Edges
            .Where(e => p.InsertColumns.Contains(e.Column, StringComparer.OrdinalIgnoreCase) && byName.ContainsKey(e.RefTable))
            .ToList());
        foreach (var p in plans)
            foreach (var e in deps[p].Where(e => e.RefTable.Equals(p.Entry.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (Nullable(p, e.Column)) p.Deferred.Add(e.Column);
                deps[p].Remove(e);
            }
        var order = new List<Plan>();
        var left = new HashSet<Plan>(plans);
        while (left.Count > 0)
        {
            var ready = left.Where(p => deps[p].All(e => !left.Contains(byName[e.RefTable]))).OrderBy(p => p.Index).ToList();
            if (ready.Count == 0)
            {
                // Break the cycle: a nullable reference between remaining tables.
                var cut = left.SelectMany(p => deps[p].Where(e => left.Contains(byName[e.RefTable]) && Nullable(p, e.Column)).Select(e => (p, e))).FirstOrDefault();
                if (cut.p == null)
                {
                    result.Errors.Add("Reference cycle without a nullable column: " + string.Join(", ", left.Select(p => p.Entry.Name)));
                    return plans;
                }
                cut.p.Deferred.Add(cut.e.Column);
                deps[cut.p].Remove(cut.e);
                continue;
            }
            foreach (var p in ready) { order.Add(p); left.Remove(p); }
        }
        foreach (var p in order) p.Result.DeferredColumns = p.Deferred.ToList();
        return order;
    }

    /// <summary>
    /// db-restore: in one session — deferred columns of inserted rows set to NULL, inserted rows deleted (reverse order),
    /// previous versions of updated rows written back. Rows deleted by FK cascades on the target are not brought back.
    /// </summary>
    public async Task<List<string>> RestoreAsync(string workDir, Action<string>? progress = null)
    {
        progress ??= _ => { };
        var path = DataRestoreInfo.PathIn(workDir);
        var info = JsonSerializer.Deserialize<DataRestoreInfo>(await Io.ReadAllTextAsync(path), DataArchive.Json)
                   ?? throw new FormatException($"{path}: bad restore info.");
        var gi = await _gate.InfoAsync();
        _sql = new SqlDialect(gi.Engine ?? "MSSql");
        var log = new List<string>();
        var session = new GateSession(_gate);
        var id = _sql.Q("Id");
        try
        {
            int k = 0;
            var stages = new Dictionary<string, string>();
            // Ids of inserted rows into staging tables.
            foreach (var t in info.Tables.Where(t => t.Inserted.Count > 0))
            {
                var ids = "cst_d" + k++;
                stages[t.Name] = ids;
                var rows = new JsonArray(t.Inserted.Select(g => (JsonNode)new JsonArray(g.ToString())).ToArray());
                await session.RunAsync([new GateBatch(_sql.CreateIds(ids)), GateBatch.LoadRows(_sql.Temp(ids), [new GateColumn("Id", "guid")], rows)]);
            }
            var batches = new List<GateBatch>();
            foreach (var t in info.Tables.Where(t => t.DeferredColumns.Count > 0 && stages.ContainsKey(t.Name)))
                batches.Add(new GateBatch($"UPDATE {_sql.Q(t.Name)} SET {string.Join(", ", t.DeferredColumns.Select(c => _sql.Q(c) + " = NULL"))} " +
                                          $"WHERE {id} IN (SELECT {id} FROM {_sql.Temp(stages[t.Name])})"));
            var deletes = new List<string>();
            foreach (var t in Enumerable.Reverse(info.Tables).Where(t => stages.ContainsKey(t.Name)))
            {
                batches.Add(new GateBatch($"DELETE FROM {_sql.Q(t.Name)} WHERE {id} IN (SELECT {id} FROM {_sql.Temp(stages[t.Name])})"));
                deletes.Add(t.Name);
            }
            progress("Deleting inserted rows...");
            var r = batches.Count > 0 ? await session.RunAsync(batches) : null;
            int first = batches.Count - deletes.Count;
            for (int i = 0; i < deletes.Count; i++) log.Add($"{deletes[i]}: deleted {r!.Affected[first + i]} inserted rows");
            foreach (var t in info.Tables.Where(t => t.UpdatedRowsFile != null))
            {
                var rows = new JsonArray(File.ReadAllLines(Path.Combine(workDir, t.UpdatedRowsFile!)).Where(l => l.Length > 0)
                    .Select(l => JsonNode.Parse(l)).ToArray());
                var cols = t.UpdatedColumns.Select(c => c.Name).ToList();
                var stage = "cst_u" + k++;
                var u = await session.RunAsync([
                    new GateBatch(_sql.CreateStage(stage, t.Name, cols)),
                    GateBatch.LoadRows(_sql.Temp(stage), t.UpdatedColumns, rows),
                    new GateBatch(_sql.UpdateFrom(t.Name, stage, cols.Where(c => !c.Equals("Id", StringComparison.OrdinalIgnoreCase)), ""))]);
                log.Add($"{t.Name}: put back {u.Affected[2]} updated rows");
            }
            progress("Committing...");
            await session.CommitAsync();
            return log;
        }
        catch
        {
            try { await session.RollbackAsync(); } catch { }
            throw;
        }
    }
}
