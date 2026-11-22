namespace Cst.Data;

/// <summary>
/// "DbData" section of appsettings.json: thresholds of the table data transfer (docs/db-transfer.md, 3.4).
/// </summary>
public sealed class DataSettings
{
    /// <summary>A table at or above this size (MB) is "large" and gets <see cref="DefaultTop"/> rows by default.</summary>
    public double LargeTableMb { get; set; } = 25;
    /// <summary>Optional row threshold for "large"; null = only the size counts.</summary>
    public long? LargeTableRows { get; set; }
    /// <summary>Rows taken from a large table by default.</summary>
    public int DefaultTop { get; set; } = 1000;
    /// <summary>Order of the default top rows.</summary>
    public string DefaultTopOrder { get; set; } = "CreatedOn desc";
    /// <summary>Most rows one reference may pull into the export before the closure stops and asks.</summary>
    public int MaxPullRows { get; set; } = 10000;
    /// <summary>Rows in one DataService SelectQuery page.</summary>
    public int PageSize { get; set; } = 2000;
    /// <summary>Rows in one temporary SQL script on import.</summary>
    public int ScriptRows { get; set; } = 1000;
    /// <summary>Largest temporary SQL script body (MB). A ~21 MB body was refused with HTTP 400 on creatio-dev.</summary>
    public double ScriptMb { get; set; } = 4;

    public bool IsLarge(long rows, long bytes) =>
        bytes >= LargeTableMb * 1024 * 1024 || (LargeTableRows is { } r && rows >= r);
}
