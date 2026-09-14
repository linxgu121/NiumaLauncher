using System;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NiumaLauncher.Services;

/// <summary>
/// 在一次安装流程中保留已验证目录的引用。
/// 不是排他锁，必须由所属流程负责释放。
/// </summary>
internal sealed class WindowsDirectoryReference : IDisposable
{
    #region State and Construction(状态与构造)

    private SafeFileHandle? _handle;

    // 后两项共同保存完整的 128 位文件标识。
    private readonly (
        ulong Volume,
        ulong Part1,
        ulong Part2) _identity;

    internal WindowsDirectoryReference(
        SafeFileHandle handle,
        ulong volumeSerialNumber,
        ulong fileIdPart1,
        ulong fileIdPart2)
    {
        _handle = handle;
        _identity = (
            volumeSerialNumber,
            fileIdPart1,
            fileIdPart2);
    }

    #endregion

    #region Verification(身份复核)

    public void RequireSameDirectoryAt(string directoryPath)
    {
        SafeFileHandle? originalHandle = _handle;

        ObjectDisposedException.ThrowIf(
            originalHandle is null,
            this);

        // 必须重新打开当前路径，才能知道它现在指向哪个对象。
        using WindowsDirectoryReference current =
            WindowsDirectoryPathVerifier.OpenVerifiedDirectory(
                directoryPath);

        bool sameDirectory = _identity == current._identity;

        // 明确保留原句柄的托管引用，直到比较完成。
        GC.KeepAlive(originalHandle);

        if (!sameDirectory)
        {
            throw new InvalidDataException(
                $"目录实体已经变化，不能继续安装：{directoryPath}");
        }
    }

    #endregion

    #region Disposal(释放引用)

    public void Dispose()
    {
        SafeFileHandle? handle =
            Interlocked.Exchange(ref _handle, null);

        handle?.Dispose();
    }

    #endregion
}