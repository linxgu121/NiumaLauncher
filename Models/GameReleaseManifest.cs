namespace NiumaLauncher.Models;

/// <summary>
/// 发布信息数据
/// </summary>
public class GameReleaseManifest
{
    public int SchemaVersion { get; set; }

    public string GameId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public long BuildNumber { get; set; }

    /// <summary>
    /// 发布包的完整下载地址。
    /// </summary>
    public string PackageUrl { get; set; } = string.Empty;

    /// <summary>
    /// ZIP 文件本身的字节数，不是解压后的安装大小。
    /// </summary>
    public long PackageSizeBytes { get; set; }

    /// <summary>
    /// ZIP 文件的 SHA-256，使用 64 位十六进制字符串表示。
    /// </summary>
    public string PackageSha256 { get; set; } = string.Empty;

    
}