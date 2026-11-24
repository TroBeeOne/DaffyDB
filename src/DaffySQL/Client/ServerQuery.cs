using System.Buffers.Binary;
using System.Collections.Concurrent;
using DaffySQL.DuckDb;
using DaffySQL.Tds;

namespace DaffySQL.Client;

/// <summary>Errors SQL Server raised, as it raised them: "Msg 208, Level 16, State 1, Line 1: ...".</summary>
internal sealed class SqlServerException(IReadOnlyList<ServerMessage> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<ServerMessage> Errors { get; } = errors;
}

/// <summary>The T-SQL ran without error and returned no rows to read.</summary>
internal sealed class NoResultSetException()
    : Exception("the T-SQL returned no result set: daffy_query reads the first result set a batch returns; daffy_exec runs the rest");

/// <summary>One result set of an answer read whole: its columns, and its rows as .NET values.</summary>
internal sealed record AnswerSet(IReadOnlyList<ResultColumn> Columns, List<object?[]> Rows);

/// <summary>The SQL Server requests in flight, by the DuckDB connection they run for: what a cancel looks up.</summary>
internal static class RunningQueries
{
    private static readonly ConcurrentDictionary<ServerQuery, byte> Queries = new();

    public static void Add(ServerQuery query) => Queries[query] = 0;
    public static void Remove(ServerQuery query) => Queries.TryRemove(query, out _);

    public static int Cancel(ulong duckConnection, string reason)
    {
        int cancelled = 0;
        foreach (var query in Queries.Keys)
            if (query.DuckConnection == duckConnection && query.Cancel(reason)) cancelled++;
        return cancelled;
    }
}

