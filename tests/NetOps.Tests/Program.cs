using System.Net;
using System.Text;
using System.Text.Json;
using NetOps.Application;
using NetOps.Domain;
using NetOps.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), "netops-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var workspace = new LocalWorkspace(Path.Combine(root, "workspace.db"));
var vault = new DpapiVault(Path.Combine(root, "vault"));
var drivers = new DriverRegistry();
DemoWorkspace.Seed(workspace);
var device = workspace.GetDevices()[0];
var engine = new ChangeEngine(workspace, drivers);
int passed = 0, failed = 0;

void Check(bool ok, string message = "断言失败") { if (!ok) throw new Exception(message); }
void Reject(Action action)
{ try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return; } throw new Exception("应拒绝此操作"); }
void Test(string name, Action action)
{ try { action(); passed++; Console.WriteLine("通过：" + name); } catch (Exception ex) { failed++; Console.WriteLine("失败：" + name + "：" + ex.Message); } }
async Task AsyncTest(string name, Func<Task> action)
{ try { await action(); passed++; Console.WriteLine("通过：" + name); } catch (Exception ex) { failed++; Console.WriteLine("失败：" + name + "：" + ex.Message); } }
ChangeRequest Access(string port, int vlan = 20) => new(Ports: [port], Mode: PortMode.Access, Pvid: vlan, TaggedVlans: [], Enabled: true);

