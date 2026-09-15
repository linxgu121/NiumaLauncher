using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 分析同一安装路径的事务历史关系。
/// 只处理内存报告，不检查目录内容，也不执行恢复。
/// </summary>
internal static class GameInstallHistoryAnalyzer
{
    #region Public API(分析入口)

    /// <summary>
    /// 正常完成历史检查：不允许存在未完成事务。
    /// </summary>
    internal static IReadOnlyList<Guid> GetCompletedOperationIds(
        InstallTransactionInspection inspection,
        string expectedGameId,
        string expectedExecutablePath)
    {
        return Analyze(
            inspection,
            expectedGameId,
            expectedExecutablePath,
            requirePending: false).CompletedOperationIds;
    }

    /// <summary>
    /// 恢复历史检查：要求恰好一笔未完成事务，并位于历史末尾。
    /// </summary>
    internal static (
        IReadOnlyList<Guid> CompletedOperationIds,
        Guid PendingOperationId) GetRecoveryOperationIds(
        InstallTransactionInspection inspection,
        string expectedGameId,
        string expectedExecutablePath)
    {
        var result = Analyze(
            inspection,
            expectedGameId,
            expectedExecutablePath,
            requirePending: true);

        if (result.PendingOperationId is not Guid pendingId)
        {
            throw new InvalidDataException(
                "恢复历史中没有唯一待恢复事务。");
        }

        return (result.CompletedOperationIds, pendingId);
    }

    #endregion

    #region Analysis(共用历史关系检查)

    private static (
        IReadOnlyList<Guid> CompletedOperationIds,
        Guid? PendingOperationId) Analyze(
        InstallTransactionInspection inspection,
        string expectedGameId,
        string expectedExecutablePath,
        bool requirePending)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        if (!string.Equals(
                inspection.GameId,
                expectedGameId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "检查报告不属于本次指定的游戏。");
        }

        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                expectedExecutablePath);

        InstallTransactionDiscovery discovery = inspection.Discovery;

        // 不能过滤掉异常条目，再从剩余记录中挑一个进行恢复。
        if (inspection.ReadFailures.Count > 0 ||
            discovery.TemporaryFileNames.Count > 0 ||
            discovery.UnexpectedEntryNames.Count > 0)
        {
            throw new InvalidDataException(
                "报告中仍有读取失败、临时文件或异常条目。");
        }

        if (!discovery.DirectoryExists &&
            discovery.RecordIds.Count > 0)
        {
            throw new InvalidDataException(
                "事务目录不存在，但报告仍包含事务编号。");
        }

        var remainingIds = new HashSet<Guid>(discovery.RecordIds);

        if (remainingIds.Count != discovery.RecordIds.Count ||
            remainingIds.Contains(Guid.Empty))
        {
            throw new InvalidDataException(
                "已发现的事务编号存在重复或无效值。");
        }

        // 复制必要字段，后续不继续引用可变 DTO。
        // 调用方仍须保证复制期间不并发修改报告。
        var records = inspection.LoadedRecords
            .Select(record => new
            {
                record.OperationId,
                record.Phase,
                record.GameId,
                record.GameExecutablePath,
                record.CurrentVersion,
                record.CurrentBuildNumber,
                record.TargetVersion,
                record.TargetBuildNumber
            })
            .ToArray();

        if (records.Length != remainingIds.Count)
        {
            throw new InvalidDataException(
                "已发现的事务数量与成功读取的记录数量不一致。");
        }

        foreach (var record in records)
        {
            // 只消费本地校对集合，不修改报告或磁盘。
            if (!remainingIds.Remove(record.OperationId))
            {
                throw new InvalidDataException(
                    "已读取的事务编号重复，或不在发现列表中。");
            }

            if (!string.Equals(
                    record.GameId,
                    expectedGameId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    record.GameExecutablePath,
                    executablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "报告包含其他游戏或安装路径的事务。");
            }

            // 必须先确认阶段受支持，不能把未知阶段当成待恢复。
            if (record.Phase is not (
                    InstallTransactionPhase.PreparingCandidate
                    or InstallTransactionPhase.CandidateReady
                    or InstallTransactionPhase.BackupMovePending
                    or InstallTransactionPhase.CandidateMovePending
                    or InstallTransactionPhase.Completed))
            {
                throw new InvalidDataException(
                    "历史记录包含不支持的事务阶段。");
            }

            if (string.IsNullOrWhiteSpace(record.CurrentVersion) ||
                string.IsNullOrWhiteSpace(record.TargetVersion) ||
                record.CurrentBuildNumber <= 0 ||
                record.TargetBuildNumber <= record.CurrentBuildNumber)
            {
                throw new InvalidDataException(
                    "历史记录中的原构建或目标构建信息无效。");
            }
        }

        // 从刚复制的快照分类，不使用可能已过时的报告分组。
        var pendingRecords = records
            .Where(record =>
                record.Phase != InstallTransactionPhase.Completed)
            .ToArray();

        int expectedPendingCount = requirePending ? 1 : 0;

        if (pendingRecords.Length != expectedPendingCount)
        {
            throw new InvalidDataException(
                requirePending
                    ? "恢复分析要求恰好一笔未完成事务。"
                    : "历史中仍有未完成事务，不能按正常检查放行。");
        }

        // 只有正常完成历史入口能够接受空历史。
        if (records.Length == 0)
        {
            return (Array.Empty<Guid>(), null);
        }

        // 按构建号排序，不依赖文件时间、Guid 或版本文本顺序。
        var ordered = records
            .OrderBy(record => record.TargetBuildNumber)
            .ToArray();

        for (int index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            var current = ordered[index];

            if (previous.TargetBuildNumber == current.TargetBuildNumber)
            {
                throw new InvalidDataException(
                    "多份事务指向同一个目标构建，无法确定唯一历史。");
            }

            // 后一次更新必须从前一次更新的目标构建开始。
            if (previous.TargetBuildNumber != current.CurrentBuildNumber ||
                !string.Equals(
                    previous.TargetVersion,
                    current.CurrentVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "历史存在断档、分叉或版本标签不一致。");
            }
        }

        Guid? pendingId = pendingRecords.Length == 1
            ? pendingRecords[0].OperationId
            : null;

        if (pendingId is Guid id &&
            ordered[^1].OperationId != id)
        {
            throw new InvalidDataException(
                "未完成事务不在历史末尾，不能自动确定恢复顺序。");
        }

        // 只返回只读编号列表，不把可变事务对象继续传下去。
        IReadOnlyList<Guid> completedIds = Array.AsReadOnly(
            ordered
                .Where(record =>
                    record.Phase == InstallTransactionPhase.Completed)
                .Select(record => record.OperationId)
                .ToArray());

        return (completedIds, pendingId);
    }

    #endregion
}