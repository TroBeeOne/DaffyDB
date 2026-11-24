using System.Text;
using DaffySQL.Client;
using DaffySQL.DuckDb;
using DuckDB.NET.Data;

namespace DaffySQL;

/// <summary>
/// Which of DaffySQL's functions <see cref="DaffySql.Register"/> adds. A host that names its
/// servers from code, and lets people it does not trust write the SQL, registers only the reads:
/// daffy_connect would let any of them repoint a name the host made, and daffy_disconnect break it.
/// </summary>
[Flags]
public enum DaffyFunctions
{
    None = 0,

    /// <summary>daffy_connect, daffy_disconnect and daffy_connections.</summary>
    Connections = 1,

    /// <summary>daffy_query: any T-SQL, run at bind, its first result set as a table.</summary>
    Query = 2,

    /// <summary>daffy_scan: a table or view, only the columns used.</summary>
    Scan = 4,

    /// <summary>daffy_exec: any T-SQL, run for its effects.</summary>
    Exec = 8,

    /// <summary>daffy_tables: the tables and views a server has.</summary>
    Tables = 16,

    /// <summary>daffy_columns: a table's columns and the DuckDB types they arrive as.</summary>
    Columns = 32,

    All = Connections | Query | Scan | Exec | Tables | Columns,
}

/// <summary>Where a connection string points, and as whom: what a host shows people, never the password.</summary>
public sealed record SqlServerAddress(string Host, int Port, string User, string? Database);

/// <summary>
/// A column of a result <see cref="DaffySql.Run"/> read. <paramref name="TypeName"/> is SQL Server's name for
/// its type, with no size: int, nvarchar, decimal, datetime2, rowversion. A CLR type (geography,
/// hierarchyid) is varbinary, since its values are SQL Server's own serialization.
/// </summary>
public sealed record SqlServerColumn(string Name, string TypeName, bool Nullable, byte Precision, byte Scale);

