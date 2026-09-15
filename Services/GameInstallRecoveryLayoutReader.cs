using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 读取指定未完成事务的目录布局。
/// 只观察位置，不核对构建内容，也不执行恢复。
/// </summary>
internal static class GameInstallRecoveryLayoutReader
{
    #region Observation(事务布局观察)

    /// <summary>
    /// 调用方必须在后台执行，并持续持有安装锁。
    /// 本方法不获取或释放安装锁。
    /// 读取失败直接抛出，不返回部分布局。
    /// </summary>
    internal static InstallRecoveryLayout ReadUnderLock(
        Guid operationId,
        string expectedGameId,
        string expectedExecutablePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                expectedExecutablePath);

        var transactionStore = new GameInstallTransactionStore();

        // 重新读取并严格校验，不使用界面缓存中的可变事务对象。
        GameInstallTransaction record =
            transactionStore.LoadExisting(operationId, expectedGameId);

        if (record.Phase == InstallTransactionPhase.Completed)
        {
            throw new InvalidDataException(
                "已完成事务应使用完成历史复核流程。");
        }

        // 期望路径来自调用方，不能从待验证记录中反推。
        if (!string.Equals(
                record.GameExecutablePath,
                executablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "事务不属于本次指定的游戏程序路径。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string gameDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("无法确定正式游戏目录。");

        string parentDirectory = Path.GetDirectoryName(gameDirectory)
            ?? throw new InvalidDataException("无法确定游戏父目录。");

        // LoadExisting 已校验工作区与正式路径、操作编号的关系。
        string workspacePath = record.WorkspaceDirectoryPath;
        string candidatePath = Path.Combine(workspacePath, "candidate");
        string backupPath = Path.Combine(workspacePath, "backup");
        string failedNewPath = Path.Combine(workspacePath, "failed-new");

        using WindowsDirectoryReference parentReference =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                parentDirectory);

        bool gameExists =
            ReadKnownDirectory(gameDirectory, cancellationToken);

        bool workspaceExists =
            ReadKnownDirectory(workspacePath, cancellationToken);

        using WindowsDirectoryReference? workspaceReference =
            workspaceExists
                ? WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                    workspacePath)
                : null;

        bool candidateExists;
        bool backupExists;
        bool failedNewExists;

        if (workspaceExists)
        {
            RequireKnownWorkspaceEntries(
                workspacePath, cancellationToken);

            candidateExists =
                ReadKnownDirectory(candidatePath, cancellationToken);

            backupExists =
                ReadKnownDirectory(backupPath, cancellationToken);

            failedNewExists =
                ReadKnownDirectory(failedNewPath, cancellationToken);
        }
        else
        {
            // 工作区已确认不存在，三个固定子路径也不可能存在。
            // 这里没有把读取失败的 null 改成 false。
            candidateExists = false;
            backupExists = false;
            failedNewExists = false;
        }

        // 再次观察有无状态，发现中途变化就停止。
        RequireSamePresence(
            gameDirectory, gameExists, cancellationToken);

        RequireSamePresence(
            workspacePath, workspaceExists, cancellationToken);

        if (workspaceExists)
        {
            RequireKnownWorkspaceEntries(
                workspacePath, cancellationToken);

            RequireSamePresence(
                candidatePath, candidateExists, cancellationToken);

            RequireSamePresence(
                backupPath, backupExists, cancellationToken);

            RequireSamePresence(
                failedNewPath, failedNewExists, cancellationToken);
        }

        workspaceReference?.RequireSameDirectoryAt(workspacePath);
        parentReference.RequireSameDirectoryAt(parentDirectory);

        cancellationToken.ThrowIfCancellationRequested();

        // 这里只分类位置组合，不判断目录中是否是正确版本。
        return GameInstallRecoveryLayoutAnalyzer.Classify(
            record.Phase,
            gameDirectoryExists: gameExists,
            workspaceDirectoryExists: workspaceExists,
            candidateDirectoryExists: candidateExists,
            backupDirectoryExists: backupExists,
            failedNewDirectoryExists: failedNewExists);
    }

    #endregion

    #region Directory Checks(目录观察与复验)

    private static bool ReadKnownDirectory(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool? exists =
            WindowsDirectoryPathVerifier.ObserveDirectory(
                path, out string diagnostic);

        cancellationToken.ThrowIfCancellationRequested();

        if (exists is bool knownValue)
        {
            return knownValue;
        }

        // 保留单目录观察给出的原因，不继续拼装半份结果。
        throw new IOException(
            $"无法确认目录状态：{path}。{diagnostic}");
    }

    private static void RequireSamePresence(
        string path,
        bool expectedPresence,
        CancellationToken cancellationToken)
    {
        if (ReadKnownDirectory(path, cancellationToken) != expectedPresence)
        {
            throw new InvalidDataException(
                $"观察期间目录的有无状态发生变化：{path}");
        }
    }

    #endregion

    #region Workspace Entries(工作区条目检查)

    private static void RequireKnownWorkspaceEntries(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     workspacePath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = Path.GetFileName(entry);

            // 只接受安装器创建的三个精确名称。
            // 异常或重复名称立即停止，集合最多保留三个名称。
            if (name is not ("candidate" or "backup" or "failed-new") ||
                !names.Add(name))
            {
                throw new InvalidDataException(
                    $"工作区包含异常或重复条目：{entry}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    #endregion
}