Test("首次启动演示数据与重复初始化", () => { DemoWorkspace.Seed(workspace); Check(workspace.GetDevices().Count == 3); Check(workspace.GetState(device.Id)!.Ports.Length == 24); });
Test("SQLite 设备与状态持久化", () => { var reopened = new LocalWorkspace(Path.Combine(root, "workspace.db")); Check(reopened.GetDevices()[0].Id == device.Id); Check(reopened.GetState(device.Id)!.Vlans.Length == 4); });
Test("设备地址与管理参数校验", () => { Reject(() => (device with { ManagementIp = "not-an-ip" }).Validate()); Reject(() => (device with { ManagementVlan = 4095 }).Validate()); Reject(() => (device with { SshPort = 0 }).Validate()); });
Test("拒绝非法 VLAN 与名称注入", () => { Reject(() => engine.Preview(device, new(CreateVlanId: 4095, VlanName: "Bad"))); Reject(() => engine.Preview(device, new(CreateVlanId: 30, VlanName: "X\nshutdown"))); Reject(() => engine.Preview(device, new(CreateVlanId: 10, VlanName: "Duplicate"))); });
Test("拒绝未知端口及描述命令注入", () => { Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1;reboot"))); Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1") with { Description = "test\nshutdown" })); });
Test("拒绝不存在的 PVID 与空选择", () => { Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1", 100))); Reject(() => engine.Preview(device, new(Ports: [], Mode: PortMode.Access, Pvid: 20))); });
Test("干道允许列表必须显式包含 PVID", () => { Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1") with { Mode = PortMode.Trunk, TaggedVlans = [] })); Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1") with { Mode = PortMode.Trunk, TaggedVlans = [10] })); });
Test("预览不改变任何配置或备份", () => { var hash = ChangeEngine.Hash(workspace.GetState(device.Id)!); engine.Preview(device, new(CreateVlanId: 30, VlanName: "Guest")); Check(ChangeEngine.Hash(workspace.GetState(device.Id)!) == hash); Check(workspace.GetSnapshots(device.Id).Count == 0); });
Test("未确认时不能执行", () => { var p = engine.Preview(device, new(CreateVlanId: 30, VlanName: "Guest")); Reject(() => engine.ApplySimulation(p.Id, false)); Check(workspace.GetState(device.Id)!.Vlans.All(v => v.Id != 30)); });
Test("执行、自动备份、回读验证、审计", () => { var p = engine.Preview(device, new(CreateVlanId: 30, VlanName: "Guest")); engine.ApplySimulation(p.Id, true); Check(workspace.GetState(device.Id)!.Vlans.Any(v => v.Id == 30)); Check(JsonSerializer.Deserialize<DeviceState>(workspace.GetSnapshots(device.Id)[0].Content)!.Vlans.All(v => v.Id != 30)); Check(workspace.GetAudit(device.Id).Count == 1); Reject(() => engine.ApplySimulation(p.Id, true)); });
Test("旧预览被新预览替换", () => { var old = engine.Preview(device, new(CreateVlanId: 40, VlanName: "Old")); engine.Preview(device, new(CreateVlanId: 50, VlanName: "New")); Reject(() => engine.ApplySimulation(old.Id, true)); });
Test("基线变化后拒绝执行", () => { var p = engine.Preview(device, new(CreateVlanId: 40, VlanName: "Guest")); var s = workspace.GetState(device.Id)!; workspace.SaveState(s with { Revision = s.Revision + 1 }); Reject(() => engine.ApplySimulation(p.Id, true)); });
Test("预览请求与返回值均隔离调用方修改", () => { var req = Access("GigabitEthernet1/0/1"); var p = engine.Preview(device, req); req.Ports![0] = "GigabitEthernet1/0/24"; p.Request.Ports![0] = "GigabitEthernet1/0/23"; p.Commands[0] = "reboot"; engine.ApplySimulation(p.Id, true); Check(workspace.GetState(device.Id)!.Ports[0].Pvid == 20); Check(workspace.GetState(device.Id)!.Ports[22].Pvid == 10); });
Test("管理 VLAN 与上联端口风险提示", () => { var p = engine.Preview(device, Access("GigabitEthernet1/0/24", 99)); Check(p.Risks.Any(r => r.Contains("上联"))); Check(p.Risks.Any(r => r.Contains("管理 VLAN"))); });
Test("无变化时不生成执行计划", () => Reject(() => engine.Preview(device, Access("GigabitEthernet1/0/1"))));
Test("真实设备不能绕过执行隔离", () => { var real = device with { Id = Guid.NewGuid(), IsSimulation = false }; workspace.SaveDevice(real); workspace.SaveState(workspace.GetState(device.Id)! with { DeviceId = real.Id }); Reject(() => engine.Preview(real, new(CreateVlanId: 40, VlanName: "Blocked"))); var p = engine.Preview(device, new(CreateVlanId: 40, VlanName: "Blocked")); workspace.SaveDevice(device with { IsSimulation = false }); try { Reject(() => engine.ApplySimulation(p.Id, true)); } finally { workspace.SaveDevice(device); } });
Test("未实现的品牌及设备角色不能生成配置", () => { Reject(() => engine.Preview(device with { Vendor = Vendor.Juniper }, new(CreateVlanId: 40, VlanName: "Blocked"))); Reject(() => engine.Preview(device with { Role = DeviceRole.Firewall }, new(CreateVlanId: 40, VlanName: "Blocked"))); });
Test("四种厂商命令按语法分离", () => { foreach (var vendor in new[] { Vendor.Huawei, Vendor.H3C, Vendor.Cisco, Vendor.Ruijie }) { var p = engine.Preview(device with { Vendor = vendor }, new(CreateVlanId: 40, VlanName: "Guest")); Check(p.Commands.Contains("vlan 40")); Check(!p.Commands.Any(c => c is "save" or "write memory")); } Check(drivers.Resolve(device).ReadCommand(ReadCommand.Version) == "display version"); Check(drivers.Resolve(device with { Vendor = Vendor.Cisco }).ReadCommand(ReadCommand.Version) == "show version"); });
Test("事务竞争时拒绝覆盖新状态", () => { var before = workspace.GetState(device.Id)!; workspace.SaveState(before with { Revision = before.Revision + 1 }); Reject(() => workspace.CommitSimulation(before, before with { Revision = before.Revision + 2 }, new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "竞争测试", "x"), new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "竞争测试", "x"))); });
Test("事务失败时备份和状态一起回滚", () => { var before = workspace.GetState(device.Id)!; var count = workspace.GetSnapshots(device.Id).Count; var audit = workspace.GetAudit(device.Id)[0]; try { workspace.CommitSimulation(before, before with { Revision = before.Revision + 1 }, new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "回滚测试", "x"), audit); throw new Exception("未触发唯一键冲突"); } catch (Microsoft.Data.Sqlite.SqliteException) { } Check(workspace.GetSnapshots(device.Id).Count == count); Check(ChangeEngine.Hash(workspace.GetState(device.Id)!) == ChangeEngine.Hash(before)); });
Test("DPAPI 凭据往返且不落明文", () => { var secret = "只用于测试的密钥-12345"; var reference = vault.Store(secret); Check(vault.Read(reference) == secret); var bytes = File.ReadAllBytes(Path.Combine(root, "vault", reference + ".dpapi")); Check(!Encoding.UTF8.GetString(bytes).Contains(secret)); Reject(() => vault.Read("../../secrets")); });
Test("配置备份在数据库中加密保存", () => { const string marker = "TEST-SENSITIVE-CONFIG-DO-NOT-STORE-PLAIN"; workspace.AddSnapshot(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "加密测试", marker)); Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "workspace.db"))).Contains(marker)); Check(workspace.GetSnapshots(device.Id).Any(s => s.Content == marker)); });
Test("AI 上下文字段白名单排除凭据与网络标识", () => { var context = AiContext.Build(device with { CredentialReference = "SECRET-REFERENCE", Name = "PRIVATE-NAME", Location = "PRIVATE-LOCATION" }, workspace.GetState(device.Id)); Check(!context.Contains(device.ManagementIp)); Check(!context.Contains("SECRET-REFERENCE")); Check(!context.Contains("Office-01")); Check(!context.Contains("PRIVATE-")); Check(context.Contains("模拟")); });
Test("真实设备无结构化状态时不伪造上下文", () => { var context = AiContext.Build(device with { IsSimulation = false }, workspace.GetState(device.Id)); Check(!context.Contains("GigabitEthernet")); Check(context.Contains("尚无结构化实时采集")); });
Test("AI 地址安全校验及本机服务例外", () => { var p = new AiProfile(Guid.NewGuid(), "测试", AiProtocol.ChatCompletions, "http://example.com", "test", null); Reject(() => AiClient.Validate(p)); Reject(() => AiClient.Validate(p with { BaseUrl = "https://user:secret@example.com" })); Reject(() => AiClient.Validate(p with { BaseUrl = "https://example.com?key=secret" })); AiClient.Validate(p with { BaseUrl = "http://localhost:11434" }); });

