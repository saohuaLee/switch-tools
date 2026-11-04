using System.Diagnostics;
using System.IO;

namespace NetOps.Desktop;

public static class CollectorLauncher
{
    public static void Start(string root)
    {
        // 独立进程与桌面使用同一用户，避免改变 DPAPI 凭据的账户边界。
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前应用程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--collector"); start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(Path.GetFullPath(root));
        Process.Start(start)?.Dispose();
    }
}
