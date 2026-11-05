using NetOps.Application;

namespace NetOps.Desktop;

public sealed record CommandExplanation(string Command, string Explanation)
{
    public static CommandExplanation From(string command) => new(command, Describe(command));
    private static string Describe(string c)
    {
        if (c is "configure terminal" or "system-view") return "进入交换机的全局配置模式。";
        if (c.StartsWith("interface ")) return $"进入端口 {c[10..]} 的配置模式。";
        if (c == "shutdown") return "关闭当前端口，端口业务将中断。";
        if (c is "no shutdown" or "undo shutdown") return "启用当前端口，允许链路重新建立。";
        if (c == "switchport port-security") return "启用当前端口的 MAC 地址安全控制。";
        if (c == "no switchport port-security") return "关闭当前端口安全控制，取消源 MAC 接入限制。";
        if (c == "no switchport port-security maximum") return "将允许的 MAC 数量上限恢复为设备默认值。";
        if (c == "no switchport port-security violation") return "将违规动作恢复为设备默认值 shutdown（关闭违规端口）。";
        if (c.StartsWith("switchport port-security maximum ")) return $"设置当前端口最多允许 {c.Split(' ').Last()} 个安全 MAC 地址；超过静态绑定数量的名额可动态学习。";
        if (c.StartsWith("switchport port-security violation ")) return $"设置违规动作：{ChangeEngine.ViolationDescription(c.Split(' ').Last())}。";
        if (c.StartsWith("no switchport port-security mac-address ")) return $"删除旧的静态安全 MAC 绑定 {c.Split(' ').Last()}。";
        if (c.StartsWith("switchport port-security mac-address ")) return $"添加静态安全 MAC 绑定 {c.Split(' ').Last()}。";
        if (c is "switchport mode access" or "port link-type access") return "将当前端口设置为接入模式 Access。";
        if (c is "switchport mode trunk" or "port link-type trunk") return "将当前端口设置为干道模式 Trunk。";
        if (c.StartsWith("no mac ") || c.StartsWith("no mac-address-table ") || c.StartsWith("undo mac-address static ")) return "删除指定 MAC、VLAN 与端口的静态转发映射。";
        if (c.StartsWith("mac address-table static ") || c.StartsWith("mac-address-table static ") || c.StartsWith("mac-address static ")) return "添加目的 MAC 的静态转发出口；不限制源 MAC 接入。";
        if (c is "no description" or "undo description") return "清除当前端口的描述。";
        if (c.StartsWith("description ")) return "设置当前端口或 VLAN 的描述。";
        if (c.StartsWith("name ")) return "设置 VLAN 名称。";
        if (c.StartsWith("no vlan ") || c.StartsWith("undo vlan ")) return "删除指定的 VLAN。";
        if (c.StartsWith("vlan ")) return "创建或进入指定 VLAN 的配置模式。";
        if (c.Contains("access vlan ") || c.StartsWith("port default vlan ")) return "设置接入端口所属的 VLAN。";
        if (c.Contains("native vlan ") || c.Contains("pvid vlan ")) return "设置干道端口处理无标签报文的默认 VLAN。";
        if (c.Contains("trunk allowed vlan ") || c.Contains("trunk allow-pass vlan ") || c.Contains("trunk permit vlan ")) return c.StartsWith("undo ") ? "清除干道端口原有的允许 VLAN 列表。" : "设置干道端口允许通过的 VLAN 列表。";
        if (c is "exit" or "quit") return "退出当前配置层级，返回上一级配置模式。";
        if (c is "end" or "return") return "退出配置模式，准备回读并验证设备配置。";
        if (c is "write memory" or "save force") return "将运行配置写入设备启动配置文件，重启后保留。";
        return "执行此厂商的配置命令，请核对目标与上方变更差异。";
    }
}
