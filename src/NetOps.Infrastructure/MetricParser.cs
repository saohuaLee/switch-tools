using NetOps.Domain;

namespace NetOps.Infrastructure;

public static class MetricParser
{
    public static MetricReading[] Parse(IReadOnlyList<SnmpCell> data, MonitorProfile profile, DateTimeOffset now)
    {
        List<MetricReading> result = [];
        void Add(string key, string label, double? value, string unit, string source, int? member = null)
        {
            var valid = value is double v && double.IsFinite(v) && (unit == "%" ? v is >= 0 and <= 100 : unit != "℃" || v is >= -100 and <= 250);
            result.Add(new(key, label, valid ? value : null, unit, valid ? CollectionQuality.Valid : CollectionQuality.Unsupported, now, source, member));
        }
        if (profile.CpuOid.Length > 0) Add("cpu.custom", "CPU（自定义指标）", data.FirstOrDefault(d => d.Oid == profile.CpuOid)?.Number, "%", profile.CpuOid, profile.MetricMemberId);
        else foreach (var cell in data.Where(d => d.Oid.StartsWith("1.3.6.1.2.1.25.3.3.1.2."))) Add("cpu." + cell.Oid.Split('.').Last(), "处理器 " + cell.Oid.Split('.').Last() + " · 1 分钟平均", cell.Number, "%", "HOST-RESOURCES-MIB");
        if (profile.MemoryOid.Length > 0) Add("memory.custom", "内存使用率（自定义指标）", data.FirstOrDefault(d => d.Oid == profile.MemoryOid)?.Number, "%", profile.MemoryOid, profile.MetricMemberId);
        else
        {
            const string root = "1.3.6.1.2.1.25.2.3.1.";
            foreach (var cell in data.Where(d => d.Oid.StartsWith(root + "2.") && d.Text == "1.3.6.1.2.1.25.2.1.2"))
            {
                var index = cell.Oid[(root.Length + 2)..]; double? Number(int column) => data.FirstOrDefault(d => d.Oid == root + column + "." + index)?.Number;
                var size = Number(5); var used = Number(6); var unit = Number(4); var name = data.FirstOrDefault(d => d.Oid == root + "3." + index)?.Text ?? "物理内存";
                Add("memory." + index, name, size is > 0 && used is >= 0 && used <= size ? used / size * 100 : null, "%", "HOST-RESOURCES-MIB");
                if (size is > 0 && unit is > 0) { Add("ram-total." + index, name + " · 总量", size * unit / 1048576, " MB", "HOST-RESOURCES-MIB"); Add("ram-used." + index, name + " · 已用", used * unit / 1048576, " MB", "HOST-RESOURCES-MIB"); }
            }
        }
        if (profile.TemperatureOid.Length > 0) Add("temperature.custom", "温度（自定义指标，℃）", data.FirstOrDefault(d => d.Oid == profile.TemperatureOid)?.Number, "℃", profile.TemperatureOid, profile.MetricMemberId);
        else
        {
            const string root = "1.3.6.1.2.1.99.1.1.1.";
            foreach (var cell in data.Where(d => d.Oid.StartsWith(root + "1.") && d.Number == 8))
            {
                var index = cell.Oid[(root.Length + 2)..]; double? Number(int column) => data.FirstOrDefault(d => d.Oid == root + column + "." + index)?.Number;
                var name = data.FirstOrDefault(d => d.Oid == "1.3.6.1.2.1.47.1.1.1.1.7." + index)?.Text ?? "温度传感器 " + index;
                var scale = Number(2); var precision = Number(3); var value = Number(4);
                Add("temperature." + index, name, Number(5) == 1 && scale is >= 1 and <= 17 && precision is >= -8 and <= 9 ? value * Math.Pow(10, 3 * (scale.Value - 9) - precision.Value) : null, "℃", "ENTITY-SENSOR-MIB");
            }
        }
        foreach (var (prefix, label, unit) in new[] { ("cpu", "CPU", "%"), ("memory", "内存使用率", "%"), ("temperature", "温度", "℃") }) if (!result.Any(m => m.Key.StartsWith(prefix))) Add(prefix, label, null, unit, "设备未提供标准指标，可设置厂商 OID 或使用 SSH 监控");
        return result.ToArray();
    }
}
