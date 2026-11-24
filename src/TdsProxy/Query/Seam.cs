using System.Text;
using TdsProxy.Sql;
namespace TdsProxy.Query;

public enum RequestKind { Batch, Rpc }

/// <summary>
/// Column types the response writer can encode. The first eight carry data; VarChar and DateTime
/// exist so a system procedure can answer in exactly the types SQL Server uses, because ODBC and
/// OLE DB read those results by position and type rather than by name.
/// </summary>
public enum SqlColumnType
{
    Int, BigInt, Double, Decimal, NVarChar, Bit, Date, DateTime2,
    TinyInt, SmallInt, Real, Time, VarBinary, UniqueIdentifier, VarChar, DateTime, Binary,
}

/// <summary>
/// One parameter recovered from an sp_executesql RPC. <paramref name="DeclaredType"/> comes from
/// the @params declaration string the client sends alongside the statement.
/// </summary>
public sealed record SqlParameterValue(string Name, string DeclaredType, byte TdsTypeByte, object? Value)
{
    /// <summary>
    /// The client passed it by reference, as an OUTPUT parameter: sp_prepexec's @handle, for one,
    /// which the server fills in and sends back.
    /// </summary>
    public bool IsOutput { get; init; }

    public override string ToString() =>
        $"{Name} {DeclaredType} = {(Value is null ? "NULL" : Value is string s ? $"'{s}'" : Value)}" +
        (IsOutput ? " OUTPUT" : "");
}

/// <summary>
/// An OUTPUT parameter's value, sent back after the procedure's results as RETURNVALUE.
/// <paramref name="Ordinal"/> is the parameter's position in the call and <paramref name="Name"/>
/// the name the client gave it, empty if it gave none. Every procedure that has one here returns
/// an int: a statement or cursor handle, or a cursor's options.
/// </summary>
public sealed record ReturnValue(int Ordinal, string Name, int? Value);

/// <summary>
/// What one connection remembers between its requests, beyond the database it is in: the
/// statements it has prepared, and SET ROWCOUNT. It goes away with the connection.
/// </summary>
public sealed class SessionState
{
    /// <summary>SET ROWCOUNT: the most rows a query returns, or 0 for no limit.</summary>
    public int RowCount { get; set; }

    /// <summary>sp_prepare and sp_prepexec's statements, by the handle they were given.</summary>
    public Dictionary<int, PreparedStatement> Prepared { get; } = [];

    private int _lastHandle;

    /// <summary>
    /// The next handle, for a prepared statement or a cursor. SQL Server numbers prepared
    /// statements from 1 on each connection, and so does this.
    /// </summary>
    public int NextHandle() => ++_lastHandle;
}

/// <summary>A statement sp_prepare or sp_prepexec kept, and the parameters it declared.</summary>
public sealed record PreparedStatement(string Sql, string Declarations);

/// <summary>
/// Who is connected, as recovered from LOGIN7. This is what a handler needs in order to make a
/// decision about a caller — authenticate them, pick their credentials, scope what they can see.
///
/// <paramref name="Password"/> is present only for a SQL login, and only because TDS sends it: the
/// wire form is an obfuscation, not encryption, so treat it as readable by anyone on the network
/// unless the connection is encrypted. <paramref name="IsEncrypted"/> says whether it was.
/// </summary>
public sealed record SqlSession(
    int SessionId,
    string UserName,
    string? Password,
    string Database,
    string HostName,
    string ApplicationName,
    string ClientAddress,
    bool IsEncrypted)
{
    /// <summary>
    /// True for daffydb's own prompt, and never for a connection. Whoever is at the prompt started
    /// the server, and could have mounted anything with --data-dir or --init, so the admin
    /// commands that --allow-admin opens to clients are always open to them.
    /// </summary>
    public bool IsConsole { get; init; }

    /// <summary>
    /// The connection's own state. A copy made with <c>with</c> shares it, which is the point: the
    /// session record is replaced when a USE moves it, and the connection is still the same one.
    /// </summary>
    public SessionState State { get; init; } = new();

    /// <summary>
    /// Stands in before a login has happened, and for handlers invoked out of band. It names no
    /// database, which a handler takes to mean its default one. Each is a new one, so two callers
    /// out of band never share a <see cref="State"/>.
    /// </summary>
    public static SqlSession Anonymous => new(0, "", null, "", "", "", "", IsEncrypted: false);

    /// <summary>Safe to log: never includes the password, only whether one was sent.</summary>
    public override string ToString() =>
        $"session {SessionId} user='{UserName}' db='{Database}' host='{HostName}' " +
        $"app='{ApplicationName}' from={ClientAddress} " +
        $"password={(Password is null ? "none" : $"{Password.Length} chars")} " +
        $"encrypted={IsEncrypted}";
}

