using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NiumaLauncher.Models;
using NiumaLauncher.Services;

namespace NiumaLauncher.ViewModel
{
    public class LauncherViewModel : INotifyPropertyChanged
    {
        #region Runtime State(运行时状态)

        private string _gameExecutablePath = string.Empty;
        private string _statusText = "正在等待启动检查……";
        private LauncherState _state = LauncherState.CheckingInstallation;

        private readonly LauncherSettingsStore _settingsStore = new();

        // 当前启动器页面对应的游戏身份，必须与清单一致。
        private const string ExpectedGameId = "niuma-project";

        private readonly GameBuildManifestReader _buildManifestReader = new();

        private string _localVersionText = "本地版本：未选择游戏";

        private readonly GameReleaseClient _releaseClient = new();

        // 开发阶段使用本机地址，正式发布时替换为 HTTPS 地址。
        private static readonly Uri ReleaseFeedUri = new("http://127.0.0.1:8088/latest.json");

        private readonly GamePackageDownloader _packageDownloader = new();

        // 只有检查确认存在新版本后，才保存下载候选。
        private GameReleaseManifest? _availableRelease;

        // 每次下载创建独立的取消源，不复用已经取消的对象。
        private CancellationTokenSource? _downloadCancellation;

        private double _downloadProgressPercent;

        // 保存当前会话中的下载结果，不从界面提示文字反推数据。
        private DownloadedGamePackage? _downloadedPackage;

        private readonly GamePackageStager _packageStager = new();

        // 每次准备创建独立取消源。
        private CancellationTokenSource? _stagingCancellation;

        // 暂存成功只是准备完成，不代表安装完成。
        private StagedGamePackage? _stagedPackage;

        private readonly GameInstallPlanBuilder _installPlanBuilder = new();

        // 负责安装事务的发现、读取和汇总。
        private readonly GameInstallTransactionStore _transactionStore = new();

        // 同一个 ViewModel 生命周期只执行一次启动初始化。
        private Task? _initializationTask;

        // 保留完整报告，后续用于展示待处理事务详情。
        private InstallTransactionInspection? _startupInspection;

        // 独立保留诊断信息，不从随操作变化的 StatusText 反推。
        private string _startupInitializationError = string.Empty;

        // 只表示本轮启动事务检查通过，不是永久安装权限。
        private bool _startupInspectionPassed;

        // 只由 UI 线程读写，防止同一检查尚未结束时重复进入。
        private bool _isStartupInspectionRunning;

        // 游戏选择只恢复一次，重新检查不能覆盖本次会话的选择。
        private bool _hasRestoredGameSelection;

        #endregion

        #region Installation Inspection(安装状态检查)

        /// <summary>
        /// 由窗口 UI 线程调用；首次初始化任务只创建一次。
        /// 主动重新检查使用独立入口，不清空这个任务。
        /// </summary>
        public Task InitializeAsync()
        {
            return _initializationTask ??= RunStartupInspectionAsync();
        }

        /// <summary>
        /// 由 UI 线程主动请求新一轮检查。
        /// 不允许与游戏运行、下载或其它检查交叉执行。
        /// </summary>
        public Task RecheckStartupInspectionAsync()
        {
            if (!CanRecheckStartupInspection)
            {
                return Task.CompletedTask;
            }

            return RunStartupInspectionAsync();
        }

