using System.Security.Cryptography;
using System.Text;
using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public sealed class MonitorScheduler(LocalWorkspace store, IMonitorProbe probe)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> topologyReads = [];
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var devices = store.GetDevices().Where(d => !d.Archived).ToDictionary(d => d.Id);
        var profiles = store.MonitorProfiles().Where(p => p.Enabled && devices.ContainsKey(p.DeviceId)).ToArray();
        var latest = store.LatestSamples().ToDictionary(s => s.DeviceId);
        var due = profiles.Where(p => !latest.TryGetValue(p.DeviceId, out var old) || now - old.CollectedAt >= TimeSpan.FromSeconds(p.IntervalSeconds)).ToArray();
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (profile, token) =>
        {
            var device = devices[profile.DeviceId]; var previous = latest.GetValueOrDefault(device.Id);
            if (profile.Maintenance) { store.SaveSample(new(Guid.NewGuid(), device.Id, now, Reachability.Maintenance, [], IsSimulation: device.IsSimulation)); return; }
            var topology = !topologyReads.TryGetValue(device.Id, out var lastTopology) || now - lastTopology >= TimeSpan.FromMinutes(5);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(50));
            ProbeResult result;
            try { result = await probe.ReadAsync(device, profile, topology, timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = new(false, false, [], [], "采集超过本轮时限，状态未知。"); }
            var continuous = previous is not null && now - previous.CollectedAt <= TimeSpan.FromSeconds(Math.Max(90, profile.IntervalSeconds * 3));
            var failures = result.Responded || result.Reachable ? 0 : (continuous ? previous!.ConsecutiveFailures : 0) + 1;
            var state = MonitorRules.Reachable(result.Responded, result.Reachable, failures, profile);
            if (result.Error == "采集超过本轮时限，状态未知。") { state = Reachability.Unknown; failures = 0; }
            var sample = new MonitorSample(Guid.NewGuid(), device.Id, now, state, result.Metrics, result.Error, result.LatencyMs, result.Uptime, failures, device.IsSimulation, result.TopologyCollected, result.SystemName ?? previous?.SystemName, result.ChassisIdentity ?? previous?.ChassisIdentity, result.Members ?? previous?.Members, result.Members is not null ? now : previous?.MembersCapturedAt);
            // 采集中用户停用、归档或改变参数时丢弃旧结果。
            if (store.GetDevices().FirstOrDefault(d => d.Id == device.Id)?.Archived != false || store.MonitorProfiles().FirstOrDefault(p => p.DeviceId == device.Id) != profile) return;
            store.SaveSample(sample);
            if (result.TopologyCollected)
            {
                var identities = store.LatestSamples(); var links = result.Links.Select(l => l with { RemoteDeviceId = TopologyDiscovery.Resolve(l.RemoteIdentity, l.RemoteName, devices.Values.ToArray(), identities) }).ToArray();
                store.SaveTopologyLinks(device.Id, links, true); lock (topologyReads) topologyReads[device.Id] = now;
            }
            foreach (var alarm in MonitorRules.Evaluate(device, profile, store.Samples(device.Id, 10), store.Alarms(), now)) store.SaveAlarm(alarm);
        });
        RefreshDemoLinks(store, now);
    }
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !store.CollectorStopRequested())
        {
            try
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                store.SaveCollector(new(DateTimeOffset.UtcNow)); var tick = TickAsync(DateTimeOffset.UtcNow, cycle.Token);
                while (!tick.IsCompleted)
                {
                    await Task.WhenAny(tick, Task.Delay(TimeSpan.FromSeconds(5), ct));
                    store.SaveCollector(new(DateTimeOffset.UtcNow));
                    if (store.CollectorStopRequested()) cycle.Cancel();
                }
                try { await tick; } catch (OperationCanceledException) when (store.CollectorStopRequested()) { break; }
                store.SaveCollector(new(DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { store.SaveCollector(new(DateTimeOffset.UtcNow, Error: "本轮采集未完成，保留历史数据；下轮重试。")); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        store.SaveCollector(new(DateTimeOffset.UtcNow, true));
    }
    public static void SeedSimulation(LocalWorkspace store)
    {
        foreach (var device in store.GetDevices().Where(d => d.IsSimulation && !d.Archived))
        {
            if (store.MonitorProfiles().Any(p => p.DeviceId == device.Id)) continue;
            store.SaveMonitorProfile(new(device.Id, true, IntervalSeconds: 60));
            if (store.Samples(device.Id, 1).Count > 0) continue;
            var now = DateTimeOffset.UtcNow;
            for (var i = 30; i >= 1; i--)
            { var time = now.AddMinutes(-i); var data = MonitorProbe.Demo(device, time, false); store.SaveSample(new(Guid.NewGuid(), device.Id, time, Reachability.Online, data.Metrics, LatencyMs: data.LatencyMs, UptimeSeconds: data.Uptime, IsSimulation: true)); }
        }
        RefreshDemoLinks(store, DateTimeOffset.UtcNow);
    }
    private static void RefreshDemoLinks(LocalWorkspace store, DateTimeOffset now)
    {
        var devices = store.GetDevices().Where(d => d.IsSimulation && !d.Archived).Take(3).ToArray(); if (devices.Length < 3) return;
        foreach (var remote in devices.Skip(1))
        {
            var id = TopologyDiscovery.StableId(devices[0].Id, "GigabitEthernet1/0/24", remote.Id.ToString(), "GigabitEthernet1/0/24", "模拟");
            store.SaveTopologyLinks(devices[0].Id, [new(id, devices[0].Id, remote.Id == devices[1].Id ? "GigabitEthernet1/0/23" : "GigabitEthernet1/0/24", remote.Id, remote.Id.ToString(), remote.Name, "GigabitEthernet1/0/24", "模拟示例", now)], true);
        }
    }
}
public static class CollectorHost
{
    public static string MutexName(string root) => "Local\\NetOpsCollector_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    public static void Run(string root, CancellationToken ct)
    {
        using var mutex = new Mutex(false, MutexName(root)); bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;
        try { var store = new LocalWorkspace(Path.Combine(root, "workspace.db")); var vault = new DpapiVault(Path.Combine(root, "vault")); new MonitorScheduler(store, new MonitorProbe(vault, new SessionFactory(vault))).RunAsync(ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally { mutex.ReleaseMutex(); }
    }
}
