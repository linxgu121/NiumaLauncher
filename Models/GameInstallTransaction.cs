namespace NiumaLauncher.Models;

/// <summary>
/// 用于持久保存安装事务的数据。
/// 反序列化成功不代表内容合法，使用前必须校验。
/// </summary>
public sealed class GameInstallTransaction
{
    #region Identity and Phase(身份与阶段)

    /// <summary>
    /// 事务文件格式版本，不是游戏版本。
    /// 默认保留 0；后续创建记录时明确赋值。
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    /// 沿用安装计划的操作编号，不能重新生成。
    /// </summary>
    public Guid OperationId { get; set; }

    public string GameId { get; set; } = string.Empty;

    public InstallTransactionPhase Phase { get; set; }

    #endregion

    #region Build Snapshot(构建快照)

    /// <summary>
    /// 操作开始时的原版本，不能随着安装进度改成目标版本。
    /// </summary>
    public string CurrentVersion { get; set; } = string.Empty;

    public long CurrentBuildNumber { get; set; }

    public string TargetVersion { get; set; } = string.Empty;

    public long TargetBuildNumber { get; set; }

    /// <summary>
    /// 下载 ZIP 的字节数，不是解压后的大小。
    /// </summary>
    public long PackageSizeBytes { get; set; }

    /// <summary>
    /// 下载 ZIP 的 SHA-256，使用 64 个十六进制字符表示。
    /// </summary>
    public string PackageSha256 { get; set; } = string.Empty;

    #endregion

    #region Paths(路径依据)

    /// <summary>
    /// 正式游戏 EXE 的完整路径。
    /// 从记录读取后仍需检查，不能直接信任。
    /// </summary>
    public string GameExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// 本次工作区路径。
    /// 必须与正式目录及操作编号重新推导出的结果一致。
    /// </summary>
    public string WorkspaceDirectoryPath { get; set; } = string.Empty;

    #endregion
}