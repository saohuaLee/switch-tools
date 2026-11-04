using System.Globalization;
using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public sealed record SnmpCell(string Oid, string Text, double? Number = null, byte[]? Bytes = null);
public interface ISnmpSource
{
    Task<IReadOnlyList<SnmpCell>> GetAsync(string[] oids, CancellationToken ct);
    Task<IReadOnlyList<SnmpCell>> WalkAsync(string root, CancellationToken ct);
}
public sealed class SnmpSource(Device device, MonitorProfile profile, MonitorCredential credential) : ISnmpSource
{
    private readonly IPEndPoint endpoint = new(IPAddress.Parse(device.ManagementIp), profile.Port);
    private readonly VersionCode version = profile.Protocol == MonitorProtocol.SnmpV3 ? VersionCode.V3 : VersionCode.V2;
    private IPrivacyProvider privacy = DefaultPrivacyProvider.DefaultPair;
    private ISnmpMessage? report;
    private int requests;
    private async Task<ISnmpMessage> RequestAsync(string[] oids, bool bulk, CancellationToken ct)
    {
        if (++requests > 160) throw new InvalidOperationException("本轮 SNMP 请求达到上限，剩余数据未采集。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var token = deadline.Token;
        if (version == VersionCode.V3 && report is null)
        {
            if (string.IsNullOrWhiteSpace(credential.Username) || credential.AuthenticationPassword.Length < 8 || credential.PrivacyPassword.Length < 8) throw new ArgumentException("SNMPv3 需要用户名及至少 8 位认证、加密口令。");
            // 仅为用户明确选择的旧设备配置保留 SHA1，默认使用 SHA256。
#pragma warning disable CS0618
            IAuthenticationProvider auth = credential.Authentication == "SHA1" ? new SHA1AuthenticationProvider(new OctetString(credential.AuthenticationPassword)) : new SHA256AuthenticationProvider(new OctetString(credential.AuthenticationPassword));
#pragma warning restore CS0618
            privacy = new AESPrivacyProvider(new OctetString(credential.PrivacyPassword), auth);
            report = await Messenger.GetNextDiscovery(SnmpType.GetRequestPdu).GetResponseAsync(endpoint, token);
        }
        var identity = new OctetString(version == VersionCode.V3 ? credential.Username : credential.Community);
        if (version == VersionCode.V2 && string.IsNullOrWhiteSpace(credential.Community)) throw new ArgumentException("请填写只读 SNMP community。");
        var variables = oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList();
        ISnmpMessage Build() => version == VersionCode.V3 ? bulk
            ? new GetBulkRequestMessage(version, Messenger.NextMessageId, Messenger.NextRequestId, identity, OctetString.Empty, 0, 16, variables, privacy, Messenger.MaxMessageSize, report!)
            : new GetRequestMessage(version, Messenger.NextMessageId, Messenger.NextRequestId, identity, OctetString.Empty, variables, privacy, Messenger.MaxMessageSize, report!)
            : bulk ? new GetBulkRequestMessage(Messenger.NextRequestId, version, identity, 0, 16, variables) : new GetRequestMessage(Messenger.NextRequestId, version, identity, variables);
        var response = await Build().GetResponseAsync(endpoint, token);
        if (response is ReportMessage && response.Pdu().Variables.FirstOrDefault()?.Id.ToString() == "1.3.6.1.6.3.15.1.1.2.0")
        { report = response; response = await Build().GetResponseAsync(endpoint, token); }
        if (response is ReportMessage) throw new InvalidOperationException("设备已响应，但 SNMPv3 认证、加密或用户权限未通过。");
        if (response.Pdu().ErrorStatus.ToInt32() != 0) throw new InvalidOperationException("设备已响应，但 SNMP 指标读取被拒绝。");
        return response;
    }
    private static SnmpCell? Cell(Variable v)
    {
        if (v.Data.TypeCode is SnmpType.NoSuchInstance or SnmpType.NoSuchObject or SnmpType.EndOfMibView) return null;
        var text = v.Data.ToString() ?? ""; double? number = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        // TimeTicks.ToString 可能为格式化时长，使用原始 tick 数。
        if (v.Data is TimeTicks ticks) number = ticks.ToUInt32();
        return new(v.Id.ToString(), text, number, v.Data is OctetString octets ? octets.GetRaw() : null);
    }
    public async Task<IReadOnlyList<SnmpCell>> GetAsync(string[] oids, CancellationToken ct) => (await RequestAsync(oids, false, ct)).Pdu().Variables.Select(Cell).OfType<SnmpCell>().ToArray();
    public async Task<IReadOnlyList<SnmpCell>> WalkAsync(string root, CancellationToken ct)
    {
        var cursor = root; List<SnmpCell> result = []; var seen = new HashSet<string>();
        for (var page = 0; page < 64; page++)
        {
            var response = await RequestAsync([cursor], true, ct); var cells = response.Pdu().Variables;
            if (cells.Count == 0) return result;
            foreach (var variable in cells)
            {
                var cell = Cell(variable);
                if (cell is null || !cell.Oid.StartsWith(root + ".", StringComparison.Ordinal)) return result;
                if (!seen.Add(cell.Oid)) throw new InvalidOperationException("SNMP 表未向前推进，已停止本轮采集。");
                result.Add(cell); cursor = cell.Oid;
            }
        }
        throw new InvalidOperationException("SNMP 表超过 1024 项，本轮未完成采集。");
    }
}
