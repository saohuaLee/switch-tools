using NetOps.Domain;

namespace NetOps.Application;

public interface IWorkspace
{
    IReadOnlyList<Device> GetDevices();
    void SaveDevice(Device device);
    DeviceState? GetState(Guid deviceId);
    void SaveState(DeviceState state);
    IReadOnlyList<Snapshot> GetSnapshots(Guid deviceId);
    void AddSnapshot(Snapshot snapshot);
    IReadOnlyList<AuditEntry> GetAudit(Guid deviceId);
    void AddAudit(AuditEntry entry);
    // 在同一事务内核对基线、备份、更新状态并记录审计。
    void CommitSimulation(DeviceState before, DeviceState after, Snapshot backup, AuditEntry audit);
    IReadOnlyList<AiProfile> GetAiProfiles();
    void SaveAiProfile(AiProfile profile);
}
public interface ISecretVault
{
    string Store(string secret);
    string Read(string reference);
    void Remove(string reference);
}
public interface IDeviceDriver
{
    Vendor Vendor { get; }
    string[] Compile(ChangeRequest request);
    string ReadCommand(ReadCommand command);
}
public interface IDriverRegistry
{
    IDeviceDriver Resolve(Device device);
    IReadOnlyList<DriverDescriptor> Descriptors { get; }
}
public interface IReadOnlyTransport
{
    Task<string> ReadAsync(Device device, ReadCommand command, CancellationToken cancellationToken);
}
public interface IAiClient
{
    Task<string> AnalyzeAsync(AiProfile profile, string question, string context, CancellationToken cancellationToken);
}
