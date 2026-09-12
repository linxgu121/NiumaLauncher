using System.IO;
using System.Text.Json;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public class GameBuildManifestReader
{
    #region Configuration(配置)

    public const string ManifestFileName = "game-build.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #endregion

    #region Reading(读取)

    public GameBuildManifest? Load(
        string executablePath,
        string expectedGameId)
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
            json = File.ReadAllText(manifestPath);
        }
        catch (FileNotFoundException)
        {
            // 缺少清单表示没有版本信息，不能擅自当作 1.0.0。
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