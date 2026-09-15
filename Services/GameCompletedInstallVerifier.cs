using System;
using System.IO;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 复核完成记录对应的正式构建及保留工作区。
/// 后续由统一持锁流程调用；这里只读，不恢复、不清理、不改变门禁。
/// </summary>
internal static class GameCompletedInstallVerifier
{
    #region Verification(复核入口)

    internal static void VerifyCurrentBuild(
        Guid operationId,
        string expectedGameId,
        string expectedExecutablePath)
    {
        GameInstallTransaction record = LoadCompletedRecord(
            operationId,
            expectedGameId,
            expectedExecutablePath);

        string executablePath = record.GameExecutablePath;

        string gameDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("无法确定正式游戏目录。");

        using WindowsDirectoryReference directoryReference =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                gameDirectory);

        // 正式位置应当是本次更新的目标构建。
        GameInstallBuildVerifier.VerifyAtPath(
            executablePath,
            expectedGameId,
            record.TargetVersion,
            record.TargetBuildNumber,
            directoryReference);
    }

    internal static void VerifyRetainedWorkspace(
        Guid operationId,
        string expectedGameId,
        string expectedExecutablePath)
    {
        GameInstallTransaction record = LoadCompletedRecord(
            operationId,
            expectedGameId,
            expectedExecutablePath);

        // LoadExisting 已检查工作区与正式路径、操作编号的关系。
        string workspacePath = record.WorkspaceDirectoryPath;
        string backupPath = Path.Combine(workspacePath, "backup");

        string backupExecutablePath = Path.Combine(
            backupPath,
            Path.GetFileName(record.GameExecutablePath));

        using WindowsDirectoryReference workspaceReference =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                workspacePath);

        RequireBackupOnly(workspacePath);

        using WindowsDirectoryReference backupReference =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                backupPath);

        // Current 是安装开始前的原构建，不是现在的正式构建。
        GameInstallBuildVerifier.VerifyAtPath(
            backupExecutablePath,
            expectedGameId,
            record.CurrentVersion,
            record.CurrentBuildNumber,
            backupReference);

        // 内容读取结束后，再检查布局和最初观察到的目录实体。
        RequireBackupOnly(workspacePath);

        backupReference.RequireSameDirectoryAt(backupPath);
        workspaceReference.RequireSameDirectoryAt(workspacePath);
    }

    /// <summary>
    /// 调用方必须在整个方法执行期间持续持有安装锁。
    /// 本方法不获取或释放锁，也不使用界面缓存中的旧报告。
    /// </summary>
    internal static (
        InstallTransactionInspection Inspection,
        string GameExecutablePath,
        IReadOnlyList<Guid> VerifiedOperationIds)
        VerifyHistoryUnderLock(
            string expectedGameId,
            string expectedExecutablePath,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        cancellationToken.ThrowIfCancellationRequested();

        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                expectedExecutablePath);

        var transactionStore = new GameInstallTransactionStore();

        // 必须在持锁期间重新取得报告。
        InstallTransactionInspection inspection =
            transactionStore.InspectExisting(expectedGameId);

        cancellationToken.ThrowIfCancellationRequested();

        // 分析器负责拒绝未完成记录、异常条目和不连续的历史。
        IReadOnlyList<Guid> operationIds =
           GameInstallHistoryAnalyzer.GetCompletedOperationIds(
                inspection,
                expectedGameId,
                executablePath);

        // 每份完成记录都应保留自己的原构建备份。
        foreach (Guid operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            VerifyRetainedWorkspace(
                operationId,
                expectedGameId,
                executablePath);
        }

        if (operationIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid latestOperationId =
                operationIds[operationIds.Count - 1];

            // 正式构建只与历史末端的目标版本比较。
            VerifyCurrentBuild(
                latestOperationId,
                expectedGameId,
                executablePath);
        }

        // 这里只做复核，没有发布阶段或移动目录，可以响应迟到取消。
        cancellationToken.ThrowIfCancellationRequested();

        // 空编号列表表示没有完成历史，不表示游戏文件已经检查通过。
        return (inspection, executablePath, operationIds);
    }

    #endregion

    #region Record Binding(完成记录绑定)

    private static GameInstallTransaction LoadCompletedRecord(
        Guid operationId,
        string expectedGameId,
        string expectedExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        // 期望路径来自调用方确定的选择或计划，不能从记录反推。
        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                expectedExecutablePath);

        var transactionStore = new GameInstallTransactionStore();

        // 每次重新读取，不使用界面缓存中的可变事务对象。
        GameInstallTransaction record =
            transactionStore.LoadExisting(
                operationId,
                expectedGameId);

        if (record.Phase != InstallTransactionPhase.Completed)
        {
            throw new InvalidDataException(
                "该事务尚未登记完成，不能使用完成记录复核流程。");
        }

        if (!string.Equals(
                record.GameExecutablePath,
                executablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "完成记录不属于本次指定的游戏程序路径。");
        }

        return record;
    }

    #endregion

    #region Workspace Layout(工作区布局)

    private static void RequireBackupOnly(string workspacePath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        int count = 0;

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     workspacePath,
                     "*",
                     options))
        {
            count++;

            // 只允许恰好一个 backup；任何其他条目都停止检查。
            if (count > 1 ||
                !string.Equals(
                    Path.GetFileName(entry),
                    "backup",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "已完成事务的工作区只能保留一个 backup 条目。");
            }
        }

        if (count != 1)
        {
            throw new InvalidDataException(
                "已完成事务的工作区缺少 backup 条目。");
        }

        // backup 是否为普通目录，由随后的目录引用检查负责。
    }


    #endregion
}