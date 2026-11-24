using System.Security.Cryptography;
using System.Text;

namespace NetTrack.Core.Configuration;

/// <summary>
/// 管理数据目录及内部文件路径，支持生产默认路径与测试隔离临时目录注入。
/// </summary>
public sealed class NetTrackPaths
{
    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public string DatabasePath { get; }
    public string LockFilePath { get; }
    public string LogsDirectory { get; }
    public string NormalizedDirectoryHash { get; }

    public NetTrackPaths(string? dataDirectory = null)
    {
        var resolved = string.IsNullOrWhiteSpace(dataDirectory)
            ? GetDefaultDataDirectory()
            : dataDirectory;

        DataDirectory = Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SettingsPath = Path.Combine(DataDirectory, "settings.json");
        DatabasePath = Path.Combine(DataDirectory, "traffic.sqlite3");
        LockFilePath = Path.Combine(DataDirectory, "collector.lock");
        LogsDirectory = Path.Combine(DataDirectory, "logs");

        // 计算规范化路径哈希，用于同会话同目录桌面单窗口激活标识
        var keyBytes = Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant());
        var hash = SHA256.HashData(keyBytes);
        NormalizedDirectoryHash = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    public static string GetDefaultDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "NetTrack");
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
