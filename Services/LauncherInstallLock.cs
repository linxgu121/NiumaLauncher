using System;
using System.IO;
using System.Threading;

namespace NiumaLauncher.Services;

/// <summary>
/// 当前用户范围的安装操作锁。
/// 只有成功取得排他文件句柄，才表示持有锁。
/// </summary>
internal sealed class LauncherInstallLock : IDisposable
{
    #region Configuration and State(配置与状态)

    // 所有参与互斥的启动器实例必须使用同一个固定名称。
    private const string LockFileName = "install.lock";

    // 句柄保持打开期间，安装锁才有效。
    private FileStream? _lockStream;

    private LauncherInstallLock(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    #endregion

    #region Acquisition(获取锁)

    /// <summary>
    /// 尝试获取当前用户的安装锁。
    /// 占用或其它文件错误直接抛出，不主动排队等待。
    /// </summary>
    public static LauncherInstallLock Acquire()
    {
        string localAppData =
            GameInstallPlanBuilder.NormalizeLocalPath(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData));

        // 用户配置根应当已经存在，只允许创建应用自己的子目录。
        GameInstallPlanBuilder.EnsureDirectoryChain(localAppData);

        string launcherDirectory = Path.Combine(
            localAppData,
            "NiumaLauncher");

        Directory.CreateDirectory(launcherDirectory);
        GameInstallPlanBuilder.EnsureDirectoryChain(launcherDirectory);

        string lockPath = Path.Combine(
            launcherDirectory,
            LockFileName);

        // 首次使用时允许尚无锁文件，但已有文件必须符合约束。
        ValidateLockFile(lockPath, allowMissing: true);

        FileStream? stream = null;

        try
        {
            // 文件存在就复用；是否能排他打开，由操作系统判断。
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            // 获取句柄后再次复核，再把持有权交给调用方。
            GameInstallPlanBuilder.EnsureDirectoryChain(
                launcherDirectory);

            ValidateLockFile(lockPath, allowMissing: false);

            return new LauncherInstallLock(stream);
        }
        catch
        {
            // 获取后发生异常，也必须释放本次已经打开的句柄。
            stream?.Dispose();
            throw;
        }
    }

    #endregion

    #region Disposal(释放锁)

    public void Dispose()
    {
        // 先取走字段，避免同一个对象重复释放。
        FileStream? stream = Interlocked.Exchange(
            ref _lockStream,
            null);

        stream?.Dispose();

        // 不删除锁文件；关闭句柄就是释放占用。
    }

    #endregion

    #region Validation(锁文件检查)

    private static void ValidateLockFile(
        string lockPath,
        bool allowMissing)
    {
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(lockPath);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return;
        }

        // 不把权限错误、目录缺失等其它问题当成“锁空闲”。
        if ((attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "安装锁必须是普通文件，不能是目录或重解析点。");
        }
    }

    #endregion
}