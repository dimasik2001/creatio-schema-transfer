using System.Security.Cryptography;
using System.Text;

namespace Cst;

/// <summary>
/// File and path helpers that work the same on net10 (the exe) and net48 (the PowerShell-loaded
/// library, which lacks File.*Async and Path.GetRelativePath).
/// </summary>
internal static class Io
{
    public static Task<string> ReadAllTextAsync(string path) => Task.FromResult(File.ReadAllText(path));
    public static Task<byte[]> ReadAllBytesAsync(string path) => Task.FromResult(File.ReadAllBytes(path));

    public static Task WriteAllTextAsync(string path, string contents)
    {
        File.WriteAllText(path, contents);
        return Task.CompletedTask;
    }

    public static Task WriteAllTextAsync(string path, string contents, Encoding encoding)
    {
        File.WriteAllText(path, contents, encoding);
        return Task.CompletedTask;
    }

    public static Task WriteAllBytesAsync(string path, byte[] bytes)
    {
        File.WriteAllBytes(path, bytes);
        return Task.CompletedTask;
    }

    public static string GetRelativePath(string relativeTo, string path)
    {
#if NETFRAMEWORK_POLYFILL
        string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseDir = Full(relativeTo) + Path.DirectorySeparatorChar;
        var target = Full(path);
        var baseUri = new Uri(baseDir);
        var targetUri = new Uri(target);
        var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString());
        return rel.Replace('/', Path.DirectorySeparatorChar);
#else
        return Path.GetRelativePath(relativeTo, path);
#endif
    }

    /// <summary>Lowercase hex of bytes (net48 has no Convert.ToHexString).</summary>
    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static byte[] Sha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(bytes);
    }
}
