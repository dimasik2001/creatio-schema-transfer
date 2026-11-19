using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cst.Config;
using Cst.Creatio;
using Cst.Packaging;

namespace Cst.Transfer;

public sealed partial class Exporter(CreatioClient client)
{
    private static readonly Regex _guidRegex = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    private static Regex GuidRegex() => _guidRegex;

    public async Task<Manifest> ExportAsync(List<SchemaListPackage> list, string outPath, List<string>? fileEntries = null,
        List<string>? wholePackages = null)
    {
        fileEntries ??= [];
        wholePackages ??= [];
        list = list.Where(p => p.Entries.Count > 0).ToList();
        if (list.Count == 0 && fileEntries.Count == 0 && wholePackages.Count == 0)
            throw new ArgumentException("The schema list is empty.");

        var manifest = new Manifest { SourceUrl = client.Url };
        var errors = new List<string>();

        // Whole packages (packages: section): the package zip exactly as Creatio exports it.
        byte[]? wholeZip = null;
        if (wholePackages.Count > 0)
        {
            foreach (var both in list.Where(p => wholePackages.Contains(p.Source, StringComparer.OrdinalIgnoreCase)))
                errors.Add($"Package {both.Source} is in the packages: section (whole package) and also lists single elements; keep one.");
            var onSource = (await new Catalog(client).LoadPackagesAsync())
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var w in wholePackages)
            {
                if (onSource.TryGetValue(w, out var p)) names.Add(p.Name);
                else errors.Add($"Package {w} (packages: section) does not exist on the source environment.");
            }
            if (errors.Count > 0) throw new TransferException(errors);
            Log.Info($"Downloading whole packages from {client.Url}: {string.Join(", ", names)}");
            wholeZip = await client.GetZipPackagesAsync(names);
            var contents = PackageGz.ReadZip(wholeZip);
            foreach (var name in names)
            {
                if (!contents.TryGetValue(name, out var content)) { errors.Add($"Package {name} was not returned by the source environment."); continue; }
                var pd = content.PackageDescriptor() ?? throw new InvalidDataException($"{name}: descriptor.json is missing.");
                manifest.WholePackages.Add(new ManifestWholePackage
                {
                    Name = name,
                    UId = Guid.Parse(pd["UId"]!.ToString()),
                    Version = pd["PackageVersion"]?.ToString(),
                    Maintainer = pd["Maintainer"]?.ToString(),
                    DependsOn = (pd["DependsOn"] as JsonArray ?? []).OfType<JsonObject>().Select(d => new PackageRef
                    {
                        UId = Guid.Parse(d["UId"]!.ToString()), Name = d["Name"]!.ToString(), PackageVersion = d["PackageVersion"]?.ToString()
                    }).ToList(),
                    Schemas = content.ElementNames(ItemKind.Schema).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    ElementUIds = new[] { ItemKind.Schema, ItemKind.SqlScript, ItemKind.Data }
                        .SelectMany(k => content.ElementNames(k).Select(n => content.ElementDescriptor(k, n)?["UId"]?.ToString()))
                        .Select(u => Guid.TryParse(u, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList()
                });
            }
            if (errors.Count > 0) throw new TransferException(errors);
        }

        // Files of the Creatio folder (files: section): need the source environment's root folder.
        Dictionary<string, byte[]>? fileBytes = null;
        if (fileEntries.Count > 0)
        {
            var root = client.RootPath ?? throw new ArgumentException(
                $"The list has a files section, but the source environment has no Creatio root folder: set \"Path\" for it in appsettings.json (like clio's EnvironmentPath).");
            var relative = FileTransfer.Resolve(root, fileEntries, errors);
            if (errors.Count > 0) throw new TransferException(errors);
            (manifest.Files, fileBytes) = FileTransfer.Read(root, relative);
            Log.Info($"Files from {root}: {manifest.Files.Count} ({manifest.Files.Sum(f => f.Size) / 1024} KB)");
        }

        var sources = await CollectItemsAsync(list, manifest);

        await ResolveDependenciesAsync(manifest, sources);
        ComputeOrder(manifest);
        var exports = await ExportSchemaFilesAsync(manifest);

        var extra = fileBytes ?? new Dictionary<string, byte[]>();
        if (wholeZip != null) extra[Manifest.WholePackagesEntry] = wholeZip;
        TransferArchive.Write(outPath, manifest, sources, exports, extra);
        Log.Info($"Archive written: {Path.GetFullPath(outPath)}");
        return manifest;
    }

    /// <summary>
    /// Elements of the user's own packages that the listed elements reference but the list does not contain
    /// (the same GUID analysis as the export, no archive written). Only one level: what the selection itself needs.
    /// </summary>
    /// <param name="isOwnPackage">Keeps references into these packages; others (base packages) are expected on the target.</param>
    public async Task<List<ExternalDependency>> FindReferencesAsync(List<SchemaListPackage> list, Func<string, bool> isOwnPackage)
    {
        list = list.Where(p => p.Entries.Count > 0).ToList();
        if (list.Count == 0) return [];
        var manifest = new Manifest { SourceUrl = client.Url };
        var sources = await CollectItemsAsync(list, manifest);
        await ResolveDependenciesAsync(manifest, sources);
        return manifest.External.Where(e => isOwnPackage(e.SourcePackage)).ToList();
    }

    /// <summary>Downloads the listed packages and adds their listed elements to the manifest (packages and items).</summary>
    private async Task<Dictionary<string, PackageContent>> CollectItemsAsync(List<SchemaListPackage> list, Manifest manifest)
    {
        var errors = new List<string>();
        var sources = new Dictionary<string, PackageContent>(StringComparer.OrdinalIgnoreCase);
        if (list.Count > 0)
        {
            Log.Info($"Downloading packages from {client.Url}: {string.Join(", ", list.Select(p => p.Source))}");
            sources = PackageGz.ReadZip(await client.GetZipPackagesAsync(list.Select(p => p.Source)));
        }

        foreach (var pkg in list)
        {
            if (!sources.TryGetValue(pkg.Source, out var content))
            {
                errors.Add($"Package {pkg.Source} was not returned by the source environment.");
                continue;
            }
            var pd = content.PackageDescriptor() ?? throw new InvalidDataException($"{pkg.Source}: descriptor.json is missing.");
            manifest.Packages.Add(new ManifestPackage
            {
                Source = pkg.Source,
                Target = pkg.Target,
                SourceUId = Guid.Parse(pd["UId"]!.ToString()),
                Maintainer = pd["Maintainer"]?.ToString(),
                SourceDependsOn = (pd["DependsOn"] as JsonArray ?? []).OfType<JsonObject>().Select(d => new PackageRef
                {
                    UId = Guid.Parse(d["UId"]!.ToString()),
                    Name = d["Name"]!.ToString(),
                    PackageVersion = d["PackageVersion"]?.ToString()
                }).ToList()
            });

            foreach (var entry in pkg.Entries)
            {
                var kinds = entry.Kind is { } k ? [k] : new[] { ItemKind.Schema, ItemKind.SqlScript, ItemKind.Data };
                var found = kinds
                    .Select(kind => (kind, name: content.ElementNames(kind).FirstOrDefault(n => n.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))))
                    .Where(x => x.name != null).ToList();
                if (found.Count == 0) { errors.Add($"{pkg.Source}: '{entry.Name}' not found (no schema, SQL script or data binding with this name)."); continue; }
                if (found.Count > 1) { errors.Add($"{pkg.Source}: '{entry.Name}' is ambiguous ({string.Join(", ", found.Select(f => f.kind))}); add a type hint like [sql]."); continue; }

                var (itemKind, name) = found[0];
                var d = content.ElementDescriptor(itemKind, name!) ?? throw new InvalidDataException($"{pkg.Source}/{name}: descriptor.json is missing.");
                manifest.Items.Add(new ManifestItem
                {
                    Kind = itemKind,
                    Name = name!,
                    UId = Guid.Parse(d["UId"]!.ToString()),
                    Manager = d["ManagerName"]?.ToString(),
                    SourcePackage = pkg.Source,
                    TargetPackage = pkg.Target,
                    Files = content.ElementFiles(itemKind, name!)
                });
            }
        }
        if (errors.Count > 0) throw new TransferException(errors);

        var dupes = manifest.Items.GroupBy(i => i.UId).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            throw new TransferException(dupes.Select(g => $"Element {g.First().Name} is listed more than once ({string.Join(", ", g.Select(i => i.SourcePackage))})."));
        return sources;
    }

