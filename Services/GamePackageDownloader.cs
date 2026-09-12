using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public sealed class GamePackageDownloader
{
    #region Configuration(下载配置)

    private const int BufferSize = 128 * 1024;

    // 本阶段设置为整个下载及校验操作最多 30 分钟。
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
    {
        // 由下面的 CancellationToken 覆盖整个操作的超时。
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    #endregion

    #region Download(下载与校验)

    public async Task<string> DownloadAsync(
        GameReleaseManifest release,
        Uri manifestUri,
        IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(manifestUri);

        cancellationToken.ThrowIfCancellationRequested();

        // 保存本次下载参数，不在 await 之后反复读取外部可变对象。
        var package = new GameReleaseManifest
        {
            PackageUrl = release.PackageUrl,
            PackageSizeBytes = release.PackageSizeBytes,
            PackageSha256 = release.PackageSha256
        };

        GameReleaseClient.ValidatePackage(package, manifestUri);

        var packageUri = new Uri(package.PackageUrl);
        long expectedSize = package.PackageSizeBytes;
        string expectedHash = package.PackageSha256.ToLowerInvariant();

        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "NiumaLauncher",
            "Downloads");

        Directory.CreateDirectory(cacheDirectory);

        // 文件名由本地生成，不使用远端 URL 中的文件名。
        string partialPath = Path.Combine(
            cacheDirectory,
            $"{Guid.NewGuid():N}.part");

        string completedPath = Path.Combine(
            cacheDirectory,
            $"package-{expectedHash}.zip");

        // 只有本次成功创建的临时文件，才允许在失败时清理。
        bool ownsPartialFile = false;

        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        operationCancellation.CancelAfter(OperationTimeout);

        CancellationToken token = operationCancellation.Token;

        try
        {
            progress?.Report(
                new PackageDownloadProgress(0, expectedSize, false));

            // 只先读取响应头，正文随后分块读取。
            using HttpResponseMessage response =
                await Client.GetAsync(
                    packageUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            // 本阶段请求完整文件，不支持断点续传的 206 响应。
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException(
                    "服务器没有返回完整发布包。");
            }

            // 有 Content-Length 时先检查一次；
            // 没有该响应头，仍会在下面统计实际字节数。
            if (response.Content.Headers.ContentLength is long length &&
                length != expectedSize)
            {
                throw new InvalidDataException(
                    "服务器声明的文件大小与发布清单不一致。");
            }

            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsPartialFile = true;

                await using Stream source =
                    await response.Content.ReadAsStreamAsync(token)
                        .ConfigureAwait(false);

                byte[] buffer = new byte[BufferSize];
                long received = 0;

                var reportTimer = Stopwatch.StartNew();

                while (true)
                {
                    int read = await source.ReadAsync(
                        buffer.AsMemory(),
                        token).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    // 在写入前拦截超量数据，同时避免计数溢出。
                    if (read > expectedSize - received)
                    {
                        throw new InvalidDataException(
                            "实际下载数据超过发布清单声明的大小。");
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        token).ConfigureAwait(false);

                    received += read;

                    // 限制报告频率，避免大量进度消息堆积到界面线程。
                    if (reportTimer.ElapsedMilliseconds >= 100 ||
                        received == expectedSize)
                    {
                        progress?.Report(
                            new PackageDownloadProgress(
                                received,
                                expectedSize,
                                false));

                        reportTimer.Restart();
                    }
                }

                if (received != expectedSize)
                {
                    throw new InvalidDataException(
                        "下载提前结束，文件大小不足。");
                }

                await destination.FlushAsync(token)
                    .ConfigureAwait(false);
            }

            // 写入流已经关闭，现在重新读取实际文件进行校验。
            progress?.Report(
                new PackageDownloadProgress(
                    expectedSize,
                    expectedSize,
                    true));

            byte[] hash;

            await using (var verificationStream = new FileStream(
                partialPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (verificationStream.Length != expectedSize)
                {
                    throw new InvalidDataException(
                        "临时文件的实际大小不正确。");
                }

                hash = await SHA256.HashDataAsync(
                    verificationStream,
                    token).ConfigureAwait(false);
            }

            string actualHash = Convert.ToHexString(hash);

            if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "发布包 SHA-256 不匹配，不能使用该文件。");
            }

            token.ThrowIfCancellationRequested();

            // 仅在大小、哈希都通过后，才成为已校验的缓存包。
            // 替换目标也只在下载缓存目录中，不涉及游戏安装目录。
            File.Move(partialPath, completedPath, overwrite: true);
            ownsPartialFile = false;

            return completedPath;
        }
        finally
        {
            if (ownsPartialFile)
            {
                TryDeletePartialFile(partialPath);
            }
        }
    }

    #endregion

    #region Cleanup(临时文件清理)

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不能掩盖原本的下载异常。
            Debug.WriteLine(
                $"下载临时文件清理失败：{path}，{exception.Message}");
        }
    }

    #endregion
}