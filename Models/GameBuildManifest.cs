namespace NiumaLauncher.Models;

public class GameBuildManifest
{
    /// <summary>
    /// 默认保持 0，遗漏字段时不能误判为支持的格式。
    /// JSON 数据格式版本。游戏更新时不一定修改它
    /// </summary>
    public int SchemaVersion { get; set; }
 
    /// <summary>
    /// 稳定的游戏身份，不随游戏显示名称改变
    /// </summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>
    /// 用于展示，后续不要直接按字符串大小比较版本。
    /// 给玩家看的版本文字，例如 1.0.0
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// 构建编号，后续在同一游戏、同一发布线路内比较新旧
    /// </summary>
    public long BuildNumber { get; set; }

    /// <summary>
    /// 本阶段要求清单与游戏 EXE 位于同一目录。
    /// 清单所属的游戏程序文件名，不是完整路径
    /// </summary>
    public string ExecutableName { get; set; } = string.Empty;
}