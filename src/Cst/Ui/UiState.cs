using System.Text.Json;

namespace Cst.Ui;

/// <summary>
/// What the console UI remembers between runs (cst.ui-state.json next to appsettings.json). Never passwords.
/// A missing, unreadable or broken file means a first run.
/// </summary>
public sealed class UiState
{
    public const int MaxRecent = 10;
    public const string FileName = "cst.ui-state.json";

    /// <summary>"src->dst" → source package → target package.</summary>
    public Dictionary<string, Dictionary<string, string>> Mappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LastSource { get; set; }
    public string? LastTarget { get; set; }
    public List<string> RecentLists { get; set; } = [];
    public List<string> RecentArchives { get; set; } = [];
    public List<string> RecentWorkDirs { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // "dev->test", not "dev->test"
    };

    public static string PathNextTo(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? AppContext.BaseDirectory, FileName);

    /// <summary>Loads the state; on any problem returns an empty state and a warning to show once.</summary>
    public static UiState Load(string path, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(path)) return new UiState();
            var s = JsonSerializer.Deserialize<UiState>(File.ReadAllText(path), Json) ?? new UiState();
            s.Mappings = new Dictionary<string, Dictionary<string, string>>(s.Mappings ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var k in s.Mappings.Keys.ToList())
                s.Mappings[k] = new Dictionary<string, string>(s.Mappings[k] ?? [], StringComparer.OrdinalIgnoreCase);
            s.RecentLists ??= []; s.RecentArchives ??= []; s.RecentWorkDirs ??= [];
            return s;
        }
        catch (Exception ex)
        {
            warning = $"{FileName} could not be read ({ex.Message}); starting without remembered choices.";
            return new UiState();
        }
    }

    /// <summary>Saves the state; returns an error message instead of throwing (the UI keeps working).</summary>
    public string? Save(string path)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
            return null;
        }
        catch (Exception ex) { return $"{FileName} could not be saved: {ex.Message}"; }
    }

    public static void Remember(List<string> list, string item)
    {
        list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, item);
        if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }

    public static string MappingKey(string source, string target) => source + "->" + target;
}
