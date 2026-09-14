using System.IO;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 只读地检查安装信息，并生成计划。
/// 不创建工作区、不执行安装，也不代表取得了目录所有权。
/// </summary>
public sealed class GameInstallPlanBuilder
{
    #region Configuration(配置)

    internal const string WorkspacePrefix = ".NiumaLauncherInstall-";
    // 读取原版本清单前，先拒绝明显异常的文件大小。
    private const long MaxManifestBytes = 64 * 1024;

    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars();

    private readonly GameBuildManifestReader _manifestReader = new();

    #endregion

    #region Plan Creation(计划生成与复核)
        /// <summary>
    /// 创建一份新计划，每次使用新的操作编号。
    /// 保留原签名，现有预览调用不需要修改。
    /// </summary>
    public GameInstallPlan Build(
        DownloadedGamePackage package,
        string gameExecutablePath,
        string expectedGameId)
    {
        return BuildCore(
            package,
            gameExecutablePath,
            expectedGameId,
            Guid.NewGuid(),
            WorkspaceRequirement.MustBeAbsent);
    }

        /// <summary>
    /// 工作区创建前复核，要求工作区尚不存在。
    /// </summary>
    internal GameInstallPlan RevalidateBeforeWorkspaceCreation(
        GameInstallPlan plan,
        string expectedGameId)
    {
        return RevalidatePlan(
            plan,
            expectedGameId,
            WorkspaceRequirement.MustBeAbsent);
    }

    /// <summary>
    /// 旧目录移动前，复核原构建和已准备的工作区布局。
    /// 不替代候选内容、目录实体、事务及进程检查。
    /// </summary>
    internal GameInstallPlan RevalidatePreparedLayout(
        GameInstallPlan plan,
        string expectedGameId)
    {
        return RevalidatePlan(
            plan,
            expectedGameId,
            WorkspaceRequirement.PreparedLayout);
    }

