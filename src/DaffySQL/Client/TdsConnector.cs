using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DaffySQL.Tds;

namespace DaffySQL.Client;

/// <summary>Why a login did not produce a usable session.</summary>
internal enum ConnectFailure
{
    /// <summary>TCP connect failed or timed out.</summary>
    Unreachable,

    /// <summary>The two sides could not agree on encryption, or the TLS handshake failed.</summary>
    Security,

    /// <summary>The server answered LOGIN7 with an error: bad password, no such database, disabled login.</summary>
    LoginRejected,

    /// <summary>The server redirected the login a second time, after the one redirect that is followed.</summary>
    Routed,

    /// <summary>The server said something that is not a valid TDS login exchange.</summary>
    Protocol,

    /// <summary>
    /// No Entra ID access token: Entra refused the application or its secret, could not be reached,
    /// or the server asked for one from somewhere that is not Entra ID.
    /// </summary>
    Token,
}

internal sealed class ConnectException(ConnectFailure failure, string message, LoginResponse? response = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectFailure Failure { get; } = failure;
    public LoginResponse? Response { get; } = response;
}

/// <summary>
/// DaffySQL as a TDS client: connect to a SQL Server, negotiate PRELOGIN and TLS, send a LOGIN7,
/// and read the answer. Lifted from DaffyDuo's UpstreamConnector; what DaffyDuo needed only as a
/// relay - the client's own LOGIN7, the login response kept raw to pass on - is left behind.
///
/// The TLS handshake for TDS 7.4 travels inside PRELOGIN packets, so SslStream is handed the
/// <see cref="PreLoginTlsStream"/> shim, which frames and unframes it. TLS 1.2 is pinned for 7.4,
/// as SqlClient pins it.
///
/// A server may answer the login with "go there instead" - a routing ENVCHANGE, which is how Azure
/// SQL's Redirect connection policy and an availability group's read-only routing work. It is
/// followed: the login goes again where it was sent, once, as SqlClient follows it once.
///
/// With an Entra ID application as the login, the exchange is SqlClient's for
/// Authentication=ActiveDirectoryServicePrincipal: PRELOGIN says FEDAUTHREQUIRED, LOGIN7 carries a
/// FEDAUTH feature instead of a user name and password, the server answers with FEDAUTHINFO (where
/// to get a token, and for what), and the token goes in a FEDAUTH_TOKEN message before the real
/// login response arrives.
/// </summary>
internal static class TdsConnector
{
    private static readonly SslApplicationProtocol Tds8 = new("tds/8.0");

    /// <summary>
    /// One server on the way to a logged-in session: the one named, or where it redirected the
    /// login. <paramref name="RedirectedFrom"/> is the certificate name of the server that sent
    /// DaffySQL here, which this one's certificate may carry instead of its own (see AuthenticateAsync).
    /// </summary>
    private sealed record Hop(string Host, int Port, string CertificateHost, string? RedirectedFrom = null)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    public static async Task<TdsSession> ConnectAsync(
        ServerEndpoint server, CancellationToken cancellationToken, IAccessTokenSource? tokens = null)
    {
        tokens ??= EntraTokenSource.Shared;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(server.ConnectTimeoutSeconds));

        var login = CreateLogin(server);
        var hop = new Hop(server.Host, server.Port, server.HostNameInCertificate ?? server.Host);
        RoutingTarget? routedTo = null;