/// <summary>
/// One request on one session, from the moment its T-SQL is sent until the session goes back to
/// its pool: the answer read as DuckDB asks for it, and whatever is left of it dealt with so the
/// session can be used again.
///
/// A query can be cancelled from any thread - <see cref="DaffySql.Cancel"/> - including while
/// another is blocked reading its answer: DuckDB's own interrupt only takes effect between chunks,
/// so it cannot stop a read that is waiting on the server. The cancel sends an attention; the
/// server stops, and acknowledges with a DONE carrying the ATTN bit at the end of a message,
/// after whatever of the answer it had already sent, cut short wherever it was. The reader that
/// was waiting wakes, sees the cancel, and stops. Whoever holds the answer reads it away to the
/// acknowledgement, and the session goes back to the pool at once - not when DuckDB lets go of
/// the query, which for a reader closed early is not until its connection's next statement.
///
/// Before a session goes back, a transaction the batch left open is rolled back: an idle session
/// holding locks would block whoever needs them until someone happened to reuse it. The answers
/// say when one begins and ends (ENVCHANGE), so a session known to be clean goes straight back.
/// One that is not - a transaction open, part of an answer read past unparsed, a request
/// cancelled - is reset first, there and then; and one that cannot be reset is closed, which
/// rolls back on the server. (DONE's "in transaction" bit would have been simpler, but SQL Server
/// does not set it.)
/// </summary>
internal sealed class ServerQuery
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The CurCmd SQL Server puts in a SELECT's DONE: its count is rows returned, not changed.</summary>
    private const ushort SelectCommand = 0xC1;

    private readonly Lock _gate = new();      // the flags a cancel changes from another thread
    private readonly Lock _reading = new();   // held by whichever thread is reading the answer

    private volatile bool _cancelled;
    private volatile bool _complete;
    private bool _attentionSent, _acknowledged, _detached, _released, _failed, _rowsEnded;
    private bool _unparsed;   // some of the answer was read past without parsing: a transaction may have begun unseen
    private string _reason = "the query was cancelled";

    private ServerQuery(NamedConnection connection, TdsSession session, ulong duckConnection)
    {
        Connection = connection;
        Session = session;
        DuckConnection = duckConnection;
    }

    public NamedConnection Connection { get; }
    public TdsSession Session { get; }

    /// <summary>The DuckDB connection the query runs for; 0 when there is none to name.</summary>
    public ulong DuckConnection { get; }

    /// <summary>The first result set's columns, once <see cref="ReadColumns"/> has found them.</summary>
    public IReadOnlyList<ResultColumn> Columns { get; private set; } = [];

    /// <summary>INFO messages - PRINT output, warnings - in the order they came.</summary>
    public List<ServerMessage> Messages { get; } = [];

    /// <summary>What <see cref="ReadToEnd"/> and <see cref="ReadAnswer"/> counted: rows inserted, updated, deleted.</summary>
    public long RowsAffected { get; private set; }

    /// <summary>Every result set, once <see cref="ReadAnswer"/> has read them.</summary>
    public List<AnswerSet> ResultSets { get; } = [];

    /// <summary>The errors the batch raised, once <see cref="ReadAnswer"/> has read them.</summary>
    public List<ServerMessage> Errors { get; } = [];

    private ResponseReader Reader => Session.Reader;

    /// <summary>
    /// Sends T-SQL on a session from the connection's pool and makes the first read. A session that
    /// sat idle may have died meanwhile - a restart, a failover - which shows only when it is used,
    /// so when a reused session fails before the server has answered anything, the batch goes again
    /// on a new login, once. Once the server has started answering, it never goes again.
    ///
    /// <paramref name="retryStale"/> false never sends it a second time at all. A read that goes again
    /// costs nothing, but a batch that was heard and died before its first answer would run twice.
    /// </summary>
    public static ServerQuery Begin(NamedConnection connection, string sql, ulong duckConnection, Action<ServerQuery> firstRead,
                                    bool retryStale = true)
    {
        for (int attempt = 1; ; attempt++)
        {
            var session = connection.Acquire(out bool reused);
            var query = new ServerQuery(connection, session, duckConnection);
            try
            {
                session.SendBatch(sql);
                RunningQueries.Add(query);   // from here a cancel can reach it
                firstRead(query);
                return query;
            }
            catch (IOException ex) when (retryStale && reused && attempt == 1 && !session.Reader.Received)
            {
                Log.Debug("query.stale_session", $"{connection.Name}: SPID {session.Spid} had gone while idle ({ex.Message}); trying a new session");
                session.Break();
                query.Release();
            }
            catch
            {
                query.Finish();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads the answer up to its first result set's columns. Errors raised before them are thrown
    /// once the answer has been read to its end, and so is an answer with no result set at all.
    /// </summary>
    public void ReadColumns()
    {
        lock (_reading)
        {
            var errors = new List<ServerMessage>();
            try
            {
                while (!Reader.AtEnd)
                {
                    StopIfCancelled();
                    byte token = Reader.PeekByte();
                    if (token == TdsConstants.Token.ColMetadata)
                    {
                        var columns = Reader.ReadWhole<List<ResultColumn>>(ParseColumns);
                        if (columns.Count == 0) continue;
                        if (errors.Count > 0) break;
                        Columns = columns;
                        return;
                    }

                    Reader.ReadByte();
                    Token(token, errors);
                }
                SkipRest();
            }
            catch (Exception) when (_cancelled)
            {
                DrainAcknowledgement();
                throw Cancelled();
            }
            catch (Exception ex) when (IsWireFailure(ex))
            {
                Session.Break();
                throw;
            }

            if (errors.Count > 0) throw new SqlServerException(errors);
            throw new NoResultSetException();
        }
    }

    /// <summary>
    /// Reads up to <paramref name="capacity"/> rows of the first result set into the chunk
    /// <paramref name="rows"/> is on, and returns how many. Zero means the result set has ended.
    /// Whatever the batch returns after it is read past unparsed: its statements still run.
    /// </summary>
    public int ReadRows(RowWriter rows, int capacity)
    {
        lock (_reading)
        {
            StopIfCancelled();
            if (_rowsEnded) return 0;

            int count = 0;
            try
            {
                while (count < capacity)
                {
                    if (_cancelled) StopIfCancelled();
                    byte token = Reader.ReadByte();
                    switch (token)
                    {
                        case TdsConstants.Token.Row:
                            rows.Row(Reader, nullBitmap: false, count++);
                            break;

                        case TdsConstants.Token.NbcRow:
                            rows.Row(Reader, nullBitmap: true, count++);
                            break;

                        case TdsConstants.Token.Done or TdsConstants.Token.DoneProc or TdsConstants.Token.DoneInProc:
                            Done(out _, out _);
                            _rowsEnded = true;
                            SkipRest();
                            return count;

                        case TdsConstants.Token.Error:
                            _failed = true;
                            throw new SqlServerException([LoginResponse.ReadMessageBody(Reader.Read(Reader.ReadUInt16()))]);

                        default:
                            Token(token, errors: null);
                            break;
                    }
                }
            }
            catch (Exception) when (_cancelled)
            {
                DrainAcknowledgement();
                throw Cancelled();
            }
            catch (Exception ex) when (IsWireFailure(ex))
            {
                Session.Break();
                throw;
            }

            return count;
        }
    }

    /// <summary>
    /// Reads the whole answer, for a batch run for its effects: counts the rows its statements
    /// changed, keeps its messages, reads past any result sets, and throws the errors it raised.
    /// </summary>
    public void ReadToEnd()
    {
        lock (_reading)
        {
            var errors = new List<ServerMessage>();
            RowWriter? skip = null;
            try
            {
                while (!Reader.AtEnd)
                {
                    StopIfCancelled();
                    byte token = Reader.PeekByte();
                    if (token == TdsConstants.Token.ColMetadata)
                    {
                        var columns = Reader.ReadWhole<List<ResultColumn>>(ParseColumns);
                        skip = columns.Count == 0 ? null : new RowWriter(ResultPlan.For(columns), []);
                        continue;
                    }

                    Reader.ReadByte();
                    switch (token)
                    {
                        case TdsConstants.Token.Row or TdsConstants.Token.NbcRow:
                            (skip ?? throw new InvalidDataException("a row arrived before its columns"))
                                .Row(Reader, nullBitmap: token == TdsConstants.Token.NbcRow, 0);
                            break;

                        case TdsConstants.Token.Done or TdsConstants.Token.DoneInProc:
                            ushort status = Done(out ushort command, out ulong count);
                            if ((status & TdsConstants.DoneStatus.Count) != 0 && command != SelectCommand) RowsAffected += (long)count;
                            break;

                        default:
                            Token(token, errors);   // DONEPROC among them: its count repeats its statements'
                            break;
                    }
                }
                _complete = true;
            }
            catch (Exception) when (_cancelled)
            {
                DrainAcknowledgement();
                throw Cancelled();
            }
            catch (Exception ex) when (IsWireFailure(ex))
            {
                Session.Break();
                throw;
            }

            if (errors.Count > 0) throw new SqlServerException(errors);
        }
    }

    /// <summary>
    /// Reads the whole answer and keeps it, for a host that wants what the batch returned rather than
    /// a table for DuckDB: every result set, its messages, the rows its statements changed. An error
    /// is kept rather than thrown, because what came before it is part of the answer too, and the
    /// batch may carry on after it, as T-SQL does.
    /// </summary>
    public void ReadAnswer()
    {
        lock (_reading)
        {
            AnswerSet? current = null;
            try
            {
                while (!Reader.AtEnd)
                {
                    StopIfCancelled();
                    byte token = Reader.PeekByte();
                    if (token == TdsConstants.Token.ColMetadata)
                    {
                        var columns = Reader.ReadWhole<List<ResultColumn>>(ParseColumns);
                        current = columns.Count == 0 ? null : new AnswerSet(columns, []);
                        if (current is not null) ResultSets.Add(current);
                        continue;
                    }

                    Reader.ReadByte();
                    switch (token)
                    {
                        case TdsConstants.Token.Row or TdsConstants.Token.NbcRow:
                            if (current is null) throw new InvalidDataException("a row arrived before its columns");
                            current.Rows.Add(RowValues.Read(Reader, current.Columns, nullBitmap: token == TdsConstants.Token.NbcRow));
                            break;

                        case TdsConstants.Token.Done or TdsConstants.Token.DoneInProc:
                            ushort status = Done(out ushort command, out ulong count);
                            if ((status & TdsConstants.DoneStatus.Count) != 0 && command != SelectCommand) RowsAffected += (long)count;
                            break;

                        default:
                            Token(token, Errors);   // DONEPROC among them: its count repeats its statements'
                            break;
                    }
                }
                _complete = true;
            }
            catch (Exception) when (_cancelled)
            {
                DrainAcknowledgement();
                throw Cancelled();
            }
            catch (Exception ex) when (IsWireFailure(ex))
            {
                Session.Break();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads the rest of the answer, parsed but kept for nothing: for SELECT TOP (0), whose columns
    /// are all that is wanted. Parsed rather than skipped, so it leaves the session known to be clean.
    /// </summary>
    public void ReadPastRest()
    {
        lock (_reading)
        {
            try
            {
                RowWriter? skip = null;
                while (!Reader.AtEnd)
                {
                    byte token = Reader.ReadByte();
                    if (token is TdsConstants.Token.Row or TdsConstants.Token.NbcRow)
                        (skip ??= new RowWriter(ResultPlan.For(Columns), [])).Row(Reader, nullBitmap: token == TdsConstants.Token.NbcRow, 0);
                    else
                        Token(token, errors: null);
                }
                _complete = true;
            }
            catch (Exception ex) when (IsWireFailure(ex))
            {
                Session.Break();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops the request, from any thread. False when there is nothing to cancel: the query is
    /// already done with, or already cancelled.
    /// </summary>
    public bool Cancel(string reason)
    {
        bool send;
        lock (_gate)
        {
            if (_released || _cancelled) return false;
            _cancelled = true;
            _reason = reason;
            send = !_complete && !_attentionSent;
            if (send) _attentionSent = true;
        }

        Log.Debug("query.cancel", $"{Connection.Name}: SPID {Session.Spid}: {reason}");
        if (send) SendAttention();
        Task.Run(RestAndRelease);
        return true;
    }

    /// <summary>
    /// DuckDB is done with the query - read to the end, or stopped early for whatever reason.
    /// A session at rest goes back now; one still answering is cancelled first, which waits on the
    /// server, away from DuckDB's thread.
    /// </summary>
    public void Finish()
    {
        bool atRest;
        lock (_gate)
        {
            if (_released) return;
            atRest = _complete && (!_attentionSent || _acknowledged);
        }

        if (!atRest)
        {
            Task.Run(RestAndRelease);
            return;
        }

        lock (_reading) _detached = true;
        Release();
    }

    // ---- bringing a session to rest ---------------------------------------------------------

    private void RestAndRelease()
    {
        try
        {
            lock (_reading)
            {
                if (_released) return;
                BringToRest();
                _detached = true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("query.rest_failed", $"{Connection.Name}: SPID {Session.Spid}: {ex.Message}; closing the session");
            Session.Break();
        }
        Release();
    }

    /// <summary>
    /// Leaves nothing of the answer unread. A server that raised an error is finishing the batch on
    /// its own, so the rest is read past; one still answering is sent an attention first.
    /// </summary>
    private void BringToRest()
    {
        if (Session.Broken) return;

        bool send;
        lock (_gate)
        {
            send = !_complete && !_failed && !_attentionSent;
            if (send) _attentionSent = true;
        }

        if (send) SendAttention();
        if (!_complete && !_attentionSent) SkipRest();
        if (_attentionSent) DrainAcknowledgement();
    }

    /// <summary>Reads away whatever comes before the acknowledgement, the acknowledgement included.</summary>
    private void DrainAcknowledgement()
    {
        if (!_attentionSent || _acknowledged || Session.Broken) return;

        Session.SetReadTimeout(AcknowledgementTimeout);
        try
        {
            bool acknowledged = !Reader.AtEnd ? Reader.SkipToEnd() : Reader.EndedWithAcknowledgement;
            while (!acknowledged)
            {
                Reader.Begin();
                acknowledged = Reader.SkipToEnd();
            }
            _acknowledged = true;
            _complete = true;
        }
        catch (Exception ex)
        {
            Log.Debug("query.drain_failed", $"{Connection.Name}: SPID {Session.Spid}: no acknowledgement of the cancel ({ex.Message})");
            Session.Break();
        }
        finally
        {
            Session.SetReadTimeout(null);
        }
    }

    /// <summary>Gives the session back to its pool - reset first if it may not be clean - exactly once.</summary>
    private void Release()
    {
        lock (_gate)
        {
            if (_released) return;
            _released = true;
        }
        RunningQueries.Remove(this);

        if (!Session.Broken && (_attentionSent || _unparsed || Session.Transaction != 0)) Reset();
        Connection.Return(Session);
    }

    /// <summary>
    /// Resets the session now, rather than when it is next used: RESETCONNECTION rolls back a
    /// transaction, and the batch makes sure of it. The answer is parsed, so the session's
    /// transaction is known to be gone afterwards; if it is not, or anything fails, the session is
    /// closed instead, and the server rolls back for it.
    /// </summary>
    private void Reset()
    {
        Session.SetReadTimeout(AcknowledgementTimeout);
        try
        {
            Session.SendBatch("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", reset: true);
            var errors = new List<ServerMessage>();
            while (!Reader.AtEnd) Token(Reader.ReadByte(), errors);

            if (errors.Count > 0 || Session.Transaction != 0)
            {
                Log.Debug("query.reset_failed", $"{Connection.Name}: SPID {Session.Spid}: {string.Join("; ", errors)}; closing the session");
                Session.Break();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("query.reset_failed", $"{Connection.Name}: SPID {Session.Spid}: {ex.Message}; closing the session");
            Session.Break();
        }
        finally
        {
            Session.SetReadTimeout(null);
        }
    }

    private void SendAttention()
    {
        try
        {
            Session.SendAttention();
        }
        catch (Exception ex)
        {
            Log.Debug("query.attention_failed", $"{Connection.Name}: SPID {Session.Spid}: {ex.Message}");
            Session.Break();
        }
    }

    // ---- tokens -----------------------------------------------------------------------------

    private void StopIfCancelled()
    {
        if (!_cancelled && !_detached) return;
        DrainAcknowledgement();
        throw Cancelled();
    }

    private OperationCanceledException Cancelled() => new(_reason);

    private void SkipRest()
    {
        if (!Reader.AtEnd) _unparsed = true;
        if (Reader.SkipToEnd()) _acknowledged = true;
        _complete = true;
    }

    /// <summary>The rest of a DONE token, its type already read. An attention's acknowledgement means the answer was cancelled.</summary>
    private ushort Done(out ushort command, out ulong count)
    {
        ushort status = Reader.ReadUInt16();
        command = Reader.ReadUInt16();
        count = Reader.ReadUInt64();
        if ((status & TdsConstants.DoneStatus.Attention) != 0)
        {
            _acknowledged = true;
            if (Reader.AtEnd) _complete = true;
            throw Cancelled();
        }
        return status;
    }

    /// <summary>A token that carries no rows: noted if it is a message, read past otherwise.</summary>
    private void Token(byte token, List<ServerMessage>? errors)
    {
        switch (token)
        {
            case TdsConstants.Token.EnvChange:
                EnvChange(Reader.Read(Reader.ReadUInt16()));
                break;

            case TdsConstants.Token.Order or TdsConstants.Token.ColInfo or TdsConstants.Token.TabName:
                Reader.Skip(Reader.ReadUInt16());
                break;

            case TdsConstants.Token.Info:
                Messages.Add(LoginResponse.ReadMessageBody(Reader.Read(Reader.ReadUInt16())));
                break;

            case TdsConstants.Token.Error when errors is not null:
                errors.Add(LoginResponse.ReadMessageBody(Reader.Read(Reader.ReadUInt16())));
                break;

            case TdsConstants.Token.ReturnStatus or TdsConstants.Token.Offset:
                Reader.Skip(4);
                break;

            case TdsConstants.Token.Done or TdsConstants.Token.DoneProc or TdsConstants.Token.DoneInProc:
                Done(out _, out _);
                break;

            case TdsConstants.Token.SessionState:
                Reader.Skip(Reader.ReadUInt32());
                break;

            default:
                throw new InvalidDataException($"unexpected {TdsConstants.TokenName(token)} token (0x{token:X2})");
        }
    }

    /// <summary>
    /// The ENVCHANGEs that say where the session's transaction stands: begun (the new value is its
    /// 8-byte descriptor), committed, rolled back, ended, or gone with a reset.
    /// </summary>
    private void EnvChange(ReadOnlySpan<byte> body)
    {
        switch (body[0])
        {
            case TdsConstants.EnvChangeType.BeginTransaction when body.Length >= 10 && body[1] == 8:
                Session.Transaction = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(2, 8));
                break;

            case TdsConstants.EnvChangeType.CommitTransaction or TdsConstants.EnvChangeType.RollbackTransaction
                 or TdsConstants.EnvChangeType.TransactionEnded or TdsConstants.EnvChangeType.ResetConnectionAck:
                Session.Transaction = 0;
                break;
        }
    }

    private static bool ParseColumns(ReadOnlySpan<byte> data, out List<ResultColumn> columns, out int used) =>
        ResultColumns.TryRead(data, 0, out columns, out used);

    private static bool IsWireFailure(Exception ex) =>
        ex is IOException or InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException;
}
