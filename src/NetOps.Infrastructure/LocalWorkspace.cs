using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public sealed class DpapiVault(string root) : ISecretVault
{
    public string Store(string secret)
    {
        Directory.CreateDirectory(root);
        var reference = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(root, reference + ".dpapi"), Protect(secret));
        return reference;
    }
    public string Read(string reference) => Unprotect(File.ReadAllBytes(Resolve(reference)));
    public void Remove(string reference) => File.Delete(Resolve(reference));
    private string Resolve(string reference)
    {
        if (!Guid.TryParseExact(reference, "N", out _)) throw new ArgumentException("凭据引用无效。");
        return Path.Combine(root, reference + ".dpapi");
    }
    public static byte[] Protect(string value) => ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
    public static string Unprotect(byte[] value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser));
}

public sealed class LocalWorkspace : IWorkspace
{
    private readonly string connectionString;
    public LocalWorkspace(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO metadata VALUES ('schema_version','1');
            CREATE TABLE IF NOT EXISTS devices (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS states (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, created TEXT NOT NULL, payload BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, created TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ai_profiles (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private List<T> ReadAll<T>(string sql, string? deviceId = null, bool encrypted = false)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        if (deviceId is not null) cmd.Parameters.AddWithValue("$device", deviceId);
        using var reader = cmd.ExecuteReader(); List<T> result = [];
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(encrypted ? DpapiVault.Unprotect((byte[])reader[0]) : reader.GetString(0))!);
        return result;
    }
    private void Save(string table, Guid id, object payload)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        // table 只由本类内部常量提供。
        cmd.CommandText = $"INSERT INTO {table}(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload)); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<Device> GetDevices() => ReadAll<Device>("SELECT payload FROM devices ORDER BY rowid");
    public void SaveDevice(Device device) { device.Validate(); Save("devices", device.Id, device); }
    public void ImportDevices(IReadOnlyList<Device> devices)
    {
        if (devices.Count is < 1 or > 1000) throw new ArgumentException("每次导入 1–1000 台设备。");
        foreach (var d in devices) { d.Validate(); foreach (var p in d.GetConnections()) p.Validate(); }
        using var db = Open(); using var tx = db.BeginTransaction();
        using var read = db.CreateCommand(); read.Transaction = tx; read.CommandText = "SELECT payload FROM devices";
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var rows = read.ExecuteReader()) while (rows.Read()) { var d = JsonSerializer.Deserialize<Device>(rows.GetString(0))!; if (System.Net.IPAddress.TryParse(d.ManagementIp, out var ip)) addresses.Add(ip.ToString()); }
        foreach (var d in devices)
        {
            if (System.Net.IPAddress.TryParse(d.ManagementIp, out var ip) && !addresses.Add(ip.ToString())) throw new ArgumentException($"管理地址 {ip} 已存在或在清单中重复；本次导入未保存任何设备。");
            using var insert = db.CreateCommand(); insert.Transaction = tx; insert.CommandText = "INSERT INTO devices(id,payload) VALUES($id,$payload)"; insert.Parameters.AddWithValue("$id", d.Id.ToString()); insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(d)); insert.ExecuteNonQuery();
            if (DemoWorkspace.CreateState(d) is { } state)
            {
                using var stateInsert = db.CreateCommand(); stateInsert.Transaction = tx; stateInsert.CommandText = "INSERT INTO states(id,payload) VALUES($id,$payload)"; stateInsert.Parameters.AddWithValue("$id", d.Id.ToString()); stateInsert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state)); stateInsert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }
    public DeviceState? GetState(Guid deviceId) => ReadAll<DeviceState>("SELECT payload FROM states WHERE id=$device", deviceId.ToString()).SingleOrDefault();
    public void SaveState(DeviceState state) => Save("states", state.DeviceId, state);
    public IReadOnlyList<Snapshot> GetSnapshots(Guid deviceId) => ReadAll<Snapshot>("SELECT payload FROM snapshots WHERE device_id=$device ORDER BY created DESC", deviceId.ToString(), true);
    public IReadOnlyList<AuditEntry> GetAudit(Guid deviceId) => ReadAll<AuditEntry>("SELECT payload FROM audit WHERE device_id=$device ORDER BY created DESC", deviceId.ToString());
    public IReadOnlyList<AiProfile> GetAiProfiles() => ReadAll<AiProfile>("SELECT payload FROM ai_profiles ORDER BY rowid");
    public void SaveAiProfile(AiProfile profile) => Save("ai_profiles", profile.Id, profile);
    public void AddSnapshot(Snapshot snapshot) { using var db = Open(); InsertEvent(db, null, "snapshots", snapshot.Id, snapshot.DeviceId, snapshot.CreatedAt, DpapiVault.Protect(JsonSerializer.Serialize(snapshot))); }
    public void AddAudit(AuditEntry entry) { using var db = Open(); InsertEvent(db, null, "audit", entry.Id, entry.DeviceId, entry.CreatedAt, JsonSerializer.Serialize(entry)); }
    private static void InsertEvent(SqliteConnection db, SqliteTransaction? tx, string table, Guid id, Guid deviceId, DateTimeOffset time, object payload)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table}(id,device_id,created,payload) VALUES($id,$device,$created,$payload)";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$device", deviceId.ToString());
        cmd.Parameters.AddWithValue("$created", time.ToString("O")); cmd.Parameters.AddWithValue("$payload", payload); cmd.ExecuteNonQuery();
    }
    public void CommitSimulation(DeviceState before, DeviceState after, Snapshot backup, AuditEntry audit)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var read = db.CreateCommand(); read.Transaction = tx;
        read.CommandText = "SELECT payload FROM states WHERE id=$id"; read.Parameters.AddWithValue("$id", before.DeviceId.ToString());
        var current = read.ExecuteScalar() as string;
        if (current is null || ChangeEngine.Hash(JsonSerializer.Deserialize<DeviceState>(current)!) != ChangeEngine.Hash(before))
            throw new InvalidOperationException("配置已被其他操作更新，请重新预览。");
        using var deviceRead = db.CreateCommand(); deviceRead.Transaction = tx;
        deviceRead.CommandText = "SELECT payload FROM devices WHERE id=$id"; deviceRead.Parameters.AddWithValue("$id", before.DeviceId.ToString());
        var device = JsonSerializer.Deserialize<Device>((string)deviceRead.ExecuteScalar()!);
        if (device?.IsSimulation != true) throw new InvalidOperationException("事务拒绝修改真实设备。");
        InsertEvent(db, tx, "snapshots", backup.Id, backup.DeviceId, backup.CreatedAt, DpapiVault.Protect(JsonSerializer.Serialize(backup)));
        using var write = db.CreateCommand(); write.Transaction = tx;
        write.CommandText = "UPDATE states SET payload=$payload WHERE id=$id";
        write.Parameters.AddWithValue("$id", after.DeviceId.ToString()); write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(after)); write.ExecuteNonQuery();
        InsertEvent(db, tx, "audit", audit.Id, audit.DeviceId, audit.CreatedAt, JsonSerializer.Serialize(audit));
        tx.Commit();
    }
}

