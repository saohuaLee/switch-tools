using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;
public partial class ImportDevicesWindow : Window
{
    private readonly MainViewModel vm;
    private Device[] rows = [];
    public ImportDevicesWindow(MainViewModel vm) { InitializeComponent(); this.vm = vm; }
    private void ChooseFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择交换机清单", Filter = "设备清单|*.csv;*.xlsx|CSV 文件|*.csv|Excel 工作簿|*.xlsx" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 2_000_000) throw new ArgumentException("清单文件最多 2 MB，请拆分后再添加。");
            Device[] parsed;
            if (Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) { using var stream = File.OpenRead(dialog.FileName); parsed = DeviceXlsx.Parse(stream); }
            else { using var reader = File.OpenText(dialog.FileName); parsed = DeviceCsv.Parse(reader); }
            rows = parsed; PreviewGrid.ItemsSource = rows; ImportButton.IsEnabled = rows.Length > 0; FileLabel.Text = $"已读取 {Path.GetFileName(dialog.FileName)}，共 {rows.Length} 台设备。请核对真实 / 练习标记。"; StatusText.Text = "当前只是预览，还没有添加设备。";
        }
        catch (Exception ex) { rows = []; PreviewGrid.ItemsSource = rows; ImportButton.IsEnabled = false; StatusText.Text = ex is ArgumentException ? ex.Message : "无法读取清单，请检查文件格式；没有添加任何设备。"; }
    }
    private void SaveExampleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "保存交换机清单填写示例", Filter = "CSV 文件|*.csv", FileName = "交换机清单填写示例.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, "设备名称,管理IP,厂商,分组,角色,模拟\r\n办公室练习交换机,192.0.2.51,思科,办公室,交换机,是\r\n", new UTF8Encoding(true)); StatusText.Text = "示例已保存。模拟列“是”表示练习，“否”表示真实设备；请按实际情况填写。"; }
        catch (IOException) { StatusText.Text = "无法保存示例，请换一个有写入权限的位置。"; }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    { try { vm.ImportDevices(rows); DialogResult = true; } catch (Exception ex) { StatusText.Text = ex is ArgumentException ? ex.Message : "添加失败，没有保存部分数据。请核对是否与已有设备重复。"; } }
}
