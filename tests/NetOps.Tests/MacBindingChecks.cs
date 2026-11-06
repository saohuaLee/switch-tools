using System.IO;
using System.Net.Http;
using NetOps.Application;
using NetOps.Desktop;
using NetOps.Domain;
using NetOps.Infrastructure;

public static class MacBindingChecks
{
    private const string Port1 = "GigabitEthernet3/0/5", Port2 = "GigabitEthernet3/0/6";
    private const string Raw = "vlan 10\n name Office\n!\ninterface GigabitEthernet3/0/5\n switchport mode access\n switchport access vlan 10\n!\ninterface GigabitEthernet3/0/6\n switchport mode access\n switchport access vlan 10\n shutdown\n!\nend\n";
    private static void Check(bool value, string message = "断言失败") { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("应拒绝此输入"); }
    public static async Task Run(string root, Action<string, Action> test, Func<string, Func<Task>, Task> asyncTest)
    {
        var db = new LocalWorkspace(Path.Combine(root, "mac-editor.db")); DemoWorkspace.Seed(db);
        var vault = new DpapiVault(Path.Combine(root, "mac-vault")); var drivers = new DriverRegistry(); using var http = new HttpClient();
        var profile = new ConnectionProfile(Guid.NewGuid(), "MAC 会话夹具");
        Device Create(bool live = false, string raw = Raw)
        {
            var d = db.GetDevices()[0] with { Id = Guid.NewGuid(), Name = "MAC 绑定独立验证", Vendor = Vendor.Cisco, DriverId = "cisco.iosxe", IsSimulation = !live, Connections = [profile] };
            db.SaveDevice(d); db.SaveState(ConfigurationParser.Parse(d, raw)); return d;
        }
        MainViewModel Editor(Device d)
        {
            var sessions = new FakeFactory(_ => ""); var vm = new MainViewModel(db, vault, drivers, new NoAi(), sessions, new NetworkOperations(db, drivers, sessions, vault, http));
            vm.SelectedDevice = vm.Devices.Single(v => v.Id == d.Id); vm.ReadCurrent.Execute(null); Check(vm.CanConfigure); return vm;
        }
        MacBinding Bind(string address, string port = Port1) => new(MacRules.Normalize(address), 10, port, MacBindingKind.SecureSource);
        test("MAC 横线、冒号、点号、空格、连续及全角输入统一为思科格式", () =>
        {
            foreach (var value in new[] { "38-ff-59-06-1a-71", "38:FF:59:06:1A:71", "38ff.5906.1a71", "38FF59061A71", "38 ff 59 06 1a 71", " ３８：ｆｆ：５９：０６：１ａ：７１ " })
            { Check(MacRules.Normalize(value) == "38:ff:59:06:1a:71"); Check(MacRules.VendorAddress(value, false) == "38ff.5906.1a71"); }
            foreach (var value in new[] { "38-ff-59-06-1a-7", "38-gg-59-06-1a-71", "ff-ff-ff-ff-ff-ff", "01-00-5e-00-00-01", "00-00-00-00-00-00", "38ff59061a71;reload" }) Reject(() => MacRules.Normalize(value));
        });
        test("三种违规动作均可预览与回读，默认不覆盖为 restrict", () =>
        {
            var d = Create(); var b = Bind("38ff59061a71");
            foreach (var action in new[] { "shutdown", "restrict", "protect" })
            {
                var plan = new ChangeEngine(db, drivers).Preview(d, new(AddMacBindings: [b], SecuritySettings: [new(Port1, 2, action)]));
                Check(plan.Commands.Contains($"switchport port-security violation {action}")); Check(plan.Risks.Any(r => r.Contains("动态学习")));
                var parsed = ConfigurationParser.Parse(d, Raw.Replace(" switchport access vlan 10", $" switchport access vlan 10\n switchport port-security\n switchport port-security maximum 2\n switchport port-security violation {action}\n switchport port-security mac-address 38ff.5906.1a71", StringComparison.Ordinal).Replace("interface GigabitEthernet3/0/6", "!\ninterface GigabitEthernet3/0/6"));
                Check(parsed.PortSecurity!.All(p => p.Maximum == 2 && p.Violation == action));
            }
            var request = new ChangeRequest(AddMacBindings: [b]); Check(new ChangeEngine(db, drivers).Preview(d, request).Commands.Contains("switchport port-security violation shutdown"));
        });
        test("安全上限不能低于最终数量，拒绝非法参数及未适配策略", () =>
        {
            var d = Create(); var e = new ChangeEngine(db, drivers); var first = Bind("38ff59061a71"); var second = Bind("38ff59061a72");
            Reject(() => e.Preview(d, new(AddMacBindings: [first, second], SecuritySettings: [new(Port1, 1)])));
            foreach (var settings in new[] { new PortSecuritySettings(Port1, 0), new(Port1, -1), new(Port1, 2, "shutdown\nreload"), new("Unknown1", 2) }) Reject(() => e.Preview(d, new(AddMacBindings: [first], SecuritySettings: [settings])));
            Reject(() => e.Preview(d, new(AddMacBindings: [first], SecuritySettings: [new(Port1), new(Port1)])));
            db.SaveState(db.GetState(d.Id)! with { PortSecurity = [new(Port1, true, 1, "shutdown", true)] }); Reject(() => e.Preview(d, new(AddMacBindings: [first])));
        });
        test("换绑一次关闭端口，先删旧再加新，原禁用端口最终启用", () =>
        {
            var d = Create(); var old = Bind("d0460cf52960", Port2); var next = Bind("38ff59061a71", Port2);
            db.SaveState(db.GetState(d.Id)! with { MacBindings = [old], PortSecurity = [new(Port2, true)] });
            var e = new ChangeEngine(db, drivers); var p = e.Preview(d, new(RemoveMacBindings: [old], AddMacBindings: [next], SecuritySettings: [new(Port2)]));
            var commands = p.Commands; int shut = Array.IndexOf(commands, "shutdown"), remove = Array.FindIndex(commands, c => c.Contains("no switchport port-security mac-address")), add = Array.FindIndex(commands, c => c.StartsWith("switchport port-security mac-address ")), up = Array.IndexOf(commands, "no shutdown");
            Check(shut < remove && remove < add && add < up && commands.Count(c => c == "shutdown") == 1 && commands.Count(c => c == "no shutdown") == 1);
            e.ApplySimulation(p.Id, true); Check(db.GetState(d.Id)!.Ports.Single(p => p.Name == Port2).Enabled); Check(db.GetState(d.Id)!.MacBindings!.Single() == next);
        });
        test("相邻的删除与新增安全变更合并，多个端口先删除再迁移", () =>
        {
            var d = Create(); var old = Bind("d0460cf52960", Port2); var next = Bind("38ff59061a71", Port2);
            db.SaveState(db.GetState(d.Id)! with { MacBindings = [old], PortSecurity = [new(Port2, true)] }); var e = new ChangeEngine(db, drivers);
            var p = e.PreviewMany(d, [new(RemoveMacBindings: [old]), new(AddMacBindings: [next])]); Check(p.Commands.Count(c => c == "shutdown") == 1 && !p.Commands.Contains("no switchport port-security"));
            var moved = old with { PortName = Port1 }; p = e.PreviewMany(d, [new(RemoveMacBindings: [old]), new(AddMacBindings: [moved])]);
            Check(Array.IndexOf(p.Commands, "no switchport port-security mac-address d046.0cf5.2960") < Array.IndexOf(p.Commands, "switchport port-security mac-address d046.0cf5.2960"));
            var empty = Create(); p = e.PreviewMany(empty, [new(AddMacBindings: [next]), new(RemoveMacBindings: [next])]);
            Check(p.Commands.Contains("no shutdown") && ChangeEngine.ProjectBatch(empty, db.GetState(empty.Id)!, ChangeEngine.Requests(p)).Ports.Single(x => x.Name == Port2).Enabled);
        });
        test("界面批量导入自动格式化，并按各端口计算数量", () =>
        {
            var d = Create(); using var vm = Editor(d); Check(vm.IsSecureMac && vm.MacAutomaticMaximum && vm.MacMaximum == "1" && vm.SelectedMacViolation.Value == "shutdown");
            vm.MacPastePort = Port1; vm.MacPasteText = "38-ff-59-06-1a-71\n38ff.5906.1a72"; vm.ImportMacRows.Execute(null);
            Check(vm.MacRows.Count == 2 && vm.MacRows[0].Address == "38:ff:59:06:1a:71" && vm.MacMaximum == "2");
            vm.MacPasteText = $"38ff59061a73,10,{Port2}"; vm.ImportMacRows.Execute(null);
            var r = vm.BuildMacRequest(); Check(r.SecuritySettings!.Single(s => s.PortName == Port1).Maximum == 2 && r.SecuritySettings!.Single(s => s.PortName == Port2).Maximum == 1);
            Check(vm.MacValidation == ""); vm.MacAutomaticMaximum = false; vm.MacMaximum = "1"; Check(vm.MacValidation.Contains("超过")); vm.MacMaximum = "5"; Check(vm.MacValidation == "");
            vm.SelectedMacViolation = vm.MacViolations.Single(v => v.Value == "protect"); vm.StageMac.Execute(null); Check(vm.PendingChanges.Single().Request.SecuritySettings!.All(s => s.Maximum == 5 && s.Violation == "protect"));
            vm.SaveAfterVerify = false; vm.ReviewQueue.Execute(null); Check(vm.IsSecurityPlan && vm.SaveAfterVerify && !vm.CanChooseSave && vm.PlanCommandRows.Last().Command == "write memory");
        });
        test("界面换绑保留原记录，自动上限包含未修改的已有绑定", () =>
        {
            var d = Create(); var old = Bind("d0460cf52960"); var keep = Bind("38ff59061a72");
            db.SaveState(db.GetState(d.Id)! with { MacBindings = [old, keep], PortSecurity = [new(Port1, true, 2)] }); using var vm = Editor(d);
            vm.MacBindings.Single(m => m.Binding == old).Picked = true; vm.EditSelectedMac.Execute(null); Check(vm.MacRows.Single().OldAddress == old.Address);
            vm.MacRows[0].Address = "38-ff-59-06-1a-71"; var r = vm.BuildMacRequest(); Check(r.RemoveMacBindings!.Single() == old && r.AddMacBindings!.Single() == Bind("38ff59061a71")); Check(r.SecuritySettings!.Single().Maximum == 2);
            vm.StageMac.Execute(null); Check(vm.PendingChanges.Count == 1); vm.ReviewQueue.Execute(null); Check(vm.PlanCommandRows.Count(c => c.Command == "shutdown") == 1);
        });
        test("MAC 参数与换绑草稿跨设备保留，非法整批导入不产生部分行", () =>
        {
            var first = Create(); var second = Create(); using var vm = Editor(first);
            vm.MacPastePort = Port1; vm.MacPasteText = "38ff59061a71\n38ff59061a7z"; vm.ImportMacRows.Execute(null); Check(vm.MacRows.Count == 0 && vm.MacPasteText.Length > 0);
            vm.MacPasteText = "38ff59061a71"; vm.ImportMacRows.Execute(null); vm.MacAutomaticMaximum = false; vm.MacMaximum = "9"; vm.SelectedMacViolation = vm.MacViolations[1];
            vm.SelectedDevice = vm.Devices.Single(d => d.Id == second.Id); Check(vm.MacRows.Count == 0 && vm.MacAutomaticMaximum && vm.SelectedMacViolation.Value == "shutdown");
            vm.SelectedDevice = vm.Devices.Single(d => d.Id == first.Id); Check(vm.MacRows.Count == 1 && !vm.MacAutomaticMaximum && vm.MacMaximum == "9" && vm.SelectedMacViolation.Value == "restrict");
        });
        test("删除部分绑定时自动收紧上限，同时保留原违规动作", () =>
        {
            var d = Create(); var old = Bind("d0460cf52960"); var keep = Bind("38ff59061a72");
            db.SaveState(db.GetState(d.Id)! with { MacBindings = [old, keep], PortSecurity = [new(Port1, true, 2, "protect")] }); using var vm = Editor(d);
            vm.MacBindings.Single(m => m.Binding == old).Picked = true; var r = vm.BuildDeleteMacRequest();
            Check(r.RemoveMacBindings!.Single() == old && r.SecuritySettings!.Single() == new PortSecuritySettings(Port1, 1, "protect"));
            vm.StageDeleteMac.Execute(null); Check(vm.PendingChanges.Count == 1 && vm.PendingChanges[0].Request.SecuritySettings!.Single().Maximum == 1);
        });
        test("命令说明逐条解释英文、删除、恢复与保存动作", () =>
        {
            var d = Create(); var old = Bind("d0460cf52960"); db.SaveState(db.GetState(d.Id)! with { MacBindings = [old], PortSecurity = [new(Port1, true)] });
            var p = new ChangeEngine(db, drivers).Preview(d, new(RemoveMacBindings: [old], AddMacBindings: [Bind("38ff59061a71")], SecuritySettings: [new(Port1)]));
            Check(p.Commands.Select(CommandExplanation.From).All(c => !c.Explanation.StartsWith("执行此厂商")));
            Check(CommandExplanation.From("switchport port-security violation protect").Explanation.Contains("仅丢弃")); Check(CommandExplanation.From("write memory").Explanation.Contains("启动配置"));
        });
        test("操作入口先选任务，读取后继续当前任务且自动选定可用端口", () =>
        {
            var d = Create(); var f = new FakeFactory(_ => ""); using var vm = new MainViewModel(db, vault, drivers, new NoAi(), f, new NetworkOperations(db, drivers, f, vault, http));
            vm.SelectedDevice = vm.Devices.Single(v => v.Id == d.Id); Check(vm.Page == 2 && vm.TaskLanding);
            vm.OpenDevice.Execute(d.Id); vm.ChooseTask.Execute("6"); Check(!vm.TaskLanding && vm.NeedsTaskRead && !vm.TaskContentVisible);
            vm.ReadCurrent.Execute(null); Check(vm.CanConfigure && vm.TaskContentVisible && vm.Section == 6 && vm.MacPastePort == Port1);
            vm.MacPasteText = "38-ff-59-06-1a-71\n38ff59061a72"; Check(vm.MacMaximum == "2");
            vm.BackToTasks.Execute(null); Check(vm.TaskLanding); vm.ChooseTask.Execute("6"); Check(vm.MacPasteText.Contains("38-ff") && vm.TaskContentVisible);
        });
        test("MAC 输入后直接审核，无需导入或加入队列，取消后仍保留输入", () =>
        {
            var d = Create(); using var vm = Editor(d); vm.ChooseTask.Execute("6"); var before = ChangeEngine.ConfigurationHash(db.GetState(d.Id)!);
            vm.MacPasteText = "38-ff-59-06-1a-71\n38ff59061a72"; Check(vm.MacMaximum == "2"); vm.ReviewMacOperation.Execute(null);
            Check(vm.ReviewOpen && vm.PendingChanges.Count == 0 && vm.MacRows.Count == 2 && vm.MacRows.All(r => r.Address.Contains(':')));
            Check(ChangeEngine.ConfigurationHash(db.GetState(d.Id)!) == before && vm.PlanCommandRows.Any(c => c.Command == "switchport port-security maximum 2"));
            vm.CloseReview.Execute(null); Check(vm.MacRows.Count == 2 && vm.TaskContentVisible); vm.ReviewMacOperation.Execute(null); Check(vm.ReviewOpen && vm.PendingChanges.Count == 0);
        });
        test("端口与 VLAN 表单都能直接审核，批量草稿作为显式附加范围", () =>
        {
            var d = Create(); using var vm = Editor(d); vm.ChooseTask.Execute("1"); vm.Ports[0].Picked = true; vm.PortDescription = "直接审核描述";
            vm.ReviewPortOperation.Execute(null); Check(vm.ReviewOpen && vm.PendingChanges.Count == 0 && vm.PlanCommands.Contains("description 直接审核描述")); vm.CloseReview.Execute(null);
            vm.ChooseTask.Execute("2"); vm.VlanId = "20"; vm.VlanName = "Office20"; vm.ReviewVlanOperation.Execute(null);
            Check(vm.ReviewOpen && vm.PendingChanges.Count == 0 && vm.PlanCommands.Contains("vlan 20")); vm.CloseReview.Execute(null);
            vm.Stage(new(CreateVlanId: 30, VlanName: "Draft30"), "创建批量草稿 VLAN"); vm.VlanId = "40"; vm.VlanName = "Office40"; vm.ReviewVlanOperation.Execute(null);
            Check(vm.ReviewOpen && vm.PendingChanges.Count == 1 && !vm.PlanCommands.Contains("vlan 30") && vm.PlanCommands.Contains("vlan 40"));
            vm.CloseReview.Execute(null); vm.IncludeSavedDrafts = true; vm.ReviewVlanOperation.Execute(null);
            Check(vm.ReviewOpen && vm.ReviewScope.Contains("1 组") && vm.PlanCommands.Contains("vlan 30") && vm.PlanCommands.Contains("vlan 40"));
        });
        await asyncTest("直接审核执行后展示结果，清理本机草稿并保留真实设备隔离", async () =>
        {
            var d = Create(); using var vm = Editor(d); vm.ChooseTask.Execute("6"); vm.MacPasteText = "38-ff-59-06-1a-71"; vm.ReviewMacOperation.Execute(null);
            vm.PlanConfirmed = true; vm.ApplyPlan.Execute(null); for (var i = 0; i < 200 && vm.Busy; i++) await Task.Delay(10);
            Check(!vm.Busy && vm.HasLastResult && vm.LastResult.Contains("模拟") && !vm.ReviewOpen && vm.PendingChanges.Count == 0 && db.GetState(d.Id)!.MacBindings!.Single().Address == "38:ff:59:06:1a:71");
        });
        await asyncTest("真实执行路径换绑回读后自动保存，即使通用保存开关关闭", async () =>
        {
            var before = Raw.Replace(" switchport access vlan 10", " switchport access vlan 10\n switchport port-security\n switchport port-security mac-address d046.0cf5.2960");
            // 仅为第一个端口保留安全配置，第二个端口作为未修改基线。
            before = before[..before.IndexOf("interface GigabitEthernet3/0/6", StringComparison.Ordinal)] + Raw[Raw.IndexOf("interface GigabitEthernet3/0/6", StringComparison.Ordinal)..];
            var after = before.Replace("d046.0cf5.2960", "38ff.5906.1a71"); var d = Create(true, before); var e = new ChangeEngine(db, drivers);
            var p = e.Preview(d, new(RemoveMacBindings: [Bind("d0460cf52960")], AddMacBindings: [Bind("38ff59061a71")], SecuritySettings: [new(Port1)])); int reads = 0;
            var factory = new FakeFactory(c => c == "show running-config" ? ++reads == 1 ? before : after : c == "write memory" && reads != 2 ? throw new Exception("必须在回读后保存") : "");
            var result = await new NetworkOperations(db, drivers, factory, vault, http).ApplyAsync(d, profile, p, false, new Progress<string>(), CancellationToken.None);
            Check(result.Contains("验证通过") && factory.Sent.Last() == "write memory" && db.GetSnapshots(d.Id).Count == 1);
        });
        await asyncTest("安全绑定命令被拒绝或回读不符时停止，不保存部分配置", async () =>
        {
            foreach (var deny in new[] { false, true })
            {
                var d = Create(true); var p = new ChangeEngine(db, drivers).Preview(d, new(AddMacBindings: [Bind("38ff59061a71")], SecuritySettings: [new(Port1)]));
                var f = new FakeFactory(c => c == "show running-config" ? Raw : deny && c.StartsWith("switchport port-security mac-address ") ? "% Cannot add secure address" : "");
                try { await new NetworkOperations(db, drivers, f, vault, http).ApplyAsync(d, profile, p, false, new Progress<string>(), CancellationToken.None); throw new Exception("应验证失败"); }
                catch (InvalidOperationException) { Check(!f.Sent.Contains("write memory")); if (deny) Check(!f.Sent.Contains("no shutdown")); }
            }
        });
    }
    private sealed class NoAi : IAiClient { public Task<string> AnalyzeAsync(AiProfile p, string q, string c, CancellationToken ct) => throw new NotSupportedException(); }
    private sealed class FakeFactory(Func<string, string> handler) : ISessionFactory
    {
        public List<string> Sent { get; } = [];
        public IDeviceSession Create(ConnectionProtocol protocol) => new Session(handler, Sent);
    }
    private sealed class Session(Func<string, string> handler, List<string> sent) : IDeviceSession
    {
        private readonly Queue<string> receive = new(); public bool IsConnected { get; private set; }
        public Task ConnectAsync(Device d, ConnectionProfile p, CancellationToken ct) { IsConnected = true; receive.Enqueue("test#"); return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken ct) { var c = text.Trim(); sent.Add(c); receive.Enqueue(handler(c) + "\ntest#"); return Task.CompletedTask; }
        public Task<string> ReadAvailableAsync(CancellationToken ct) => Task.FromResult(receive.Count > 0 ? receive.Dequeue() : "");
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
