using System.IO;

namespace NiumaLauncher.Models;

/// <summary>
/// 一次安装操作的只读计划。
/// 保存数据，不执行文件操作，也不代表已获得覆盖目录的授权。
/// </summary>
public sealed class GameInstallPlan
{
    #region Identity and Versions(操作身份与版本)

    /// <summary>
    /// 本次操作的标识，不是游戏 ID 或构建编号。
    /// </summary>
    public Guid OperationId { get; }

    /// <summary>
    /// 本次使用的下载包，包含目标版本、缓存 ZIP 和哈希信息。
    /// </summary>
    public DownloadedGamePackage SourcePackage { get; }

    // 保存生成计划时读取的原版本值。
    // 不直接持有可变的 GameBuildManifest 对象。
    public string CurrentVersion { get; }

    public long CurrentBuildNumber { get; }

    #endregion

    #region Paths(安装路径)

    /// <summary>
    /// 正式游戏 EXE 的完整路径，安装后仍保持这个位置。
    /// </summary>
    public string GameExecutablePath { get; }

    /// <summary>
    /// 待更新的独立游戏构建目录。
    /// </summary>
    public string GameDirectoryPath { get; }

    /// <summary>
    /// 本次同卷工作区的计划路径，不表示目录已经创建。
    /// </summary>
    public string WorkspaceDirectoryPath { get; }

    /// <summary>
    /// 用于准备完整的新版本。
    /// </summary>
    public string CandidateDirectoryPath { get; }

    /// <summary>
    /// 用于保留完整的旧版本。
    /// </summary>
    public string BackupDirectoryPath { get; }

    /// <summary>
    /// 回滚时，隔离能确认属于本次操作的新版本目录。
    /// </summary>
    public string FailedNewDirectoryPath { get; }

    #endregion

    #region Construction(构造)

    // 后续由计划生成器完成检查后调用。
    // internal 只限制程序集外直接构造，不是安全隔离。
    internal GameInstallPlan(
        Guid operationId,
        DownloadedGamePackage sourcePackage,
        string currentVersion,
        long currentBuildNumber,
        string gameExecutablePath,
        string gameDirectoryPath,
        string workspaceDirectoryPath)
    {
        OperationId = operationId;
        SourcePackage = sourcePackage;

        CurrentVersion = currentVersion;
        CurrentBuildNumber = currentBuildNumber;

        GameExecutablePath = gameExecutablePath;
        GameDirectoryPath = gameDirectoryPath;
        WorkspaceDirectoryPath = workspaceDirectoryPath;

        // 固定相对名称，避免调用者分别传入不相关的位置。
        // 这里只组合字符串，不创建任何目录。
        CandidateDirectoryPath = Path.Combine(
            workspaceDirectoryPath,
            "candidate");

        BackupDirectoryPath = Path.Combine(
            workspaceDirectoryPath,
            "backup");

        FailedNewDirectoryPath = Path.Combine(
            workspaceDirectoryPath,
            "failed-new");
    }

    #endregion
}