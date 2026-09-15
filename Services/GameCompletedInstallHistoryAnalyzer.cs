using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 分析同一安装路径的完成记录顺序。
/// 只处理内存报告，不读取磁盘，也不授予安装或恢复权限。
/// </summary>
internal static class GameCompletedInstallHistoryAnalyzer
{
    #region Analysis(历史关系检查)

    internal static IReadOnlyList<Guid> GetOrderedOperationIds(
        InstallTransactionInspection inspection,
        string expectedGameId,
        string expectedExecutablePath)
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

        // 不能忽略未知情况，只从剩余记录里挑一个“最新成功”。
        if (inspection.ReadFailures.Count > 0 ||
            discovery.TemporaryFileNames.Count > 0 ||
            discovery.UnexpectedEntryNames.Count > 0)
        {
            throw new InvalidDataException(
                "报告中仍有读取失败、临时文件或异常条目，不能分析纯完成历史。");
        }

        var remainingIds = new HashSet<Guid>(discovery.RecordIds);

        if (remainingIds.Count != discovery.RecordIds.Count ||
            remainingIds.Contains(Guid.Empty))
        {
            throw new InvalidDataException(
                "已发现的事务编号存在重复或无效值。");
        }

        // 只复制分析需要的值，后续排序不继续引用可变 DTO。
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
            // 只消费本地校对集合，不删除文件，也不修改原报告。
            if (!remainingIds.Remove(record.OperationId))
            {
                throw new InvalidDataException(
                    "已读取的事务编号重复，或不在发现列表中。");
            }

            if (record.Phase != InstallTransactionPhase.Completed)
            {
                throw new InvalidDataException(
                    "历史中仍有未完成事务，不能按纯完成历史处理。");
            }

            if (!string.Equals(
                    record.GameId,
                    expectedGameId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "历史记录包含其他游戏的事务。");
            }

            // 当前阶段只处理一个指定安装位置，不能悄悄过滤其他路径。
            if (!string.Equals(
                    record.GameExecutablePath,
                    executablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "报告包含其他安装路径的事务，本阶段不能自动合并处理。");
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

        if (records.Length == 0)
        {
            return Array.Empty<Guid>();
        }

        // 当前更新规则要求构建号递增，不按文件时间、Guid 或版本文本排序。
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
                    "多份完成记录指向同一个目标构建，无法确定唯一历史。");
            }

            // 后一次更新必须从前一次更新的目标构建开始。
            if (previous.TargetBuildNumber != current.CurrentBuildNumber ||
                !string.Equals(
                    previous.TargetVersion,
                    current.CurrentVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "完成历史存在断档、分叉或版本标签不一致，不能自动确定顺序。");
            }
        }

        // 只返回只读编号列表，不把可变事务对象继续传下去。
        return Array.AsReadOnly(
            ordered.Select(record => record.OperationId).ToArray());
    }

    #endregion
}