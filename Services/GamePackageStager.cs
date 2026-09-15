using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
        using var operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operation.CancelAfter(OperationTimeout);
        CancellationToken token = operation.Token;

        token.ThrowIfCancellationRequested();

        // 接管已校验文件流的生命周期。
        // 后面的 ZipArchive 继续使用同一个流，不重新打开缓存路径。
        await using FileStream archiveStream =
            await OpenVerifiedArchiveAsync(
                package,
                expectedGameId,
                expectedExecutableName,
                token).ConfigureAwait(false);

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

    #region Candidate Preparation(候选版本准备)

    /// <summary>
    /// 在调用方持有的安装会话内准备候选并推进事务阶段。
    /// 调用方必须等待本方法结束，期间不能释放会话。
    /// 不切换正式目录，不直接接入 UI。
    /// </summary>
    internal static async Task<StagedGamePackage> PrepareCandidateCoreAsync(
        GameInstallSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var operation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operation.CancelAfter(OperationTimeout);
        CancellationToken token = operation.Token;

        token.ThrowIfCancellationRequested();

        // 同一会话只允许一次准备尝试。
        GameInstallPlan plan = session.ClaimCandidatePreparation();
        string expectedGameId = session.ExpectedGameId;

        var planBuilder = new GameInstallPlanBuilder();

        GameInstallPlan checkedPlan =
            planBuilder.RevalidateBeforeWorkspaceCreation(
                plan,
                expectedGameId);

        string expectedExecutableName =
            Path.GetFileName(checkedPlan.GameExecutablePath);

        string candidateRoot = checkedPlan.CandidateDirectoryPath;

        // 校验后继续使用同一个 ZIP 文件流。
        await using FileStream archiveStream =
            await OpenVerifiedArchiveAsync(
                checkedPlan.SourcePackage,
                expectedGameId,
                expectedExecutableName,
                token).ConfigureAwait(false);

        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: true);

        token.ThrowIfCancellationRequested();

        // 预检不需要工作区，明显坏包先在这里拒绝。
        List<PlannedEntry> entries = BuildPlan(
            archive,
            candidateRoot,
            expectedExecutableName,
            token);

        token.ThrowIfCancellationRequested();

        // 哈希与条目预检可能耗时，登记前重新核对。
        session.VerifyGameDirectoryUnchanged();
        session.VerifyNoMatchingGameProcess();

        token.ThrowIfCancellationRequested();

        var transactionStore = new GameInstallTransactionStore();

        // 从登记尝试开始，失败就可能留下持久记录或临时记录。
        try
        {
            _ = transactionStore.RegisterForCandidatePreparation(
                checkedPlan,
                expectedGameId);

            // 登记成功后才能创建工作区。
            token.ThrowIfCancellationRequested();

            session.VerifyGameDirectoryUnchanged();
            session.VerifyNoMatchingGameProcess();

            checkedPlan = CreateWorkspaceForPlan(
                checkedPlan,
                expectedGameId,
                token);

            // 会话持有引用，准备方法返回后仍保留身份基线。
            // 必须在工作区创建成功后、开始解压前调用。
            session.CaptureWorkspaceDirectories();

            await ExtractAsync(
                candidateRoot,
                entries,
                token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            session.VerifyWorkspaceDirectoriesUnchanged();

            session.VerifyGameDirectoryUnchanged();
            session.VerifyNoMatchingGameProcess();

            // 沿用已有 EXE、GameId、版本与构建号检查。
            string executablePath = ValidateStagedBuild(
                candidateRoot,
                checkedPlan.SourcePackage,
                expectedGameId,
                expectedExecutableName);

            session.VerifyWorkspaceDirectoriesUnchanged();

            token.ThrowIfCancellationRequested();

            // 先构造结果，再持久发布阶段。
            var result = new StagedGamePackage(
                checkedPlan.SourcePackage,
                candidateRoot,
                executablePath);

            _ = transactionStore.MarkCandidateReady(
                checkedPlan,
                expectedGameId);

            // 阶段已经持久发布，不再因随后到来的取消请求
            // 主动把本次准备报成未完成。
            return result;
        }
        catch
        {
            // 登记、创建或推进阶段失败，都不能猜测磁盘状态。
            // 保留现场，不调用旧暂存清理器，不自动重试。
            Debug.WriteLine(
                "候选准备流程异常，可能遗留事务或工作区，未自动清理：" +
                $"OperationId={checkedPlan.OperationId:N}, " +
                $"Workspace={checkedPlan.WorkspaceDirectoryPath}");

            throw;
        }
    }

    #endregion

    #region Candidate Revalidation(提交前候选复核)

    /// <summary>
    /// 在活跃安装会话内重新检查候选构建。
    /// 不重新解压、不推进事务，也不取得新的安装权限。
    /// </summary>
    internal static void RevalidateCandidateBuild(GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var transactionStore = new GameInstallTransactionStore();

        // 先确认本次正式记录存在、匹配，并处于候选就绪阶段。
        transactionStore.RequireCandidateReady(session);

        session.VerifyGameDirectoryUnchanged();
        session.VerifyWorkspaceDirectoriesUnchanged();

        var planBuilder = new GameInstallPlanBuilder();

        // 重新检查原构建与准备后的目录布局。
        GameInstallPlan checkedPlan =
            planBuilder.RevalidatePreparedLayout(
                session.Plan,
                session.ExpectedGameId);

        // 程序名称来自计划，不由候选清单改变启动目标。
        string expectedExecutableName =
            Path.GetFileName(checkedPlan.GameExecutablePath);

        _ = ValidateStagedBuild(
            checkedPlan.CandidateDirectoryPath,
            checkedPlan.SourcePackage,
            session.ExpectedGameId,
            expectedExecutableName);

        // 内容读取结束后，再比较目录实体。
        session.VerifyWorkspaceDirectoriesUnchanged();
        session.VerifyGameDirectoryUnchanged();

        // 再次读取，拒绝前后观察到的事务身份或阶段变化。
        transactionStore.RequireCandidateReady(session);
    }

    /// <summary>
    /// 旧目录已备份时，重新检查候选构建。
    /// 不要求旧游戏仍在正式位置，也不推进事务。
    /// </summary>
    internal static void RevalidateCandidateAfterBackup(
        GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.VerifyBeforeCandidateMove();

        GameInstallPlan plan = session.Plan;

        // 目标程序名仍来自计划，不接受候选自行改变程序名称。
        string expectedExecutableName =
            Path.GetFileName(plan.GameExecutablePath);

        _ = ValidateStagedBuild(
            plan.CandidateDirectoryPath,
            plan.SourcePackage,
            session.ExpectedGameId,
            expectedExecutableName);

        // 读取清单之后，再观察一次目录身份和空缺位置。
        session.VerifyBeforeCandidateMove();
    }

    /// <summary>
    /// 候选落位后，从正式位置重新验证新构建。
    /// 只验证，不登记完成，也不启动游戏。
    /// </summary>
    internal static void RevalidateInstalledBuild(
        GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.VerifyAfterCandidateMove();

        GameInstallPlan plan = session.Plan;

        string expectedExecutableName =
            Path.GetFileName(plan.GameExecutablePath);

        // 关键：这里读取正式目录，不再读取 candidate。
        _ = ValidateStagedBuild(
            plan.GameDirectoryPath,
            plan.SourcePackage,
            session.ExpectedGameId,
            expectedExecutableName);

        // 内容读取结束后，再核对落位后的身份和布局。
        session.VerifyAfterCandidateMove();
    }

    #endregion

    #region Workspace Preparation(工作区准备)

    /// <summary>
    /// 复核计划并创建本次工作区及空候选目录。
    /// 仅供后续受控流程调用，不执行解压或正式目录替换。
    /// </summary>
    private static GameInstallPlan CreateWorkspaceForPlan(
        GameInstallPlan plan,
        string expectedGameId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var planBuilder = new GameInstallPlanBuilder();

        // 沿用原操作编号，重新检查当前版本、路径和工作区冲突。
        GameInstallPlan checkedPlan =
            planBuilder.RevalidateBeforeWorkspaceCreation(
                plan,
                expectedGameId);

        token.ThrowIfCancellationRequested();

        // 只记录本次创建调用是否成功返回，不代表长期目录所有权。
        bool workspaceCreated = false;

        try
        {
            CreateNewDirectory(checkedPlan.WorkspaceDirectoryPath);
            workspaceCreated = true;

            EnsurePlainDirectory(checkedPlan.WorkspaceDirectoryPath);

            token.ThrowIfCancellationRequested();

            // candidate 必须是新目录。
            // backup 和 failed-new 此时不创建。
            CreateNewDirectory(checkedPlan.CandidateDirectoryPath);
            EnsurePlainDirectory(checkedPlan.CandidateDirectoryPath);

            token.ThrowIfCancellationRequested();

            // 返回复核后的数据；不代表候选内容已经准备或安装成功。
            return checkedPlan;
        }
        catch
        {
            if (workspaceCreated)
            {
                // 调用方已经先登记准备意图，清理与恢复执行仍未接入
                // 保留现场，不把旧暂存清理器用于这里
                Debug.WriteLine(
                    "工作区准备未完成，可能留下部分目录，未自动清理：" +
                    checkedPlan.WorkspaceDirectoryPath);
            }

            // 保留原异常，包括取消异常，让上层正确处理。
            throw;
        }
    }

    #endregion

    #region Archive Verification(缓存归档校验)

    /// <summary>
    /// 打开缓存并核对长度、哈希。
    /// 成功后由调用方负责释放返回的文件流。
    /// </summary>
    private static async Task<FileStream> OpenVerifiedArchiveAsync(
        DownloadedGamePackage package,
        string expectedGameId,
        string expectedExecutableName,
        CancellationToken token)
    {
        string archivePath = ValidateRequest(
            package,
            expectedGameId,
            expectedExecutableName);

        token.ThrowIfCancellationRequested();

        // 这里不能使用 using：
        // 校验成功后，需要把仍打开的流交给调用方继续解压。
        var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous);

        try
        {
            if (stream.Length != package.SizeBytes)
            {
                throw new InvalidDataException("缓存 ZIP 大小已经变化。");
            }

            byte[] hash = await SHA256.HashDataAsync(
                stream,
                token).ConfigureAwait(false);

            if (!string.Equals(
                    Convert.ToHexString(hash),
                    package.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("缓存 ZIP 哈希不匹配。");
            }

            // 哈希计算已经读取过文件，交给 ZIP 解析前归零。
            stream.Position = 0;

            token.ThrowIfCancellationRequested();

            return stream;
        }
        catch
        {
            // 失败或取消时，流尚未交给调用方，由这里释放。
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
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

    #region Exclusive Directory Creation(新目录排他创建)

    /// <summary>
    /// 只创建全新目录；目标已存在时失败，不接管已有内容。
    /// 调用前仍须完成安装计划和完整路径边界检查。
    /// </summary>
    private static void CreateNewDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "当前目录创建流程仅支持 Windows。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        // 这里只接收普通的完整本地盘符路径。
        // 这些基础检查不能替代完整安装路径验证。
        if (!Path.IsPathFullyQualified(directoryPath) ||
            directoryPath.Length < 3 ||
            !char.IsAsciiLetter(directoryPath[0]) ||
            directoryPath[1] != ':' ||
            directoryPath[2] != '\\')
        {
            throw new InvalidDataException(
                "新目录必须使用完整的本地盘符路径。");
        }

        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directoryPath));

        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                "不能把磁盘根目录作为本次新目录。");

        // 父目录必须已经存在，不在这里递归创建整条路径。
        // 完整父目录链的检查由后续计划复核负责。
        EnsurePlainDirectory(parent);

        if (CreateDirectoryNative(fullPath, IntPtr.Zero))
        {
            return;
        }

        // 原生调用失败后立即读取错误码。
        int errorCode = Marshal.GetLastWin32Error();
        var nativeError = new Win32Exception(errorCode);

        // 已存在、权限不足等情况一律停止。
        // 不删除冲突目录，也不回退到使用已有目录。
        throw new IOException(
            $"无法创建全新目录：{fullPath}。" +
            $"系统错误 {errorCode}：{nativeError.Message}",
            nativeError);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(
        string lpPathName,
        IntPtr lpSecurityAttributes);

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