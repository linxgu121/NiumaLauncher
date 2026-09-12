namespace NiumaLauncher.Models;

/// <summary>
/// 下载器向界面报告的数据，不直接操作界面。
/// </summary>
public readonly record struct PackageDownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    bool IsVerifying)
{
    public double Percentage =>
        TotalBytes > 0
            ? DownloadedBytes * 100d / TotalBytes
            : 0d;
}