using System.Text.RegularExpressions;

namespace NetOps.Infrastructure;

public static partial class PortSelections
{
    public static HashSet<string> Parse(string text, IReadOnlyList<string> ports)
    {
        var prefixes = ports.Select(p => p[..Math.Max(0, p.LastIndexOf('/') + 1)]).Distinct().ToArray();
        HashSet<string> selected = [];
        foreach (var token in text.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = FullRange().Match(token);
            string prefix; string numbers;
            if (match.Success) { prefix = match.Groups[1].Value; numbers = match.Groups[2].Value; }
            else { if (prefixes.Length != 1) throw new ArgumentException("设备包含多个槽位或接口类型，请输入完整范围，例如 GigabitEthernet1/0/1-8。"); prefix = prefixes[0]; numbers = token; }
            foreach (var n in VlanRanges.Parse(numbers))
            {
                var name = prefix + n;
                if (!ports.Contains(name)) throw new ArgumentException($"端口 {name} 不存在，请核对范围。");
                selected.Add(name);
            }
        }
        if (selected.Count == 0) throw new ArgumentException("请输入端口范围，例如 1-8。");
        return selected;
    }
    [GeneratedRegex(@"^(.*/)([0-9]+(?:-[0-9]+)?)$")] private static partial Regex FullRange();
}
