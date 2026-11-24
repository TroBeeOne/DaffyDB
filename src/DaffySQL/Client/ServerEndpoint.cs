using System.Data.Common;

namespace DaffySQL.Client;

/// <summary>How DaffySQL encrypts its connection to the server.</summary>
internal enum ServerEncrypt
{
    /// <summary>TDS 7.4: PRELOGIN in the clear, then TLS for the whole session. The default, as in SqlClient 4+.</summary>
    Mandatory,

    /// <summary>No TLS. The login's password crosses the network only scrambled.</summary>
    Optional,

    /// <summary>TDS 8.0: TLS from the first byte. Needs SQL Server 2022 or later.</summary>
    Strict,
}

/// <summary>How DaffySQL proves who it is to the server.</summary>
internal enum ServerAuthentication
{
    /// <summary>A SQL login and its password.</summary>
    SqlPassword,

    /// <summary>
    /// Entra ID, as an application: User Id is the application (client) id - optionally
    /// "id@tenant", as sqlcmd writes it - and Password is one of its client secrets. DaffySQL asks
    /// Entra for an access token and presents that; the secret itself never reaches the server.
    /// </summary>
    EntraServicePrincipal,
}

/// <summary>
/// A SQL Server to connect to, and as whom: an ordinary SQL Server connection string, parsed.
///
/// Lifted from DaffyDuo's TargetEndpoint. It is parsed with the BCL's DbConnectionStringBuilder
/// rather than SqlClient's - DaffySQL never opens an ADO.NET connection, so it carries no SqlClient
/// at all. Only keywords that mean something here are accepted; anything else is refused by name
/// rather than silently ignored.
/// </summary>
internal sealed record ServerEndpoint(
    string Host,
    int Port,
    string UserName,
    string Password,
    string? Database,
    ServerEncrypt Encrypt,
    bool TrustServerCertificate,
    string? HostNameInCertificate,
    int ConnectTimeoutSeconds,
    ServerAuthentication Authentication = ServerAuthentication.SqlPassword,
    string? ApplicationName = null,
    int PacketSize = ServerEndpoint.DefaultPacketSize,
    int LockTimeoutSeconds = ServerEndpoint.DefaultLockTimeout)
{
    public const int DefaultPort = 1433;
    public const int DefaultConnectTimeout = 15;

    /// <summary>
    /// How long a query DaffySQL writes itself waits for a lock before SQL Server gives up on it
    /// (Msg 1222). A reader under READ COMMITTED waits behind a writer's locks, and without a limit
    /// it waits until the writer finishes, however long that is. 0 is no limit.
    /// </summary>
    public const int DefaultLockTimeout = 30;

    /// <summary>The most Lock Timeout takes, so that its milliseconds still fit SET LOCK_TIMEOUT's int.</summary>
    public const int MaxLockTimeout = int.MaxValue / 1000;

    /// <summary>
    /// What LOGIN7 asks for, as SqlClient does; the server may grant less. Bigger packets mean
    /// fewer of them in a large result. SQL Server accepts 512 to 32767.
    /// </summary>
    public const int DefaultPacketSize = 8000;

    public const string DefaultApplicationName = "DaffySQL";

    private static readonly string[] ServerKeys = ["server", "data source", "address", "addr", "network address"];
    private static readonly string[] UserKeys = ["user id", "uid", "user"];
    private static readonly string[] PasswordKeys = ["password", "pwd"];
    private static readonly string[] DatabaseKeys = ["initial catalog", "database"];
    private static readonly string[] EncryptKeys = ["encrypt"];
    private static readonly string[] TrustKeys = ["trustservercertificate", "trust server certificate"];
    private static readonly string[] HostNameKeys = ["hostnameincertificate", "host name in certificate"];
    private static readonly string[] TimeoutKeys = ["connect timeout", "connection timeout", "timeout"];
    private static readonly string[] ApplicationKeys = ["application name", "app"];
    private static readonly string[] PacketSizeKeys = ["packet size"];
    private static readonly string[] LockTimeoutKeys = ["lock timeout"];

    /// <summary>Keywords accepted because SqlClient connection strings carry them, but with no effect here.</summary>
    private static readonly string[] IgnoredKeys =
        ["pooling", "min pool size", "max pool size", "multipleactiveresultsets", "multiple active result sets",
         "workstation id", "wsid", "persist security info", "persistsecurityinfo",
         "trusted_connection", "integrated security", "authentication", "command timeout",
         "applicationintent", "application intent", "multisubnetfailover", "multi subnet failover",
         "connectretrycount", "connect retry count", "connectretryinterval", "connect retry interval"];

    public static ServerEndpoint Parse(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var values = builder.Keys.Cast<string>()
            .ToDictionary(k => k.ToLowerInvariant(), k => builder[k]?.ToString() ?? "");

        string? Get(string[] keys) => keys.Select(k => values.GetValueOrDefault(k)).FirstOrDefault(v => v is not null);

        string? integrated = values.GetValueOrDefault("integrated security") ?? values.GetValueOrDefault("trusted_connection");
        if (integrated is not null && IsTrue(integrated))
            throw new FormatException("Integrated Security is not supported: DaffySQL logs in with a SQL login and password, " +
                                      "or as an Entra ID application");

        var authentication = ParseAuthentication(values.GetValueOrDefault("authentication"));

        var known = ServerKeys.Concat(UserKeys).Concat(PasswordKeys).Concat(DatabaseKeys).Concat(EncryptKeys)
            .Concat(TrustKeys).Concat(HostNameKeys).Concat(TimeoutKeys).Concat(ApplicationKeys).Concat(PacketSizeKeys)
            .Concat(LockTimeoutKeys).Concat(IgnoredKeys).ToHashSet();
        var unknown = values.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new FormatException($"unsupported connection string keyword(s): {string.Join(", ", unknown)}");

        string server = Get(ServerKeys) ?? throw new FormatException("the connection string needs Server=host[,port]");
        var (host, port) = ParseServer(server);

        string user = Get(UserKeys) ?? throw new FormatException("the connection string needs User Id=");
        string password = Get(PasswordKeys) ?? "";

        string? database = Get(DatabaseKeys);
        if (string.IsNullOrWhiteSpace(database)) database = null;

        var encrypt = ParseEncrypt(Get(EncryptKeys));
        bool trust = Get(TrustKeys) is { } t && IsTrue(t);
        string? hostName = Get(HostNameKeys) is { Length: > 0 } h ? h : null;

        int timeout = DefaultConnectTimeout;
        if (Get(TimeoutKeys) is { } timeoutText && (!int.TryParse(timeoutText, out timeout) || timeout <= 0))
            throw new FormatException($"Connect Timeout must be a positive number of seconds, not '{timeoutText}'");

        string? application = Get(ApplicationKeys) is { Length: > 0 } a ? a : null;

        int packetSize = DefaultPacketSize;
        if (Get(PacketSizeKeys) is { } packetText && (!int.TryParse(packetText, out packetSize) || packetSize is < 512 or > 32767))
            throw new FormatException($"Packet Size must be from 512 to 32767, not '{packetText}'");

        int lockTimeout = DefaultLockTimeout;
        if (Get(LockTimeoutKeys) is { } lockText && (!int.TryParse(lockText, out lockTimeout) || lockTimeout is < 0 or > MaxLockTimeout))
            throw new FormatException($"Lock Timeout must be a number of seconds, 0 for no limit, not '{lockText}'");

        return new ServerEndpoint(host, port, user, password, database, encrypt, trust, hostName, timeout, authentication,
                                  application, packetSize, lockTimeout);
    }

    private static ServerAuthentication ParseAuthentication(string? value) =>
        value?.Replace(" ", "").ToLowerInvariant() switch
        {
            null or "" or "sqlpassword" => ServerAuthentication.SqlPassword,
            "activedirectoryserviceprincipal" => ServerAuthentication.EntraServicePrincipal,
            _ => throw new FormatException(
                $"Authentication={value} is not supported: SqlPassword (a SQL login) and ActiveDirectoryServicePrincipal " +
                "(an Entra ID application and its client secret) are"),
        };

    public bool IsEntra => Authentication == ServerAuthentication.EntraServicePrincipal;

    /// <summary>For an Entra application: its client id, without any "@tenant".</summary>
    public string ClientId => IsEntra && UserName.IndexOf('@') is > 0 and var at ? UserName[..at] : UserName;

    /// <summary>
    /// For an Entra application written "id@tenant": the tenant - used only when the server does
    /// not name its own, which Azure SQL always does.
    /// </summary>
    public string? TenantId => IsEntra && UserName.IndexOf('@') is > 0 and var at && at < UserName.Length - 1 ? UserName[(at + 1)..] : null;

    /// <summary>
    /// Accepts host, host,port, tcp:host,port and the local shorthands. A named instance needs the
    /// SQL Browser service to find its port, which is not implemented yet, so it is refused with the
    /// fix spelled out rather than failing later as a timeout.
    /// </summary>
    public static (string Host, int Port) ParseServer(string server)
    {
        string value = server.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        if (value.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"'{server}': only TCP servers are supported");

        int port = DefaultPort;
        int comma = value.LastIndexOf(',');
        if (comma >= 0)
        {
            if (!int.TryParse(value[(comma + 1)..].Trim(), out port) || port is <= 0 or > 65535)
                throw new FormatException($"'{server}': the port after the comma is not a valid port number");
            value = value[..comma].Trim();
        }

        if (value.Contains('\\'))
            throw new FormatException($"'{server}': named instances are not supported yet; give the instance's TCP port instead, as host,port");

        if (value is "." or "(local)" or "(localdb)") value = "localhost";
        if (value.Length == 0) throw new FormatException("the server name is empty");

        return (value, port);
    }

    private static ServerEncrypt ParseEncrypt(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "true" or "yes" or "mandatory" => ServerEncrypt.Mandatory,
        "false" or "no" or "optional" => ServerEncrypt.Optional,
        "strict" => ServerEncrypt.Strict,
        _ => throw new FormatException($"Encrypt={value} is not recognised (Mandatory, Optional or Strict)"),
    };

    private static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "sspi" or "1";

    /// <summary>
    /// What goes before a query DaffySQL writes itself - daffy_scan's, daffy_tables' - so that it
    /// waits no longer than Lock Timeout for a lock. It is sent with every one, because a session's
    /// SET options go when it is reset for reuse. daffy_query and daffy_exec run the caller's T-SQL
    /// as written, with nothing before it: CREATE VIEW and CREATE PROCEDURE must open their batch.
    /// </summary>
    public string LockTimeoutPrefix => LockTimeoutSeconds == 0 ? "" : $"SET LOCK_TIMEOUT {LockTimeoutSeconds * 1000}; ";

    public string DescribeLockTimeout() => LockTimeoutSeconds == 0 ? "no lock timeout" : $"lock timeout {LockTimeoutSeconds} s";

    /// <summary>Safe to show and log: who, where, how - never the password. An Entra application is named by its client id.</summary>
    public string Describe() =>
        $"{(IsEntra ? $"app {ClientId}" : UserName)}@{Host}:{Port}{(Database is null ? "" : "/" + Database)}";

    public string DescribeSecurity() =>
        Encrypt switch
        {
            ServerEncrypt.Optional => "unencrypted",
            ServerEncrypt.Strict => "Strict (TDS 8.0)",
            _ => "Mandatory (TDS 7.4)",
        } + (Encrypt != ServerEncrypt.Optional && TrustServerCertificate ? ", certificate trusted without validation" : "")
          + (IsEntra ? ", Entra ID application" : "");

    /// <summary>Records print every member, and this one has a password in it.</summary>
    public override string ToString() => Describe();
}
