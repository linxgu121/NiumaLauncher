using System;
using System.IO;
using System.Threading;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 持有安装操作锁、原游戏及工作区目录身份引用和已复核计划
/// 当前不登记事务、不创建候选、不切换游戏目录。
/// </summary>`
internal sealed class GameInstallSession : IDisposable
{
    #region State and Construction(状态与构造)

    private LauncherInstallLock? _installationLock;
    private WindowsDirectoryReference? _gameDirectoryReference;

    private WindowsDirectoryReference? _workspaceDirectoryReference;
    private WindowsDirectoryReference? _candidateDirectoryReference;

    private readonly GameInstallPlan _plan;

    // 只表示本会话开始过准备尝试，不表示准备成功。
    private int _candidatePreparationStarted;

    private GameInstallSession(
        LauncherInstallLock installationLock,
        WindowsDirectoryReference gameDirectoryReference,
        GameInstallPlan plan,
        string expectedGameId)
    {
        _installationLock = installationLock;
        _gameDirectoryReference = gameDirectoryReference;
        _plan = plan;
        ExpectedGameId = expectedGameId;
    }

    #endregion

    #region Properties(会话数据)

    public string ExpectedGameId { get; }

    public GameInstallPlan Plan
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                _installationLock is null,
                this);

            return _plan;
        }
    }

    #endregion

    #region Opening(建立持锁会话)

    /// <summary>
    /// 存在未完成记录或异常条目时返回 null，并保留检查报告。
    /// 完成历史必须在本会话持锁期间重新复核。
    /// 复核、计划检查或身份核对失败时抛出异常。
    /// </summary>
    public static GameInstallSession? BeginIfClear(
        GameInstallPlan plan,
        string expectedGameId,
        CancellationToken cancellationToken,
        out InstallTransactionInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        cancellationToken.ThrowIfCancellationRequested();

        LauncherInstallLock? installationLock =
            LauncherInstallLock.Acquire();

        WindowsDirectoryReference? gameDirectoryReference = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transactionStore = new GameInstallTransactionStore();

            inspection = transactionStore.InspectExisting(
                expectedGameId);

            cancellationToken.ThrowIfCancellationRequested();

            // Completed 本身不是阻塞原因。
            // 未完成、读取失败、临时文件和异常条目仍然必须阻止。
            if (inspection.IncompleteRecords.Count > 0 ||
                inspection.ReadFailures.Count > 0 ||
                inspection.Discovery.TemporaryFileNames.Count > 0 ||
                inspection.Discovery.UnexpectedEntryNames.Count > 0)
            {
                return null;
            }

            // 已经持有安装锁，直接调用同步核心。
            // 不能调用会再次获取同一把锁的异步入口。
            var history =
                GameCompletedInstallVerifier.VerifyHistoryUnderLock(
                    expectedGameId,
                    plan.GameExecutablePath,
                    cancellationToken);

            inspection = history.Inspection;

            var planBuilder = new GameInstallPlanBuilder();

            GameInstallPlan checkedPlan =
                planBuilder.RevalidateBeforeWorkspaceCreation(
                    plan,
                    expectedGameId);

            if (history.VerifiedOperationIds.Count > 0)
            {
                Guid latestOperationId =
                    history.VerifiedOperationIds[
                        history.VerifiedOperationIds.Count - 1];

                // 顺序来自历史分析器，不使用文件枚举顺序。
                GameInstallTransaction latestRecord =
                    inspection.CompletedRecords.Single(
                        record => record.OperationId == latestOperationId);

                // 新计划必须从上一轮已经完成的目标构建开始。
                if (checkedPlan.CurrentBuildNumber !=
                        latestRecord.TargetBuildNumber ||
                    !string.Equals(
                        checkedPlan.CurrentVersion,
                        latestRecord.TargetVersion,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "新安装计划的原版本与完成历史末端不一致，" +
                        "请重新检查并预览安装计划。");
                }
            }

            gameDirectoryReference =
                WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                    checkedPlan.GameDirectoryPath);

            // 捕获原目录引用后，再检查计划和当前位置。
            // 保留原 OperationId，不重新生成计划编号。
            checkedPlan =
                planBuilder.RevalidateBeforeWorkspaceCreation(
                    checkedPlan,
                    expectedGameId);

            gameDirectoryReference.RequireSameDirectoryAt(checkedPlan.GameDirectoryPath);

            // 重新观察实际进程，不使用界面缓存中的运行状态。
            GameProcessGate.RequireNoMatchingProcess(checkedPlan.GameExecutablePath);

            // 尚未登记事务或移动目录，交接前仍然可以响应取消。
            cancellationToken.ThrowIfCancellationRequested();

            var session = new GameInstallSession(
                installationLock,
                gameDirectoryReference,
                checkedPlan,
                expectedGameId);

            // 成功交接后，两个资源统一由会话负责释放。
            gameDirectoryReference = null;
            installationLock = null;

            return session;
        }
        finally
        {
            try
            {
                gameDirectoryReference?.Dispose();
            }
            finally
            {
                // 即使目录引用释放失败，也要尝试释放安装锁。
                installationLock?.Dispose();
            }
        }
    }

    #endregion

    #region Candidate Preparation(候选准备入口)

    internal GameInstallPlan ClaimCandidatePreparation()
    {
        // 同时检查会话尚未释放。
        GameInstallPlan plan = Plan;

        if (Interlocked.CompareExchange(
                ref _candidatePreparationStarted,
                1,
                0) != 0)
        {
            throw new InvalidOperationException(
                "同一个安装会话只能发起一次候选准备。");
        }

        VerifyGameDirectoryUnchanged();
        VerifyNoMatchingGameProcess();

        return plan;
    }

    #endregion

    #region Workspace Identity(工作区身份)

    /// <summary>
    /// 工作区创建后、解压前，捕获并持有两个目录的身份。
    /// 成功捕获后不能重新建立基线。
    /// </summary>
    internal void CaptureWorkspaceDirectories()
    {
        GameInstallPlan plan = Plan;

        if (_candidatePreparationStarted != 1)
        {
            throw new InvalidOperationException(
                "尚未认领候选准备，不能捕获工作区目录。");
        }

        if (_workspaceDirectoryReference is not null ||
            _candidateDirectoryReference is not null)
        {
            throw new InvalidOperationException(
                "本会话已经捕获工作区目录，不能重新建立身份基线。");
        }

        VerifyGameDirectoryUnchanged();

        WindowsDirectoryReference? workspaceReference = null;
        WindowsDirectoryReference? candidateReference = null;

        try
        {
            workspaceReference =
                WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                    plan.WorkspaceDirectoryPath);

            candidateReference =
                WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                    plan.CandidateDirectoryPath);

            workspaceReference.RequireSameDirectoryAt(
                plan.WorkspaceDirectoryPath);

            candidateReference.RequireSameDirectoryAt(
                plan.CandidateDirectoryPath);

            VerifyGameDirectoryUnchanged();

            // 全部成功后才交给会话持有。
            _workspaceDirectoryReference = workspaceReference;
            _candidateDirectoryReference = candidateReference;

            workspaceReference = null;
            candidateReference = null;
        }
        finally
        {
            // 只释放尚未成功交接的局部资源。
            try
            {
                candidateReference?.Dispose();
            }
            finally
            {
                workspaceReference?.Dispose();
            }
        }
    }

    /// <summary>
    /// 比较当前路径与准备时捕获的目录实体。
    /// 适用于工作区与候选仍位于原准备位置的阶段。
    /// </summary>
    internal void VerifyWorkspaceDirectoriesUnchanged()
    {
        GameInstallPlan plan = Plan;

        WindowsDirectoryReference? workspaceReference =
            _workspaceDirectoryReference;

        WindowsDirectoryReference? candidateReference =
            _candidateDirectoryReference;

        if (workspaceReference is null || candidateReference is null)
        {
            throw new InvalidOperationException(
                "本会话尚未捕获工作区和候选目录身份。");
        }

        // 没有基线就报错，不能在这里重新捕获来替代比较。
        workspaceReference.RequireSameDirectoryAt(
            plan.WorkspaceDirectoryPath);

        candidateReference.RequireSameDirectoryAt(
            plan.CandidateDirectoryPath);
    }

    #endregion

    #region Verification(目录与进程复核)

    internal void VerifyGameDirectoryUnchanged()
    {
        WindowsDirectoryReference? directoryReference =
            _gameDirectoryReference;

        if (_installationLock is null || directoryReference is null)
        {
            throw new ObjectDisposedException(
                nameof(GameInstallSession));
        }

        directoryReference.RequireSameDirectoryAt(
            _plan.GameDirectoryPath);
    }

    /// <summary>
    /// 移动后，确认备份位置对应的仍是原游戏目录实体。
    /// 沿用原引用，不重新建立身份基线。
    /// </summary>
    internal void VerifyGameDirectoryAtBackup()
    {
        // 同时检查会话尚未释放。
        GameInstallPlan plan = Plan;

        WindowsDirectoryReference? gameReference =
            _gameDirectoryReference;

        if (gameReference is null)
        {
            throw new InvalidOperationException(
                "缺少原游戏目录身份引用，无法核对备份。");
        }

        gameReference.RequireSameDirectoryAt(
            plan.BackupDirectoryPath);
    }

    internal void VerifyNoMatchingGameProcess()
    {
        // Plan getter 会拒绝使用已经释放的会话。
        GameProcessGate.RequireNoMatchingProcess(
            Plan.GameExecutablePath);
    }

    #endregion

    #region Candidate Placement(候选落位布局)

    /// <summary>
    /// 旧目录已备份，候选尚未移入正式位置。
    /// </summary>
    internal void VerifyBeforeCandidateMove() =>
        VerifyCandidatePlacementLayout(candidateAtGamePath: false);

    /// <summary>
    /// 候选已移入正式位置，旧目录仍在备份位置。
    /// </summary>
    internal void VerifyAfterCandidateMove() =>
        VerifyCandidatePlacementLayout(candidateAtGamePath: true);

    private void VerifyCandidatePlacementLayout(bool candidateAtGamePath)
    {
        GameInstallPlan plan = Plan;

        WindowsDirectoryReference? workspaceReference =
            _workspaceDirectoryReference;
        WindowsDirectoryReference? candidateReference =
            _candidateDirectoryReference;

        if (workspaceReference is null || candidateReference is null)
        {
            throw new InvalidOperationException(
                "缺少工作区或候选目录身份，无法检查落位布局。");
        }

        // 工作区和旧游戏的备份位置，在两种布局中都不变。
        workspaceReference.RequireSameDirectoryAt(
            plan.WorkspaceDirectoryPath);
        VerifyGameDirectoryAtBackup();

        // 只改变检查位置，始终使用准备时保存的候选身份。
        candidateReference.RequireSameDirectoryAt(
            candidateAtGamePath
                ? plan.GameDirectoryPath
                : plan.CandidateDirectoryPath);

        string vacantPath = candidateAtGamePath
            ? plan.CandidateDirectoryPath
            : plan.GameDirectoryPath;

        foreach (string path in new[]
                 {
                 vacantPath,
                 plan.FailedNewDirectoryPath
             })
        {
            try
            {
                _ = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            // 文件、目录或链接占用这里，都不能擅自覆盖或删除。
            throw new IOException(
                $"候选落位要求该位置不存在任何条目：{path}");
        }
    }

    #endregion

    #region Disposal(结束会话)

    public void Dispose()
    {
        // 先从字段取走资源，避免串行重复 Dispose 时再次释放。
        // using 按声明的相反顺序释放，因此安装锁最后释放。
        using LauncherInstallLock? installationLock =
            Interlocked.Exchange(ref _installationLock, null);

        using WindowsDirectoryReference? gameDirectoryReference =
            Interlocked.Exchange(ref _gameDirectoryReference, null);

        using WindowsDirectoryReference? workspaceDirectoryReference =
            Interlocked.Exchange(ref _workspaceDirectoryReference, null);

        using WindowsDirectoryReference? candidateDirectoryReference =
            Interlocked.Exchange(ref _candidateDirectoryReference, null);
    }

    #endregion
}