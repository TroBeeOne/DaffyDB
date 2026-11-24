using System.Diagnostics;
using System.Text;
using TdsProxy.Query;
using TdsProxy.Sql;

namespace TdsProxy.Repl;

/// <summary>
/// A prompt attached to the running server, for trying T-SQL against the mounted files without
/// reaching for another client.
///
/// It goes through <see cref="IQueryHandler"/> in process rather than opening a loopback TDS
/// connection to itself. That is the seam this whole project is built on: the handler hands back
/// columns and rows ready to print, the same semaphore that serialises client sessions serialises
/// this one, and the same log lines appear. The trade is that a query typed here does not exercise
/// the TDS encoder, so the probes remain the thing that proves the wire format.
/// </summary>
internal sealed class ReplLoop(
    IQueryHandler handler,
    LogBuffer log,
    TextWriter output,
    TextReader input,
    CancellationTokenSource shutdown,
    bool interactive,
    Action<TextReader, TextWriter>? question = null,
    Action<TextReader, TextWriter>? powerBi = null)
{
    /// <summary>
    /// Identifies this caller in the log, so a REPL statement is distinguishable from a client's.
    /// It starts where a client naming no database does, and a USE at the prompt moves it.
    /// </summary>
    private SqlSession _session = new(
        SessionId: 0, UserName: "repl", Password: null, Database: Databases.Data,
        HostName: Environment.MachineName, ApplicationName: "daffydb repl",
        ClientAddress: "in-process", IsEncrypted: false) { IsConsole = true };

    private readonly ResultPrinter _printer = new(output);

    /// <summary>Zero means fit the window, which is the default and is re-read on every query.</summary>
    private int _rowsPerPage;

    public async Task RunAsync()
    {
        WriteWelcome();

        // Something to settle before the first command, such as whether to trust the certificate.
        // Only asked of someone typing: piped input is a script, and would answer with its first line.
        if (interactive) question?.Invoke(input, output);

        var statement = new StringBuilder();

        while (!shutdown.IsCancellationRequested)
        {
            output.Write(statement.Length == 0 ? "daffydb> " : "      -> ");
            output.Flush();

            string? line = input.ReadLine();

            // Null is end of input: a closed pipe, or Ctrl-C landing while ReadLine was blocked.
            if (line is null) break;
            if (shutdown.IsCancellationRequested) break;

            // A piped script usually arrives with a byte order mark on its first line - PowerShell
            // writes one by default - and U+FEFF is not whitespace, so Trim leaves it in place and
            // ":tables" stops looking like a command. Drop it before anything else looks at the line.
            line = line.TrimStart('\uFEFF');

            if (statement.Length == 0)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.StartsWith(':'))
                {
                    if (await RunCommandAsync(trimmed)) break;
                    continue;
                }
            }

            statement.AppendLine(line);

            // A trailing semicolon is the usual signal; a blank line rescues anyone who forgot it
            // rather than leaving them stuck at a continuation prompt with no way out.
            string sql = statement.ToString().Trim();
            if (!sql.EndsWith(';') && line.Trim().Length != 0) continue;

            statement.Clear();
            if (sql.Length > 0) await RunSqlAsync(sql);
        }
    }

    private void WriteWelcome()
    {
        output.WriteLine();
        output.WriteLine("  Type T-SQL and end it with ;   :help for commands, :quit to stop the server.");
        output.WriteLine();
    }

    /// <summary>Returns true when the loop should end.</summary>
    private async Task<bool> RunCommandAsync(string line)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case ":quit" or ":q" or ":exit":
                output.WriteLine("  stopping the server.");
                shutdown.Cancel();
                return true;

            case ":help" or ":?":
                WriteHelp();
                return false;

            case ":tables":
                await ShowTablesAsync();
                return false;

            case ":columns":
                if (argument.Length == 0) output.WriteLine("  usage: :columns TABLE   (or schema.table)");
                else await ShowColumnsAsync(argument);
                return false;

            case ":log":
                ShowLog(argument);
                return false;

            case ":wide":
                _printer.Wide = !_printer.Wide;
                output.WriteLine($"  wide mode {(_printer.Wide ? "on - one row per record" : "off - grid")}");
                return false;

            case ":rows":
                SetRows(argument);
                return false;

            case ":pbi":
                if (powerBi is null) output.WriteLine("  Power BI Desktop cannot be opened from here");
                else powerBi(input, output);
                return false;

            default:
                output.WriteLine($"  unknown command '{command}' - :help for the list");
                return false;
        }
    }

    private void WriteHelp()
    {
        output.WriteLine("""

              :tables            the mounted tables and views, with a sample query for each
              :columns NAME      columns and types for one table or view
              :log [n|follow]    the last n log lines (default 20), or stream them until a key
              :wide              one row per record, for tables too wide for a grid
              :rows N            rows per page; 0 fits the window
              :pbi               open Power BI Desktop, connected to this server (Windows)
              :help              this
              :quit              stop the server and exit

              T-SQL runs when a line ends with ; or on a blank line.
              While paging: [space] for the next page, [q] to stop.

            """);
    }

    private void SetRows(string argument)
    {
        if (!int.TryParse(argument, out int rows) || rows < 0)
        {
            output.WriteLine("  usage: :rows N   (0 fits the window)");
            return;
        }

        _rowsPerPage = rows;
        output.WriteLine(rows == 0 ? "  rows per page: fit the window" : $"  rows per page: {rows}");
    }

    private void ShowLog(string argument)
    {
        if (argument.Equals("follow", StringComparison.OrdinalIgnoreCase))
        {
            FollowLog();
            return;
        }

        if (argument.Length > 0 && !int.TryParse(argument, out _))
        {
            output.WriteLine("  usage: :log [n|follow]");
            return;
        }

        int count = argument.Length == 0 ? 20 : int.Parse(argument);
        var lines = log.Tail(count);

        if (lines.Count == 0)
        {
            output.WriteLine("  (log is empty)");
            return;
        }

        output.WriteLine();
        foreach (string line in lines) output.WriteLine("  " + line);
        output.WriteLine($"  {lines.Count} of {log.Count} line(s)");
    }

    private void FollowLog()
    {
        if (!interactive)
        {
            output.WriteLine("  :log follow needs an interactive console");
            return;
        }

        output.WriteLine("  following the log - press any key to stop");
        log.Follower = line => output.WriteLine("  " + line);
        try
        {
            Console.ReadKey(intercept: true);
        }
        finally
        {
            log.Follower = null;
        }
        output.WriteLine("  stopped following");
    }

    private async Task ShowTablesAsync()
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type_desc AS object_type,
                   (SELECT COUNT(*) FROM daffydb.sys.columns c WHERE c.object_id = o.object_id) AS column_count
            FROM daffydb.sys.objects o
            JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id
            ORDER BY s.name, o.name
            """;

        var sets = await ExecuteAsync(sql);
        if (sets is null) return;

        var source = sets.Count > 0 ? sets[0] : ResultSet.Empty;
        if (source.Rows.Count == 0)
        {
            output.WriteLine("  nothing mounted - point --data-dir at a folder of .parquet, .csv, .json or .xlsx files");
            return;
        }

        // Row counts are deliberately absent: free for parquet, a full scan for CSV, and a folder
        // of large CSVs would make the one command you reach for first the slowest one.
        ColumnDef[] columns =
        [
            new("schema", SqlColumnType.NVarChar, Nullable: false),
            new("name", SqlColumnType.NVarChar, Nullable: false),
            new("type", SqlColumnType.NVarChar, Nullable: false),
            new("cols", SqlColumnType.Int, Nullable: false),
            new("sample", SqlColumnType.NVarChar, Nullable: false),
        ];

        var rows = new List<object?[]>();
        foreach (object?[] row in source.Rows)
        {
            string schema = row[0]?.ToString() ?? "";
            string name = row[1]?.ToString() ?? "";
            string kind = (row[2]?.ToString() ?? "") == "USER_TABLE" ? "table" : "view";
            rows.Add([schema, name, kind, row[3], $"SELECT TOP 5 * FROM [{schema}].[{name}];"]);
        }

        ConfigurePrinter();
        _printer.Print([new ResultSet(columns, rows)]);
    }

    private async Task ShowColumnsAsync(string name)
    {
        string schema = "";
        string table = name.Trim().Trim('[', ']');

        int dot = table.IndexOf('.');
        if (dot > 0)
        {
            schema = table[..dot].Trim('[', ']');
            table = table[(dot + 1)..].Trim('[', ']');
        }

        string filter = schema.Length == 0 ? "" : $" AND s.name = '{Escape(schema)}'";
        string sql = $"""
            SELECT s.name AS schema_name, o.name AS object_name, c.name AS column_name,
                   t.name AS data_type, c.is_nullable
            FROM daffydb.sys.columns c
            JOIN daffydb.sys.types t ON t.user_type_id = c.user_type_id
            JOIN daffydb.sys.objects o ON o.object_id = c.object_id
            JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.name = '{Escape(table)}'{filter}
            ORDER BY s.name, o.name, c.column_id
            """;

        var sets = await ExecuteAsync(sql);
        if (sets is null) return;

        var source = sets.Count > 0 ? sets[0] : ResultSet.Empty;
        if (source.Rows.Count == 0)
        {
            output.WriteLine($"  no table or view named '{name}' - :tables for the list");
            return;
        }

        ConfigurePrinter();

        // An unqualified name can match the same table in two schemas. When it resolved to exactly
        // one object the schema and name are repeated down every row for nothing, so they become a
        // heading instead; when it matched several they have to stay, or the list is meaningless.
        var matched = source.Rows
            .Select(r => $"{r[0]}.{r[1]}")
            .Distinct()
            .ToList();

        if (matched.Count > 1)
        {
            _printer.Print([source]);
            return;
        }

        ColumnDef[] columns =
        [
            new("column", SqlColumnType.NVarChar, Nullable: false),
            new("type", SqlColumnType.NVarChar, Nullable: false),
            new("nullable", SqlColumnType.Bit, Nullable: false),
        ];

        var rows = source.Rows.Select(r => new[] { r[2], r[3], r[4] }).ToList();

        output.WriteLine();
        output.WriteLine($"  {matched[0]}");
        _printer.Print([new ResultSet(columns, rows)]);
    }

    private async Task RunSqlAsync(string sql)
    {
        var stopwatch = Stopwatch.StartNew();
        var sets = await ExecuteAsync(sql);
        stopwatch.Stop();

        if (sets is null) return;

        ConfigurePrinter();

        if (sets.Count == 0 && _raised.Count == 0) output.WriteLine("  (no result set)");
        else _printer.Print(sets);

        foreach (var error in _raised) output.WriteLine($"  error: Msg {error.Number}, Level {error.Severity}: {Redact.Text(error.Message)}");

        output.WriteLine($"  {stopwatch.ElapsedMilliseconds} ms");
        output.WriteLine();
    }

    /// <summary>Errors the last batch's results came with: SQL Server's, through daffydb.run_sqlserver.</summary>
    private IReadOnlyList<SqlErrorInfo> _raised = [];

    /// <summary>Runs a batch, reporting any failure the way a client would see it. Null means it failed.</summary>
    private async Task<IReadOnlyList<ResultSet>?> ExecuteAsync(string sql)
    {
        try
        {
            var request = new SqlRequest(RequestKind.Batch, sql, []) { Session = _session };
            var result = await handler.ExecuteAsync(request, shutdown.Token);

            if (result.Database is { } moved)
            {
                _session = _session with { Database = moved };
                output.WriteLine($"  Changed database context to '{moved}'.");
            }

            foreach (string message in result.Messages) output.WriteLine($"  {message}");

            _raised = result.Errors;
            return result.ResultSets;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            output.WriteLine($"  error: {Redact.Text(FirstLine(ex.Message))}");
            output.WriteLine("  :log to see the DuckDB it was translated into");
            output.WriteLine();
            return null;
        }
    }

    /// <summary>
    /// Re-read every time, because a terminal can be resized between one query and the next and
    /// a page sized for the old window is either half empty or scrolls off the top.
    /// </summary>
    private void ConfigurePrinter()
    {
        _printer.PageSize = _rowsPerPage > 0 ? _rowsPerPage : WindowRows();
        _printer.MaxWidth = WindowWidth();
        _printer.Pager = interactive ? WaitForPage : null;
    }

    /// <summary>Only the space bar continues. Anything else stops, as the footer says it will.</summary>
    private static bool WaitForPage() => Console.ReadKey(intercept: true).Key == ConsoleKey.Spacebar;

    private static int WindowRows()
    {
        try
        {
            // Leave room for the header, separator, footer and the next prompt.
            return Math.Max(5, Console.WindowHeight - 8);
        }
        catch (IOException)
        {
            return 20;
        }
    }

    private static int WindowWidth()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth - 1);
        }
        catch (IOException)
        {
            return 120;
        }
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static string FirstLine(string message)
    {
        int newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }
}
