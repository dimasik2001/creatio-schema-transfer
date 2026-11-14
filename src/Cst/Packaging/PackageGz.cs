using System.IO.Compression;
using System.Text;

namespace Cst.Packaging;

/// <summary>
/// The package content format Creatio uses inside a package zip: a gzip stream of records
/// [int32 path length in chars][UTF-16LE path][int32 content length][content bytes].
/// Paths use backslashes and are relative to the package root.
/// </summary>
public static class PackageGz
{
    public static PackageContent Read(byte[] gz)
    {
        using var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        var data = ms.ToArray();
        var content = new PackageContent();
        int i = 0;
        while (i < data.Length)
        {
            int nameLen = BitConverter.ToInt32(data, i); i += 4;
            string path = Encoding.Unicode.GetString(data, i, nameLen * 2); i += nameLen * 2;
            int len = BitConverter.ToInt32(data, i); i += 4;
            var bytes = new byte[len];
            Buffer.BlockCopy(data, i, bytes, 0, len); i += len;
            content.Set(path, bytes);
        }
        return content;
    }

    public static byte[] Write(PackageContent content)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(gz, Encoding.Unicode, leaveOpen: true))
        {
            foreach (var (path, bytes) in content.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var native = path.Replace('/', '\\');
                w.Write(native.Length);
                w.Write(Encoding.Unicode.GetBytes(native));
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Reads a package zip (as returned by GetZipPackages) into package name → content.</summary>
    public static Dictionary<string, PackageContent> ReadZip(byte[] zip)
    {
        var result = new Dictionary<string, PackageContent>(StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result[Path.GetFileNameWithoutExtension(entry.Name)] = Read(ms.ToArray());
        }
        return result;
    }

    public static byte[] WriteZip(string packageName, PackageContent content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(packageName + ".gz", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(Write(content));
        }
        return ms.ToArray();
    }
}
