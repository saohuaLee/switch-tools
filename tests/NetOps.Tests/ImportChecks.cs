using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetOps.Domain;
using NetOps.Infrastructure;

public static class ImportChecks
{
    private static void Check(bool value) { if (!value) throw new Exception("断言失败"); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("应拒绝导入"); }
    private static MemoryStream Workbook(bool formula = false)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Add(string path, string xml) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8); writer.Write(xml); }
            Add("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='设备' sheetId='1' r:id='rId1'/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Target='worksheets/sheet1.xml'/></Relationships>");
            Add("xl/sharedStrings.xml", "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>设备名称</t></si></sst>");
            Add("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row><c r='A1' t='s'><v>0</v></c><c r='B1' t='inlineStr'><is><t>管理IP</t></is></c><c r='C1' t='inlineStr'><is><t>厂商</t></is></c></row><row><c r='A2' t='inlineStr'><is><t>测试,交换机</t></is></c><c r='B2' t='inlineStr'><is><t>192.0.2.81</t></is></c><c r='C2' t='inlineStr'>" + (formula ? "<f>EXTERNAL()</f>" : "") + "<is><t>华为</t></is></c></row></sheetData></worksheet>");
        }
        stream.Position = 0; return stream;
    }
    public static async Task Run(string root, Action<string, Action> test, Func<string, Func<Task>, Task> asyncTest)
    {
        test("Excel 设备清单支持共享文本、内联文本与逗号", () => { using var stream = Workbook(); var rows = DeviceXlsx.Parse(stream); Check(rows.Length == 1 && rows[0].Name == "测试,交换机" && rows[0].Vendor == Vendor.Huawei && !rows[0].IsSimulation); });
        test("Excel 公式拒绝执行，超大文件拒绝读取", () => { using var stream = Workbook(true); Reject(() => DeviceXlsx.Parse(stream)); using var large = new MemoryStream(new byte[2_000_001]); Reject(() => DeviceXlsx.Parse(large)); });
        test("设备批量导入事务回滚，重复地址不产生部分数据", () =>
        {
            var db = new LocalWorkspace(Path.Combine(root, "imports.db")); var first = new Device(Guid.NewGuid(), "已有", "192.0.2.91", Vendor.Cisco, "", "", DeviceRole.Switch, ""); db.SaveDevice(first);
            Reject(() => db.ImportDevices([first with { Id = Guid.NewGuid(), ManagementIp = "192.0.2.92" }, first with { Id = Guid.NewGuid() }])); Check(db.GetDevices().Count == 1);
            db.ImportDevices([first with { Id = Guid.NewGuid(), ManagementIp = "192.0.2.92" }]); Check(db.GetDevices().Count == 2 && db.GetState(db.GetDevices()[1].Id)!.Ports.Length == 24);
        });
        test("发现范围最多 256 个地址，拒绝倒置和 IPv6", () => { Check(DeviceDiscovery.Addresses("192.0.2.0", "192.0.2.255").Length == 256); Reject(() => DeviceDiscovery.Addresses("192.0.2.0", "192.0.3.0")); Reject(() => DeviceDiscovery.Addresses("192.0.2.2", "192.0.2.1")); Reject(() => DeviceDiscovery.Addresses("::1", "::1")); });
        await asyncTest("发现仅检查本机回环端点，支持取消", async () =>
        {
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start(); var port = ((IPEndPoint)server.LocalEndpoint).Port;
            var found = await DeviceDiscovery.ScanAsync("127.0.0.1", "127.0.0.1", port, null, CancellationToken.None); Check(found.Length == 1 && found[0].Port == port);
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { await DeviceDiscovery.ScanAsync("127.0.0.1", "127.0.0.1", port, null, cancel.Token); throw new Exception("未取消"); } catch (OperationCanceledException) { }
        });
    }
}
