using System.IO.Ports;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetOps.Application;
using NetOps.Domain;
using Renci.SshNet;

namespace NetOps.Infrastructure;

public static class SshSecurity
{
    public static ConnectionInfo Connection(Device device, ConnectionProfile profile, ISecretVault vault)
    {
        profile.Validate();
        if (profile.CredentialReference is null) throw new InvalidOperationException("请在连接设置中保存用户名和凭据。");
        var secret = JsonSerializer.Deserialize<DeviceCredential>(vault.Read(profile.CredentialReference))!;
        if (string.IsNullOrWhiteSpace(secret.Username)) throw new ArgumentException("SSH 用户名不能为空。");
        AuthenticationMethod auth = string.IsNullOrWhiteSpace(secret.PrivateKey)
            ? new PasswordAuthenticationMethod(secret.Username, secret.Password)
            : new PrivateKeyAuthenticationMethod(secret.Username, new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(secret.PrivateKey)), secret.KeyPassphrase));
        return new ConnectionInfo(device.ManagementIp, profile.Port, secret.Username, auth) { Timeout = TimeSpan.FromSeconds(12) };
    }
    public static void Pin(BaseClient client, ConnectionProfile profile)
    {
        var fingerprint = profile.HostFingerprint?.Trim().Replace("SHA256:", "", StringComparison.Ordinal).TrimEnd('=');
        try { if (fingerprint is null || Convert.FromBase64String(fingerprint + "=").Length != 32) throw new FormatException(); }
        catch (FormatException) { throw new ArgumentException("连接前请填写并核对可信 SHA256 主机指纹。"); }
        client.HostKeyReceived += (_, e) => e.CanTrust = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(e.FingerPrintSHA256.TrimEnd('=')), Encoding.ASCII.GetBytes(fingerprint));
    }
}

public sealed class SessionFactory(ISecretVault vault) : ISessionFactory
{
    public IDeviceSession Create(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => new SshSession(vault), ConnectionProtocol.Console => new SerialSession(),
        ConnectionProtocol.Telnet => new TelnetSession(), _ => throw new NotSupportedException("此协议使用只读结构化请求，请在连接页读取，不能作为交互终端。")
    };
}

public sealed class SshSession(ISecretVault vault) : IDeviceSession
{
    private SshClient? client;
    private ShellStream? shell;
    public bool IsConnected => client?.IsConnected == true;
    public async Task ConnectAsync(Device device, ConnectionProfile profile, CancellationToken ct)
    {
        client = new SshClient(SshSecurity.Connection(device, profile, vault)) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        SshSecurity.Pin(client, profile);
        await client.ConnectAsync(ct);
        shell = await Task.Run(() => client.CreateShellStream("vt100", 160, 40, 1280, 800, 65536), ct);
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (shell is null || !IsConnected) throw new InvalidOperationException("SSH 会话尚未连接。");
        await shell.WriteAsync(Encoding.UTF8.GetBytes(text), ct); shell.Flush();
    }
    public Task<string> ReadAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(shell?.DataAvailable == true ? shell.Read() : "");
    }
    public ValueTask DisposeAsync() { shell?.Dispose(); client?.Dispose(); shell = null; client = null; return ValueTask.CompletedTask; }
}

public sealed class SerialSession : IDeviceSession
{
    private SerialPort? port;
    public bool IsConnected => port?.IsOpen == true;
    public Task ConnectAsync(Device device, ConnectionProfile profile, CancellationToken ct) => Task.Run(() =>
    {
        profile.Validate(); ct.ThrowIfCancellationRequested();
        if (!Enum.TryParse<Parity>(profile.Parity, out var parity) || !Enum.TryParse<StopBits>(profile.StopBits, out var stop) || !Enum.TryParse<Handshake>(profile.FlowControl, out var flow)) throw new ArgumentException("串口校验位、停止位或流控无效。");
        port = new SerialPort(profile.SerialPort, profile.BaudRate, parity, profile.DataBits, stop)
        { Handshake = flow, ReadTimeout = 500, WriteTimeout = 3000, Encoding = Encoding.UTF8 };
        port.Open();
    }, ct);
    public Task SendAsync(string text, CancellationToken ct) => Task.Run(() => { ct.ThrowIfCancellationRequested(); if (port?.IsOpen != true) throw new InvalidOperationException("串口尚未连接。"); port.Write(text); }, ct);
    public Task<string> ReadAvailableAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(port?.IsOpen == true ? port.ReadExisting() : ""); }
    public ValueTask DisposeAsync() { port?.Dispose(); port = null; return ValueTask.CompletedTask; }
}

