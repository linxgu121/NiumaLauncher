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
    ProcessStatusUnknown,

    /// <summary>
    /// 正在获取发布信息并比较版本。
    /// </summary>
    CheckingUpdates,

    /// <summary>
    /// 正在把发布包下载到缓存。
    /// </summary>
    DownloadingPackage,

    /// <summary>
    /// 传输完成，正在校验磁盘文件。
    /// </summary>
    VerifyingPackage,

    /// <summary>
    /// 正在重新校验缓存包并解压到独立暂存目录。
    /// </summary>
    PreparingPackage,

    /// <summary>
    /// 正在只读检查安装目标并生成计划。
    /// </summary>
    BuildingInstallPlan,

    /// <summary>
    /// 正在检查上次安装留下的事务信息。
    /// </summary>
    CheckingInstallation,

    /// <summary>
    /// 有待处理事务，或无法确认安装状态，暂不开放操作。
    /// </summary>
    RecoveryRequired,

    /// <summary>
    /// 正在执行安装或安装后的复核，暂不开放其他操作。
    /// </summary>
    Installing
}