foreach (var protocol in Enum.GetValues<AiProtocol>())
    await AsyncTest("AI 协议请求与输出解析：" + protocol, async () =>
    {
        var keyRef = vault.Store("test-key-never-real");
        var fixture = protocol switch
        {
            AiProtocol.Responses => "{\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"中文建议\"}]}]}",
            AiProtocol.ClaudeMessages => "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"隐藏思考\"},{\"type\":\"text\",\"text\":\"中文建议\"}]}",
            AiProtocol.Gemini => "{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"隐藏思考\"},{\"text\":\"中文建议\"}]}}]}",
            AiProtocol.Ollama => "{\"message\":{\"content\":\"中文建议\",\"thinking\":\"隐藏思考\"}}",
            _ => "{\"choices\":[{\"message\":{\"content\":\"中文建议\",\"reasoning_content\":\"隐藏思考\"}}]}"
        };
        using var http = new HttpClient(new StubHandler(async request =>
        {
            var payload = await request.Content!.ReadAsStringAsync();
            Check(!payload.Contains("test-key-never-real")); Check(!payload.Contains("\"tools\""));
            using var json = JsonDocument.Parse(payload);
            Check(request.RequestUri!.Scheme == "https");
            if (protocol == AiProtocol.Responses) { Check(request.RequestUri.AbsolutePath == "/v1/responses"); Check(!json.RootElement.GetProperty("store").GetBoolean()); }
            if (protocol == AiProtocol.ClaudeMessages) Check(request.Headers.Contains("anthropic-version") && request.Headers.Contains("x-api-key"));
            if (protocol == AiProtocol.Gemini) Check(request.Headers.Contains("x-goog-api-key") && !request.RequestUri.Query.Contains("key"));
            return new(HttpStatusCode.OK) { Content = new StringContent(fixture) };
        }));
        var answer = await new AiClient(http, vault).AnalyzeAsync(new(Guid.NewGuid(), "协议测试", protocol, "https://example.com/v1", "test-model", keyRef), "如何排障", "模拟上下文", CancellationToken.None);
        Check(answer == "中文建议");
    });
await AsyncTest("AI 错误不回显服务端密钥", async () =>
{
    using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("server-secret-key") })));
    try { await new AiClient(http, vault).AnalyzeAsync(new(Guid.NewGuid(), "测试", AiProtocol.Ollama, "http://localhost:11434", "model", null), "问题", "上下文", CancellationToken.None); throw new Exception("未拒绝错误响应"); }
    catch (InvalidOperationException ex) { Check(!ex.Message.Contains("server-secret-key")); Check(ex.Message.Contains("401")); }
});
await AsyncTest("SSH 在联网前拒绝缺少指纹的真实设备", async () =>
{
    try { await new SshReadOnlyTransport(vault, drivers).ReadAsync(device with { IsSimulation = false, CredentialReference = vault.Store("{}"), HostFingerprint = null }, ReadCommand.Version, CancellationToken.None); throw new Exception("未拒绝缺少指纹"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("指纹")); }
});

Console.WriteLine($"\n验证结果：通过 {passed} 项，失败 {failed} 项；未连接真实设备或 AI 服务。");
var resolved = Path.GetFullPath(root);
if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("netops-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
return failed == 0 ? 0 : 1;

sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
