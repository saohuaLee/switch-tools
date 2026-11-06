using NetOps.Domain;

namespace NetOps.Application;

/// <summary>明确的最终接入名单，保留项与新输入分别统计，不把新增输入当成完整替换。</summary>
public sealed record PortMacList(MacBinding[] Original, MacBinding[] Kept, MacBinding[] Added, MacBinding[] Removed, MacBinding[] Target, int InputCount)
{
    public static PortMacList Prepare(DeviceState state, string portName, IEnumerable<string> keep, string input)
    {
        var port = state.Ports.SingleOrDefault(p => p.Name == portName) ?? throw new ArgumentException("找不到端口，请重新读取。");
        var original = MacRules.Bindings(state).Where(b => b.PortName == portName && b.Kind == MacBindingKind.SecureSource).ToArray();
        var retained = keep.Select(MacRules.Normalize).Distinct().ToArray();
        if (retained.Any(a => !original.Any(b => b.Address == a))) throw new ArgumentException("原有名单已经变化，请重新读取端口。");
        var lines = input.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 64) throw new ArgumentException("每次最多输入 64 条地址。");
        var entered = lines.Select((line, index) =>
        {
            try { return MacRules.Normalize(line); }
            catch (ArgumentException ex) { throw new ArgumentException($"新增地址第 {index + 1} 行不正确：{ex.Message}"); }
        }).Distinct().ToArray();
        var target = retained.Concat(entered).Distinct().Select(a => new MacBinding(a, port.Pvid, port.Name, MacBindingKind.SecureSource)).ToArray();
        if (target.Length > 64) throw new ArgumentException("应用后的名单最多 64 个地址，请减少名单后再确认。");
        return new(original, original.Intersect(target).ToArray(), target.Except(original).ToArray(), original.Except(target).ToArray(), target, lines.Length);
    }

    public ChangeRequest BuildRequest(string portName, bool automatic, string maximumText, string violation)
    {
        if (Target.Length == 0) return new(RemoveMacBindings: Removed);
        var maximum = Target.Length;
        if (!automatic && (!int.TryParse(maximumText, out maximum) || maximum < 1)) throw new ArgumentException("允许设备数量请填写正整数，或改用自动数量。");
        if (maximum < Target.Length) throw new ArgumentException($"名单中保留 {Kept.Length} 个原有地址，新增 {Added.Length} 个，共 {Target.Length} 个；手动上限 {maximum} 不够。可点击“按名单自动设置”，或取消保留不再使用的原地址。");
        return new(AddMacBindings: Added, RemoveMacBindings: Removed, SecuritySettings: [new(portName, maximum, violation)]);
    }
}