    /// <summary>
    /// Also exports each schema in the Configuration section format (.md). The importer uses it for target packages
    /// whose locally modified elements block the zip installer.
    /// </summary>
    private async Task<Dictionary<string, string>> ExportSchemaFilesAsync(Manifest manifest)
    {
        var schemas = manifest.Items.Where(i => i.Kind == ItemKind.Schema).ToList();
        var exports = new Dictionary<string, string>();
        if (schemas.Count == 0) return exports;
        Log.Info($"Exporting {schemas.Count} schema(s) in Configuration format...");
        var ids = await new Catalog(client).GetSchemaIdsAsync(schemas.Select(s => s.UId));
        var missing = schemas.Where(s => !ids.ContainsKey(s.UId)).Select(s => s.Name).ToList();
        if (missing.Count > 0) throw new TransferException(missing.Select(m => $"Schema {m} is in the package files but not in SysSchema."));
        var md = await client.ExportSchemasAsync(schemas.Select(s => (ids[s.UId], s.UId, s.Name)));
        foreach (var s in schemas)
        {
            if (!md.TryGetValue(s.Name, out var text)) throw new TransferException([$"ExportSchema returned nothing for {s.Name}."]);
            s.SchemaExport = $"exports/{s.SourcePackage}/{s.Name}.md";
            exports[s.SchemaExport] = text;
        }
        return exports;
    }

