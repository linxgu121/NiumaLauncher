using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NiumaLauncher.Models;
using NiumaLauncher.Services;

namespace NiumaLauncher.ViewModel
{
    public class LauncherViewModel : INotifyPropertyChanged
    {
        #region Runtime State(运行时状态)

        private string _gameExecutablePath = string.Empty;
        private string _statusText = "请选择已经打包完成的游戏程序";
        private LauncherState _state = LauncherState.NoGameSelected;

        private readonly LauncherSettingsStore _settingsStore = new();

        // 当前启动器页面对应的游戏身份，必须与清单一致。
        private const string ExpectedGameId = "niuma-project";

        private readonly GameBuildManifestReader _buildManifestReader = new();

        private string _localVersionText = "本地版本：未选择游戏";

        private readonly GameReleaseClient _releaseClient = new();

        // 开发阶段使用本机地址，正式发布时替换为 HTTPS 地址。
        private static readonly Uri ReleaseFeedUri = new("http://127.0.0.1:8088/latest.json");

        #endregion

        #region Initialization(初始化)

        public LauncherViewModel()
        {
            RestoreGameSelection();
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

        public string LaunchButtonText => State switch
        {
            LauncherState.Ready => "开始游戏",
            LauncherState.Starting => "正在启动……",
            LauncherState.Running => "游戏运行中",
            LauncherState.GameMissing => "游戏路径失效",
            LauncherState.ProcessStatusUnknown => "进程状态待确认",
            LauncherState.CheckingUpdates => "正在检查更新",
            _ => "请选择游戏"
        };

        #endregion

        #region Game Selection(游戏选择模块)

        public void SelectGame(string executablePath)
        {
            if (!CanSelectGame)
            {
                return;
            }

            if (!IsValidGameExecutable(executablePath))
            {
                StatusText = "请选择存在的游戏 .exe 文件。";
                return;
            }

            ApplyGameExecutablePath(executablePath);

            State = LauncherState.Ready;

            try
            {
                _settingsStore.Save(new LauncherSettings
                {
                    GameExecutablePath = executablePath
                });

                StatusText = "已选择并保存游戏路径，可以启动。";
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 保存失败不影响本次使用已经选择的游戏。
                StatusText = $"本次可以启动，但路径保存失败：{exception.Message}";
            }
        }

        private void RestoreGameSelection()
        {
            try
            {
                LauncherSettings settings = _settingsStore.Load();

                // 保留失效路径，方便用户知道上次选择了哪个游戏。
                ApplyGameExecutablePath(
                    settings.GameExecutablePath ?? string.Empty);

                State = EvaluateIdleState();

                StatusText = State switch
                {
                    LauncherState.Ready =>
                        "已恢复上次选择的游戏，可以启动。",

                    LauncherState.GameMissing =>
                        $"上次的游戏路径不可用，请重新选择：{GameExecutablePath}",

                    _ => "请选择已经打包完成的游戏程序"
                };
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException)
            {
                ApplyGameExecutablePath(string.Empty);
                State = LauncherState.NoGameSelected;

                StatusText = $"读取设置失败，请重新选择游戏：{exception.Message}";
            }
        }

        private void ApplyGameExecutablePath(string executablePath)
        {
            _gameExecutablePath = executablePath;

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

        /// <summary>
        /// 仅在没有启动任务占用时，根据游戏路径判断可用状态。
        /// 不负责判断游戏进程是否正在运行。
        /// </summary>
        private LauncherState EvaluateIdleState()
        {
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
                    StatusText =
                        $"发现新版本 {release.Version}" +
                        $"（构建 {release.BuildNumber}），" +
                        $"本地构建 {local.BuildNumber}。尚未下载。";
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