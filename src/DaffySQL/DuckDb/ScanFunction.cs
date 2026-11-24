using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DaffySQL.Client;
using DaffySQL.Tds;

namespace DaffySQL.DuckDb;

/// <summary>
///   daffy_scan(connection VARCHAR, table VARCHAR, filter := VARCHAR)
///   daffy_columns(connection VARCHAR, table VARCHAR)
///
/// daffy_scan reads a SQL Server table or view, asking the server for only the columns the DuckDB
/// query uses: SELECT Name FROM daffy_scan('sales', 'dbo.Customers') sends
/// SELECT [Name] FROM [dbo].[Customers]. count(*) still reads one column, the first.
///
/// DuckDB's C API has no filter pushdown, so a WHERE in the DuckDB query is applied by DuckDB after
/// the rows arrive. filter is the way round that: a T-SQL condition, sent as the SELECT's WHERE, so
/// filter := '[Total] > 100' sends SELECT ... FROM [dbo].[Orders] WHERE ([Total] > 100). It is
/// T-SQL, compared by SQL Server's rules (its collation's, for text), and passed on as written: it
/// is the caller's to build, as daffy_query's T-SQL is - but held to one condition, so that it
/// cannot close the WHERE's parenthesis and add statements of its own (SqlNames.Condition). NULL or
/// blank is no filter.
///
/// Its bind only describes the table, with SELECT TOP (0) - and the filter, so a misnamed column or
/// a syntax error in it fails there - so DESCRIBE and EXPLAIN cost one round trip and read no rows;
/// the SELECT itself is sent when the scan starts. Both wait no longer than the connection's Lock
/// Timeout for a lock. daffy_columns returns that description as rows:
/// each column, its SQL Server type, and the DuckDB type it arrives as.
/// </summary>
internal static unsafe class ScanFunction
{
    /// <summary>The SELECT the last daffy_scan sent. For tests, which check what was asked of the server.</summary>
    internal static string? LastSql;

    public static void Register(nint connection, bool scan, bool columns)
    {
        if (scan) Register(connection, "daffy_scan", &Bind, &Init, &QueryFunction.Scan, projection: true);
        if (columns) Register(connection, "daffy_columns", &ColumnsBind, &ColumnsInit, &ColumnsScan, projection: false);
    }

    private static void Register(nint connection, string name, delegate* unmanaged[Cdecl]<nint, void> bind,
                                 delegate* unmanaged[Cdecl]<nint, void> init, delegate* unmanaged[Cdecl]<nint, nint, void> scan, bool projection)
    {
        nint function = DuckApi.CreateTableFunction();
        try
        {
            DuckFunction.SetName(function, name, DuckApi.TableFunctionSetName);
            DuckFunction.AddParameter(function, DuckType.Varchar, DuckApi.TableFunctionAddParameter);
            DuckFunction.AddParameter(function, DuckType.Varchar, DuckApi.TableFunctionAddParameter);
            if (projection) DuckFunction.AddNamedParameter(function, "filter", DuckType.Varchar);
            DuckApi.TableFunctionSetBind(function, bind);
            DuckApi.TableFunctionSetInit(function, init);
            DuckApi.TableFunctionSetFunction(function, scan);
            if (projection) DuckApi.TableFunctionSupportsProjectionPushdown(function, 1);
            if (DuckApi.RegisterTableFunction(connection, function) != 0)
                throw new InvalidOperationException($"DuckDB refused to register {name}");
        }
        finally
        {
            DuckApi.DestroyTableFunction(&function);
        }
    }

    /// <summary>A table as SQL Server describes it: its quoted name, its columns' own names, how each arrives in DuckDB.</summary>
    private sealed record Described(string Table, IReadOnlyList<ResultColumn> Columns, ColumnPlan[] Plans);

    /// <summary>Where: the WHERE clause the filter makes, with its leading space, or "" for no filter.</summary>
    private sealed record BindState(NamedConnection Connection, ulong DuckConnection, Described Table, string Where);

    private static string Where(string? filter) => string.IsNullOrWhiteSpace(filter) ? "" : $" WHERE ({SqlNames.Condition(filter)})";

