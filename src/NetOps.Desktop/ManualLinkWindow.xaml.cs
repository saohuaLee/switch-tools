using System.Windows;
using System.Windows.Controls;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public partial class ManualLinkWindow : Window
{
    private readonly MainViewModel vm;
    public ManualLinkWindow(MainViewModel vm) { InitializeComponent(); this.vm = vm; var devices = vm.Devices.Where(d => !d.Archived).ToArray(); LocalDevice.ItemsSource = RemoteDevice.ItemsSource = devices; }
    private void LocalChanged(object sender, SelectionChangedEventArgs e) { if (vm is not null && LocalDevice.SelectedItem is Device d) LocalPort.ItemsSource = vm.PortsForDevice(d.Id); }
    private void RemoteChanged(object sender, SelectionChangedEventArgs e) { if (vm is not null && RemoteDevice.SelectedItem is Device d) RemotePort.ItemsSource = vm.PortsForDevice(d.Id); }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LocalDevice.SelectedItem is not Device local || RemoteDevice.SelectedItem is not Device remote || local.Id == remote.Id || string.IsNullOrWhiteSpace(LocalPort.Text) || string.IsNullOrWhiteSpace(RemotePort.Text)) throw new ArgumentException("请选择不同的两台设备，并填写两端端口。");
            var id = TopologyDiscovery.StableId(local.Id, LocalPort.Text.Trim(), remote.Id.ToString(), RemotePort.Text.Trim(), "手工");
            vm.SaveManualLink(new(id, local.Id, LocalPort.Text.Trim(), remote.Id, remote.Id.ToString(), remote.Name, RemotePort.Text.Trim(), "手工连接", DateTimeOffset.UtcNow, Manual: true)); DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex is ArgumentException ? ex.Message : "拓扑连接保存失败。"; }
    }
}