public static class DemoWorkspace
{
    public static void Seed(IWorkspace workspace)
    {
        if (workspace.GetDevices().Count != 0) return;
        Add(workspace, new(Guid.NewGuid(), "园区核心交换机（模拟）", "192.0.2.10", Vendor.Huawei, "S5735 · 模拟档案", "VRP · 未经真机验证", DeviceRole.Switch, "总部 · 核心机房", ManagementVlan: 99));
        Add(workspace, new(Guid.NewGuid(), "办公区接入交换机（模拟）", "192.0.2.20", Vendor.H3C, "S5130 · 模拟档案", "Comware · 未经真机验证", DeviceRole.Switch, "总部 · 二层弱电间", ManagementVlan: 99));
        var stack = new Device(Guid.NewGuid(), "研发区堆叠交换机（模拟）", "192.0.2.30", Vendor.Cisco, "Catalyst · 双成员模拟档案", "IOS · 未经真机验证", DeviceRole.Switch, "研发中心", ManagementVlan: 99);
        Add(workspace, stack);
        var baseline = workspace.GetState(stack.Id)!;
        var ports = Enumerable.Range(1, 2).SelectMany(member => Enumerable.Range(1, member == 1 ? 24 : 48).Select(i => new PortState($"GigabitEthernet{member}/0/{i}", $"Member{member}-Desk{i:00}", true, i <= 12, 1000, PortMode.Access, 10, []))).ToArray();
        workspace.SaveState(baseline with { Ports = ports, StackMembers = [new(1, "Active", "模拟在线", "24 口模拟成员", PortCount: 24, Online: true), new(2, "Standby", "模拟在线", "48 口模拟成员", PortCount: 48, Online: true)] });
    }
    public static void Add(IWorkspace workspace, Device device)
    {
        workspace.SaveDevice(device);
        if (CreateState(device) is { } state) workspace.SaveState(state);
    }
    public static DeviceState? CreateState(Device device)
    {
        if (!device.IsSimulation || device.Role != DeviceRole.Switch) return null;
        var prefix = device.Vendor == Vendor.Cisco ? "GigabitEthernet" : "GigabitEthernet";
        var vlans = new[] { new VlanState(1, "Default"), new(10, "Office"), new(20, "Server"), new(device.ManagementVlan, "Management") }.DistinctBy(v => v.Id).OrderBy(v => v.Id).ToArray();
        var ports = Enumerable.Range(1, 24).Select(i => new PortState($"{prefix}1/0/{i}", i == 24 ? "Core-Uplink" : $"Office-{i:00}", true, i <= 16 || i == 24,
            1000, i == 24 ? PortMode.Trunk : PortMode.Access, i == 24 ? device.ManagementVlan : 10,
            i == 24 ? vlans.Select(v => v.Id).ToArray() : [], i == 24)).ToArray();
        return new(device.Id, 1, ports, vlans);
    }
}
