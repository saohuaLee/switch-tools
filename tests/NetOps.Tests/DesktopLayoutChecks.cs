using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Media;
using NetOps.Desktop;
using NetOps.Domain;

public static class DesktopLayoutChecks
{
    public static void Run(Device device, Action<string, Action> test)
    {
        test("总览实际显示模板可绑定只读地址与来源，切换数据后正确刷新", () =>
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
                }
                catch (Exception ex) { failure = ex; }
                finally { app?.Shutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("WPF 模板加载超时。");
            if (failure is not null) throw new InvalidOperationException("总览显示模板验证失败。", failure);
        });
    }
}
