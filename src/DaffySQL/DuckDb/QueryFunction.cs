using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DaffySQL.Client;
using DaffySQL.Tds;

namespace DaffySQL.DuckDb;

/// <summary>
///   daffy_query(connection VARCHAR, tsql VARCHAR)
///   daffy_tables(connection VARCHAR)
///
/// daffy_query runs T-SQL on a named SQL Server and returns its first result set as a DuckDB table,
/// streamed: rows are read from the server as DuckDB asks for them, 2048 at a time, straight into
/// its vectors. daffy_tables is the same with T-SQL of DaffySQL's own, listing the tables and views.
///
/// The T-SQL runs when DuckDB binds the query, because that is when DuckDB must be told the columns
/// and nothing describes a T-SQL batch's result as exactly as running it. The scan then carries on
/// reading the answer the bind started. A query DuckDB binds but never scans - DESCRIBE, EXPLAIN,
/// a PREPARE that is not executed - is cancelled on the server when DuckDB lets go of it; one it
/// scans again - a prepared statement executed twice - is run again, and must come back with the
/// same columns. So daffy_query is for reading; daffy_exec is for changing things.
/// </summary>
internal static unsafe class QueryFunction
{
    /// <summary>Times a scan had to run its query again, the bind's answer having been used. For tests.</summary>
    internal static int Reruns;

    private const string TablesSql =
        "SELECT s.name AS table_schema, o.name AS table_name, " +
        "CASE o.type WHEN 'U' THEN 'BASE TABLE' ELSE 'VIEW' END AS table_type, " +
        "(SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS row_count " +
        "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
        "WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name";

    public static void Register(nint connection, bool query, bool tables)
    {
        if (query) RegisterTable(connection, "daffy_query", parameters: 2, &Bind);
        if (tables) RegisterTable(connection, "daffy_tables", parameters: 1, &TablesBind);
    }

    private static void RegisterTable(nint connection, string name, int parameters, delegate* unmanaged[Cdecl]<nint, void> bind)
    {
        nint function = DuckApi.CreateTableFunction();
        try
        {
            DuckFunction.SetName(function, name, DuckApi.TableFunctionSetName);
            for (int i = 0; i < parameters; i++) DuckFunction.AddParameter(function, DuckType.Varchar, DuckApi.TableFunctionAddParameter);
            DuckApi.TableFunctionSetBind(function, bind);
            DuckApi.TableFunctionSetInit(function, &Init);
            DuckApi.TableFunctionSetFunction(function, &Scan);
            DuckApi.TableFunctionSupportsProjectionPushdown(function, 1);
            if (DuckApi.RegisterTableFunction(connection, function) != 0)
                throw new InvalidOperationException($"DuckDB refused to register {name}");
        }
        finally
        {
            DuckApi.DestroyTableFunction(&function);
        }
    }

    /// <summary>A query running on a session, and the DuckDB columns its result was planned as.</summary>
    internal sealed record Started(ServerQuery Query, ColumnPlan[] Plans);

    /// <summary>What a bind leaves for the scans: where and what to run, and the answer it started.</summary>
    private sealed class BindState(NamedConnection connection, string sql, ulong duckConnection, Started started) : IDisposable
    {
        private Started? _pending = started;

        public NamedConnection Connection { get; } = connection;
        public string Sql { get; } = sql;
        public ulong DuckConnection { get; } = duckConnection;
        public ColumnPlan[] Plans { get; } = started.Plans;

        public Started? TakePending() => Interlocked.Exchange(ref _pending, null);

        /// <summary>DuckDB is done with the query. If no scan ever took the answer the bind started, it is cancelled.</summary>
        public void Dispose() => TakePending()?.Query.Finish();
    }

    /// <summary>One scan's query and the writer its rows go through. Shared with daffy_scan.</summary>
    internal sealed class ScanState(ServerQuery query, RowWriter writer) : IDisposable
    {
        public ServerQuery Query { get; } = query;
        public RowWriter Writer { get; } = writer;

        /// <summary>DuckDB is done with the scan: read to the end, or stopped early, for whatever reason.</summary>
        public void Dispose() => Query.Finish();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Bind(nint info) => BindQuery(info, "daffy_query", fixedSql: null);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void TablesBind(nint info) => BindQuery(info, "daffy_tables", TablesSql);

