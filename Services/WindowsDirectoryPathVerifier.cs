using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NiumaLauncher.Services;

/// <summary>
/// 打开已有普通目录，核对最终路径并读取实体身份
/// 成功返回的目录引用必须由调用方释放
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