using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DaffySQL.Client;

namespace DaffySQL.DuckDb;

/// <summary>
///   daffy_exec(connection VARCHAR, tsql VARCHAR) -> TABLE (rows_affected BIGINT, messages VARCHAR)
///
/// Runs T-SQL for its effects - DDL, INSERT, UPDATE, DELETE, a stored procedure - and returns one
/// row: the rows its statements changed, and its PRINT output and other messages (NULL for none).
/// Result sets it returns are read past. Use it as CALL daffy_exec('sales', '...') or in a FROM.
///
/// Unlike daffy_query it runs when the scan starts, not at bind, so DESCRIBE, EXPLAIN and a PREPARE
/// that is never executed do not run it. Each call is its own batch on a pooled session: a
/// transaction must begin and end within one, and one left open is rolled back before the session
/// is used again. A batch that fails part-way keeps what its earlier statements did, as T-SQL does
/// without a transaction around it.
/// </summary>
internal static unsafe class ExecFunction
{
    public static void Register(nint connection)
    {
        nint function = DuckApi.CreateTableFunction();
        try
        {
            DuckFunction.SetName(function, "daffy_exec", DuckApi.TableFunctionSetName);
            DuckFunction.AddParameter(function, DuckType.Varchar, DuckApi.TableFunctionAddParameter);
            DuckFunction.AddParameter(function, DuckType.Varchar, DuckApi.TableFunctionAddParameter);
            DuckApi.TableFunctionSetBind(function, &Bind);
            DuckApi.TableFunctionSetInit(function, &Init);
            DuckApi.TableFunctionSetFunction(function, &Scan);
            if (DuckApi.RegisterTableFunction(connection, function) != 0)
                throw new InvalidOperationException("DuckDB refused to register daffy_exec");
        }
        finally
        {
            DuckApi.DestroyTableFunction(&function);
        }
    }

    private sealed record BindState(NamedConnection Connection, string Sql, ulong DuckConnection);

    /// <summary>One execution: it has run once it has returned its row.</summary>
    private sealed class ExecState
    {
        public bool Done;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Bind(nint info)
    {
        string? name = null;
        try
        {
            name = DuckFunction.Parameter(info, 0, "daffy_exec", "the name of a connection");
            string sql = DuckFunction.Parameter(info, 1, "daffy_exec", "T-SQL to run");

            var state = new BindState(Connections.Get(name), sql, DuckFunction.ConnectionId(info));
            DuckFunction.AddResultColumn(info, "rows_affected", DuckType.BigInt);
            DuckFunction.AddResultColumn(info, "messages", DuckType.Varchar);
            DuckApi.BindSetCardinality(info, 1, 1);
            DuckApi.BindSetBindData(info, DuckFunction.Keep(state), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.BindSetError, info, QueryFunction.Describe(name, ex));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Init(nint info)
    {
        try
        {
            DuckApi.InitSetInitData(info, DuckFunction.Keep(new ExecState()), DuckFunction.Release);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.InitSetError, info, ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Scan(nint info, nint chunk)
    {
        BindState? bound = null;
        try
        {
            var state = DuckFunction.Kept<ExecState>(DuckApi.FunctionGetInitData(info));
            if (state.Done)
            {
                DuckApi.DataChunkSetSize(chunk, 0);
                return;
            }
            state.Done = true;

            bound = DuckFunction.Kept<BindState>(DuckApi.FunctionGetBindData(info));
            var query = ServerQuery.Begin(bound.Connection, bound.Sql, bound.DuckConnection, q => q.ReadToEnd());
            long affected = query.RowsAffected;
            string? messages = query.Messages.Count == 0 ? null : string.Join("\n", query.Messages.Select(m => m.Text));
            query.Finish();

            ((long*)DuckApi.VectorGetData(DuckApi.DataChunkGetVector(chunk, 0)))[0] = affected;
            DuckFunction.WriteString(DuckApi.DataChunkGetVector(chunk, 1), 0, messages);
            DuckApi.DataChunkSetSize(chunk, 1);
        }
        catch (Exception ex)
        {
            DuckApi.SetError(DuckApi.FunctionSetError, info, QueryFunction.Describe(bound?.Connection.Name, ex));
        }
    }
}
