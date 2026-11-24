using System.Windows.Forms;
using NetTrack.Core.Configuration;
using NetTrack.Desktop.Coordination;
using NetTrack.Desktop.Forms;

namespace NetTrack.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? injectedDataDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                injectedDataDir = args[i + 1];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(injectedDataDir))
        {
            var envDir = Environment.GetEnvironmentVariable("NETTRACK_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                injectedDataDir = envDir;
            }
        }

        var paths = new NetTrackPaths(injectedDataDir);

        using var coordinator = DesktopWindowCoordinator.TryCreateOrSignalExisting(paths);
        if (!coordinator.IsPrimaryInstance)
        {
            // 已有同会话同目录桌面窗口，已发送唤醒信号，直接退出
            return;
        }

        using var mainForm = new MainForm(paths);
        coordinator.ActivationRequested += mainForm.RestoreFromTray;
        coordinator.StartListening();

        Application.Run(mainForm);
    }
}
