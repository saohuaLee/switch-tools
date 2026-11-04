using NetOps.Domain;

namespace NetOps.Application;

public static class MonitorRules
{
    public static MonitorSample Freshness(MonitorSample sample, MonitorProfile profile, DateTimeOffset now)
    {
        if (profile.Maintenance) return sample with { Reachability = Reachability.Maintenance };
        if (now - sample.CollectedAt <= TimeSpan.FromSeconds(Math.Max(90, profile.IntervalSeconds * 3))) return sample;
        return sample with { Reachability = Reachability.Unknown, Error = "采集数据已过期，请检查后台采集器。", Metrics = sample.Metrics.Select(m => m with { Quality = CollectionQuality.Stale }).ToArray() };
    }
    public static Reachability Reachable(bool responded, bool anyReachable, int failures, MonitorProfile profile) => profile.Maintenance ? Reachability.Maintenance : responded || anyReachable ? Reachability.Online : failures >= profile.FailureThreshold ? Reachability.Offline : Reachability.Suspect;
    public static AlarmEvent[] Evaluate(Device device, MonitorProfile profile, IReadOnlyList<MonitorSample> latest, IReadOnlyList<AlarmEvent> existing, DateTimeOffset now)
    {
        if (latest.Count == 0 || now - latest[0].CollectedAt > TimeSpan.FromSeconds(Math.Max(90, profile.IntervalSeconds * 3))) return [];
        List<AlarmEvent> changes = [];
        void Rule(string key, string title, bool trigger, bool recover, string detail)
        {
            var old = existing.FirstOrDefault(a => a.DeviceId == device.Id && a.Key == key && a.RecoveredAt is null);
            if (old is null && trigger) changes.Add(new(Guid.NewGuid(), device.Id, key, title, detail, now, IsSimulation: device.IsSimulation, DeviceName: device.Name));
            else if (old is not null && recover) changes.Add(old with { RecoveredAt = now });
        }
        if (profile.Maintenance) return [];
        Rule("offline", "设备管理不可达", latest[0].Reachability == Reachability.Offline, latest[0].Reachability == Reachability.Online, "连续探测失败；表示从本采集端不可达，不代表设备已断电。");
        foreach (var metric in latest[0].Metrics)
        {
            var threshold = metric.Unit == "%" && metric.Key.StartsWith("cpu") ? profile.CpuThreshold : metric.Unit == "%" && metric.Key.StartsWith("memory") ? profile.MemoryThreshold : metric.Unit == "℃" ? profile.TemperatureThreshold : null;
            if (threshold is not double limit) continue;
            var readings = latest.Take(profile.AlarmSamples).Select(s => s.Metrics.FirstOrDefault(m => m.Key == metric.Key)).ToArray();
            var sustained = readings.Length == profile.AlarmSamples && latest.Take(profile.AlarmSamples).All(s => now - s.CollectedAt <= TimeSpan.FromSeconds(profile.IntervalSeconds * profile.AlarmSamples * 2)) && readings.All(m => m?.Quality == CollectionQuality.Valid && m.Value >= limit);
            var recover = metric.Quality == CollectionQuality.Valid && metric.Value < limit - (metric.Unit == "℃" ? 3 : 5);
            Rule(metric.Key, metric.Label + "持续超限", sustained, recover, $"连续 {profile.AlarmSamples} 次达到阈值 {limit:0.#}{metric.Unit}；当前 {metric.ValueLabel}。");
        }
        return changes.ToArray();
    }
}
