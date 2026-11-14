using System.Text.Json;

namespace Cst.Config;

public sealed class EnvironmentSettings
{
    public string Url { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>null = detect automatically.</summary>
    public bool? IsNetCore { get; set; }
    public int TimeoutMinutes { get; set; } = 30;
    /// <summary>DB engine of the environment: MSSql, PostgreSql or Oracle. Required for import (decides which SQL scripts run).</summary>
    public string? DbEngine { get; set; }

    /// <summary>SysPackageSqlScript.DBEngineType value of <see cref="DbEngine"/>: 0 MSSQL, 1 Oracle, 2 PostgreSQL.</summary>
    public static int ParseDbEngine(string? name, string where) => name?.Trim().ToLowerInvariant() switch
    {
        "mssql" or "sqlserver" => 0,
        "oracle" => 1,
        "postgresql" or "postgres" or "pg" => 2,
        null or "" => throw new ArgumentException($"DbEngine is not set for {where}: add \"DbEngine\": \"MSSql\" | \"PostgreSql\" | \"Oracle\" to the environment in appsettings.json or pass --db-engine."),
        _ => throw new ArgumentException($"Unknown DbEngine '{name}' for {where}; use MSSql, PostgreSql or Oracle.")
    };
}

public sealed class AppSettings
{
    public Dictionary<string, EnvironmentSettings> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load(string? path)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppSettings();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var result = settings ?? new AppSettings();
        result.Environments = new Dictionary<string, EnvironmentSettings>(result.Environments, StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Resolves an environment from --env plus overrides (--url, --login, --password).
    /// The password may also come from CST_PASSWORD or CST_PASSWORD_&lt;ENV&gt;.
    /// </summary>
    public EnvironmentSettings Resolve(string? envName, string? url, string? login, string? password, string? dbEngine = null)
    {
        EnvironmentSettings env;
        if (envName != null)
        {
            if (!Environments.TryGetValue(envName, out var found))
                throw new ArgumentException($"Environment '{envName}' is not defined in appsettings.json.");
            env = new EnvironmentSettings
            {
                Url = found.Url, Login = found.Login, Password = found.Password,
                IsNetCore = found.IsNetCore, TimeoutMinutes = found.TimeoutMinutes, DbEngine = found.DbEngine
            };
        }
        else env = new EnvironmentSettings();

        if (dbEngine != null) env.DbEngine = dbEngine;
        if (url != null) env.Url = url;
        if (login != null) env.Login = login;
        if (password != null) env.Password = password;
        if (string.IsNullOrEmpty(env.Password))
        {
            var key = envName == null ? null : "CST_PASSWORD_" + envName.ToUpperInvariant().Replace('-', '_');
            env.Password = (key != null ? Environment.GetEnvironmentVariable(key) : null)
                           ?? Environment.GetEnvironmentVariable("CST_PASSWORD") ?? "";
        }
        if (string.IsNullOrEmpty(env.Url)) throw new ArgumentException("Environment URL is not set (use --env or --url).");
        if (string.IsNullOrEmpty(env.Login)) throw new ArgumentException("Login is not set.");
        if (string.IsNullOrEmpty(env.Password)) throw new ArgumentException("Password is not set (appsettings.json, --password or CST_PASSWORD).");
        return env;
    }
}
