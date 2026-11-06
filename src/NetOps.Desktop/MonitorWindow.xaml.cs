using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NetOps.Domain;

namespace NetOps.Desktop;

public partial class MonitorWindow : Window
{
    private readonly MainViewModel vm;
    private readonly MonitorProfile original;
    private readonly MonitorCredential? oldCredential;
    public MonitorWindow(MainViewModel vm)
    {
        InitializeComponent(); this.vm = vm; original = vm.CurrentMonitorProfile; DeviceLabel.Text = vm.SelectedDevice!.Name;
        ProtocolBox.ItemsSource = new[] { new Choice<MonitorProtocol>(MonitorProtocol.SnmpV3, "专用监控账户（SNMPv3，需管理员提供）"), new(MonitorProtocol.SnmpV2, "旧式监控口令（SNMPv2c，只用于可信网络）"), new(MonitorProtocol.Ssh, "使用已有登录连接（SSH，只读）") };
        ProtocolBox.SelectedIndex = (int)original.Protocol; AuthBox.SelectedIndex = 0;
        EnabledBox.IsChecked = original.Enabled; MaintenanceBox.IsChecked = original.Maintenance; PortBox.Text = original.Port.ToString(); IntervalBox.Text = original.IntervalSeconds.ToString(); FailureBox.Text = original.FailureThreshold.ToString();
        CpuBox.Text = original.CpuThreshold.ToString(CultureInfo.InvariantCulture); MemoryBox.Text = original.MemoryThreshold.ToString(CultureInfo.InvariantCulture); TemperatureBox.Text = original.TemperatureThreshold?.ToString(CultureInfo.InvariantCulture) ?? ""; AlarmSamplesBox.Text = original.AlarmSamples.ToString();
        CpuOidBox.Text = original.CpuOid; MemoryOidBox.Text = original.MemoryOid; TemperatureOidBox.Text = original.TemperatureOid; MemberBox.Text = original.MetricMemberId?.ToString() ?? "";
        try { oldCredential = vm.CurrentMonitorCredential; if (oldCredential is not null) { UsernameBox.Text = oldCredential.Username; AuthBox.SelectedIndex = oldCredential.Authentication == "SHA1" ? 1 : 0; } }
        catch { ErrorText.Text = "原监控凭据无法读取，启用采集前请重新填写。"; }
    }
    private void ProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is not Choice<MonitorProtocol> selected || SnmpPanel is null) return;
        SnmpPanel.Visibility = selected.Value == MonitorProtocol.Ssh ? Visibility.Collapsed : Visibility.Visible; SshHint.Visibility = selected.Value == MonitorProtocol.Ssh ? Visibility.Visible : Visibility.Collapsed;
        V3Panel.Visibility = AuthPanel.Visibility = selected.Value == MonitorProtocol.SnmpV3 ? Visibility.Visible : Visibility.Collapsed; V2Panel.Visibility = selected.Value == MonitorProtocol.SnmpV2 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            int Int(TextBox box, string name) => int.TryParse(box.Text, out var value) ? value : throw new ArgumentException(name + "必须为整数。");
            double Double(TextBox box, string name) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new ArgumentException(name + "必须为有效数字。");
            var protocol = ((Choice<MonitorProtocol>)ProtocolBox.SelectedItem).Value;
            var profile = original with { Enabled = EnabledBox.IsChecked == true, Maintenance = MaintenanceBox.IsChecked == true, Protocol = protocol, Port = Int(PortBox, "端口"), IntervalSeconds = Int(IntervalBox, "间隔"), FailureThreshold = Int(FailureBox, "失败次数"), AlarmSamples = Int(AlarmSamplesBox, "超限次数"), CpuThreshold = Double(CpuBox, "CPU 阈值"), MemoryThreshold = Double(MemoryBox, "内存阈值"), TemperatureThreshold = string.IsNullOrWhiteSpace(TemperatureBox.Text) ? null : Double(TemperatureBox, "温度阈值"), CpuOid = CpuOidBox.Text.Trim(), MemoryOid = MemoryOidBox.Text.Trim(), TemperatureOid = TemperatureOidBox.Text.Trim(), MetricMemberId = string.IsNullOrWhiteSpace(MemberBox.Text) ? null : Int(MemberBox, "成员编号") };
            MonitorCredential? credential = null;
            if (protocol != MonitorProtocol.Ssh)
            {
                credential = new(UsernameBox.Text.Trim(), AuthenticationBox.Password.Length > 0 ? AuthenticationBox.Password : oldCredential?.AuthenticationPassword ?? "", PrivacyBox.Password.Length > 0 ? PrivacyBox.Password : oldCredential?.PrivacyPassword ?? "", CommunityBox.Password.Length > 0 ? CommunityBox.Password : oldCredential?.Community ?? "", AuthBox.SelectedIndex == 1 ? "SHA1" : "SHA256");
                if (profile.Enabled && !vm.SelectedDevice!.IsSimulation && (protocol == MonitorProtocol.SnmpV3 ? credential.Username.Length == 0 || credential.AuthenticationPassword.Length < 8 || credential.PrivacyPassword.Length < 8 : credential.Community.Length == 0)) throw new ArgumentException("请填写完整监控凭据；SNMPv3 两个口令至少 8 位。");
            }
            vm.SaveMonitor(profile, credential); DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex is ArgumentException or InvalidOperationException ? ex.Message : "监控设置保存失败，请检查本地工作区。"; }
    }
}
