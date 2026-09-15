using System;
using System.IO;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 核对指定目录中的构建身份。
/// 不负责选择恢复方案，也不修改目录或事务记录。
/// </summary>
internal static class GameInstallBuildVerifier
{
    #region Verification(构建身份核对)

    internal static void VerifyAtPath(
        string executablePath,
        string expectedGameId,
        string expectedVersion,
        long expectedBuildNumber,
        WindowsDirectoryReference directoryReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        ArgumentNullException.ThrowIfNull(directoryReference);

        if (expectedBuildNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedBuildNumber),
                "期望构建编号必须大于 0。");
        }

        string normalizedExecutablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(executablePath);

        if (!string.Equals(
                Path.GetExtension(normalizedExecutablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("构建核对目标必须是 EXE。");
        }

        string buildDirectory =
            Path.GetDirectoryName(normalizedExecutablePath)
            ?? throw new InvalidDataException("无法确定构建目录。");

        // 使用调用方持有的目录身份基准，不在这里重新建立基准。
        directoryReference.RequireSameDirectoryAt(buildDirectory);

        string manifestPath = Path.Combine(
            buildDirectory,
            GameBuildManifestReader.ManifestFileName);

        RequireRegularFile(normalizedExecutablePath);
        RequireRegularFile(manifestPath);

        // 保持程序文件的读取句柄，直到本次核对结束。
        using var executableStream = new FileStream(
            normalizedExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        if (executableStream.Length <= 0)
        {
            throw new InvalidDataException("构建中的游戏程序为空。");
        }

        // Reader 已负责清单大小、格式、游戏 ID 和程序名称检查。
        var manifestReader = new GameBuildManifestReader();

        GameBuildManifest manifest =
            manifestReader.Load(
                normalizedExecutablePath,
                expectedGameId)
            ?? throw new InvalidDataException("构建目录缺少版本清单。");

        // 期望值来自调用方，不能拿清单里的版本作为自己的核对标准。
        if (manifest.BuildNumber != expectedBuildNumber ||
            !string.Equals(
                manifest.Version,
                expectedVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "构建版本与本次核对的期望版本不一致。");
        }

        // 读取结束后，再检查文件类型和目录身份。
        RequireRegularFile(normalizedExecutablePath);
        RequireRegularFile(manifestPath);
        directoryReference.RequireSameDirectoryAt(buildDirectory);
    }

    #endregion

    #region Files(文件类型检查)

    private static void RequireRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"要求普通文件，不能是目录或重解析点：{path}");
        }
    }

    #endregion
}