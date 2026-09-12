using System.IO;
using System.Net.Http;
using System.Text.Json;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public class GameReleaseClient
{
    #region Configuration(请求配置)

    // 复用连接池，不为每次检查更新创建一个 HttpClient。
    private static readonly HttpClient Client = new(
        new SocketsHttpHandler
        {
            //要求配置的地址直接返回清单，不自动跟随跳转
            AllowAutoRedirect = false,
            //连接使用一段时间后更新，避免长期沿用旧 DNS 解析结果(2m)
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
    {
        //请求超时后结束等待，不让按钮一直锁住(10s)
        Timeout = TimeSpan.FromSeconds(10),

        // 发布清单很小，限制响应大小，避免误读大文件。本例最多缓冲 64 KiB 的响应正文
        MaxResponseContentBufferSize = 64 * 1024
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #endregion

    #region Request(请求发布信息)

    public async Task<GameReleaseManifest> FetchAsync(
        Uri manifestUri,
        string expectedGameId)
    {
        bool allowedAddress = manifestUri.IsAbsoluteUri &&
            (
                manifestUri.Scheme == Uri.UriSchemeHttps ||
                (
                    manifestUri.Scheme == Uri.UriSchemeHttp &&
                    manifestUri.IsLoopback
                )
            );

        if (!allowedAddress)
        {
            throw new InvalidDataException("发布地址必须使用 HTTPS，本机调试地址除外。");
        }

        using HttpResponseMessage response =
            await Client.GetAsync(manifestUri).ConfigureAwait(false);

        // 404、500 等响应必须作为失败处理。
        response.EnsureSuccessStatusCode();

        string json =
            await response.Content.ReadAsStringAsync()
                .ConfigureAwait(false);

        GameReleaseManifest manifest =
            JsonSerializer.Deserialize<GameReleaseManifest>(
                json,
                JsonOptions)
            ?? throw new JsonException("发布清单不能为 null。");

        Validate(manifest, expectedGameId);

        return manifest;
    }

    #endregion

    #region Validation(发布信息检查)

    private static void Validate(
        GameReleaseManifest manifest,
        string expectedGameId)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("不支持的发布清单格式。");
        }

        if (string.IsNullOrWhiteSpace(manifest.GameId) ||
            !string.Equals(
                manifest.GameId,
                expectedGameId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("发布信息不属于当前游戏。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("发布信息缺少版本号。");
        }

        if (manifest.BuildNumber <= 0)
        {
            throw new InvalidDataException("发布构建编号必须大于 0。");
        }
    }

    #endregion
}