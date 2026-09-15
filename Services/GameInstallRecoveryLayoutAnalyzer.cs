using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 根据日志阶段和目录观察结果，分类可能的恢复布局。
/// 不读取磁盘、不修改事务，也不授予移动或删除权限。
/// </summary>
internal static class GameInstallRecoveryLayoutAnalyzer
{
    #region Classification(布局分类)

    /// <summary>
    /// 目录观察值：
    /// true：确认存在符合要求的普通目录。
    /// false：确认该路径没有文件或目录条目。
    /// null：尚未观察、无法读取，或条目类型不符合要求。
    /// </summary>
    internal static InstallRecoveryLayout Classify(
        InstallTransactionPhase phase,
        bool? gameDirectoryExists,
        bool? workspaceDirectoryExists,
        bool? candidateDirectoryExists,
        bool? backupDirectoryExists,
        bool? failedNewDirectoryExists)
    {
        // 不允许把“读不出来”当成“不存在”。
        if (gameDirectoryExists is not bool gameExists ||
            workspaceDirectoryExists is not bool workspaceExists ||
            candidateDirectoryExists is not bool candidateExists ||
            backupDirectoryExists is not bool backupExists ||
            failedNewDirectoryExists is not bool failedNewExists)
        {
            return InstallRecoveryLayout.Unknown;
        }

        // 当前还没有描述失败隔离过程的恢复阶段。
        // 发现 failed-new 时，不能猜测它的来源和用途。
        if (failedNewExists)
        {
            return InstallRecoveryLayout.Unknown;
        }

        // 准备意图已经登记，但可能还没来得及创建工作区。
        if (!workspaceExists)
        {
            return phase == InstallTransactionPhase.PreparingCandidate &&
                   gameExists &&
                   !candidateExists &&
                   !backupExists
                ? InstallRecoveryLayout.GameOnly
                : InstallRecoveryLayout.Unknown;
        }

        // 以下分支中，工作区已确认存在。
        if (gameExists && !backupExists)
        {
            if (phase == InstallTransactionPhase.PreparingCandidate)
            {
                // candidate 可以尚不存在，也可以只写了一部分。
                return InstallRecoveryLayout.PreparationWorkspace;
            }

            if (candidateExists &&
                (phase is InstallTransactionPhase.CandidateReady
                    or InstallTransactionPhase.BackupMovePending))
            {
                return InstallRecoveryLayout.GameAndCandidate;
            }

            return InstallRecoveryLayout.Unknown;
        }

        // 可能已完成旧目录备份，但候选尚未进入正式位置。
        if (!gameExists &&
            candidateExists &&
            backupExists &&
            (phase is InstallTransactionPhase.BackupMovePending
                or InstallTransactionPhase.CandidateMovePending))
        {
            return InstallRecoveryLayout.CandidateAndBackup;
        }

        // 可能已完成候选移动，但还没有登记 Completed。
        if (gameExists &&
            !candidateExists &&
            backupExists &&
            phase == InstallTransactionPhase.CandidateMovePending)
        {
            return InstallRecoveryLayout.GameAndBackup;
        }

        // Completed 交给已有的完成历史复核流程。
        // 非法阶段和其他无法解释的组合也不能放行。
        return InstallRecoveryLayout.Unknown;
    }

    #endregion
}