using System.Net.Security;
using System.Net.Sockets;
using DaffySQL.Tds;

namespace DaffySQL.Client;

/// <summary>
/// One logged-in session on a SQL Server: the transport (TLS or not), what the server said at
/// login, and the reader its answers are read with. One request at a time - DaffySQL never asks
/// for MARS - so a session belongs to one query from the moment it is sent until its answer has
/// been read to the end, and then goes back to its pool.
///
/// Once anything goes wrong on the wire - a read or write fails, a response does not parse - the
/// session is <see cref="Broken"/> and is closed, never reused: where the next answer would start
/// is no longer known.
/// </summary>
internal sealed class TdsSession(TcpClient client, Stream stream, SslStream? tls, LoginResponse login, ushort spid,
                                 RoutingTarget? routedTo) : IDisposable
{
    /// <summary>What a SQL Server grants when it says nothing else, and all a client may assume.</summary>
    public int PacketSize { get; } = login.PacketSize ?? TdsConstants.DefaultPacketSize;

    public LoginResponse Login { get; } = login;
    public ushort Spid { get; } = spid;

    /// <summary>Where the server redirected the login, when it did: the server this session is really on.</summary>
    public RoutingTarget? RoutedTo { get; } = routedTo;

    public ResponseReader Reader { get; } = new(stream, login.PacketSize ?? TdsConstants.DefaultPacketSize);

    public bool Broken { get; private set; }

    private readonly Lock _writing = new();
    private bool _resetPending;

    /// <summary>The TLS protocol and cipher suite negotiated; null for an unencrypted session.</summary>
    public string? Tls => tls is null ? null : $"{tls.SslProtocol}, {tls.NegotiatedCipherSuite}";

    /// <summary>
    /// The next request asks the server to reset the session first: back to how it was at login,
    /// with no temp tables, SET options or USE left from whoever had it last.
    /// </summary>
    public void ResetBeforeNextRequest() => _resetPending = true;

    /// <summary>
    /// The descriptor of the transaction open on the session, from the ENVCHANGE that began it; 0
    /// for none. Every request carries it, as SqlClient's do. Kept by whoever parses the answers.
    /// </summary>
    public ulong Transaction { get; set; }

    /// <summary>Sends a SQL batch and makes the reader ready for its answer. <paramref name="reset"/> resets the session first, now.</summary>
    public void SendBatch(string sql, bool reset = false)
    {
        byte[] packets = Requests.Batch(sql, reset || _resetPending, Transaction, PacketSize);
        _resetPending = false;
        Write(packets);
        Reader.Begin();
    }

    /// <summary>Asks the server to stop the request in progress. Its answer ends with an acknowledgement.</summary>
    public void SendAttention() => Write(Requests.Attention);

    /// <summary>
    /// Limits how long one read may wait, for draining an answer nobody is waiting on any more.
    /// Infinite otherwise: a query is allowed to take as long as it takes.
    /// </summary>
    public void SetReadTimeout(TimeSpan? timeout) =>
        client.ReceiveTimeout = timeout is { } t ? (int)t.TotalMilliseconds : 0;

    /// <summary>Marks the session unusable: something went wrong on the wire.</summary>
    public void Break() => Broken = true;

    /// <summary>
    /// A cancel sends its attention from another thread than the one that sent the request, and
    /// may do it while that thread is blocked reading - which SslStream allows, one reader and one
    /// writer at a time. Writes are one at a time.
    /// </summary>
    private void Write(byte[] packets)
    {
        lock (_writing)
        {
            try
            {
                stream.Write(packets);
                stream.Flush();
            }
            catch
            {
                Broken = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        Broken = true;
        try { tls?.Dispose(); } catch { }
        client.Dispose();
    }
}
