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
}