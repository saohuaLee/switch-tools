using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Diagnostics;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NetOps.Application;
using NetOps.Desktop;
using NetOps.Domain;
using NetOps.Infrastructure;

public static class ObservabilityChecks
{
    private static void Check(bool value, string message = "断言失败") { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return; } throw new Exception("操作应拒绝"); }
    public static async Task Run(string root, Action<string, Action> test, Func<string, Func<Task>, Task> asyncTest)
    {
        var db = new LocalWorkspace(Path.Combine(root, "observability.db")); var vault = new DpapiVault(Path.Combine(root, "observability-vault")); DemoWorkspace.Seed(db);
        var device = db.GetDevices()[0]; var now = DateTimeOffset.UtcNow; var profile = new MonitorProfile(device.Id, true, TemperatureThreshold: 70);
        MetricReading Metric(double? n, CollectionQuality quality = CollectionQuality.Valid) => new("cpu.1", "CPU", n, "%", quality, now, "测试来源");
        MonitorSample Sample(double? n, DateTimeOffset? time = null, CollectionQuality quality = CollectionQuality.Valid) => new(Guid.NewGuid(), device.Id, time ?? now, Reachability.Online, [Metric(n) with { CollectedAt = time ?? now }]);
        test("监控配置边界与非有限数拒绝", () => { Reject(() => (profile with { IntervalSeconds = 1 }).Validate()); Reject(() => (profile with { CpuOid = "1.2;reboot" }).Validate()); Reject(() => (profile with { CpuThreshold = 101 }).Validate()); Reject(() => (profile with { CpuThreshold = double.NaN }).Validate()); });
        test("监控数据与旧配置状态独立持久化", () => { var before = ChangeEngine.Hash(db.GetState(device.Id)!); db.SaveMonitorProfile(profile); db.SaveSample(Sample(25)); Check(db.Samples(device.Id).Single().Metrics[0].Value == 25); Check(ChangeEngine.Hash(db.GetState(device.Id)!) == before); Check(new LocalWorkspace(Path.Combine(root, "observability.db")).MonitorProfiles().Single() == profile); });
        test("未知、维护、疑似和不可达分别判断", () => { Check(MonitorRules.Reachable(false, false, 1, profile) == Reachability.Suspect); Check(MonitorRules.Reachable(false, false, 3, profile) == Reachability.Offline); Check(MonitorRules.Reachable(false, true, 4, profile) == Reachability.Online); Check(MonitorRules.Reachable(true, true, 0, profile with { Maintenance = true }) == Reachability.Maintenance); });
        test("采集端停止后数据过期，不把旧成功当作实时在线", () => { var old = Sample(0, now.AddMinutes(-10)); var stale = MonitorRules.Freshness(old, profile, now); Check(stale.Reachability == Reachability.Unknown && stale.Metrics[0].Quality == CollectionQuality.Stale); Check(stale.Metrics[0].ValueLabel == "已过期"); Check(MonitorRules.Freshness(Sample(0), profile, now).Metrics[0].ValueLabel == "0%"); });
        test("资源持续超限、确认与回差恢复", () =>
        {
            var recent = new[] { Sample(90), Sample(92, now.AddMinutes(-1)), Sample(91, now.AddMinutes(-2)) };
            Check(MonitorRules.Evaluate(device, profile, recent.Take(2).ToArray(), [], now).Length == 0);
            var opened = MonitorRules.Evaluate(device, profile, recent, [], now); Check(opened.Length == 1 && opened[0].RecoveredAt is null && opened[0].DeviceName == device.Name); db.SaveAlarm(opened[0] with { Acknowledged = true });
            Check(MonitorRules.Evaluate(device, profile, recent, db.Alarms(), now).Length == 0);
            var recovered = MonitorRules.Evaluate(device, profile, [Sample(70)], db.Alarms(), now); Check(recovered.Single().RecoveredAt is not null && recovered[0].Acknowledged);
        });
        test("缺失、过期和间断历史不触发资源阈值", () => { Check(MonitorRules.Evaluate(device, profile, [Sample(null, quality: CollectionQuality.Unsupported), Sample(99), Sample(99)], [], now).Length == 0); Check(MonitorRules.Evaluate(device, profile, [Sample(99), Sample(99, now.AddHours(-2)), Sample(99, now.AddHours(-3))], [], now).Length == 0); });
        test("温度缩放、传感器状态与内存口径", () =>
        {
            var cells = new List<SnmpCell> { new("1.3.6.1.2.1.25.3.3.1.2.1", "18", 18), new("1.3.6.1.2.1.25.2.3.1.2.1", "1.3.6.1.2.1.25.2.1.2"), new("1.3.6.1.2.1.25.2.3.1.3.1", "物理内存"), new("1.3.6.1.2.1.25.2.3.1.4.1", "1024", 1024), new("1.3.6.1.2.1.25.2.3.1.5.1", "1000", 1000), new("1.3.6.1.2.1.25.2.3.1.6.1", "450", 450), new("1.3.6.1.2.1.99.1.1.1.1.10", "8", 8), new("1.3.6.1.2.1.99.1.1.1.2.10", "9", 9), new("1.3.6.1.2.1.99.1.1.1.3.10", "1", 1), new("1.3.6.1.2.1.99.1.1.1.4.10", "425", 425), new("1.3.6.1.2.1.99.1.1.1.5.10", "1", 1), new("1.3.6.1.2.1.47.1.1.1.1.7.10", "成员1主板传感器") };
            var parsed = MetricParser.Parse(cells, profile, now); Check(parsed.Single(m => m.Key == "temperature.10").Value == 42.5); Check(parsed.Single(m => m.Key == "memory.1").Value == 45); Check(parsed.All(m => m.MemberId is null));
            cells.RemoveAll(c => c.Oid == "1.3.6.1.2.1.99.1.1.1.5.10"); var invalid = MetricParser.Parse(cells, profile, now); Check(invalid.Single(m => m.Key == "temperature.10").Value is null);
        });
        test("自定义指标只归属指定成员，不复制给其他成员", () => { var custom = profile with { CpuOid = "1.2.3.0", MetricMemberId = 2 }; var parsed = MetricParser.Parse([new("1.2.3.0", "33", 33)], custom, now); Check(parsed.Single(m => m.Key.StartsWith("cpu")).MemberId == 2); Check(parsed.Where(m => m.Key.StartsWith("memory") || m.Key.StartsWith("temperature")).All(m => m.Quality == CollectionQuality.Unsupported)); });
        test("SSH CPU、内存池与温度解析保留未知值", () => { var metrics = SshMetrics.Parse("CPU utilization for five seconds: 12%; one minute: 18%; five minutes: 16%", "Processor Pool Total: 1000 Used: 250 Free: 750", "Temperature Value: 42 C", profile, now); Check(metrics[0].Value == 18 && metrics[1].Value == 25 && metrics[2].Value == 42); Check(SshMetrics.Parse("", "", "Temperature is OK", profile, now).All(m => m.Value is null)); });
        test("LLDP 端口映射不把本地编号误当 ifIndex", () =>
        {
            var cells = new[] { new SnmpCell("1.0.8802.1.1.2.1.4.1.1.5.0.7.1", "MAC", Bytes: [0,17,34,51,68,85]), new("1.0.8802.1.1.2.1.4.1.1.4.0.7.1", "4", 4), new("1.0.8802.1.1.2.1.4.1.1.7.0.7.1", "Gi2/0/48"), new("1.0.8802.1.1.2.1.4.1.1.9.0.7.1", "remote"), new("1.0.8802.1.1.2.1.3.7.1.3.7", "Gi1/0/24") };
            var link = TopologyDiscovery.FromSnmp(device.Id, cells, now).Single(); Check(link.LocalPort == "Gi1/0/24" && link.RemoteIdentity == "00:11:22:33:44:55");
            Check(TopologyDiscovery.FromSnmp(device.Id, cells.Where(c => !c.Oid.StartsWith("1.0.8802.1.1.2.1.3")).ToArray(), now).Single().LocalPort == "LLDP 本地端口 7");
        });
        test("Huawei、H3C、Cisco LLDP 与 CDP 邻居解析", () =>
        {
            foreach (var raw in new[] { "GigabitEthernet1/0/24 has 1 neighbor(s)\nChassis ID : 0011-2233-4455\nPort ID : Gi2/0/1\nSystem name : peer", "LLDP neighbor-information of port 24[GigabitEthernet1/0/24]:\nChassis ID : 0011-2233-4455\nPort ID : Gi2/0/1\nSystem name : peer", "Local Intf: GigabitEthernet1/0/24\nChassis id: 0011.2233.4455\nPort id: Gi2/0/1\nSystem Name: peer" }) Check(TopologyDiscovery.FromCli(device.Id, raw, now).Single().LocalPort == "GigabitEthernet1/0/24");
            var cdp = TopologyDiscovery.FromCli(device.Id, "Device ID: peer\nInterface: GigabitEthernet1/0/24, Port ID (outgoing port): GigabitEthernet2/0/1", now, "CDP / SSH"); Check(cdp.Single().RemoteName == "peer");
        });
        test("拓扑身份冲突不猜测，双向边去重且布局持久化", () =>
        {
            var peer = db.GetDevices()[1]; var link = new TopologyLink(Guid.NewGuid(), device.Id, "GigabitEthernet1/0/24", peer.Id, "peer", peer.Name, "GigabitEthernet1/0/1", "LLDP", now);
            Check(TopologyDiscovery.Edges([link, link with { Id = Guid.NewGuid(), LocalDeviceId = peer.Id, RemoteDeviceId = device.Id, LocalPort = "GigabitEthernet1/0/1", RemotePort = "GigabitEthernet1/0/24" }]).Length == 1);
            Check(TopologyDiscovery.Resolve("", peer.Name, [peer, peer with { Id = Guid.NewGuid() }], []) is null); db.SaveTopologyLinks(device.Id, [link], true); db.SavePosition(new(device.Id, 100, 200)); Check(db.TopologyLinks().Count == 1 && db.Positions().Single().Y == 200);
        });
        await asyncTest("后台轮询连续失败判不可达，维护暂停，恢复闭合告警", async () =>
        {
            db.SaveMonitorProfile(profile with { Enabled = false });
            var d = device with { Id = Guid.NewGuid(), IsSimulation = false }; db.SaveDevice(d); var p = profile with { DeviceId = d.Id, IntervalSeconds = 15 }; db.SaveMonitorProfile(p);
            var probe = new FakeProbe(); var scheduler = new MonitorScheduler(db, probe);
            for (var i = 0; i < 3; i++) await scheduler.TickAsync(now.AddSeconds(i * 16), CancellationToken.None);
            Check(db.Samples(d.Id, 1).Single().Reachability == Reachability.Offline && db.Alarms().Any(a => a.DeviceId == d.Id && a.Key == "offline"));
            probe.Responded = true; await scheduler.TickAsync(now.AddSeconds(50), CancellationToken.None); Check(db.Samples(d.Id, 1).Single().Reachability == Reachability.Online); Check(db.Alarms().First(a => a.DeviceId == d.Id).RecoveredAt is not null);
            var calls = probe.Calls; db.SaveMonitorProfile(p with { Maintenance = true }); await scheduler.TickAsync(now.AddSeconds(70), CancellationToken.None); Check(probe.Calls == calls && db.Samples(d.Id, 1).Single().Reachability == Reachability.Maintenance);
        });
        await asyncTest("采集长时间中断后重新累计失败，不沿用休眠前次数", async () =>
        {
            var isolated = new LocalWorkspace(Path.Combine(root, "resumed-collector.db")); var d = device with { Id = Guid.NewGuid(), IsSimulation = false }; isolated.SaveDevice(d); isolated.SaveMonitorProfile(new(d.Id, true, IntervalSeconds: 15));
            isolated.SaveSample(new(Guid.NewGuid(), d.Id, now.AddHours(-1), Reachability.Suspect, [], ConsecutiveFailures: 2));
            await new MonitorScheduler(isolated, new FakeProbe()).TickAsync(now, CancellationToken.None);
            var sample = isolated.Samples(d.Id, 1).Single(); Check(sample.Reachability == Reachability.Suspect && sample.ConsecutiveFailures == 1 && isolated.Alarms().Count == 0);
        });
        await asyncTest("SNMPv2c 本机 UDP GET 与有界 GETBULK 往返", async () =>
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var peer = Task.Run(async () => { for (var i = 0; i < 2; i++) { var datagram = await server.ReceiveAsync(timeout.Token); var request = MessageFactory.ParseMessages(datagram.Buffer, new UserRegistry()).Single(); Check(request.TypeCode() is SnmpType.GetRequestPdu or SnmpType.GetBulkRequestPdu); IList<Variable> values = i == 0 ? [new(new ObjectIdentifier("1.3.6.1.2.1.1.3.0"), new TimeTicks(12300))] : [new(new ObjectIdentifier("1.3.6.1.2.1.25.3.3.1.2.1"), new Integer32(22)), new(new ObjectIdentifier("1.3.6.1.2.1.26.1"), new Integer32(0))]; var response = new ResponseMessage(request.RequestId(), VersionCode.V2, new OctetString("test-only"), ErrorCode.NoError, 0, values); await server.SendAsync(response.ToBytes(), datagram.RemoteEndPoint, timeout.Token); } });
            var source = new SnmpSource(device with { ManagementIp = "127.0.0.1" }, profile with { Protocol = MonitorProtocol.SnmpV2, Port = port }, new("", "", "", "test-only")); var uptime = await source.GetAsync(["1.3.6.1.2.1.1.3.0"], timeout.Token); var table = await source.WalkAsync("1.3.6.1.2.1.25.3.3.1.2", timeout.Token); Check(uptime.Single().Number == 12300 && table.Single().Number == 22); await peer;
        });
        test("模拟监控有明确来源、成员指标及状态隔离", () => { var data = MonitorProbe.Demo(db.GetDevices()[2], now, true); Check(data.Metrics.Count(m => m.MemberId == 1) == 3 && data.Metrics.Count(m => m.MemberId == 2) == 3 && data.Members!.Length == 2); Check(data.Metrics.All(m => m.Source == "模拟数据")); });
        test("同工作区采集器互斥标识稳定，停止信号持久化", () => { Check(CollectorHost.MutexName(root) == CollectorHost.MutexName(root.ToUpperInvariant())); db.RequestCollectorStop(true); db.SaveCollector(new(now)); Check(db.CollectorStopRequested()); db.RequestCollectorStop(false); Check(!db.CollectorStopRequested()); });
        test("多组变更统一审核：创建 VLAN、分配端口、绑定 MAC", () =>
        {
            var d = db.GetDevices()[1]; var engine = new ChangeEngine(db, new DriverRegistry()); var port = db.GetState(d.Id)!.Ports[0].Name;
            var requests = new[] { new ChangeRequest(CreateVlanId: 60, VlanName: "Finance"), new(Ports: [port], Pvid: 60, Description: "财务接入口"), new(AddMacBindings: [new("00:11:22:33:44:55",60,port)]) };
            var plan = engine.PreviewMany(d, requests); Check(plan.Differences.Length == 3); engine.ApplySimulation(plan.Id, true); var state = db.GetState(d.Id)!; Check(state.Ports[0].Pvid == 60 && state.MacBindings!.Single().VlanId == 60 && db.GetSnapshots(d.Id).Count == 1);
        });
        test("端口面板可连续多选，点击编辑后才打开批量表单", () =>
        {
            using var http = new HttpClient(); var factory = new NoSession(); var drivers = new DriverRegistry();
            using var vm = new MainViewModel(db, vault, drivers, new NoAi(), factory, new NetworkOperations(db, drivers, factory, vault, http));
            vm.SelectedDevice = vm.Devices[2]; vm.ReadCurrent.Execute(null); var first = vm.Ports.First(p => p.State.Name == "GigabitEthernet1/0/1"); var second = vm.Ports.First(p => p.State.Name == "GigabitEthernet2/0/1");
            vm.TogglePort.Execute(first); vm.TogglePort.Execute(second); Check(!vm.EditorOpen && vm.Ports.Count(p => p.Picked) == 2); vm.EditPorts.Execute(null); Check(vm.EditorOpen);
        });
        test("浅色工作台待提交队列、直接编辑与 MAC 草稿跨设备保留", () =>
        {
            using var http = new HttpClient(); var factory = new NoSession(); var drivers = new DriverRegistry(); using var vm = new MainViewModel(db, vault, drivers, new NoAi(), factory, new NetworkOperations(db, drivers, factory, vault, http));
            vm.ReadCurrent.Execute(null); vm.Ports[0].Picked = true; var stageNotifications = 0; vm.StagePorts.CanExecuteChanged += (_, _) => stageNotifications++; vm.Pvid = "20"; Check(stageNotifications > 0 && vm.ApplyPvid && vm.StagePorts.CanExecute(null)); vm.StagePorts.Execute(null); Check(vm.PendingChanges.Count == 1 && db.GetState(device.Id)!.Ports[0].Pvid == 10);
            vm.Ports[0].Picked = true; vm.AddMacRow.Execute(null); vm.MacRows[0].Address = "00:11:22:33:44:66"; var old = vm.SelectedDevice; vm.SelectedDevice = vm.Devices[1]; vm.SelectedDevice = old; Check(vm.MacRows.Single().Address == "00:11:22:33:44:66");
            vm.RefreshObservability(); Check(vm.PendingChanges.Count == 1 && vm.Ports[0].Picked); vm.ReviewQueue.Execute(null); Check(vm.ReviewOpen);
        });
        test("跨页面队列中创建 VLAN 后可直接分配端口与 MAC", () =>
        {
            var d = device with { Id = Guid.NewGuid(), Name = "连续配置测试" }; DemoWorkspace.Add(db, d); using var http = new HttpClient(); var factory = new NoSession(); var drivers = new DriverRegistry();
            using var vm = new MainViewModel(db, vault, drivers, new NoAi(), factory, new NetworkOperations(db, drivers, factory, vault, http)); vm.SelectedDevice = vm.Devices.Single(x => x.Id == d.Id); vm.ReadCurrent.Execute(null);
            vm.VlanId = "80"; vm.VlanName = "New80"; vm.StageVlan.Execute(null); Check(vm.PendingChanges.Count == 1);
            vm.Ports[0].Picked = true; vm.Pvid = "80"; Check(vm.CanPreviewPorts); vm.StagePorts.Execute(null); vm.Ports[0].Picked = true;
            vm.AddMacRow.Execute(null); Check(vm.MacRows.Single().Vlan == "80"); vm.MacRows[0].Address = "00:11:22:33:44:77"; Check(vm.StageMac.CanExecute(null)); vm.StageMac.Execute(null); Check(vm.PendingChanges.Count == 3 && vm.ReviewQueue.CanExecute(null));
            vm.RemovePending.Execute(vm.PendingChanges[0]); Check(!vm.ReviewQueue.CanExecute(null) && vm.QueueValidation.Contains("依赖"));
        });
        test("堆叠拓扑展开按成员归属指标，接口定位到正确成员", () =>
        {
            var isolated = new LocalWorkspace(Path.Combine(root, "topology-ui.db")); DemoWorkspace.Seed(isolated);
            var stacked = isolated.GetDevices()[2]; var data = MonitorProbe.Demo(stacked, DateTimeOffset.UtcNow, true);
            isolated.SaveMonitorProfile(new(stacked.Id, true)); isolated.SaveSample(new(Guid.NewGuid(), stacked.Id, DateTimeOffset.UtcNow, Reachability.Online, data.Metrics, Members: data.Members, MembersCapturedAt: DateTimeOffset.UtcNow));
            using var http = new HttpClient(); var factory = new NoSession(); var drivers = new DriverRegistry();
            using var vm = new MainViewModel(isolated, vault, drivers, new NoAi(), factory, new NetworkOperations(isolated, drivers, factory, vault, http));
            Check(!vm.CanExpandStack && !vm.CanRemoveManualLink && !vm.CanOpenTopologyDevice);
            vm.SelectedTopologyNode = vm.TopologyNodes.Single(n => n.Id == stacked.Id); Check(vm.CanExpandStack && vm.CanOpenTopologyDevice); vm.ToggleStack();
            var children = vm.TopologyNodes.Where(n => n.OwnerId == stacked.Id).ToArray(); Check(children.Length == 2);
            foreach (var child in children) Check(child.Health!.Sample!.Metrics.Length == 3 && child.Health.Sample.Metrics.All(m => m.MemberId == child.MemberId));
            Check(vm.EndpointNode(stacked.Id, "GigabitEthernet2/0/1")?.MemberId == 2);
            vm.SelectedTopologyNode = vm.TopologyNodes.Single(n => n.Id == stacked.Id); vm.ToggleStack(); Check(vm.TopologyNodes.All(n => n.OwnerId is null));
            vm.TopologySearch = "不存在的设备"; Check(vm.TopologyNodes.Length == 0); vm.TopologySearch = stacked.ManagementIp; Check(vm.TopologyNodes.Single().Id == stacked.Id);
        });
        test("从拓扑进入其他设备后，后台刷新保留设备与配置草稿", () =>
        {
            using var http = new HttpClient(); var factory = new NoSession(); var drivers = new DriverRegistry();
            using var vm = new MainViewModel(db, vault, drivers, new NoAi(), factory, new NetworkOperations(db, drivers, factory, vault, http));
            var target = vm.Devices[2]; vm.OpenDevice.Execute(target.Id); vm.ReadCurrent.Execute(null); vm.Ports[0].Picked = true; vm.PortDescription = "刷新时保留";
            for (var i = 0; i < 5; i++) vm.RefreshObservability();
            Check(vm.SelectedDevice!.Id == target.Id && vm.SelectedRow!.Device.Id == target.Id && vm.Ports[0].Picked && vm.PortDescription == "刷新时保留" && vm.ApplyDescription && vm.CanConfigure);
        });
        test("手工拓扑连接可移除，自动邻居记录受到保护", () =>
        {
            var peer = db.GetDevices()[1]; var manual = new TopologyLink(Guid.NewGuid(), device.Id, "GigabitEthernet1/0/2", peer.Id, "peer", peer.Name, "GigabitEthernet1/0/3", "手工", now, Manual: true);
            db.SaveTopologyLinks(device.Id, [manual], false); db.RemoveManualLink(manual.Id); Check(db.TopologyLinks().All(l => l.Id != manual.Id));
            var automatic = manual with { Id = Guid.NewGuid(), Manual = false, Source = "LLDP" }; db.SaveTopologyLinks(device.Id, [automatic], true); Reject(() => db.RemoveManualLink(automatic.Id)); Check(db.TopologyLinks().Any(l => l.Id == automatic.Id));
        });
        await asyncTest("独立后台进程无桌面窗口持续采集，同工作区不重复启动", async () =>
        {
            var collectorRoot = Path.Combine(root, "collector-process"); var store = new LocalWorkspace(Path.Combine(collectorRoot, "workspace.db")); DemoWorkspace.Seed(store); var d = store.GetDevices()[0]; store.SaveMonitorProfile(new(d.Id, true, IntervalSeconds: 15));
            Process Start()
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                var testPath = Path.Combine(AppContext.BaseDirectory, "NetOps.Tests");
                if (!Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.FileName = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetDirectoryName(Environment.ProcessPath!)!, "dotnet.exe");
                foreach (var arg in new[] { "exec", "--runtimeconfig", testPath + ".runtimeconfig.json", "--depsfile", testPath + ".deps.json", Path.Combine(AppContext.BaseDirectory, "NetOpsStudio.dll"), "--collector", "--workspace", collectorRoot }) info.ArgumentList.Add(arg);
                return Process.Start(info)!;
            }
            using var worker = Start(); Process? duplicate = null;
            try
            {
                for (var i = 0; i < 120 && store.Samples(d.Id, 1).Count == 0; i++) { Check(!worker.HasExited, "后台进程意外退出"); await Task.Delay(100); }
                var first = store.Samples(d.Id, 1).Single(); duplicate = Start(); await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(!worker.HasExited && duplicate.ExitCode == 0);
                for (var i = 0; i < 210 && store.Samples(d.Id, 1).Single().Id == first.Id; i++) await Task.Delay(100);
                Check(store.Samples(d.Id, 2).Count == 2 && store.Collector() is { StopRequested: false });
                store.RequestCollectorStop(true); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(worker.ExitCode == 0 && store.Collector()!.StopRequested);
            }
            finally { store.RequestCollectorStop(true); if (!worker.HasExited) worker.Kill(true); if (duplicate is not null) { if (!duplicate.HasExited) duplicate.Kill(true); duplicate.Dispose(); } }
        });
    }
    private sealed class FakeProbe : IMonitorProbe { public bool Responded { get; set; } public int Calls { get; private set; } public Task<ProbeResult> ReadAsync(Device d, MonitorProfile p, bool topology, CancellationToken ct) { Calls++; return Task.FromResult(new ProbeResult(Responded, Responded, [], [])); } }
    private sealed class NoAi : IAiClient { public Task<string> AnalyzeAsync(AiProfile p, string q, string c, CancellationToken ct) => Task.FromResult("测试"); }
    private sealed class NoSession : ISessionFactory { public IDeviceSession Create(ConnectionProtocol protocol) => throw new NotSupportedException("测试不连接真实设备"); }
}
