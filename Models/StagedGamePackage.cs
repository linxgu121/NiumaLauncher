namespace NiumaLauncher.Models;

/// <summary>
/// 已完成解压和包内版本核对的暂存结果。
/// 暂存成功不等于已经安装。
/// </summary>
public sealed class StagedGamePackage
{
    public DownloadedGamePackage SourcePackage { get; }

    public string DirectoryPath { get; }

    public string ExecutablePath { get; }

    internal StagedGamePackage(
        DownloadedGamePackage sourcePackage,
        string directoryPath,
        string executablePath)
    {
        SourcePackage = sourcePackage;
        DirectoryPath = directoryPath;
        ExecutablePath = executablePath;
    }
}