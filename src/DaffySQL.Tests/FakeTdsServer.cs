using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DaffySQL.Tds;

namespace DaffySQL.Tests;

/// <summary>
/// A TDS server good for one login: answers PRELOGIN (and TLS, given a certificate), keeps the
/// LOGIN7 it is sent, and answers it with a canned reply. Given a FEDAUTHINFO as well, it plays
/// Azure SQL's Entra ID exchange: asks for a token, keeps the FEDAUTH_TOKEN it gets, then replies.
/// Lifted from DaffyDuo's tests, with the server's halves of PRELOGIN and LOGIN7 - which DaffySQL,
/// only ever a client, does not carry - here instead.
/// </summary>
internal sealed class FakeTdsServer : IAsyncDisposable
{
    public sealed record Received(
        SentLogin Login, string? TlsHostName, IReadOnlyList<(byte Option, byte[] Data)> PreLogin, byte[]? FedAuthToken);

    private readonly TcpListener _listener;

    public FakeTdsServer(byte[] reply, X509Certificate2? certificate = null, IPAddress? address = null, byte[]? fedAuthInfo = null)
    {
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Served = ServeAsync(reply, certificate, fedAuthInfo);
    }

    public int Port { get; }
    public Task<Received> Served { get; }

    /// <summary>The client's PRELOGIN options, kept as soon as they arrive - also when the exchange then fails.</summary>
    public IReadOnlyList<(byte Option, byte[] Data)>? PreLoginSeen { get; private set; }

    private async Task<Received> ServeAsync(byte[] reply, X509Certificate2? certificate, byte[]? fedAuthInfo)
    {
        using var socket = await _listener.AcceptTcpClientAsync();
        Stream stream = socket.GetStream();
        var reader = new TdsMessageReader(stream);

        var preLogin = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no PRELOGIN");
        var requested = PreLogin.Parse(preLogin.Message.Payload);
        PreLoginSeen = requested;
        byte encryption = certificate is null ? TdsConstants.Encryption.NotSupported : TdsConstants.Encryption.On;

        // As Azure SQL: FEDAUTHREQUIRED is answered 1 when the server is going to ask for a token.
        await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult,
            PreLoginAnswer(requested, encryption, fedAuth: fedAuthInfo is not null), CancellationToken.None);

        string? tlsHostName = null;
        if (certificate is not null)
        {
            var shim = new PreLoginTlsStream(stream);
            var tls = new SslStream(shim, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12,
            });
            shim.StopFraming();
            tlsHostName = tls.TargetHostName;
            stream = tls;
            reader.Stream = tls;
        }

        var login = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no LOGIN7");

        byte[]? fedAuthToken = null;
        if (fedAuthInfo is not null)
        {
            await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult, fedAuthInfo, CancellationToken.None);
            var token = await reader.ReadAsync(CancellationToken.None) ?? throw new IOException("no FEDAUTH_TOKEN");
            if (token.Message.Type != TdsConstants.PacketType.FedAuthToken) throw new IOException($"expected FEDAUTH_TOKEN, got {token.Message.Type}");
            fedAuthToken = token.Message.Payload.ToArray();
        }

        await new PacketWriter(stream).WriteMessageAsync(TdsConstants.PacketType.TabularResult, reply, CancellationToken.None);
        return new Received(SentLogin.Parse(login.Message.Payload), tlsHostName, requested, fedAuthToken);
    }

    /// <summary>
    /// The server's PRELOGIN answer: exactly the options asked about, in order - what the ODBC
    /// driver insists on, and SQL Server does. (DaffyDuo's PreLogin.BuildServerResponse.)
    /// </summary>
    private static byte[] PreLoginAnswer(IReadOnlyList<(byte Option, byte[] Data)> requested, byte encryption, bool fedAuth)
    {
        var options = new List<(byte Option, byte[] Data)>();
        foreach (var (option, _) in requested.OrderBy(o => o.Option))
        {
            byte[]? data = option switch
            {
                TdsConstants.PreLoginOption.Version => [16, 0, 0x10, 0x87, 0x00, 0x00],
                TdsConstants.PreLoginOption.Encryption => [encryption],
                TdsConstants.PreLoginOption.InstOpt => [0x00],
                TdsConstants.PreLoginOption.Mars => [0x00],
                TdsConstants.PreLoginOption.FedAuthRequired => [fedAuth ? (byte)1 : (byte)0],
                TdsConstants.PreLoginOption.ThreadId => [],
                _ => null,
            };
            if (data is not null) options.Add((option, data));
        }
        return PreLogin.Build(options);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try { await Served; } catch { }
    }

    public static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // SChannel cannot serve with an ephemeral key; a PKCS#12 round trip gives it one it can.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
    }
}

/// <summary>
/// The LOGIN7 a client sent, read back: the fields a test asserts on. DaffySQL only writes LOGIN7,
/// so the reading half of DaffyDuo's Login7 is here.
/// </summary>
internal sealed record SentLogin(string UserName, string Password, string AppName, string ServerName, string Database, byte[]? FeatureExt)
{
    public IReadOnlyList<byte> FeatureIds => Features().Select(f => f.Id).ToList();

    public byte[] FeatureData(byte id) => Features().First(f => f.Id == id).Data;