    /// <summary>SELECT TOP (0) *: the table's columns, as a result's would be described, and no rows.</summary>
    private static Described Describe(NamedConnection connection, string name, ulong duckConnection, string where = "")
    {
        string table = SqlNames.Table(name);
        var query = ServerQuery.Begin(connection, $"{connection.Endpoint.LockTimeoutPrefix}SELECT TOP (0) * FROM {table}{where}", duckConnection, q =>
        {
            q.ReadColumns();
            q.ReadPastRest();
        });
        try
        {
            return new Described(table, query.Columns, ResultPlan.For(query.Columns));
        }
        finally
        {
            query.Finish();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Bind(nint info)
    {
        string? name = null;
        try
        {
            name = DuckFunction.Parameter(info, 0, "daffy_scan", "the name of a connection");
            string table = DuckFunction.Parameter(info, 1, "daffy_scan", "a table name");
            string where = Where(DuckApi.TakeString(DuckApi.NamedParameter(info, "filter")));

            var connection = Connections.Get(name);
            ulong duckConnection = DuckFunction.ConnectionId(info);
            var described = Describe(connection, table, duckConnection, where);

            foreach (var plan in described.Plans) DuckFunction.AddResultColumn(info, plan.Name, plan.DuckType, plan.Width, plan.Scale);
            DuckApi.BindSetBindData(info, DuckFunction.Keep(new BindState(connection, duckConnection, described, where)), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.BindSetError, info, QueryFunction.Describe(name, ex));
        }
    }

    /// <summary>
    /// The SELECT, with the columns DuckDB asked for in the order of its chunk's vectors - so each
    /// result column lands in its own vector. Asked for none that are the table's (a row id), it
    /// takes the first, so there are rows to count.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Init(nint info)
    {
        BindState? bound = null;
        try
        {
            bound = DuckFunction.Kept<BindState>(DuckApi.InitGetBindData(info));
            var table = bound.Table;
            var projection = QueryFunction.Projection(info);

            var selected = projection.Where(c => c < table.Plans.Length).ToList();
            if (selected.Count == 0) selected.Add(0);
            string sql = $"SELECT {string.Join(", ", selected.Select(c => SqlNames.Quote(table.Columns[c].Name)))} FROM {table.Table}{bound.Where}";
            LastSql = sql;

            var started = QueryFunction.Start(bound.Connection, bound.Connection.Endpoint.LockTimeoutPrefix + sql, bound.DuckConnection);
            try
            {
                for (int k = 0; k < selected.Count; k++)
                    if (started.Plans[k].DuckSignature != table.Plans[selected[k]].DuckSignature)
                        throw new InvalidOperationException(
                            $"column {SqlNames.Quote(table.Columns[selected[k]].Name)} of {table.Table} is {started.Plans[k].SqlType} now, " +
                            $"not the {table.Plans[selected[k]].SqlType} it was when DuckDB bound the query; run the statement again");

                var position = selected.Select((column, k) => (column, k)).ToDictionary(p => p.column, p => p.k);
                var vectors = projection.Select(c => position.TryGetValue(c, out int k) ? k : int.MaxValue).ToArray();
                var scan = new QueryFunction.ScanState(started.Query, new RowWriter(started.Plans, vectors));
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
            DuckApi.SetError(DuckApi.InitSetError, info, QueryFunction.Describe(bound?.Connection.Name, ex));
        }
    }

    // ---- daffy_columns(connection, table) ---------------------------------------------------

    private sealed record ColumnRow(string Name, int Ordinal, string SqlType, string DuckType, bool Nullable);

    private sealed class ColumnsScanState(List<ColumnRow> rows)
    {
        public List<ColumnRow> Rows { get; } = rows;
        public int Next;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ColumnsBind(nint info)
    {
        string? name = null;
        try
        {
            name = DuckFunction.Parameter(info, 0, "daffy_columns", "the name of a connection");
            string table = DuckFunction.Parameter(info, 1, "daffy_columns", "a table name");
            var described = Describe(Connections.Get(name), table, DuckFunction.ConnectionId(info));

            var rows = described.Columns.Select((column, i) => new ColumnRow(
                column.Name, i + 1, described.Plans[i].SqlType, described.Plans[i].DuckSignature, column.Nullable)).ToList();

            DuckFunction.AddResultColumn(info, "column_name", DuckType.Varchar);
            DuckFunction.AddResultColumn(info, "ordinal", DuckType.Integer);
            DuckFunction.AddResultColumn(info, "sql_type", DuckType.Varchar);
            DuckFunction.AddResultColumn(info, "duckdb_type", DuckType.Varchar);
            DuckFunction.AddResultColumn(info, "nullable", DuckType.Boolean);
            DuckApi.BindSetCardinality(info, (ulong)rows.Count, 1);
            DuckApi.BindSetBindData(info, DuckFunction.Keep(rows), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.BindSetError, info, QueryFunction.Describe(name, ex));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ColumnsInit(nint info)
    {
        try
        {
            var rows = DuckFunction.Kept<List<ColumnRow>>(DuckApi.InitGetBindData(info));
            DuckApi.InitSetInitData(info, DuckFunction.Keep(new ColumnsScanState(rows)), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.InitSetError, info, ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ColumnsScan(nint info, nint chunk)
    {
        try
        {
            var scan = DuckFunction.Kept<ColumnsScanState>(DuckApi.FunctionGetInitData(info));
            int count = Math.Min(scan.Rows.Count - scan.Next, (int)DuckApi.VectorSize());
            nint Vector(int column) => DuckApi.DataChunkGetVector(chunk, (ulong)column);

            for (int i = 0; i < count; i++)
            {
                var row = scan.Rows[scan.Next + i];
                DuckFunction.WriteString(Vector(0), (ulong)i, row.Name);
                ((int*)DuckApi.VectorGetData(Vector(1)))[i] = row.Ordinal;
                DuckFunction.WriteString(Vector(2), (ulong)i, row.SqlType);
                DuckFunction.WriteString(Vector(3), (ulong)i, row.DuckType);
                ((byte*)DuckApi.VectorGetData(Vector(4)))[i] = row.Nullable ? (byte)1 : (byte)0;
            }

            scan.Next += count;
            DuckApi.DataChunkSetSize(chunk, (ulong)count);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.FunctionSetError, info, ex.Message);
        }
    }
}