    private async Task ResolveDependenciesAsync(Manifest manifest, Dictionary<string, PackageContent> sources)
    {
        var byUId = manifest.Items.ToDictionary(i => i.UId);
        var packageUIds = manifest.Packages.Select(p => p.SourceUId)
            .Concat(manifest.Packages.SelectMany(p => p.SourceDependsOn.Select(d => d.UId))).ToHashSet();
        var candidates = new Dictionary<ManifestItem, HashSet<Guid>>();

        foreach (var item in manifest.Items)
        {
            var content = sources[item.SourcePackage];
            var folder = PackageContent.Folder(item.Kind) + "/" + item.Name + "/";
            // Data bindings: only the descriptor (data.json holds record ids, not schema references).
            var scan = item.Kind == ItemKind.Data
                ? new[] { folder + "descriptor.json" }
                : item.Files.Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            var guids = new HashSet<Guid>();
            foreach (var f in scan)
            {
                var bytes = content.Get(f);
                if (bytes == null) continue;
                foreach (Match m in GuidRegex().Matches(PackageContent.DecodeText(bytes)))
                    guids.Add(Guid.Parse(m.Value));
            }
            guids.Remove(item.UId);
            guids.ExceptWith(packageUIds);
            // SQL scripts reference tables by name: depend on archive entities mentioned in the script text.
            if (item.Kind == ItemKind.SqlScript)
            {
                var sql = string.Concat(item.Files.Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                    .Select(f => PackageContent.DecodeText(content.Get(f) ?? [])));
                foreach (var e in manifest.Items.Where(e => e.Manager == "EntitySchemaManager"))
                    if (Regex.IsMatch(sql, $@"(?<![\w]){Regex.Escape(e.Name)}(?![\w])", RegexOptions.IgnoreCase))
                        guids.Add(e.UId);
            }
            item.DependsOn = guids.Where(byUId.ContainsKey).OrderBy(g => g).ToList();
            guids.ExceptWith(item.DependsOn);
            candidates[item] = guids;
        }

        // Data bindings on SysSettingsValue need the settings themselves: order them after SysSettings bindings.
        string? BoundEntity(ManifestItem i) => i.Kind == ItemKind.Data
            ? sources[i.SourcePackage].ElementDescriptor(ItemKind.Data, i.Name)?["Schema"]?["Name"]?.ToString() : null;
        var settingBindings = manifest.Items.Where(i => BoundEntity(i) == "SysSettings").Select(i => i.UId).ToList();
        foreach (var item in manifest.Items.Where(i => BoundEntity(i) == "SysSettingsValue"))
            item.DependsOn = item.DependsOn.Union(settingBindings).OrderBy(g => g).ToList();

        var all = candidates.Values.SelectMany(g => g).ToHashSet();
        Log.Info($"Resolving {all.Count} referenced ids on the source environment...");
        var found = (await new Catalog(client).FindElementsAsync(all))
            .GroupBy(e => e.UId).ToDictionary(g => g.Key, g => g.First());

        var external = new Dictionary<Guid, ExternalDependency>();
        foreach (var (item, guids) in candidates)
        {
            foreach (var g in guids)
            {
                if (!found.TryGetValue(g, out var el)) continue; // column ids, record ids, etc.
                item.External.Add(g);
                if (!external.TryGetValue(g, out var ext))
                    external[g] = ext = new ExternalDependency { UId = g, Name = el.Name, Kind = el.Kind, Manager = el.ManagerName, SourcePackage = el.PackageName };
                ext.RequiredBy.Add(item.Name);
            }
            item.External.Sort();
        }
        manifest.External = external.Values.OrderBy(e => e.SourcePackage).ThenBy(e => e.Name).ToList();

        var listedPackages = manifest.Packages.Select(p => p.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest.External.Where(e => listedPackages.Contains(e.SourcePackage)))
            manifest.Warnings.Add($"{string.Join(", ", e.RequiredBy)} → {e.Name} ({e.Kind}) from {e.SourcePackage}, which is not in the list. It must already exist on the target.");
    }

