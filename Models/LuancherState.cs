namespace NiumaLauncher.Models;

public enum LauncherState
{
    /// <summary>
    /// 尚未选择游戏
    /// </summary>
    NoGameSelected,
    /// <summary>
    /// 游戏路径有效，可以启动
    /// </summary>
    Ready,
    /// <summary>
    /// 正在创建游戏进程
    /// </summary>
    Starting,
    /// <summary>
    /// 游戏进程已创建，等待退出
    /// </summary>
    Running,
    /// <summary>
    /// 已记录的游戏路径不可用
    /// </summary>
    GameMissing,
    /// <summary>
    /// 监控失败不等于进程退出，暂时禁止重复启动。
    /// </summary>
    ProcessStatusUnknown
}