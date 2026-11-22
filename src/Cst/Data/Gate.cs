using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Data;

/// <summary>Gate answer of Info.</summary>
public sealed record GateInfo(string Version, string Runtime, string User, Guid ContactId, bool CanUse,
    string? Engine, string? Provider, string? ServerVersion, string? Database);

/// <summary>A column of a gate result. Type: guid, string, int, long, decimal, double, bool, datetime, binary, other.</summary>
public sealed record GateColumn(string Name, string Type);

/// <summary>Rows of one result set; values are JSON nodes (decimal and datetime as strings, binary as base64).</summary>
public sealed record GateResult(List<GateColumn> Columns, List<JsonArray> Rows, bool Truncated, long Ms);

/// <summary>One step of an Execute call: a statement, or (Load) typed rows bulk-written into a table.</summary>
public sealed record GateBatch(string Sql, JsonObject? Params = null, bool ReturnRows = false, JsonObject? Load = null)
{
    /// <summary>
    /// Rows written into <paramref name="table"/> (usually a #temp staging table of the same call): SqlBulkCopy on MSSql,
    /// multi-row parameterized INSERTs elsewhere. Column types as in results (guid, string, int, long, decimal, double,
    /// bool, datetime, binary).
    /// </summary>
    public static GateBatch LoadRows(string table, IEnumerable<GateColumn> columns, JsonArray rows, string? method = null) => new("", Load: new JsonObject
    {
        ["method"] = method,
        ["table"] = table,
        ["columns"] = new JsonArray(columns.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.Type }).ToArray()),
        ["rows"] = rows
    });
}

public sealed record GateExecuteResult(List<int> Affected, Dictionary<int, GateResult> Results, bool Committed, long Ms, List<long> BatchMs, List<string> Notes)
{
    /// <summary>Session id (Execute with a session), null otherwise.</summary>
    public string? Session { get; init; }
}

/// <summary>
/// A gate session: one connection and one open transaction across Execute calls (for imports bigger than one request).
/// Load batches that make a request too big are split into several calls automatically.
/// </summary>
public sealed class GateSession(GateClient gate)
{
    public string? Id { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>Runs batches in the session (opens it on the first call). Results are keyed by the index in <paramref name="batches"/>.</summary>
    public async Task<GateExecuteResult> RunAsync(IReadOnlyList<GateBatch> batches, string? end = null)
    {
        if (Closed) throw new InvalidOperationException("The gate session is closed.");
        try
        {
            var r = await gate.ExecuteSplitAsync(batches, Id ?? "begin", end);
            Id = r.Session ?? Id;
            if (end != null) Closed = true;
            return r;
        }
        catch (GateException) { Closed = true; throw; } // the gate rolled the session back
    }

    public Task CommitAsync() => RunAsync([], "commit");
    public async Task RollbackAsync() { if (!Closed && Id != null) await RunAsync([], "rollback"); Closed = true; }
}

public sealed class GateException(string message, string? type = null, string? number = null, int? batch = null) : Exception(message)
{
    public string? ErrorType { get; } = type;
    public string? ErrorNumber { get; } = number;
    public int? Batch { get; } = batch;
}

/// <summary>The gate is not installed (or not loaded yet) on the stand.</summary>
public sealed class GateMissingException(string message) : Exception(message);

/// <summary>
/// Client of CstGate (rest/CstGateService): the SQL gateway cst installs on a stand for the table data transfer only.
/// Requests and responses are gzip-compressed JSON.
/// </summary>
public sealed class GateClient(CreatioClient client)
{
    public const string ServicePath = "rest/CstGateService/";

    /// <summary>Sizes of the last call: request and response on the wire (gzip) and the response unpacked.</summary>
    public (long RequestBytes, long ResponseBytes, long ResponseUnpacked) LastSizes { get; private set; }

    public async Task<GateInfo> InfoAsync()
    {
        var o = await CallAsync("Info", new JsonObject());
        return new GateInfo(S(o, "version") ?? "?", S(o, "runtime") ?? "", S(o, "user") ?? "",
            Guid.TryParse(S(o, "contactId"), out var c) ? c : Guid.Empty, o["canUse"]?.GetValue<bool>() == true,
            S(o, "engine"), S(o, "provider"), S(o, "serverVersion"), S(o, "database"));
    }

    /// <summary>Runs one SELECT. maxRows null = all rows.</summary>
    public async Task<GateResult> QueryAsync(string sql, JsonObject? parameters = null, long? maxRows = null, int timeoutSeconds = 600)
    {
        var body = new JsonObject { ["sql"] = sql, ["timeout"] = timeoutSeconds };
        if (parameters != null) body["params"] = parameters;
        if (maxRows != null) body["maxRows"] = maxRows;
        var o = await CallAsync("Query", body);
        ThrowIfFailed(o);
        return ParseResult(o);
    }

