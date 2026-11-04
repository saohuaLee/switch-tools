using System.Text.Json.Serialization;

namespace NetOps.Domain;

public enum Reachability { Unknown, Online, Suspect, Offline, Maintenance }
public enum CollectionQuality { Valid, Unsupported, Failed, Stale }
public enum MonitorProtocol { SnmpV3, SnmpV2, Ssh }
public sealed record MetricReading(string Key, string Label, double? Value, string Unit, CollectionQuality Quality,
    DateTimeOffset CollectedAt, string Source, int? MemberId = null)
{
    [JsonIgnore] public string ValueLabel => Quality == CollectionQuality.Valid && Value is double v ? $"{v:0.#}{Unit}" : Quality switch { CollectionQuality.Stale => "已过期", CollectionQuality.Failed => "采集失败", _ => "未提供" };
}
public sealed record MonitorSample(Guid Id, Guid DeviceId, DateTimeOffset CollectedAt, Reachability Reachability,
    MetricReading[] Metrics, string? Error = null, double? LatencyMs = null, long? UptimeSeconds = null,
    int ConsecutiveFailures = 0, bool IsSimulation = false, bool TopologyCollected = false, string? SystemName = null, string? ChassisIdentity = null,
    StackMemberState[]? Members = null, DateTimeOffset? MembersCapturedAt = null);
public sealed record MonitorProfile(Guid DeviceId, bool Enabled = false, MonitorProtocol Protocol = MonitorProtocol.SnmpV3,
    int Port = 161, string? CredentialReference = null, int IntervalSeconds = 60, int FailureThreshold = 3,
    double CpuThreshold = 85, double MemoryThreshold = 90, double? TemperatureThreshold = null,
    int AlarmSamples = 3, bool Maintenance = false, string CpuOid = "", string MemoryOid = "", string TemperatureOid = "", int? MetricMemberId = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Protocol) || Port is < 1 or > 65535 || IntervalSeconds is < 15 or > 3600 || FailureThreshold is < 2 or > 10 || AlarmSamples is < 2 or > 10) throw new ArgumentException("监控端口、轮询间隔（15–3600 秒）或连续次数（2–10）无效。");
        if (!double.IsFinite(CpuThreshold) || !double.IsFinite(MemoryThreshold) || TemperatureThreshold is double temp && !double.IsFinite(temp) || CpuThreshold is < 1 or > 100 || MemoryThreshold is < 1 or > 100 || TemperatureThreshold is < -40 or > 200) throw new ArgumentException("CPU/内存阈值为 1–100%，温度阈值为 -40–200℃。");
        foreach (var oid in new[] { CpuOid, MemoryOid, TemperatureOid }) if (oid.Length > 160 || oid.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(oid, @"\A[0-9]+(?:\.[0-9]+)+\z")) throw new ArgumentException("高级指标 OID 必须为数字点分格式。");
    }
}
public sealed record MonitorCredential(string Username, string AuthenticationPassword, string PrivacyPassword, string Community = "", string Authentication = "SHA256")
{
    public override string ToString() => "受保护的监控凭据";
}
public sealed record TopologyLink(Guid Id, Guid LocalDeviceId, string LocalPort, Guid? RemoteDeviceId, string RemoteIdentity,
    string RemoteName, string RemotePort, string Source, DateTimeOffset ObservedAt, int TtlSeconds = 600, bool Manual = false);
public sealed record NodePosition(Guid DeviceId, double X, double Y);
public sealed record AlarmEvent(Guid Id, Guid DeviceId, string Key, string Title, string Detail, DateTimeOffset OpenedAt,
    DateTimeOffset? RecoveredAt = null, bool Acknowledged = false, bool IsSimulation = false, string? DeviceName = null)
{
    [JsonIgnore] public string StateLabel => RecoveredAt is not null ? "已恢复" : Acknowledged ? "已确认" : "待处理";
    [JsonIgnore] public string TimeLabel => OpenedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
}
public sealed record CollectorState(DateTimeOffset Heartbeat, bool StopRequested = false, string? Error = null);
