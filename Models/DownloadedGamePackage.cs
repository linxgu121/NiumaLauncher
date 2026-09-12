namespace NiumaLauncher.Models;

/// <summary>
/// 下载时已通过大小和哈希校验的缓存包记录。
/// 不代表已经安装，也不能替代使用前的再次校验。
/// </summary>
public sealed class DownloadedGamePackage
{
    public string GameId { get; }

    public string Version { get; }

    public long BuildNumber { get; }

    /// <summary>
    /// 本地缓存 ZIP 的完整路径，不是游戏启动路径。
    /// </summary>
    public string FilePath { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }

    // 同一程序集内部创建，属性在构造之后不能重新赋值。
    internal DownloadedGamePackage(
        string gameId,
        string version,
        long buildNumber,
        string filePath,
        long sizeBytes,
        string sha256)
    {
        GameId = gameId;
        Version = version;
        BuildNumber = buildNumber;
        FilePath = filePath;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }
}