    /// <summary>Largest gzip request sent in one call: Creatio refused ~10 MB (HTTP 400) on creatio-dev, 8.3 MB passed.</summary>
    public long MaxRequestBytes { get; set; } = 7L * 1024 * 1024;

    /// <summary>Runs statements in one transaction (unless transaction: false); rollback: true = dry run.</summary>
    public Task<GateExecuteResult> ExecuteAsync(IEnumerable<GateBatch> batches, bool transaction = true, bool rollback = false,
        string? isolation = null, int timeoutSeconds = 600)
    {
        var body = new JsonObject { ["batches"] = BatchArray(batches), ["transaction"] = transaction, ["rollback"] = rollback, ["timeout"] = timeoutSeconds };
        if (isolation != null) body["isolation"] = isolation;
        return ExecuteBodyAsync(body);
    }

    /// <summary>
    /// Session call (session = "begin" or an id). When the request would be too big, the batches are sent in several calls
    /// of the same session (load batches split by rows); <paramref name="end"/> goes with the last one.
    /// </summary>
    internal async Task<GateExecuteResult> ExecuteSplitAsync(IReadOnlyList<GateBatch> batches, string session, string? end, int timeoutSeconds = 1800)
    {
        var body = new JsonObject { ["batches"] = BatchArray(batches), ["session"] = session, ["timeout"] = timeoutSeconds };
        if (end != null) body["end"] = end;
        if (batches.Count == 0 || Gzip(Encoding.UTF8.GetBytes(body.ToJsonString())).Length <= MaxRequestBytes)
            return await ExecuteBodyAsync(body);

        List<GateBatch> first, second;
        if (batches.Count > 1)
        {
            first = batches.Take(batches.Count / 2).ToList();
            second = batches.Skip(batches.Count / 2).ToList();
        }
        else if (batches[0].Load is { } load && load["rows"] is JsonArray rows && rows.Count > 1)
        {
            var half = rows.Count / 2;
            first = [LoadPart(load, rows, 0, half)];
            second = [LoadPart(load, rows, half, rows.Count - half)];
        }
        else throw new GateException($"One gate batch is larger than {MaxRequestBytes / 1024 / 1024} MB compressed and cannot be split.");

        var a = await ExecuteSplitAsync(first, session, null, timeoutSeconds);
        var b = await ExecuteSplitAsync(second, a.Session ?? session, end, timeoutSeconds);
        if (batches.Count == 1) // one load split in two: one batch for the caller
            return b with { Affected = [a.Affected.Sum() + b.Affected.Sum()], Ms = a.Ms + b.Ms, BatchMs = [a.Ms + b.Ms], Notes = [.. a.Notes, .. b.Notes] };
        var results = new Dictionary<int, GateResult>(a.Results);
        foreach (var (k, v) in b.Results) results[k + first.Count] = v;
        return b with { Affected = [.. a.Affected, .. b.Affected], Results = results, Ms = a.Ms + b.Ms, BatchMs = [.. a.BatchMs, .. b.BatchMs], Notes = [.. a.Notes, .. b.Notes] };
    }

    private static GateBatch LoadPart(JsonObject load, JsonArray rows, int start, int count)
    {
        var part = new JsonArray();
        for (int i = start; i < start + count; i++) part.Add(rows[i]?.DeepClone());
        var copy = (JsonObject)load.DeepClone();
        copy["rows"] = part;
        return new GateBatch("", Load: copy);
    }

    private static JsonArray BatchArray(IEnumerable<GateBatch> batches)
    {
        var array = new JsonArray();
        foreach (var b in batches)
        {
            if (b.Load != null) { array.Add(new JsonObject { ["load"] = b.Load.DeepClone() }); continue; }
            var item = new JsonObject { ["sql"] = b.Sql };
            if (b.Params != null) item["params"] = b.Params.DeepClone();
            if (b.ReturnRows) item["rows"] = true;
            array.Add(item);
        }
        return array;
    }