    /// <summary>
    /// 按指定布局条件重算计划，并对照原版本和路径。
    /// 不改变原操作编号，不自动接受已经变化的构建。
    /// </summary>
    private GameInstallPlan RevalidatePlan(
        GameInstallPlan plan,
        string expectedGameId,
        WorkspaceRequirement workspaceRequirement)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.OperationId == Guid.Empty)
        {
            throw new InvalidDataException(
                "安装计划缺少有效的操作编号，请重新预览。");
        }

        // 使用原编号重算，才能检查原工作区是否已经被占用。
        // 不调用会生成新编号的公开 Build 方法。
        GameInstallPlan refreshed = BuildCore(
            plan.SourcePackage,
            plan.GameExecutablePath,
            expectedGameId,
            plan.OperationId,
            workspaceRequirement);

        // 原版本变化时停止，不静默修改原计划继续执行。
        if (plan.CurrentBuildNumber != refreshed.CurrentBuildNumber ||
            !string.Equals(
                plan.CurrentVersion,
                refreshed.CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "本地版本与预览时不同，请重新预览安装计划。");
        }

        // 对照路径来自统一规则重新计算，
        // 不能仅因为路径保存在计划对象里就直接信任。
        (string Name, string Planned, string Current)[] pathChecks =
        {
            (
                "游戏程序",
                plan.GameExecutablePath,
                refreshed.GameExecutablePath
            ),
            (
                "正式目录",
                plan.GameDirectoryPath,
                refreshed.GameDirectoryPath
            ),
            (
                "工作区",
                plan.WorkspaceDirectoryPath,
                refreshed.WorkspaceDirectoryPath
            ),
            (
                "候选目录",
                plan.CandidateDirectoryPath,
                refreshed.CandidateDirectoryPath
            ),
            (
                "备份目录",
                plan.BackupDirectoryPath,
                refreshed.BackupDirectoryPath
            ),
            (
                "失败隔离目录",
                plan.FailedNewDirectoryPath,
                refreshed.FailedNewDirectoryPath
            )
        };

        foreach (var check in pathChecks)
        {
            if (!string.Equals(
                    check.Planned,
                    check.Current,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"安装计划中的{check.Name}与重算结果不一致，" +
                    "请重新预览。");
            }
        }

        // 返回本次重新检查的快照，不修改原计划。
        return refreshed;
    }

     private GameInstallPlan BuildCore(
        DownloadedGamePackage package,
        string gameExecutablePath,
        string expectedGameId,
        Guid operationId,
        WorkspaceRequirement workspaceRequirement)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        string executablePath = NormalizeLocalPath(gameExecutablePath);

        if (!string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("游戏程序必须是 EXE 文件。");
        }

        string gameDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("无法确定游戏目录。");

        // 游戏直接位于磁盘根目录时，不允许整目录安装。
        string parentDirectory = Path.GetDirectoryName(gameDirectory)
            ?? throw new InvalidDataException(
                "不能把磁盘根目录作为游戏安装目录。");

        var drive = new DriveInfo(Path.GetPathRoot(executablePath)!);

        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidDataException(
                "第一版只支持可用的本地固定磁盘。");
        }

        EnsureDirectoryChain(gameDirectory);
        ValidateTargetDirectory(gameDirectory);

        if (RequireRegularFile(executablePath) == 0)
        {
            throw new InvalidDataException("游戏 EXE 不能为空文件。");
        }

        ValidatePackage(package, expectedGameId);

        string manifestPath = Path.Combine(
            gameDirectory,
            GameBuildManifestReader.ManifestFileName);

        long manifestLength = RequireRegularFile(manifestPath);

        if (manifestLength <= 0 || manifestLength > MaxManifestBytes)
        {
            throw new InvalidDataException(
                "本地版本清单为空或超过 64 KiB。");
        }

        // 重新读取原版本，不从界面显示文字取得版本。
        GameBuildManifest current = _manifestReader.Load(
            executablePath,
            expectedGameId)
            ?? throw new InvalidDataException("缺少本地版本清单。");

        if (package.BuildNumber <= current.BuildNumber)
        {
            throw new InvalidDataException(
                "目标构建号必须高于当前构建号。");
        }

        // 与游戏目录同父目录，规划同卷工作区。
        // 这里只生成路径，不创建或占有这个目录。
        string workspaceDirectory = NormalizeLocalPath(
            Path.Combine(
                parentDirectory,
                $"{WorkspacePrefix}{operationId:N}"));

        if (IsAtOrBelow(workspaceDirectory, gameDirectory) ||
            IsAtOrBelow(gameDirectory, workspaceDirectory))
        {
            throw new InvalidDataException(
                "工作区不能与正式游戏目录重叠。");
        }

        var result = new GameInstallPlan(
            operationId,
            package,
            current.Version,
            current.BuildNumber,
            executablePath,
            gameDirectory,
            workspaceDirectory);

        // 构造的只是内存计划，不会创建目录。
        ValidateWorkspaceLayout(result, workspaceRequirement);

        return result;
    }

    #endregion

    #region Workspace Layout(工作区布局检查)

    // 本类内部的检查条件，不是持久化事务阶段。
    private enum WorkspaceRequirement
    {
        MustBeAbsent,
        PreparedLayout
    }

    private static void ValidateWorkspaceLayout(
        GameInstallPlan plan,
        WorkspaceRequirement workspaceRequirement)
    {
        switch (workspaceRequirement)
        {
            case WorkspaceRequirement.MustBeAbsent:
            {
                // 创建前不能接管已经被占用的工作区。
                if (EntryExists(plan.WorkspaceDirectoryPath))
                {
                    throw new IOException(
                        "工作区名称已被占用，禁止创建。");
                }

                return;
            }

            case WorkspaceRequirement.PreparedLayout:
            {
                // 准备后，这两个位置必须已经是普通目录。
                EnsureDirectoryChain(plan.WorkspaceDirectoryPath);
                EnsureDirectoryChain(plan.CandidateDirectoryPath);

                // 移动目标必须不存在，不能预先创建空备份目录。
                foreach (string path in new[]
                         {
                             plan.BackupDirectoryPath,
                             plan.FailedNewDirectoryPath
                         })
                {
                    if (EntryExists(path))
                    {
                        throw new IOException(
                            $"提交前的保留位置已被占用：{path}");
                    }
                }

                return;
            }

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(workspaceRequirement),
                    workspaceRequirement,
                    "未知的工作区检查条件。");
        }
    }

    #endregion

    #region Package Validation(缓存包检查)

    private static void ValidatePackage(
        DownloadedGamePackage package,
        string expectedGameId)
    {
        if (!string.Equals(
                package.GameId,
                expectedGameId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(package.Version) ||
            package.BuildNumber <= 0 ||
            package.SizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(package.Sha256) ||
            package.Sha256.Length != 64 ||
            !package.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "缓存包身份、版本、大小或哈希格式无效。");
        }

        string cachePath = NormalizeLocalPath(package.FilePath);

        string expectedPath = NormalizeLocalPath(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "NiumaLauncher",
            "Downloads",
            $"package-{package.Sha256.ToLowerInvariant()}.zip"));

        if (!string.Equals(
                cachePath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "缓存包不在本启动器预期的下载位置。");
        }

        EnsureDirectoryChain(Path.GetDirectoryName(cachePath)!);

        if (RequireRegularFile(cachePath) != package.SizeBytes)
        {
            throw new InvalidDataException("缓存 ZIP 大小已经变化。");
        }

        // 本步不重算整个 ZIP 的哈希。
        // 生成安装候选时仍必须重新校验哈希和归档内容。
    }

    #endregion

    #region Path Rules(路径规则)

    internal static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("必须使用完整的本地路径。");
        }

        string normalized = Path.TrimEndingDirectorySeparator(
            path.Replace('/', '\\'));

        // 仅接受 C:\... 这样的普通盘符路径。
        // 不接受 UNC、设备路径或盘符相对路径。
        if (normalized.Length < 3 ||
            !char.IsAsciiLetter(normalized[0]) ||
            normalized[1] != ':' ||
            normalized[2] != '\\')
        {
            throw new InvalidDataException(
                "第一版不支持网络路径或设备路径。");
        }

        string relative = normalized[3..];

        if (relative.Length > 0)
        {
            foreach (string segment in relative.Split(
                         '\\',
                         StringSplitOptions.None))
            {
                // 保守限制短文件名形式，避免引入另一种路径表示。
                if (segment.Length == 0 ||
                    segment is "." or ".." ||
                    segment.Contains('~') ||
                    segment.EndsWith(' ') ||
                    segment.EndsWith('.') ||
                    segment.IndexOfAny(InvalidNameChars) >= 0)
                {
                    throw new InvalidDataException(
                        "路径包含不支持的名称、空段或相对目录标记。");
                }
            }
        }

        return Path.GetFullPath(normalized);
    }

    private static void ValidateTargetDirectory(string gameDirectory)
    {
        // 不能整体接管这些根目录，或包含这些根目录的上级目录。
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.CommonDesktopDirectory,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.CommonDocuments,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData
                 })
        {
            RejectProtectedOverlap(
                gameDirectory,
                Environment.GetFolderPath(folder),
                blockDescendants: false);
        }

        // 这些位置连内部子目录也不接受。
        RejectProtectedOverlap(
            gameDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            blockDescendants: true);

        RejectProtectedOverlap(
            gameDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            blockDescendants: true);

        RejectProtectedOverlap(
            gameDirectory,
            AppContext.BaseDirectory,
            blockDescendants: true);

        RejectProtectedOverlap(
            gameDirectory,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "NiumaLauncher"),
            blockDescendants: true);

        // 只检查顶层常见源码标记，不递归扫描整个项目。
        foreach (string marker in new[] { ".git", ".svn", "ProjectSettings" })
        {
            if (EntryExists(Path.Combine(gameDirectory, marker)))
            {
                throw new InvalidDataException(
                    $"目标包含源码目录标记 {marker}，不能整体替换。");
            }
        }

        bool hasProjectFile = Directory.EnumerateFiles(
                gameDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Any(path => Path.GetExtension(path).ToLowerInvariant()
                is ".csproj" or ".sln" or ".slnx");

        if (hasProjectFile)
        {
            throw new InvalidDataException(
                "目标包含工程文件，请选择独立的游戏构建目录。");
        }
    }

    private static void RejectProtectedOverlap(
        string gameDirectory,
        string protectedDirectory,
        bool blockDescendants)
    {
        // 部分特殊目录可能没有配置。
        if (string.IsNullOrWhiteSpace(protectedDirectory))
        {
            return;
        }

        string protectedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(protectedDirectory));

        bool containsProtectedDirectory =
            IsAtOrBelow(protectedPath, gameDirectory);

        bool isInsideProtectedDirectory =
            blockDescendants &&
            IsAtOrBelow(gameDirectory, protectedPath);

        if (containsProtectedDirectory || isInsideProtectedDirectory)
        {
            throw new InvalidDataException(
                $"游戏目录与受保护位置重叠：{protectedPath}");
        }
    }

    private static bool IsAtOrBelow(string path, string directory)
    {
        if (string.Equals(
                path,
                directory,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        // 带分隔符比较，避免把 GameOther 当作 Game 的子目录。
        return path.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region File System Inspection(只读文件系统检查)

    internal static void EnsureDirectoryChain(string directory)
    {
        string root = Path.GetPathRoot(directory)
            ?? throw new InvalidDataException("无法确定磁盘根目录。");

        RequirePlainDirectory(root);

        string relative = directory[root.Length..];
        string current = root;

        if (relative.Length == 0)
        {
            return;
        }

        // 从根向下检查，不能只检查最后一个目录。
        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.None))
        {
            current = Path.Combine(current, segment);
            RequirePlainDirectory(current);
        }
    }

    private static void RequirePlainDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"路径不是普通目录，或包含重解析点：{path}");
        }
    }

    private static long RequireRegularFile(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"路径不是普通文件：{path}");
        }

        return new FileInfo(path).Length;
    }

    private static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        // 无权限和其它 I/O 错误必须继续抛出。
        // 不能把“无法判断”当成“这个位置不存在”。
    }

    #endregion
}