        /// <summary>
        /// 首次启动、重新检查和重新选择游戏共用的检查流程。
        /// 后台复核通过之前，不应用候选路径，也不恢复操作权限。
        /// </summary>
        private async Task RunStartupInspectionAsync(
            string? requestedExecutablePath = null)
        {
            if (_isStartupInspectionRunning)
            {
                return;
            }

            // 必须在第一次 await 前关闭操作权限。
            _isStartupInspectionRunning = true;
            _startupInspectionPassed = false;

            // 不沿用上一轮的报告或错误。
            _startupInspection = null;
            _startupInitializationError = string.Empty;

            State = LauncherState.CheckingInstallation;
            StatusText = "正在检查安装状态，请稍候……";

            try
            {
                // 用户刚选中的路径优先；普通重新检查使用当前会话的选择。
                string expectedExecutablePath =
                    requestedExecutablePath ?? GameExecutablePath;

                if (requestedExecutablePath is null &&
                    !_hasRestoredGameSelection)
                {
                    // 首次只读取路径字符串，不在这里恢复 Ready。
                    LauncherSettings settings =
                        await Task.Run(() => _settingsStore.Load());

                    expectedExecutablePath =
                        settings.GameExecutablePath ?? string.Empty;
                }

                int verifiedHistoryCount = 0;

                if (string.IsNullOrWhiteSpace(expectedExecutablePath))
                {
                    expectedExecutablePath = string.Empty;

                    // 没有独立期望路径，不能从事务记录反推出游戏位置。
                    // 此时只检查事务；读取过程同样持有安装锁。
                    _startupInspection = await Task.Run(() =>
                    {
                        using LauncherInstallLock installationLock =
                            LauncherInstallLock.Acquire();

                        return _transactionStore.InspectExisting(
                            ExpectedGameId);
                    });

                    if (_startupInspection.RequiresAttention)
                    {
                        State = LauncherState.RecoveryRequired;

                        StatusText =
                            "尚未选择游戏，但发现待处理安装信息。" +
                            "无法确定独立的期望路径，已暂停启动和更新，" +
                            "请查看安装检查详情。";

                        return;
                    }
                }
                else
                {
                    // 只有完整复核正常返回，才接收本轮结果。
                    var result =
                        await GameInstallCoordinator.VerifyCompletedHistoryAsync(
                            ExpectedGameId,
                            expectedExecutablePath,
                            CancellationToken.None);

                    _startupInspection = result.Inspection;
                    expectedExecutablePath = result.GameExecutablePath;
                    verifiedHistoryCount = result.VerifiedOperationIds.Count;
                }

                if (requestedExecutablePath is not null ||
                    !_hasRestoredGameSelection)
                {
                    // 首次恢复或主动切换：通过检查后才应用路径。
                    ApplyGameExecutablePath(expectedExecutablePath);
                    _hasRestoredGameSelection = true;
                }
                else
                {
                    // 普通重新检查不清空当前下载结果，也不恢复旧设置。
                    RefreshLocalVersion();
                }

                _startupInspectionPassed = true;

                // 事务检查通过，不等于一定选了游戏或路径仍然存在。
                State = EvaluateIdleState();

                string inspectionSummary = verifiedHistoryCount > 0
                    ? $"本轮已完成 {verifiedHistoryCount} 份历史记录及对应构建复核。"
                    : "本轮未发现待处理安装事务。";

                StatusText = State switch
                {
                    LauncherState.Ready =>
                        inspectionSummary + "可以继续操作。",

                    LauncherState.GameMissing =>
                        inspectionSummary +
                        "但游戏路径已失效，请重新选择。",

                    _ =>
                        inspectionSummary + "请选择游戏程序。"
                };

                if (requestedExecutablePath is not null)
                {
                    try
                    {
                        // 新选择通过复核并应用后，才写入持久设置。
                        _settingsStore.Save(new LauncherSettings
                        {
                            GameExecutablePath = expectedExecutablePath
                        });

                        StatusText += " 已保存游戏路径。";
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        // 保存失败与复核失败分开，不宣称新路径已经持久化。
                        StatusText +=
                            $" 但保存游戏路径失败，下次启动可能仍使用旧设置：" +
                            exception.Message;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or JsonException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or OperationCanceledException
                    or System.Security.SecurityException)
            {
                _startupInitializationError =
                    $"{exception.GetType().Name}: {exception.Message}";

                _startupInspectionPassed = false;
                State = LauncherState.RecoveryRequired;

                StatusText =
                    $"检查未完成，已暂停游戏启动和更新：{exception.Message}";

                Debug.WriteLine(exception);
            }
            catch (Exception exception)
            {
                // 未预期错误也必须先关闭权限，再交给窗口处理。
                _startupInitializationError =
                    $"{exception.GetType().Name}: {exception.Message}";

                _startupInspectionPassed = false;
                State = LauncherState.RecoveryRequired;
                StatusText = "检查发生未预期错误，已停止后续操作。";

                throw;
            }
            finally
            {
                // 只结束检查占用，不能在 finally 中强行恢复 Ready。
                _isStartupInspectionRunning = false;

                OnPropertyChanged(nameof(CanRecheckStartupInspection));
                OnPropertyChanged(nameof(CanViewStartupInspection));
            }
        }

        #endregion

        #region Bindable Properties(可绑定属性区域)

        public string GameExecutablePath => _gameExecutablePath;

        public string LocalVersionText
        {
            get => _localVersionText;

            private set
            {
                if (_localVersionText == value)
                {
                    return;
                }

                _localVersionText = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;

            private set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                OnPropertyChanged();

            }
        }

        /// <summary>
        /// 启动器当前状态。按钮权限和文字都从它推导。
        /// </summary>
        public LauncherState State
        {
            get => _state;

            private set
            {
                if (_state == value)
                {
                    return;
                }

                _state = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSelectGame));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(LaunchButtonText));
                OnPropertyChanged(nameof(CanCheckForUpdates));
                NotifyDownloadUi();
                NotifyStagingUi();
                OnPropertyChanged(nameof(IsBuildingInstallPlan));
                OnPropertyChanged(nameof(CanPreviewInstallPlan));
                OnPropertyChanged(nameof(CanViewStartupInspection));
                OnPropertyChanged(nameof(CanRecheckStartupInspection));
                OnPropertyChanged(nameof(IsInstalling));
            }
        }

        // 使用允许列表，后续新增下载、安装等状态时不会意外开放按钮。
        public bool CanSelectGame =>
            State is LauncherState.NoGameSelected
                or LauncherState.Ready
                or LauncherState.GameMissing;

        public bool CanLaunch => State == LauncherState.Ready;

        // 本阶段只检查已经选择且路径有效的本地游戏。
        public bool CanCheckForUpdates => State == LauncherState.Ready;

