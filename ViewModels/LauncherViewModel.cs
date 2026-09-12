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
        private bool _isRunning;

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
        /// 是否正在运行
        /// 
        /// 从提交启动请求到进程退出期间保持为 true，
        /// 防止连续点击按钮重复启动。
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                {
                    return;
                }

                _isRunning = value;
                OnPropertyChanged();

                // 这些属性依赖 IsRunning，也必须通知界面重新读取。
                OnPropertyChanged(nameof(CanSelectGame));
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(LaunchButtonText));
            }
        }

        public bool CanSelectGame => !IsRunning;

        public bool CanLaunch => !IsRunning && !string.IsNullOrWhiteSpace(GameExecutablePath);

        public string LaunchButtonText => IsRunning ? "游戏运行中" : "开始游戏";

        #endregion

        #region Game Selection(游戏选择模块)

        public void SelectGame(string executablePath)
        {
            if (IsRunning)
            {
                return;
            }

            if (!IsValidGameExecutable(executablePath))
            {
                StatusText = "请选择存在的游戏 .exe 文件。";
                return;
            }

            ApplyGameExecutablePath(executablePath);

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

                if (string.IsNullOrWhiteSpace(settings.GameExecutablePath))
                {
                    // 首次使用，没有需要恢复的游戏。
                    return;
                }

                if (!IsValidGameExecutable(settings.GameExecutablePath))
                {
                    StatusText =
                        $"上次的游戏路径不可用，请重新选择：{settings.GameExecutablePath}";

                    // 不立即覆盖配置，游戏所在的外置硬盘也可能只是没连接。
                    return;
                }

                ApplyGameExecutablePath(settings.GameExecutablePath);

                StatusText = "已恢复上次选择的游戏，可以启动。";
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException)
            {
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

        #endregion

        #region Game Process(游戏进程模块)

        public async Task LaunchGameAsync()
        {
            if (!CanLaunch)
            {
                return;
            }


            // 选择后文件仍可能被移动或删除，启动前需要再次检查。
            if (!File.Exists(GameExecutablePath))
            {
                // 清空当前选择，使“开始游戏”重新变为不可点击。
                ApplyGameExecutablePath(string.Empty);

                StatusText = "游戏程序已不存在，请重新选择。";
                return;
            }
            IsRunning = true;
            StatusText = "正在启动游戏……";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    // 使用完整路径，包含空格也不需要手工添加引号。
                    FileName = GameExecutablePath,

                    // 游戏使用相对路径访问文件时，以游戏目录为基准。
                    WorkingDirectory = Path.GetDirectoryName(GameExecutablePath)!,

                    // 直接创建程序进程，方便追踪它的退出。
                    UseShellExecute = false
                };

                using Process? gameProcess = Process.Start(startInfo);

                if (gameProcess == null)
                {
                    StatusText = "未能创建游戏进程。";
                    return;
                }

                StatusText = "游戏进程已启动。";

                // 异步等待不会阻塞窗口，期间仍能拖动和操作启动器。
                await gameProcess.WaitForExitAsync();

                StatusText = gameProcess.ExitCode == 0
                    ? "游戏已退出，可以再次启动。"
                    : $"游戏进程已退出，退出码：{gameProcess.ExitCode}";
            }
            catch (Exception exception) when (
                exception is Win32Exception or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException)
            {
                StatusText = $"启动或监控游戏失败：{exception.Message}";
            }
            finally
            {
                // 正常退出和失败都要解除本次启动占用。
                IsRunning = false;
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