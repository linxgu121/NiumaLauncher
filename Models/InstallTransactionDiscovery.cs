using System;
using System.Collections.Generic;
using System.Linq;

namespace NiumaLauncher.Models;

/// <summary>
/// 一次事务目录发现结果。
/// 只描述观察到的条目，不表示事务有效或允许安装。
/// </summary>
public sealed class InstallTransactionDiscovery
{
    #region Properties(结果属性)

    // 仅表示本次检查是否发现目录，不代表没有历史操作。
    public bool DirectoryExists { get; }

    // 文件名符合规则的正式记录编号，尚未验证 JSON 内容。
    public IReadOnlyList<Guid> RecordIds { get; }

    // 名称符合写入规则的临时文件，不自动删除。
    public IReadOnlyList<string> TemporaryFileNames { get; }

    // 未知名称、子目录、重解析点等异常条目。
    public IReadOnlyList<string> UnexpectedEntryNames { get; }

    #endregion

    #region Construction(构造)

    internal InstallTransactionDiscovery(
        bool directoryExists,
        IEnumerable<Guid> recordIds,
        IEnumerable<string> temporaryFileNames,
        IEnumerable<string> unexpectedEntryNames)
    {
        DirectoryExists = directoryExists;

        // 复制集合，避免外部修改原列表影响已返回的结果。
        RecordIds = Array.AsReadOnly(recordIds.ToArray());
        TemporaryFileNames = Array.AsReadOnly(temporaryFileNames.ToArray());
        UnexpectedEntryNames = Array.AsReadOnly(unexpectedEntryNames.ToArray());
    }

    #endregion
}