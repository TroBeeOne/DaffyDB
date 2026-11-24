using DuckDB.NET.Data;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TdsProxy.Query;
using TdsProxy.Sql;

namespace TdsProxy.Tds;

/// <summary>Listens on a TCP port and speaks TDS well enough for a real SqlClient to connect.</summary>
internal sealed class TdsServer(
    int port, bool trace, IQueryHandler handler,
    X509Certificate2? certificate = null, StartupInfo? startup = null)
{
    /// <summary>
    /// The first byte of a TLS record is 0x16 (handshake), which is not a TDS packet type. That is
    /// what lets one port serve both: peek a byte, and a TDS 8.0 client announces itself.
    /// </summary>
    private const byte TlsHandshake = 0x16;

    /// <summary>TDS 8.0 requires the protocol be named in ALPN; a client that offers it expects it back.</summary>
    private static readonly SslApplicationProtocol Tds8 = new("tds/8.0");

    private int _sessionNumber;

    /// <summary>
    /// <paramref name="onListening"/> runs once the port is bound and the banner is out. The REPL
    /// uses it to know when the terminal is free: starting a prompt before the banner has printed
    /// leaves the two interleaved.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken, Action? onListening = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        // Printed here rather than in Program, so it is only ever shown for a port actually bound.
        if (startup is not null)
        {
            StartupBanner.Print(port, startup);
        }
        else
        {
            Console.WriteLine($"[serve] listening on 127.0.0.1:{port}");
            Console.WriteLine($"[serve] connect with: Server=tcp:127.0.0.1,{port};Encrypt=False;TrustServerCertificate=True;Pooling=False");
        }

        onListening?.Invoke();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                int number = Interlocked.Increment(ref _sessionNumber);
                _ = Task.Run(() => RunSessionAsync(client, number), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Decides whether this connection is TDS 8.0 and, if so, completes the TLS handshake before
    /// any TDS is read.
    ///
    /// This is the easy half of encryption, and the reason it was done first. TDS 8.0 is plain TLS
    /// from byte zero with TDS inside, so the whole protocol loop above runs unchanged over an
    /// SslStream. A 7.4 client looks like a plaintext one here and asks for encryption later, in
    /// PRELOGIN; that upgrade is <see cref="TdsSession.UpgradeToTlsAsync"/>.
    /// </summary>
    private async Task<Stream> MaybeWrapInTlsAsync(TcpClient client, int number)
    {
        var stream = client.GetStream();

        // Peek rather than read: a plaintext client's first byte has to stay in the buffer.
        var probe = new byte[1];
        bool wantsTls = client.Client.Receive(probe, 0, 1, SocketFlags.Peek) > 0 && probe[0] == TlsHandshake;

        if (certificate is null)
        {
            // Worth naming, because the alternative is a desync and a puzzling disconnect.
            if (wantsTls)
                Console.WriteLine($"[s{number}] client opened with TLS but no certificate is configured; restart with --cert");

            return stream;
        }

        if (!wantsTls) return stream;

        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ApplicationProtocols = [Tds8],
            ClientCertificateRequired = false,
        });

        Chatter.Line($"[s{number}] TLS established: {tls.SslProtocol}, " +
                     $"alpn={tls.NegotiatedApplicationProtocol}, cipher={tls.NegotiatedCipherSuite}");
        return tls;
    }

    private async Task RunSessionAsync(TcpClient client, int number)
    {
        Chatter.Line($"[serve] session {number} accepted from {client.Client.RemoteEndPoint}");
        ServerFacts.SessionOpened();
        try
        {
            client.NoDelay = true;
            string clientAddress = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

            await using Stream stream = await MaybeWrapInTlsAsync(client, number);

            // The certificate travels with the session because a 7.4 client only asks for
            // encryption once PRELOGIN arrives, by which point this method is long finished.
            await using var session = new TdsSession(stream, number, trace, handler, clientAddress, certificate);
            await session.RunAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Chatter.Line($"[serve] session {number} disconnected: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(Redact.Text($"[serve] session {number} error: {ex}"));
        }
        finally
        {
            ServerFacts.SessionClosed();
            client.Dispose();
            Chatter.Line($"[serve] session {number} closed");
        }
    }
}

