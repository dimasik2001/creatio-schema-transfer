using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Transfer;

public enum BindingState
{
    /// <summary>The target package has no binding of this Name.</summary>
    New,
    /// <summary>Same Name and same UId: the import updates it.</summary>
    Same,
    /// <summary>Same Name, different UId: the binding SQL would fail; needs a <see cref="BindingFix"/>.</summary>
    Conflict,
    /// <summary>The target package could not be downloaded (see <see cref="BindingCheckRow.Error"/>).</summary>
    Unavailable
}

public sealed record BindingCheckRow(ManifestItem Item, BindingState State, Guid? TargetUId, string? Error);

public enum BindingFixAction { Rename, UseTargetUId, Skip }

/// <summary>How one conflicting binding is resolved in a new archive.</summary>
public sealed class BindingFix
{
    /// <summary>Source package of the binding in the archive.</summary>
    public required string Package { get; init; }
    public required string Name { get; init; }
    public required BindingFixAction Action { get; init; }
    /// <summary>For <see cref="BindingFixAction.Rename"/>.</summary>
    public string? NewName { get; init; }
    /// <summary>For <see cref="BindingFixAction.UseTargetUId"/>.</summary>
    public Guid? TargetUId { get; init; }

    public static string DefaultRename(string name) => name + "_resolved";
}

/// <summary>
/// Compares the data bindings of an archive with the target packages (by Name and UId) and writes a resolved copy of
/// the archive. A same-name binding with a DIFFERENT UId is what makes the binding SQL fail
/// ("the package already has a data binding named X with another UId").
/// </summary>
public static class BindingCheck
{
    /// <summary>Downloads each target package once (slow: tens of seconds to minutes) and classifies every binding.</summary>
    public static async Task<List<BindingCheckRow>> CheckAsync(CreatioClient client, Manifest manifest)
    {
        var rows = new List<BindingCheckRow>();
        foreach (var g in manifest.Items.Where(i => i.Kind == ItemKind.Data).GroupBy(i => i.TargetPackage, StringComparer.OrdinalIgnoreCase))
        {
            PackageContent content;
            try { content = PackageGz.ReadZip(await client.GetZipPackagesAsync([g.Key])).GetValueOrDefault(g.Key) ?? new PackageContent(); }
            catch (Exception ex)
            {
                rows.AddRange(g.Select(i => new BindingCheckRow(i, BindingState.Unavailable, null, ex.Message)));
                continue;
            }
            var targetByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in content.ElementNames(ItemKind.Data))
                targetByName[n] = content.ElementDescriptor(ItemKind.Data, n)?["UId"]?.ToString();
            foreach (var item in g)
            {
                if (!targetByName.TryGetValue(item.Name, out var t) || t == null)
                    rows.Add(new BindingCheckRow(item, BindingState.New, null, null));
                else if (Guid.TryParse(t, out var tUId) && tUId == item.UId)
                    rows.Add(new BindingCheckRow(item, BindingState.Same, tUId, null));
                else if (Guid.TryParse(t, out tUId))
                    rows.Add(new BindingCheckRow(item, BindingState.Conflict, tUId, null));
                else
                    rows.Add(new BindingCheckRow(item, BindingState.Unavailable, null, $"target binding has an invalid UId '{t}'"));
            }
        }
        return rows;
    }

    static string Key(string package, string name) => package + "\u0000" + name;

    /// <summary>Copies the archive applying the chosen fixes: rename a data binding, adopt the target UId, or drop it.</summary>
    public static void WriteResolvedArchive(string src, string dst, IEnumerable<BindingFix> fixList)
    {
        var (manifest, _) = TransferArchive.Read(src);
        var fixes = new Dictionary<string, BindingFix>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixList)
        {
            if (f.Action == BindingFixAction.Rename && (string.IsNullOrWhiteSpace(f.NewName) || f.NewName!.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"{f.Package}/{f.Name}: rename needs a new name different from the current one.");
            if (f.Action == BindingFixAction.UseTargetUId && f.TargetUId == null)
                throw new ArgumentException($"{f.Package}/{f.Name}: the target UId is not set.");
            fixes[Key(f.Package, f.Name)] = f;
        }

        // Patch the typed manifest.
        var remove = new List<ManifestItem>();
        foreach (var f in fixes.Values)
        {
            var item = manifest.Items.FirstOrDefault(i => i.Kind == ItemKind.Data
                && i.SourcePackage.Equals(f.Package, StringComparison.OrdinalIgnoreCase)
                && i.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            switch (f.Action)
            {
                case BindingFixAction.Skip: remove.Add(item); break;
                case BindingFixAction.UseTargetUId: item.UId = f.TargetUId!.Value; break;
                case BindingFixAction.Rename:
                    var oldP = $"Data/{item.Name}/"; var newP = $"Data/{f.NewName}/";
                    item.Files = item.Files.Select(p => p.StartsWith(oldP, StringComparison.OrdinalIgnoreCase) ? newP + p.Substring(oldP.Length) : p).ToList();
                    item.Name = f.NewName!;
                    break;
            }
        }
        foreach (var it in remove) manifest.Items.Remove(it);

        using var inZip = ZipFile.OpenRead(src);
        using var outFs = File.Create(dst);
        using var outZip = new ZipArchive(outFs, ZipArchiveMode.Create);
        using (var s = outZip.CreateEntry(TransferArchive.ManifestName).Open()) JsonSerializer.Serialize(s, manifest, Manifest.Json);

        foreach (var e in inZip.Entries)
        {
            if (e.FullName == TransferArchive.ManifestName || e.FullName.EndsWith("/")) continue;
            byte[] bytes;
            using (var s = e.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            var outName = e.FullName;
            var parts = e.FullName.Split('/');
            if (parts.Length >= 4 && parts[0] == "packages" && parts[2] == "Data"
                && fixes.TryGetValue(Key(parts[1], parts[3]), out var f))
            {
                var rest = string.Join("/", parts.Skip(4));
                if (f.Action == BindingFixAction.Skip) continue; // drop the binding's files
                if (f.Action == BindingFixAction.Rename)
                {
                    outName = $"packages/{parts[1]}/Data/{f.NewName}/{rest}";
                    if (rest == "descriptor.json") bytes = PatchDataDescriptor(bytes, f.NewName, null);
                }
                else if (rest == "descriptor.json")
                    bytes = PatchDataDescriptor(bytes, null, f.TargetUId);
            }
            using var os = outZip.CreateEntry(outName).Open();
            os.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Patches a data binding descriptor.json: Descriptor.Name and/or Descriptor.UId.</summary>
    static byte[] PatchDataDescriptor(byte[] bytes, string? newName, Guid? newUId)
    {
        var root = JsonNode.Parse(PackageContent.DecodeText(bytes)) as JsonObject;
        if (root?["Descriptor"] is JsonObject d)
        {
            if (newName != null) d["Name"] = newName;
            if (newUId != null) d["UId"] = newUId.Value.ToString();
        }
        return System.Text.Encoding.UTF8.GetBytes(root?.ToJsonString() ?? "");
    }
}
