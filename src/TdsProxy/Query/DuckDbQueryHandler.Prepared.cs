using System.Text.RegularExpressions;
using TdsProxy.Sql;
using TdsProxy.Tds;

namespace TdsProxy.Query;

/// <summary>
/// Prepared statements and cursors, the way drivers use them rather than the way a person would:
/// sp_prepare, sp_execute, sp_prepexec and sp_unprepare, and sp_cursoropen.
///
/// A linked server is what needs them. Through ODBC, SQL Server asks for a remote query's columns
/// before it runs the query - sp_cursoropen for a table it names, sp_prepare for OPENQUERY - and
/// then runs it, with sp_prepexec or as a plain batch. A driver's prepared statement uses the same
/// procedures, as SqlClient does after SqlCommand.Prepare().
///
/// A cursor is opened only to describe its columns, which is all SQL Server wants of one. It is
/// never filled, so fetching from it is refused.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    /// <summary>SQL Server's number for an sp_execute handle that was never prepared, or was unprepared.</summary>
    private const int UnknownHandleNumber = 8179;

    /// <summary>sp_cursoropen's concurrency option for a read-only cursor, the only kind there is here.</summary>
    private const int ReadOnlyCursor = 0x0001;

    private static readonly HashSet<string> PreparedProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_prepare", "sp_execute", "sp_prepexec", "sp_unprepare", "sp_cursoropen", "sp_cursorclose",
        "sp_cursor", "sp_cursorfetch", "sp_cursoroption", "sp_cursorprepare", "sp_cursorexecute",
        "sp_cursorprepexec", "sp_cursorunprepare",
    };

    private static readonly Regex RowCountPattern = new(
        @"^\s*SET\s+ROWCOUNT\s+(?<rows>@?\w+)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QueryStart = new(
        @"^\s*(?:\(|SELECT\b|WITH\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsPreparedProcedure(string name) => PreparedProcedures.Contains(SystemProcedures.BareName(name));

    /// <summary>
    /// What describing a statement runs: a query, a system procedure, and the EXEC('...') that
    /// wraps either. Anything else - a write, USE, SET, DaffyDB's commands, sp_duckdb - is passed
    /// over, as sp_prepare passes over it on SQL Server.
    /// </summary>
    private static bool IsDescribable(string statement)
    {
        if (DaffyCommands.TryParse(statement, out _) || SystemProcedures.TryParseDuckDb(statement, out _)) return false;
        if (SystemProcedures.TryParseInsertExec(statement, out _, out _)) return false;
        if (SystemProcedures.TryParseCall(statement, out var call))
            return SystemProcedures.Find(call.Name) is not null ||
                   SystemProcedures.BareName(call.Name).Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);
        if (LocalVariables.TryParseExec(statement, out _)) return true;
        return QueryStart.IsMatch(statement);
    }

    private void RunPrepared(BatchContext context)
    {
        var request = context.Request;
        string procedure = SystemProcedures.BareName(request.Procedure!).ToLowerInvariant();
        var state = request.Session.State;

        SqlParameterValue? Argument(int position) =>
            position < request.Parameters.Count ? request.Parameters[position] : null;

        switch (procedure)
        {
            // sp_prepare @handle OUTPUT, @params, @stmt [, @options]: keep the statement, and say
            // what it returns. Its parameters are NULL while it is described.
            case "sp_prepare":
            {
                var prepared = new PreparedStatement(Text(Argument(2)), Text(Argument(1)));
                int handle = state.NextHandle();
                state.Prepared[handle] = prepared;
                Chatter.Line($"[duckdb] prepare: handle {handle}");

                var unbound = RequestParser.Declarations(prepared.Declarations)
                    .Select(d => new SqlParameterValue(d.Name, d.Type, 0, null))
                    .ToList();
                RunStatement(prepared.Sql, unbound, context, describeOnly: true);
                Return(context, 0, Argument(0), handle);
                break;
            }

            // sp_prepexec @handle OUTPUT, @params, @stmt, values...: keep it, and run it.
            case "sp_prepexec":
            {
                var prepared = new PreparedStatement(Text(Argument(2)), Text(Argument(1)));
                int handle = state.NextHandle();
                state.Prepared[handle] = prepared;
                Chatter.Line($"[duckdb] prepare: handle {handle}");

                RunStatement(prepared.Sql, Bind(prepared.Declarations, request.Parameters.Skip(3)), context);
                Return(context, 0, Argument(0), handle);
                break;
            }

            // sp_execute @handle, values...: run what was prepared, with these values.
            case "sp_execute":
            {
                int handle = Handle(Argument(0));
                if (!state.Prepared.TryGetValue(handle, out var prepared))
                    throw new TdsQueryException(new SqlErrorInfo(UnknownHandleNumber,
                        $"Could not find prepared statement with handle {handle}."));

                RunStatement(prepared.Sql, Bind(prepared.Declarations, request.Parameters.Skip(1)), context);
                break;
            }

            case "sp_unprepare":
                state.Prepared.Remove(Handle(Argument(0)));
                break;

            // sp_cursoropen @cursor OUTPUT, @stmt, @scrollopt OUTPUT, @ccopt OUTPUT, @rowcount OUTPUT
            // [, @params, values...]: describe the statement, and report a read-only cursor over
            // it with as many rows as SET ROWCOUNT allows, or -1, "not known", when it is not set.
            case "sp_cursoropen":
            {
                string sql = Text(Argument(1));
                var values = Bind(Text(Argument(5)), request.Parameters.Skip(6));

                int rows = -1;
                if (state.RowCount > 0)
                {
                    // Counting the rows reads them, but no more than ROWCOUNT, and the cursor is
                    // not filled with them: only its columns go back.
                    var counted = new List<ResultSet>();
                    RunStatement(sql, values, context, collect: counted);
                    rows = counted.Count == 0 ? 0 : counted[^1].Rows.Count;
                    context.ResultSets.AddRange(counted.Select(r => r with { Rows = [] }));
                }
                else
                {
                    RunStatement(sql, values, context, describeOnly: true);
                }

                int cursor = state.NextHandle();
                Chatter.Line($"[duckdb] cursor: {cursor} described, {rows} row(s)");

                Return(context, 0, Argument(0), cursor);
                Return(context, 2, Argument(2), Number(Argument(2)) & 0x0FFF);
                Return(context, 3, Argument(3), ReadOnlyCursor);
                Return(context, 4, Argument(4), rows);
                break;
            }

            case "sp_cursorclose":
                break;

            default:
                throw new TdsQueryException(
                    $"{procedure} is not supported: DaffyDB opens a cursor only to describe its columns, " +
                    "and does not fetch through one. Read the rows with a query instead.");
        }
    }

    /// <summary>
    /// EXEC sp_executesql written in a batch: the statement, its declarations, then the values,
    /// named or in the declared order. Each argument may be a literal or a variable of the batch.
    /// </summary>
    private void RunExecuteSql(ProcedureCall call, BatchContext context)
    {
        var arguments = call.Arguments
            .Select(a => (a.Name, Value: EvaluateArgument(a.Expression, context)))
            .ToList();

        string sql = arguments.Count > 0 ? arguments[0].Value as string ?? "" : "";
        string declarations = arguments.Count > 1 ? arguments[1].Value as string ?? "" : "";
        var values = arguments.Skip(2)
            .Select(a => new SqlParameterValue(a.Name ?? "", "", 0, a.Value))
            .ToList();

        Chatter.Line($"[duckdb] executesql: {Flatten(sql)}");
        RunStatement(sql, Bind(declarations, values), context);
    }

    /// <summary>
    /// Runs a prepared statement's text with its parameters, as sp_executesql would, adding what
    /// it returns to <paramref name="context"/> - or to <paramref name="collect"/> instead. It is
    /// only described if the batch running it is.
    /// </summary>
    private void RunStatement(string sql, IReadOnlyList<SqlParameterValue> parameters, BatchContext context,
                              bool describeOnly = false, List<ResultSet>? collect = null)
    {
        var statement = new BatchContext(context.Request with { Sql = sql, Parameters = parameters, Procedure = null })
        {
            DescribeOnly = describeOnly || context.DescribeOnly,
        };

        try
        {
            Run(TSqlParser.Parse(sql), statement, depth: 1);
        }
        catch (TdsQueryException ex) when (ex.Raised is not null)
        {
            // SQL Server's own errors, through daffydb.run_sqlserver: its rows go with them, here too.
            Keep();
            throw;
        }

        Keep();

        void Keep()
        {
            (collect ?? context.ResultSets).AddRange(statement.ResultSets);
            context.Messages.AddRange(statement.Messages);
            if (statement.ChangedDatabase is { } database)
            {
                context.ChangedDatabase = database;
                context.Session = statement.Session;
            }
        }
    }

    /// <summary>
    /// The values a prepared statement is run with, named after the parameters it declared. A
    /// value the client named keeps its name; one it did not takes the next declared one's.
    /// </summary>
    private static List<SqlParameterValue> Bind(string declarations, IEnumerable<SqlParameterValue> supplied)
    {
        var declared = RequestParser.Declarations(declarations);
        var bound = new List<SqlParameterValue>();

        foreach (var value in supplied)
        {
            string name = value.Name.Length > 0 ? value.Name
                        : bound.Count < declared.Count ? declared[bound.Count].Name
                        : $"@P{bound.Count + 1}";

            string? type = declared.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Type;
            bound.Add(value with { Name = name, DeclaredType = type ?? value.DeclaredType });
        }

        return bound;
    }

    /// <summary>Sends an OUTPUT parameter's value back - if the client passed it as one.</summary>
    private static void Return(BatchContext context, int ordinal, SqlParameterValue? parameter, int value)
    {
        if (parameter is not { IsOutput: true }) return;
        context.ReturnValues.Add(new ReturnValue(ordinal, parameter.Name, value));
    }

    private static string Text(SqlParameterValue? parameter) => parameter?.Value as string ?? "";

    private static int Number(SqlParameterValue? parameter) =>
        parameter?.Value is null ? 0 : System.Convert.ToInt32(parameter.Value);

    private static int Handle(SqlParameterValue? parameter) =>
        parameter?.Value is null
            ? throw new TdsQueryException(new SqlErrorInfo(UnknownHandleNumber, "Could not find prepared statement with handle 0."))
            : System.Convert.ToInt32(parameter.Value);
}
