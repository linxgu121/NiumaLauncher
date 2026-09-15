using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 将本轮检查事实转换成恢复建议。
/// 不读取磁盘、不修改事务、不执行目录操作。
/// </summary>
internal static class GameInstallRecoveryDecisionAnalyzer
{
    #region Recommendation(恢复建议)

    /// <summary>
    /// 仅用于完整检查流程得到的事实。
    /// 枚举参数本身不是旧构建有效或允许恢复的凭证。
    /// </summary>
    internal static InstallRecoveryRecommendation Recommend(
        InstallRecoveryLayout layout,
        InstallRecoveryTargetStatus targetStatus)
    {
        return (layout, targetStatus) switch
        {
            // 尚未完成候选准备，保留原来的正式构建。
            (
                InstallRecoveryLayout.GameOnly
                    or InstallRecoveryLayout.PreparationWorkspace,
                InstallRecoveryTargetStatus.NotChecked
            ) => InstallRecoveryRecommendation.KeepOriginalBuild,

            // 原构建仍在正式位置，不因候选情况自动续装或清理。
            (
                InstallRecoveryLayout.GameAndCandidate,
                InstallRecoveryTargetStatus.MatchesExpected
                    or InstallRecoveryTargetStatus.NotVerified
            ) => InstallRecoveryRecommendation.KeepOriginalBuild,

            // 正式位置为空、旧备份已通过核对，优先恢复旧版。
            // 此建议不操作候选目录。
            (
                InstallRecoveryLayout.CandidateAndBackup,
                InstallRecoveryTargetStatus.MatchesExpected
                    or InstallRecoveryTargetStatus.NotVerified
            ) => InstallRecoveryRecommendation.RestoreOriginalBackup,

            // 新构建已经落位且身份匹配，进入完成确认流程。
            (
                InstallRecoveryLayout.GameAndBackup,
                InstallRecoveryTargetStatus.MatchesExpected
            ) => InstallRecoveryRecommendation.ConfirmTargetInstallation,

            // 包括正式位置内容未能确认、未知值及不相容组合。
            _ => InstallRecoveryRecommendation.ManualReview
        };
    }

    #endregion
}