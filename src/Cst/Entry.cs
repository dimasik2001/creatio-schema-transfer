namespace Cst
{
    /// <summary>Public entry point so a host (e.g. Windows PowerShell loading the assembly) can run cst in-process.</summary>
    public static class CstEntry
    {
        public static int Run(string[] args) => global::Cli.RunAsync(args).GetAwaiter().GetResult();
    }
}