/// <summary>
/// A handler's answer to a login attempt. Rejecting sends the client an ERROR token in place of
/// LOGINACK, which is what a real server does for a bad password.
/// </summary>
public sealed record LoginVerdict(bool Accepted, string? Reason = null)
{
    /// <summary>
    /// The database the session starts in. Left null, the server decides: the one the client
    /// asked for, or master if it asked for none.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>
    /// SQL Server's number for the refusal. 18456 is a failed login; anything else - 4060, for a
    /// database that cannot be opened - is sent first, followed by the 18456 a real server adds.
    /// </summary>
    public int Number { get; init; } = 18456;

    public static LoginVerdict Accept { get; } = new(true);

    public static LoginVerdict AcceptInto(string database) => new(true) { Database = database };

    public static LoginVerdict Reject(string reason) => new(false, reason);

    public static LoginVerdict UnknownDatabase(string database) =>
        new(false, $"Cannot open database \"{database}\" requested by the login. The login failed.")
        {
            Number = Databases.LoginNotFoundNumber,
        };
}

/// <summary>What the proxy extracted from the client. This is the whole point of phase 1.</summary>
public sealed record SqlRequest(RequestKind Kind, string Sql, IReadOnlyList<SqlParameterValue> Parameters)
{
    /// <summary>The connection this arrived on. Always set by the server; defaults for direct use.</summary>
    public SqlSession Session { get; init; } = SqlSession.Anonymous;

    /// <summary>
    /// The procedure an RPC names, when it names one: ODBC and OLE DB call their catalog
    /// procedures this way, with no SQL text at all. Null for batches, and for sp_executesql,
    /// whose statement is in <see cref="Sql"/>.
    /// </summary>
    public string? Procedure { get; init; }
}

/// <summary>
/// Precision and scale are only meaningful for <see cref="SqlColumnType.Decimal"/>.
/// <paramref name="MaxLength"/> is the declared size in bytes of a character or binary column;
/// left null, the writer sizes the column from the widest value it is about to send.
/// </summary>
public sealed record ColumnDef(string Name, SqlColumnType Type, bool Nullable, byte Precision = 38, byte Scale = 4,
                               int? MaxLength = null);

public sealed record ResultSet(IReadOnlyList<ColumnDef> Columns, IReadOnlyList<object?[]> Rows)
{
    public static ResultSet Empty { get; } = new([], []);
}

/// <summary>
/// What one request produced. A batch can contain several statements, and each that returns rows
/// contributes a result set — "SELECT 1; SELECT 2" is two, "SET NOCOUNT ON" is none.
/// </summary>
public sealed record QueryResult(IReadOnlyList<ResultSet> ResultSets)
{
    /// <summary>
    /// The database a USE in the request moved the session to, or null if there was none. The
    /// server tells the client, and every later request on the session runs there.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>
    /// Informational messages, sent as a PRINT's would be, ahead of the result sets: the line a
    /// daffydb.* command reports what it did in, and anything it passed over.
    /// </summary>
    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>The OUTPUT parameters an RPC sets, sent back after its results.</summary>
    public IReadOnlyList<ReturnValue> ReturnValues { get; init; } = [];

    /// <summary>
    /// Errors raised after the result sets were made, sent after them, as SQL Server sends them:
    /// daffydb.run_sqlserver's, whose rows are real whatever came next. A failure of DaffyDB's own
    /// is a <see cref="TdsQueryException"/>, and replaces the whole answer.
    /// </summary>
    internal IReadOnlyList<SqlErrorInfo> Errors { get; init; } = [];

    public static QueryResult None { get; } = new([]);

    public static QueryResult Single(ResultSet resultSet) => new([resultSet]);
}