        // 空闲或待处理时允许查看；业务忙碌期间不开放。
        // 检查结束且存在结果时，才能打开详情。
        public bool CanViewStartupInspection =>
            !_isStartupInspectionRunning
            && (State is LauncherState.Ready
                or LauncherState.NoGameSelected
                or LauncherState.GameMissing
                or LauncherState.RecoveryRequired)
            && (_startupInspection is not null
                || !string.IsNullOrWhiteSpace(_startupInitializationError));

        // 只在空闲或待处理状态允许重新检查。
        public bool CanRecheckStartupInspection =>
            !_isStartupInspectionRunning
            && (State is LauncherState.Ready
                or LauncherState.NoGameSelected
                or LauncherState.GameMissing
                or LauncherState.RecoveryRequired);

        public string LaunchButtonText => State switch
        {
            LauncherState.CheckingInstallation => "正在检查安装状态",
            LauncherState.RecoveryRequired => "安装状态待处理",
            LauncherState.Ready => "开始游戏",
            LauncherState.Starting => "正在启动……",
            LauncherState.Running => "游戏运行中",
            LauncherState.GameMissing => "游戏路径失效",
            LauncherState.ProcessStatusUnknown => "进程状态待确认",
            LauncherState.CheckingUpdates => "正在检查更新",
            LauncherState.DownloadingPackage => "正在下载更新",
            LauncherState.VerifyingPackage => "正在校验更新",
            LauncherState.PreparingPackage => "正在准备安装",
            LauncherState.BuildingInstallPlan => "正在生成安装计划",
            LauncherState.Installing => "正在安装更新",
            _ => "请选择游戏"
        };

        public double DownloadProgressPercent
        {
            get => _downloadProgressPercent;

            private set
            {
                if (_downloadProgressPercent == value)
                {
                    return;
                }

                _downloadProgressPercent = value;
                OnPropertyChanged();
            }
        }

        public bool IsDownloading =>
            State is LauncherState.DownloadingPackage
                or LauncherState.VerifyingPackage;

        public bool IsVerifyingPackage =>
            State == LauncherState.VerifyingPackage;

        public bool CanDownloadPackage =>
            State == LauncherState.Ready &&
            _availableRelease is not null;

        // 同一个按钮：空闲时下载，工作时取消。
        // 请求取消后暂时禁用，等任务真正结束。
        public bool CanUseDownloadButton =>
            IsDownloading
                ? _downloadCancellation is { IsCancellationRequested: false }
                : CanDownloadPackage;

        public string DownloadButtonText =>
            IsDownloading
                ? _downloadCancellation?.IsCancellationRequested == true
                    ? "正在取消……"
                    : "取消下载"
                : "下载更新";

        public string DownloadedPackageText => _downloadedPackage is { } package
            ? $"缓存包：{package.Version}（构建 {package.BuildNumber}），尚未安装"
            : "缓存包：当前会话没有已完成的下载";

        public bool IsPreparingPackage => State == LauncherState.PreparingPackage;

        public bool CanPreparePackage =>
            State == LauncherState.Ready &&
            _downloadedPackage is not null &&
            _stagedPackage is null;

        public bool CanUsePrepareButton =>
            IsPreparingPackage
                ? _stagingCancellation is { IsCancellationRequested: false }
                : CanPreparePackage;

        public string PrepareButtonText =>
            IsPreparingPackage
                ? _stagingCancellation?.IsCancellationRequested == true
                    ? "正在取消……"
                    : "取消准备"
                : _stagedPackage is not null
                    ? "已准备"
                    : "准备安装";

        public string StagedPackageText =>
            _stagedPackage is { } staged
                ? $"暂存版本：{staged.SourcePackage.Version}" +
                  $"（构建 {staged.SourcePackage.BuildNumber}），尚未安装。" +
                  $"目录：{staged.DirectoryPath}"
                : "暂存版本：尚未准备";

        public bool IsBuildingInstallPlan => State == LauncherState.BuildingInstallPlan;

        public bool IsInstalling => State == LauncherState.Installing;

        // 本步要求当前会话已经准备完成，且准备的是当前下载包。
        // 这是流程一致性检查，不是对磁盘文件的再次完整性校验。
        public bool CanPreviewInstallPlan =>
            State == LauncherState.Ready &&
            _stagedPackage is { } staged &&
            ReferenceEquals(_downloadedPackage, staged.SourcePackage);

        // 没有百分比回调的阶段，统一显示忙碌动画。
        public bool IsPackageWorkIndeterminate =>
            State == LauncherState.CheckingInstallation ||
            IsInstalling ||
            IsVerifyingPackage ||
            IsPreparingPackage ||
            IsBuildingInstallPlan;

        #endregion

        #region Startup Inspection Details(启动检查详情)

        // 只限制界面展示，不影响扫描结果和启动门禁。
        private const int MaxDetailEntriesPerGroup = 50;
        private const int MaxDetailValueCharacters = 512;

