using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

        #endregion

        #region Initialization(初始化)

        public LauncherViewModel()
        {
            RestoreGameSelection();
        }

        #endregion

        #region Bindable Properties(可绑定属性区域)

        public string GameExecutablePath => _gameExecutablePath;

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
            }
        }

        // 使用允许列表，后续新增下载、安装等状态时不会意外开放按钮。
        public bool CanSelectGame =>
            State is LauncherState.NoGameSelected
                or LauncherState.Ready
                or LauncherState.GameMissing;

        public bool CanLaunch => State == LauncherState.Ready;

        public string LaunchButtonText => State switch
        {
            LauncherState.Ready => "开始游戏",
            LauncherState.Starting => "正在启动……",
            LauncherState.Running => "游戏运行中",
            LauncherState.GameMissing => "游戏路径失效",
            LauncherState.ProcessStatusUnknown => "进程状态待确认",
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

            // 路径变化后，文本和启动按钮都要重新读取属性。
            OnPropertyChanged(nameof(GameExecutablePath));
            OnPropertyChanged(nameof(CanLaunch));
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