    private async Task<GateExecuteResult> ExecuteBodyAsync(JsonObject body)
    {
        var o = await CallAsync("Execute", body);
        ThrowIfFailed(o);
        var affected = o["affected"]?.AsArray().Select(n => n?.GetValue<int>() ?? -1).ToList() ?? [];
        var results = new Dictionary<int, GateResult>();
        foreach (var r in o["results"]?.AsArray().OfType<JsonObject>() ?? [])
            results[r["batch"]!.GetValue<int>()] = ParseResult(r);
        var batchMs = o["batchMs"]?.AsArray().Select(n => n?.GetValue<long>() ?? 0).ToList() ?? [];
        var notes = o["notes"]?.AsArray().Select(n => n?.ToString() ?? "").ToList() ?? [];
        return new GateExecuteResult(affected, results, o["committed"]?.GetValue<bool>() == true, o["ms"]?.GetValue<long>() ?? 0, batchMs, notes)
        { Session = S(o, "session") };
    }

    private async Task<JsonObject> CallAsync(string method, JsonObject body)
    {
        body["gzip"] = true;
        var request = Gzip(Encoding.UTF8.GetBytes(body.ToJsonString()));
        var (status, bytes) = await client.PostBytesAsync(ServicePath + method, request);
        if (status == 404) throw new GateMissingException("CstGate is not installed on the stand (rest/CstGateService not found). Run: cst gate-install --env <stand>");
        long wire = bytes.Length;
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b) bytes = Gunzip(bytes);
        LastSizes = (request.Length, wire, bytes.Length);
        var text = Encoding.UTF8.GetString(bytes);
        if (status is < 200 or >= 300) throw new CreatioException($"CstGate {method}: HTTP {status} {(text.Length > 400 ? text[..400] : text)}");
        var o = JsonNode.Parse(text) as JsonObject ?? throw new CreatioException($"CstGate {method}: unexpected answer");
        if (S(o, "fatal") is { } fatal) throw new GateException(fatal, "Fatal");
        return o;
    }

    private static void ThrowIfFailed(JsonObject o)
    {
        if (o["ok"]?.GetValue<bool>() == true) return;
        throw new GateException(S(o, "error") ?? o.ToJsonString(), S(o, "errorType"), S(o, "errorNumber"), o["batch"]?.GetValue<int>());
    }

    private static GateResult ParseResult(JsonObject o)
    {
        var columns = o["columns"]?.AsArray().OfType<JsonObject>()
            .Select(c => new GateColumn(S(c, "name") ?? "", S(c, "type") ?? "other")).ToList() ?? [];
        var rows = o["rows"]?.AsArray().OfType<JsonArray>().ToList() ?? [];
        return new GateResult(columns, rows, o["truncated"]?.GetValue<bool>() == true, o["ms"]?.GetValue<long>() ?? 0);
    }

    private static string? S(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Builds and installs the CstGate package: Files\Bin\CstGate.dll (+ netstandard build), no schemas.</summary>
public static class GateInstaller
{
    public const string PackageName = "CstGate";
    /// <summary>Fixed package UId (never change: reinstalls replace the same package).</summary>
    public static readonly Guid PackageUId = new("c57ca7e0-6a7e-4c57-9a7e-0000000c5747");
    /// <summary>The base package every Creatio has (CrtBase on 8.x/10.x keeps this UId; cliogate depends on it too).</summary>
    private static readonly Guid BasePackageUId = new("5be3998b-c5c3-42bb-a01c-2e4149059a97");

    /// <summary>Version of the gate embedded in this cst.</summary>
    public static string EmbeddedVersion
    {
        get
        {
            var dll = Resource("CstGate.net472.dll");
            return Assembly.Load(dll).GetName().Version?.ToString() ?? "?";
        }
    }

    public static byte[] BuildZip()
    {
        var content = new PackageContent();
        var descriptor = new JsonObject
        {
            ["Descriptor"] = new JsonObject
            {
                ["UId"] = PackageUId.ToString(),
                ["PackageVersion"] = EmbeddedVersion,
                ["Name"] = PackageName,
                ["Type"] = 0,
                ["ProjectPath"] = "",
                ["ModifiedOnUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Maintainer"] = "cst",
                ["DependsOn"] = new JsonArray(new JsonObject
                {
                    ["UId"] = BasePackageUId.ToString(), ["PackageVersion"] = "7.8.0", ["Name"] = "Base"
                })
            }
        };
        content.Set("descriptor.json", Encoding.UTF8.GetBytes(descriptor.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        content.Set("Files\\Bin\\CstGate.dll", Resource("CstGate.net472.dll"));
        content.Set("Files\\Bin\\netstandard\\CstGate.dll", Resource("CstGate.netstandard.dll"));
        foreach (var folder in new[] { "Assemblies", "Schemas", "SqlScripts", "Data", "Resources" })
            content.Set(folder + "\\placeholder.txt", []);
        return PackageGz.WriteZip(PackageName, content);
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(GateInstaller).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Embedded resource {name} is missing (build gate\\CstGate).");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
