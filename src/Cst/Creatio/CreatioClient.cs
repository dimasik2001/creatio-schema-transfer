using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Config;

namespace Cst.Creatio;

public sealed class CreatioException(string message) : Exception(message);

/// <summary>
/// Thin client over standard Creatio endpoints: AuthService, DataService, PackageInstallerService,
/// WorkspaceExplorerService. Works with .NET Framework (services under /0) and .NET Core (root) hosts.
/// </summary>
public sealed class CreatioClient : IDisposable
{
    private readonly EnvironmentSettings _env;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Uri _base;
    private string _servicePrefix = "/0";

    public CreatioClient(EnvironmentSettings env)
    {
        _env = env;
        _base = new Uri(env.Url.TrimEnd('/') + "/");
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(env.TimeoutMinutes) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cst/0.1");
        if (env.IsNetCore == true) _servicePrefix = "";
    }

    public string Url => _base.ToString().TrimEnd('/');

    public async Task LoginAsync()
    {
        var body = new JsonObject { ["UserName"] = _env.Login, ["UserPassword"] = _env.Password };
        using var resp = await _http.PostAsync(new Uri(_base, "ServiceModel/AuthService.svc/Login"),
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new CreatioException($"Login failed: HTTP {(int)resp.StatusCode} {Shorten(text)}");
        var json = JsonNode.Parse(text);
        if (json?["Code"]?.GetValue<int>() != 0)
            throw new CreatioException($"Login failed: {json?["Message"]}");
        var csrf = _cookies.GetCookies(_base)["BPMCSRF"]?.Value;
        if (csrf != null)
        {
            _http.DefaultRequestHeaders.Remove("BPMCSRF");
            _http.DefaultRequestHeaders.Add("BPMCSRF", csrf);
        }
        if (_env.IsNetCore == null) await DetectPrefixAsync();
    }

    private async Task DetectPrefixAsync()
    {
        foreach (var prefix in new[] { "/0", "" })
        {
            _servicePrefix = prefix;
            using var resp = await PostRawAsync("DataService/json/SyncReply/SelectQuery",
                new StringContent(SelectQueryProbe, Encoding.UTF8, "application/json"));
            if (resp.StatusCode != HttpStatusCode.NotFound) return;
        }
        throw new CreatioException("Cannot find Creatio services under /0 or the site root.");
    }

    private const string SelectQueryProbe =
        "{\"RootSchemaName\":\"SysSettings\",\"OperationType\":0,\"RowCount\":1,\"AllColumns\":false,\"Columns\":{\"Items\":{\"Id\":{\"Expression\":{\"ExpressionType\":0,\"ColumnPath\":\"Id\"}}}}}";

    private Uri ServiceUri(string relative) =>
        new(_base, (_servicePrefix + "/" + relative).TrimStart('/'));

    private Task<HttpResponseMessage> PostRawAsync(string relative, HttpContent content) =>
        _http.PostAsync(ServiceUri(relative), content);

    public async Task<JsonNode?> PostJsonAsync(string relative, JsonNode? body)
    {
        using var resp = await PostRawAsync(relative,
            new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new CreatioException($"{relative}: HTTP {(int)resp.StatusCode} {Shorten(text)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>Runs a DataService SelectQuery and returns rows.</summary>
    public async Task<List<JsonObject>> SelectAsync(JsonObject query)
    {
        var json = await PostJsonAsync("DataService/json/SyncReply/SelectQuery", query);
        if (json?["success"]?.GetValue<bool>() == false)
            throw new CreatioException($"SelectQuery {query["RootSchemaName"]}: {json["responseStatus"]?["Message"] ?? json.ToJsonString()}");
        return json?["rows"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }

    public async Task<byte[]> GetZipPackagesAsync(IEnumerable<string> packageNames)
    {
        var body = new JsonArray(packageNames.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        using var resp = await PostRawAsync("ServiceModel/PackageInstallerService.svc/GetZipPackages",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (!resp.IsSuccessStatusCode || bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
            throw new CreatioException($"GetZipPackages({string.Join(",", packageNames)}): HTTP {(int)resp.StatusCode} {Shorten(Encoding.UTF8.GetString(bytes))}");
        return bytes;
    }

    public async Task UploadPackageAsync(string fileName, byte[] zip)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-zip-compressed");
        form.Add(file, "files", fileName);
        using var resp = await PostRawAsync("ServiceModel/PackageInstallerService.svc/UploadPackage", form);
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, "UploadPackage");
    }

    public async Task InstallPackageAsync(string fileName)
    {
        using var resp = await PostRawAsync("ServiceModel/PackageInstallerService.svc/InstallPackage",
            new StringContent(JsonSerializer.Serialize(fileName), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, "InstallPackage");
    }

    public async Task<string> GetInstallLogAsync()
    {
        using var resp = await _http.GetAsync(ServiceUri("ServiceModel/PackageInstallerService.svc/GetLogFile"));
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Exports schemas in the Configuration section's per-schema format (.md, JSON inside).
    /// Returns schema name → .md text.
    /// </summary>
    public async Task<Dictionary<string, string>> ExportSchemasAsync(IEnumerable<(Guid Id, Guid UId, string Name)> schemas)
    {
        var body = new JsonArray(schemas.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id.ToString(), ["uId"] = s.UId.ToString(), ["name"] = s.Name
        }).ToArray());
        using var resp = await PostRawAsync("ServiceModel/WorkspaceExplorerService.svc/ExportSchema",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (!resp.IsSuccessStatusCode || bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
            throw new CreatioException($"ExportSchema: HTTP {(int)resp.StatusCode} {Shorten(Encoding.UTF8.GetString(bytes))}");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            result[Path.GetFileNameWithoutExtension(e.Name)] = await r.ReadToEndAsync();
        }
        return result;
    }

    /// <summary>Imports one schema (.md from ExportSchema) into a package, like the Configuration section's import.</summary>
    public async Task ImportSchemaAsync(Guid packageUId, string schemaName, string md)
    {
        var body = new JsonObject
        {
            ["packageUId"] = packageUId.ToString(),
            ["fileName"] = schemaName + ".md",
            ["itemData"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(md))
        };
        using var resp = await PostRawAsync("ServiceModel/WorkspaceExplorerService.svc/ImportWorkspaceItem",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, $"ImportWorkspaceItem({schemaName})");
    }

    /// <summary>
    /// Creates/updates DB tables of entity schemas (WorkspaceExplorerService/UpdateDBStructure).
    /// Core 8.3 answers success:false with "update not needed" when the tables are already up to date, so a
    /// failure is returned as a message for the caller to report, not thrown.
    /// </summary>
    public async Task<string?> UpdateDbStructureAsync(IEnumerable<Guid> entitySchemaUIds)
    {
        var body = new JsonArray(entitySchemaUIds.Select(u => (JsonNode)JsonValue.Create(u.ToString())!).ToArray());
        using var resp = await PostRawAsync("ServiceModel/WorkspaceExplorerService.svc/UpdateDBStructure",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new CreatioException($"UpdateDBStructure: HTTP {(int)resp.StatusCode} {Shorten(text)}");
        var json = JsonNode.Parse(text);
        return json?["success"]?.GetValue<bool>() == false ? json["errorInfo"]?["message"]?.ToString() ?? text : null;
    }

    /// <summary>Application facts from ApplicationInfoService/GetApplicationInfo (present in 8.x and 10.x).</summary>
    public sealed record AppInfo(string CoreVersion, string PrimaryCulture, int MaxEntitySchemaNameLength);

    private AppInfo? _appInfo;

    public async Task<AppInfo> GetAppInfoAsync()
    {
        if (_appInfo != null) return _appInfo;
        var json = await PostJsonAsync("ServiceModel/ApplicationInfoService.svc/GetApplicationInfo", new JsonObject());
        var sys = json?["applicationInfo"]?["sysValues"];
        _appInfo = new AppInfo(
            sys?["coreVersion"]?.ToString() ?? "",
            sys?["primaryCulture"]?["displayValue"]?.ToString() ?? "en-US",
            int.TryParse(sys?["maxEntitySchemaNameLength"]?.ToString(), out var n) ? n : 0);
        return _appInfo;
    }

    /// <summary>
    /// DB engine of the environment from its settings (DbEngine / --db-engine): 0 MSSQL, 1 Oracle, 2 PostgreSQL
    /// (SysPackageSqlScript.DBEngineType values). Not detected: the user's configuration is the source of truth.
    /// </summary>
    public int DbEngineType => EnvironmentSettings.ParseDbEngine(_env.DbEngine, Url);

    /// <summary>Creates a system setting (DataService InsertSysSettingRequest; keeps the given Id).</summary>
    public Task InsertSysSettingAsync(JsonObject setting) => SysSettingsCallAsync("InsertSysSettingRequest", setting);

    /// <summary>Updates a system setting's properties (DataService UpdateSysSettingRequest).</summary>
    public Task UpdateSysSettingAsync(JsonObject setting) => SysSettingsCallAsync("UpdateSysSettingRequest", setting);

    /// <summary>Sets default values (for All employees) of system settings by code (DataService PostSysSettingsValues).</summary>
    public async Task PostSysSettingsValuesAsync(Dictionary<string, JsonNode?> values)
    {
        var map = new JsonObject();
        foreach (var (k, v) in values) map[k] = v?.DeepClone();
        var json = await PostJsonAsync("DataService/json/SyncReply/PostSysSettingsValues", new JsonObject { ["isPersonal"] = false, ["sysSettingsValues"] = map });
        // BaseSysSettingsService never sets Success for this request (it stays false); saveResult is the outcome.
        var saved = json?["saveResult"] as JsonObject;
        var failed = values.Keys.Where(k => saved?[k]?.GetValue<bool>() != true).ToList();
        if (failed.Count > 0)
            throw new CreatioException($"PostSysSettingsValues failed for {string.Join(", ", failed.Count > 0 ? failed : values.Keys)}: {json?["errorInfo"]?["message"] ?? json?["responseStatus"]?["Message"]}");
    }

    private async Task SysSettingsCallAsync(string request, JsonObject body)
    {
        var json = await PostJsonAsync($"DataService/json/SyncReply/{request}", body);
        if (json?["success"]?.GetValue<bool>() != true)
            throw new CreatioException($"{request} {body["code"]}: {json?["responseStatus"]?["Message"] ?? json?["errorInfo"]?["message"] ?? json?.ToJsonString()}");
    }

    /// <summary>Reads an SQL script element (SqlScriptSchemaDesignerService/GetSchema); null if it does not exist.</summary>
    public async Task<JsonObject?> GetSqlScriptAsync(Guid uId)
    {
        using var resp = await PostRawAsync("ServiceModel/SqlScriptSchemaDesignerService.svc/GetSchema",
            new StringContent(new JsonObject { ["schemaUId"] = uId.ToString() }.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return null;
        var json = JsonNode.Parse(text);
        return json?["success"]?.GetValue<bool>() == true ? json["schema"] as JsonObject : null;
    }

    /// <summary>Creates or updates an SQL script element like the Configuration SQL designer does.</summary>
    public async Task SaveSqlScriptAsync(Guid uId, string name, Guid packageUId, string body, int installType, int dbEngineType,
        IEnumerable<Guid> dependsOn, bool backwardCompatibilityConfirmed)
    {
        var dto = new JsonObject
        {
            ["uId"] = uId.ToString(),
            ["name"] = name,
            ["package"] = new JsonObject { ["uId"] = packageUId.ToString() },
            ["body"] = body,
            ["installType"] = installType,
            ["dbEngineType"] = dbEngineType,
            ["backwardCompatibilityConfirmed"] = backwardCompatibilityConfirmed,
            ["dependOnSqlScripts"] = new JsonArray(dependsOn.Select(d => (JsonNode)new JsonObject { ["uId"] = d.ToString() }).ToArray())
        };
        using var resp = await PostRawAsync("ServiceModel/SqlScriptSchemaDesignerService.svc/SaveSchema",
            new StringContent(dto.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, $"SqlScriptSchemaDesignerService/SaveSchema({name})");
    }

    /// <summary>Executes SQL script elements stored in the DB (WorkspaceExplorerService/InstallSqlScripts).</summary>
    public async Task InstallSqlScriptsAsync(IEnumerable<Guid> uIds)
    {
        var body = new JsonArray(uIds.Select(u => (JsonNode)JsonValue.Create(u.ToString())!).ToArray());
        using var resp = await PostRawAsync("ServiceModel/WorkspaceExplorerService.svc/InstallSqlScripts",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, "InstallSqlScripts");
    }

    /// <summary>Installs stored data bindings with Creatio's own data installer (WorkspaceExplorerService/InstallData):
    /// rows from SysPackageSchemaData.Data, translations from SysPackageDataLcz.</summary>
    public async Task InstallDataAsync(IEnumerable<Guid> bindingUIds)
    {
        var body = new JsonArray(bindingUIds.Select(u => (JsonNode)JsonValue.Create(u.ToString())!).ToArray());
        using var resp = await PostRawAsync("ServiceModel/WorkspaceExplorerService.svc/InstallData",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, "InstallData");
    }

    /// <summary>Saves a data binding (SchemaDataDesignerService/SaveSchema). Row values are read from the DB by record id.</summary>
    public async Task SaveSchemaDataAsync(JsonObject dto, string name)
    {
        using var resp = await PostRawAsync("ServiceModel/SchemaDataDesignerService.svc/SaveSchema",
            new StringContent(dto.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        EnsureBaseResponse(resp, text, $"SchemaDataDesignerService/SaveSchema({name})");
        var json = JsonNode.Parse(text);
        var warning = json?["errorInfo"]?["message"]?.ToString();
        if (!string.IsNullOrEmpty(warning)) Log.Warn($"{name}: {warning}");
    }

    /// <summary>Runs a DataService insert/update/delete query.</summary>
    public async Task<int> ExecuteDataQueryAsync(string operation, JsonObject query)
    {
        var json = await PostJsonAsync($"DataService/json/SyncReply/{operation}", query);
        if (json?["success"]?.GetValue<bool>() != true)
            throw new CreatioException($"{operation} {query["RootSchemaName"]}: {json?["responseStatus"]?["Message"] ?? json?.ToJsonString()}");
        return json["rowsAffected"]?.GetValue<int>() ?? 0;
    }

    /// <summary>Compiles changed configuration (WorkspaceExplorerService/Build). Returns error lines.</summary>
    public async Task<List<string>> BuildAsync(bool full)
    {
        var json = await PostJsonAsync("ServiceModel/WorkspaceExplorerService.svc/" + (full ? "Rebuild" : "Build"), null);
        var errors = new List<string>();
        foreach (var e in json?["errors"]?.AsArray() ?? [])
        {
            if (e is not JsonObject o) continue;
            if (o["warning"]?.GetValue<bool>() == true) continue;
            errors.Add($"{o["fileName"]}({o["line"]},{o["column"]}): {o["errorNumber"]} {o["errorText"]}");
        }
        if (json?["success"]?.GetValue<bool>() == false && errors.Count == 0)
            errors.Add(json["errorInfo"]?["message"]?.ToString() ?? json["message"]?.ToString() ?? "Build failed");
        return errors;
    }

    /// <summary>Raw POST to a service path (diagnostics): returns status and body.</summary>
    public async Task<(int Status, string Body)> RawPostAsync(string relative, string json)
    {
        using var resp = await PostRawAsync(relative, new StringContent(json, Encoding.UTF8, "application/json"));
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static void EnsureBaseResponse(HttpResponseMessage resp, string text, string op)
    {
        if (!resp.IsSuccessStatusCode)
            throw new CreatioException($"{op}: HTTP {(int)resp.StatusCode} {Shorten(text)}");
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        if (json?["success"]?.GetValue<bool>() == false)
            throw new CreatioException($"{op}: {json["errorInfo"]?["message"] ?? text}");
    }

    private static string Shorten(string s) => s.Length > 500 ? s[..500] + "..." : s;

    public void Dispose() => _http.Dispose();
}
