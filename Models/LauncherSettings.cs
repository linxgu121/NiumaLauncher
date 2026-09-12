namespace NiumaLauncher.Models;

public class LauncherSettings
{
    // 只保存用户选择，不保存 IsRunning 等临时运行状态。
    public string GameExecutablePath { get; set; } = string.Empty;
}