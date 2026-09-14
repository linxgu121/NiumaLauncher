using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

/// <summary>
/// 安装事务记录服务
/// 当前支持记录创建、首次登记和受限读取，恢复流程后续接入
/// </summary>
public sealed class GameInstallTransactionStore
{
    #region Configuration(配置)

    // 本服务当前认识的事务文件格式版本。
    private const int SupportedSchemaVersion = 1;

    // 限制事务 JSON 的读写字节数，不是安装包大小
    private const int MaxTransactionBytes = 64 * 1024;

    // 限制一次发现处理的条目数量，包含文件、目录和异常项。
    private const int MaxDiscoveryEntries = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 保留缩进，便于排查记录。
        WriteIndented = true,

        // OperationId 等属性名写成 operationId。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // 同一个对象不允许重复字段，例如出现两个 phase。
        AllowDuplicateProperties = false,

        // 遇到当前模型不认识的字段就报错，不静默忽略。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // 当前记录是扁平对象，不需要很深的嵌套。
        MaxDepth = 8,

        Converters =
        {
            // 阶段保存为成员名称，而不是数字。
            new JsonStringEnumConverter<InstallTransactionPhase>(
                namingPolicy: null,
                allowIntegerValues: false)
        }
    };

    #endregion

    #region Record Creation(创建记录)

    /// <summary>
    /// 在工作区创建前，根据重新复核的计划生成事务数据。
    /// 返回的是内存对象，不表示已经持久登记。
    /// </summary>
    public GameInstallTransaction CreateForCandidatePreparation(
        GameInstallPlan plan,
        string expectedGameId)
    {
        var planBuilder = new GameInstallPlanBuilder();

        GameInstallPlan checkedPlan =
            planBuilder.RevalidateBeforeWorkspaceCreation(
                plan,
                expectedGameId);

        DownloadedGamePackage package = checkedPlan.SourcePackage;

        var record = new GameInstallTransaction
        {
            SchemaVersion = SupportedSchemaVersion,

            // 沿用同一操作编号，不重新生成。
            OperationId = checkedPlan.OperationId,
            GameId = package.GameId,

            // 初始阶段由服务确定，不能直接跳到候选就绪。
            Phase = InstallTransactionPhase.PreparingCandidate,

            CurrentVersion = checkedPlan.CurrentVersion,
            CurrentBuildNumber = checkedPlan.CurrentBuildNumber,

            TargetVersion = package.Version,
            TargetBuildNumber = package.BuildNumber,
            PackageSizeBytes = package.SizeBytes,
            PackageSha256 = package.Sha256,

            GameExecutablePath = checkedPlan.GameExecutablePath,
            WorkspaceDirectoryPath = checkedPlan.WorkspaceDirectoryPath
        };

        ValidateRecordFields(record, expectedGameId);

        return record;
    }

    #endregion

    #region Initial Registration(首次登记)

    /// <summary>
    /// 复核计划，并首次持久登记候选准备意图。
    /// 正式记录已经存在时失败，不覆盖旧事务。
    /// 暂不直接接入 UI 或候选准备流程。
    /// </summary>
    internal GameInstallTransaction RegisterForCandidatePreparation(
        GameInstallPlan plan,
        string expectedGameId)
    {
        // 在方法内部创建独立记录。
        // 写入完成前，不把这个可变对象交给调用方。
        GameInstallTransaction record = CreateForCandidatePreparation(
            plan,
            expectedGameId);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            record,
            JsonOptions);

        // 内容检查通过后，才开始准备存储目录。
        if (jsonBytes.Length == 0 ||
            jsonBytes.Length > MaxTransactionBytes)
        {
            throw new InvalidDataException(
                "事务记录为空或超过 64 KiB。");
        }

        string directory = PrepareTransactionDirectory();

        string recordPath = Path.Combine(
            directory,
            $"{record.OperationId:N}.json");

        // 第二个 GUID 只用于临时文件防重名，
        // 不改变安装事务的 OperationId。
        string temporaryPath = Path.Combine(
            directory,
            $"{record.OperationId:N}.{Guid.NewGuid():N}.tmp");

        // 临时文件必须全新，不能覆盖或截断已有文件。
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(jsonBytes, 0, jsonBytes.Length);

            // 完整写入后，请求刷新文件缓冲。
            stream.Flush(flushToDisk: true);
        }

        // 流已关闭，再检查目录链并发布正式文件。
        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        // 首次登记不能覆盖已有事务。
        // 失败时异常向上抛出，可能留下临时文件，不自动删除。
        File.Move(
            temporaryPath,
            recordPath,
            overwrite: false);

        // 只有正式文件发布成功，才返回本次登记结果。
        return record;
    }

    #endregion

    #region Transaction Verification(提交前事务核对)

    /// <summary>
    /// 确认正式记录匹配当前会话，并处于候选就绪阶段。
    /// 只读检查，不创建、修复或推进事务。
    /// </summary>
    internal void RequireCandidateReady(GameInstallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Plan getter 同时拒绝使用已经释放的会话。
        GameInstallPlan plan = session.Plan;

        // 按当前操作编号读取正式记录，不使用历史检查结果。
        GameInstallTransaction record = LoadExisting(
            plan.OperationId,
            session.ExpectedGameId);

        // 核对游戏、版本、构建、包信息和目标路径。
        ValidateRecordMatchesPlan(record, plan);

        if (record.Phase != InstallTransactionPhase.CandidateReady)
        {
            throw new InvalidOperationException(
                "提交前要求事务处于 CandidateReady，" +
                $"当前阶段：{record.Phase}。");
        }
    }

    #endregion

    #region Phase Transitions(事务阶段推进)

    /// <summary>
    /// 候选准备中 → 候选就绪。
    /// 调用方必须持有安装锁，并已完成候选验证。
    /// </summary>
    internal GameInstallTransaction MarkCandidateReady(
        GameInstallPlan plan,
        string expectedGameId)
    {
        return AdvancePhase(
            plan,
            expectedGameId,
            InstallTransactionPhase.PreparingCandidate,
            InstallTransactionPhase.CandidateReady);
    }

    /// <summary>
    /// 候选就绪 → 已登记旧目录备份意图。
    /// 调用方必须持有安装锁，并已完成提交前复核。
    /// 本方法不移动目录，也不表示备份已经完成。
    /// </summary>
    internal GameInstallTransaction MarkBackupMovePending(
        GameInstallPlan plan,
        string expectedGameId)
    {
        return AdvancePhase(
            plan,
            expectedGameId,
            InstallTransactionPhase.CandidateReady,
            InstallTransactionPhase.BackupMovePending);
    }

    /// <summary>
    /// 备份移动意图 → 候选落位意图。
    /// 调用方必须持有同一个安装会话，
    /// 已完成旧目录备份及身份核对，并确认候选和目标布局有效。
    /// 本方法只更新记录，不移动候选目录。
    /// </summary>
    internal GameInstallTransaction MarkCandidateMovePending(
        GameInstallPlan plan,
        string expectedGameId)
    {
        return AdvancePhase(
            plan,
            expectedGameId,
            InstallTransactionPhase.BackupMovePending,
            InstallTransactionPhase.CandidateMovePending);
    }

    /// <summary>
    /// 共用的持久更新实现。
    /// 保持 private，不向其他类开放任意阶段跳转。
    /// </summary>
    private GameInstallTransaction AdvancePhase(
        GameInstallPlan plan,
        string expectedGameId,
        InstallTransactionPhase expectedPhase,
        InstallTransactionPhase nextPhase)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Guid operationId = plan.OperationId;

        // 从磁盘读取独立记录，不使用外部传来的可变 DTO。
        GameInstallTransaction record = LoadExisting(
            operationId,
            expectedGameId);

        ValidateRecordMatchesPlan(record, plan);

        // 阶段合法不代表转换合法，必须符合本入口的前置阶段。
        if (record.Phase != expectedPhase)
        {
            throw new InvalidOperationException(
                $"事务阶段不允许推进：期望 {expectedPhase}，" +
                $"实际 {record.Phase}，目标 {nextPhase}。");
        }

        // 此时只修改本地对象，尚未持久保存。
        record.Phase = nextPhase;

        ValidateRecordFields(record, expectedGameId);

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            record,
            JsonOptions);

        if (jsonBytes.Length == 0 ||
            jsonBytes.Length > MaxTransactionBytes)
        {
            throw new InvalidDataException(
                "事务记录为空或超过 64 KiB。");
        }

        // 更新已有记录；目录缺失必须失败，不能重新创建。
        string directory = GetTransactionDirectoryPath();
        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        string recordPath = Path.Combine(
            directory,
            $"{operationId:N}.json");

        // 沿用发现层已经认识的临时文件命名规则。
        string temporaryPath = Path.Combine(
            directory,
            $"{operationId:N}.{Guid.NewGuid():N}.tmp");

        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(jsonBytes, 0, jsonBytes.Length);
            stream.Flush(flushToDisk: true);
        }

        // 临时流关闭后，再核对发布时使用的路径。
        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        foreach (string path in new[] { recordPath, temporaryPath })
        {
            FileAttributes attributes = File.GetAttributes(path);

            if ((attributes &
                 (FileAttributes.Directory |
                  FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException(
                    "事务更新要求正式记录和临时记录均为普通文件。");
            }
        }

        // 目标必须已存在；失败不回退到删除、复制或重新登记。
        File.Replace(
            temporaryPath,
            recordPath,
            destinationBackupFileName: null,
            ignoreMetadataErrors: false);

        // 只有正式记录替换成功，才返回推进结果。
        return record;
    }

    #endregion

    #region Record Discovery(发现记录)

    /// <summary>
    /// 只检查固定事务目录的当前层条目。
    /// 不创建目录、不读取 JSON、不执行恢复或清理。
    /// </summary>
    internal InstallTransactionDiscovery Discover()
    {
        string directory = GetTransactionDirectoryPath();
        string launcherDirectory = Path.GetDirectoryName(directory)!;
        string localAppData = Path.GetDirectoryName(launcherDirectory)!;

        // 系统提供的基础目录必须正常，不能把基础路径异常当作首次启动。
        GameInstallPlanBuilder.EnsureDirectoryChain(localAppData);

        // 应用子目录可能尚未创建，发现操作不能主动创建它。
        if (!TryInspectPlainDirectory(launcherDirectory) ||
            !TryInspectPlainDirectory(directory))
        {
            return new InstallTransactionDiscovery(
                false,
                Array.Empty<Guid>(),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        var recordIds = new HashSet<Guid>();
        var temporaryFileNames = new List<string>();
        var unexpectedEntryNames = new List<string>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        int entryCount = 0;

        foreach (string entryPath in Directory.EnumerateFileSystemEntries(
                     directory, "*", options))
        {
            if (++entryCount > MaxDiscoveryEntries)
            {
                throw new InvalidDataException(
                    $"事务目录条目超过限制：{MaxDiscoveryEntries}。");
            }

            string fileName = Path.GetFileName(entryPath);

            // 中途消失或无法访问时直接报错，不返回部分成功结果。
            FileAttributes attributes = File.GetAttributes(entryPath);

            if ((attributes &
                 (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                // 不进入子目录，也不跟随链接读取。
                unexpectedEntryNames.Add(fileName);
                continue;
            }

            if (TryParseRecordFileName(fileName, out Guid operationId))
            {
                if (!recordIds.Add(operationId))
                {
                    throw new IOException(
                        $"发现重复的事务编号：{operationId:N}。");
                }
            }
            else if (IsTemporaryFileName(fileName))
            {
                temporaryFileNames.Add(fileName);
            }
            else
            {
                unexpectedEntryNames.Add(fileName);
            }
        }

        // 复核目录链，但这不等于持有目录锁或获得原子快照。
        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        // 排序只为了稳定展示，不代表事务发生时间或恢复顺序。
        return new InstallTransactionDiscovery(
            true,
            recordIds.OrderBy(id => id),
            temporaryFileNames.OrderBy(
                name => name, StringComparer.Ordinal),
            unexpectedEntryNames.OrderBy(
                name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// 允许固定应用子目录不存在；其他异常不能吞掉。
    /// </summary>
    private static bool TryInspectPlainDirectory(string directory)
    {
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(directory);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"事务路径必须是普通目录，不能是文件或重解析点：{directory}");
        }

        return true;
    }

    /// <summary>
    /// 正式文件必须与写入端的 {OperationId:N}.json 命名完全一致。
    /// </summary>
    private static bool TryParseRecordFileName(
        string fileName,
        out Guid operationId)
    {
        operationId = Guid.Empty;

        return Guid.TryParseExact(
                   Path.GetFileNameWithoutExtension(fileName),
                   "N",
                   out operationId)
               && operationId != Guid.Empty
               && string.Equals(
                   fileName,
                   $"{operationId:N}.json",
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// 临时文件格式：{OperationId:N}.{随机Guid:N}.tmp。
    /// </summary>
    private static bool IsTemporaryFileName(string fileName)
    {
        string[] parts = fileName.Split('.');

        if (parts.Length != 3 ||
            !Guid.TryParseExact(parts[0], "N", out Guid operationId) ||
            !Guid.TryParseExact(parts[1], "N", out Guid nonce) ||
            operationId == Guid.Empty ||
            nonce == Guid.Empty)
        {
            return false;
        }

        // 重新格式化后比较，要求与写入端完全相同的小写命名。
        return string.Equals(
            fileName,
            $"{operationId:N}.{nonce:N}.tmp",
            StringComparison.Ordinal);
    }

    #endregion

    #region Startup Inspection(启动检查)

    /// <summary>
    /// 发现事务，并逐份读取正式记录。
    /// 单份记录失败会保留原因；发现阶段失败则整体抛出。
    /// </summary>
    internal InstallTransactionInspection InspectExisting(
        string expectedGameId)
    {
        // 调用参数错误不能被解释成每份事务文件都损坏。
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        // 必须放在逐文件的 try/catch 外面。
        // 连目录都无法检查时，不能返回一份空报告。
        InstallTransactionDiscovery discovery = Discover();

        var loadedRecords = new List<GameInstallTransaction>();
        var readFailures = new Dictionary<Guid, string>();

        foreach (Guid operationId in discovery.RecordIds)
        {
            GameInstallTransaction record;

            try
            {
                // 复用已有的受限读取与身份检查，不重新实现校验。
                record = LoadExisting(operationId, expectedGameId);
            }
            catch (Exception ex) when (
                ex is IOException
                    or InvalidDataException
                    or JsonException
                    or UnauthorizedAccessException
                    or SecurityException
                    or ArgumentException)
            {
                // 记录失败，不伪造默认事务，也不删除损坏文件。
                readFailures.Add(
                    operationId,
                    $"{ex.GetType().Name}: {ex.Message}");

                // 继续检查其他已发现的记录，收集完整诊断信息。
                continue;
            }

            loadedRecords.Add(record);
        }

        return new InstallTransactionInspection(
            expectedGameId,
            discovery,
            loadedRecords,
            readFailures);
    }

    #endregion

    #region Record Loading(读取记录)

    /// <summary>
    /// 读取指定编号的正式事务记录。
    /// 记录应当存在；缺失或损坏时抛出异常，不返回默认记录。
    /// </summary>
    internal GameInstallTransaction LoadExisting(
        Guid operationId,
        string expectedGameId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "必须提供有效的事务操作编号。",
                nameof(operationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        // 读取不能调用会创建目录的 PrepareTransactionDirectory。
        string directory = GetTransactionDirectoryPath();
        GameInstallPlanBuilder.EnsureDirectoryChain(directory);

        // 路径由请求编号生成，不接受外部传入任意文件路径。
        string recordPath = Path.Combine(
            directory,
            $"{operationId:N}.json");

        FileAttributes attributes = File.GetAttributes(recordPath);

        if ((attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "事务记录不是普通文件，或属于重解析点。");
        }

        using var stream = new FileStream(
            recordPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        long length = stream.Length;

        // 先限制长度，再分配内存，不能按任意文件大小分配数组。
        if (length <= 0 || length > MaxTransactionBytes)
        {
            throw new InvalidDataException(
                "事务记录为空或超过 64 KiB。");
        }

        byte[] jsonBytes = new byte[(int)length];

        // 必须读满；文件提前结束时会抛出异常。
        stream.ReadExactly(jsonBytes, 0, jsonBytes.Length);

        // 不允许忽略额外尾部，也检查读取期间长度是否变化。
        if (stream.ReadByte() != -1 || stream.Length != length)
        {
            throw new InvalidDataException(
                "事务文件长度在读取过程中发生变化。");
        }

        GameInstallTransaction record =
            JsonSerializer.Deserialize<GameInstallTransaction>(
                jsonBytes,
                JsonOptions)
            ?? throw new InvalidDataException(
                "事务记录不能为 null。");

        // 解析成功不代表字段或路径关系合法。
        ValidateRecordFields(record, expectedGameId);

        // 文件名来自请求编号，内容必须描述同一个操作。
        if (record.OperationId != operationId)
        {
            throw new InvalidDataException(
                "事务文件名与记录中的操作编号不一致。");
        }

        return record;
    }

    #endregion

    #region Transaction Directory(事务目录)

    /// <summary>
    /// 只计算固定路径，不创建或检查实际目录。
    /// </summary>
    private static string GetTransactionDirectoryPath()
    {
        string localAppData =
            GameInstallPlanBuilder.NormalizeLocalPath(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData));

        return Path.Combine(
            localAppData,
            "NiumaLauncher",
            "InstallTransactions");
    }

    /// <summary>
    /// 仅供写入使用，逐层准备固定的事务目录。
    /// </summary>
    private static string PrepareTransactionDirectory()
    {
        string transactionDirectory = GetTransactionDirectoryPath();

        // 上面固定追加了两级目录，因此这两个父级路径可以取得。
        string launcherDirectory = Path.GetDirectoryName(transactionDirectory)!;

        string localAppData = Path.GetDirectoryName(launcherDirectory)!;

        GameInstallPlanBuilder.EnsureDirectoryChain(localAppData);

        Directory.CreateDirectory(launcherDirectory);
        GameInstallPlanBuilder.EnsureDirectoryChain(launcherDirectory);

        Directory.CreateDirectory(transactionDirectory);
        GameInstallPlanBuilder.EnsureDirectoryChain(transactionDirectory);

        return transactionDirectory;
    }

    #endregion

    #region Record/Plan Matching(记录与计划对照)

    private static void ValidateRecordMatchesPlan(
        GameInstallTransaction record,
        GameInstallPlan plan)
    {
        DownloadedGamePackage package = plan.SourcePackage;

        bool matches =
            record.OperationId == plan.OperationId &&
            string.Equals(
                record.GameId,
                package.GameId,
                StringComparison.Ordinal) &&
            string.Equals(
                record.CurrentVersion,
                plan.CurrentVersion,
                StringComparison.Ordinal) &&
            record.CurrentBuildNumber == plan.CurrentBuildNumber &&
            string.Equals(
                record.TargetVersion,
                package.Version,
                StringComparison.Ordinal) &&
            record.TargetBuildNumber == package.BuildNumber &&
            record.PackageSizeBytes == package.SizeBytes &&
            string.Equals(
                record.PackageSha256,
                package.Sha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                record.GameExecutablePath,
                plan.GameExecutablePath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                record.WorkspaceDirectoryPath,
                plan.WorkspaceDirectoryPath,
                StringComparison.OrdinalIgnoreCase);

        if (!matches)
        {
            throw new InvalidDataException(
                "事务记录与当前安装计划不一致，禁止推进候选阶段。");
        }
    }

    #endregion

    #region Field Validation(字段校验)

    /// <summary>
    /// 只检查记录结构与字段关系，不检查磁盘实际状态。
    /// 后续读取事务时也要经过这里。
    /// </summary>
    private static void ValidateRecordFields(
        GameInstallTransaction record,
        string expectedGameId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGameId);

        if (record.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                "不支持该事务文件格式版本。");
        }

        if (record.OperationId == Guid.Empty)
        {
            throw new InvalidDataException(
                "事务记录缺少有效的操作编号。");
        }

        if (!string.Equals(
                record.GameId,
                expectedGameId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "事务记录不属于当前游戏。");
        }

        // Unknown 虽然是已定义枚举，但不属于允许使用的阶段。
        // 只接受当前流程明确支持的阶段，Unknown 仍然无效。
        if (record.Phase != InstallTransactionPhase.PreparingCandidate &&
            record.Phase != InstallTransactionPhase.CandidateReady &&
            record.Phase != InstallTransactionPhase.BackupMovePending &&
            record.Phase != InstallTransactionPhase.CandidateMovePending)
        {
            throw new InvalidDataException("事务记录包含未知或不支持的阶段。");
        }

        if (string.IsNullOrWhiteSpace(record.CurrentVersion) ||
            string.IsNullOrWhiteSpace(record.TargetVersion) ||
            record.CurrentBuildNumber <= 0 ||
            record.TargetBuildNumber <= record.CurrentBuildNumber)
        {
            throw new InvalidDataException(
                "事务记录的原版本或目标版本无效。");
        }

        if (record.PackageSizeBytes <= 0 ||
            string.IsNullOrWhiteSpace(record.PackageSha256) ||
            record.PackageSha256.Length != 64 ||
            !record.PackageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(
                "事务记录的包大小或哈希格式无效。");
        }

        ValidateRecordPaths(record);
    }

    /// <summary>
    /// 检查路径的字符串形式与布局，不取得目录操作权限。
    /// </summary>
    private static void ValidateRecordPaths(
        GameInstallTransaction record)
    {
        string executablePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                record.GameExecutablePath);

        string workspacePath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                record.WorkspaceDirectoryPath);

        // 记录应保存规范化路径；发现不一致就拒绝，不静默改写。
        if (!string.Equals(
                executablePath,
                record.GameExecutablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                workspacePath,
                record.WorkspaceDirectoryPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "事务记录中的路径不是规范化形式。");
        }

        if (!string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "事务记录中的游戏程序必须是 EXE。");
        }

        string gameDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException(
                "无法确定事务对应的游戏目录。");

        string parentDirectory = Path.GetDirectoryName(gameDirectory)
            ?? throw new InvalidDataException(
                "事务不能以磁盘根目录作为游戏目录。");

        // 从正式目录和原操作编号重算，不能只检查名称前缀。
        string expectedWorkspace = Path.Combine(
            parentDirectory,
            $"{GameInstallPlanBuilder.WorkspacePrefix}" +
            $"{record.OperationId:N}");

        if (!string.Equals(
                workspacePath,
                expectedWorkspace,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                workspacePath,
                gameDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("事务工作区与正式目录或操作编号不匹配。");
        }
    }

    #endregion
}