using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public sealed class GamePackageStager
{
    #region Configuration(暂存限制)

    private const int BufferSize = 128 * 1024;
    private const int MaxEntries = 10_000;
    private const long MaxManifestBytes = 64 * 1024;

    private const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxFileBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;

    private const string StagePrefix = "NiumaLauncherStage-";

    private static readonly TimeSpan OperationTimeout =
        TimeSpan.FromMinutes(30);

    #endregion

    #region Public API(暂存入口)

    public Task<StagedGamePackage> StageAsync(
        DownloadedGamePackage package,
        string expectedGameId,
        string expectedExecutableName,
        CancellationToken cancellationToken = default)
    {
        // ZIP 目录解析及部分解压工作是同步/CPU 工作，
        // 整体移出 UI 线程；内部文件操作仍使用异步接口。
        return Task.Run(
            () => StageCoreAsync(
                package,
                expectedGameId,
                expectedExecutableName,
                cancellationToken),
            cancellationToken);
    }

    #endregion

    #region Staging(暂存主流程)

    private static async Task<StagedGamePackage> StageCoreAsync(
        DownloadedGamePackage package,
        string expectedGameId,
        string expectedExecutableName,
        CancellationToken cancellationToken)
    {
        string archivePath = ValidateRequest(
            package,
            expectedGameId,
            expectedExecutableName);

        using var operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operation.CancelAfter(OperationTimeout);
        CancellationToken token = operation.Token;

        token.ThrowIfCancellationRequested();

        // 不开放写入和删除共享。
        // 校验和解压一直使用这个句柄，不重新按路径打开。
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        if (archiveStream.Length != package.SizeBytes)
        {
            throw new InvalidDataException("缓存 ZIP 大小已经变化。");
        }

        byte[] hash = await SHA256.HashDataAsync(
            archiveStream,
            token).ConfigureAwait(false);

        if (!string.Equals(
                Convert.ToHexString(hash),
                package.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("缓存 ZIP 哈希不匹配。");
        }

        archiveStream.Position = 0;

        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: true);

        token.ThrowIfCancellationRequested();

        // 由系统创建本次唯一的空目录，不复用旧解压目录。
        DirectoryInfo ownedDirectory =
            Directory.CreateTempSubdirectory(StagePrefix);

        string root = Path.GetFullPath(ownedDirectory.FullName);
        string parent = Path.GetDirectoryName(root)!;
        bool keepDirectory = false;

        try
        {
            EnsurePlainDirectory(root);

            List<PlannedEntry> plan = BuildPlan(
                archive,
                root,
                expectedExecutableName,
                token);

            await ExtractAsync(root, plan, token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            string executablePath = ValidateStagedBuild(
                root,
                package,
                expectedGameId,
                expectedExecutableName);

            token.ThrowIfCancellationRequested();

            var result = new StagedGamePackage(
                package,
                root,
                executablePath);

            keepDirectory = true;
            return result;
        }
        finally
        {
            // 成功时保留目录交给下一阶段，失败时仅清理本次目录。
            if (!keepDirectory)
            {
                TryCleanup(root, parent);
            }
        }
    }

    #endregion

    #region Request Validation(请求检查)

    private static string ValidateRequest(
        DownloadedGamePackage package,
        string expectedGameId,
        string expectedExecutableName)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (string.IsNullOrWhiteSpace(expectedGameId) ||
            !string.Equals(
                package.GameId,
                expectedGameId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(package.Version) ||
            package.BuildNumber <= 0)
        {
            throw new InvalidDataException(
                "缓存包的游戏身份或版本信息无效。");
        }

        if (string.IsNullOrWhiteSpace(expectedExecutableName) ||
            !string.Equals(
                Path.GetFileName(expectedExecutableName),
                expectedExecutableName,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetExtension(expectedExecutableName),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "期望的游戏程序必须是单独的 EXE 文件名。");
        }

        if (package.SizeBytes <= 0 ||
            package.SizeBytes > MaxArchiveBytes ||
            string.IsNullOrWhiteSpace(package.Sha256) ||
            package.Sha256.Length != 64 ||
            !package.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "缓存包大小超限或哈希格式无效。");
        }

        if (string.IsNullOrWhiteSpace(package.FilePath) ||
            !Path.IsPathFullyQualified(package.FilePath))
        {
            throw new InvalidDataException("缓存路径必须是完整路径。");
        }

        string expectedPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "NiumaLauncher",
            "Downloads",
            $"package-{package.Sha256.ToLowerInvariant()}.zip"));

        if (!string.Equals(
                Path.GetFullPath(package.FilePath),
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "文件不属于本启动器预期的下载缓存位置。");
        }

        return expectedPath;
    }

    #endregion

    #region Archive Plan(条目预检)

    private sealed record PlannedEntry(
        ZipArchiveEntry Entry,
        string Destination,
        bool IsDirectory);

    private static List<PlannedEntry> BuildPlan(
        ZipArchive archive,
        string root,
        string expectedExecutableName,
        CancellationToken token)
    {
        if (archive.Entries.Count == 0 ||
            archive.Entries.Count > MaxEntries)
        {
            throw new InvalidDataException("ZIP 条目数量无效或超限。");
        }

        var plan = new List<PlannedEntry>();
        var paths = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        string manifestPath = Path.Combine(
            root,
            GameBuildManifestReader.ManifestFileName);

        string executablePath = ZipEntryPathResolver.Resolve(
            root,
            expectedExecutableName,
            out _);

        long totalLength = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            string destination = ZipEntryPathResolver.Resolve(
                root,
                entry.FullName,
                out bool isDirectory);

            ValidateEntryType(entry, isDirectory);

            if (!paths.TryAdd(destination, isDirectory))
            {
                throw new InvalidDataException("ZIP 存在重复目标路径。");
            }

            if (entry.Length < 0 ||
                entry.Length > MaxFileBytes ||
                entry.Length > MaxExpandedBytes - totalLength)
            {
                throw new InvalidDataException("ZIP 展开容量超过限制。");
            }

            if (isDirectory && entry.Length != 0)
            {
                throw new InvalidDataException("目录条目不能携带文件内容。");
            }

            if (string.Equals(
                    destination,
                    manifestPath,
                    StringComparison.OrdinalIgnoreCase) &&
                entry.Length > MaxManifestBytes)
            {
                throw new InvalidDataException("包内版本清单过大。");
            }

            totalLength += entry.Length;
            plan.Add(new PlannedEntry(entry, destination, isDirectory));
        }

        // 文件不能同时成为另一个条目的父目录。
        // 例如：既有 data 文件，又有 data/config.json。
        foreach (PlannedEntry item in plan)
        {
            token.ThrowIfCancellationRequested();

            string? parent = Path.GetDirectoryName(item.Destination);

            while (parent != null &&
                   !string.Equals(
                       parent,
                       root,
                       StringComparison.OrdinalIgnoreCase))
            {
                if (paths.TryGetValue(parent, out bool isDirectory) &&
                    !isDirectory)
                {
                    throw new InvalidDataException(
                        "ZIP 存在文件与父目录冲突。");
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        if (!paths.TryGetValue(manifestPath, out bool manifestIsDirectory) ||
            manifestIsDirectory ||
            !paths.TryGetValue(executablePath, out bool exeIsDirectory) ||
            exeIsDirectory)
        {
            throw new InvalidDataException(
                "ZIP 根目录必须包含 game-build.json 和预期的游戏 EXE。");
        }

        return plan;
    }

    private static void ValidateEntryType(
        ZipArchiveEntry entry,
        bool isDirectory)
    {
        int attributes = entry.ExternalAttributes;

        if ((attributes & (int)FileAttributes.ReparsePoint) != 0 ||
            ((attributes & (int)FileAttributes.Directory) != 0 &&
             !isDirectory))
        {
            throw new InvalidDataException(
                "ZIP 包含重解析点或不一致的目录属性。");
        }

        // 常见 Unix 类型位：普通文件 0x8000，目录 0x4000。
        // 0 表示没有提供这些类型位。
        int unixType = (attributes >> 16) & 0xF000;
        int expectedType = isDirectory ? 0x4000 : 0x8000;

        if (unixType != 0 && unixType != expectedType)
        {
            throw new InvalidDataException(
                "ZIP 包含链接或其他不支持的特殊类型。");
        }
    }

    #endregion

    #region Extraction(限量解压)

    private static async Task ExtractAsync(
        string root,
        List<PlannedEntry> plan,
        CancellationToken token)
    {
        byte[] buffer = new byte[BufferSize];
        long totalWritten = 0;

        foreach (PlannedEntry item in plan)
        {
            token.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                EnsureSafeDirectory(root, item.Destination);
                continue;
            }

            EnsureSafeDirectory(
                root,
                Path.GetDirectoryName(item.Destination)!);

            await using Stream source = item.Entry.Open();

            // 目标存在就失败，不覆盖已有文件或链接。
            await using var destination = new FileStream(
                item.Destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous);

            long written = 0;

            while (true)
            {
                int read = await source.ReadAsync(
                    buffer.AsMemory(),
                    token).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                // 在写入前检查，不能仅相信归档声明的长度。
                if (read > item.Entry.Length - written ||
                    read > MaxExpandedBytes - totalWritten)
                {
                    throw new InvalidDataException(
                        "实际展开数据超过声明大小或总量限制。");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    token).ConfigureAwait(false);

                written += read;
                totalWritten += read;
            }

            if (written != item.Entry.Length)
            {
                throw new InvalidDataException("ZIP 条目内容不完整。");
            }

            await destination.FlushAsync(token).ConfigureAwait(false);
        }
    }

    #endregion

    #region Directory Safety(目录检查)

    private static void EnsureSafeDirectory(
        string root,
        string directory)
    {
        EnsurePlainDirectory(root);

        if (string.Equals(
                root,
                directory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!directory.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("目录超出本次暂存范围。");
        }

        string relative = Path.GetRelativePath(root, directory);
        string current = root;

        // 逐层创建、检查，不一次性创建整串目录。
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            Directory.CreateDirectory(current);
            EnsurePlainDirectory(current);
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "暂存路径不是普通目录，或包含重解析点。");
        }
    }

    #endregion

    #region Build Validation(包内版本检查)

    private static string ValidateStagedBuild(
        string root,
        DownloadedGamePackage package,
        string expectedGameId,
        string expectedExecutableName)
    {
        EnsurePlainDirectory(root);

        string executablePath = ZipEntryPathResolver.Resolve(
            root,
            expectedExecutableName,
            out _);

        string manifestPath = Path.Combine(
            root,
            GameBuildManifestReader.ManifestFileName);

        foreach (string path in new[] { executablePath, manifestPath })
        {
            FileAttributes attributes = File.GetAttributes(path);

            if ((attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "游戏程序或版本清单不是普通文件。");
            }
        }

        if (new FileInfo(executablePath).Length == 0 ||
            new FileInfo(manifestPath).Length > MaxManifestBytes)
        {
            throw new InvalidDataException(
                "游戏程序为空，或版本清单超过大小限制。");
        }

        var reader = new GameBuildManifestReader();

        GameBuildManifest manifest =
            reader.Load(executablePath, expectedGameId)
            ?? throw new InvalidDataException("缺少包内版本清单。");

        if (manifest.BuildNumber != package.BuildNumber ||
            !string.Equals(
                manifest.Version,
                package.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "包内版本与本次下载的发布信息不一致。");
        }

        return executablePath;
    }

    #endregion

    #region Cleanup(本次暂存清理)

    private static void TryCleanup(
        string ownedRoot,
        string expectedParent)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(ownedRoot));

            string parent = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(expectedParent));

            // 只允许清理由本次 CreateTempSubdirectory 返回的目录。
            // 此方法不能被用来删除任意游戏安装路径。
            if (!string.Equals(
                    Path.GetDirectoryName(root),
                    parent,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(
                    StagePrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("拒绝清理非本次暂存目录。");
            }

            if (!Directory.Exists(root))
            {
                return;
            }

            EnsurePlainDirectory(root);
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            // 清理失败不能掩盖原本的解压或校验异常。
            Debug.WriteLine(
                $"暂存目录清理失败：{ownedRoot}，{exception.Message}");
        }
    }

    #endregion
}