    private static void ComputeOrder(Manifest manifest)
    {
        var byUId = manifest.Items.ToDictionary(i => i.UId);

        // Package order (on target package names). A cycle between packages cannot be installed.
        var targets = manifest.Packages.Select(p => p.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var pkgDeps = targets.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Items)
            foreach (var dep in item.DependsOn.Select(u => byUId[u]))
                if (!dep.TargetPackage.Equals(item.TargetPackage, StringComparison.OrdinalIgnoreCase))
                    pkgDeps[item.TargetPackage].Add(dep.TargetPackage);
        var pkgGroups = Graph.OrderedGroups(targets, t => pkgDeps[t]);
        var cycles = pkgGroups.Where(g => g.Count > 1).ToList();
        if (cycles.Count > 0)
            throw new TransferException(cycles.Select(c =>
                $"Packages {string.Join(" ↔ ", c)} reference each other's schemas; Creatio packages cannot depend on each other cyclically. Move the schemas into one package."));
        for (int i = 0; i < pkgGroups.Count; i++)
            foreach (var p in manifest.Packages.Where(p => p.Target.Equals(pkgGroups[i][0], StringComparison.OrdinalIgnoreCase)))
                p.Order = i;

        // Item order inside the whole archive; cycles (entities referencing each other,
        // processes calling each other) share one order number and are installed together.
        var groups = Graph.OrderedGroups(manifest.Items, i => i.DependsOn.Select(u => byUId[u]));
        for (int i = 0; i < groups.Count; i++)
            foreach (var item in groups[i]) item.Order = i;

        manifest.Packages.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.Items.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));
    }
}

public sealed class TransferException(IEnumerable<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors.ToList();
}
