using System.Data.Common;
using System.Globalization;
using System.IO.Enumeration;
using System.Numerics;
using System.Text.RegularExpressions;
using DaffySQL;
using DuckDB.NET.Data;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// Executes the decoded T-SQL against DuckDB, over views mounted on parquet, CSV and JSON files
/// and tables loaded from Excel workbooks.
///
/// Everything above this class is protocol; everything inside it is ordinary data access. The only
/// protocol-adjacent concern is mapping DuckDB's column types onto <see cref="SqlColumnType"/>,
/// since that decides how the response is encoded.
/// </summary>
internal sealed partial class DuckDbQueryHandler : IQueryHandler, IDisposable
{
    private readonly DuckDBConnection _connection;

    // DuckDB connections are not safe for concurrent commands, and an in-memory database is not
    // shared between connections, so sessions take turns on one connection.
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>
    /// The request holding the connection, so a cancel can tell whether the query DuckDB is running
    /// is the one that was cancelled. With every session sharing the connection, interrupting it
    /// blindly could stop another client's query. Guarded by <see cref="_runningLock"/>, which is
    /// also held while the connection is interrupted.
    /// </summary>
    private SqlRequest? _runningRequest;
    private CancellationToken _runningToken;
    private volatile bool _cancelRequested;
    private readonly object _runningLock = new();

    /// <summary>
    /// Whether clients may reach DuckDB directly through sp_duckdb. Off unless asked for: an
    /// init script is chosen by whoever started the process, but sp_duckdb is chosen by whoever
    /// connected, and those are not the same trust.
    /// </summary>
    private readonly bool _allowPassthrough;

    /// <summary>
    /// Whether clients may run the daffydb.* commands that mount and unmount: --allow-admin. Like
    /// sp_duckdb, they are chosen by whoever connected rather than whoever started the process.
    /// DaffyDB's own prompt may run them regardless; see <see cref="SqlSession.IsConsole"/>.
    /// </summary>
    private readonly bool _allowAdmin;

    /// <summary>
    /// The database the shared connection is in. Sessions take turns on one connection but each has
    /// its own current database, so a request switches to its session's first; see
    /// <see cref="UseDatabase"/>.
    /// </summary>
    private string _current = "";

    public DuckDbQueryHandler(string dataDirectory, string? initScript = null, bool allowPassthrough = false,
                              bool allowAdmin = false)
    {
        _allowPassthrough = allowPassthrough;
        _allowAdmin = allowAdmin;
        _dataDirectory = dataDirectory;
        _initScript = initScript;

        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();

        // SQL Server, for daffydb.sqlserver: daffy_scan reads a table, daffy_tables lists them,
        // daffy_columns describes one. Only those, because clients' SQL reaches DuckDB too:
        // daffy_connect would let a client repoint a mount's name at a server of its choosing, and
        // daffy_disconnect break it. Mounts name their servers from here, with DaffySql.Connect.
        DaffySql.Register(_connection, DaffyFunctions.Scan | DaffyFunctions.Tables | DaffyFunctions.Columns);
        DaffySql.Diagnostics = (_, text) => Chatter.Line($"[sqlserver] {text}");

        // SQL Server compares strings case-insensitively by default and DuckDB does not, which is
        // the difference a client notices first: WHERE name = 'paTricia' finds nothing. This makes
        // =, IN, JOIN, DISTINCT, GROUP BY and ORDER BY fold case, matching the CI collation the
        // catalog already reports. LIKE is not affected - DuckDB's LIKE ignores collation, and
        // ILIKE is the insensitive one - so a LIKE pattern is still case-sensitive here.
        Execute("SET GLOBAL default_collation = 'nocase'");

        // In T-SQL an integer divided by an integer is an integer, truncated toward zero: 7 / 2 is
        // 3 and -1 / 2 is 0. DuckDB's / gives a double unless told otherwise. It matters beyond
        // arithmetic: SMO takes @@microsoftversion apart with / and then a bitwise &, which a
        // double cannot feed, and Power BI reads an nvarchar(max) column's length as
        // floor(max_length / 2) - 0 on SQL Server, and -1 here as a double.
        Execute("SET GLOBAL integer_division = true");

        // @@VERSION and xp_msver name the DuckDB build, so it has to be known before the catalog is.
        TSqlTranslator.SetEngineVersion(Scalar("SELECT version()"));

        // A column alias without AS is refused by DuckDB when it is one of DuckDB's keywords, and
        // reports name columns Year, Name and Value all the time. The translator puts the AS in,
        // and the list it checks against is DuckDB's own, so it follows the DuckDB build.
        using (var keywords = _connection.CreateCommand())
        {
            keywords.CommandText = "SELECT keyword_name FROM duckdb_keywords()";
            using var reader = keywords.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            TSqlTranslator.SetKeywords(names);
        }

        // Each database is a catalog of its own name, so the three-part names clients like to use
        // (master.sys.databases, daffydb.dbo.orders) resolve without any rewriting. master holds
        // the system catalog and nothing else, msdb the little SSMS reads there, and the data
        // goes in daffydb. See Databases.
        //
        // The sys views go up before any data. They are live projections over DuckDB's own
        // metadata, so they describe whatever is mounted afterwards — including anything an init
        // script or a later CREATE adds — and a failing init still leaves a describable server.
        foreach (var (database, _) in Databases.All)
        {
            Execute($"ATTACH ':memory:' AS {database}");
            Execute($"USE {database}");
            Execute("CREATE SCHEMA dbo");
            foreach (string view in SystemCatalog.Views(database)) Execute(view);
        }

        // dbo is the current schema, so "FROM orders", "FROM dbo.orders" and "FROM daffydb.dbo.orders"
        // all resolve to one object and the catalog does not report each table twice. Mounting
        // and the init script run here, which is what puts everything they create in daffydb.
        UseDatabase(Databases.Data);

        foreach (string statement in SystemCatalog.Msdb()) Execute(statement);
        Console.WriteLine("[duckdb] sys catalog views created");

        var startup = new ScanRun(dataDirectory);
        int found = MountDirectory(startup);
        RegisterStartupMount(startup);

        if (initScript is not null)
        {
            RunInitScript(initScript);

            // The script is DuckDB's own dialect and may have said USE itself, so where the
            // connection is now is not known; the first request puts it right.
            _current = "";
        }

        if (allowPassthrough) Console.WriteLine("[duckdb] sp_duckdb pass-through is ENABLED for all clients");
        if (allowAdmin) Console.WriteLine("[duckdb] daffydb.* admin commands are ENABLED for all clients");

        if (found == 0 && initScript is null)
        {
            Console.WriteLine($"[duckdb] warning: no .parquet, .csv, .json or .xlsx files under {Path.GetFullPath(dataDirectory)}");
            Console.WriteLine("[duckdb] run 'daffydb --seed' for sample data, or point --data-dir somewhere else.");
        }
    }

