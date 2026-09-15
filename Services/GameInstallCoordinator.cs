using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 协调后台安装阶段及会话生命周期
/// 提供完整安装与完成历史复核入口；实际安装尚未接入界面
/// </summary>
internal static class GameInstallCoordinator
{
    #region Background Entry(后台入口)

    /// <summary>
    /// 执行完整安装流程；调用方必须先取得用户对安装计划的明确确认。
    /// 成功时返回 CompletedOperationId；
    /// 明确阻塞时返回 BlockingInspection；
    /// 其他错误与取消通过异常传播。
    /// 当前暂不接入界面。
    /// </summary>
    internal static Task<(
        Guid? CompletedOperationId,
        InstallTransactionInspection? BlockingInspection)>
        InstallAsync(
            GameInstallPlan plan,
            string expectedGameId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        // 会话也在后台建立，避免文件检查和安装工作阻塞界面。
        return Task.Run(
            () => InstallCoreAsync(
                plan,
                expectedGameId,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 在后台持续持有同一把安装锁，完成历史及构建复核。
    /// 错误或取消通过异常传播，不返回部分成功结果。
    /// </summary>
    internal static Task<(
        InstallTransactionInspection Inspection,
        string GameExecutablePath,
        IReadOnlyList<Guid> VerifiedOperationIds)>
        VerifyCompletedHistoryAsync(
            string expectedGameId,
            string expectedExecutablePath,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 锁在后台取得，作用域覆盖完整检查过程。
                using LauncherInstallLock installationLock =
                    LauncherInstallLock.Acquire();

                return GameCompletedInstallVerifier.VerifyHistoryUnderLock(
                    expectedGameId,
                    expectedExecutablePath,
                    cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// 在同一段持锁流程中检查恢复历史、保留备份和未完成事务现场。
    /// 只返回检查结果，不执行恢复，也不改变界面门禁。
    /// </summary>
    internal static Task<(
        InstallTransactionInspection Inspection,
        string GameExecutablePath,
        IReadOnlyList<Guid> VerifiedCompletedOperationIds,
        Guid PendingOperationId,
        (
            InstallRecoveryLayout Layout,
            InstallRecoveryTargetStatus TargetStatus,
            string TargetDiagnostic,
            InstallRecoveryRecommendation Recommendation
        ) PendingBuild)>
        InspectRecoveryAsync(
            string expectedGameId,
            string expectedExecutablePath,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                string executablePath =
                    GameInstallPlanBuilder.NormalizeLocalPath(
                        expectedExecutablePath);

                // 锁覆盖整轮检查，中途不能释放后再重新取得。
                using LauncherInstallLock installationLock =
                    LauncherInstallLock.Acquire();

                cancellationToken.ThrowIfCancellationRequested();

                var transactionStore = new GameInstallTransactionStore();

                // 不使用界面缓存中的旧报告。
                InstallTransactionInspection inspection =
                    transactionStore.InspectExisting(expectedGameId);

                cancellationToken.ThrowIfCancellationRequested();

                // 拒绝异常日志、历史断档及多笔未完成事务。
                var history =
                    GameInstallHistoryAnalyzer.GetRecoveryOperationIds(
                        inspection,
                        expectedGameId,
                        executablePath);

                // 历史完成记录只检查各自保留的工作区和旧备份。
                // 恢复时不能假设正式目录仍是最后一次完成的版本。
                foreach (Guid operationId in history.CompletedOperationIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    GameCompletedInstallVerifier.VerifyRetainedWorkspace(
                        operationId,
                        expectedGameId,
                        executablePath);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 唯一未完成事务根据自身布局检查新旧构建，并形成建议。
                var pendingBuild =
                    GameInstallRecoveryVerifier.InspectBuildsUnderLock(
                        history.PendingOperationId,
                        expectedGameId,
                        executablePath,
                        cancellationToken);

                // 本流程没有切换目录，可以响应检查期间收到的取消。
                cancellationToken.ThrowIfCancellationRequested();

                return (
                    Inspection: inspection,
                    GameExecutablePath: executablePath,
                    VerifiedCompletedOperationIds:
                        history.CompletedOperationIds,
                    PendingOperationId: history.PendingOperationId,
                    PendingBuild: pendingBuild);
            },
            cancellationToken);
    }

    #endregion

    #region Commit Boundary(提交边界)

    /// <summary>
    /// 完成提交前最后检查，并持久登记旧目录备份意图。
    /// 调用方必须在后台串行执行，并持续持有同一个安装会话。
    /// 本方法不移动目录，也不释放会话。
    /// </summary>
    private static void RegisterBackupMoveIntent(
        GameInstallSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        cancellationToken.ThrowIfCancellationRequested();

        // Plan getter 同时拒绝使用已经释放的会话。
        GameInstallPlan plan = session.Plan;

        // 复用事务、目录身份、布局与候选构建的检查。
        GamePackageStager.RevalidateCandidateBuild(session);

        // 同步检查期间可能收到取消请求。
        cancellationToken.ThrowIfCancellationRequested();

        // 准备阶段的进程检查已经过去一段时间，需要重新观察。
        session.VerifyNoMatchingGameProcess();

        // 继续使用本会话保存的实体基线，不重新捕获目录。
        session.VerifyGameDirectoryUnchanged();
        session.VerifyWorkspaceDirectoriesUnchanged();

        var transactionStore = new GameInstallTransactionStore();

        // 普通用户取消的最后响应点。
        // 通过这里后，先完整完成意图发布。
        cancellationToken.ThrowIfCancellationRequested();

        // 服务内部仍会重新核对记录、计划和前置阶段。
        // 成功仅表示意图发布，不代表目录已经移动。
        _ = transactionStore.MarkBackupMovePending(
            plan,
            session.ExpectedGameId);

        // 不在发布成功后追加取消检查。
        // 后续完整流程应继续受控切换或失败处理。
    }

    #endregion

    #region Directory Switching(目录切换)

    /// <summary>
    /// 登记备份意图，并将原游戏目录移入本次备份位置。
    /// 仅供后续完整安装流程在同一个持锁会话内调用。
    /// 本方法不是重启恢复入口，也不表示安装已经完成。
    /// </summary>
    private static void MoveOldGameToBackup(
        GameInstallSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        GameInstallPlan plan = session.Plan;

        // 包含最终复核和最后一次普通用户取消检查。
        // 登记失败会抛异常，不会继续执行下面的移动。
        RegisterBackupMoveIntent(session, cancellationToken);

        // 登记期间路径仍可能发生变化，移动前再次核对实体。
        session.VerifyGameDirectoryUnchanged();
        session.VerifyWorkspaceDirectoriesUnchanged();

        // backup 必须不存在；不预先创建，不覆盖已有目标。
        Directory.Move(
            plan.GameDirectoryPath,
            plan.BackupDirectoryPath);

        // 工作区和 candidate 没有移动，继续核对它们原来的位置。
        session.VerifyWorkspaceDirectoriesUnchanged();

        // 旧游戏已经换了位置，改在 backup 核对原身份。
        session.VerifyGameDirectoryAtBackup();
    }

    /// <summary>
    /// 旧目录备份成功后，将候选移入正式游戏位置。
    /// 必须由完整安装流程在同一个持锁会话内串行调用。
    /// 本方法不负责重启恢复，也不表示安装已经完成。
    /// </summary>
    private static void MoveCandidateToGame(GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        GameInstallPlan plan = session.Plan;

        GamePackageStager.RevalidateCandidateAfterBackup(session);

        // 清单读取后重新观察游戏进程与目录布局。
        session.VerifyNoMatchingGameProcess();
        session.VerifyBeforeCandidateMove();

        var transactionStore = new GameInstallTransactionStore();

        // 内部重新核对计划及 BackupMovePending 前置阶段。
        // 登记失败会抛异常，不会继续移动。
        _ = transactionStore.MarkCandidateMovePending(
            plan,
            session.ExpectedGameId);

        // 意图发布期间路径可能变化，移动前再次检查。
        session.VerifyBeforeCandidateMove();

        Directory.Move(
            plan.CandidateDirectoryPath,
            plan.GameDirectoryPath);

        // 使用原候选身份检查正式位置，不重新捕获基线。
        session.VerifyAfterCandidateMove();
    }

    /// <summary>
    /// 候选落位后执行最终复验，并持久登记完成。
    /// 必须继续使用同一个安装会话，不在这里释放资源。
    /// </summary>
    private static void CompleteInstallation(GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        GameInstallPlan plan = session.Plan;

        // 验证失败会抛异常，不会继续登记 Completed。
        GamePackageStager.RevalidateInstalledBuild(session);

        var transactionStore = new GameInstallTransactionStore();

        _ = transactionStore.MarkCompleted(
            plan,
            session.ExpectedGameId);

        // 已发布完成阶段，不追加取消检查，也不自动删除备份。
    }

    #endregion

    #region Session Lifetime(会话生命周期)

    private static async Task<(
    Guid? CompletedOperationId,
    InstallTransactionInspection? BlockingInspection)>
    InstallCoreAsync(
        GameInstallPlan plan,
        string expectedGameId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 同一个会话覆盖准备、目录切换和完成登记。
        // 方法正常结束或抛出异常后，才释放会话资源。
        using GameInstallSession? session =
            GameInstallSession.BeginIfClear(
                plan,
                expectedGameId,
                cancellationToken,
                out InstallTransactionInspection inspection);

        if (session is null)
        {
            return (null, inspection);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 等待候选准备结束。
        // 不把候选路径作为最终结果，因为后面会移动该目录。
        _ = await GamePackageStager.PrepareCandidateCoreAsync(
            session,
            cancellationToken).ConfigureAwait(false);

        // 内部已经包含提交前复核、最后普通取消响应和备份意图登记。
        // 不要在外层重复调用 RegisterBackupMoveIntent。
        MoveOldGameToBackup(session, cancellationToken);

        // 进入提交边界后，不在两个目录移动之间追加普通取消检查。
        MoveCandidateToGame(session);

        // 从正式位置重新验证新版本，然后持久登记 Completed。
        CompleteInstallation(session);

        // 完成登记后不再用迟到取消反转结果，也不自动清理备份。
        return (session.Plan.OperationId, null);
    }
    #endregion
}