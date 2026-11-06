using System.IO;
using System.IO.Ports;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NetOps.Domain;

namespace NetOps.Desktop;

public partial class AddDeviceWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly Device? original;
    private ConnectionProfile current = new(Guid.NewGuid(), "默认 SSH");
    private bool loading = true;
    private string privateKey = "";
    private string originalUsername = "";
    public AddDeviceWindow(MainViewModel viewModel, Device? device = null)
    {
        InitializeComponent(); this.viewModel = viewModel; original = device;
        VendorBox.ItemsSource = Enum.GetValues<Vendor>().Select(v => new Choice<Vendor>(v, Labels.Vendor(v))).ToArray();
        RoleBox.ItemsSource = new[] { new Choice<DeviceRole>(DeviceRole.Switch, "交换机"), new(DeviceRole.Router, "路由器"), new(DeviceRole.Firewall, "防火墙") };
        ProtocolBox.ItemsSource = Enum.GetValues<ConnectionProtocol>().Select(p => new Choice<ConnectionProtocol>(p, new ConnectionProfile(Guid.Empty, "", p).ProtocolLabel)).ToArray();
        SerialPortBox.ItemsSource = SerialPort.GetPortNames().Order().ToArray();
        ParityBox.ItemsSource = Enum.GetNames<Parity>(); StopBitsBox.ItemsSource = Enum.GetNames<StopBits>().Where(s => s != "None").ToArray(); FlowBox.ItemsSource = Enum.GetNames<Handshake>();
        VendorBox.SelectedIndex = device is null ? -1 : (int)device.Vendor; RoleBox.SelectedIndex = device is null ? 0 : (int)device.Role;
        if (device is not null)
        {
            DialogHeading.Text = "编辑设备与连接"; DeviceName.Text = device.Name; DeviceIp.Text = device.ManagementIp; ModelBox.Text = device.Model; FirmwareBox.Text = device.Firmware; LocationBox.Text = device.Location;
            ManagementVlanBox.Text = device.ManagementVlan.ToString(); SimulationBox.IsChecked = device.IsSimulation; SimulationBox.IsEnabled = false;
            ConnectionBox.ItemsSource = device.GetConnections(); current = device.GetConnections().FirstOrDefault(p => p.Id == device.PreferredConnectionId) ?? device.GetConnections()[0]; ConnectionBox.SelectedItem = current;
        }
        LoadProfile(current); loading = false;
    }
    private void SimulationChanged(object sender, RoutedEventArgs e)
    {
        if (SaveDeviceButton is null) return;
        SaveDeviceButton.Content = SimulationBox.IsChecked == true ? "创建练习设备" : "保存，进入端口设置";
        ShowProtocol();
    }
    private void LoadProfile(ConnectionProfile profile)
    {
        loading = true; current = profile; privateKey = ""; ConnectionName.Text = profile.Name; ProtocolBox.SelectedIndex = (int)profile.Protocol; NetworkPortBox.Text = profile.Port.ToString(); FingerprintBox.Text = profile.HostFingerprint ?? "";
        SerialPortBox.Text = profile.SerialPort; BaudBox.Text = profile.BaudRate.ToString(); DataBitsBox.Text = profile.DataBits.ToString(); ParityBox.SelectedItem = profile.Parity; StopBitsBox.SelectedItem = profile.StopBits; FlowBox.SelectedItem = profile.FlowControl;
        EndpointBox.Text = profile.Endpoint ?? ""; AllowTelnetBox.IsChecked = profile.AllowTelnet; PasswordBox.Clear();
        try { originalUsername = viewModel.CredentialUsername(profile.CredentialReference); } catch { originalUsername = ""; ErrorText.Text = "原凭据无法读取，请重新填写凭据。"; }
        UsernameBox.Text = originalUsername; KeyLabel.Text = "已有凭据留空保留；导入新私钥后保存为密文"; loading = false; ShowProtocol();
    }
    private void NewConnectionClick(object sender, RoutedEventArgs e) => LoadProfile(new(Guid.NewGuid(), "新连接"));
    private void ConnectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && ConnectionBox.SelectedItem is ConnectionProfile profile) LoadProfile(profile); }
    private void ProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ProtocolBox.SelectedItem is not Choice<ConnectionProtocol> choice) return;
        NetworkPortBox.Text = choice.Value switch { ConnectionProtocol.Telnet => "23", ConnectionProtocol.Netconf => "830", ConnectionProtocol.Restconf or ConnectionProtocol.HttpApi => "443", _ => "22" }; ShowProtocol();
    }
    private void ShowProtocol()
    {
        if (ProtocolBox is null || ProtocolBox.SelectedItem is not Choice<ConnectionProtocol> choice) return;
        SerialOptions.Visibility = choice.Value == ConnectionProtocol.Console ? Visibility.Visible : Visibility.Collapsed;
        ApiOptions.Visibility = choice.Value is ConnectionProtocol.Restconf or ConnectionProtocol.HttpApi ? Visibility.Visible : Visibility.Collapsed;
        FingerprintOptions.Visibility = choice.Value is ConnectionProtocol.Ssh or ConnectionProtocol.Netconf ? Visibility.Visible : Visibility.Collapsed;
        CredentialsOptions.Visibility = SimulationBox.IsChecked == true || choice.Value is ConnectionProtocol.Console or ConnectionProtocol.Telnet ? Visibility.Collapsed : Visibility.Visible;
        AllowTelnetBox.Visibility = choice.Value == ConnectionProtocol.Telnet ? Visibility.Visible : Visibility.Collapsed;
        NetworkPortBox.IsEnabled = choice.Value != ConnectionProtocol.Console;
    }
    private void ImportKeyClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 SSH 私钥（只保存加密副本）", Filter = "私钥文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { if (new FileInfo(dialog.FileName).Length > 65536) throw new ArgumentException("私钥文件不得超过 64 KB。"); privateKey = File.ReadAllText(dialog.FileName); KeyLabel.Text = "已导入新私钥，保存后使用用户级加密保护"; }
        catch (Exception) { ErrorText.Text = "无法读取私钥文件，请检查文件格式和权限。"; }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (VendorBox.SelectedItem is null) throw new ArgumentException("请选择设备厂商。");
            if (SimulationBox.IsChecked == true && string.IsNullOrWhiteSpace(DeviceIp.Text)) DeviceIp.Text = Enumerable.Range(100, 155).Select(n => $"192.0.2.{n}").First(ip => !viewModel.Devices.Any(d => d.ManagementIp == ip));
            if (!int.TryParse(ManagementVlanBox.Text, out var vlan) || !int.TryParse(NetworkPortBox.Text, out var port) || !int.TryParse(BaudBox.Text, out var baud) || !int.TryParse(DataBitsBox.Text, out var bits)) throw new ArgumentException("管理 VLAN、网络端口和串口参数必须为整数。");
            var profile = current with { Name = ConnectionName.Text.Trim(), Protocol = ((Choice<ConnectionProtocol>)ProtocolBox.SelectedItem).Value, Port = port, HostFingerprint = FingerprintBox.Text.Trim(), SerialPort = SerialPortBox.Text.Trim(), BaudRate = baud, DataBits = bits, Parity = ParityBox.SelectedItem?.ToString() ?? "None", StopBits = StopBitsBox.SelectedItem?.ToString() ?? "One", FlowControl = FlowBox.SelectedItem?.ToString() ?? "None", Endpoint = EndpointBox.Text.Trim(), AllowTelnet = AllowTelnetBox.IsChecked == true };
            if (profile.Protocol != ConnectionProtocol.Console && !IPAddress.TryParse(DeviceIp.Text.Trim(), out _)) { DeviceIp.Focus(); throw new ArgumentException("设备管理地址没有填正确，请填写交换机的 IP 地址。"); }
            if (SimulationBox.IsChecked != true && profile.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Netconf)
            {
                if (string.IsNullOrWhiteSpace(UsernameBox.Text)) throw new ArgumentException("请填写交换机的登录用户名。");
                if (string.IsNullOrWhiteSpace(FingerprintBox.Text)) throw new ArgumentException("还缺设备的可信 SSH 指纹，请向管理员获取后填写，不能跳过身份核对。");
                if (PasswordBox.Password.Length == 0 && privateKey.Length == 0 && profile.CredentialReference is null) throw new ArgumentException("请填写登录密码，或在高级选项中导入私钥。");
            }
            var device = new Device(original?.Id ?? Guid.NewGuid(), DeviceName.Text.Trim(), DeviceIp.Text.Trim(), ((Choice<Vendor>)VendorBox.SelectedItem).Value, ModelBox.Text.Trim(), FirmwareBox.Text.Trim(), ((Choice<DeviceRole>)RoleBox.SelectedItem).Value, LocationBox.Text.Trim(), SimulationBox.IsChecked == true, vlan, port, Connections: [profile]);
            var replace = PasswordBox.Password.Length > 0 || privateKey.Length > 0 || UsernameBox.Text != originalUsername;
            viewModel.SaveDevice(device, UsernameBox.Text.Trim(), PasswordBox.Password, privateKey, profile, replace); PasswordBox.Clear(); privateKey = ""; DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { ErrorText.Text = ex.Message; }
    }
}
