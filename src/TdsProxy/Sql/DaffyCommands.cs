using System.Globalization;
using System.Text.RegularExpressions;
using TdsProxy.Query;

namespace TdsProxy.Sql;

/// <summary>The table INSERT ... EXEC daffydb.run_sqlserver names, and the columns it lists, if it lists any.</summary>
internal sealed record InsertTarget(string Table, string? Columns);

/// <summary>
/// One of DaffyDB's own commands. <paramref name="Parameters"/> are in the order positional
/// arguments bind in, and the first <paramref name="Required"/> of them must be given.
/// </summary>
internal sealed record DaffyCommand(
    string Name,
    string[] Parameters,
    int Required,
    bool Admin,
    string Summary,
    string[] Details,
    string[] Examples)
{
    public string Usage => $"EXEC {DaffyCommands.Schema}.{Name}" + string.Join("", Parameters.Select((p, i) =>
        i < Required ? $"{(i == 0 ? " " : ", ")}@{p}" : $" [{(i == 0 ? "" : ", ")}@{p}]"));
}

/// <summary>
/// The commands DaffyDB answers itself, named in the daffydb schema: EXEC daffydb.status,
/// EXEC daffydb.mount 'D:\exports'. This is the shape DaffyDuo's daffy.* and DaffyTee's daffytee.*
/// commands have - a stored procedure call, so valid T-SQL that any client sends unchanged, with
/// arguments by position or by name - and the shape of their errors: severity 16, a number of
/// DaffyDB's own, and a message that starts with the product's name.
///
/// They never reach the translator. help, status and tables only read, and are open to everyone;
/// the rest add to or take away from what is served, or write files, or run T-SQL elsewhere, and are
/// open to clients only when the server was started with --allow-admin. Everything they mount is
/// read-only; the save_parquet commands write new files and never replace one; run_sqlserver does
/// whatever its T-SQL does, as far as a mount's login may.
/// </summary>
internal static class DaffyCommands
{
    public const string Schema = "daffydb";

    /// <summary>An admin command, from a client, on a server started without --allow-admin.</summary>
    public const int AdminDisabledNumber = 50301;

    /// <summary>No such command, or arguments it does not take; the message shows how to call it.</summary>
    public const int BadCallNumber = 50302;

    /// <summary>The file, folder or database could not be opened or read.</summary>
    public const int UnreachableNumber = 50303;

    /// <summary>A name the mount would use is already taken, so nothing was mounted.</summary>
    public const int NameTakenNumber = 50304;

    /// <summary>A DuckDB extension the command needs could not be downloaded.</summary>
    public const int ExtensionNumber = 50305;

    /// <summary>No such mount, or no such table in the source.</summary>
    public const int NotFoundNumber = 50306;

    /// <summary>A file a save would write is already there, or two tables would share one, so nothing was written.</summary>
    public const int FileTakenNumber = 50307;

    /// <summary>The folder or file a save writes could not be made.</summary>
    public const int UnwritableNumber = 50308;

    /// <summary>T-SQL ran, but what it returned could not go in the temp table or table it was asked to.</summary>
    public const int NotKeptNumber = 50309;