        while (true)
        {
            try
            {
                return await ConnectToHopAsync(server, hop, login, routedTo, tokens, timeout, cancellationToken, stopwatch);
            }
            catch (ConnectException ex) when (ex.Failure == ConnectFailure.Routed && ex.Response?.Routing is { } routing)
            {
                if (routedTo is not null)
                    throw new ConnectException(ConnectFailure.Routed,
                        $"{server.Host}:{server.Port} redirected the login to {routedTo}, which redirected it again, to {routing}; " +
                        "only one redirect per login is followed", ex.Response);

                if (routing.Protocol != 0 || routing.Port == 0 || routing.Server.Length == 0)
                    throw new ConnectException(ConnectFailure.Protocol,
                        $"the server redirected the login to an address that cannot be used ('{routing}', protocol {routing.Protocol})",
                        ex.Response);

                Log.Debug("login.redirected", $"{hop} redirected the login to {routing}; following");
                routedTo = routing;
                hop = new Hop(routing.Server, routing.Port, routing.Server, RedirectedFrom: hop.CertificateHost);

                // SqlClient names the server it was sent to, with its port, in the second LOGIN7.
                login = login.Clone();
                login.ServerName = $"{routing.Server},{routing.Port}";
            }
        }
    }

    /// <summary>
    /// The LOGIN7 DaffySQL sends, shaped like Microsoft.Data.SqlClient's, so a server treats it no
    /// differently. ODBC_ON makes the server start the session with the ANSI settings every modern
    /// client expects (ANSI_NULLS, QUOTED_IDENTIFIER and the rest). UTF8SUPPORT is asked for so that
    /// varchar in a UTF-8 collation arrives as the UTF-8 it is, which DuckDB takes as it comes.
    /// </summary>
    internal static Login7 CreateLogin(ServerEndpoint server)
    {
        var login = new Login7
        {
            TdsVersion = 0x74000004,
            PacketSize = (uint)server.PacketSize,
            ClientProgVer = 0x06000000,
            ClientPid = (uint)Environment.ProcessId,
            OptionFlags1 = 0xE0,        // USE_DB_ON | INIT_DB_FATAL | SET_LANG_ON
            OptionFlags2 = 0x03,        // INIT_LANG_FATAL | ODBC_ON
            OptionFlags3 = 0x08,        // UNKNOWN_COLLATION_HANDLING
            ClientLcid = 0x0409,
            HostName = Environment.MachineName,
            AppName = server.ApplicationName ?? ServerEndpoint.DefaultApplicationName,
            ClientInterfaceName = "DaffySQL",
            ServerName = server.HostNameInCertificate ?? server.Host,
            Database = server.Database ?? "",

            // An Entra ID login names no user and carries no password: the token says who it is.
            UserName = server.IsEntra ? "" : server.UserName,
            Password = server.IsEntra ? "" : server.Password,
        };
        login.AddFeature(TdsConstants.FeatureId.Utf8Support, []);
        return login;
    }

    private static async Task<TdsSession> ConnectToHopAsync(
        ServerEndpoint server, Hop hop, Login7 login, RoutingTarget? routedTo, IAccessTokenSource tokens,
        CancellationTokenSource timeout, CancellationToken cancellationToken, Stopwatch stopwatch)
    {
        for (int attempt = 1; ; attempt++)
        {
            var progress = new AttemptProgress();
            try
            {
                return await AttemptAsync(server, hop, login, routedTo, tokens, timeout.Token, cancellationToken, progress, stopwatch);
            }
            catch (ConnectException ex) when (attempt < MaxAttempts && !progress.LoginSent && !timeout.IsCancellationRequested
                                              && ex.Failure is ConnectFailure.Unreachable or ConnectFailure.Protocol)
            {
                Log.Debug("login.retry", $"{hop} failed before login on attempt {attempt} ({ex.Message}); retrying");
                try { await Task.Delay(attempt == 1 ? 50 : 250, timeout.Token); }
                catch (OperationCanceledException) { throw ex; }
            }
        }
    }

    /// <summary>
    /// Attempts made at connecting. A failure BEFORE LOGIN7 is sent - the connect itself, a peer that
    /// hangs up during PRELOGIN or the TLS handshake - is retried: nothing has been presented and
    /// nothing on the server has happened, and it is just what a port proxy, load balancer or busy
    /// listener does under a burst of connections. Once LOGIN7 is on the wire there is no retry.
    /// </summary>
    private const int MaxAttempts = 3;

    private sealed class AttemptProgress
    {
        public bool LoginSent;
    }

    private static async Task<TdsSession> AttemptAsync(
        ServerEndpoint server, Hop hop, Login7 login, RoutingTarget? routedTo, IAccessTokenSource tokens,
        CancellationToken token, CancellationToken cancellationToken, AttemptProgress progress, Stopwatch stopwatch)
    {
        var client = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        try
        {
            try
            {
                await client.ConnectAsync(hop.Host, hop.Port, token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.Unreachable,
                    ex is OperationCanceledException
                        ? $"could not connect to {hop} within {server.ConnectTimeoutSeconds}s"
                        : $"could not connect to {hop}: {ex.Message}", inner: ex);
            }

            SocketTuning.EnableKeepAlive(client.Client);
            Stream stream = client.GetStream();

            // Strict: TLS from byte zero, with the protocol named in ALPN, then plain TDS inside it.
            if (server.Encrypt == ServerEncrypt.Strict)
            {
                tls = new SslStream(stream, leaveInnerStreamOpen: true);
                await AuthenticateAsync(tls, server, hop, strict: true, token);
                stream = tls;
            }

            var reader = new TdsMessageReader(stream);
            var writer = new PacketWriter(stream);

            // Under Strict the session is already encrypted, so PRELOGIN says NOT_SUP - which is what
            // SqlClient sends too - to stop the server starting a second handshake inside the first.
            byte encryption = server.Encrypt == ServerEncrypt.Mandatory
                ? TdsConstants.Encryption.On
                : TdsConstants.Encryption.NotSupported;

            await writer.WriteMessageAsync(TdsConstants.PacketType.PreLogin,
                PreLogin.BuildClientRequest(encryption, mars: false, fedAuth: server.IsEntra), token);

            var preLogin = await reader.ReadAsync(token)
                ?? throw new ConnectException(ConnectFailure.Protocol, "the server closed the connection during PRELOGIN");

            var answer = PreLogin.Parse(preLogin.Message.Payload);
            byte serverEncryption = PreLogin.Value(answer, TdsConstants.PreLoginOption.Encryption) ?? TdsConstants.Encryption.NotSupported;

            if (server.Encrypt == ServerEncrypt.Mandatory)
            {
                if (serverEncryption is not (TdsConstants.Encryption.On or TdsConstants.Encryption.Required))
                    throw new ConnectException(ConnectFailure.Security,
                        $"the server does not support encryption (it answered {TdsConstants.EncryptionName(serverEncryption)}); " +
                        "install a certificate on it, or set Encrypt=Optional in the connection string to connect unencrypted");

                var shim = new PreLoginTlsStream(stream);
                tls = new SslStream(shim, leaveInnerStreamOpen: true);
                await AuthenticateAsync(tls, server, hop, strict: false, token);
                shim.StopFraming();

                stream = tls;
                reader.Stream = tls;
                writer = new PacketWriter(tls);
            }
            else if (server.Encrypt == ServerEncrypt.Optional && serverEncryption == TdsConstants.Encryption.Required)
            {
                throw new ConnectException(ConnectFailure.Security,
                    "the server requires encryption; set Encrypt=Mandatory in the connection string");
            }

            if (server.IsEntra) login = WithFedAuth(login, echo: PreLogin.Value(answer, TdsConstants.PreLoginOption.FedAuthRequired) == 1);

            progress.LoginSent = true;
            await writer.WriteMessageAsync(TdsConstants.PacketType.Login7, login.Build(), token);

            var loginReply = await reader.ReadAsync(token)
                ?? throw new ConnectException(ConnectFailure.Protocol, "the server closed the connection after LOGIN7");

            var response = LoginResponse.Parse(loginReply.Message.Payload);

            if (response.FedAuth is { } fedAuth)
            {
                if (!server.IsEntra)
                    throw new ConnectException(ConnectFailure.Protocol, "the server asked for an Entra ID token, which a SQL login does not use");

                string accessToken = await tokens.GetTokenAsync(server, fedAuth, token);
                byte[]? nonce = answer.FirstOrDefault(o => o.Option == TdsConstants.PreLoginOption.NonceOpt).Data;
                await writer.WriteMessageAsync(TdsConstants.PacketType.FedAuthToken, FedAuthToken(accessToken, nonce), token);

                loginReply = await reader.ReadAsync(token)
                    ?? throw new ConnectException(ConnectFailure.Protocol, "the server closed the connection after the Entra ID token");
                response = LoginResponse.Parse(loginReply.Message.Payload);
            }

            // Checked before success: a redirect can come with a LOGINACK, but the session is not here.
            if (response.Routing is not null)
                throw new ConnectException(ConnectFailure.Routed, $"{hop} redirected the login to {response.Routing}", response);

            if (!response.Succeeded)
                throw new ConnectException(ConnectFailure.LoginRejected,
                    response.Errors.Count > 0
                        ? string.Join("; ", response.Errors)
                        : "the server did not acknowledge the login",
                    response);

            if (reader.Pending > 0)
                throw new ConnectException(ConnectFailure.Protocol, $"the server sent {reader.Pending} bytes nobody asked for after the login");

            var session = new TdsSession(client, stream, tls, response, loginReply.Spid, routedTo);
            Log.Debug("login.ok", $"{server.Describe()} as SPID {session.Spid}: {response.ServerProgram} {response.ServerVersion}, " +
                                  $"{session.Tls ?? "unencrypted"}, packets of {session.PacketSize} bytes, in {stopwatch.ElapsedMilliseconds} ms");
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await DisposeQuietly(tls, client);
            throw new ConnectException(ConnectFailure.Unreachable,
                $"the server did not complete the login within {server.ConnectTimeoutSeconds}s");
        }
        catch (ConnectException)
        {
            await DisposeQuietly(tls, client);
            throw;
        }
        catch (Exception ex) when (ex is AuthenticationException)
        {
            await DisposeQuietly(tls, client);

            // A peer that simply hung up mid-handshake is a connection problem, not a certificate one.
            throw ex.InnerException is IOException
                ? new ConnectException(ConnectFailure.Protocol, $"the server closed the connection during the TLS handshake: {Innermost(ex)}", inner: ex)
                : new ConnectException(ConnectFailure.Security, $"TLS with the server failed: {Innermost(ex)}", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            await DisposeQuietly(tls, client);
            throw new ConnectException(ConnectFailure.Protocol, $"the connection to the server failed: {Innermost(ex)}", inner: ex);
        }
        catch
        {
            await DisposeQuietly(tls, client);
            throw;
        }
    }

    /// <summary>
    /// Adds the FEDAUTH feature as SqlClient does for a service principal: the MSAL library (2) in
    /// the upper bits of the options byte, the echo of the server's FEDAUTHREQUIRED in the lowest,
    /// then the workflow - 1, the byte SqlClient uses for a service principal.
    /// </summary>
    private static Login7 WithFedAuth(Login7 login, bool echo)
    {
        var withFedAuth = login.Clone();
        withFedAuth.AddFeature(TdsConstants.FeatureId.FedAuth, [(byte)((0x02 << 1) | (echo ? 1 : 0)), 0x01]);
        return withFedAuth;
    }

    /// <summary>
    /// FEDAUTH_TOKEN: the length of what follows, the token as UTF-16LE with its own length in
    /// front, and the nonce the server sent in PRELOGIN if it sent one.
    /// </summary>
    internal static byte[] FedAuthToken(string accessToken, byte[]? nonce)
    {
        byte[] token = Encoding.Unicode.GetBytes(accessToken);
        bool withNonce = nonce is { Length: 32 };

        var writer = new TdsWriter();
        writer.WriteUInt32((uint)(4 + token.Length + (withNonce ? 32 : 0)));
        writer.WriteUInt32((uint)token.Length);
        writer.WriteBytes(token);
        if (withNonce) writer.WriteBytes(nonce);
        return writer.ToArray();
    }

    /// <summary>
    /// TLS as a client. The certificate must chain to a trusted issuer and name the server - or, on
    /// a redirected hop, either that server or the one that redirected the login: a trusted
    /// certificate for the server the connection string names is proof enough that this is part of it.
    /// </summary>
    private static async Task AuthenticateAsync(SslStream tls, ServerEndpoint server, Hop hop, bool strict, CancellationToken token)
    {
        SslPolicyErrors rejected = SslPolicyErrors.None;
        string? issuedTo = null;

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = hop.CertificateHost,
            EnabledSslProtocols = strict ? SslProtocols.None : SslProtocols.Tls12,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (server.TrustServerCertificate || errors == SslPolicyErrors.None) return true;

                var presented = certificate as X509Certificate2;
                if (errors == SslPolicyErrors.RemoteCertificateNameMismatch && hop.RedirectedFrom is { } original
                    && presented?.MatchesHostname(original) == true)
                {
                    Log.Debug("login.certificate", $"{hop.Host}'s certificate names {original}, the server that redirected the login; accepted");
                    return true;
                }

                rejected = errors;
                issuedTo = presented?.GetNameInfo(X509NameType.DnsName, forIssuer: false);
                return false;
            },
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        if (strict) options.ApplicationProtocols = [Tds8];

        try
        {
            await tls.AuthenticateAsClientAsync(options, token);
        }
        catch (AuthenticationException ex) when (!server.TrustServerCertificate)
        {
            string whose = hop.RedirectedFrom is null
                ? "the server's certificate"
                : $"the certificate of {hop.Host}, where the server redirected the login,";
            string why = rejected == SslPolicyErrors.None ? Innermost(ex) : Explain(rejected, hop, issuedTo);
            throw new ConnectException(ConnectFailure.Security,
                $"{whose} was not accepted ({why}); trust its issuer on this machine, " +
                "or set TrustServerCertificate=True in the connection string", inner: ex);
        }
        catch (Exception ex) when (strict && ex is IOException or AuthenticationException)
        {
            // SQL Server hangs up mid-handshake, with no TLS alert, when it has no configured
            // certificate: its errorlog says "A valid TLS certificate is not configured to accept
            // strict (TDS 8.0 and above) connections". A self-generated one is not enough.
            throw new ConnectException(ConnectFailure.Security,
                "the server closed the connection during the TDS 8.0 (Encrypt=Strict) handshake. SQL Server does this " +
                "when no certificate is configured for strict connections (its self-generated one does not count): " +
                "configure a certificate on the server, or use Encrypt=Mandatory in the connection string", inner: ex);
        }
    }

    private static string Explain(SslPolicyErrors errors, Hop hop, string? issuedTo)
    {
        var reasons = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) reasons.Add("no certificate was presented");
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            reasons.Add($"it is issued to '{issuedTo}', not {hop.CertificateHost}" +
                        (hop.RedirectedFrom is null ? "" : $" or {hop.RedirectedFrom}"));
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) reasons.Add("its issuer is not trusted here");
        return string.Join("; ", reasons);
    }

    private static async Task DisposeQuietly(SslStream? tls, TcpClient client)
    {
        try { if (tls is not null) await tls.DisposeAsync(); } catch { }
        client.Dispose();
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex.Message;
    }
}
