using System.IO;
using System.Net.Http;
using System.Windows;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--workspace");
        var root = index >= 0 && args.Length > index + 1 ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetOpsStudio", "data");
        var workspace = new LocalWorkspace(Path.Combine(root, "workspace.db"));
        var vault = new DpapiVault(Path.Combine(root, "vault"));
        var drivers = new DriverRegistry();
        DemoWorkspace.Seed(workspace);
        viewModel = new(workspace, vault, drivers, new SshReadOnlyTransport(vault, drivers), new AiClient(http, vault));
        DataContext = viewModel;
        Loaded += (_, _) => { OverviewScroll.ScrollToTop(); Focus(); };
        Closed += (_, _) => { viewModel.Dispose(); http.Dispose(); };
    }
    private void AddDeviceClick(object sender, RoutedEventArgs e) => new AddDeviceWindow(viewModel) { Owner = this }.ShowDialog();
    private void SaveProfileClick(object sender, RoutedEventArgs e) => SaveProfile(false);
    private void SaveNewProfileClick(object sender, RoutedEventArgs e) => SaveProfile(true);
    private void SaveProfile(bool asNew)
    {
        try { viewModel.SaveProfile(ApiKeyBox.Password, asNew); ApiKeyBox.Clear(); }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "AI 配置未保存", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