    public static readonly IReadOnlyList<DaffyCommand> All =
    [
        new("help", ["command"], 0, Admin: false,
            "what you can run, with examples",
            ["Without an argument, every command. With one, that command in full."],
            ["EXEC daffydb.help", "EXEC daffydb.help 'mount'"]),

        new("status", [], 0, Admin: false,
            "the server: version, how it was started, who is connected, what is mounted",
            ["One row per fact. Passwords are never shown."],
            ["EXEC daffydb.status"]),

        new("tables", [], 0, Admin: false,
            "every table and view, and where each one comes from",
            ["source is what kind of thing a table reads, location is where it is, and mount is the",
             "name daffydb.unmount takes. Both are NULL for a view made by --init, sp_duckdb or a",
             "client's own CREATE VIEW. Never a password."],
            ["EXEC daffydb.tables"]),

        new("mount", ["path", "schema"], 1, Admin: true,
            "add a folder of data files, or one file, to what is served",
            ["A folder is read as --data-dir reads one: every .parquet, .csv, .json and .xlsx file",
             "two folders deep, named by the same rules, a folder of parts as one table. A file",
             "becomes one table, or a workbook a schema of sheets. The path is on the machine",
             "DaffyDB runs on.",
             "@schema puts every table in that schema instead of the one its name gives it.",
             "The mount is named after its folder or file, or after @schema when one is given;",
             "daffydb.tables shows the name, and daffydb.unmount takes it.",
             "Mounting the same folder or file into the same schema again reads it again: new files",
             "appear, and files that are gone go. If a table it would make has a name already in",
             "use, nothing is mounted and the error says which."],
            [@"EXEC daffydb.mount 'D:\exports\2025'",
             @"EXEC daffydb.mount 'D:\exports\orders.parquet', 'staging'"]),

        new("postgres", ["connection", "table", "schema"], 1, Admin: true,
            "every table and view of a PostgreSQL database, or just @table, read live",
            ["@connection is libpq's: host=... port=... dbname=... user=... password=..., or a",
             "postgresql:// URL. dbname is required. Leave the password out and PGPASSWORD or",
             "pgpass.conf on this machine is used instead. The password is kept in a DuckDB",
             "secret, in memory, and never shown or logged.",
             "Tables keep their PostgreSQL schema, and public becomes dbo; @schema puts them all in",
             "that one schema instead. The mount is named after the database, or after @schema.",
             "Without @table every table is mounted, replacing what that mount held; @table names",
             "one, as orders or sales.orders, and adds it. Nothing is copied: every query reads",
             "PostgreSQL as it is now, and nothing is ever written to it.",
             "Needs DuckDB's postgres extension, which downloads once, on first use."],
            ["EXEC daffydb.postgres 'host=localhost port=5432 dbname=shop user=reader password=...'",
             "EXEC daffydb.postgres 'host=localhost dbname=shop user=reader', 'sales.orders'",
             "EXEC daffydb.postgres 'postgresql://reader@localhost/shop', @schema = 'pg'"]),

        new("mysql", ["connection", "table", "schema"], 1, Admin: true,
            "every table of a MySQL or MariaDB database, or just @table, read live",
            ["@connection is host=... port=... database=... user=... password=..., or a mysql://",
             "URL. database is required. Leave the password out and MYSQL_PWD on this machine is",
             "used instead. The password is kept in a DuckDB secret, in memory, and never shown or",
             "logged.",
             "Tables go in dbo, or in @schema. The mount is named after the database, or after",
             "@schema. Without @table every table is mounted, replacing what that mount held; @table",
             "names one and adds it. Nothing is copied, and nothing is ever written.",
             "Needs DuckDB's mysql extension, which downloads once, on first use."],
            ["EXEC daffydb.mysql 'host=localhost port=3306 database=shop user=reader password=...'",
             "EXEC daffydb.mysql 'host=localhost database=shop user=reader', 'orders'"]),

        new("sqlserver", ["connection", "table", "schema"], 1, Admin: true,
            "every table and view of a SQL Server or Azure SQL database, or just @table, read live",
            ["@connection is a SQL Server connection string: Server=host[,port];Database=...;",
             "User Id=...;Password=... . Database is required. Encrypt is Mandatory unless it says",
             "Optional or Strict, and the server's certificate is checked unless TrustServerCertificate",
             "is True. Authentication=ActiveDirectoryServicePrincipal logs in as an Entra ID",
             "application: its client id as User Id, a client secret as Password. Lock Timeout is how",
             "many seconds a read waits behind another session's locks before it fails: 30 unless",
             "given, 0 for no limit. The password is kept in memory, outside DuckDB, and never shown",
             "or logged.",
             "Tables keep their SQL Server schema; @schema puts them all in that one schema instead.",
             "The mount is named after the database, or after @schema. Without @table every table and",
             "view is mounted, replacing what that mount held; @table names one, as orders or",
             "sales.orders, and adds it. Nothing is copied: every query reads SQL Server as it is now,",
             "asking it for only the columns it uses and sending it the conditions it can apply as",
             "DaffyDB would, so only the rows they keep cross the network. Mount with a login that",
             "can only read.",
             "Needs nothing downloaded: DaffyDB speaks SQL Server's protocol itself."],
            ["EXEC daffydb.sqlserver 'Server=tcp:sql01;Database=Sales;User Id=reader;Password=...'",
             "EXEC daffydb.sqlserver 'Server=sql01,1433;Database=Sales;User Id=reader;Password=...', 'dbo.Orders'",
             "EXEC daffydb.sqlserver 'Server=sql01;Database=Sales;User Id=reader;Password=...;Lock Timeout=120', @schema = 'sales'"]),

        new("run_sqlserver", ["mount", "sql", "into"], 2, Admin: true,
            "run T-SQL on a mounted SQL Server, a stored procedure among them, and return what it returns",
            ["@mount is a SQL Server mount's name, as daffydb.tables shows it. @sql is T-SQL, SQL Server's",
             "own, sent as it is: one batch, so no GO. Every result set comes back, PRINT output comes back",
             "as messages, and an error SQL Server raises comes back with its own number, after whatever",
             "came before it.",
             "It runs as the mount's login, and can do what that login can, which may be to change data. A",
             "login that can only read needs GRANT EXECUTE on the procedures it should run.",
             "Each call is its own batch on its own session: a temp table, a USE or a SET lasts only as long",
             "as it does, and a transaction left open is rolled back. It waits as long as SQL Server takes,",
             "with no Lock Timeout; DaffyDB goes on answering other clients meanwhile, and a cancel reaches",
             "SQL Server at once.",
             "@into puts the first result set in a temp table of that name, made from its columns and",
             "replacing any there, and returns the rest. INSERT #t EXEC daffydb.run_sqlserver ... puts every",
             "result set in a table you made yourself instead. Rows that came before an error are kept, and",
             "rows that cannot be kept come back with the error, since the T-SQL has run."],
            ["EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders @min = 2, @name = N''Ann'''",
             "EXEC daffydb.run_sqlserver 'shop', N'SELECT TOP (5) * FROM dbo.orders; PRINT ''done'''",
             "EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders', @into = '#orders'",
             "INSERT #orders EXEC daffydb.run_sqlserver 'shop', N'EXEC dbo.GetOrders'"]),

        new("sqlite", ["path", "table", "schema"], 1, Admin: true,
            "every table and view of a SQLite database file, or just @table",
            ["Tables go in dbo, or in @schema. The mount is named after the file, or after @schema.",
             "Without @table every table is mounted, replacing what that mount held; @table names",
             "one and adds it. The file is opened read-only, and read live.",
             "Needs DuckDB's sqlite extension, which downloads once, on first use."],
            [@"EXEC daffydb.sqlite 'D:\data\chinook.db'",
             @"EXEC daffydb.sqlite 'D:\data\chinook.db', 'invoices', 'music'"]),

        new("duckdb", ["path", "table", "schema"], 1, Admin: true,
            "every table and view of a DuckDB database file, or just @table",
            ["Tables keep their schema, and main becomes dbo; @schema puts them all in that one",
             "schema instead. The mount is named after the file, or after @schema. Without @table",
             "every table is mounted, replacing what that mount held; @table names one and adds it.",
             "Mount as many files as you like, each with its own command. A file is opened",
             "read-only, and while it is mounted no other program can open it to write;",
             "daffydb.unmount lets it go.",
             "Needs nothing downloaded: DuckDB reads its own files."],
            [@"EXEC daffydb.duckdb 'D:\data\warehouse.duckdb'",
             @"EXEC daffydb.duckdb 'D:\data\2024.duckdb', @schema = 'y2024'"]),

        new("unmount", ["mount"], 1, Admin: true,
            "take a mount's tables away again, and let go of its file or database",
            ["@mount is a name from the mount column of daffydb.tables: a folder's or a file's name,",
             "or the @schema it was mounted into. It is never a schema's name as such, so the tables",
             "of other mounts in the same schema stay. The folder --data-dir named is a mount too,",
             "named after its folder."],
            ["EXEC daffydb.unmount 'shop'"]),

        new("save_parquet", ["table", "folder", "file"], 2, Admin: true,
            "write one table, view or temp table to a parquet file",
            ["@table is orders, sales.orders, or a temp table such as #orders. The file is named",
             "schema.table.parquet, as sales.orders.parquet, so mounting the folder gives the table",
             "its name back; a temp table's is its name without the #, as orders.parquet. @file",
             "names the file instead: a name, not a path, with .parquet added if it is left off.",
             "@folder is on the machine DaffyDB runs on, and is made if it is not there. A file",
             "already there is never replaced: the command fails, and nothing is written.",
             "DuckDB's own parquet writer writes it, from the table as it is now."],
            [@"EXEC daffydb.save_parquet 'sales.orders', 'D:\exports'",
             @"EXEC daffydb.save_parquet '#totals', 'D:\exports', 'totals_2025'"]),

        new("save_parquet_all", ["folder", "mount"], 1, Admin: true,
            "write every table, view and temp table to parquet files, one each",
            ["Every table and view daffydb.tables lists, and every temp table, each to a file in",
             "@folder named as daffydb.save_parquet names it. @mount saves just that mount's tables.",
             "Every file name is checked first: if one is already there, or two tables would share",
             "one, nothing is written and the error says which. A table that cannot be read is left",
             "out and said so, and the rest are saved. Other clients' queries run between one table",
             "and the next. Temp tables here are shared by every session, so all of them are saved."],
            [@"EXEC daffydb.save_parquet_all 'D:\exports\2025-10-20'",
             @"EXEC daffydb.save_parquet_all 'D:\exports\shop', @mount = 'shop'"]),
    ];

