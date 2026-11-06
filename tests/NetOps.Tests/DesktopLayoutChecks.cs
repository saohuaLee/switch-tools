using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Media;
using NetOps.Desktop;
using NetOps.Domain;
using NetOps.Application;
using NetOps.Infrastructure;
using System.IO;
using System.Net.Http;

public static class DesktopLayoutChecks
{
    public static void Run(Device device, string root, Action<string, Action> test)
    {
        test("WPF 实际模板、新手所有任务及窄窗口固定按钮正常，切换步骤回到顶部", () =>
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                App? app = null;
                try
                {
                    app = new App(); app.InitializeComponent();
                    var errors = new List<Exception>();
                    app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                    var template = (DataTemplate)app.FindResource("HealthDeviceIdentity");
                    var panel = (StackPanel)template.LoadContent();
                    void Render(Device target)
                    {
                        panel.DataContext = new DeviceHealthRow(target, null, null, 0);
                        panel.Measure(new Size(340, 48)); panel.Arrange(new Rect(0, 0, 340, 48)); panel.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        if (errors.Count > 0) throw new InvalidOperationException("显示模板出现绑定异常。", errors[0]);
                        var labels = panel.Children.OfType<TextBlock>().ToArray();
                        var detail = string.Concat(labels[1].Inlines.OfType<Run>().Select(r => r.Text));
                        if (labels[0].Text != target.Name || !detail.Contains(target.ManagementIp) || !detail.Contains(target.IsSimulation ? "模拟设备" : "真实设备"))
                            throw new InvalidOperationException("设备名称、地址或来源显示不正确。");
                        if (panel.DesiredSize.Height > 48) throw new InvalidOperationException("设备身份内容超出行高。");
                    }
                    Render(device with { Name = "排版测试交换机", ManagementIp = "192.0.2.99", IsSimulation = true });
                    Render(device with { Name = "另一台排版测试交换机", ManagementIp = "192.0.2.100", IsSimulation = false });
                    var macPanel = new MacBindingPanel();
                    macPanel.Measure(new Size(900, 360)); macPanel.Arrange(new Rect(0, 0, 900, 360)); macPanel.UpdateLayout();
                    IEnumerable<DependencyObject> Descendants(DependencyObject root)
                    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
                    var next = Descendants(macPanel).OfType<Button>().Single(b => b.Content?.ToString() == "下一步：审核并执行 →");
                    var position = next.TranslatePoint(new Point(0, 0), macPanel);
                    if (position.Y < 0 || position.Y + next.ActualHeight > 360 || position.X + next.ActualWidth > 900) throw new InvalidOperationException("MAC 页底下一步按钮超出窄窗口可视范围。");
                    var db = new LocalWorkspace(Path.Combine(root, "layout.db")); DemoWorkspace.Seed(db);
                    var vault = new DpapiVault(Path.Combine(root, "layout-vault")); var drivers = new DriverRegistry(); var sessions = new NoSessions(); using var http = new HttpClient();
                    using var vm = new MainViewModel(db, vault, drivers, new NoAi(), sessions, new NetworkOperations(db, drivers, sessions, vault, http));
                    var target = vm.Devices.First(d => d.Vendor == Vendor.Cisco); vm.SelectedDevice = target; vm.OpenDevice.Execute(target.Id); vm.ReadCurrent.Execute(null);
                    void Layout(FrameworkElement element)
                    {
                        element.Measure(new Size(780, 430)); element.Arrange(new Rect(0, 0, 780, 430)); element.UpdateLayout();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        if (errors.Count > 0) throw new InvalidOperationException("新手页面出现绑定异常。", errors[0]);
                    }
                    var launcher = new TaskLauncherView { DataContext = vm }; Layout(launcher);
                    var wizard = new EasyWorkflowView { DataContext = vm };
                    void CheckFooter()
                    {
                        Layout(wizard);
                        var button = (Button)wizard.FindName("NextStepButton"); var point = button.TranslatePoint(new Point(0, 0), wizard);
                        if (point.Y < 0 || point.Y + button.ActualHeight > 430 || point.X < 0 || point.X + button.ActualWidth > 780 || button.ActualHeight < 30)
                            throw new InvalidOperationException("新手页固定操作按钮超出窄窗口。");
                    }
                    foreach (var action in Enum.GetValues<EasyAction>())
                    {
                        vm.StartEasy.Execute(action.ToString()); CheckFooter();
                        if (vm.EasyNeedsPort) { vm.Ports.First(p => p.State.Mode == PortMode.Access && !p.State.IsUplink).Picked = true; vm.EasyNext.Execute(null); CheckFooter(); }
                        vm.ExitEasy.Execute(null);
                    }
                    vm.StartEasy.Execute("PortName"); vm.Ports.First(p => p.State.Mode == PortMode.Access && !p.State.IsUplink).Picked = true; vm.EasyNext.Execute(null); vm.EasyName = "布局验收打印机";
                    var body = (ScrollViewer)wizard.FindName("BodyScroll"); CheckFooter(); body.ScrollToEnd(); Layout(wizard); vm.EasyNext.Execute(null); Layout(wizard);
                    if (!vm.EasyReviewVisible || body.VerticalOffset != 0 || vm.EasyReviewRows.Any(r => r.After.Contains("PVID"))) throw new InvalidOperationException("确认页未回到顶部或仍显示无关端口字段。");
                    var apply = Descendants(wizard).OfType<Button>().Single(b => b.Content?.ToString() == "确认修改并检查结果");
                    var applyPosition = apply.TranslatePoint(new Point(0, 0), wizard);
                    if (applyPosition.Y + apply.ActualHeight > 430 || applyPosition.X + apply.ActualWidth > 780) throw new InvalidOperationException("确认页执行按钮不可见。");
                    vm.ExitEasy.Execute(null);
                    var add = new AddDeviceWindow(vm); if (((CheckBox)add.FindName("SimulationBox")).IsChecked == true) throw new InvalidOperationException("添加设备不能默认成为练习设备。");
                    var setup = (TabControl)add.FindName("SetupPages"); Layout((FrameworkElement)add.Content);
                    var nameInput = (TextBox)add.FindName("DeviceName");
                    if (setup.SelectedContent is null || nameInput.ActualHeight < 30 || nameInput.ActualWidth < 100) throw new InvalidOperationException("添加设备首步内容没有实际显示。");
                    setup.SelectedIndex = 1; Layout((FrameworkElement)add.Content);
                    if (((TextBox)add.FindName("UsernameBox")).ActualHeight < 30) throw new InvalidOperationException("连接账户步骤没有实际显示。");
                    add.Close(); var import = new ImportDevicesWindow(vm); Layout((FrameworkElement)import.Content); import.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { app?.Shutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("WPF 模板加载超时。");
            if (failure is not null) throw new InvalidOperationException("总览显示模板验证失败。", failure);
        });
    }
    private sealed class NoAi : IAiClient { public Task<string> AnalyzeAsync(AiProfile p, string q, string c, CancellationToken ct) => throw new NotSupportedException(); }
    private sealed class NoSessions : ISessionFactory { public IDeviceSession Create(ConnectionProtocol protocol) => throw new InvalidOperationException("排版检查不允许连接真实设备。"); }
}
