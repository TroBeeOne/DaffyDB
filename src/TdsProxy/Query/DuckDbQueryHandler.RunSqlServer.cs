using System.Text.RegularExpressions;
using DaffySQL;
using DuckDB.NET.Data;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// daffydb.run_sqlserver: T-SQL run on a mounted SQL Server, through DaffySQL and with no DuckDB in
/// the way, and all of what it returned sent on to the client. It is a command, and not DaffySQL's
/// daffy_query table function, for three reasons:
///
/// - Clients' SQL reaches every table function DuckDB has, so a registered function would be open to
///   every client, as the mount's login. A command is open only under --allow-admin.
/// - daffy_query runs its T-SQL when DuckDB binds the query, so a driver preparing a statement
///   would run a procedure once to learn its columns and again for its rows. Describing a statement
///   passes over a command, so a command runs once.
/// - DuckDB takes one table from a function. A batch may return several, or none, and PRINT
///   output, and an error after its rows.
///
/// SQL Server may take as long as the T-SQL takes, and DuckDB is not needed for any of it, so other
/// sessions have the connection meanwhile.
///
/// What it returns can be kept, to JOIN or save later: @into makes a temp table from a result's own
/// columns, and INSERT #t EXEC daffydb.run_sqlserver ... adds to a table the client made. Where a
/// keep cannot work at all, that is found before the T-SQL runs. Once it has run, a failure says so, and the rows go to the client.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    private static readonly Regex TempTableName = new(@"^#{1,2}\w+$", RegexOptions.CultureInvariant);

    /// <summary>The table a batch's rows pass through on the way into INSERT ... EXEC's, within its transaction.</summary>
    private const string InsertLoad = "#daffydb_insert_load";

    private void RunOnSqlServer(string name, string sql, string? into, InsertTarget? insert, BatchContext context)
    {
        var usage = DaffyCommands.Find("run_sqlserver")!;
        var mount = NamedMount(name);
        if (mount.Kind != "sqlserver")
            throw DaffyCommands.BadCall(usage, $"'{mount.Name}' is a {mount.Kind} mount, and T-SQL runs only on a SQL Server's.");

        if (into is not null && insert is not null)
            throw DaffyCommands.BadCall(usage, "@into and INSERT ... EXEC both say where the rows go; use one.");
        if (into is not null && !TempTableName.IsMatch(into))
            throw DaffyCommands.BadCall(usage, $"@into names a temp table, as #orders, and '{into}' is not one.");
        if (insert is not null) CheckInsertTarget(insert, context);

        string catalog = mount.Catalog!;
        string location = mount.Location;

        SqlServerAnswer answer;
        try
        {
            answer = WithoutTheConnection(token => DaffySql.Run(catalog, sql, token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"{location} could not finish the T-SQL, which may or may not have run: {FirstLine(ex.Message)}");
        }

        StopIfCancelled();

        Chatter.Line($"[sqlserver] ran on {location}: {Plural(answer.ResultSets.Count, "result set")}, " +
                     $"{Plural(answer.RowsAffected, "row")} changed, {Plural(answer.Errors.Count, "error")}");

        context.Messages.AddRange(answer.Messages.Select(m => m.Text));
        if (answer.RowsAffected > 0) context.Messages.Add($"({Plural(answer.RowsAffected, "row")} affected)");

        var raised = answer.Errors
            .Select(e => new SqlErrorInfo(e.Number, Redact.Text(e.Text), e.Severity, e.State, e.Line, e.Procedure))
            .ToList();
        var sets = answer.ResultSets.Select(FromSqlServer).ToList();

        try
        {
            if (into is not null) sets = KeepInto(into, sets, nothingRaised: raised.Count == 0, context);
            else if (insert is not null) { KeepInserted(insert, sets, context); sets = []; }
        }
        catch (TdsQueryException ex)
        {
            // The T-SQL ran, and may not be safe to run again, so rows that could not be kept go to
            // the client rather than be lost, and its own errors are still to be told.
            raised.Insert(0, ex.Error);
        }

        context.ResultSets.AddRange(sets);
        if (raised.Count > 0) throw new TdsQueryException(raised[0]) { Raised = raised };
    }

    /// <summary>
    /// @into: the first result set becomes the temp table, replacing any of that name, as CREATE
    /// TABLE #t does here. What it returned after that goes to the client, and is what this returns.
    /// </summary>
    private List<ResultSet> KeepInto(string into, List<ResultSet> sets, bool nothingRaised, BatchContext context)
    {
        if (sets.Count == 0)
        {
            if (!nothingRaised) return sets;
            throw DaffyCommands.Error(DaffyCommands.NotKeptNumber, $"the T-SQL ran and returned no result set, so {into} was not made.");
        }

        var first = sets[0];
        InTransaction($"{into} could not hold what the T-SQL returned, though the T-SQL did run", () =>
        {
            Execute($"CREATE OR REPLACE TEMP TABLE {Quote(into)} ({ColumnsOf(first)})");
            Load(into, first);
        });

        context.Messages.Add($"Put {Plural(first.Rows.Count, "row")} in {into}.");
        return sets.Skip(1).ToList();
    }

    /// <summary>
    /// Refuses, before the T-SQL has run for nothing, a table that is not there, a column it does
    /// not have, or something that cannot be written to, such as a mounted view. Nothing is inserted:
    /// DuckDB binds this statement, and no row can pass the WHERE.
    /// </summary>
    private void CheckInsertTarget(InsertTarget insert, BatchContext context)
    {
        string list = insert.Columns ?? "*";
        string named = insert.Columns is null ? "" : $" ({insert.Columns})";
        Execute(TSqlTranslator.Translate(
            $"INSERT INTO {insert.Table}{named} SELECT {list} FROM {insert.Table} WHERE 1 = 0", context.Error, context.Session).Sql);
    }

    /// <summary>
    /// INSERT ... EXEC: every result set goes in the table, all of them or none, as on SQL Server. A
    /// value that does not fit fails the whole insert, where an INSERT ... SELECT here gives it NULL:
    /// a client's SELECT can be read again and corrected, and a procedure may not be safe to run again.
    /// </summary>
    private void KeepInserted(InsertTarget insert, List<ResultSet> sets, BatchContext context)
    {
        string named = insert.Columns is null ? "" : $" ({insert.Columns})";
        string into = TSqlTranslator.Translate(
            $"INSERT INTO {insert.Table}{named} SELECT * FROM {InsertLoad}", context.Error, context.Session).Sql;

        InTransaction($"{insert.Table} could not take what the T-SQL returned, though the T-SQL did run", () =>
        {
            foreach (var set in sets)
            {
                Execute($"CREATE OR REPLACE TEMP TABLE {Quote(InsertLoad)} ({ColumnsOf(set)})");
                Load(InsertLoad, set);
                Execute(into);
                Execute($"DROP TABLE {Quote(InsertLoad)}");
            }
        });

        long rows = sets.Sum(set => (long)set.Rows.Count);
        context.Messages.Add($"({Plural(rows, "row")} affected)");
    }

    private void InTransaction(string failure, Action work)
    {
        Execute("BEGIN TRANSACTION");
        try
        {
            work();
            Execute("COMMIT");
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the transaction ends here: the connection is every session's.
            try { Execute("ROLLBACK"); } catch (DuckDBException) { }
            throw DaffyCommands.Error(DaffyCommands.NotKeptNumber, $"{failure}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// A table's columns for a result: what a mounted table's would be, under names a table can have.
    /// As DaffySQL names them for DuckDB, a column with no name is column0, column1... by position,
    /// and a repeated name gets _1, _2.
    /// </summary>
    private static string ColumnsOf(ResultSet set)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>(set.Columns.Count);
        for (int i = 0; i < set.Columns.Count; i++)
        {
            string name = set.Columns[i].Name.Length > 0 ? set.Columns[i].Name : $"column{i}";
            string unique = name;
            for (int n = 1; !taken.Add(unique); n++) unique = $"{name}_{n}";
            columns.Add($"{Quote(unique)} {DuckDbType(set.Columns[i])}");
        }
        return string.Join(", ", columns);
    }

    private static string DuckDbType(ColumnDef column) => column.Type switch
    {
        SqlColumnType.TinyInt => "UTINYINT",
        SqlColumnType.SmallInt => "SMALLINT",
        SqlColumnType.Int => "INTEGER",
        SqlColumnType.BigInt => "BIGINT",
        SqlColumnType.Bit => "BOOLEAN",
        SqlColumnType.Real => "FLOAT",
        SqlColumnType.Double => "DOUBLE",
        SqlColumnType.Decimal => $"DECIMAL({column.Precision},{column.Scale})",
        SqlColumnType.Date => "DATE",
        SqlColumnType.Time => "TIME",
        SqlColumnType.DateTime or SqlColumnType.DateTime2 => "TIMESTAMP",
        SqlColumnType.UniqueIdentifier => "UUID",
        SqlColumnType.VarBinary or SqlColumnType.Binary => "BLOB",
        _ => "VARCHAR",
    };

    /// <summary>A result's rows into a temp table made from it, by DuckDB's appender: a procedure can return a lot of them.</summary>
    private void Load(string table, ResultSet set)
    {
        using var appender = _connection.CreateAppender("temp", "main", table);
        foreach (var values in set.Rows)
        {
            var row = appender.CreateRow();
            for (int i = 0; i < values.Length; i++)
            {
                switch (values[i])
                {
                    case null: row.AppendNullValue(); break;
                    case bool v: row.AppendValue(v); break;
                    case byte v: row.AppendValue(v); break;
                    case short v: row.AppendValue(v); break;
                    case int v: row.AppendValue(v); break;
                    case long v: row.AppendValue(v); break;
                    case float v: row.AppendValue(v); break;
                    case double v: row.AppendValue(v); break;
                    case decimal v: row.AppendValue(v); break;
                    case Guid v: row.AppendValue(v); break;
                    case string v: row.AppendValue(v); break;
                    case byte[] v: row.AppendValue(v); break;
                    case TimeSpan v: row.AppendValue(TimeOnly.FromTimeSpan(v)); break;
                    case DateTime v when set.Columns[i].Type == SqlColumnType.Date: row.AppendValue(DateOnly.FromDateTime(v)); break;
                    case DateTime v: row.AppendValue(v); break;
                    default: throw new InvalidOperationException($"no way to keep a {values[i]!.GetType().Name}");
                }
            }
            row.EndRow();
        }
    }

    /// <summary>
    /// A result as DaffyDB sends it. Text is always nvarchar, since a varchar's code page need not
    /// be the one DaffyDB declares, and a datetimeoffset is datetime2 in UTC, as a mounted one is.
    /// A decimal too wide for .NET's arrives as exact text, and goes on as text rather than be rounded.
    /// </summary>
    private static ResultSet FromSqlServer(SqlServerResultSet set)
    {
        var columns = set.Columns.Select(FromSqlServer).ToList();
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Type == SqlColumnType.Decimal && set.Rows.Any(row => row[i] is string))
                columns[i] = new ColumnDef(columns[i].Name, SqlColumnType.NVarChar, columns[i].Nullable);
        }

        var rows = new List<object?[]>(set.Rows.Count);
        foreach (var row in set.Rows)
        {
            var values = new object?[row.Length];
            for (int i = 0; i < row.Length; i++)
            {
                values[i] = row[i] switch
                {
                    null => null,
                    DateTimeOffset at => at.UtcDateTime,
                    var value => Convert(columns[i].Type, value),
                };
            }
            rows.Add(values);
        }

        return new ResultSet(columns, rows);
    }

    private static ColumnDef FromSqlServer(SqlServerColumn column)
    {
        ColumnDef As(SqlColumnType type) => new(column.Name, type, column.Nullable);
        ColumnDef AsDecimal(byte precision, byte scale) => new(column.Name, SqlColumnType.Decimal, column.Nullable, precision, scale);

        return column.TypeName switch
        {
            "tinyint" => As(SqlColumnType.TinyInt),
            "smallint" => As(SqlColumnType.SmallInt),
            "int" => As(SqlColumnType.Int),
            "bigint" => As(SqlColumnType.BigInt),
            "bit" => As(SqlColumnType.Bit),
            "real" => As(SqlColumnType.Real),
            "float" => As(SqlColumnType.Double),
            "decimal" or "numeric" => AsDecimal(Math.Max(column.Precision, (byte)1), column.Scale),
            "money" => AsDecimal(19, 4),
            "smallmoney" => AsDecimal(10, 4),
            "date" => As(SqlColumnType.Date),
            "time" => As(SqlColumnType.Time),
            "datetime" or "smalldatetime" => As(SqlColumnType.DateTime),
            "datetime2" or "datetimeoffset" => As(SqlColumnType.DateTime2),
            "uniqueidentifier" => As(SqlColumnType.UniqueIdentifier),
            "binary" or "varbinary" or "image" or "rowversion" => As(SqlColumnType.VarBinary),
            _ => As(SqlColumnType.NVarChar),   // char, varchar, nchar, nvarchar, text, ntext, xml, sql_variant
        };
    }
}
