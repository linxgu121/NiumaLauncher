using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NiumaLauncher.Models;

/// <summary>
/// 启动时的事务检查汇总。
/// 只报告检查结果，不执行恢复或授予安装权限。
/// </summary>
public sealed class InstallTransactionInspection
{
    #region Properties(检查结果)

    // 本次检查使用的配置身份，不从不可信记录中反推。
    public string GameId { get; }

    // 保留正式编号、临时文件和异常条目的发现结果。
    public InstallTransactionDiscovery Discovery { get; }

    // 成功读取并通过字段校验的记录。
    // 集合只读，但其中的事务对象仍然是可变 DTO。
    public IReadOnlyList<GameInstallTransaction> LoadedRecords { get; }

    // 操作编号 → 读取失败原因。
    public IReadOnlyDictionary<Guid, string> ReadFailures { get; }

    /// <summary>
    /// 是否发现需要进一步处理的信息。
    /// false 不代表游戏文件完整，也不代表可以直接安装。
    /// </summary>
    public bool RequiresAttention =>
        Discovery.RecordIds.Count > 0 ||
        Discovery.TemporaryFileNames.Count > 0 ||
        Discovery.UnexpectedEntryNames.Count > 0 ||
        ReadFailures.Count > 0;

    #endregion

    #region Construction(构造)

    internal InstallTransactionInspection(
        string gameId,
        InstallTransactionDiscovery discovery,
        IEnumerable<GameInstallTransaction> loadedRecords,
        IDictionary<Guid, string> readFailures)
    {
        GameId = gameId;
        Discovery = discovery;

        LoadedRecords = Array.AsReadOnly(loadedRecords.ToArray());

        // 先复制，再包装，避免原字典变化影响已经返回的结果。
        ReadFailures = new ReadOnlyDictionary<Guid, string>(
            new Dictionary<Guid, string>(readFailures));
    }

    #endregion
}