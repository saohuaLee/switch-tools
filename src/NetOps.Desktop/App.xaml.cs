using System.Windows;

namespace NetOps.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成。请检查本地数据目录权限后重试；若持续出现，请保留操作步骤以便排查。", "网络运维工作台", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