    /// <summary>
    /// The DuckDB build underneath, for --version. Deliberately opens its own throwaway connection
    /// rather than reusing a handler's: --version runs before anything is mounted, and its real job
    /// is to prove the native library can be found at all, which is the one thing packaging breaks.
    /// </summary>
    public static string EngineVersion()
    {
        try
        {
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT version()";
            return command.ExecuteScalar()?.ToString() ?? "unknown";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name}: {FirstLine(ex.Message)})";
        }
    }

    /// <summary>
    /// The file extensions a directory mount understands, and the DuckDB function that reads each.
    /// All of them are built into DuckDB itself - JSON and gzip included - so a folder scan never
    /// needs an extension installed.
    ///
    /// The order decides precedence: if a folder holds both <c>orders.parquet</c> and
    /// <c>orders.csv</c> they claim the same name, and the typed one should win. A parquet file
    /// carries its schema; a CSV has to be sniffed; JSON is sniffed too, and is the loosest of the
    /// three. DuckDB reads either JSON layout - one array, or one object per line - from any of
    /// the three names, and sees a .gz for what it is from the name.
    ///
    /// A workbook has no reader here: DuckDB's is an extension, so <see cref="ExcelWorkbook"/>
    /// loads it instead, and it comes last because each of its sheets is a copy, not a view.
    /// </summary>
    private static readonly (string Extension, string? Reader)[] MountableFiles =
    [
        (".parquet", "read_parquet"),
        (".csv", "read_csv_auto"),
        (".csv.gz", "read_csv_auto"),
        (".json", "read_json_auto"),
        (".jsonl", "read_json_auto"),
        (".ndjson", "read_json_auto"),
        (".json.gz", "read_json_auto"),
        (".jsonl.gz", "read_json_auto"),
        (".ndjson.gz", "read_json_auto"),
        (".xlsx", null),
        (".xlsm", null),
    ];

    /// <summary>
    /// How many folders down a scan looks: the data folder itself and two levels below it. That is
    /// deep enough for a folder of parts inside a schema folder (Sales/Orders/part-00000.parquet).
    /// Anything deeper is usually partitioned data (year=2024/month=01/part-0.parquet), which one
    /// glob in an --init script mounts better than a table per folder ever could. It also keeps a
    /// scan started at the wrong level from walking a whole drive.
    /// </summary>
    private const int MaxScanDepth = 2;

    /// <summary>
    /// A folder with more tables than this is almost certainly the wrong folder, so none of it is
    /// mounted and the log says why. Mounting the first 500 would be worse: a server that is quietly
    /// missing tables, chosen by nothing more than the order the names sort in. A folder of parts
    /// counts once, however many parts it was written in.
    /// </summary>
    private const int MaxScanFiles = 500;

    /// <summary>
    /// The names tools give the pieces of a table too big for one file: part-00000.parquet and on
    /// from Spark, Hadoop, pyarrow and sqlcarbon (Spark's with more after the number, as in
    /// part-00000-&lt;uuid&gt;-c000.snappy.parquet, and Hadoop's as part-r-00000), and data_0.parquet
    /// and on from DuckDB's own COPY. A number has to follow the part-, so part-numbers.parquet is a
    /// table of part numbers and not a piece of something.
    /// </summary>
    private static readonly Regex PartFileName = new(
        @"^(?:part-(?:[a-z]-)?\d+(?:[-.].*)?|data_\d+)\.parquet$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The empty file Spark, Hadoop and sqlcarbon write into a folder of parts once the export has
    /// finished. A folder holding it is one table whatever its parts are called.
    /// </summary>
    private const string SuccessMarker = "_SUCCESS";

    /// <summary>
    /// One table's worth of what a scan found: a data file, or a folder of parts read as one table,
    /// in which case Path is the folder and Parts the files in it.
    /// </summary>
    private sealed record ScanEntry(string Path, int Kind, IReadOnlyList<string> Parts)
    {
        public bool IsFolder => Parts.Count > 0;
    }

    /// <summary>
    /// Folders a scan does not go into: build output, which holds copies of whatever a project
    /// ships, and tooling. Dot-folders (.git, .vs, .vscode) are skipped as well. None of this
    /// applies to --data-dir itself - pointed straight at a folder named bin, it is still read.
    /// </summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules",
    };

    /// <summary>
    /// JSON is as much a configuration format as a data one, and these are the configuration files
    /// most likely to sit beside data. Each would otherwise mount as a one-row table. The last is
    /// the lock file Excel keeps beside an open workbook, ~$Budget.xlsx, which is not a workbook
    /// and is only sometimes hidden.
    /// </summary>
    private static readonly string[] SkippedFiles =
    [
        "appsettings.json", "appsettings.*.json", "launchSettings.json", "package.json", "package-lock.json",
        "~$*",
    ];

    /// <summary>
    /// Mounts every data file under a directory as a view, so a folder of exports becomes a
    /// database with no configuration at all.
    ///
    /// The file name decides where it lands, which is what makes an AdventureWorks-style export
    /// work unchanged:
    /// <list type="bullet">
    ///   <item><c>orders.parquet</c> becomes <c>daffydb.dbo.orders</c></item>
    ///   <item><c>Sales.Customer.parquet</c> becomes <c>daffydb.Sales.Customer</c></item>
    ///   <item><c>Sales/Customer.parquet</c> becomes the same thing</item>
    ///   <item><c>orders.csv</c> becomes <c>daffydb.dbo.orders</c>, its types sniffed by DuckDB</item>
    ///   <item><c>orders.csv.gz</c> and <c>orders.json</c> become the same thing again</item>
    ///   <item><c>orders/part-00000.parquet</c>, <c>part-00001.parquet</c> and on become it too:
    ///         a folder of parts is one table, named after the folder</item>
    /// </list>
    /// Schemas are created on demand, and because sys.schemas and sys.objects are views over
    /// DuckDB's metadata, all of it appears in a client's object tree without further work.
    ///
    /// Returns how many data files were found, which is not how many were mounted: a folder of
    /// unreadable files still had files in it, and saying otherwise sends the operator looking in
    /// the wrong place. What was mounted is in <see cref="ScanRun.Objects"/>.
    /// </summary>
    private int MountDirectory(ScanRun run)
    {
        var entries = ScanEntries(run, out int found);
        if (entries is not null) MountEntries(run, entries);
        return found;
    }

    /// <summary>
    /// What a folder holds, one entry per table it will make: each data file, or each folder of
    /// parts. Null when there is nothing to mount - the folder is missing, or holds more than a
    /// scan will mount - with the reason given to <see cref="ScanRun.Note"/>.
    /// </summary>
    private static List<ScanEntry>? ScanEntries(ScanRun run, out int found)
    {
        found = 0;
        string dataDirectory = run.Root;

        if (!Directory.Exists(dataDirectory))
        {
            run.Note($"{Path.GetFullPath(dataDirectory)} does not exist; nothing mounted");
            return null;
        }

        var files = FindDataFiles(dataDirectory).ToList();
        found = files.Count;

        // Grouped by extension rather than sorted as one flat list, so precedence is the order of
        // MountableFiles and not whatever alphabetical order the names happen to fall in.
        var entries = FoldPartFolders(files)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count > MaxScanFiles)
        {
            string what = entries.Any(e => e.IsFolder) ? "data files and part folders" : "data files";
            run.TooMany = true;
            run.Note($"warning: found {entries.Count:N0} {what} under {Path.GetFullPath(dataDirectory)}, " +
                     $"more than the {MaxScanFiles} a folder scan will mount; nothing was mounted from it");
            run.Note("         point --data-dir at a narrower folder, or mount what you need with --init");
            return null;
        }

        return entries;
    }

    /// <summary>
    /// Mounts what <see cref="ScanEntries"/> found, each entry under the name its file or folder
    /// gives it. A file that cannot be read costs its own table and nothing else.
    /// </summary>
    private void MountEntries(ScanRun run, List<ScanEntry> entries)
    {
        var superseded = Superseded(run, entries);
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int mounted = 0;
        int skipped = 0;

        foreach (var entry in entries)
        {
            var (extension, reader) = MountableFiles[entry.Kind];

            if (reader is null)
            {
                int sheets = LoadWorkbook(entry.Path, extension, taken, run);
                if (sheets > 0) mounted++;
                if (sheets < 0) skipped++;
                continue;
            }

            var (schema, table) = NameFor(run, entry);
            string qualified = $"{schema}.{table}";

            if (superseded.TryGetValue(entry.Path, out string? newer))
            {
                run.Note($"warning: {entry.Path} would also be {qualified}, and {newer} is newer; skipped");
                continue;
            }

            if (taken.TryGetValue(qualified, out string? already))
            {
                run.Note($"warning: {entry.Path} would also be {qualified}, already mounted from {already}; skipped");
                continue;
            }

            string path = Path.GetFullPath(entry.Path).Replace('\\', '/');
            if (!schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                Execute($"CREATE SCHEMA IF NOT EXISTS {Quote(schema)}");

            // A directory scan is opportunistic in a way an --init script is not: the script was
            // written for this server, but a folder is just a folder, and one unreadable file in
            // it should cost that one table rather than every other table with it. A CSV whose
            // dialect DuckDB cannot sniff is the usual case, and JSON that does not parse, or a
            // .gz that is not gzip, fail here the same way.
            try
            {
                Execute($"CREATE OR REPLACE VIEW {Quote(schema)}.{Quote(table)} AS " +
                        $"SELECT * FROM {reader}({(entry.IsFolder ? PartsSource(path, entry.Parts) : Literal(path))})");
            }
            catch (DuckDBException ex)
            {
                run.Note($"warning: {path} could not be read; skipped");
                run.Note($"         {FirstLine(ex.Message)}");
                skipped++;
                continue;
            }

            // A mounted file is a view over a reader function, but it stands for the table a
            // client came to find, so the catalog is told to report it as one.
            Execute("INSERT INTO sys.mounted_objects VALUES " +
                    $"('{schema.Replace("'", "''")}', '{table.Replace("'", "''")}')");

            taken[qualified] = entry.Path;
            mounted += entry.IsFolder ? entry.Parts.Count : 1;
            run.Objects.Add(new MountedObject(schema, table, SourceOf(entry), entry.IsFolder ? path + "/" : path));
            run.Log(entry.IsFolder
                ? $"mounted {qualified,-40} <- {path}/, {entry.Parts.Count:N0} part(s)"
                : $"mounted {qualified,-40} <- {path}");
        }

        string tally = $"{mounted} data file(s) mounted from {Path.GetFullPath(run.Root)}";
        run.Log(skipped == 0 ? tally : $"{tally} ({skipped} skipped)");
    }

    /// <summary>What daffydb.tables calls the kind of file an entry is read from.</summary>
    private static string SourceOf(ScanEntry entry)
    {
        if (entry.IsFolder) return "parquet parts";

        return MountableFiles[entry.Kind].Extension.TrimStart('.').Split('.')[0] switch
        {
            "jsonl" or "ndjson" => "json",
            "xlsx" or "xlsm" => "excel",
            var kind => kind,
        };
    }

    /// <summary>
    /// Folds each folder of parts into one entry and passes every other file through as its own.
    ///
    /// A folder is one table when its data files are all parquet, and either all named as parts or
    /// vouched for by a _SUCCESS marker. One that mixes parts with anything else is left alone, file
    /// by file, because then nothing says which reading is meant. Only the files directly in a
    /// folder count, which is what lets a folder of parts sit inside a schema folder.
    /// </summary>
    private static IEnumerable<ScanEntry> FoldPartFolders(List<(string File, int Kind)> files)
    {
        foreach (var folder in files.GroupBy(f => Path.GetDirectoryName(f.File) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            // A drive's root has no name of its own to give a table.
            bool parts = Path.GetFileName(folder.Key).Length > 0
                         && folder.All(f => MountableFiles[f.Kind].Extension == ".parquet")
                         && (folder.All(f => PartFileName.IsMatch(Path.GetFileName(f.File)))
                             || File.Exists(Path.Combine(folder.Key, SuccessMarker)));

            if (parts)
                yield return new ScanEntry(folder.Key, folder.First().Kind,
                                           folder.Select(f => f.File).Order(StringComparer.OrdinalIgnoreCase).ToList());
            else
                foreach (var (file, kind) in folder)
                    yield return new ScanEntry(file, kind, []);
        }
    }

    /// <summary>
    /// Entries that lose to a newer export of the same table, keyed by path, with the path of the one
    /// that wins. A table exported once as a single file and later as a folder of parts, or the
    /// other way round, leaves both behind under one name, and the newer is the export that counts.
    ///
    /// Only a clash with a folder of parts in it is settled by age. Any other keeps the rule that
    /// the first found wins, which is what puts parquet ahead of CSV.
    /// </summary>
    private static Dictionary<string, string> Superseded(ScanRun run, List<ScanEntry> entries)
    {
        var losers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var claims = entries
            .Where(e => MountableFiles[e.Kind].Extension == ".parquet")
            .GroupBy(e => NameFor(run, e).ToString(), StringComparer.OrdinalIgnoreCase)
            .Where(claim => claim.Count() > 1 && claim.Any(e => e.IsFolder));

        foreach (var claim in claims)
        {
            var newest = claim.MaxBy(LastWritten)!;
            foreach (var entry in claim)
                if (entry != newest) losers[entry.Path] = newest.Path;
        }

        return losers;
    }

    /// <summary>When a file was written, or a folder's newest part.</summary>
    private static DateTime LastWritten(ScanEntry entry) =>
        entry.IsFolder ? entry.Parts.Max(File.GetLastWriteTimeUtc) : File.GetLastWriteTimeUtc(entry.Path);

    /// <summary>
    /// What a folder of parts is read from: a glob over the folder, so a re-export that writes more
    /// parts or fewer is read as it now stands, just as a single file is read as it now stands.
    /// DuckDB has no way to escape a glob character, though, and a folder whose name holds one
    /// matches nothing, so that folder is read from the list of parts the scan found instead.
    /// </summary>
    private static string PartsSource(string folder, IReadOnlyList<string> parts) =>
        folder.IndexOfAny(['[', ']', '*', '?']) < 0
            ? Literal($"{folder}/*.parquet")
            : "[" + string.Join(", ", parts.Select(p => Literal(Path.GetFullPath(p).Replace('\\', '/')))) + "]";

    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>
    /// Loads a workbook's sheets as tables in a schema of the workbook's own name - always, one
    /// sheet or many, so that adding a sheet never renames the ones already there. Folders and dots
    /// play no part: Finance/Budget.2024.xlsx is the schema Budget.2024.
    ///
    /// Returns how many sheets were loaded, or -1 when the workbook could not be opened at all.
    /// Like any other file in a scan, a workbook that fails costs itself and nothing else.
    /// </summary>
    private int LoadWorkbook(string file, string extension, Dictionary<string, string> taken, ScanRun run)
    {
        string schema = run.Schema ?? WorkbookSchema(file, extension);
        string path = Path.GetFullPath(file).Replace('\\', '/');

        List<ExcelWorkbook.Outcome> outcomes;
        try
        {
            outcomes = ExcelWorkbook.Load(_connection, file, Databases.Data, schema,
                                          sheet => taken.GetValueOrDefault($"{schema}.{sheet}"));
        }
        catch (Exception ex)
        {
            run.Note($"warning: {path} could not be read; skipped");
            run.Note($"         {FirstLine(ex.Message)}");
            return -1;
        }

        int loaded = 0;
        foreach (var outcome in outcomes)
        {
            string qualified = $"{schema}.{outcome.Sheet}";
            string where = $"{path} [{outcome.Sheet}]";

            switch (outcome)
            {
                case ExcelWorkbook.Loaded done:
                    taken[qualified] = file;
                    loaded++;
                    run.Objects.Add(new MountedObject(schema, outcome.Sheet, "excel", where, IsTable: true));
                    run.Log($"loaded  {qualified,-40} <- {where}, {done.Rows:N0} row(s)");
                    break;

                case ExcelWorkbook.Clash clash:
                    run.Note($"warning: {where} would also be {qualified}, already mounted from {clash.Owner}; skipped");
                    break;

                case ExcelWorkbook.NotATable:
                    run.Note($"note: {where} has values to the right of its header row, so it does not look like a table; skipped");
                    break;

                case ExcelWorkbook.Failed failed:
                    run.Note($"warning: {where} could not be loaded; skipped");
                    run.Note($"         {failed.Reason}");
                    break;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Every mountable file under a directory, with its index in <see cref="MountableFiles"/>.
    ///
    /// A folder that cannot be opened is passed over rather than failing the scan, and so are
    /// hidden and system ones: a scan is opportunistic, and one locked folder should not keep the
    /// server from starting.
    /// </summary>
    private static IEnumerable<(string File, int Kind)> FindDataFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxScanDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var entries = new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                entry.FileName is not ['.', ..] && !SkippedFolders.Contains(entry.FileName.ToString()),
        };

        foreach (string file in entries)
        {
            string name = Path.GetFileName(file);
            if (SkippedFiles.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name))) continue;

            int kind = KindOf(file);
            if (kind >= 0) yield return (file, kind);
        }
    }

    /// <summary>A file's index in <see cref="MountableFiles"/>, by its extension, or -1 for one no reader takes.</summary>
    private static int KindOf(string file)
    {
        string name = Path.GetFileName(file);
        return Array.FindIndex(MountableFiles, k => name.Length > k.Extension.Length &&
                                                    name.EndsWith(k.Extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Works out the schema and table an entry maps to. A file goes by its name, the whole of the
    /// extension off first, so orders.csv.gz is orders and not a table named csv; a folder of parts
    /// goes by the folder's name. Either way a dot in the name splits schema from table; failing
    /// that, the folder it is in does; failing both, it lands in dbo. So does a folder of parts that
    /// is the data folder itself.
    /// </summary>
    private static (string Schema, string Table) NameFor(ScanRun run, ScanEntry entry)
    {
        var (schema, table) = NameFor(run.Root, entry);
        return (run.Schema ?? schema, table);
    }

    /// <summary>A workbook is a schema of its own name, the whole of the extension off.</summary>
    private static string WorkbookSchema(string file, string extension) => Path.GetFileName(file)[..^extension.Length];

    private static (string Schema, string Table) NameFor(string root, ScanEntry entry)
    {
        string full = Path.GetFullPath(entry.Path);
        string stem = entry.IsFolder ? Path.GetFileName(full) : Path.GetFileName(full)[..^MountableFiles[entry.Kind].Extension.Length];

        int dot = stem.IndexOf('.');
        if (dot > 0 && dot < stem.Length - 1) return (stem[..dot], stem[(dot + 1)..]);

        // Trimmed, because a folder typed as data\ arrives with the separator still on it, and then
        // nothing would ever equal it: every file at the top would be in a schema named data.
        string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? directory = entry.IsFolder && full.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
            ? rootPath
            : Path.GetDirectoryName(full);

        // Only an immediate subdirectory names a schema; anything deeper uses its nearest folder.
        if (directory is not null && !directory.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return (Path.GetFileName(directory), stem);

        return ("dbo", stem);
    }

    /// <summary>
    /// Runs a DuckDB SQL script at startup — the place to INSTALL httpfs, CREATE SECRET, ATTACH a
    /// database, or mount anything a file glob cannot express.
    ///
    /// This is DuckDB's own dialect, not T-SQL, and deliberately so: everything in DuckDB's
    /// documentation can be pasted in unchanged. A statement that fails stops the server, because
    /// a server silently missing half its tables is worse than one that did not come up.
    /// </summary>
    private void RunInitScript(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"init script not found: {path}", path);

        string text = File.ReadAllText(path);
        var statements = SqlSegments.SplitStatements(text);
        Console.WriteLine($"[duckdb] running {statements.Count} statement(s) from {Path.GetFullPath(path)}");

        foreach (string statement in statements)
        {
            Console.WriteLine($"[duckdb] init  : {Redact.Text(Flatten(statement))}");
            try
            {
                Execute(statement);
            }
            catch (DuckDBException ex)
            {
                throw new InvalidOperationException(Redact.Text(
                    $"init script failed at: {Flatten(statement)}{Environment.NewLine}  {FirstLine(ex.Message)}"), ex);
            }
        }
    }

    /// <summary>
    /// Every caller is accepted, into the database they asked for - or daffydb, where the data is,
    /// if they named none, as SQL Server does for a login whose default database is set. A database
    /// that does not exist fails the login, as it does on SQL Server, rather than quietly landing
    /// the client somewhere it did not ask to be.
    ///
    /// Real authentication goes here as well: the session carries the user name, the password and
    /// where they connected from, so a handler can check credentials, pick per-user storage
    /// credentials, or turn the caller away by returning <see cref="LoginVerdict.Reject"/>.
    /// </summary>
    public Task<LoginVerdict> OnLoginAsync(SqlSession session, CancellationToken cancellationToken)
    {
        Chatter.Line($"[duckdb] login : {session}");

        if (string.IsNullOrEmpty(session.Database))
            return Task.FromResult(LoginVerdict.AcceptInto(Databases.Data));

        string? database = Databases.Resolve(session.Database);
        return Task.FromResult(database is null
            ? LoginVerdict.UnknownDatabase(session.Database)
            : LoginVerdict.AcceptInto(database));
    }

    /// <summary>
    /// Puts the shared connection in a session's database. DuckDB's USE is per connection and every
    /// session shares this one, so it is issued whenever the session asking is not in the database
    /// the last one left behind - and is otherwise skipped, which is nearly always.
    /// </summary>
    private void UseDatabase(string database)
    {
        if (database == _current) return;

        Execute($"USE {database}.dbo");
        _current = database;
    }

    /// <summary>
    /// T-SQL's USE, which moves this session - not the whole server - to another database. The
    /// client is told through the result, which becomes the ENVCHANGE a real server sends, so that
    /// SqlConnection.Database and every later DB_NAME() follow.
    /// </summary>
    private void ChangeDatabase(string requested, BatchContext context)
    {
        string database = Databases.Resolve(requested)
            ?? throw new TdsQueryException(new SqlErrorInfo(Databases.NotFoundNumber,
                   $"Database '{requested.Trim('[', ']', '"')}' does not exist. Make sure that the name is entered correctly."));

        UseDatabase(database);
        context.Session = context.Session with { Database = database };
        context.ChangedDatabase = database;
        Chatter.Line($"[duckdb] use   : {database}");
    }

    /// <summary>
    /// The mutable state of one batch: the variables it has declared, the result sets it has
    /// produced so far, the line it is executing, and the error a CATCH block is handling.
    /// </summary>
    private sealed class BatchContext(SqlRequest request)
    {
        public SqlRequest Request { get; } = request;

        /// <summary>
        /// What SUSER_SNAME(), HOST_NAME(), APP_NAME() and DB_NAME() answer for this batch. A USE
        /// part-way through replaces it, so the statements after it see the new database.
        /// </summary>
        public SessionContext Session { get; set; } = Describe(request.Session);

        /// <summary>Where a USE in this batch moved the session, if one did; the client is told.</summary>
        public string? ChangedDatabase { get; set; }

        public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ResultSet> ResultSets { get; } = [];

        /// <summary>What daffydb.* commands report besides their rows, sent as PRINT output is.</summary>
        public List<string> Messages { get; } = [];

        /// <summary>Non-null only while a CATCH block is running; this is what ERROR_* report.</summary>
        public SqlErrorInfo? Error { get; set; }

        public int Line { get; set; }

        /// <summary>The OUTPUT parameters the request's procedure set: a handle, a cursor's options.</summary>
        public List<ReturnValue> ReturnValues { get; } = [];

        /// <summary>
        /// Run only to learn what each statement returns, for sp_prepare and sp_cursoropen, which
        /// describe a statement without running it. A query is read for its columns and none of
        /// its rows, and nothing that would change anything runs at all.
        /// </summary>
        public bool DescribeOnly { get; init; }

        /// <summary>The most rows a query here returns: none when describing, else SET ROWCOUNT's.</summary>
        public int? RowLimit =>
            DescribeOnly ? 0 : Request.Session.State.RowCount > 0 ? Request.Session.State.RowCount : null;

        private static SessionContext Describe(SqlSession session) => new(
            string.IsNullOrEmpty(session.UserName) ? "dbo" : session.UserName,
            Databases.Resolve(session.Database) ?? Databases.Data,
            string.IsNullOrEmpty(session.HostName) ? "client" : session.HostName,
            string.IsNullOrEmpty(session.ApplicationName) ? "TDS proxy client" : session.ApplicationName);
    }

    /// <summary>
    /// Runs the batch one statement at a time. DECLARE and SET of a variable are handled here
    /// rather than sent to DuckDB, which has no notion of @-prefixed batch variables; every other
    /// statement is translated, executed, and contributes a result set if it returns columns.
    /// </summary>
    public async Task<QueryResult> ExecuteAsync(SqlRequest request, CancellationToken cancellationToken)
    {
        var context = new BatchContext(request);
        IReadOnlyList<SqlErrorInfo> raised = [];

        await _mutex.WaitAsync(cancellationToken);
        lock (_runningLock)
        {
            _runningRequest = request;
            _runningToken = cancellationToken;
            _cancelRequested = false;
        }

        try
        {
            StopIfCancelled();
            UseDatabase(context.Session.Database);

            // An RPC that names a procedure carries no SQL to parse: its arguments arrive already
            // typed, so they go straight to the procedure - or to the command, for daffydb.*.
            var arguments = request.Parameters.Select(p => ((string?)p.Name, p.Value));

            if (request.Procedure is not null && DaffyCommands.IsCommand(request.Procedure, out string verb))
                RunCommand(verb, arguments, context);
            else if (request.Procedure is not null && IsPreparedProcedure(request.Procedure))
                RunPrepared(context);
            else if (request.Procedure is not null)
                CallProcedure(request.Procedure, arguments, context);
            else
                Run(TSqlParser.Parse(request.Sql), context, depth: 0);
        }
        catch (TdsQueryException ex) when (ex.Raised is not null)
        {
            // SQL Server's own errors, through daffydb.run_sqlserver: what it returned first stands.
            raised = ex.Raised;
        }
        finally
        {
            lock (_runningLock) _runningRequest = null;
            _mutex.Release();
        }

        return new QueryResult(context.ResultSets)
        {
            Database = context.ChangedDatabase,
            Messages = context.Messages,
            ReturnValues = context.ReturnValues,
            Errors = raised,
        };
    }

    /// <summary>
    /// A client cancelled its request. If that request is the one on the connection, DuckDB is
    /// interrupted - the statement in progress fails with DuckDB's "Interrupted" - and the batch
    /// stops before its next statement, so a TRY block cannot carry it on. Without this a runaway
    /// query, such as a recursive CTE with no end, held the one connection every client shares
    /// until it ran out of memory.
    /// </summary>
    public void Cancel(SqlRequest request)
    {
        lock (_runningLock)
        {
            if (!ReferenceEquals(_runningRequest, request)) return;

            _cancelRequested = true;

            // DuckDB interrupts per connection; any command on it will do.
            using var interrupt = _connection.CreateCommand();
            interrupt.Cancel();

            // DuckDB's interrupt lands between chunks, so it cannot stop a SQL Server that has not
            // answered yet, or a read blocked on a lock: this sends SQL Server the attention itself.
            // It is inside the lock, as the interrupt is, so it can only reach this request.
            DaffySql.Cancel(_connection);
        }
    }

    private void StopIfCancelled()
    {
        if (_cancelRequested || _runningToken.IsCancellationRequested)
            throw new OperationCanceledException("the client cancelled the request");
    }

    /// <summary>
    /// Lets the sessions waiting for the connection have it, part way through a long request, and
    /// takes it back once they are done: daffydb.save_parquet_all does this between one table and
    /// the next. They wait asynchronously, and SemaphoreSlim hands a released slot to one of them
    /// directly, so this request goes to the back of the queue rather than taking the slot straight
    /// back.
    ///
    /// The wait is not cancellable, because ExecuteAsync lets the connection go on the way out and
    /// so has to hold it then. A cancel that comes meanwhile is still seen: the request's token is
    /// cancelled, and its cancel is asked for again until it stops.
    /// </summary>
    private void LetOthersRun()
    {
        WithoutTheConnection<object?>(_ => null);
        StopIfCancelled();
    }

    /// <summary>
    /// Runs work that needs nothing of DuckDB's, with the connection let go for other sessions, and
    /// takes it back once they are done, as <see cref="LetOthersRun"/> says. The work is given the
    /// request's token, which is how a cancel reaches it: <see cref="Cancel"/> has no request to find
    /// while the connection is let go.
    /// </summary>
    private T WithoutTheConnection<T>(Func<CancellationToken, T> work)
    {
        SqlRequest? request;
        CancellationToken token;
        lock (_runningLock)
        {
            request = _runningRequest;
            token = _runningToken;
            _runningRequest = null;
        }

        string database = _current;
        _mutex.Release();
        try
        {
            return work(token);
        }
        finally
        {
            _mutex.Wait(CancellationToken.None);

            lock (_runningLock)
            {
                _runningRequest = request;
                _runningToken = token;
                _cancelRequested = false;
            }

            UseDatabase(database);
        }
    }

    private void Run(IReadOnlyList<TSqlStatement> statements, BatchContext context, int depth)
    {
        if (depth > 16) throw new TdsQueryException("nested EXEC is too deep");

        foreach (var statement in statements)
        {
            StopIfCancelled();

            switch (statement)
            {
                case BlockStatement block:
                    Run(block.Statements, context, depth);
                    break;

                case TryCatchStatement guarded:
                    RunGuarded(guarded, context, depth);
                    break;

                case IfStatement branch:
                    bool condition = EvaluateCondition(branch.Condition, context);
                    Chatter.Line($"[duckdb] if {Flatten(branch.Condition)} -> {condition}");

                    var taken = condition ? branch.Then : branch.Else;
                    if (taken is not null) Run([taken], context, depth);
                    break;

                case SimpleStatement simple:
                    context.Line = simple.Line;
                    RunSimple(simple.Text, context, depth);
                    break;
            }
        }
    }

    /// <summary>
    /// BEGIN TRY / BEGIN CATCH. An error inside the guarded block abandons the rest of it and runs
    /// the handler, which reads the error through ERROR_MESSAGE() and friends. Result sets the TRY
    /// block already produced are kept — SQL Server keeps them too.
    /// </summary>
    private void RunGuarded(TryCatchStatement guarded, BatchContext context, int depth)
    {
        var outer = context.Error;

        try
        {
            Run([guarded.Try], context, depth);
            return;                       // nothing was thrown, so the CATCH block does not run
        }
        catch (Exception ex) when (ex is TdsQueryException or DuckDBException)
        {
            context.Error = Caught(ex, context.Line);
            Chatter.Line($"[duckdb] caught: {context.Error.Message}");
        }

        try
        {
            if (guarded.Catch is not null) Run([guarded.Catch], context, depth);
        }
        finally
        {
            // ERROR_* go back to reporting the enclosing CATCH, or NULL once outside one entirely.
            context.Error = outer;
        }
    }

    /// <summary>
    /// Describes a failure the way a CATCH block expects to see it. DuckDB has no equivalent of
    /// SQL Server's message catalogue, so everything it raises is reported under the number
    /// reserved for user-raised errors.
    /// </summary>
    private static SqlErrorInfo Caught(Exception exception, int line) => exception switch
    {
        TdsQueryException raised => raised.Error with { Line = raised.Error.Line == 0 ? line : raised.Error.Line },
        _ => new SqlErrorInfo(SqlErrors.UserErrorNumber, FirstLine(exception.Message), Line: line),
    };

    private static string FirstLine(string message)
    {
        int newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }

    private void RunSimple(string statement, BatchContext context, int depth)
    {
        if (LocalVariables.TryParseDeclare(statement, out var declarations))
        {
            foreach (var declaration in declarations)
            {
                context.Variables[declaration.Name] = declaration.InitialExpression is null
                    ? null
                    : EvaluateScalar(declaration.InitialExpression, declaration.Type, context);

                Chatter.Line($"[duckdb] declare {declaration.Name} = {Describe(context.Variables[declaration.Name])}");
            }
            return;
        }

        if (LocalVariables.TryParseSet(statement, out string name, out string expression))
        {
            if (!context.Variables.ContainsKey(name))
                throw new TdsQueryException($"{name} is assigned but was never declared");

            context.Variables[name] = EvaluateScalar(expression, null, context);
            Chatter.Line($"[duckdb] set {name} = {Describe(context.Variables[name])}");
            return;
        }

        if (LocalVariables.TryParseSelectAssignment(statement, out var assignment))
        {
            AssignFromSelect(assignment!, context);
            return;
        }

        // THROW and RAISERROR exist so that a CATCH block can be reached on purpose, and so that a
        // bare THROW inside one can pass the error back out.
        if (SqlErrors.TryParseThrow(statement, out var thrown))
        {
            var error = thrown ?? context.Error
                ?? throw new TdsQueryException("THROW with no arguments is only allowed inside a CATCH block");

            throw new TdsQueryException(error with { Line = context.Line });
        }

        if (SqlErrors.TryParseRaiserror(statement, out var raised))
            throw new TdsQueryException(raised! with { Line = context.Line });

        // Describing a statement runs its queries for their columns and nothing else: not a
        // command, not DuckDB SQL, not a USE, and not anything that writes.
        if (context.DescribeOnly && !IsDescribable(statement)) return;

        // SET ROWCOUNT stays with the connection: every query after it returns at most that many
        // rows, until SET ROWCOUNT 0. A linked server sets it to 1 to read one row of a table.
        if (RowCountPattern.Match(statement) is { Success: true } rowCount)
        {
            object? rows = EvaluateArgument(rowCount.Groups["rows"].Value, context);
            context.Request.Session.State.RowCount = Math.Max(System.Convert.ToInt32(rows ?? 0), 0);
            Chatter.Line($"[duckdb] rowcount: {context.Request.Session.State.RowCount}");
            return;
        }

        // EXEC daffydb.status and the rest are DaffyDB's own, and never reach the translator.
        if (DaffyCommands.TryParse(statement, out var command))
        {
            DaffyCommands.IsCommand(command.Name, out string verb);
            RunCommand(verb, command.Arguments.Select(a => (a.Name, EvaluateCommandArgument(a.Expression, context))),
                       context);
            return;
        }

        // INSERT #t EXEC daffydb.run_sqlserver ...: the same command, its rows kept in a table.
        if (DaffyCommands.TryParseInsertRun(statement, out var insert, out var inserted))
        {
            DaffyCommands.IsCommand(inserted.Name, out string insertVerb);
            RunCommand(insertVerb, inserted.Arguments.Select(a => (a.Name, EvaluateCommandArgument(a.Expression, context))),
                       context, insert);
            return;
        }

        // EXEC sp_duckdb N'...' runs its argument against DuckDB with no translation at all.
        if (SystemProcedures.TryParseDuckDb(statement, out string passthrough))
        {
            RunPassthrough(passthrough, context);
            return;
        }

        // INSERT #t EXEC some_proc reduces to the query that backs the procedure.
        if (SystemProcedures.TryParseInsertExec(statement, out string target, out string insertQuery))
        {
            ExecuteStatement($"INSERT INTO {target} {insertQuery}", context);
            return;
        }

        if (Databases.TryParseUse(statement, out string database))
        {
            ChangeDatabase(database, context);
            return;
        }

        // EXEC name args reaches the same procedures an RPC does. The arguments are evaluated here
        // first, because written as text they may be variables or expressions as well as literals.
        if (SystemProcedures.TryParseCall(statement, out var call))
        {
            // EXEC sp_executesql N'...', N'@a int', @a = 1 is the RPC written out as text, which
            // the ODBC driver sends when a linked server connects.
            if (SystemProcedures.BareName(call.Name).Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
            {
                RunExecuteSql(call, context);
                return;
            }

            // A procedure that does nothing is not handed its arguments: they can use what DaffyDB
            // does not evaluate - xp_qv is passed @@SERVICENAME - and nothing would read them.
            var arguments = SystemProcedures.Find(call.Name) is { IsNoOp: true }
                ? []
                : call.Arguments.Select(a => (a.Name, EvaluateArgument(a.Expression, context)));

            CallProcedure(call.Name, arguments, context);
            return;
        }

        // EXEC('...') runs the text it is given, so evaluate the expression and execute the result
        // as a batch of its own.
        if (LocalVariables.TryParseExec(statement, out string execExpression))
        {
            object? inner = EvaluateScalar(execExpression, null, context);
            string innerSql = inner as string ?? "";
            Chatter.Line($"[duckdb] exec  : {Flatten(innerSql)}");

            if (innerSql.Trim().Length > 0)
                Run(TSqlParser.Parse(innerSql), context, depth + 1);
            return;
        }

        if (SqlStatement.IsIgnorable(statement)) return;

        var resultSet = ExecuteStatement(statement, context);
        if (resultSet is not null && SqlStatement.ProducesResultSet(statement)) context.ResultSets.Add(resultSet);
    }

    /// <summary>
    /// Runs a system procedure, however it was called. One DaffyDB does not have is reported with
    /// SQL Server's own error, which clients know how to present - not as whatever DuckDB makes of
    /// the name when it is mistaken for SQL, which is what ODBC and OLE DB used to be told.
    /// </summary>
    private void CallProcedure(string name, IEnumerable<(string? Name, object? Value)> arguments, BatchContext context)
    {
        // A procedure named with a database runs in that database, as a system procedure does on
        // SQL Server: daffydb.sys.sp_tables lists daffydb's tables whichever database the caller
        // is in. The caller is put back afterwards, even if the procedure fails.
        string? qualifier = SystemProcedures.DatabaseOf(name);
        string? database = qualifier is null
            ? null
            : Databases.Resolve(qualifier) ?? throw new TdsQueryException(new SqlErrorInfo(Databases.NotFoundNumber,
                  $"Database '{qualifier}' does not exist. Make sure that the name is entered correctly."));

        string home = _current;
        if (database is not null) UseDatabase(database);
        try
        {
            RunProcedure(name, arguments, context);
        }
        finally
        {
            UseDatabase(home);
        }
    }

    private void RunProcedure(string name, IEnumerable<(string? Name, object? Value)> arguments, BatchContext context)
    {
        var procedure = SystemProcedures.Find(name)
            ?? throw new TdsQueryException(new SqlErrorInfo(SystemProcedures.NotFoundNumber,
                   $"Could not find stored procedure '{SystemProcedures.BareName(name)}'."));

        if (procedure.IsNoOp)
        {
            Chatter.Line($"[duckdb] proc  : {procedure.Name} (does nothing here)");
            return;
        }

        var bound = SystemProcedures.Bind(procedure, arguments);
        Chatter.Line($"[duckdb] proc  : {procedure.Name} " +
                     string.Join(", ", bound.Select(b => $"@{b.Key} = {Describe(b.Value)}")));

        using var command = _connection.CreateCommand();
        command.CommandText = procedure.Sql;
        foreach (var (parameter, value) in bound)
            command.Parameters.Add(new DuckDBParameter(parameter, value ?? DBNull.Value));

        var result = ReadAtMost(command, context.RowLimit);
        if (result is null) return;

        context.ResultSets.Add(procedure.Columns is null ? result : Reshape(result, procedure.Columns));
    }

    /// <summary>
    /// Sends a procedure's rows in the shape it declares. DuckDB has one string type and names every
    /// column; SQL Server tells varchar from nvarchar and leaves some columns unnamed, and the
    /// drivers that call these procedures read them by position and type.
    /// </summary>
    private static ResultSet Reshape(ResultSet result, IReadOnlyList<ColumnDef> columns)
    {
        if (result.Columns.Count != columns.Count)
            throw new InvalidOperationException(
                $"a procedure query returned {result.Columns.Count} columns but declares {columns.Count}");

        var rows = result.Rows
            .Select(row => row.Select((value, i) => value is null ? null : Convert(columns[i].Type, value)).ToArray())
            .ToList();

        return new ResultSet(columns, rows);
    }

    private static readonly Regex StringLiteral = new(
        @"^N?'(?<body>(?:[^']|'')*)'$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// An EXEC argument's value. Literals, which are nearly everything a driver or a tool sends, are
    /// read directly rather than costing a query each; DEFAULT means the parameter's default;
    /// anything else - a variable, an expression - is evaluated.
    /// </summary>
    private object? EvaluateArgument(string expression, BatchContext context)
    {
        string text = expression.Trim();

        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) return SystemProcedures.DefaultValue;

        var literal = StringLiteral.Match(text);
        if (literal.Success) return literal.Groups["body"].Value.Replace("''", "'");

        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int small)) return small;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long large)) return large;

        return EvaluateScalar(text, null, context);
    }

    /// <summary>
    /// Hands a statement to DuckDB exactly as written — no T-SQL rewriting, no parameter
    /// substitution of its contents. This is how a client reaches INSTALL, LOAD, CREATE SECRET,
    /// ATTACH, SET, and anything else DuckDB understands but T-SQL has no way to express.
    ///
    /// Two things worth being clear about. The argument is evaluated first, so it may be a literal
    /// or a variable built up earlier in the batch. And what it creates belongs to the process,
    /// not the connection: one DuckDB connection serves every session, so a view made here is
    /// visible to everyone until the proxy stops.
    /// </summary>
    private void RunPassthrough(string argument, BatchContext context)
    {
        if (!_allowPassthrough)
            throw new TdsQueryException(
                "sp_duckdb is disabled; start the proxy with --allow-passthrough to enable it");

        object? evaluated = EvaluateScalar(argument, null, context);
        string sql = evaluated as string ?? "";

        if (sql.Trim().Length == 0)
            throw new TdsQueryException("sp_duckdb needs a string of DuckDB SQL to run");

        try
        {
            foreach (string one in SqlSegments.SplitStatements(sql))
            {
                Chatter.Line($"[duckdb] raw   : {Flatten(one)}");

                using var command = _connection.CreateCommand();
                command.CommandText = one;

                using var reader = command.ExecuteReader();
                if (reader.FieldCount > 0) context.ResultSets.Add(Read(reader));
            }
        }
        finally
        {
            // DuckDB's own USE is not T-SQL's: it would move the shared connection without moving
            // the session. So the session's database is put back, whatever the SQL said.
            _current = "";
            UseDatabase(context.Session.Database);
        }
    }

    /// <summary>
    /// Runs the query behind a "SELECT [TOP n] @a = ..., @b = ... FROM ..." and assigns from it.
    ///
    /// T-SQL assigns once per row, so what survives is the value from the last row the query
    /// produced — which is why the prefix matters: TOP 1 with an ORDER BY is how you ask for a
    /// particular one. A query that returns nothing leaves the variables exactly as they were.
    /// </summary>
    private void AssignFromSelect(SelectAssignment assignment, BatchContext context)
    {
        foreach (var (name, _) in assignment.Assignments)
        {
            if (!context.Variables.ContainsKey(name))
                throw new TdsQueryException($"{name} is assigned but was never declared");
        }

        string prefix = assignment.Prefix.Length == 0 ? "" : assignment.Prefix + " ";
        string projection = string.Join(", ", assignment.Assignments.Select(a => a.Expression));
        var translated = TSqlTranslator.Translate($"SELECT {prefix}{projection} {assignment.Tail}", context.Error, context.Session);

        Chatter.Line($"[duckdb] assign: {Flatten(translated.Sql)}");

        using var command = _connection.CreateCommand();
        command.CommandText = translated.Sql;
        BindParameters(command, context, translated);

        using var reader = command.ExecuteReader();

        bool any = false;
        var values = new object?[assignment.Assignments.Count];
        while (reader.Read())
        {
            any = true;
            for (int i = 0; i < values.Length && i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        if (!any) return;

        for (int i = 0; i < assignment.Assignments.Count; i++)
        {
            context.Variables[assignment.Assignments[i].Name] = values[i];
            Chatter.Line($"[duckdb] select {assignment.Assignments[i].Name} = {Describe(values[i])}");
        }
    }

    /// <summary>
    /// T-SQL treats a NULL condition as false, so anything that is not definitely true takes the
    /// ELSE branch.
    /// </summary>
    private bool EvaluateCondition(string condition, BatchContext context)
    {
        object? value = EvaluateScalar($"CAST(({condition}) AS BOOLEAN)", null, context);
        return value is bool flag && flag;
    }

    private ResultSet? ExecuteStatement(string statement, BatchContext context)
    {
        var translated = TSqlTranslator.Translate(statement, context.Error, context.Session);

        Chatter.Line($"[duckdb] T-SQL : {Flatten(statement)}");
        Chatter.Line($"[duckdb] DuckDB: {Flatten(translated.Sql)}");
        foreach (string note in translated.Notes) Chatter.Line($"[duckdb] note  : {note}");

        using var command = _connection.CreateCommand();
        // A query's conditions on mounted SQL Server tables go to SQL Server as well; see Pushdown.cs.
        string sql = WithInsertConversions(translated.Sql);
        command.CommandText = SqlStatement.ProducesResultSet(statement) ? WithSqlServerFilters(sql) : sql;
        BindParameters(command, context, translated);

        // A query cut short - by SET ROWCOUNT, or to describe it - is streamed, so DuckDB stops
        // once the rows wanted are read instead of producing all of them first: one row of a
        // table, which is what a linked server asks for, then costs one row. A statement that
        // writes is never cut short, since ROWCOUNT is not applied to writes here.
        return ReadAtMost(command, SqlStatement.ProducesResultSet(statement) ? context.RowLimit : null);
    }

    /// <summary>
    /// Runs a command and reads its result - null when it has no columns - all of it, or streamed
    /// and cut short at <paramref name="limit"/> rows. A streamed query cut short stays open in
    /// DuckDB until the connection's next statement, and so does a SQL Server scan under it, with a
    /// session that may hold locks; so SQL Server is told to stop now, and the session goes back.
    /// </summary>
    private ResultSet? ReadAtMost(DuckDBCommand command, int? limit)
    {
        command.UseStreamingMode = limit is not null;
        try
        {
            using var reader = command.ExecuteReader();
            return reader.FieldCount == 0 ? null : Read(reader, limit);
        }
        finally
        {
            if (limit is not null) DaffySql.Cancel(_connection);
        }
    }

    private static readonly Regex InsertSelectPattern = new(
        @"^\s*INSERT\s+INTO\s+(?<target>""[^""]+""|[\w.]+)\s+(?=SELECT|VALUES|WITH)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Runs an INSERT ... SELECT through TRY_CAST into the target's own column types.
    ///
    /// T-SQL converts loosely on insert — an empty string lands in an int column as zero — while
    /// DuckDB refuses and fails the statement. Client tools rely on that looseness: SMO builds a
    /// VALUES list mixing '' with integers in one column. TRY_CAST keeps the statement alive,
    /// yielding NULL where T-SQL would have produced a default.
    /// </summary>
    private string WithInsertConversions(string sql)
    {
        var match = InsertSelectPattern.Match(sql);
        if (!match.Success) return sql;

        string target = match.Groups["target"].Value;
        var columns = TargetColumns(target.Trim('"'));
        if (columns.Count == 0) return sql;

        string source = sql[(match.Index + match.Length)..];
        var aliases = columns.Select((_, i) => $"c{i}").ToList();
        string projection = string.Join(", ",
            columns.Select((column, i) => $"TRY_CAST({aliases[i]} AS {column.Type}) AS {Quote(column.Name)}"));

        // The newline matters: the source may end in a -- comment, which would otherwise swallow
        // the closing parenthesis.
        return $"INSERT INTO {target} SELECT {projection} FROM ({source}\n) AS __ins({string.Join(", ", aliases)})";
    }

    private List<(string Name, string Type)> TargetColumns(string table)
    {
        var columns = new List<(string, string)>();

        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT column_name, data_type FROM duckdb_columns() WHERE table_name = $1 ORDER BY column_index";
            var parameter = command.CreateParameter();
            parameter.Value = table;
            command.Parameters.Add(parameter);

            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        catch (DuckDBException)
        {
            return [];   // unknown target; let the original statement report the problem
        }

        return columns;
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';

    /// <summary>
    /// Evaluates a variable's value by selecting the expression. Casting to the declared type keeps
    /// the conversion behaviour close to T-SQL, where the declaration decides the type.
    /// </summary>
    private object? EvaluateScalar(string expression, string? declaredType, BatchContext context)
    {
        var translated = TSqlTranslator.Translate(
            declaredType is null ? $"SELECT {expression}" : $"SELECT CAST({expression} AS {declaredType})",
            context.Error, context.Session);

        using var command = _connection.CreateCommand();
        command.CommandText = translated.Sql;
        BindParameters(command, context, translated);

        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// The translator turned @name into $1, $2, ... in order of first use; bind the values in that
    /// same order. A name resolves to an RPC parameter first, then to a batch variable.
    /// </summary>
    private static void BindParameters(DbCommand command, BatchContext context, TranslatedSql translated)
    {
        foreach (string name in translated.ParameterOrder)
        {
            var supplied = context.Request.Parameters.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            object? value;
            if (supplied is not null) value = supplied.Value;
            else if (context.Variables.TryGetValue(name, out object? variable)) value = variable;
            else throw new TdsQueryException(
                $"the statement references {name} but no such parameter or variable was supplied");

            var parameter = command.CreateParameter();
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        _ => value.ToString() ?? "",
    };

    /// <summary>Reads a result: its columns, and its rows up to <paramref name="limit"/>, or all of them.</summary>
    private static ResultSet Read(DbDataReader reader, int? limit = null)
    {
        // DuckDB.NET names a type by its own enum - "Decimal", never DECIMAL(10,2) - so a decimal's
        // precision and scale are only to be had from the schema table. Without it every decimal
        // went out as (18,4), and 12345.67 reached the client as 12345.6700.
        var schema = reader.GetSchemaTable();

        var columns = new List<ColumnDef>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var info = schema is not null && i < schema.Rows.Count ? schema.Rows[i] : null;
            columns.Add(DescribeColumn(reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i),
                                       SmallNumber(info?["NumericPrecision"]), SmallNumber(info?["NumericScale"])));
        }

        var rows = new List<object?[]>();
        while ((limit is null || rows.Count < limit) && reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert(columns[i].Type, reader.GetValue(i));

            rows.Add(row);
        }

        SendFlagsAsBits(columns, rows);
        return new ResultSet(columns, rows);
    }

    /// <summary>
    /// The catalog keeps SQL Server's bit columns as 0/1 DECIMAL(1,0), so they can be added up; see
    /// <see cref="SystemCatalog"/>. Sent as they are, SSMS casts them to bool and fails. So a
    /// DECIMAL(1,0) goes out as bit - but only when every value in it is 0, 1 or NULL, so that a
    /// real one-digit decimal in someone's data can never lose a value.
    /// </summary>
    private static void SendFlagsAsBits(List<ColumnDef> columns, List<object?[]> rows)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i] is not { Type: SqlColumnType.Decimal, Precision: 1, Scale: 0 }) continue;
            if (!rows.All(row => row[i] is null or 0m or 1m)) continue;

            columns[i] = columns[i] with { Type = SqlColumnType.Bit };
            foreach (var row in rows)
                if (row[i] is decimal flag) row[i] = flag == 1m;
        }
    }

    private static byte? SmallNumber(object? value) =>
        value is null or DBNull ? null : System.Convert.ToByte(value);

    /// <summary>
    /// Maps a DuckDB column onto the closest type the response writer can encode. Anything without
    /// a natural TDS equivalent (lists, structs, intervals) is rendered as text rather than
    /// failing the query.
    ///
    /// The names are DuckDB.NET's (UnsignedTinyInt, TimestampMs) as well as DuckDB's own
    /// (UTINYINT, TIMESTAMP_MS), because the reader reports the first and a declared type reads
    /// like the second. Underscores are dropped so the two spellings meet.
    /// </summary>
    private static ColumnDef DescribeColumn(string name, string duckDbType, Type clrType,
                                            byte? precision = null, byte? scale = null)
    {
        string bare = duckDbType.ToUpperInvariant().Replace("_", "");

        if (bare.StartsWith("DECIMAL") || bare.StartsWith("NUMERIC"))
        {
            var match = Regex.Match(bare, @"\((\d+)\s*,\s*(\d+)\)");
            byte p = match.Success ? byte.Parse(match.Groups[1].Value) : precision ?? 18;
            byte s = match.Success ? byte.Parse(match.Groups[2].Value) : scale ?? 4;

            // The writer always sends a 17-byte value, which covers precision up to 38.
            return new ColumnDef(name, SqlColumnType.Decimal, true, Math.Max(p, (byte)1), s);
        }

        // SUM() over an INTEGER column widens to HUGEINT in DuckDB, which is 128-bit and arrives as
        // a BigInteger. There is no 128-bit TDS integer, so it is carried as DECIMAL(38,0). An
        // unsigned 64-bit value overflows bigint in the same way, one digit sooner.
        if (bare is "HUGEINT" or "UHUGEINT" or "UNSIGNEDHUGEINT") return new ColumnDef(name, SqlColumnType.Decimal, true, 38, 0);
        if (bare is "UBIGINT" or "UNSIGNEDBIGINT") return new ColumnDef(name, SqlColumnType.Decimal, true, 20, 0);

        var type = bare switch
        {
            "BOOLEAN" => SqlColumnType.Bit,

            // SQL Server's tinyint is unsigned, so only DuckDB's unsigned one fits it. A signed
            // TINYINT widens to int.
            "UTINYINT" or "UNSIGNEDTINYINT" => SqlColumnType.TinyInt,
            "TINYINT" => SqlColumnType.Int,
            "SMALLINT" => SqlColumnType.SmallInt,
            "USMALLINT" or "UNSIGNEDSMALLINT" or "INTEGER" => SqlColumnType.Int,
            "UINTEGER" or "UNSIGNEDINTEGER" or "BIGINT" => SqlColumnType.BigInt,

            "FLOAT" or "REAL" => SqlColumnType.Real,
            "DOUBLE" => SqlColumnType.Double,
            "DATE" => SqlColumnType.Date,
            "TIME" => SqlColumnType.Time,
            "TIMESTAMP" or "TIMESTAMPS" or "TIMESTAMPMS" or "TIMESTAMPNS" or "TIMESTAMPTZ" or "DATETIME" => SqlColumnType.DateTime2,
            "BLOB" => SqlColumnType.VarBinary,
            "UUID" => SqlColumnType.UniqueIdentifier,
            _ => FromClrType(clrType),
        };

        return new ColumnDef(name, type, Nullable: true);
    }

    private static SqlColumnType FromClrType(Type clrType)
    {
        if (clrType == typeof(Guid)) return SqlColumnType.UniqueIdentifier;
        if (clrType == typeof(byte[]) || typeof(Stream).IsAssignableFrom(clrType)) return SqlColumnType.VarBinary;
        if (clrType == typeof(TimeOnly)) return SqlColumnType.Time;

        return Type.GetTypeCode(clrType) switch
        {
            TypeCode.Boolean => SqlColumnType.Bit,
            TypeCode.Byte => SqlColumnType.TinyInt,
            TypeCode.Int16 => SqlColumnType.SmallInt,
            TypeCode.SByte or TypeCode.UInt16 or TypeCode.Int32 => SqlColumnType.Int,
            TypeCode.UInt32 or TypeCode.Int64 => SqlColumnType.BigInt,
            TypeCode.Single => SqlColumnType.Real,
            TypeCode.Double => SqlColumnType.Double,
            TypeCode.Decimal => SqlColumnType.Decimal,
            TypeCode.DateTime => SqlColumnType.DateTime2,
            _ => SqlColumnType.NVarChar,
        };
    }

    private static object? Convert(SqlColumnType target, object value)
    {
        // BigInteger does not implement IConvertible, so it has to be handled before anything
        // reaches Convert.ToDecimal or friends.
        if (value is BigInteger huge)
        {
            if (huge < MinDecimal || huge > MaxDecimal)
                throw new TdsQueryException($"value {huge} is too large to return over TDS");

            return target == SqlColumnType.NVarChar ? huge.ToString() : (decimal)huge;
        }

        return target switch
        {
            SqlColumnType.Date or SqlColumnType.DateTime2 or SqlColumnType.DateTime => value switch
            {
                DateTime dateTime => dateTime,
                DateOnly dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue),
                DateTimeOffset offset => offset.DateTime,
                _ => System.Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            },

            // A TimeOnly printed as text is "1:14 PM" in an en-US culture: the seconds are gone
            // and the format is not one any client parses back.
            SqlColumnType.Time => value switch
            {
                TimeOnly time => time.ToTimeSpan(),
                TimeSpan span => span,
                _ => TimeSpan.Parse(System.Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            },

            // A BLOB arrives as a stream over DuckDB's own buffer, valid only until the next row;
            // its ToString() is the name of its class, which is what clients used to receive.
            SqlColumnType.VarBinary or SqlColumnType.Binary => value switch
            {
                byte[] bytes => bytes,
                Stream stream => ReadAll(stream),
                _ => System.Text.Encoding.UTF8.GetBytes(Render(value)),
            },

            SqlColumnType.UniqueIdentifier => value is Guid guid ? guid : Guid.Parse(Render(value)),
            SqlColumnType.TinyInt => System.Convert.ToByte(value),
            SqlColumnType.SmallInt => System.Convert.ToInt16(value),
            SqlColumnType.Real => System.Convert.ToSingle(value),
            SqlColumnType.NVarChar or SqlColumnType.VarChar => Render(value),
            _ => value,
        };
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Text for a value that has no column type of its own. Lists and structs are spelled out -
    /// otherwise a client is shown "System.Collections.Generic.List`1[System.Int32]" - and
    /// everything is formatted invariantly, so no answer depends on the server's culture.
    /// </summary>
    private static string Render(object? value) => value switch
    {
        null => "NULL",
        string text => text,
        System.Collections.IDictionary map => "{" + string.Join(", ",
            map.Keys.Cast<object>().Select(key => $"{Render(key)}: {Render(map[key])}")) + "}",
        System.Collections.IEnumerable items and not byte[] => "[" + string.Join(", ",
            items.Cast<object?>().Select(Render)) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static readonly BigInteger MinDecimal = new(decimal.MinValue);
    private static readonly BigInteger MaxDecimal = new(decimal.MaxValue);

    private static string Flatten(string sql)
    {
        string flat = sql.ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat;
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string Scalar(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString() ?? "";
    }

    public void Dispose()
    {
        _connection.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>
/// A failure that should reach the client as a SQL error rather than a dropped connection. It
/// carries the whole <see cref="SqlErrorInfo"/> so that a RAISERROR's own number, severity and
/// state survive both the trip to the client and a CATCH block's ERROR_* functions.
/// </summary>
internal sealed class TdsQueryException : Exception
{
    public TdsQueryException(string message) : base(message) =>
        Error = new SqlErrorInfo(SqlErrors.UserErrorNumber, message);

    public TdsQueryException(SqlErrorInfo error) : base(error.Message) => Error = error;

    public SqlErrorInfo Error { get; }

    /// <summary>
    /// Set when SQL Server raised it, in answer to daffydb.run_sqlserver: every error it raised, which
    /// reach the client after the result sets already made, where other failures replace them. A
    /// CATCH block sees <see cref="Error"/>, the first, as SqlClient's SqlException.Number does.
    /// </summary>
    public IReadOnlyList<SqlErrorInfo>? Raised { get; init; }
}
