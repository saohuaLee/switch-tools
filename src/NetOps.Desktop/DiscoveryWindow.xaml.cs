using System.Windows;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public sealed class DiscoveryItem(DiscoveredEndpoint endpoint)
{
    public DiscoveredEndpoint Endpoint { get; } = endpoint;
    public bool Picked { get; set; } = true;
}
public partial class DiscoveryWindow : Window
{
    private readonly MainViewModel vm;
    private CancellationTokenSource? cancellation;
    public DiscoveryWindow(MainViewModel vm)
    {
        InitializeComponent(); this.vm = vm;
        VendorBox.ItemsSource = Enum.GetValues<Vendor>().Select(v => new Choice<Vendor>(v, Labels.Vendor(v))).ToArray(); VendorBox.SelectedIndex = (int)Vendor.Other;
        Closed += (_, _) => cancellation?.Cancel();
    }
    private async void StartClick(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        try
        {
            if (!int.TryParse(TcpPort.Text, out var port)) throw new ArgumentException("TCP 端口必须为整数。");
            var total = DeviceDiscovery.Addresses(StartAddress.Text.Trim(), EndAddress.Text.Trim()).Length;
            cancellation = new(); StartButton.IsEnabled = false; CancelButton.IsEnabled = true; ImportButton.IsEnabled = false; ResultsGrid.ItemsSource = null;
            var results = await DeviceDiscovery.ScanAsync(StartAddress.Text.Trim(), EndAddress.Text.Trim(), port, new Progress<int>(n => StatusText.Text = $"已检查 {n}/{total} 个地址…"), cancellation.Token);
            ResultsGrid.ItemsSource = results.Select(p => new DiscoveryItem(p)).ToArray(); StatusText.Text = $"发现 {results.Length} 个可达端点，尚未识别设备和登录。"; ImportButton.IsEnabled = results.Length > 0;
        }
        catch (OperationCanceledException) { StatusText.Text = "发现已取消，未导入设备。"; }
        catch (Exception ex) { StatusText.Text = ex is ArgumentException ? ex.Message : "发现未完成，请核对范围与网络连接。"; }
        finally { cancellation?.Dispose(); cancellation = null; StartButton.IsEnabled = true; CancelButton.IsEnabled = false; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vendor = ((Choice<Vendor>)VendorBox.SelectedItem).Value;
            var devices = ((DiscoveryItem[]?)ResultsGrid.ItemsSource ?? []).Where(p => p.Picked).Select(p => new Device(Guid.NewGuid(), $"待识别设备 {p.Endpoint.Address}", p.Endpoint.Address, vendor, "", "", DeviceRole.Switch, "网络发现", false, Connections: [new(Guid.NewGuid(), "SSH", Port: p.Endpoint.Port)])).ToArray();
            vm.ImportDevices(devices); DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex is ArgumentException ? ex.Message : "导入失败，未保存部分设备。"; }
    }
}