/// <summary>跨数据包保留 IAC 状态，拒绝未实现的选项；终端类型为 VT100。</summary>
public sealed class TelnetCodec
{
    private int state;
    private byte verb;
    private readonly List<byte> negotiation = [];
    public (byte[] Data, byte[] Reply) Decode(ReadOnlySpan<byte> bytes)
    {
        List<byte> data = [], reply = [];
        foreach (var b in bytes)
        {
            switch (state)
            {
                case 0: if (b == 255) state = 1; else data.Add(b); break;
                case 1:
                    if (b == 255) { data.Add(b); state = 0; }
                    else if (b is 251 or 252 or 253 or 254) { verb = b; state = 2; }
                    else if (b == 250) { negotiation.Clear(); state = 3; }
                    else state = 0;
                    break;
                case 2:
                    if (verb == 251) reply.AddRange([255, (byte)(b is 1 or 3 ? 253 : 254), b]);
                    else if (verb == 253) reply.AddRange([255, (byte)(b is 3 or 24 ? 251 : 252), b]);
                    state = 0; break;
                case 3:
                    if (b == 255) state = 4;
                    else if (negotiation.Count < 4096) negotiation.Add(b);
                    else throw new InvalidOperationException("Telnet 选项协商数据过大。");
                    break;
                case 4:
                    if (b == 240) { if (negotiation.SequenceEqual(new byte[] { 24, 1 })) reply.AddRange([255, 250, 24, 0, 86, 84, 49, 48, 48, 255, 240]); state = 0; }
                    else { if (b == 255) negotiation.Add(255); state = 3; }
                    break;
            }
        }
        return (data.ToArray(), reply.ToArray());
    }
    public static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text).SelectMany(b => b == 255 ? new byte[] { 255, 255 } : [b]).ToArray();
}

public sealed class TelnetSession : IDeviceSession
{
    private TcpClient? client;
    private readonly TelnetCodec codec = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    public bool IsConnected => client?.Connected == true;
    public async Task ConnectAsync(Device device, ConnectionProfile profile, CancellationToken ct)
    {
        profile.Validate(); client = new TcpClient();
        await client.ConnectAsync(device.ManagementIp, profile.Port, ct);
    }
    public async Task SendAsync(string text, CancellationToken ct)
    { if (!IsConnected) throw new InvalidOperationException("Telnet 尚未连接。"); await client!.GetStream().WriteAsync(TelnetCodec.Encode(text), ct); }
    public async Task<string> ReadAvailableAsync(CancellationToken ct)
    {
        if (client is null || !IsConnected) return "";
        if (client.Available == 0)
        {
            if (client.Client.Poll(0, SelectMode.SelectRead)) throw new IOException("Telnet 对端已关闭连接。");
            return "";
        }
        var buffer = new byte[Math.Min(client.Available, 65536)];
        var n = await client.GetStream().ReadAsync(buffer, ct);
        var result = codec.Decode(buffer.AsSpan(0, n));
        if (result.Reply.Length > 0) await client.GetStream().WriteAsync(result.Reply, ct);
        var chars = new char[Encoding.UTF8.GetMaxCharCount(result.Data.Length)];
        var count = decoder.GetChars(result.Data, chars, false);
        return new string(chars, 0, count);
    }
    public ValueTask DisposeAsync() { client?.Dispose(); client = null; return ValueTask.CompletedTask; }
}
