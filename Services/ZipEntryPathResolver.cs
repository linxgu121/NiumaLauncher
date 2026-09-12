using System.IO;

namespace NiumaLauncher.Services;

/// <summary>
/// 检查 ZIP 条目名称，并计算暂存目录内的目标路径。
/// 只检查路径，不负责创建文件或验证磁盘上的链接。
/// </summary>
internal static class ZipEntryPathResolver
{
    #region Configuration(路径限制)

    // 以下是本应用的限制，不是所有文件系统的统一上限。
    private const int MaxEntryNameLength = 1024;
    private const int MaxSegmentLength = 255;
    private const int MaxPathDepth = 32;

    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars();

    #endregion

    #region Resolution(目标路径解析)

    public static string Resolve(
        string extractionRoot,
        string entryName,
        out bool isDirectory)
    {
        isDirectory = false;

        // 根目录由本地解压服务提供，不能来自 ZIP。
        if (string.IsNullOrWhiteSpace(extractionRoot) ||
            !Path.IsPathFullyQualified(extractionRoot))
        {
            throw new ArgumentException(
                "暂存根目录必须使用完整路径。",
                nameof(extractionRoot));
        }

        string root = Path.GetFullPath(extractionRoot);

        // 防止把整个磁盘或共享根目录当成暂存目录。
        if (string.Equals(
                root,
                Path.GetPathRoot(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "暂存目录不能是磁盘或共享根目录。",
                nameof(extractionRoot));
        }

        if (string.IsNullOrWhiteSpace(entryName) ||
            entryName.Length > MaxEntryNameLength)
        {
            throw new InvalidDataException(
                "ZIP 条目名称为空或过长。");
        }

        // 统一两种分隔符，再按同一套规则检查。
        string normalized = entryName.Replace('\\', '/');

        // 同时拒绝绝对路径、UNC、盘符相对路径和数据流名称。
        if (normalized.StartsWith('/') ||
            normalized.Contains(':'))
        {
            throw new InvalidDataException(
                "ZIP 条目必须使用相对路径，且不能包含冒号。");
        }

        // 这里只识别名称约定，后续仍需检查条目的实际类型。
        isDirectory = normalized.EndsWith('/');

        if (isDirectory)
        {
            normalized = normalized[..^1];
        }

        // 保留空段，不能把 data//file 静默修复成合法路径。
        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.None);

        if (segments.Length > MaxPathDepth)
        {
            throw new InvalidDataException(
                "ZIP 条目的路径层级过深。");
        }

        foreach (string segment in segments)
        {
            ValidateSegment(segment);
        }

        string relativePath = Path.Combine(segments);

        string destinationPath = Path.GetFullPath(
            Path.Combine(root, relativePath));

        // 必须带分隔符，避免把 job-other 误认为 job 的子目录。
        string rootPrefix =
            Path.TrimEndingDirectorySeparator(root) +
            Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "ZIP 条目的目标路径超出了暂存目录。");
        }

        return destinationPath;
    }

    #endregion

    #region Validation(名称检查)

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 ||
            segment.Length > MaxSegmentLength ||
            segment is "." or "..")
        {
            throw new InvalidDataException(
                "ZIP 路径包含空段、超长名称或相对目录标记。");
        }

        // 拒绝 Windows 下容易产生歧义的名称。
        if (segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.IndexOfAny(InvalidNameChars) >= 0)
        {
            throw new InvalidDataException(
                "ZIP 路径包含非法字符、尾随空格或尾随点。");
        }

        if (IsReservedDeviceName(segment))
        {
            throw new InvalidDataException(
                "ZIP 路径使用了 Windows 保留设备名称。");
        }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        // NUL.txt 仍然是保留名称，不能只检查完整文件名。
        int dotIndex = segment.IndexOf('.');

        string stem = dotIndex >= 0
            ? segment[..dotIndex]
            : segment;

        stem = stem.TrimEnd(' ').ToUpperInvariant();

        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            or "CONIN$" or "CONOUT$")
        {
            return true;
        }

        if (stem.Length != 4 ||
            !(stem.StartsWith("COM", StringComparison.Ordinal) ||
              stem.StartsWith("LPT", StringComparison.Ordinal)))
        {
            return false;
        }

        // 保守屏蔽端口名称；上标数字也需要考虑。
        return stem[3] is >= '0' and <= '9'
            or '¹' or '²' or '³';
    }

    #endregion
}