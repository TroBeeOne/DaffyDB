using System.Collections.Concurrent;
using DaffySQL.Tds;

namespace DaffySQL.Client;

/// <summary>
/// A SQL Server under a name - where, as whom - and the sessions kept open for it.
///
/// A query takes a session for as long as its answer is being read, and gives it back; an idle
/// session is handed to the next query with RESETCONNECTION set, so it starts as a fresh login
/// would, with nothing left of the last query's temp tables or SET options - what SqlClient's pool
/// does. Up to <see cref="MaxIdle"/> are kept; a busy moment may open more, which are closed as
/// they come back. A session that broke is closed, never returned.
/// </summary>
internal sealed class NamedConnection(string name, ServerEndpoint endpoint) : IDisposable
{
    public const int MaxIdle = 8;

    private readonly Lock _gate = new();
    private readonly Stack<TdsSession> _idle = new();
    private int _open;
    private bool _closed;

    public string Name { get; } = name;
    public ServerEndpoint Endpoint { get; } = endpoint;

    /// <summary>
    /// The most recent login's answer, its TLS (null for none), and where the server redirected
    /// it (null when it did not), for describing the connection.
    /// </summary>
    public LoginResponse? LastLogin { get; private set; }
    public string? Tls { get; private set; }
    public RoutingTarget? RoutedTo { get; private set; }

    public int Open { get { lock (_gate) return _open; } }
    public int Idle { get { lock (_gate) return _idle.Count; } }

    /// <summary>
    /// An idle session, or a new one logged in now. <paramref name="reused"/> says which: a reused
    /// session may have died while it sat idle, and a query that fails on one is worth one more go.
    /// </summary>
    public TdsSession Acquire(out bool reused)
    {
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException($"the connection '{Name}' has been disconnected");
            while (_idle.TryPop(out var idle))
            {
                if (!idle.Broken)
                {
                    reused = true;
                    return idle;
                }
                _open--;
                idle.Dispose();
            }
            _open++;
        }

        reused = false;
        try
        {
            var session = TdsConnector.ConnectAsync(Endpoint, CancellationToken.None).GetAwaiter().GetResult();
            lock (_gate)
            {
                LastLogin = session.Login;
                Tls = session.Tls;
                RoutedTo = session.RoutedTo;
            }
            return session;
        }
        catch
        {
            lock (_gate) _open--;
            throw;
        }
    }

    /// <summary>Takes a session back: kept for the next query if it is healthy and there is room, closed otherwise.</summary>
    public void Return(TdsSession session)
    {
        bool keep;
        lock (_gate)
        {
            keep = !session.Broken && !_closed && _idle.Count < MaxIdle;
            if (keep)
            {
                session.ResetBeforeNextRequest();
                _idle.Push(session);
            }
            else
            {
                _open--;
            }
        }

        if (!keep)
        {
            Log.Debug("session.closed", $"{Name}: SPID {session.Spid} closed{(session.Broken ? " (broken)" : "")}");
            session.Dispose();
        }
    }

    /// <summary>Closes the idle sessions now; sessions in use are closed as their queries give them back.</summary>
    public int Close()
    {
        List<TdsSession> idle;
        lock (_gate)
        {
            _closed = true;
            idle = [.. _idle];
            _idle.Clear();
            _open -= idle.Count;
        }
        foreach (var session in idle) session.Dispose();
        return idle.Count;
    }

    public void Dispose() => Close();

    public string Describe() =>
        $"{Name}: {Endpoint.Describe()}" +
        (LastLogin is { } login ? $", {login.ServerProgram.Trim()} {login.ServerVersion}" : "") +
        $", {Endpoint.DescribeSecurity()}{(Tls is null ? "" : $" ({Tls})")}" +
        (RoutedTo is null ? "" : $", redirected to {RoutedTo}") +
        $", {Endpoint.DescribeLockTimeout()}";
}

/// <summary>
/// The named SQL Servers of the process, shared by every DuckDB database and connection in it.
/// Names are case-insensitive, as DuckDB's identifiers are.
/// </summary>
internal static class Connections
{
    private static readonly ConcurrentDictionary<string, NamedConnection> ByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names a server, after logging in to it once - so a wrong password or an unreachable server
    /// is found out here and not at the first query - and keeps that session for the first query.
    /// A name already in use is replaced, and its sessions closed.
    /// </summary>
    public static NamedConnection Add(string name, ServerEndpoint endpoint)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("a connection needs a name");

        var connection = new NamedConnection(name, endpoint);
        connection.Return(connection.Acquire(out _));

        if (ByName.TryGetValue(name, out var previous)) previous.Close();
        ByName[name] = connection;
        return connection;
    }

    public static NamedConnection Get(string name) =>
        ByName.TryGetValue(name.Trim(), out var connection)
            ? connection
            : throw new InvalidOperationException(
                $"there is no connection named '{name}'; name one first with daffy_connect('{name}', 'Server=...;User Id=...;Password=...')");

    public static bool Remove(string name, out int closed)
    {
        closed = 0;
        if (!ByName.TryRemove(name.Trim(), out var connection)) return false;
        closed = connection.Close();
        return true;
    }

    public static IReadOnlyList<NamedConnection> All => ByName.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
