using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using NetOps.Application;
using NetOps.Desktop;
using NetOps.Domain;
using NetOps.Infrastructure;

public static class WorkflowChecks
{
    private static void Check(bool value, string message = "断言失败") { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return; } throw new Exception("应拒绝操作"); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OperationCanceledException) { return; } throw new Exception("应拒绝操作"); }
    private static async Task Finish(MainViewModel vm) { for (var i = 0; i < 200 && vm.Busy; i++) await Task.Delay(10); Check(!vm.Busy, "操作未按时结束"); }
    public static async Task Run(string root, Action<string, Action> test, Func<string, Func<Task>, Task> asyncTest)
    {
        var db = new LocalWorkspace(Path.Combine(root, "workflow.db")); var vault = new DpapiVault(Path.Combine(root, "workflow-vault")); var drivers = new DriverRegistry();
        DemoWorkspace.Seed(db); var device = db.GetDevices()[0]; var engine = new ChangeEngine(db, drivers);
        using var http = new HttpClient(new HttpStub(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"测试\"}") }));
        var factory = new FakeFactory(_ => ""); var network = new NetworkOperations(db, drivers, factory, vault, http); var deferredAi = new DeferredAi();
        using var vm = new MainViewModel(db, vault, drivers, deferredAi, factory, network);
        test("按正常配置步骤先读取基线", () => { Check(!vm.CanConfigure); Check(!vm.PreviewPorts.CanExecute(null)); vm.ReadCurrent.Execute(null); Check(vm.CanConfigure); Check(vm.Ports.Count == 24); });
        test("单端口表单读取原值，默认不修改字段", () => { vm.Ports[0].Picked = true; Check(vm.Pvid == "10"); Check(vm.PortDescription == "Office-01"); Check(vm.ModifiedFieldCount == 0); Check(!vm.CanPreviewPorts); });
        test("直接编辑中文描述自动勾选，可取消并保留原描述", () => { vm.PortDescription = "财务室 / 打印机（备用）"; Check(vm.ApplyDescription && vm.CanPreviewPorts); Check(vm.BuildPortRequest().Description == "财务室 / 打印机（备用）"); vm.ApplyDescription = false; Check(vm.BuildPortRequest().Description is null); });
        test("中文描述批量独立变更与清除，拒绝控制字符和命令分隔符", () =>
        {
            var state = db.GetState(device.Id)!; var names = state.Ports.Take(8).Select(p => p.Name).ToArray();
            var request = new ChangeRequest(Ports: names, Description: "财务室 / 打印机（备用）"); var p = engine.Preview(device, request); Check(p.Differences.Length == 8); engine.ApplySimulation(p.Id, true);
            var actual = db.GetState(device.Id)!; Check(actual.Ports.Take(8).All(p => p.Description == request.Description)); Check(actual.Ports[8].Description == state.Ports[8].Description);
            Check(actual.Ports.Select(p => (p.Mode, p.Pvid, p.Enabled, p.TaggedLabel)).SequenceEqual(state.Ports.Select(p => (p.Mode, p.Pvid, p.Enabled, p.TaggedLabel))));
            foreach (var bad in new[] { "a\rshutdown", "a\u001b[2J", "a\u2028shutdown", "a;shutdown", "a|shutdown", new string('中', 43) }) Reject(() => engine.Preview(device, request with { Description = bad }));
            db.SaveState(state);
        });
        test("修改描述不会提交模式、PVID、启停或允许列表", () => { vm.ApplyDescription = true; vm.PortDescription = "Desk-A"; var r = vm.BuildPortRequest(); Check(r.Description == "Desk-A"); Check(r.Mode is null && r.Pvid is null && r.Enabled is null && r.TaggedVlans is null); });
        test("多端口混合配置按字段保留", () =>
        {
            var state = db.GetState(device.Id)!; var changed = state with { Ports = state.Ports.Select((p, i) => i == 1 ? p with { Mode = PortMode.Trunk, Pvid = 99, TaggedVlans = [1, 10, 20, 99], Enabled = false } : p).ToArray() }; db.SaveState(changed);
            var r = new ChangeRequest(Ports: [state.Ports[0].Name, state.Ports[1].Name], Description: "Bulk-Only"); var p = engine.Preview(device, r);
            Check(p.Commands.All(c => !c.Contains("link-type") && !c.Contains("vlan ") && !c.Contains("shutdown"))); engine.ApplySimulation(p.Id, true);
            var actual = db.GetState(device.Id)!; Check(actual.Ports[0].Pvid == 10); Check(actual.Ports[1].Pvid == 99 && actual.Ports[1].Mode == PortMode.Trunk && !actual.Ports[1].Enabled); Check(actual.Ports.Take(2).All(p => p.Description == "Bulk-Only"));
        });
        await asyncTest("备份后保留端口选择和草稿", async () => { vm.Backup.Execute(null); await Finish(vm); Check(vm.Ports[0].Picked && vm.ApplyDescription && vm.PortDescription == "Desk-A"); });
        test("设备切换后恢复各自的批量编辑草稿", () => { var first = vm.SelectedDevice; vm.SelectedDevice = vm.Devices[1]; Check(!vm.ApplyDescription && vm.Ports.All(p => !p.Picked)); vm.SelectedDevice = first; Check(vm.Ports[0].Picked && vm.ApplyDescription && vm.PortDescription == "Desk-A"); });
        test("范围选择支持 1-8，排除未选择端口", () => { vm.PortRange = "1-8"; vm.SelectRange.Execute(null); Check(vm.Ports.Count(p => p.Picked) == 8); Check(vm.BuildPortRequest().Ports!.Length == 8); });
        test("审核计划在编辑字段改变后失效", () => { vm.PreviewPorts.Execute(null); Check(vm.ReviewOpen); vm.PortDescription = "New-Edit"; Check(!vm.ReviewOpen); Check(!vm.ApplyPlan.CanExecute(null)); });
        test("同步创建 VLAN 并批量分配为单份计划", () =>
        {
            var p = engine.Preview(device, new(CreateVlanId: 40, VlanName: "Guest", Ports: db.GetState(device.Id)!.Ports.Take(8).Select(p => p.Name).ToArray(), Mode: PortMode.Access, Pvid: 40));
            Check(p.Differences.Length == 9); engine.ApplySimulation(p.Id, true); Check(db.GetState(device.Id)!.Ports.Take(8).All(p => p.Pvid == 40)); Check(db.GetState(device.Id)!.Ports[8].Pvid == 10);
        });
        test("VLAN 重命名、删除引用保护和清除描述", () =>
        {
            var p = engine.Preview(device, new(RenameVlanId: 40, VlanName: "Office40")); engine.ApplySimulation(p.Id, true); Check(db.GetState(device.Id)!.Vlans.Single(v => v.Id == 40).Name == "Office40");
            Reject(() => engine.Preview(device, new(DeleteVlanId: 40))); Reject(() => engine.Preview(device, new(DeleteVlanId: 1)));
            p = engine.Preview(device, new(CreateVlanId: 50, VlanName: "Unused")); engine.ApplySimulation(p.Id, true); p = engine.Preview(device, new(DeleteVlanId: 50)); engine.ApplySimulation(p.Id, true); Check(db.GetState(device.Id)!.Vlans.All(v => v.Id != 50));
            p = engine.Preview(device, new(Ports: [db.GetState(device.Id)!.Ports[0].Name], Description: "")); Check(p.Commands.Contains("undo description")); engine.ApplySimulation(p.Id, true); Check(db.GetState(device.Id)!.Ports[0].Description == "");
        });
        test("VLAN 范围边界与去重", () => { Check(VlanRanges.Parse("1,10,20-23,23").SequenceEqual(new[] { 1, 10, 20, 21, 22, 23 })); Reject(() => VlanRanges.Parse("40-20")); Reject(() => VlanRanges.Parse("1-4095")); Reject(() => VlanRanges.Parse("20;reload")); });
        test("按系统系列识别，拒绝把 NX-OS 当作 IOS", () => { Check(DriverRegistry.Identify(Vendor.Cisco, "Cisco IOS Software, test") == "cisco.ios"); Check(DriverRegistry.Identify(Vendor.Cisco, "Cisco NX-OS test") is null); Check(DriverRegistry.Identify(Vendor.H3C, "H3C Comware Software") == "h3c.comware"); });
        test("切换排障模型不会覆盖设置中的未保存内容，默认配置可恢复", () =>
        {
            var a = new AiProfile(Guid.NewGuid(), "配置甲", AiProtocol.Ollama, "http://localhost:11434", "mock-a", null);
            var b = new AiProfile(Guid.NewGuid(), "配置乙", AiProtocol.Ollama, "http://localhost:11434", "mock-b", null);
            db.SaveAiProfile(a); db.SaveAiProfile(b);
            using var profileVm = new MainViewModel(db, vault, drivers, deferredAi, factory, network);
            profileVm.EditingProfile = a; profileVm.ProfileName = "未保存的编辑"; profileVm.SelectedProfile = b;
            Check(profileVm.ProfileName == "未保存的编辑"); profileVm.SetDefaultProfile();
            Check(db.GetAiProfiles().Count(p => p.IsDefault) == 1);
            using var restored = new MainViewModel(db, vault, drivers, deferredAi, factory, network); Check(restored.SelectedProfile!.Id == a.Id);
        });
        await asyncTest("SSH 交互会话在联网前拒绝缺少可信指纹", async () =>
        {
            var reference = vault.Store("{\"Username\":\"test-user\",\"Password\":\"test-only\"}");
            await using var session = new SshSession(vault);
            try { await session.ConnectAsync(device with { IsSimulation = false }, new(Guid.NewGuid(), "测试 SSH", CredentialReference: reference), CancellationToken.None); throw new Exception("缺少指纹未被拒绝"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("指纹")); }
        });
        test("多连接配置与旧版本档案兼容", () =>
        {
            var legacy = device.GetConnections()[0]; Check(legacy.Protocol == ConnectionProtocol.Ssh);
            vm.SaveDevice(device, "", "", "", new(Guid.NewGuid(), "串口", ConnectionProtocol.Console, SerialPort: "COM3"), false);
            Check(db.GetDevices().Single(d => d.Id == device.Id).GetConnections().Length == 2);
            var current = vm.SelectedDevice!; Check(current.PreferredConnectionId == vm.SelectedConnection!.Id);
            Reject(() => vm.SaveDevice(current with { IsSimulation = false }, "", "", "", legacy, false));
        });
        test("Console 无管理 IP 可建档，Telnet 必须显式允许", () => { var serial = new ConnectionProfile(Guid.NewGuid(), "本地 Console", ConnectionProtocol.Console); (device with { ManagementIp = "", Connections = [serial] }).Validate(); Reject(() => (serial with { Protocol = ConnectionProtocol.Telnet }).Validate()); });
        test("CSV 中文列、引号、角色与批量数据校验", () =>
        {
            var rows = DeviceCsv.Parse(new StringReader("设备名称,管理IP,厂商,分组,角色,模拟\n\"办公,交换机\",192.0.2.51,华为,总部,交换机,是\n网关,192.0.2.52,Juniper,分部,路由器,否"));
            Check(rows.Length == 2 && rows[0].Name == "办公,交换机" && rows[0].IsSimulation && rows[1].Role == DeviceRole.Router && !rows[1].IsSimulation);
            Reject(() => DeviceCsv.Parse(new StringReader("设备名称,管理IP,厂商\na,not-ip,华为"))); Reject(() => DeviceCsv.Parse(new StringReader("设备名称,管理IP,厂商\na,192.0.2.1,华为\nb,192.0.2.1,H3C")));
        });
        await asyncTest("AI 等待时可浏览，完成后不清除端口草稿", async () =>
        {
            db.SaveAiProfile(new(Guid.NewGuid(), "本机测试", AiProtocol.Ollama, "http://localhost:11434", "mock", null));
            using var aiVm = new MainViewModel(db, vault, drivers, deferredAi, factory, network); aiVm.ReadCurrent.Execute(null); aiVm.Ports[0].Picked = true; aiVm.ApplyDescription = true; aiVm.PortDescription = "Keep-Me"; aiVm.AiQuestion = "测试问题"; aiVm.ContextApproved = true; aiVm.AskAi.Execute(null);
            Check(aiVm.Busy); aiVm.Section = 3; Check(aiVm.Section == 3); deferredAi.Completion.SetResult("仅测试分析"); await Finish(aiVm); Check(aiVm.Ports[0].Picked && aiVm.PortDescription == "Keep-Me" && aiVm.ApplyDescription);
        });
        test("Telnet IAC 跨包协商、转义与终端类型", () => { var codec = new TelnetCodec(); Check(codec.Decode([255, 251]).Reply.Length == 0); var r = codec.Decode([1, 65, 255, 255, 66]); Check(r.Reply.SequenceEqual(new byte[] { 255, 253, 1 })); Check(r.Data.SequenceEqual(new byte[] { 65, 255, 66 })); codec.Decode([255, 250, 24]); r = codec.Decode([1, 255, 240]); Check(r.Reply.Contains((byte)'V')); });
        await asyncTest("Telnet 本机回环连通与协商", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var server = Task.Run(async () => { using var peer = await listener.AcceptTcpClientAsync(timeout.Token); await peer.GetStream().WriteAsync(new byte[] { 255, 251, 1, 72, 105 }, timeout.Token); var b = new byte[32]; var n = await peer.GetStream().ReadAsync(b, timeout.Token); Check(n >= 3 && b[0] == 255); await Task.Delay(100, timeout.Token); });
            await using var session = new TelnetSession(); await session.ConnectAsync(device with { ManagementIp = "127.0.0.1" }, new(Guid.NewGuid(), "回环", ConnectionProtocol.Telnet, port, AllowTelnet: true), timeout.Token);
            var text = ""; while (text.Length == 0) { await Task.Delay(10, timeout.Token); text = await session.ReadAvailableAsync(timeout.Token); } Check(text == "Hi"); await server;
        });

        const string beforeRaw = "vlan 10\n name Office\n!\ninterface GigabitEthernet1/0/1\n description Old\n switchport mode access\n switchport access vlan 10\n!\ninterface GigabitEthernet1/0/2\n description Keep\n switchport mode access\n switchport access vlan 10\n shutdown\n!\nend\n";
        const string afterRaw = "vlan 10\n name Office\n!\ninterface GigabitEthernet1/0/1\n description New\n switchport mode access\n switchport access vlan 10\n!\ninterface GigabitEthernet1/0/2\n description Keep\n switchport mode access\n switchport access vlan 10\n shutdown\n!\nend\n";
        var liveProfile = new ConnectionProfile(Guid.NewGuid(), "测试 SSH");
        Device MakeReal() { var d = device with { Id = Guid.NewGuid(), IsSimulation = false, Vendor = Vendor.Cisco, DriverId = "cisco.ios", Connections = [liveProfile] }; db.SaveDevice(d); db.SaveState(ConfigurationParser.Parse(d, beforeRaw)); return d; }
        ChangePlan Plan(Device d) => new ChangeEngine(db, drivers).Preview(d, new(Ports: ["GigabitEthernet1/0/1"], Description: "New"));
        test("堆叠成员解析覆盖 Huawei、H3C、Cisco 与锐捷 VSU", () =>
        {
            var huawei = SwitchInventory.Members(Vendor.Huawei, "Slot# Role Mac address Priority Device type\n0 Master 0011-2233-4400 100 S5735\n1 Standby 0011-2233-4401 90 S5735"); Check(huawei.Length == 2 && huawei[0].Id == 0 && huawei[1].Model == "S5735");
            var h3c = SwitchInventory.Members(Vendor.H3C, "MemberID Role Priority CPU-Mac Description\n*+1 Master 32 0011-2233-4400 ---\n 2 Standby 1 0011-2233-4401 ---"); Check(h3c.Length == 2 && h3c[1].Online == true);
            var cisco = SwitchInventory.Members(Vendor.Cisco, "Switch# Role Mac Address Priority Version State\n*1 Active 0011.2233.4400 15 V03 Ready\n2 Standby 0011.2233.4401 14 V03 Ready\n3 Member 0000.0000.0000 0 V00 Provisioned"); Check(cisco.Length == 3 && cisco[2].Online == false);
            var ruijie = SwitchInventory.Members(Vendor.Ruijie, "Switch mode : Virtual Switch\nLocal switch number : 1\nLocal switch operational role: ACTIVE\nPeer switch number : 2\nPeer switch operational role: STANDBY"); Check(ruijie.Length == 2 && ruijie[1].Role == "STANDBY");
            Check(SwitchInventory.Members(Vendor.Huawei, "Unrecognized command").Length == 0);
        });
        test("端口数量来自物理接口清单，成员口数不同且排除逻辑口", () =>
        {
            var list = string.Join("\n", Enumerable.Range(1, 24).Select(i => $"Gi1/0/{i} connected 10 auto auto 10/100/1000BaseTX").Concat(Enumerable.Range(1, 48).Select(i => $"Gi2/0/{i} notconnect 10 auto auto 10/100/1000BaseTX"))) + "\nTe2/1/1 connected trunk auto 10G\nVlan10 up up\nPort-channel1 up up\nGi2/0/1.100 up up";
            var ports = SwitchInventory.PhysicalPorts(list); Check(ports.Length == 73); var members = new[] { new StackMemberState(1, "Active", "Ready", Online: true), new(2, "Standby", "Ready", Online: true) };
            var d = MakeReal(); var state = SwitchInventory.Merge(ConfigurationParser.Parse(d, beforeRaw), ports, members, null); Check(state.StackMembers![0].PortCount == 24 && state.StackMembers[1].PortCount == 49); Check(state.Ports.Length == 73 && state.InventoryCollected);
            Reject(() => PortSelections.Parse("1-8", state.Ports.Select(p => p.Name).ToArray()));
            var selected = PortSelections.Parse("GigabitEthernet1/0/1-8,GigabitEthernet2/0/1-8", state.Ports.Select(p => p.Name).ToArray()); Check(selected.Count == 16 && selected.Contains("GigabitEthernet2/0/8"));
            var unconfirmed = SwitchInventory.Merge(state, null, members, "接口清单未采集"); Check(unconfirmed.StackMembers!.All(m => m.PortCount is null) && !unconfirmed.InventoryCollected);
        });
        await asyncTest("真实读取自动采集堆叠和端口，拓扑变化时零写入", async () =>
        {
            var d = MakeReal(); var stack = "*1 Active 0011.2233.4400 15 V03 Ready\n2 Standby 0011.2233.4401 14 V03 Ready";
            var interfaces = "Gi1/0/1 connected 10 auto auto\nGi1/0/2 notconnect 10 auto auto\nGi2/0/1 connected 10 auto auto";
            var f = new FakeFactory(c => c == "show running-config" ? beforeRaw : c == "show switch" ? stack : c == "show interfaces status" ? interfaces : "");
            var service = new NetworkOperations(db, drivers, f, vault, http); var result = await service.ReadConfigurationAsync(d, liveProfile, CancellationToken.None); Check(result.State!.Ports.Length == 3 && result.State.StackMembers!.Length == 2); db.SaveState(result.State);
            var plan = Plan(d); var changed = new FakeFactory(c => c == "show running-config" ? beforeRaw : c == "show switch" ? stack.Replace("0011.2233.4401", "0011.2233.4499") : c == "show interfaces status" ? interfaces : "");
            await RejectAsync(() => new NetworkOperations(db, drivers, changed, vault, http).ApplyAsync(d, liveProfile, plan, true, new Progress<string>(), CancellationToken.None)); Check(!changed.Sent.Contains("configure terminal"));
            int reads = 0; var success = new FakeFactory(c => c == "show running-config" ? ++reads == 1 ? beforeRaw : afterRaw : c == "show switch" ? stack : c == "show interfaces status" ? interfaces : "");
            var completed = await new NetworkOperations(db, drivers, success, vault, http).ApplyAsync(d, liveProfile, Plan(d), true, new Progress<string>(), CancellationToken.None);
            Check(completed.Contains("验证通过") && success.Sent.Last() == "write memory" && db.GetState(d.Id)!.StackMembers!.Length == 2);
        });
        test("MAC 三种写法统一、广播组播零地址与注入拒绝", () => { Check(MacRules.Normalize("0011-2233-4455") == "00:11:22:33:44:55"); Check(MacRules.Normalize("0011.2233.4455") == "00:11:22:33:44:55"); foreach (var bad in new[] { "ff:ff:ff:ff:ff:ff", "01:00:5e:00:00:01", "00:00:00:00:00:00", "0011.2233.4455;reload" }) Reject(() => MacRules.Normalize(bad)); });
        test("批量静态 MAC 按逐行映射下发、持久化与删除", () =>
        {
            var d = MakeReal() with { Id = Guid.NewGuid(), IsSimulation = true }; DemoWorkspace.Add(db, d);
            var rows = MacRules.ParseBatch("0011-2233-4455,10,GigabitEthernet1/0/1\n00:11:22:33:44:66,10,GigabitEthernet1/0/2", MacBindingKind.StaticForwarding);
            var e = new ChangeEngine(db, drivers); var p = e.Preview(d, new(AddMacBindings: rows)); Check(p.Differences.Length == 2); Check(p.Commands.Count(c => c.StartsWith("mac address-table static")) == 2); e.ApplySimulation(p.Id, true);
            Check(new LocalWorkspace(Path.Combine(root, "workflow.db")).GetState(d.Id)!.MacBindings!.Length == 2);
            Reject(() => e.Preview(d, new(AddMacBindings: [rows[0] with { PortName = "GigabitEthernet1/0/3" }])));
            Reject(() => e.Preview(d, new(Ports: ["GigabitEthernet1/0/1"], Pvid: 20)));
            p = e.Preview(d, new(RemoveMacBindings: [rows[0]])); e.ApplySimulation(p.Id, true); Check(db.GetState(d.Id)!.MacBindings!.Single() == rows[1]);
            Reject(() => e.Preview(d, new(AddMacBindings: [rows[0] with { VlanId = 4000 }]))); Reject(() => e.Preview(d, new(AddMacBindings: [rows[0] with { PortName = "interface;reload" }])));
        });
        test("四厂商静态 MAC 编译与配置回读保留绑定", () =>
        {
            foreach (var vendor in new[] { Vendor.Huawei, Vendor.H3C, Vendor.Cisco, Vendor.Ruijie })
            {
                var d = device with { Vendor = vendor }; var b = new MacBinding("00:11:22:33:44:55", 10, "GigabitEthernet1/0/1"); var commands = new CliPreviewDriver(vendor).Compile(new(AddMacBindings: [b]));
                var staticLine = commands.Single(c => c.Contains("static ")); Check(vendor != Vendor.Huawei || !staticLine.Contains(" interface "));
                var config = beforeRaw + "\n" + staticLine; var parsed = ConfigurationParser.Parse(d, config); Check(parsed.MacBindings!.Single() == b);
                Check(ChangeEngine.ConfigurationHash(parsed) != ChangeEngine.ConfigurationHash(parsed with { MacBindings = [] }));
            }
        });
        test("源 MAC 白名单与静态转发分离，绑定数即安全上限", () =>
        {
            var d = MakeReal() with { Id = Guid.NewGuid(), IsSimulation = true }; DemoWorkspace.Add(db, d); var e = new ChangeEngine(db, drivers);
            var b = new MacBinding("00:11:22:33:44:55", 10, "GigabitEthernet1/0/1", MacBindingKind.SecureSource); var p = e.Preview(d, new(AddMacBindings: [b]));
            Check(p.Commands.Contains("switchport port-security maximum 1") && p.Commands.Contains("switchport port-security violation restrict")); Check(Array.IndexOf(p.Commands, "shutdown") < Array.IndexOf(p.Commands, "switchport port-security")); Check(p.Commands.Contains("no shutdown")); e.ApplySimulation(p.Id, true);
            Check(db.GetState(d.Id)!.PortSecurity!.Single().Enabled); Reject(() => e.Preview(d, new(Ports: [b.PortName], Pvid: 20)));
            p = e.Preview(d, new(RemoveMacBindings: [b])); Check(p.Commands.Contains("no switchport port-security")); e.ApplySimulation(p.Id, true); Check(db.GetState(d.Id)!.PortSecurity!.Length == 0);
            Reject(() => e.Preview(d with { Vendor = Vendor.Huawei }, new(AddMacBindings: [b])));
            db.SaveState(db.GetState(d.Id)! with { PortSecurity = [new(b.PortName, true, 5, "shutdown")] }); Reject(() => e.Preview(d, new(AddMacBindings: [b])));
        });
        await asyncTest("真实 MAC 回读遗漏或安全策略未生效时不保存", async () =>
        {
            var d = MakeReal(); var e = new ChangeEngine(db, drivers); var b = new MacBinding("00:11:22:33:44:55", 10, "GigabitEthernet1/0/1");
            var p = e.Preview(d, new(AddMacBindings: [b])); var f = new FakeFactory(c => c == "show running-config" ? beforeRaw : ""); await RejectAsync(() => new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, p, true, new Progress<string>(), CancellationToken.None)); Check(!f.Sent.Contains("write memory"));
            var raw = beforeRaw.Replace(" description Old", " switchport port-security\n switchport port-security maximum 1\n switchport port-security violation restrict\n switchport port-security mac-address 0011.2233.4455\n description Old");
            var parsed = ConfigurationParser.Parse(d, raw); Check(parsed.PortSecurity!.Single().Enabled && parsed.PortSecurity![0].Maximum == 1); Check(parsed.MacBindings!.Single() == b with { Kind = MacBindingKind.SecureSource });
            var securePlan = e.Preview(d, new(AddMacBindings: [b with { Kind = MacBindingKind.SecureSource }])); int reads = 0;
            var wrongPolicy = new FakeFactory(c => c == "show running-config" ? ++reads == 1 ? beforeRaw : raw.Replace("maximum 1", "maximum 2") : "");
            await RejectAsync(() => new NetworkOperations(db, drivers, wrongPolicy, vault, http).ApplyAsync(d, liveProfile, securePlan, true, new Progress<string>(), CancellationToken.None)); Check(!wrongPolicy.Sent.Contains("write memory"));
        });
        await asyncTest("未解析物理端口时保留配置原文但不开放图形化", async () => { var d = MakeReal(); var f = new FakeFactory(c => c == "show running-config" ? "hostname Test\nend" : ""); var result = await new NetworkOperations(db, drivers, f, vault, http).ReadConfigurationAsync(d, liveProfile, CancellationToken.None); Check(result.State is null && result.Raw.Contains("hostname Test") && result.Warning is not null); });
        test("配置解析保留端口状态，未知模式不猜测", () => { var d = MakeReal(); var state = ConfigurationParser.Parse(d, beforeRaw); Check(state.Ports.Length == 2 && !state.Ports[1].Enabled && state.Ports[0].Pvid == 10); var unknown = ConfigurationParser.Parse(d, "interface GigabitEthernet1/0/1\n description Unknown\n!"); Check(unknown.Ports[0].Mode == PortMode.Unknown && unknown.Ports[0].ModeLabel == "未识别"); });
        test("真实 AI 上下文使用已采集白名单，不上传描述与地址", () =>
        {
            var d = MakeReal(); var state = ConfigurationParser.Parse(d, beforeRaw); var context = AiContext.Build(d, state);
            Check(context.Contains("GigabitEthernet1/0/1") && context.Contains("非实时链路监控")); Check(!context.Contains(d.ManagementIp) && !context.Contains("Old") && !context.Contains("Keep"));
            Check(ChangeEngine.ConfigurationHash(state) == ChangeEngine.ConfigurationHash(state with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(1), Ports = state.Ports.Select(p => p with { LinkUp = true }).ToArray() }));
        });
        test("Huawei / H3C 范围与干道配置解析", () => { var state = ConfigurationParser.Parse(device, "vlan batch 10 20 99\n#\ninterface GigabitEthernet1/0/24\n port link-type trunk\n port trunk pvid vlan 99\n undo port trunk allow-pass vlan all\n port trunk allow-pass vlan 10 20 99\n#\n"); Check(state.Ports[0].TaggedVlans.SequenceEqual(new[] { 10, 20, 99 })); Check(state.Vlans.Length == 4); });
        await asyncTest("真实路径：基线、备份、下发、验证、保存顺序", async () =>
        {
            var d = MakeReal(); int reads = 0; var f = new FakeFactory(command => { if (command == "show running-config") return ++reads == 1 ? beforeRaw : afterRaw; if (command == "configure terminal") Check(db.GetSnapshots(d.Id).Count == 1, "写入前必须先备份"); if (command == "write memory") Check(reads == 2, "保存必须在验证之后"); return ""; });
            var message = await new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, Plan(d), true, new Progress<string>(), CancellationToken.None);
            Check(message.Contains("验证通过")); Check(f.Sent.Last() == "write memory"); Check(db.GetState(d.Id)!.Ports[0].Description == "New"); Check(db.GetState(d.Id)!.Ports[1].Description == "Keep" && !db.GetState(d.Id)!.Ports[1].Enabled);
        });
        await asyncTest("设备基线已变时零写入", async () => { var d = MakeReal(); var f = new FakeFactory(c => c == "show running-config" ? afterRaw : ""); await RejectAsync(() => new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, Plan(d), false, new Progress<string>(), CancellationToken.None)); Check(!f.Sent.Contains("configure terminal") && db.GetSnapshots(d.Id).Count == 0); });
        await asyncTest("命令错误后停止后续下发，不自动重试或保存", async () => { var d = MakeReal(); var f = new FakeFactory(c => c == "show running-config" ? beforeRaw : c.StartsWith("interface ") ? "% Invalid input detected" : ""); await RejectAsync(() => new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, Plan(d), true, new Progress<string>(), CancellationToken.None)); Check(f.Sent.Count(c => c.StartsWith("interface ")) == 1); Check(!f.Sent.Contains("description New") && !f.Sent.Contains("write memory")); Check(db.GetSnapshots(d.Id).Count == 1 && db.GetAudit(d.Id).Any(a => a.Action.Contains("未完成"))); });
        await asyncTest("验证失败不保存并保留原备份", async () => { var d = MakeReal(); var f = new FakeFactory(c => c == "show running-config" ? beforeRaw : ""); await RejectAsync(() => new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, Plan(d), true, new Progress<string>(), CancellationToken.None)); Check(!f.Sent.Contains("write memory") && db.GetSnapshots(d.Id).Count == 1); });
        await asyncTest("保存失败与下发验证成功分别报告", async () => { var d = MakeReal(); int reads = 0; var f = new FakeFactory(c => c == "show running-config" ? ++reads == 1 ? beforeRaw : afterRaw : c == "write memory" ? "% Error save failed" : ""); var result = await new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, liveProfile, Plan(d), true, new Progress<string>(), CancellationToken.None); Check(result.Contains("保存未确认")); Check(db.GetState(d.Id)!.Ports[0].Description == "New"); });
        await asyncTest("拒绝篡改命令或替换连接配置", async () => { var d = MakeReal(); var f = new FakeFactory(_ => ""); var p = Plan(d); p.Commands[0] = "reload"; var service = new NetworkOperations(db, drivers, f, vault, http); await RejectAsync(() => service.ApplyAsync(d, liveProfile, p, true, new Progress<string>(), CancellationToken.None)); await RejectAsync(() => service.ApplyAsync(d, liveProfile with { Port = 2222 }, Plan(d), true, new Progress<string>(), CancellationToken.None)); Check(f.Sent.Count == 0); });
        await asyncTest("SSH 分页与分段提示符会话", async () =>
        {
            var f = new PagingSession(); await using var cli = new CliConversation(f); await cli.OpenAsync(device, liveProfile, CancellationToken.None); var text = await cli.ExecuteAsync("display test", CancellationToken.None); Check(text.Contains("page1") && text.Contains("page2")); Check(f.Spaces == 2);
        });
        await asyncTest("RESTCONF / 厂商 API 只发 GET 且不回显服务端错误", async () =>
        {
            var key = vault.Store("{\"Username\":\"\",\"Password\":\"test-api-token\"}");
            using var apiHttp = new HttpClient(new HttpStub(request => { Check(request.Method == HttpMethod.Get && request.Headers.Authorization?.Scheme == "Bearer"); return new(HttpStatusCode.Unauthorized) { Content = new StringContent("server-sensitive-error") }; }));
            try { await new NetworkOperations(db, drivers, factory, vault, apiHttp).ReadProtocolAsync(device, new(Guid.NewGuid(), "测试 API", ConnectionProtocol.Restconf, 443, key, Endpoint: "https://example.com/restconf/data"), CancellationToken.None); throw new Exception("未拒绝错误响应"); }
            catch (InvalidOperationException ex) { Check(!ex.Message.Contains("server-sensitive-error") && ex.Message.Contains("401")); }
        });
    }

    private sealed class DeferredAi : IAiClient { public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public Task<string> AnalyzeAsync(AiProfile profile, string question, string context, CancellationToken cancellationToken) => Completion.Task; }
    private sealed class HttpStub(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private sealed class FakeFactory(Func<string, string> handler) : ISessionFactory
    {
        public List<string> Sent { get; } = [];
        public IDeviceSession Create(ConnectionProtocol protocol) => new FakeSession(handler, Sent);
    }
    private sealed class FakeSession(Func<string, string> handler, List<string> sent) : IDeviceSession
    {
        private readonly Queue<string> receive = new(); public bool IsConnected { get; private set; }
        public Task ConnectAsync(Device device, ConnectionProfile profile, CancellationToken ct) { IsConnected = true; receive.Enqueue("test#"); return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken ct) { var command = text.Trim(); sent.Add(command); receive.Enqueue(handler(command) + "\ntest#"); return Task.CompletedTask; }
        public Task<string> ReadAvailableAsync(CancellationToken ct) => Task.FromResult(receive.Count > 0 ? receive.Dequeue() : "");
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
    private sealed class PagingSession : IDeviceSession
    {
        private readonly Queue<string> receive = new(); public int Spaces { get; private set; } public bool IsConnected { get; private set; }
        public Task ConnectAsync(Device device, ConnectionProfile profile, CancellationToken ct) { IsConnected = true; receive.Enqueue("test#"); return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken ct) { if (text == " ") { Spaces++; receive.Enqueue(Spaces == 1 ? "\b\b\b\b\b\b\b\b\b\b\b\bpage2\n---- More ----" : "\ntest#"); } else receive.Enqueue("page1\n---- More ----"); return Task.CompletedTask; }
        public Task<string> ReadAvailableAsync(CancellationToken ct) => Task.FromResult(receive.Count > 0 ? receive.Dequeue() : "");
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
