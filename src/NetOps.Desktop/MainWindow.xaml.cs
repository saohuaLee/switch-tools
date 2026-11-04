using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using NetOps.Infrastructure;
using NetOps.Domain;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetOps.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly string workspaceRoot;
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--qa")) Title = "网络运维工作台 · 交互重构独立验收 0.4.1";
        var index = Array.IndexOf(args, "--workspace");
        var root = index >= 0 && args.Length > index + 1 ? Path.GetFullPath(args[index + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetOpsStudio", "data");
        workspaceRoot = root;
        var workspace = new LocalWorkspace(Path.Combine(root, "workspace.db"));
        var vault = new DpapiVault(Path.Combine(root, "vault"));
        var drivers = new DriverRegistry();
        DemoWorkspace.Seed(workspace);
        MonitorScheduler.SeedSimulation(workspace);
        var sessions = new SessionFactory(vault);
        viewModel = new(workspace, vault, drivers, new AiClient(http, vault), sessions, new NetworkOperations(workspace, drivers, sessions, vault, http));
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModelChanged;
        viewModel.CollectorStartRequested += () => CollectorLauncher.Start(workspaceRoot);
        refreshTimer.Tick += (_, _) => { try { viewModel.RefreshObservability(); } catch { viewModel.Status = "监控视图本次刷新未完成，历史数据仍保留。"; } };
        refreshTimer.Start();
        RefreshMatrix();
        Loaded += (_, _) => { if (!workspace.CollectorStopRequested() && workspace.MonitorProfiles().Any(p => p.Enabled)) CollectorLauncher.Start(workspaceRoot); };
        Closed += (_, _) => { refreshTimer.Stop(); viewModel.Dispose(); http.Dispose(); };
    }
    private void OpenQueueClick(object sender, RoutedEventArgs e) => viewModel.Page = 4;
    private void DeviceActionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
    private void DeviceRowDoubleClick(object sender, MouseButtonEventArgs e)
    { if (sender is DataGrid { SelectedItem: DeviceHealthRow row } && e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source) is not null) viewModel.OpenDevice.Execute(row); }
    private static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    { while (current is not null) { if (current is T match) return match; current = VisualTreeHelper.GetParent(current); } return null; }
    private void AutoLayoutClick(object sender, RoutedEventArgs e) => viewModel.AutoLayout();
    private void ToggleStackClick(object sender, RoutedEventArgs e) => viewModel.ToggleStack();
    private void RemoveManualLinkClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedTopologyLink?.Manual != true) { viewModel.Status = "请选择手工添加的连接。"; return; }
        if (MessageBox.Show(this, "移除此手工连接？设备配置不会因此改变。", "移除拓扑连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { viewModel.RemoveManualLink(); } catch (InvalidOperationException ex) { viewModel.Status = ex.Message; }
    }
    private void TopologyZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (NetworkCanvas is not null) NetworkCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue); }
    private void OpenTopologyDeviceClick(object sender, RoutedEventArgs e)
    {
        var id = viewModel.SelectedTopologyNode?.Health?.Device.Id ?? viewModel.SelectedTopologyLink?.LocalDeviceId;
        if (id is not Guid deviceId) { viewModel.Status = "请先选择一个已纳管设备或连接。"; return; }
        var port = viewModel.SelectedTopologyLink?.LocalPort; viewModel.OpenDevice.Execute(deviceId);
        if (port is not null) { viewModel.Section = 1; foreach (var item in viewModel.Ports) item.Picked = SwitchInventory.NormalizePort(item.State.Name).Equals(SwitchInventory.NormalizePort(port), StringComparison.OrdinalIgnoreCase); }
    }
    private void MonitorSettingsClick(object sender, RoutedEventArgs e)
    { if (viewModel.SelectedDevice is not null) new MonitorWindow(viewModel) { Owner = this }.ShowDialog(); }
    private void ManualLinkClick(object sender, RoutedEventArgs e) => new ManualLinkWindow(viewModel) { Owner = this }.ShowDialog();
    private void StageMacClick(object sender, RoutedEventArgs e)
    { MacDraftGrid.CommitEdit(DataGridEditingUnit.Cell, true); MacDraftGrid.CommitEdit(DataGridEditingUnit.Row, true); viewModel.StageMac.Execute(null); }
    private void AddDeviceClick(object sender, RoutedEventArgs e) => new AddDeviceWindow(viewModel) { Owner = this }.ShowDialog();
    private void EditDeviceClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedDevice is null) return;
        if (viewModel.TerminalConnected || viewModel.Busy) { viewModel.Status = "请先结束当前设备操作，再编辑连接。"; return; }
        new AddDeviceWindow(viewModel, viewModel.SelectedDevice) { Owner = this }.ShowDialog();
    }
    private void ArchiveClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedDevice is null) return;
        if (MessageBox.Show(this, $"归档“{viewModel.SelectedDevice.Name}”？备份与审计仍将保留。", "归档设备", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { viewModel.ArchiveSelected(); } catch (InvalidOperationException ex) { viewModel.Status = ex.Message; }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入设备清单", Filter = "设备清单|*.csv;*.xlsx|CSV 文件|*.csv|Excel 工作簿|*.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new ArgumentException("设备清单文件最多 2 MB。");
            if (Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) { using var stream = File.OpenRead(dialog.FileName); viewModel.ImportDevices(DeviceXlsx.Parse(stream)); }
            else { using var reader = File.OpenText(dialog.FileName); viewModel.ImportDevices(DeviceCsv.Parse(reader)); }
        }
        catch (Exception ex) { viewModel.Status = ex is ArgumentException ? ex.Message : "导入失败，请检查清单格式和本地文件权限；未保存部分数据。"; }
    }
    private void DiscoverClick(object sender, RoutedEventArgs e) => new DiscoveryWindow(viewModel) { Owner = this }.ShowDialog();
    private void OpenTerminalClick(object sender, RoutedEventArgs e) => viewModel.Section = 4;
    private void CloseEditorClick(object sender, RoutedEventArgs e) => viewModel.EditorOpen = false;
    private void ProtocolPresetClick(object sender, RoutedEventArgs e) => viewModel.UseProtocolPreset();
    private void SendTerminalClick(object sender, RoutedEventArgs e)
    {
        if (viewModel.SecretInput) { viewModel.TerminalInput = TerminalPassword.Password; TerminalPassword.Clear(); }
        viewModel.SendTerminal.Execute(null);
    }
    private void TerminalTextChanged(object sender, TextChangedEventArgs e) { if (AutoScrollTerminal?.IsChecked == true && sender is TextBox box) box.ScrollToEnd(); }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MembershipRows)) RefreshMatrix();
        if (e.PropertyName == nameof(MainViewModel.ReviewOpen) && viewModel.ReviewOpen) Dispatcher.BeginInvoke(() => PlanConfirmBox.Focus());
    }
    private void RefreshMatrix()
    {
        MembershipGrid.Columns.Clear(); MembershipGrid.Columns.Add(new DataGridTextColumn { Header = "端口", Binding = new Binding(nameof(MembershipRow.Name)), Width = 190 });
        foreach (var vlan in viewModel.Vlans) MembershipGrid.Columns.Add(new DataGridTextColumn { Header = $"VLAN {vlan.Id}", Binding = new Binding($"[{vlan.Id}]"), Width = 95 });
        MembershipGrid.ItemsSource = viewModel.MembershipRows;
    }
    private void SaveProfileClick(object sender, RoutedEventArgs e) => SaveProfile(false);
    private void SaveNewProfileClick(object sender, RoutedEventArgs e) => SaveProfile(true);
    private void SetDefaultProfileClick(object sender, RoutedEventArgs e)
    {
        try { viewModel.SetDefaultProfile(); ProfileError.Text = ""; } catch (ArgumentException ex) { ProfileError.Text = ex.Message; }
    }
    private void SaveProfile(bool asNew)
    {
        try { viewModel.SaveProfile(ApiKeyBox.Password, asNew); ApiKeyBox.Clear(); ProfileError.Text = ""; }
        catch (Exception ex) { ProfileError.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "配置保存失败，请检查本地数据目录权限。"; }
    }
}
