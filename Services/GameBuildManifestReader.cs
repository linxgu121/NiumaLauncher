using System.IO;
using System.Text.Json;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public class GameBuildManifestReader
{
    #region Configuration(配置)

    public const string ManifestFileName = "game-build.json";

    // 限制的是构建清单，不是游戏 EXE 或资源包。
    private const int MaxManifestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #endregion

    #region Reading(读取)

    public GameBuildManifest? Load(string executablePath, string expectedGameId)
    {
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidDataException("游戏程序必须使用完整路径。");
        }

        string gameDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidDataException("无法确定游戏目录。");

        string manifestPath = Path.Combine(
            gameDirectory,
            ManifestFileName);

        string json;

        try
        {
            // 长度检查和实际读取使用同一个文件句柄。
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            long length = stream.Length;

            if (length <= 0 || length > MaxManifestBytes)
            {
                throw new InvalidDataException(
                    $"版本清单必须为 1—{MaxManifestBytes} 字节。");
            }

            byte[] bytes = new byte[(int)length];

            // 如果内容不足，直接抛异常，不接受截断的清单。
            stream.ReadExactly(bytes);

            if (stream.ReadByte() != -1 || stream.Length != length)
            {
                throw new InvalidDataException(
                    "版本清单在读取过程中发生长度变化。");
            }

            // 后续只解析已经限长的内存，不重新打开文件。
            using var memory = new MemoryStream(bytes, writable: false);

            using var textReader = new StreamReader(
                memory,
                detectEncodingFromByteOrderMarks: true);

            json = textReader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            // 仅缺少清单返回 null；其它错误继续向上传递。
            return null;
        }

        GameBuildManifest manifest =
            JsonSerializer.Deserialize<GameBuildManifest>(
                json,
                JsonOptions)
            ?? throw new JsonException("版本清单不能为 null。");

        Validate(manifest, executablePath, expectedGameId);

        return manifest;
    }
    #endregion

    #region Validation(内容检查)

    private static void Validate(
        GameBuildManifest manifest,
        string executablePath,
        string expectedGameId)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"不支持的清单格式版本：{manifest.SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.GameId) ||
            !string.Equals(
                manifest.GameId,
                expectedGameId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("清单的 gameId 与当前游戏不匹配。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("清单缺少游戏版本号。");
        }

        if (manifest.BuildNumber <= 0)
        {
            throw new InvalidDataException("构建编号必须大于 0。");
        }

        // 只检查清单属于所选 EXE，不根据清单改换启动目标。
        string selectedFileName = Path.GetFileName(executablePath);

        if (!string.Equals(
                manifest.ExecutableName,
                selectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("清单中的 executableName 与所选游戏程序不匹配。");
        }
    }

    #endregion
}