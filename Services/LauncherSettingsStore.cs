using System.IO;
using System.Text.Json;
using NiumaLauncher.Models;

namespace NiumaLauncher.Services;

public class LauncherSettingsStore
{
    #region Configuration(配置)

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 添加缩进，方便开发期间查看配置。
        WriteIndented = true,

        // C# 的 GameExecutablePath 保存为 gameExecutablePath。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _settingsDirectory;

    public string FilePath { get; }
    
     public LauncherSettingsStore()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        _settingsDirectory = Path.Combine(localAppData,"NiumaLauncher");

        FilePath = Path.Combine(
            _settingsDirectory,
            "settings.json");
    }

    #endregion

    #region Loading(读取)

    public LauncherSettings Load()
    {
        string json;

        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (FileNotFoundException)
        {
            // 首次启动还没有配置，使用默认值。
            return new LauncherSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new LauncherSettings();
        }

        // 文件损坏不能当作正常读取成功，交给上层显示错误。
        return JsonSerializer.Deserialize<LauncherSettings>(
            json,
            JsonOptions)
            ?? throw new JsonException("设置文件内容不能为 null。");
    }

    #endregion

    #region Saving(保存)

    public void Save(LauncherSettings settings)
    {
        string json = JsonSerializer.Serialize(
            settings,
            JsonOptions);

        // 目录已经存在时不会重复创建。
        Directory.CreateDirectory(_settingsDirectory);

        // 临时文件与正式配置放在同一个目录。
        string temporaryPath = Path.Combine(
            _settingsDirectory,
            $"settings.{Guid.NewGuid():N}.tmp");

        try
        {
            // 先完整写入临时文件，再替换正式配置。
            File.WriteAllText(temporaryPath, json);

            File.Move(
                temporaryPath,
                FilePath,
                overwrite: true);
        }
        finally
        {
            try
            {
                // 替换成功后临时文件已经不存在，Delete 仍可调用。
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // 清理失败不能掩盖原本的保存异常。
                System.Diagnostics.Debug.WriteLine(
                    $"清理设置临时文件失败：{exception.Message}");
            }
        }
    }

    #endregion
    
}