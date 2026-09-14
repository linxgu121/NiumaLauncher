using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace NiumaLauncher.Services;

/// <summary>
/// 安装前检查是否存在与游戏主程序同名的进程。
/// 仅做一次观察，不负责启动、关闭或持续监控进程。
/// </summary>
internal static class GameProcessGate
{
    #region Verification(进程门禁)

    public static void RequireNoMatchingProcess(
        string gameExecutablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "当前游戏进程门禁仅支持 Windows。");
        }

        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                gameExecutablePath);

        if (!string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "游戏程序必须是 EXE 文件。",
                nameof(gameExecutablePath));
        }

        // GetProcessesByName 不接受完整路径，也不带 .exe。
        string processName =
            Path.GetFileNameWithoutExtension(executablePath);

        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException(
                "无法从游戏路径取得有效的进程名称。",
                nameof(gameExecutablePath));
        }

        Process[] matchingProcesses;

        // 这里只捕获查询失败，不包含下面主动抛出的拦截异常。
        try
        {
            matchingProcesses =
                Process.GetProcessesByName(processName);
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new IOException(
                "无法确认游戏进程状态，当前禁止安装。请稍后重试。",
                exception);
        }

        try
        {
            if (matchingProcesses.Length > 0)
            {
                throw new InvalidOperationException(
                    $"检测到 {matchingProcesses.Length} 个" +
                    $"名为“{processName}”的进程。" +
                    "本版按主程序名称阻止安装，请确认相关程序已关闭后重试。");
            }
        }
        finally
        {
            // 释放查询产生的包装对象，不是结束系统进程。
            foreach (Process process in matchingProcesses)
            {
                process.Dispose();
            }
        }
    }

    #endregion
}