/// <summary>
/// The seam between protocol and everything downstream. Phase 1 ships
/// <see cref="EchoQueryHandler"/>; phase 2 implements this against DuckDB (or anything else) and
/// changes one line in Program.cs. No protocol work is involved on this side.
/// </summary>
public interface IQueryHandler
{
    Task<QueryResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Called once per connection, after LOGIN7 is decoded and before LOGINACK is sent, so a
    /// handler can inspect the caller and turn them away. The default accepts everyone, which is
    /// what a handler with no opinion about identity wants.
    /// </summary>
    Task<LoginVerdict> OnLoginAsync(SqlSession session, CancellationToken cancellationToken) =>
        Task.FromResult(LoginVerdict.Accept);

    /// <summary>
    /// The client cancelled <paramref name="request"/>, which <see cref="ExecuteAsync"/> may still be
    /// running. A handler that can stop work in progress does; this may be called more than once for
    /// the same request, and for one that has already finished. The default does nothing, and the
    /// request runs to its end.
    /// </summary>
    void Cancel(SqlRequest request) { }
}

/// <summary>
/// Phase 1 handler: reflects the decoded request back as a result set, while exercising every
/// column type the writer supports (including a NULL) so the encoder is proven end to end.
/// </summary>
public sealed class EchoQueryHandler : IQueryHandler
{
    private static readonly IReadOnlyList<ColumnDef> Columns =
    [
        new("n", SqlColumnType.Int, Nullable: false),
        new("sql", SqlColumnType.NVarChar, Nullable: false),
        new("is_rpc", SqlColumnType.Bit, Nullable: false),
        new("missing", SqlColumnType.Int, Nullable: true),
        new("received_at", SqlColumnType.DateTime2, Nullable: false),
    ];

    public Task<QueryResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        if (SqlStatement.IsNonQuery(request.Sql)) return Task.FromResult(QueryResult.None);

        bool isRpc = request.Kind == RequestKind.Rpc;
        var rows = new List<object?[]>();

        rows.Add(new object?[] { request.Parameters.Count, request.Sql, isRpc, null, DateTime.Now });

        foreach (var parameter in request.Parameters)
        {
            rows.Add(new object?[] { rows.Count, parameter.ToString(), isRpc, null, DateTime.Now });
        }

        return Task.FromResult(QueryResult.Single(new ResultSet(Columns, rows)));
    }
}

public static class SqlStatement
{
    /// <summary>
    /// Session options and transaction control. These are accepted and quietly ignored: the proxy
    /// runs in autocommit, so there is nothing to apply and DuckDB would reject the syntax
    /// outright. USE is here for handlers with no databases to move between; the DuckDB handler
    /// acts on it before it gets this far.
    /// </summary>
    private static readonly string[] IgnoredPrefixes =
        ["SET", "USE", "DECLARE", "PRINT", "BEGIN", "COMMIT", "ROLLBACK", "GO"];

    /// <summary>
    /// Statements that do real work but return a row count rather than a result set. They must be
    /// executed, and their result discarded — DuckDB answers an INSERT with a one-column "Count",
    /// which a client would otherwise see as an unexpected extra result set.
    /// </summary>
    private static readonly string[] NoResultPrefixes =
        ["CREATE", "DROP", "ALTER", "TRUNCATE", "INSERT", "UPDATE", "DELETE", "MERGE"];

    public static bool IsIgnorable(string statement) => StartsWithAny(statement, IgnoredPrefixes);

    public static bool ProducesResultSet(string statement) => !StartsWithAny(statement, NoResultPrefixes);

    /// <summary>
    /// True only when *no* statement in the batch can return rows.
    ///
    /// Checking just the first statement is not enough: tools send batches like
    /// "DECLARE @x ...; SET @x = ...; SELECT ..." where the leading statement is a non-query but
    /// the batch very much does return a result set. Answering that with nothing makes the client
    /// fail with a confusing error about a missing table rather than anything actionable.
    /// </summary>
    public static bool IsNonQuery(string sql)
    {
        var statements = SqlSegments.SplitStatements(sql);
        return statements.Count == 0
            || statements.All(s => IsIgnorable(s) || !ProducesResultSet(s));
    }

    private static bool StartsWithAny(string statement, string[] prefixes) =>
        prefixes.Any(prefix =>
            statement.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (statement.Length == prefix.Length || !char.IsLetterOrDigit(statement[prefix.Length])));
}
