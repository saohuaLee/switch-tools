using System.Text.Json;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public sealed partial class LocalWorkspace
{
    public IReadOnlyList<MonitorProfile> MonitorProfiles() => ReadAll<MonitorProfile>("SELECT payload FROM monitor_profiles");
    public void SaveMonitorProfile(MonitorProfile profile) { profile.Validate(); Save("monitor_profiles", profile.DeviceId, profile); }
    public IReadOnlyList<MonitorSample> Samples(Guid deviceId, int limit = 720)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM monitor_samples WHERE device_id=$id ORDER BY created DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$id", deviceId.ToString()); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 43200));
        using var reader = cmd.ExecuteReader(); List<MonitorSample> result = []; while (reader.Read()) result.Add(JsonSerializer.Deserialize<MonitorSample>(reader.GetString(0))!); return result;
    }
    public IReadOnlyList<MonitorSample> LatestSamples() => ReadAll<MonitorSample>("SELECT payload FROM monitor_samples AS s WHERE rowid=(SELECT rowid FROM monitor_samples WHERE device_id=s.device_id ORDER BY created DESC LIMIT 1)");
    public void SaveSample(MonitorSample sample)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); InsertEvent(db, tx, "monitor_samples", sample.Id, sample.DeviceId, sample.CollectedAt, JsonSerializer.Serialize(sample));
        using var prune = db.CreateCommand(); prune.Transaction = tx; prune.CommandText = "DELETE FROM monitor_samples WHERE created < $cutoff"; prune.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O")); prune.ExecuteNonQuery(); tx.Commit();
    }
    public IReadOnlyList<TopologyLink> TopologyLinks() => ReadAll<TopologyLink>("SELECT payload FROM topology_links");
    public void RemoveManualLink(Guid id)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var read = db.CreateCommand(); read.Transaction = tx; read.CommandText = "SELECT payload FROM topology_links WHERE id=$id"; read.Parameters.AddWithValue("$id", id.ToString());
        var link = read.ExecuteScalar() is string json ? JsonSerializer.Deserialize<TopologyLink>(json) : null;
        if (link?.Manual != true) throw new InvalidOperationException("只允许移除手工添加的连接，自动邻居按采集结果更新。");
        using var delete = db.CreateCommand(); delete.Transaction = tx; delete.CommandText = "DELETE FROM topology_links WHERE id=$id"; delete.Parameters.AddWithValue("$id", id.ToString()); delete.ExecuteNonQuery(); tx.Commit();
    }
    public void SaveTopologyLinks(Guid deviceId, IReadOnlyList<TopologyLink> links, bool replaceAutomatic)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        // 保留过期自动边，下一次观察更新同一个 ID；历史关系在 UI 标为过期。
        foreach (var link in links)
        {
            if (link.LocalDeviceId != deviceId || string.IsNullOrWhiteSpace(link.LocalPort) || link.LocalPort.Length > 100 || link.RemoteName.Length > 100 || link.RemotePort.Length > 100) throw new ArgumentException("拓扑连接字段无效。");
            using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO topology_links(id,device_id,payload) VALUES($id,$device,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload"; cmd.Parameters.AddWithValue("$id", link.Id.ToString()); cmd.Parameters.AddWithValue("$device", deviceId.ToString()); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(link)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public IReadOnlyList<NodePosition> Positions() => ReadAll<NodePosition>("SELECT payload FROM topology_positions");
    public void SavePosition(NodePosition position)
    { if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X is < 0 or > 20000 || position.Y is < 0 or > 20000) throw new ArgumentException("拓扑坐标无效。"); Save("topology_positions", position.DeviceId, position); }
    public IReadOnlyList<AlarmEvent> Alarms() => ReadAll<AlarmEvent>("SELECT payload FROM alarms ORDER BY rowid DESC LIMIT 2000");
    public void SaveAlarm(AlarmEvent alarm) => Save("alarms", alarm.Id, alarm);
    public CollectorState? Collector() => ReadAll<CollectorState>("SELECT payload FROM collector LIMIT 1").FirstOrDefault();
    public void SaveCollector(CollectorState state) => Save("collector", Guid.Empty, state);
    public bool CollectorStopRequested()
    { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT value FROM metadata WHERE key='collector_stop'"; return (string?)cmd.ExecuteScalar() == "1"; }
    public void RequestCollectorStop(bool stop)
    { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO metadata(key,value) VALUES('collector_stop',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value"; cmd.Parameters.AddWithValue("$value", stop ? "1" : "0"); cmd.ExecuteNonQuery(); }
}
