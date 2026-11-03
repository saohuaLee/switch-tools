using System.Windows;
using NetOps.Domain;

namespace NetOps.Desktop;

public partial class AddDeviceWindow : Window
{
    private readonly MainViewModel viewModel;
    public AddDeviceWindow(MainViewModel viewModel)
    {
        InitializeComponent(); this.viewModel = viewModel;
        VendorBox.ItemsSource = Enum.GetValues<Vendor>().Select(v => new Choice<Vendor>(v, Labels.Vendor(v))).ToArray();
        RoleBox.ItemsSource = new[] { new Choice<DeviceRole>(DeviceRole.Switch, "交换机"), new(DeviceRole.Router, "路由器"), new(DeviceRole.Firewall, "防火墙") };
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ManagementVlanBox.Text, out var vlan) || !int.TryParse(SshPortBox.Text, out var port)) throw new ArgumentException("管理 VLAN 和 SSH 端口必须为整数。");
            var device = new Device(Guid.NewGuid(), DeviceName.Text.Trim(), DeviceIp.Text.Trim(), ((Choice<Vendor>)VendorBox.SelectedItem).Value,
                ModelBox.Text.Trim(), FirmwareBox.Text.Trim(), ((Choice<DeviceRole>)RoleBox.SelectedItem).Value, LocationBox.Text.Trim(), SimulationBox.IsChecked == true, vlan, port, HostFingerprint: FingerprintBox.Text.Trim());
            viewModel.AddDevice(device, UsernameBox.Text, PasswordBox.Password); PasswordBox.Clear(); DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { ErrorText.Text = ex.Message; }
    }
}
