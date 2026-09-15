using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NiumaLauncher.Services;

/// <summary>
/// 验证普通目录身份，并提供允许末级目标缺失的只读观察
/// OpenVerifiedDirectory 返回的目录引用必须由调用方释放
/// </summary>
internal static class WindowsDirectoryPathVerifier
{
    #region Configuration(原生参数)

    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS。
    private const uint NormalizedDosPath = 0;

    private const string ExtendedPathPrefix = @"\\?\";

    // 单位是 UTF-16 字符，容量包含末尾终止符空间。
    private const int MaxFinalPathCharacters = 32768;

    #endregion

    #region Verification(实际路径核对)

    public static WindowsDirectoryReference OpenVerifiedDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "当前目录身份检查仅支持 Windows。");
        }

        string normalizedPath =
            GameInstallPlanBuilder.NormalizeLocalPath(directoryPath);

        GameInstallPlanBuilder.EnsureDirectoryChain(normalizedPath);

        // 成功后由返回对象持有句柄，因此这里不能使用 using。
        SafeFileHandle handle = OpenDirectoryNative(
            ExtendedPathPrefix + normalizedPath,
            FileReadAttributes,
            (uint)(FileShare.Read | FileShare.Write | FileShare.Delete),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        try
        {
            if (handle.IsInvalid)
            {
                int errorCode = Marshal.GetLastWin32Error();

                throw CreateNativeFailure(
                    "打开目录进行身份检查",
                    normalizedPath,
                    errorCode);
            }

            // 核对实际打开的对象，不能只相信打开前的路径属性。
            if (!ReadAttributeTagNative(
                    handle,
                    FileInformationClass.FileAttributeTagInfo,
                    out FileAttributeTagInfoNative attributeInfo,
                    (uint)Marshal.SizeOf<FileAttributeTagInfoNative>()))
            {
                int errorCode = Marshal.GetLastWin32Error();

                throw CreateNativeFailure(
                    "读取目录对象属性",
                    normalizedPath,
                    errorCode);
            }

            FileAttributes attributes =
                (FileAttributes)attributeInfo.FileAttributes;

            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"目标必须是普通目录，不能是重解析点：{normalizedPath}");
            }

            // 属性、最终路径和身份都查询同一个句柄。
            RequireDirectPath(handle, normalizedPath);

            if (!ReadIdentityNative(
                    handle,
                    FileInformationClass.FileIdInfo,
                    out FileIdInfoNative identity,
                    (uint)Marshal.SizeOf<FileIdInfoNative>()))
            {
                int errorCode = Marshal.GetLastWin32Error();

                throw CreateNativeFailure(
                    "读取目录实体标识",
                    normalizedPath,
                    errorCode);
            }

            return new WindowsDirectoryReference(
                handle,
                identity.VolumeSerialNumber,
                identity.FileIdPart1,
                identity.FileIdPart2);
        }
        catch
        {
            // 没有成功交接时，仍由当前方法负责关闭。
            handle.Dispose();
            throw;
        }
    }

    private static void RequireDirectPath(SafeFileHandle handle, string normalizedPath)
    {

        var buffer = new StringBuilder(MaxFinalPathCharacters);

        uint length = GetFinalPathNative(
            handle,
            buffer,
            (uint)buffer.Capacity,
            NormalizedDosPath);

        if (length == 0)
        {
            int errorCode = Marshal.GetLastWin32Error();

            throw CreateNativeFailure(
                "读取目录实际路径",
                normalizedPath,
                errorCode);
        }

        // 不使用可能被截断的路径，也不按任意长度继续分配。
        if (length >= (uint)buffer.Capacity)
        {
            throw new InvalidDataException(
                "目录实际路径超过当前支持的长度。");
        }

        string returnedPath = buffer.ToString();

        if (!returnedPath.StartsWith(
                ExtendedPathPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Windows 返回了不支持的目录路径形式。");
        }

        // 只移除已经确认的前缀，再套用普通本地路径规则。
        string finalPath =
            GameInstallPlanBuilder.NormalizeLocalPath(
                returnedPath[ExtendedPathPrefix.Length..]);

        if (!string.Equals(
                normalizedPath,
                finalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "目录实际路径与计划路径不一致，不能继续安装。" +
                $"计划路径：{normalizedPath}；实际路径：{finalPath}。");
        }

        // 查询结束前再次检查原路径的目录链。
        // 这不是原子检查，也不能保证返回后目录不再变化。
        GameInstallPlanBuilder.EnsureDirectoryChain(normalizedPath);
    }

    #endregion

    #region Directory Observation(目录存在性观察)

    /// <summary>
    /// 观察目标目录，直接父目录必须存在且能够验证。
    /// true：本次观察确认存在普通目录。
    /// false：本次观察确认末级目标不存在。
    /// null：无法确认，具体原因通过 diagnostic 返回。
    /// 本方法不获取安装锁，也不提供恢复操作授权。
    /// </summary>
    internal static bool? ObserveDirectory(
        string directoryPath,
        out string diagnostic)
    {
        diagnostic = string.Empty;

        try
        {
            string normalizedPath =
                GameInstallPlanBuilder.NormalizeLocalPath(directoryPath);

            string parentPath = Path.GetDirectoryName(normalizedPath)
                ?? throw new InvalidDataException(
                    "目录观察需要明确的父目录，不能直接观察磁盘根目录。");

            // 先确认父目录可访问，并保留它的实体身份。
            // 父目录本身消失或无法读取，不能当成目标目录不存在。
            using WindowsDirectoryReference parentReference =
                OpenVerifiedDirectory(parentPath);

            FileAttributes attributes;

            try
            {
                attributes = File.GetAttributes(normalizedPath);
            }
            catch (FileNotFoundException)
            {
                // 只有目标属性读取报告缺失，并且父目录仍是原实体，
                // 才把这次观察记为 false。
                parentReference.RequireSameDirectoryAt(parentPath);
                return false;
            }

            // 路径有条目，但文件占位或重解析点都不属于合格目录。
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"目标必须是普通目录，不能是文件或重解析点：{normalizedPath}");
            }

            // 属性预检之后，继续通过句柄核对实际路径和实体。
            using WindowsDirectoryReference directoryReference =
                OpenVerifiedDirectory(normalizedPath);

            directoryReference.RequireSameDirectoryAt(normalizedPath);
            parentReference.RequireSameDirectoryAt(parentPath);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            // 保留已预期的读取/验证失败，不将其伪装成“不存在”。
            diagnostic =
                $"{exception.GetType().Name}: {exception.Message}";

            return null;
        }
    }

    #endregion

    #region Diagnostics(原生错误转换)

    private static IOException CreateNativeFailure(
        string operation,
        string path,
        int errorCode)
    {
        var nativeError = new Win32Exception(errorCode);

        return new IOException(
            $"{operation}失败：{path}。" +
            $"系统错误 {errorCode}：{nativeError.Message}",
            nativeError);
    }

    #endregion

    #region Identity Native Calls(目录身份原生接口)

    private enum FileInformationClass
    {
        FileAttributeTagInfo = 9,
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfoNative
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoNative
    {
        public ulong VolumeSerialNumber;

        // 两个 ulong 原样承载 FILE_ID_128 的 16 字节。
        public ulong FileIdPart1;
        public ulong FileIdPart2;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributeTagNative(
        SafeFileHandle file,
        FileInformationClass informationClass,
        out FileAttributeTagInfoNative information,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadIdentityNative(
        SafeFileHandle file,
        FileInformationClass informationClass,
        out FileIdInfoNative information,
        uint bufferSize);

    #endregion

    #region Native Calls(Windows 原生接口)

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle OpenDirectoryNative(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNative(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathCharacters,
        uint flags);

    #endregion
}