internal sealed class TdsSession(
    Stream stream, int number, bool trace, IQueryHandler handler, string clientAddress = "unknown",
    X509Certificate2? certificate = null) : IAsyncDisposable
{
    private const string ServerName = TSqlTranslator.ServerName;

    /// <summary>
    /// The product name in LOGINACK, the one place DaffyDB still gives SQL Server's. MSOLEDBSQL
    /// checks it: with any other name the login succeeds, and then every command and schema rowset
    /// fails with "The data source can not be used, because it is not a Microsoft SQL Server".
    /// SqlClient and the ODBC driver accept DaffyDB here. The names a person can read -
    /// @@VERSION, xp_msver, sp_server_info - are <see cref="TSqlTranslator.ProductName"/>.
    /// </summary>
    private const string LoginAckProductName = "Microsoft SQL Server";

    /// <summary>SQL Server's own number for a failed login, which clients recognise and report well.</summary>
    private const int LoginFailedNumber = 18456;

    /// <summary>
    /// The transport, which is not fixed for the life of the session. A TDS 7.4 client negotiates
    /// encryption in PRELOGIN, so this becomes an SslStream part-way through; everything below
    /// therefore goes through the field rather than the constructor parameter.
    /// </summary>
    private Stream _stream = stream;

    /// <summary>The 7.4 upgrade, if one happened, kept only so it can be disposed with the session.</summary>
    private SslStream? _upgrade;

    private PacketWriter _writer = new(stream);
    private readonly PacketFramer _framer = new();
    private readonly MessageAssembler _assembler = new();
    private readonly Annotator _annotator = new();

    private string _database = "master";

    /// <summary>Who logged in. Replaced once LOGIN7 arrives, and attached to every request after.</summary>
    private SqlSession _session = SqlSession.Anonymous;

    /// <summary>
    /// The request being answered, if one is. It runs on while the loop goes on reading, because
    /// the only thing a client sends before its answer arrives is an attention - a cancel - and a
    /// cancel that is not read until the answer is complete cannot stop anything.
    /// </summary>
    private Task? _running;
    private SqlRequest? _runningRequest;
    private CancellationTokenSource? _requestCancel;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Read through the field, not the parameter: a 7.4 handshake replaces it mid-loop.
                int read = await _stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;

                foreach (var packet in _framer.Push(buffer.AsSpan(0, read)))
                {
                    if ((packet.Status & TdsConstants.PacketStatus.ResetConnection) != 0)
                        Log("packet carries RESETCONNECTION (pooled connection reuse)");

                    var message = _assembler.Push(packet);
                    if (message is { } complete) await ReceiveAsync(complete, cancellationToken);
                }

                if (_framer.Desynced)
                {
                    Log("stream desynced - unexpected bytes, closing");
                    return;
                }
            }
        }
        finally
        {
            // A client that goes away mid-query has no use for the answer; stop working on it.
            if (_running is { IsCompleted: false })
            {
                Log("client left with a request running; cancelling it");
                await StopRunningAsync();
            }
            await FinishRunningAsync();
        }
    }

    private async Task ReceiveAsync(TdsMessage message, CancellationToken cancellationToken)
    {
        if (message.Type == TdsConstants.PacketType.Attention)
        {
            Log("<- ATTENTION (cancel)");
            if (_running is { IsCompleted: false }) await StopRunningAsync();
            await FinishRunningAsync();

            // The client discards everything until it sees DONE with the ATTN bit.
            await SendDoneAsync(TdsConstants.DoneStatus.Attention, cancellationToken);
            return;
        }

        // Without MARS a client sends nothing more until its answer is complete, so this only
        // waits when a client misbehaves; it keeps two answers from interleaving on the wire.
        await FinishRunningAsync();

        if (message.Type is TdsConstants.PacketType.SqlBatch or TdsConstants.PacketType.Rpc)
        {
            // On its own thread: called directly, a query whose every step completes synchronously
            // would run to the end before this loop got to read anything.
            _requestCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running = Task.Run(() => DispatchAsync(message, cancellationToken), CancellationToken.None);
            return;
        }

        await DispatchAsync(message, cancellationToken);
    }

    /// <summary>
    /// Asks the running request to stop and waits until it has. The handler is asked again every
    /// quarter second: a cancel that lands in the instant before a statement starts can be lost,
    /// and a request that never stopped would leave the client waiting on its cancel for ever.
    /// </summary>
    private async Task StopRunningAsync()
    {
        _requestCancel?.Cancel();

        while (_running is { IsCompleted: false } running)
        {
            if (_runningRequest is { } request) handler.Cancel(request);
            await Task.WhenAny(running, Task.Delay(250, CancellationToken.None));
        }
    }

    private async Task FinishRunningAsync()
    {
        if (_running is null) return;

        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
            Log("request cancelled");
        }
        catch (Exception ex)
        {
            // Writing an answer to a client that has gone; nothing more can be done for it.
            Log($"request ended with {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _running = null;
            _runningRequest = null;
            _requestCancel?.Dispose();
            _requestCancel = null;
        }
    }

    private async Task DispatchAsync(TdsMessage message, CancellationToken cancellationToken)
    {
        Log($"<- {TdsConstants.PacketTypeName(message.Type)} ({message.Payload.Length} bytes, {message.PacketCount} packet(s))");
        if (trace) Console.Write(_annotator.Describe(message));

        try
        {
            switch (message.Type)
            {
                case TdsConstants.PacketType.PreLogin:
                    await HandlePreLoginAsync(message, cancellationToken);
                    break;

                case TdsConstants.PacketType.Login7:
                    await HandleLogin7Async(message, cancellationToken);
                    break;

                case TdsConstants.PacketType.SqlBatch:
                    await HandleRequestAsync(
                        RequestParser.ParseBatch(message.Payload) with { Session = _session }, cancellationToken);
                    break;

                case TdsConstants.PacketType.Rpc:
                    await HandleRequestAsync(
                        RequestParser.ParseRpc(message.Payload) with { Session = _session }, cancellationToken);
                    break;

                case TdsConstants.PacketType.TransactionManager:
                    // Phase 1 runs in autocommit; acknowledge so the client does not stall.
                    await SendDoneAsync(TdsConstants.DoneStatus.Final, cancellationToken);
                    break;

                case TdsConstants.PacketType.Sspi:
                    await SendErrorAsync("integrated authentication is not supported; use SQL authentication",
                                         cancellationToken);
                    break;

                default:
                    await SendErrorAsync($"packet type 0x{message.Type:X2} is not supported by this proxy",
                                         cancellationToken);
                    break;
            }
        }
        catch (TdsQueryException ex)
        {
            // A RAISERROR or THROW chose its own number, severity and state; report them as given
            // so the client sees the error the batch actually raised.
            Log($"rejected: {ex.Message}");
            await SendErrorAsync(ex.Message, cancellationToken, ex.Error.Number, ex.Error.Severity);
        }
        catch (TdsUnsupportedException ex)
        {
            Log($"rejected: {ex.Message}");
            await SendErrorAsync(ex.Message, cancellationToken);
        }
        catch (DuckDBException ex)
        {
            // DuckDB's own message is the most useful thing we can give the client.
            Log($"duckdb error: {ex.Message}");
            await SendErrorAsync(FirstLine(ex.Message), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"handler failed: {ex}");
            await SendErrorAsync($"proxy error: {ex.GetType().Name}: {ex.Message}", cancellationToken);
        }
    }

    /// <summary>
    /// M1: answer PRELOGIN, and decide here whether the rest of the connection is encrypted.
    ///
    /// Three of the protocol's four encryption modes are served. ENCRYPT_NOT_SUP is answered to a
    /// client that wants no encryption, which is the plaintext path; a TDS 8.0 client arrives
    /// already inside TLS and is left alone; and a client asking for ENCRYPT_ON gets a 7.4
    /// handshake started the moment this reply is on the wire.
    ///
    /// The fourth, ENCRYPT_OFF — encrypt the login packet and then revert to plaintext mid-stream —
    /// is deliberately not offered. Reverting means abandoning the SslStream and resuming on the
    /// bare socket with bytes in flight, for a mode no current client asks for. Answering
    /// NOT_SUP instead makes such a client fall back to the plaintext path, exactly as before.
    ///
    /// MARS is answered off so the client does not switch to SMUX framing. A real server sends
    /// this reply as a TABULAR_RESULT packet (0x04), not as another PRELOGIN packet.
    /// </summary>
    private async Task HandlePreLoginAsync(TdsMessage message, CancellationToken cancellationToken)
    {
        var requested = ParsePreLoginOptions(message.Payload);

        byte clientEncryption = requested.TryGetValue(TdsConstants.PreLoginOption.Encryption, out var e) && e.Length > 0
            ? e[0]
            : TdsConstants.Encryption.NotSupported;

        Log($"client ENCRYPTION = 0x{clientEncryption:X2} {TdsConstants.EncryptionName(clientEncryption)}");

        bool wantsTls = clientEncryption is TdsConstants.Encryption.On or TdsConstants.Encryption.Required;

        // A Strict client is already inside TLS and still sends an ENCRYPTION option. Starting a
        // second handshake inside the first would be a very confusing way to break TDS 8.0.
        bool alreadyEncrypted = _stream is SslStream;

        bool upgrade = wantsTls && !alreadyEncrypted && certificate is not null;

        if (wantsTls && !alreadyEncrypted && certificate is null)
            Log("client asked for encryption but no certificate is configured - restart with --cert");

        byte answer = upgrade ? TdsConstants.Encryption.On : TdsConstants.Encryption.NotSupported;

        // A server answers with exactly the options the client asked about, in the same order, and
        // no others. Microsoft.Data.SqlClient tolerates extras; the ODBC driver does not — send it
        // a FEDAUTHREQUIRED it never asked for and it closes the connection without sending LOGIN7.
        var options = new List<(byte Option, byte[] Data)>();
        foreach (byte option in requested.Keys.Order())
        {
            byte[]? data = option switch
            {
                TdsConstants.PreLoginOption.Version => [17, 0, 0x0F, 0xB9, 0x00, 0x00],   // 17.0.4025
                TdsConstants.PreLoginOption.Encryption => [answer],
                TdsConstants.PreLoginOption.InstOpt => [0x00],
                TdsConstants.PreLoginOption.Mars => [0x00],            // MARS on would switch the client to SMUX framing
                TdsConstants.PreLoginOption.FedAuthRequired => [0x00],
                TdsConstants.PreLoginOption.ThreadId => [],            // echoed back empty, as a real server does
                TdsConstants.PreLoginOption.TraceId => [],
                _ => null,
            };

            if (data is not null) options.Add((option, data));
        }

        await SendAsync(TdsConstants.PacketType.TabularResult, BuildPreLogin(options), cancellationToken);
        Log($"-> PRELOGIN response ({TdsConstants.EncryptionName(answer)}, MARS off)");

        if (upgrade) await UpgradeToTlsAsync(cancellationToken);
    }

    /// <summary>
    /// Completes a TDS 7.4 handshake and makes the encrypted stream the session's transport.
    ///
    /// The handshake records are tunnelled inside PRELOGIN packets, which is what
    /// <see cref="PreLoginTlsStream"/> is for. Once it returns, the framing is switched off and
    /// everything afterwards — LOGIN7 included — is ordinary TDS inside ordinary TLS.
    ///
    /// TLS 1.2 is pinned deliberately. In 1.3 the two ends stop framing at different moments,
    /// because the handshake no longer completes symmetrically, and there is no way for either
    /// side to know which packet was the last one that needed a header. Strict does not have this
    /// problem and is not restricted here.
    /// </summary>
    private async Task UpgradeToTlsAsync(CancellationToken cancellationToken)
    {
        // The client cannot send the handshake until it has read the answer above, so anything
        // still buffered would mean the two sides already disagree about where the packets end.
        if (_framer.Pending > 0)
            Log($"warning: {_framer.Pending} byte(s) buffered before the handshake; the stream may desync");

        var shim = new PreLoginTlsStream(_stream);
        var tls = new SslStream(shim, leaveInnerStreamOpen: true);

        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12,
        }, cancellationToken);

        shim.StopFraming();

        _upgrade = tls;
        _stream = tls;
        _writer = new PacketWriter(tls);   // the old one still points at the bare socket

        Log($"TLS established (TDS 7.4): {tls.SslProtocol}, cipher={tls.NegotiatedCipherSuite}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_upgrade is not null) await _upgrade.DisposeAsync();
    }

    /// <summary>
    /// M3: the login response. Token order mirrors a real server: database and language ENVCHANGEs
    /// with their INFO messages, then LOGINACK, then the packet-size ENVCHANGE, then DONE.
    ///
    /// Deliberately omitted: the collation ENVCHANGE (only matters for non-Unicode types) and
    /// FEATUREEXTACK. Not acknowledging the column-encryption feature is what keeps COLMETADATA
    /// free of a CekTable later on.
    /// </summary>
    private async Task HandleLogin7Async(TdsMessage message, CancellationToken cancellationToken)
    {
        var login = Login7.Parse(message.Payload);

        Log($"login: user='{login.UserName}' host='{login.HostName}' app='{login.AppName}' db='{login.Database}' packetSize={login.PacketSize}");

        if (login.UsesIntegratedSecurity)
        {
            await SendErrorAsync("integrated authentication is not supported; use a SQL login", cancellationToken);
            return;
        }

        _session = new SqlSession(
            number, login.UserName, login.Password, login.Database,
            login.HostName, login.AppName, clientAddress, IsEncrypted: _stream is SslStream);

        // The handler gets a say before the client is told it is in. A refusal has to arrive in
        // place of LOGINACK, not after it, or the client believes it is connected.
        var verdict = await handler.OnLoginAsync(_session, cancellationToken);
        if (!verdict.Accepted)
        {
            await SendLoginRefusalAsync(verdict, login.UserName, cancellationToken);
            return;
        }

        // The handler may place the session somewhere other than where it asked to go - daffydb
        // when it named nothing. Whatever it decides is what the client is told below.
        _database = verdict.Database ?? (login.Database.Length > 0 ? login.Database : "master");
        _session = _session with { Database = _database };

        uint packetSize = login.PacketSize is >= 512 and <= 32767 ? login.PacketSize : TdsConstants.DefaultPacketSize;

        var writer = new TdsWriter();
        ResponseWriter.WriteEnvChange(writer, TdsConstants.EnvChangeType.Database, _database, _database);
        ResponseWriter.WriteInfo(writer, 5701, 2, $"Changed database context to '{_database}'.", ServerName);
        ResponseWriter.WriteEnvChangeBytes(writer, TdsConstants.EnvChangeType.SqlCollation, ResponseWriter.DefaultCollation);
        ResponseWriter.WriteEnvChange(writer, TdsConstants.EnvChangeType.Language, "us_english", "");
        ResponseWriter.WriteInfo(writer, 5703, 1, "Changed language setting to us_english.", ServerName);
        ResponseWriter.WriteLoginAck(writer, LoginAckProductName, major: 17, minor: 0, build: 4025);
        ResponseWriter.WriteEnvChange(writer, TdsConstants.EnvChangeType.PacketSize,
                                      packetSize.ToString(), TdsConstants.DefaultPacketSize.ToString());
        ResponseWriter.WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Final, 0, 0);

        await SendAsync(TdsConstants.PacketType.TabularResult, writer.ToArray(), cancellationToken);

        // Only now may we use the larger size: the ENVCHANGE itself must fit the old one.
        _writer.PacketSize = (int)packetSize;
        Log($"-> LOGINACK + ENVCHANGE + DONE (packet size now {packetSize})");
    }

    /// <summary>M4/M5/M8: hand the decoded request to the seam, then encode whatever comes back.</summary>
    private async Task HandleRequestAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        Log($"   {request.Kind}: {Summarize(request.Sql)}");
        foreach (var parameter in request.Parameters) Log($"   param {parameter}");

        // The handler gets the request's own token, which a cancel trips; the answer below is
        // written with the session's, since a write abandoned halfway would corrupt the stream.
        _runningRequest = request;
        var result = await handler.ExecuteAsync(request, _requestCancel?.Token ?? cancellationToken);

        byte[] payload = request.Kind == RequestKind.Rpc
            ? ResponseWriter.BuildRpcResponse(result, ServerName)
            : ResponseWriter.BuildBatchResponse(result, ServerName);

        // What a command had to say besides its rows - "Mounted 4 tables ..." - goes where PRINT
        // output goes: SSMS's Messages tab, and SqlConnection.InfoMessage.
        if (result.Messages.Count > 0)
        {
            var messages = new TdsWriter();
            foreach (string text in result.Messages) ResponseWriter.WriteInfo(messages, 0, 1, text, ServerName);
            payload = [.. messages.ToArray(), .. payload];
        }

        // A USE moved the session. The client hears it the way it heard where it started - an
        // ENVCHANGE and the "Changed database context" message - ahead of the results.
        if (result.Database is { } database)
        {
            var change = new TdsWriter();
            ResponseWriter.WriteEnvChange(change, TdsConstants.EnvChangeType.Database, database, _database);
            ResponseWriter.WriteInfo(change, 5701, 1, $"Changed database context to '{database}'.", ServerName);

            payload = [.. change.ToArray(), .. payload];
            _database = database;
            _session = _session with { Database = database };
        }

        await SendAsync(TdsConstants.PacketType.TabularResult, payload, cancellationToken);
        Log($"-> {result.ResultSets.Count} result set(s): " +
            (result.ResultSets.Count == 0
                ? "none"
                : string.Join(", ", result.ResultSets.Select(r => $"{r.Columns.Count}x{r.Rows.Count}"))));
        foreach (var error in result.Errors) Log($"-> ERROR: {error.Message}");
    }

    private async Task SendErrorAsync(string message, CancellationToken cancellationToken,
                                      int number = SqlErrors.UserErrorNumber, byte severity = 16)
    {
        // A failure can quote the statement that caused it, connection string and all.
        message = Redact.Text(message);

        var writer = new TdsWriter();
        ResponseWriter.WriteError(writer, number, severity, message, ServerName);
        ResponseWriter.WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Error, 0, 0);

        await SendAsync(TdsConstants.PacketType.TabularResult, writer.ToArray(), cancellationToken);
        Log($"-> ERROR: {message}");
    }

    /// <summary>
    /// A refused login, as SQL Server words it. Bad credentials are one error, 18456. A database
    /// that cannot be opened is its own error first - 4060, at severity 11 - and then the same
    /// 18456, because the login failed as a result; clients show the pair.
    /// </summary>
    private async Task SendLoginRefusalAsync(LoginVerdict verdict, string userName, CancellationToken cancellationToken)
    {
        string reason = verdict.Reason ?? "login rejected";

        var writer = new TdsWriter();
        if (verdict.Number != LoginFailedNumber)
        {
            ResponseWriter.WriteError(writer, verdict.Number, 11, reason, ServerName);
            reason = $"Login failed for user '{userName}'.";
        }
        ResponseWriter.WriteError(writer, LoginFailedNumber, 14, reason, ServerName);
        ResponseWriter.WriteDone(writer, TdsConstants.Token.Done, TdsConstants.DoneStatus.Error, 0, 0);

        await SendAsync(TdsConstants.PacketType.TabularResult, writer.ToArray(), cancellationToken);
        Log($"-> login refused: {verdict.Reason}");
    }

    private async Task SendDoneAsync(ushort status, CancellationToken cancellationToken)
    {
        var writer = new TdsWriter();
        ResponseWriter.WriteDone(writer, TdsConstants.Token.Done, status, 0, 0);
        await SendAsync(TdsConstants.PacketType.TabularResult, writer.ToArray(), cancellationToken);
    }

    private async Task SendAsync(byte type, byte[] payload, CancellationToken cancellationToken)
    {
        _writer.ResetPacketId();
        await _writer.WriteMessageAsync(type, payload, cancellationToken);
    }

    /// <summary>
    /// PRELOGIN is an offset/length option table followed by the option payloads. Unlike the rest
    /// of TDS, its offsets and lengths are big-endian.
    /// </summary>
    private static byte[] BuildPreLogin(List<(byte Option, byte[] Data)> options)
    {
        var writer = new TdsWriter();
        int dataOffset = options.Count * 5 + 1;   // 5 bytes per entry plus the terminator

        foreach (var (option, data) in options)
        {
            writer.WriteByte(option);
            writer.WriteUInt16BigEndian((ushort)dataOffset);
            writer.WriteUInt16BigEndian((ushort)data.Length);
            dataOffset += data.Length;
        }
        writer.WriteByte(TdsConstants.PreLoginOption.Terminator);

        foreach (var (_, data) in options) writer.WriteBytes(data);

        return writer.ToArray();
    }

    /// <summary>Reads the client's PRELOGIN option table into option id -> raw value.</summary>
    private static Dictionary<byte, byte[]> ParsePreLoginOptions(byte[] payload)
    {
        var options = new Dictionary<byte, byte[]>();
        var cursor = new TdsCursor(payload);

        while (cursor.Remaining >= 1)
        {
            byte option = cursor.ReadByte();
            if (option == TdsConstants.PreLoginOption.Terminator) break;
            if (cursor.Remaining < 4) break;

            ushort offset = cursor.ReadUInt16BigEndian();
            ushort length = cursor.ReadUInt16BigEndian();
            if (offset + length > payload.Length) break;

            options[option] = payload.AsSpan(offset, length).ToArray();
        }

        return options;
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].TrimEnd();
    }

    private static string Summarize(string sql)
    {
        // Masked before it is cut short, or the cut could leave half a password unrecognised.
        string flat = Redact.Text(sql).ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length <= 120 ? flat : flat[..117] + "...";
    }

    private void Log(string text) => Chatter.Line($"[s{number}] {text}");
}