        /// <summary>
        /// 根据内存中的检查结果生成展示文字，不访问磁盘。
        /// </summary>
        public string CreateStartupInspectionDetails()
        {
            if (!CanViewStartupInspection)
            {
                return string.Empty;
            }

            var text = new StringBuilder();

            text.AppendLine("这是最近一次检查的快照，不是实时磁盘状态。");
            text.AppendLine("打开或关闭本窗口不会重新检查、恢复或删除文件。");
            text.AppendLine($"当前界面状态：{State}");

            if (!string.IsNullOrWhiteSpace(_startupInitializationError))
            {
                text.AppendLine(
                    $"检查错误：{FormatDetailValue(_startupInitializationError)}");
            }

            if (_startupInspection is not { } report)
            {
                text.AppendLine();
                text.AppendLine("未取得完整事务报告，不能视为没有事务。");
                return text.ToString();
            }

            text.AppendLine($"游戏身份：{FormatDetailValue(report.GameId)}");
            text.AppendLine(
                report.Discovery.DirectoryExists
                    ? "发现时事务目录：存在"
                    : "发现时事务目录：未发现");

            // 局部函数只服务于本次文字生成，复用分组展示规则。
            void AppendGroup(
                string title,
                int total,
                IEnumerable<string> entries)
            {
                text.AppendLine();
                text.AppendLine($"【{title}】共 {total} 项");

                if (total == 0)
                {
                    text.AppendLine("无。");
                    return;
                }

                foreach (string entry in entries.Take(MaxDetailEntriesPerGroup))
                {
                    text.AppendLine(entry);
                    text.AppendLine();
                }

                if (total > MaxDetailEntriesPerGroup)
                {
                    text.AppendLine(
                        $"其余 {total - MaxDetailEntriesPerGroup} 项未展示，" +
                        "仍保留在检查报告中并参与状态判断。");
                }
            }

            // 两个分组使用同一种记录格式，避免字段展示不一致。
            string FormatTransaction(GameInstallTransaction record)
            {
                return string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        $"编号：{record.OperationId:N}",
                        $"登记阶段：{record.Phase}",
                        $"原版本：{FormatDetailValue(record.CurrentVersion)}" +
                        $"（构建 {record.CurrentBuildNumber}）",
                        $"目标版本：{FormatDetailValue(record.TargetVersion)}" +
                        $"（构建 {record.TargetBuildNumber}）",
                        $"记录中的程序：{FormatDetailValue(record.GameExecutablePath)}",
                        $"记录中的工作区：{FormatDetailValue(record.WorkspaceDirectoryPath)}"
                    });
            }

            AppendGroup(
                _startupInspectionPassed && report.CompletedRecords.Count > 0
                ? "已登记完成，本轮历史与构建复核通过"
                : "已登记完成，尚未取得整体复核通过结果",
                report.CompletedRecords.Count,
                report.CompletedRecords.Select(FormatTransaction));

            AppendGroup(
                "尚未登记完成，需核对现场",
                report.IncompleteRecords.Count,
                report.IncompleteRecords.Select(FormatTransaction));

            AppendGroup(
                "读取失败的正式记录",
                report.ReadFailures.Count,
                report.ReadFailures
                    .OrderBy(pair => pair.Key)
                    .Select(pair =>
                        $"{pair.Key:N}.json{Environment.NewLine}" +
                        $"原因：{FormatDetailValue(pair.Value)}"));

            AppendGroup(
                "临时文件",
                report.Discovery.TemporaryFileNames.Count,
                report.Discovery.TemporaryFileNames.Select(FormatDetailValue));

            AppendGroup(
                "异常条目",
                report.Discovery.UnexpectedEntryNames.Count,
                report.Discovery.UnexpectedEntryNames.Select(FormatDetailValue));

            text.AppendLine();
            text.AppendLine("路径来自记录快照，不表示对应目录当前存在或可以修改。");
            text.AppendLine("请勿通过删除记录来绕过待处理状态。");

            return text.ToString();
        }

        /// <summary>
        /// 限制单个字段的展示长度，避免长内容和控制字符挤乱报告。
        /// 不修改原始数据，也不能把返回值用于实际路径操作。
        /// </summary>
        private static string FormatDetailValue(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "（空）";
            }

            string preview = new string(
                value.Take(MaxDetailValueCharacters)
                    .Select(character => char.IsControl(character) ? ' ' : character)
                    .ToArray());

            return value.Length > MaxDetailValueCharacters
                ? preview + "…（已截断）"
                : preview;
        }

        #endregion

        #region Game Selection(游戏选择模块)

        public Task SelectGameAsync(string executablePath)
        {
            if (!CanSelectGame)
            {
                return Task.CompletedTask;
            }

            if (!IsValidGameExecutable(executablePath))
            {
                StatusText = "请选择存在的游戏 .exe 文件。";
                return Task.CompletedTask;
            }

            // 新路径先作为候选参与检查，通过后才能应用和保存。
            // 不能直接沿用上一个游戏路径的检查结果。
            return RunStartupInspectionAsync(executablePath);
        }

        private void ApplyGameExecutablePath(string executablePath)
        {
            _gameExecutablePath = executablePath;

            // 不能把上一个选择对应的更新信息带到新选择中。
            SetAvailableRelease(null);
            SetDownloadedPackage(null);
            DownloadProgressPercent = 0;

            OnPropertyChanged(nameof(GameExecutablePath));

            // 用户重新选择，或者启动时恢复路径，都需要重新读取版本。
            RefreshLocalVersion();
        }
        private static bool IsValidGameExecutable(string? executablePath)
        {
            return
                !string.IsNullOrWhiteSpace(executablePath) &&
                Path.IsPathFullyQualified(executablePath) &&
                File.Exists(executablePath) &&
                string.Equals(
                    Path.GetExtension(executablePath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase);
        }

        private LauncherState EvaluateIdleState()
        {
            // 必须先经过启动检查，再根据游戏路径判断空闲状态。
            // 防止其它流程的 finally 把待处理状态恢复成 Ready。
            if (!_startupInspectionPassed)
            {
                return State == LauncherState.CheckingInstallation
                    ? LauncherState.CheckingInstallation
                    : LauncherState.RecoveryRequired;
            }

            if (string.IsNullOrWhiteSpace(GameExecutablePath))
            {
                return LauncherState.NoGameSelected;
            }

            return IsValidGameExecutable(GameExecutablePath)
                ? LauncherState.Ready
                : LauncherState.GameMissing;
        }
        #endregion

        #region Local Version(本地版本信息)

        private void RefreshLocalVersion()
        {
            if (string.IsNullOrWhiteSpace(GameExecutablePath))
            {
                LocalVersionText = "本地版本：未选择游戏";
                return;
            }

            if (!IsValidGameExecutable(GameExecutablePath))
            {
                LocalVersionText = "本地版本：游戏路径不可用";
                return;
            }

            try
            {
                GameBuildManifest? manifest = _buildManifestReader.Load(
                    GameExecutablePath,
                    ExpectedGameId);

                if (manifest == null)
                {
                    LocalVersionText = "本地版本：未提供 game-build.json";
                    return;
                }

                LocalVersionText = $"本地版本：{manifest.Version}（构建 {manifest.BuildNumber}）";
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException)
            {
                // 必须覆盖原来的显示，不能继续展示上一个游戏的版本。
                LocalVersionText = $"本地版本：读取失败，{exception.Message}";
            }
        }

        #endregion

        #region Update Check(检查更新)

        public async Task CheckForUpdatesAsync()
        {
            if (!CanCheckForUpdates)
            {
                return;
            }

            SetAvailableRelease(null);
            DownloadProgressPercent = 0;

            State = LauncherState.CheckingUpdates;
            StatusText = "正在获取发布信息……";

            try
            {
                GameReleaseManifest release = await _releaseClient.FetchAsync(
                    ReleaseFeedUri,
                    ExpectedGameId);

                // 网络等待期间，本地文件可能被外部程序修改。
                // 收到响应后重新读取，不能从界面文字反推本地版本。
                if (!IsValidGameExecutable(GameExecutablePath))
                {
                    StatusText = "游戏路径已失效，无法比较版本。";
                    return;
                }

                GameBuildManifest? local = _buildManifestReader.Load(
                    GameExecutablePath,
                    ExpectedGameId);

                if (local == null)
                {
                    StatusText = "缺少本地版本清单，无法判断是否需要更新。";
                    return;
                }

                if (release.BuildNumber > local.BuildNumber)
                {
                    SetAvailableRelease(release);

                    StatusText =
                       $"发现新版本 {release.Version}" +
                       $"（构建 {release.BuildNumber}），" +
                       $"本地构建 {local.BuildNumber}。" +
                       $"发布包：{release.PackageSizeBytes / (1024d * 1024d):F2} MiB。" +
                       "尚未下载。";
                }
                else if (release.BuildNumber < local.BuildNumber)
                {
                    StatusText =
                        $"本地构建 {local.BuildNumber} 高于发布端构建 " +
                        $"{release.BuildNumber}，不执行自动降级。";
                }
                else if (!string.Equals(
                             release.Version,
                             local.Version,
                             StringComparison.Ordinal))
                {
                    // 同一个构建编号应当对应同一份发布内容。
                    StatusText =
                        "构建编号相同，但版本文字不一致，请检查发布信息。";
                }
                else
                {
                    StatusText =
                        $"本地构建与发布端一致：{local.Version}" +
                        $"（构建 {local.BuildNumber}）。";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "检查更新超时，请稍后重试。";
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException)
            {
                StatusText = $"检查更新失败：{exception.Message}";
            }
            finally
            {
                // 检查失败不代表不能启动本地游戏。
                // 检查期间禁止启动进程，因此这里可以重新判断空闲状态。
                State = EvaluateIdleState();

                RefreshLocalVersion();
            }
        }

        #endregion

        #region Package Download(发布包下载)

        private void NotifyDownloadUi()
        {
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(IsVerifyingPackage));
            OnPropertyChanged(nameof(CanDownloadPackage));
            OnPropertyChanged(nameof(CanUseDownloadButton));
            OnPropertyChanged(nameof(DownloadButtonText));
        }

        private void SetAvailableRelease(GameReleaseManifest? release)
        {
            _availableRelease = release;
            NotifyDownloadUi();
        }

        private void SetDownloadedPackage(DownloadedGamePackage? package)
        {
            _downloadedPackage = package;

            // 下载结果更换后，旧暂存记录不再代表当前这份包。
            //这里只清除引用，不擅自删除磁盘目录。
            SetStagedPackage(null);

            OnPropertyChanged(nameof(DownloadedPackageText));
        }

        public void CancelDownload()
        {
            if (!IsDownloading ||
                _downloadCancellation is not
                { IsCancellationRequested: false } cancellation)
            {
                return;
            }

            // 这里只请求取消，不能立即恢复 Ready。
            // 文件关闭和临时文件清理由下载器完成。
            cancellation.Cancel();

            NotifyDownloadUi();
            StatusText = "正在取消，等待下载任务结束……";
        }

        public async Task DownloadPackageAsync()
        {
            if (!CanDownloadPackage ||
                _availableRelease is not GameReleaseManifest release)
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();

            _downloadCancellation = cancellation;
            // 如果本次下载失败，不能继续把上次成功的结果当作本次结果。
            SetDownloadedPackage(null);
            DownloadProgressPercent = 0;

            // 在第一个 await 前锁定操作，防止重复下载。
            State = LauncherState.DownloadingPackage;
            StatusText = "正在准备下载……";

            try
            {
                // 检查更新以后，本地文件仍可能被其他程序修改。
                if (!IsValidGameExecutable(GameExecutablePath))
                {
                    SetAvailableRelease(null);
                    StatusText = "游戏路径已失效，请重新选择并检查更新。";
                    return;
                }

                GameBuildManifest? local = _buildManifestReader.Load(
                    GameExecutablePath,
                    ExpectedGameId);

                if (local == null ||
                    release.BuildNumber <= local.BuildNumber)
                {
                    SetAvailableRelease(null);
                    StatusText = "本地版本信息已变化，请重新检查更新。";
                    return;
                }

                // 本方法由 WPF 点击事件调用。
                // 在 UI 线程创建 Progress，使回调回到 UI 线程。
                var progress = new Progress<PackageDownloadProgress>(report =>
                {
                    // 排队中的旧进度不能覆盖任务结束后的提示，
                    // 也不能覆盖“正在取消”的提示。
                    if (!ReferenceEquals(_downloadCancellation, cancellation) ||
                        cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    DownloadProgressPercent = report.Percentage;

                    if (report.IsVerifying)
                    {
                        State = LauncherState.VerifyingPackage;
                        StatusText = "传输完成，正在校验发布包……";
                    }
                    else
                    {
                        StatusText =
                            $"正在下载：{report.Percentage:F1}% " +
                            $"（{report.DownloadedBytes / (1024d * 1024d):F2} / " +
                            $"{report.TotalBytes / (1024d * 1024d):F2} MiB）";
                    }
                });

                // ViewModel 需要回到 UI 线程，不使用 ConfigureAwait(false)。
                DownloadedGamePackage package = await _packageDownloader.DownloadAsync(
                    release,
                    ReleaseFeedUri,
                    progress,
                    cancellation.Token);

                SetDownloadedPackage(package);
                DownloadProgressPercent = 100;

                // 下载候选与下载结果是两份不同的数据。
                // 清空候选，不影响刚保存的下载结果。
                SetAvailableRelease(null);

                StatusText =
                    $"下载并校验完成，尚未安装。缓存包：{package.FilePath}";
            }
            catch (OperationCanceledException)
            {
                StatusText = cancellation.IsCancellationRequested
                    ? "下载已取消，可以重试。"
                    : "下载或校验超时，请重试。";
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                CryptographicException)
            {
                StatusText = $"下载失败：{exception.Message}";
            }
            finally
            {
                // 先让排队中的旧回调失效，再恢复界面操作。
                _downloadCancellation = null;

                State = EvaluateIdleState();
                RefreshLocalVersion();
            }
        }

        #endregion

        #region Package Staging(发布包暂存)

        private void NotifyStagingUi()
        {
            OnPropertyChanged(nameof(IsPreparingPackage));
            OnPropertyChanged(nameof(CanPreparePackage));
            OnPropertyChanged(nameof(CanUsePrepareButton));
            OnPropertyChanged(nameof(PrepareButtonText));
            OnPropertyChanged(nameof(IsPackageWorkIndeterminate));
        }

        private void SetStagedPackage(StagedGamePackage? package)
        {
            _stagedPackage = package;

            OnPropertyChanged(nameof(StagedPackageText));
            OnPropertyChanged(nameof(CanPreviewInstallPlan));
            NotifyStagingUi();
        }

        public void CancelPreparation()
        {
            if (!IsPreparingPackage ||
                _stagingCancellation is not
                { IsCancellationRequested: false } cancellation)
            {
                return;
            }

            // 只发送请求，不能立即恢复 Ready。
            // 服务还需要退出读写并尝试清理本次失败目录。
            cancellation.Cancel();

            NotifyStagingUi();
            StatusText = "正在取消准备，等待暂存任务结束……";
        }

        public async Task PreparePackageAsync()
        {
            if (!CanPreparePackage ||
                _downloadedPackage is not DownloadedGamePackage package)
            {
                return;
            }

            using var cancellation = new CancellationTokenSource();

            _stagingCancellation = cancellation;

            // 在第一次 await 前锁住其它操作，防止重复进入。
            State = LauncherState.PreparingPackage;
            StatusText = "正在重新校验缓存包并准备暂存文件……";

            try
            {
                string selectedExecutablePath = GameExecutablePath;

                // 下载之后，原游戏文件仍可能被外部程序修改。
                if (!IsValidGameExecutable(selectedExecutablePath))
                {
                    StatusText = "游戏路径已失效，请重新选择游戏。";
                    return;
                }

                GameBuildManifest? local = _buildManifestReader.Load(
                    selectedExecutablePath,
                    ExpectedGameId);

                if (local == null)
                {
                    StatusText = "缺少本地版本清单，无法确认更新目标。";
                    return;
                }

                if (package.BuildNumber <= local.BuildNumber)
                {
                    StatusText = "缓存包构建号不高于本地版本，请重新检查更新。";
                    return;
                }

                // 期望名称来自当前选择，而不是让 ZIP 自己决定启动哪个程序。
                string expectedExecutableName =
                    Path.GetFileName(selectedExecutablePath);

                StagedGamePackage staged = await _packageStager.StageAsync(
                    package,
                    ExpectedGameId,
                    expectedExecutableName,
                    cancellation.Token);

                // 服务成功返回就保留结果。
                // 不在这里再次检查取消，以免丢失已经成功的目录引用。
                SetStagedPackage(staged);

                StatusText =
                    "更新准备完成，尚未安装。原游戏文件和启动路径没有改变。";
            }
            catch (OperationCanceledException)
            {
                StatusText = cancellation.IsCancellationRequested
                    ? "准备已取消，下载缓存仍保留，可以重试。"
                    : "准备过程超时，下载缓存仍保留，可以重试。";
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                CryptographicException or
                NotSupportedException)
            {
                StatusText = $"准备失败：{exception.Message}";
            }
            finally
            {
                // 必须等服务结束后再解除忙碌状态。
                _stagingCancellation = null;

                State = EvaluateIdleState();
                RefreshLocalVersion();
            }
        }

        #endregion

        #region Install Plan Preview(安装计划预览)

        public async Task<GameInstallPlan?> CreateInstallPlanPreviewAsync()
        {
            if (!CanPreviewInstallPlan || _stagedPackage is not StagedGamePackage staged)
            {
                return null;
            }

            // 在 UI 线程捕获本次参数，后台服务不读取界面状态。
            string selectedExecutablePath = GameExecutablePath;
            DownloadedGamePackage package = staged.SourcePackage;

            // 第一次 await 之前进入忙碌状态，防止重复操作。
            State = LauncherState.BuildingInstallPlan;
            StatusText = "正在检查安装目标并生成只读计划……";

            try
            {
                // Build 包含文件读取和目录检查，放到后台执行。
                // 它只返回计划，不创建或移动游戏目录。
                GameInstallPlan plan = await Task.Run(
                    () => _installPlanBuilder.Build(
                        package,
                        selectedExecutablePath,
                        ExpectedGameId));

                StatusText =
                    "安装计划已生成，仅供预览，尚未创建工作区或安装。";

                return plan;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                ArgumentException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                StatusText = $"无法生成安装计划：{exception.Message}";
                return null;
            }
            finally
            {
                // 只读检查结束后，再恢复按钮状态。
                State = EvaluateIdleState();
                RefreshLocalVersion();
            }
        }

        #endregion

        #region Installation Execution(安装执行与结果处理)

        /// <summary>
        /// 仅供后续明确确认安装计划的流程调用。
        /// 当前保持 private，不连接按钮，也不在预览时调用。
        /// </summary>
        private async Task ExecuteConfirmedInstallAsync(GameInstallPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            if (!CanPreviewInstallPlan)
            {
                return;
            }

            // 用户确认的计划必须仍然对应当前选择和当前下载包。
            // 实际路径、版本、进程和历史检查仍由后台负责。
            if (!ReferenceEquals(_downloadedPackage, plan.SourcePackage) ||
                !string.Equals(
                    GameExecutablePath,
                    plan.GameExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "游戏选择或下载包已经变化，请重新生成安装计划。";
                return;
            }

            // 首次 await 之前关闭操作权限，不沿用之前的检查结果。
            _startupInspectionPassed = false;
            _startupInspection = null;
            _startupInitializationError = string.Empty;

            State = LauncherState.Installing;
            StatusText = "正在执行安装，请勿关闭启动器……";

            try
            {
                // 清除旧版本的内存引用，不删除磁盘文件。
                // plan 仍持有本次安装所需的来源信息。
                SetAvailableRelease(null);
                SetDownloadedPackage(null);
                DownloadProgressPercent = 0;
                LocalVersionText = "本地版本：安装状态待复核";

                var result = await GameInstallCoordinator.InstallAsync(
                    plan,
                    ExpectedGameId,
                    CancellationToken.None);

                if (result.BlockingInspection is { } blockingInspection)
                {
                    // 阻塞结果不能同时声称安装完成。
                    if (result.CompletedOperationId is not null ||
                        !string.Equals(
                            blockingInspection.GameId,
                            ExpectedGameId,
                            StringComparison.Ordinal) ||
                        !blockingInspection.RequiresAttention)
                    {
                        throw new InvalidDataException(
                            "安装协调器返回了不一致的阻塞结果。");
                    }

                    RequireInstallReview(
                        plan,
                        "发现待处理安装信息，本次未开始安装。",
                        blockingInspection);

                    return;
                }

                // 完成编号必须属于当前这一次计划。
                if (result.CompletedOperationId is not Guid completedId ||
                    completedId == Guid.Empty ||
                    completedId != plan.OperationId)
                {
                    throw new InvalidDataException(
                        "安装协调器返回的完成编号与本次计划不一致。");
                }

                StatusText = "安装阶段已结束，正在复核完成记录与当前构建……";

                // InstallAsync 已结束并释放会话，现在重新持锁复核。
                var verification =
                    await GameInstallCoordinator.VerifyCompletedHistoryAsync(
                        ExpectedGameId,
                        plan.GameExecutablePath,
                        CancellationToken.None);

                // 空历史不能算作“本次安装已经验证成功”。
                if (!verification.VerifiedOperationIds.Contains(completedId) ||
                    !string.Equals(
                        verification.GameExecutablePath,
                        GameExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "安装后复核结果不包含本次操作，或不属于当前游戏路径。");
                }

                _startupInspection = verification.Inspection;
                _startupInitializationError = string.Empty;

                RefreshLocalVersion();

                // 本次编号和物理复核都通过后，才恢复操作权限。
                _startupInspectionPassed = true;
                State = EvaluateIdleState();

                StatusText = State == LauncherState.Ready
                    ? "安装已完成，安装后复核通过，可以启动游戏。"
                    : "完成记录已复核，但游戏路径已失效，请重新检查。";
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or JsonException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException
                    or InvalidOperationException
                    or OperationCanceledException
                    or System.Security.SecurityException)
            {
                Debug.WriteLine(exception);

                RequireInstallReview(
                    plan,
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            catch (Exception exception)
            {
                // 未预期错误也必须先关闭权限，再交给调用方处理。
                RequireInstallReview(
                    plan,
                    $"{exception.GetType().Name}: {exception.Message}");

                throw;
            }

            // 不在 finally 中恢复 Ready。
            // 失败或取消都可能已经留下事务、候选或备份。
        }

        private void RequireInstallReview(
            GameInstallPlan plan,
            string reason,
            InstallTransactionInspection? inspection = null)
        {
            _startupInspectionPassed = false;
            _startupInspection = inspection;

            // 保存操作编号，方便把界面错误与持久记录对应起来。
            _startupInitializationError =
                $"安装操作 {plan.OperationId:N}：{reason}";

            LocalVersionText = "本地版本：安装状态待复核";
            State = LauncherState.RecoveryRequired;

            StatusText =
                "安装尚未获得整体确认，已暂停游戏启动和更新。" +
                "请重新检查，或查看安装检查详情。";
        }

        #endregion

        #region Game Process(游戏进程模块)

        public async Task LaunchGameAsync()
        {
            if (!CanLaunch)
            {
                return;
            }

            // Ready 是上一次判断的结果，启动前仍然需要检查文件。
            State = EvaluateIdleState();

            if (State != LauncherState.Ready)
            {
                StatusText = "游戏程序已不存在或路径无效，请重新选择。";
                return;
            }

            // 在创建进程之前锁住按钮，避免重复提交。
            State = LauncherState.Starting;
            StatusText = "正在启动游戏……";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = GameExecutablePath,

                    // 游戏中的相对路径以游戏目录为基准。
                    WorkingDirectory =
                        Path.GetDirectoryName(GameExecutablePath)!,

                    UseShellExecute = false
                };

                using Process? gameProcess = Process.Start(startInfo);

                if (gameProcess == null)
                {
                    throw new InvalidOperationException("未能创建游戏进程。");
                }

                State = LauncherState.Running;
                StatusText = "游戏进程已启动。";

                // 异步等待，不阻塞启动器窗口。
                await gameProcess.WaitForExitAsync();

                int exitCode = gameProcess.ExitCode;

                // 已经确认进程退出，再检查游戏路径是否还有效。
                State = EvaluateIdleState();

                if (State == LauncherState.GameMissing)
                {
                    StatusText = "游戏已退出，但游戏路径已失效，请重新选择。";
                }
                else
                {
                    StatusText = exitCode == 0
                        ? "游戏已退出，可以再次启动。"
                        : $"游戏进程已退出，退出码：{exitCode}";
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException)
            {
                if (State == LauncherState.Running)
                {
                    // 进程已经创建，等待异常不能当作游戏已经退出。
                    State = LauncherState.ProcessStatusUnknown;

                    StatusText =
                        $"进程监控失败：{exception.Message}。" +
                        "请确认游戏已关闭后，再重启启动器。";
                }
                else
                {
                    // 尚未进入运行状态，可以根据路径恢复重试能力。
                    State = EvaluateIdleState();

                    StatusText = $"启动游戏失败：{exception.Message}";
                }
            }
        }
        #endregion

        #region Property Notifications(属性通知模块)

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        #endregion

    }

}