    private static void BindQuery(nint info, string function, string? fixedSql)
    {
        string? name = null;
        try
        {
            name = DuckFunction.Parameter(info, 0, function, "the name of a connection");
            var connection = Connections.Get(name);

            // T-SQL of DaffySQL's own waits no longer than Lock Timeout; the caller's runs as written.
            string sql = fixedSql is null
                ? DuckFunction.Parameter(info, 1, function, "T-SQL to run")
                : connection.Endpoint.LockTimeoutPrefix + fixedSql;

            ulong duckConnection = DuckFunction.ConnectionId(info);
            var state = new BindState(connection, sql, duckConnection, Start(connection, sql, duckConnection));
            try
            {
                foreach (var plan in state.Plans) DuckFunction.AddResultColumn(info, plan.Name, plan.DuckType, plan.Width, plan.Scale);
                DuckApi.BindSetBindData(info, DuckFunction.Keep(state), DuckFunction.Release);
            }
            catch
            {
                state.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.BindSetError, info, Describe(name, ex));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Init(nint info)
    {
        BindState? bound = null;
        try
        {
            bound = DuckFunction.Kept<BindState>(DuckApi.InitGetBindData(info));
            var started = bound.TakePending() ?? Rerun(bound);
            try
            {
                var scan = new ScanState(started.Query, new RowWriter(started.Plans, Projection(info)));
                DuckApi.InitSetInitData(info, DuckFunction.Keep(scan), DuckFunction.Release);
            }
            catch
            {
                started.Query.Finish();
                throw;
            }
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.InitSetError, info, Describe(bound?.Connection.Name, ex));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void Scan(nint info, nint chunk)
    {
        ScanState? scan = null;
        try
        {
            scan = DuckFunction.Kept<ScanState>(DuckApi.FunctionGetInitData(info));
            scan.Writer.Begin(chunk);
            int rows = scan.Query.ReadRows(scan.Writer, (int)DuckApi.VectorSize());
            DuckApi.DataChunkSetSize(chunk, (ulong)rows);   // 0 ends the scan
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.FunctionSetError, info, Describe(scan?.Query.Connection.Name, ex));
        }
    }

    /// <summary>The columns DuckDB wants from a scan, in the order of its chunk's vectors.</summary>
    internal static int[] Projection(nint initInfo)
    {
        var projection = new int[DuckApi.InitGetColumnCount(initInfo)];
        for (int i = 0; i < projection.Length; i++)
            projection[i] = (int)Math.Min(DuckApi.InitGetColumnIndex(initInfo, (ulong)i), int.MaxValue);
        return projection;
    }

    /// <summary>Runs T-SQL and reads its answer as far as its first result set's columns.</summary>
    internal static Started Start(NamedConnection connection, string sql, ulong duckConnection)
    {
        var query = ServerQuery.Begin(connection, sql, duckConnection, q => q.ReadColumns());
        try
        {
            return new Started(query, ResultPlan.For(query.Columns));
        }
        catch
        {
            query.Finish();
            throw;
        }
    }

    /// <summary>The query again, for a second scan of one bind. DuckDB was told the columns once, and they must not have changed.</summary>
    private static Started Rerun(BindState bound)
    {
        Interlocked.Increment(ref Reruns);
        var started = Start(bound.Connection, bound.Sql, bound.DuckConnection);
        if (!started.Plans.Select(p => p.DuckSignature).SequenceEqual(bound.Plans.Select(p => p.DuckSignature)))
        {
            started.Query.Finish();
            throw new InvalidOperationException("the query's result no longer has the columns it had when DuckDB bound it; run the statement again");
        }
        return started;
    }

    /// <summary>The message DuckDB shows: server-side failures say which connection they came from.</summary>
    internal static string Describe(string? connection, Exception ex)
    {
        string message = ex is ConnectException ? $"could not log in: {ex.Message}" : ex.Message;
        bool fromServer = ex is SqlServerException or ConnectException or NoResultSetException or TdsUnsupportedException
                              or OperationCanceledException or IOException or InvalidDataException;
        return fromServer && connection is not null ? $"{connection}: {message}" : message;
    }
}