    private IEnumerable<(byte Id, byte[] Data)> Features()
    {
        if (FeatureExt is null) yield break;
        for (int position = 0; FeatureExt[position] != TdsConstants.FeatureId.Terminator;)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(FeatureExt.AsSpan(position + 1));
            yield return (FeatureExt[position], FeatureExt[(position + 5)..(position + 5 + length)]);
            position += 5 + length;
        }
    }

    public static SentLogin Parse(ReadOnlySpan<byte> payload)
    {
        byte[]? features = null;
        if ((payload[27] & 0x10) != 0)   // fExtension: the extension slot points at the FeatureExt offset
        {
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[U16(payload, 56)..]);
            features = payload[at..].ToArray();
        }

        return new SentLogin(Text(payload, 40), Unscramble(payload, 44), Text(payload, 48), Text(payload, 52), Text(payload, 68), features);
    }

    private static string Text(ReadOnlySpan<byte> payload, int entry) =>
        Encoding.Unicode.GetString(payload.Slice(U16(payload, entry), U16(payload, entry + 2) * 2));

    private static string Unscramble(ReadOnlySpan<byte> payload, int entry)
    {
        var bytes = payload.Slice(U16(payload, entry), U16(payload, entry + 2) * 2).ToArray();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte x = (byte)(bytes[i] ^ 0xA5);
            bytes[i] = (byte)((x >> 4) | (x << 4));
        }
        return Encoding.Unicode.GetString(bytes);
    }

    private static int U16(ReadOnlySpan<byte> payload, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
}

/// <summary>Login responses, token by token, as a server would send them.</summary>
internal static class LoginReplies
{
    /// <summary>Accepts: database ENVCHANGE, LOGINACK, then any FEATUREEXTACK entries, then DONE.</summary>
    public static byte[] Accepted(string database, params (byte Feature, byte[] Data)[] acknowledged)
    {
        var writer = new TdsWriter();
        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();
        writer.WriteByte(TdsConstants.EnvChangeType.Database);
        writer.WriteBVarChar(database);
        writer.WriteBVarChar("master");
        writer.PatchLengthFrom(length);

        WriteLoginAck(writer);

        if (acknowledged.Length > 0)
        {
            writer.WriteByte(TdsConstants.Token.FeatureExtAck);
            foreach (var (feature, data) in acknowledged)
            {
                writer.WriteByte(feature);
                writer.WriteUInt32((uint)data.Length);
                writer.WriteBytes(data);
            }
            writer.WriteByte(TdsConstants.FeatureId.Terminator);
        }

        WriteDone(writer);
        return writer.ToArray();
    }

    /// <summary>What Azure SQL's gateway answers under the Redirect policy: LOGINACK, then where to go, then DONE.</summary>
    public static byte[] Redirect(string server, int port, byte protocol = 0)
    {
        var writer = new TdsWriter();
        WriteLoginAck(writer);

        writer.WriteByte(TdsConstants.Token.EnvChange);
        int length = writer.ReserveUInt16();
        writer.WriteByte(TdsConstants.EnvChangeType.RoutingInformation);
        int value = writer.ReserveUInt16();
        writer.WriteByte(protocol);
        writer.WriteUInt16((ushort)port);
        writer.WriteUsVarChar(server);
        writer.PatchLengthFrom(value);
        writer.WriteUInt16(0);                            // old value: none
        writer.PatchLengthFrom(length);

        WriteDone(writer);
        return writer.ToArray();
    }

    /// <summary>FEDAUTHINFO: count, then (id, length, offset) entries, then UTF-16LE data - offsets from the count.</summary>
    public static byte[] FedAuthInfo(string stsUrl, string spn)
    {
        byte[] sts = Encoding.Unicode.GetBytes(stsUrl);
        byte[] name = Encoding.Unicode.GetBytes(spn);
        int dataStart = 4 + 2 * 9;

        var writer = new TdsWriter();
        writer.WriteByte(TdsConstants.Token.FedAuthInfo);
        writer.WriteUInt32((uint)(dataStart + sts.Length + name.Length));
        writer.WriteUInt32(2);
        writer.WriteByte(0x01);
        writer.WriteUInt32((uint)sts.Length);
        writer.WriteUInt32((uint)dataStart);
        writer.WriteByte(0x02);
        writer.WriteUInt32((uint)name.Length);
        writer.WriteUInt32((uint)(dataStart + sts.Length));
        writer.WriteBytes(sts);
        writer.WriteBytes(name);
        return writer.ToArray();
    }

    private static void WriteLoginAck(TdsWriter writer)
    {
        writer.WriteByte(TdsConstants.Token.LoginAck);
        int length = writer.ReserveUInt16();
        writer.WriteByte(1);                              // interface: SQL
        writer.WriteBytes([0x74, 0x00, 0x00, 0x04]);      // TDS 7.4, big-endian
        writer.WriteBVarChar("Microsoft SQL Server");
        writer.WriteBytes([12, 0, 0x01, 0x00]);           // 12.0.256, as Azure SQL reports itself
        writer.PatchLengthFrom(length);
    }

    private static void WriteDone(TdsWriter writer)
    {
        writer.WriteByte(TdsConstants.Token.Done);
        writer.WriteUInt16(TdsConstants.DoneStatus.Final);
        writer.WriteUInt16(0);
        writer.WriteUInt64(0);
    }
}
