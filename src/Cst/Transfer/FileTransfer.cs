using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Cst.Transfer;

/// <summary>A file of the Creatio installation folder carried by the archive (path relative to the root, '/' separators).</summary>
public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>
/// Transfer of plain files of a Creatio installation (the `files:` section of the list), enabled when the environment
/// has a root folder (`Path`, like clio's EnvironmentPath). Paths are relative to that root and may not leave it.
/// Entries: a file, a folder (all files below it) or a file-name mask (`bin\*.dll`).
/// On import the files are written last (after compilation, which would otherwise overwrite e.g.
/// Terrasoft.Configuration.dll); identical files are skipped, replaced ones are backed up, created ones are
/// recorded so that restore can delete them.
/// </summary>
public static class FileTransfer
{
    public const string ArchiveFolder = "files/";

    public sealed record Planned(ManifestFile File, string FullPath, string State); // State: new | replace | identical

    /// <summary>Resolves list entries against the source root into relative paths (sorted, unique).</summary>
    public static List<string> Resolve(string root, IEnumerable<string> entries, List<string> errors)
    {
        var rootFull = RootFull(root);
        if (!Directory.Exists(rootFull)) { errors.Add($"Creatio root folder {rootFull} does not exist."); return []; }
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in entries)
        {
            var entry = raw.Trim().Replace('/', '\\');
            if (entry.Length == 0) continue;
            if (System.IO.Path.IsPathRooted(entry)) { errors.Add($"files: '{raw}' must be relative to the Creatio root folder."); continue; }
            bool mask = System.IO.Path.GetFileName(entry).IndexOfAny(['*', '?']) >= 0;
            var dirPart = mask ? System.IO.Path.GetDirectoryName(entry) ?? "" : entry;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, dirPart));
            if (!Inside(rootFull, full)) { errors.Add($"files: '{raw}' points outside the Creatio root folder."); continue; }
            IEnumerable<string> found;
            if (mask)
            {
                if (!Directory.Exists(full)) { errors.Add($"files: folder of '{raw}' does not exist."); continue; }
                found = Directory.EnumerateFiles(full, System.IO.Path.GetFileName(entry), SearchOption.TopDirectoryOnly);
            }
            else if (File.Exists(full)) found = [full];
            else if (Directory.Exists(full)) found = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories);
            else { errors.Add($"files: '{raw}' not found under {rootFull}."); continue; }
            var list = found.ToList();
            if (list.Count == 0) { errors.Add($"files: '{raw}' matches no files."); continue; }
            foreach (var f in list) result.Add(Relative(rootFull, f));
        }
        return result.ToList();
    }

    /// <summary>Reads the files for the archive: archive entry name → bytes, plus manifest records.</summary>
    public static (List<ManifestFile> Files, Dictionary<string, byte[]> Entries) Read(string root, IEnumerable<string> relative)
    {
        var rootFull = RootFull(root);
        var files = new List<ManifestFile>();
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relative)
        {
            var bytes = File.ReadAllBytes(System.IO.Path.Combine(rootFull, rel.Replace('/', '\\')));
            files.Add(new ManifestFile { Path = rel, Size = bytes.Length, Sha256 = Hash(bytes) });
            entries[ArchiveFolder + rel] = bytes;
        }
        return (files, entries);
    }

    /// <summary>
    /// Compares the archive files with the target root and checks that every file to replace can be opened for
    /// writing (not locked). Nothing is changed. Throws when the root is missing or a file is locked.
    /// </summary>
    public static List<Planned> Plan(string root, IEnumerable<ManifestFile> files)
    {
        var rootFull = RootFull(root);
        if (!Directory.Exists(rootFull)) throw new ArgumentException($"Creatio root folder {rootFull} does not exist on this machine.");
        var plan = new List<Planned>();
        var locked = new List<string>();
        foreach (var f in files)
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, f.Path.Replace('/', '\\')));
            if (!Inside(rootFull, full)) throw new InvalidDataException($"Archive file '{f.Path}' points outside the root folder.");
            if (!File.Exists(full)) { plan.Add(new Planned(f, full, "new")); continue; }
            if (new FileInfo(full).Length == f.Size && Hash(File.ReadAllBytes(full)) == f.Sha256) { plan.Add(new Planned(f, full, "identical")); continue; }
            try { using var s = File.Open(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { locked.Add($"{f.Path}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { locked.Add($"{f.Path}: {ex.Message}"); }
            plan.Add(new Planned(f, full, "replace"));
        }
        if (locked.Count > 0)
            throw new InstallException("These files cannot be written (locked or no access); nothing was changed:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", locked));
        return plan;
    }

    /// <summary>Writes the planned files. Replaced files are copied to backupDir first; returns restore records.</summary>
    public static JsonArray Apply(List<Planned> plan, Func<string, byte[]> archiveBytes, string backupDir, string workDir)
    {
        var records = new JsonArray();
        foreach (var p in plan.Where(p => p.State != "identical"))
        {
            var bytes = archiveBytes(ArchiveFolder + p.File.Path);
            if (Hash(bytes) != p.File.Sha256) throw new InvalidDataException($"Archive file '{p.File.Path}' is damaged (hash mismatch).");
            string? backup = null;
            if (p.State == "replace")
            {
                backup = System.IO.Path.Combine(backupDir, "files", p.File.Path.Replace('/', '\\'));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                File.Copy(p.FullPath, backup, overwrite: true);
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p.FullPath)!);
            var temp = p.FullPath + ".cst-tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(p.FullPath)) File.Delete(p.FullPath); File.Move(temp, p.FullPath);
            records.Add(new JsonObject
            {
                ["path"] = p.File.Path,
                ["fullPath"] = p.FullPath,
                ["backup"] = backup == null ? null : Io.GetRelativePath(workDir, backup)
            });
            Log.Debug($"{p.File.Path}: {(p.State == "new" ? "created" : "replaced")}");
        }
        return records;
    }

    /// <summary>Plan summary: counts per state and up to <paramref name="max"/> paths each (all with --verbose).</summary>
    public static IEnumerable<string> Describe(List<Planned> plan, int max = 10)
    {
        foreach (var g in plan.GroupBy(p => p.State).OrderBy(g => g.Key switch { "replace" => 0, "new" => 1, _ => 2 }))
        {
            yield return $"  files {g.Key}: {g.Count()}";
            var shown = Log.Verbose ? g.ToList() : g.Take(max).ToList();
            foreach (var p in shown) yield return $"    {p.File.Path}";
            if (shown.Count < g.Count()) yield return $"    ... {g.Count() - shown.Count} more (--verbose lists all)";
        }
    }

    /// <summary>Restore: puts back replaced files and deletes the ones the import created.</summary>
    public static List<string> Restore(JsonArray records, string workDir)
    {
        var done = new List<string>();
        foreach (var r in records.OfType<JsonObject>())
        {
            var full = r["fullPath"]!.ToString();
            var backup = r["backup"]?.ToString();
            if (backup != null)
            {
                File.Copy(System.IO.Path.Combine(workDir, backup), full, overwrite: true);
                done.Add($"file {r["path"]} (restored)");
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
                done.Add($"file {r["path"]} (deleted, it was created by the import)");
            }
        }
        return done;
    }

    /// <summary>Files Creatio picks up only after a restart of the application (compiled configuration, web bin).</summary>
    public static bool NeedsRestart(string relative)
    {
        var p = relative.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".config", StringComparison.OrdinalIgnoreCase);
    }

    private static string RootFull(string root) => System.IO.Path.GetFullPath(root.TrimEnd('\\', '/') + "\\");

    private static bool Inside(string rootFull, string full) =>
        full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && full.Length > rootFull.Length;

    private static string Relative(string rootFull, string full) => Io.GetRelativePath(rootFull, full).Replace('\\', '/');

    private static string Hash(byte[] bytes) => Io.ToHex(Io.Sha256(bytes)).ToLowerInvariant();
}
