using System.Text;
using System.Text.Json.Nodes;

namespace Cst.Creatio;

/// <summary>
/// Reads the Configuration section's per-schema export (.md). It is JSON, except that Creatio breaks long string
/// values with physical line breaks: "...\r&lt;CR LF&gt;\n..." (also right after a backslash inside nested escaped
/// strings). Dropping raw CR/LF inside string literals gives standard JSON with the same values.
/// </summary>
public static class CreatioMd
{
    public static JsonNode? Parse(string md) => JsonNode.Parse(ToJson(md));

    public static string ToJson(string md)
    {
        var sb = new StringBuilder(md.Length);
        bool inString = false, escape = false;
        foreach (var ch in md)
        {
            if (inString && (ch == '\r' || ch == '\n')) continue; // break inside a value, keeps a pending escape
            sb.Append(ch);
            if (!inString) { if (ch == '"') inString = true; continue; }
            if (escape) { escape = false; continue; }
            if (ch == '\\') escape = true;
            else if (ch == '"') inString = false;
        }
        return sb.ToString();
    }
}
