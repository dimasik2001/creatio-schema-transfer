namespace Cst.Transfer;

/// <summary>What an import has to build after it saved schemas.</summary>
public enum CompileKind
{
    /// <summary>No schemas: SQL scripts, data bindings and files need no build.</summary>
    None,
    /// <summary>
    /// Only client modules (JS, Classic and Freedom UI pages): no C#, so no compilation and no app restart — only the
    /// client static content is regenerated (WorkspaceExplorerService/BuildConfiguration = BuildChanged).
    /// </summary>
    ContentOnly,
    /// <summary>Schemas that produce C# (objects, source code, processes, user tasks, unknown types): compile.</summary>
    Compile
}

/// <summary>
/// Decides the build after an import. A Creatio "Build" compiles the configuration AND regenerates the client content
/// of changed schemas (ConfigurationBuilderFacade.Build → WorkspaceBuilder.Build + BuildChanged, decompiled 10.0.0.801);
/// "BuildConfiguration" does only the second part. Unknown schema types are treated as needing compilation.
/// </summary>
public static class CompileNeed
{
    /// <summary>Schema managers whose schemas carry no C# (client side only).</summary>
    private static readonly HashSet<string> ClientOnlyManagers = new(StringComparer.Ordinal) { "ClientUnitSchemaManager" };

    public static bool ManagerNeedsCompile(string? manager) => manager == null || !ClientOnlyManagers.Contains(manager);

    /// <param name="managers">Manager of every imported schema (null = unknown).</param>
    /// <param name="wholePackagesNeedCompile">Whole packages in the archive and whether each needs compilation (null = unknown).</param>
    public static (CompileKind Kind, List<string> Reasons) Decide(IEnumerable<(string Name, string? Manager)> schemas, IEnumerable<(string Name, bool? NeedsCompile)> wholePackages)
    {
        var list = schemas.ToList();
        var whole = wholePackages.ToList();
        var reasons = list.Where(s => ManagerNeedsCompile(s.Manager)).Select(s => s.Name)
            .Concat(whole.Where(w => w.NeedsCompile != false).Select(w => $"package {w.Name}")).ToList();
        if (reasons.Count > 0) return (CompileKind.Compile, reasons);
        return list.Count > 0 || whole.Count > 0 ? (CompileKind.ContentOnly, []) : (CompileKind.None, []);
    }

    public static string Describe(CompileKind kind, IReadOnlyList<string> reasons) => kind switch
    {
        CompileKind.Compile => $"compilation needed for {string.Join(", ", reasons.Take(5))}{(reasons.Count > 5 ? $" and {reasons.Count - 5} more" : "")}",
        CompileKind.ContentOnly => "no compilation: only client modules — client content is regenerated (no app restart)",
        _ => "no compilation: no schemas (SQL, data bindings, files only)"
    };
}
