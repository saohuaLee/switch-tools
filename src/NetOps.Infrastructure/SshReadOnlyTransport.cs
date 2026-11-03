using System.Text.Json;
using System.Text.RegularExpressions;
using NetOps.Application;
using NetOps.Domain;
using Renci.SshNet;

namespace NetOps.Infrastructure;

/// <summary>仅提供固定只读命令，无任意命令执行接口。当前使用 exec 通道，交互式设备可能不兼容。</summary>
public sealed partial class SshReadOnlyTransport(ISecretVault vault, IDriverRegistry drivers) : IReadOnlyTransport
{
    public Task<string> ReadAsync(Device device, ReadCommand command, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        device.Validate();
        if (device.IsSimulation) throw new InvalidOperationException("模拟设备不建立 SSH 连接。");
        if (string.IsNullOrWhiteSpace(device.CredentialReference)) throw new InvalidOperationException("请先保存设备登录凭据。");
        var fingerprint = device.HostFingerprint?.Trim().Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
        if (fingerprint is null || !FingerprintPattern().IsMatch(fingerprint)) throw new InvalidOperationException("请先从可信渠道核对并填写 SHA256 主机指纹。");
        var readCommand = drivers.Resolve(device).ReadCommand(command);
        var credential = JsonSerializer.Deserialize<DeviceCredential>(vault.Read(device.CredentialReference))!;
        using var client = new SshClient(device.ManagementIp, device.SshPort, credential.Username, credential.Password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(12);
        client.HostKeyReceived += (_, args) => args.CanTrust = string.Equals(args.FingerPrintSHA256.TrimEnd('='), fingerprint, StringComparison.Ordinal);
        await client.ConnectAsync(cancellationToken);
        using var cmd = client.CreateCommand(readCommand);
        cmd.CommandTimeout = TimeSpan.FromSeconds(25);
        await cmd.ExecuteAsync(cancellationToken);
        if (cmd.ExitStatus is not (null or 0) || !string.IsNullOrWhiteSpace(cmd.Error)) throw new InvalidOperationException("设备拒绝只读命令，请检查权限与驱动兼容性。");
        var text = cmd.Result;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("设备未返回内容，可能不支持 SSH exec 通道。");
        if (text.Length > 2_000_000) throw new InvalidOperationException("设备返回超过 2 MB，本版本不保存此结果。");
        if (IncompleteOutput().IsMatch(text)) throw new InvalidOperationException("返回包含分页或命令错误，未保存不完整配置；交互式分页适配待完成。");
        return text;
    }, cancellationToken);
    [GeneratedRegex(@"^[A-Za-z0-9+/]{43}$")] private static partial Regex FingerprintPattern();
    [GeneratedRegex(@"--\s*More\s*--|----\s*More\s*----|%\s*(Invalid|Error|Unknown)|Unrecognized command|Error:", RegexOptions.IgnoreCase)] private static partial Regex IncompleteOutput();
}
