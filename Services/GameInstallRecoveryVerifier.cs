using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 读取未完成事务的布局并核对构建
/// 只报告观察结果，不执行恢复
/// </summary>
internal static class GameInstallRecoveryVerifier
{
    #region Observation and Verification(布局与构建核对)

    /// <summary>
    /// 调用方必须在后台执行，并持续持有安装锁。
    /// 布局或旧构建失败直接抛出；目标内容未能确认时记录原因
    /// 仅正常返回的检查结果才用于形成建议，建议不代表执行许可
    /// </summary>
    internal static (InstallRecoveryLayout Layout, InstallRecoveryTargetStatus TargetStatus, string TargetDiagnostic, InstallRecoveryRecommendation Recommendation) InspectBuildsUnderLock(
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

        // 先结合记录阶段与实际位置，判断旧版本应该在哪里。
        InstallRecoveryLayout layout =
            GameInstallRecoveryLayoutAnalyzer.Classify(
                record.Phase,
                gameDirectoryExists: gameExists,
                workspaceDirectoryExists: workspaceExists,
                candidateDirectoryExists: candidateExists,
                backupDirectoryExists: backupExists,
                failedNewDirectoryExists: failedNewExists);

        string originalBuildDirectory = layout switch
        {
            // 旧目录尚未移走。
            InstallRecoveryLayout.GameOnly
                or InstallRecoveryLayout.PreparationWorkspace
                or InstallRecoveryLayout.GameAndCandidate
                => gameDirectory,

            // 旧目录已经进入备份位置。
            InstallRecoveryLayout.CandidateAndBackup
                or InstallRecoveryLayout.GameAndBackup
                => backupPath,

            // 未知布局不能默认按“旧版本还在正式目录”处理。
            _ => throw new InvalidDataException(
                "当前布局无法确定旧构建位置，不能继续恢复核对。")
        };

        // 程序名称沿用已经与事务绑定的正式 EXE 名称。
        string originalExecutablePath = Path.Combine(
            originalBuildDirectory,
            Path.GetFileName(executablePath));

        // 引用保持到整个方法结束，覆盖后续的布局复验。
        using WindowsDirectoryReference originalBuildReference =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                originalBuildDirectory);

        GameInstallBuildVerifier.VerifyAtPath(
            originalExecutablePath,
            expectedGameId,
            record.CurrentVersion,
            record.CurrentBuildNumber,
            originalBuildReference);

        // 新版本的位置与旧版本不同，必须单独选择。
        string? targetBuildDirectory = layout switch
        {
            // 尚未进入候选准备完成的布局，本轮不检查目标内容。
            InstallRecoveryLayout.GameOnly
                or InstallRecoveryLayout.PreparationWorkspace
                => null,

            // 新版本仍在候选位置。
            InstallRecoveryLayout.GameAndCandidate
                or InstallRecoveryLayout.CandidateAndBackup
                => candidatePath,

            // 候选已经进入正式位置。
            InstallRecoveryLayout.GameAndBackup
                => gameDirectory,

            _ => throw new InvalidDataException(
                "当前布局无法确定目标构建的检查方式。")
        };

        // 打开目录放在内容检查的 catch 外。
        // 如果连目录边界都无法确认，应停止整个检查流程。
        using WindowsDirectoryReference? targetBuildReference =
            targetBuildDirectory is null
                ? null
                : WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                    targetBuildDirectory);

        InstallRecoveryTargetStatus targetStatus =
            InstallRecoveryTargetStatus.NotChecked;

        string targetDiagnostic =
            layout == InstallRecoveryLayout.GameOnly
                ? "本次工作区尚未创建，未核对目标构建。"
                : "候选仍处于准备阶段，未核对目标构建。";

        if (targetBuildDirectory is not null &&
            targetBuildReference is not null)
        {
            string targetExecutablePath = Path.Combine(
                targetBuildDirectory,
                Path.GetFileName(executablePath));

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // 新版本必须对比 Target，不能沿用旧版本的 Current。
                GameInstallBuildVerifier.VerifyAtPath(
                    targetExecutablePath,
                    expectedGameId,
                    record.TargetVersion,
                    record.TargetBuildNumber,
                    targetBuildReference);

                targetStatus =
                    InstallRecoveryTargetStatus.MatchesExpected;

                targetDiagnostic = string.Empty;
            }
            catch (Exception ex) when (
                ex is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or JsonException
                    or System.Security.SecurityException)
            {
                // 只记录“未能确认”，不把权限或读取错误认定为损坏。
                targetStatus =
                    InstallRecoveryTargetStatus.NotVerified;

                targetDiagnostic =
                    $"{ex.GetType().Name}: {ex.Message}";
            }
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

        // 完成其余布局检查后，再比较旧构建目录是否仍是同一实体。
        originalBuildReference.RequireSameDirectoryAt(originalBuildDirectory);

        // 实际检查过目标目录时，它也必须仍指向同一实体。
        if (targetBuildDirectory is not null &&
            targetBuildReference is not null)
        {
            targetBuildReference.RequireSameDirectoryAt(
                targetBuildDirectory);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 所有检查和复验正常结束后，才根据本轮事实形成建议。
        InstallRecoveryRecommendation recommendation =
            GameInstallRecoveryDecisionAnalyzer.Recommend(
                layout,
                targetStatus);

        return (layout,targetStatus,targetDiagnostic,recommendation);
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