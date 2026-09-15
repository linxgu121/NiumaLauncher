using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 协调后台安装阶段及会话生命周期。
/// 后台入口用于候选准备和完成历史复核，尚未开放正式安装
/// </summary>
internal static class GameInstallCoordinator
{
    #region Background Entry(后台入口)

    /// <summary>
    /// 成功时返回 Candidate；
    /// 发现未完成记录等阻塞条目时返回 BlockingInspection
    /// 其他错误与取消通过异常传播
    /// </summary>
    internal static Task<(
        StagedGamePackage? Candidate,
        InstallTransactionInspection? BlockingInspection)>
        PrepareCandidateOnlyAsync(
            GameInstallPlan plan,
            string expectedGameId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        // 会话也在后台建立，避免同步检查阻塞界面。
        // 这里不能提前取得安装锁。
        return Task.Run(
            () => PrepareCandidateOnlyCoreAsync(
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
        StagedGamePackage? Candidate,
        InstallTransactionInspection? BlockingInspection)>
        PrepareCandidateOnlyCoreAsync(
            GameInstallPlan plan,
            string expectedGameId,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // using 必须覆盖整个候选准备过程。
        using GameInstallSession? session =
            GameInstallSession.BeginIfClear(
                plan,
                expectedGameId,
                cancellationToken,
                out InstallTransactionInspection inspection);

        if (session is null)
        {
            // 未完成记录或异常条目仍然阻止建立安装会话。
            // 保留完整报告，供调用方说明阻塞原因。
            return (null, inspection);
        }

        // 同步前置检查期间可能收到取消请求。
        cancellationToken.ThrowIfCancellationRequested();

        StagedGamePackage candidate =
            await GamePackageStager.PrepareCandidateCoreAsync(
                session,
                cancellationToken).ConfigureAwait(false);

        // CandidateReady 已经持久发布，
        // 不再用随后到达的取消请求反转阶段结果。
        return (candidate, null);
    }

    #endregion
}