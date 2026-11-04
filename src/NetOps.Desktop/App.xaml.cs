using System.Windows;
using System.IO;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        var collector = e.Args.Contains("--collector");
        DispatcherUnhandledException += (_, args) =>
        {
            if (!collector) MessageBox.Show("操作未完成。请检查本地数据目录权限后重试；若持续出现，请保留操作步骤以便排查。", "网络运维工作台", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
        if (e.Args.Contains("--stop-collector"))
        {
            var stopIndex = Array.IndexOf(e.Args, "--workspace");
            if (stopIndex >= 0 && stopIndex + 1 < e.Args.Length) new LocalWorkspace(Path.Combine(Path.GetFullPath(e.Args[stopIndex + 1]), "workspace.db")).RequestCollectorStop(true);
            Shutdown(); return;
        }
        if (collector)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var index = Array.IndexOf(e.Args, "--workspace");
            if (index < 0 || index + 1 >= e.Args.Length) { Shutdown(1); return; }
            try { await Task.Run(() => CollectorHost.Run(Path.GetFullPath(e.Args[index + 1]), CancellationToken.None)); Shutdown(); }
            catch { Shutdown(1); }
            return;
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