    public static DaffyCommand? Find(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name.Trim().Trim('[', ']', '"'), StringComparison.OrdinalIgnoreCase));

    private static readonly Regex NamePattern = new(
        @"^\s*(?:\[daffydb\]|""daffydb""|daffydb)\s*\.\s*(?:\[(?<verb>[^\]]+)\]|""(?<verb>[^""]+)""|(?<verb>\w+))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingName = new(
        @"^(?:\[daffydb\]|""daffydb""|daffydb)\s*\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a procedure name is one of these commands - daffydb.status, [daffydb].[status] - and
    /// which. Only the two-part name counts: daffydb.dbo.x is a procedure in the database daffydb.
    /// </summary>
    public static bool IsCommand(string procedureName, out string verb)
    {
        var match = NamePattern.Match(procedureName);
        verb = match.Success ? match.Groups["verb"].Value : "";
        return match.Success;
    }

    /// <summary>
    /// EXEC daffydb.verb args, or the same without the EXEC, which T-SQL allows for a procedure
    /// call that opens a batch and which SSMS users type out of habit.
    /// </summary>
    public static bool TryParse(string statement, out ProcedureCall call)
    {
        string text = statement.Trim();
        if (LeadingName.IsMatch(text)) text = "EXEC " + text;

        return SystemProcedures.TryParseCall(text, out call) && IsCommand(call.Name, out _);
    }

    private static readonly Regex InsertRunPattern = new(
        @"^INSERT\s+(?:INTO\s+)?(?<target>[^\s(]+)\s*(?:\((?<columns>[^)]*)\)\s*)?EXEC(?:UTE)?\s+(?<call>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// INSERT #t [(columns)] EXEC daffydb.run_sqlserver ...: the T-SQL way to keep a procedure's rows.
    /// No other command has rows worth keeping this way, so only this one is read as this.
    /// </summary>
    public static bool TryParseInsertRun(string statement, out InsertTarget target, out ProcedureCall call)
    {
        target = new InsertTarget("", null);
        call = new ProcedureCall("", []);

        var match = InsertRunPattern.Match(statement.Trim());
        if (!match.Success) return false;
        if (!TryParse("EXEC " + match.Groups["call"].Value, out call)) return false;
        if (!IsCommand(call.Name, out string verb) || !verb.Equals("run_sqlserver", StringComparison.OrdinalIgnoreCase)) return false;

        string columns = match.Groups["columns"].Value.Trim();
        target = new InsertTarget(match.Groups["target"].Value, columns.Length == 0 ? null : columns);
        return true;
    }

    /// <summary>
    /// Matches arguments to parameters as SQL Server does - by position, then by name - and
    /// checks that the required ones are there. Every parameter is text; a number given for one
    /// is taken as its digits.
    /// </summary>
    public static Dictionary<string, string?> Bind(DaffyCommand command, IEnumerable<(string? Name, object? Value)> arguments)
    {
        var bound = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        foreach (var (rawName, value) in arguments)
        {
            string? name = string.IsNullOrEmpty(rawName) ? null : rawName.TrimStart('@');

            if (name is null)
            {
                if (position >= command.Parameters.Length)
                    throw BadCall(command, $"{command.Name} takes {Count(command.Parameters.Length)}.");
                name = command.Parameters[position++];
            }
            else if (!command.Parameters.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw BadCall(command, $"@{name} is not a parameter of {command.Name}.");
            }

            if (ReferenceEquals(value, SystemProcedures.DefaultValue)) continue;   // DEFAULT: as if left out

            bound[name] = value switch
            {
                null => null,
                string text => text,
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }

        foreach (string name in command.Parameters.Take(command.Required))
        {
            if (string.IsNullOrWhiteSpace(bound.GetValueOrDefault(name)))
                throw BadCall(command, $"{command.Name} needs @{name}.");
        }

        // Left out, or given as '' - either way, not given.
        foreach (string name in command.Parameters)
        {
            if (string.IsNullOrWhiteSpace(bound.GetValueOrDefault(name))) bound[name] = null;
            else bound[name] = bound[name]!.Trim();
        }

        return bound;
    }

    private static string Count(int n) => n switch
    {
        0 => "no arguments",
        1 => "one argument",
        _ => $"{n} arguments at most",
    };

    public static TdsQueryException Error(int number, string message) =>
        new(new SqlErrorInfo(number, "DaffyDB: " + message));

    public static TdsQueryException BadCall(DaffyCommand command, string problem) =>
        Error(BadCallNumber, $"{problem} Usage: {command.Usage}");

    public static TdsQueryException Unknown(string name) =>
        Error(BadCallNumber, $"there is no command {Schema}.{name}. The commands are " +
                             string.Join(", ", All.Select(c => c.Name)) + "; EXEC daffydb.help describes them.");
}