/// <summary>
/// A result set <see cref="DaffySql.Run"/> read. A value is null, or byte, short, int, long, bool, float,
/// double, decimal, DateTime, DateOnly, TimeSpan, DateTimeOffset, Guid, string or byte[]. A decimal too
/// wide for .NET's is its exact text, and a sql_variant is text as well.
/// </summary>
public sealed record SqlServerResultSet(IReadOnlyList<SqlServerColumn> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>A message SQL Server sent: PRINT output or a warning, or an error.</summary>
public sealed record SqlServerMessage(int Number, byte Severity, byte State, int Line, string Procedure, string Text);

/// <summary>
/// Everything a batch returned. <paramref name="Errors"/> are not thrown, because whatever came before
/// them is part of the answer: rows, then a RAISERROR, is both.
/// </summary>
public sealed record SqlServerAnswer(
    IReadOnlyList<SqlServerResultSet> ResultSets,
    IReadOnlyList<SqlServerMessage> Messages,
    IReadOnlyList<SqlServerMessage> Errors,
    long RowsAffected);

/// <summary>
/// SQL Server from DuckDB: T-SQL sent over TDS by DaffySQL's own client, the answer streamed into
/// DuckDB as a table.
///
///   DaffySql.Register(connection);
///   DaffySql.Connect("sales", "Server=tcp:sql01;Database=Sales;User Id=svc;Password=...");
///   SELECT * FROM daffy_query('sales', 'SELECT Id, Name FROM dbo.Customers');
///
/// Connections are named once per process and shared by every DuckDB database and connection in
/// it; each keeps a small pool of logged-in sessions.
/// </summary>
public static class DaffySql
{
    /// <summary>
    /// Registers DaffySQL's functions in the database <paramref name="connection"/> is open on:
    /// daffy_connect, daffy_disconnect, daffy_connections, daffy_query, daffy_scan, daffy_exec,
    /// daffy_tables and daffy_columns, or those of them <paramref name="functions"/> names.
    /// Functions belong to the database, so every connection to it -
    /// <see cref="DuckDBConnection.Duplicate"/> included - has them.
    /// </summary>
    public static void Register(DuckDBConnection connection, DaffyFunctions functions = DaffyFunctions.All)
    {
        // varchar in a code page .NET does not carry by default: 1252, 932, 936, 949, 950 and the rest.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DuckApi.Load(connection);
        nint handle = DuckApi.Handle(connection);
        if (functions.HasFlag(DaffyFunctions.Connections)) ConnectionFunctions.Register(handle);
        QueryFunction.Register(handle, functions.HasFlag(DaffyFunctions.Query), functions.HasFlag(DaffyFunctions.Tables));
        ScanFunction.Register(handle, functions.HasFlag(DaffyFunctions.Scan), functions.HasFlag(DaffyFunctions.Columns));
        if (functions.HasFlag(DaffyFunctions.Exec)) ExecFunction.Register(handle);
    }

    /// <summary>
    /// Where a connection string points - host, port, user, database - without connecting, so a
    /// host can name and show a server before it logs in. Throws <see cref="FormatException"/> for
    /// a string <see cref="Connect"/> would refuse, naming what is wrong.
    /// </summary>
    public static SqlServerAddress Address(string connectionString)
    {
        var endpoint = ServerEndpoint.Parse(connectionString);
        return new SqlServerAddress(endpoint.Host, endpoint.Port,
                                    endpoint.IsEntra ? $"app {endpoint.ClientId}" : endpoint.UserName, endpoint.Database);
    }

    /// <summary>
    /// Cancels the SQL Server queries DaffySQL is running for <paramref name="connection"/>, from any
    /// thread, and returns how many there were. Each is sent an attention, and its session goes back
    /// to its pool as soon as the server acknowledges; whatever DuckDB query was reading it fails
    /// with "the query was cancelled".
    ///
    /// DuckDB's own interrupt - <see cref="DuckDBCommand.Cancel"/> - cannot do this: it takes effect
    /// between chunks, so it waits for a SQL Server that has not answered yet. Call both. Call this
    /// too after closing a streaming reader early, or DuckDB keeps its query, and the session under
    /// it, until the connection's next statement.
    /// </summary>
    public static int Cancel(DuckDBConnection connection) =>
        RunningQueries.Cancel(DuckApi.ConnectionId(connection), "the query was cancelled");

    /// <summary>
    /// Names a SQL Server, logging in to it once to be sure the connection string works, and
    /// returns a description safe to show (never the password). A name in use is replaced.
    /// </summary>
    public static string Connect(string name, string connectionString) =>
        Connections.Add(name, ServerEndpoint.Parse(connectionString)).Describe();

    /// <summary>
    /// Runs T-SQL on a named server, from code and with no DuckDB involved, and returns all of what it
    /// sent back, held in memory: for a host that wants a stored procedure's answer as it came, where
    /// daffy_query keeps one result set for DuckDB and daffy_exec keeps none.
    ///
    /// Each call is its own batch on a pooled session, and one left in a transaction is rolled back.
    /// It waits for as long as the server takes, and <paramref name="cancel"/> stops it by sending the
    /// server an attention, ending in <see cref="OperationCanceledException"/>. The T-SQL is never sent
    /// twice: an idle session that died is found with a batch that does nothing, and if the connection
    /// fails after that, the exception does not say whether it ran.
    /// </summary>
    public static SqlServerAnswer Run(string connection, string tsql, CancellationToken cancel = default)
    {
        var named = Connections.Get(connection);
        cancel.ThrowIfCancellationRequested();

        ServerQuery.Begin(named, "SELECT 1", duckConnection: 0, q => q.ReadToEnd()).Finish();

        var query = ServerQuery.Begin(named, tsql, duckConnection: 0, q =>
        {
            using var stop = cancel.Register(() => q.Cancel("the query was cancelled"));
            q.ReadAnswer();
        }, retryStale: false);
        query.Finish();

        return new SqlServerAnswer(
            query.ResultSets.Select(set => new SqlServerResultSet(set.Columns.Select(Described).ToList(), set.Rows)).ToList(),
            query.Messages.Select(Message).ToList(),
            query.Errors.Select(Message).ToList(),
            query.RowsAffected);
    }

    private static SqlServerColumn Described(Tds.ResultColumn column) => new(
        column.Name,
        column.UserType == Tds.ResultColumn.TimestampUserType ? "rowversion"
            : column.Type.Type == 0xF0 ? "varbinary"
            : column.Type.SqlName.Split('(')[0],
        column.Nullable, column.Type.Precision, column.Type.Scale);

    private static SqlServerMessage Message(Tds.ServerMessage message) =>
        new(message.Number, message.Severity, message.State, message.Line, message.Procedure, message.Text);

    /// <summary>Forgets a named server and closes its idle sessions. False when there was no such name.</summary>
    public static bool Disconnect(string name) => Connections.Remove(name, out _);

    /// <summary>Receives DaffySQL's diagnostic lines - logins, redirects, sessions closed - as (code, text). Never passwords.</summary>
    public static Action<string, string>? Diagnostics
    {
        get => Log.Sink;
        set => Log.Sink = value;
    }
}
