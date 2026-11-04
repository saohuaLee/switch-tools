using Microsoft.VisualBasic.FileIO;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public static class DeviceCsv
{
    // 导入结构化字段，不接受 CSV 中的密码、令牌或脚本。
    public static Device[] Parse(TextReader reader)
    {
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new ArgumentException("CSV 内容为空。");
        var names = header.Select(h => h.Trim().TrimStart('\uFEFF')).ToArray();
        int Column(string chinese, string english) => Array.FindIndex(names, h => h.Equals(chinese, StringComparison.OrdinalIgnoreCase) || h.Equals(english, StringComparison.OrdinalIgnoreCase));
        var nameCol = Column("设备名称", "Name"); var ipCol = Column("管理IP", "ManagementIp"); var vendorCol = Column("厂商", "Vendor"); var modelCol = Column("型号", "Model"); var firmwareCol = Column("固件", "Firmware"); var locationCol = Column("分组", "Location"); var roleCol = Column("角色", "Role"); var simulationCol = Column("模拟", "Simulation");
        if (nameCol < 0 || ipCol < 0 || vendorCol < 0) throw new ArgumentException("CSV 至少包含：设备名称、管理IP、厂商。可选：型号、固件、分组、角色、模拟。");
        List<Device> devices = [];
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields()!; string Get(int i) => i >= 0 && i < row.Length ? row[i] : "";
            if (devices.Count >= 1000) throw new ArgumentException("每次最多导入 1000 台设备。");
            var vendorText = Get(vendorCol);
            var candidates = Enum.GetValues<Vendor>().Where(v => v.ToString().Equals(vendorText, StringComparison.OrdinalIgnoreCase) || Labels.Vendor(v) == vendorText || (v == Vendor.Cisco && vendorText == "Cisco") || (v == Vendor.H3C && vendorText == "华三") || (v == Vendor.TPLink && vendorText.Equals("TP-Link", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length != 1) throw new ArgumentException($"第 {devices.Count + 2} 行厂商无法识别，请使用华为、H3C、Cisco、锐捷等名称。");
            var roleText = Get(roleCol); var role = roleText is "路由器" or "Router" ? DeviceRole.Router : roleText is "防火墙" or "Firewall" ? DeviceRole.Firewall : DeviceRole.Switch;
            var simulation = Get(simulationCol) is "是" or "true" or "True" or "1";
            var device = new Device(Guid.NewGuid(), Get(nameCol), Get(ipCol), candidates[0], Get(modelCol), Get(firmwareCol), role, Get(locationCol), simulation);
            device.Validate(); devices.Add(device);
        }
        if (devices.Count == 0) throw new ArgumentException("CSV 没有设备数据行。");
        if (devices.GroupBy(d => d.ManagementIp).Any(g => g.Count() > 1)) throw new ArgumentException("CSV 包含重复管理地址，请核对后导入。");
        return devices.ToArray();
    }
}
