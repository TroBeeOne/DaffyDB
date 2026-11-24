using System.Globalization;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// DaffyDB's own commands, EXEC daffydb.*: what is served and where it comes from, and - for an
/// admin - mounting more, or less, while the server runs. <see cref="DaffyCommands"/> has the list
/// and the rules for calling them; this is what each one does.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    private readonly string _dataDirectory;
    private readonly string? _initScript;

    /// <summary>Everything mounted, at startup and since, in the order it was mounted.</summary>
    private readonly List<Mount> _mounts = [];

    /// <summary>
    /// Schemas a mount may not put anything in: DaffyDB's catalog lives in sys, and DuckDB keeps
    /// the other three for itself.
    /// </summary>
    private static readonly HashSet<string> ReservedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys", "information_schema", "pg_catalog", "main",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// One table or view a mount made in daffydb, and what it reads. A database's tables also say
    /// which of the database's own tables each one is, so that mounting another can keep them.
    /// </summary>
    private sealed record MountedObject(string Schema, string Name, string Source, string Location, bool IsTable = false)
    {
        public string? SourceSchema { get; init; }

        public string? SourceTable { get; init; }

        /// <summary>
        /// For a SQL Server table, its columns as SQL Server has them - type and collation, by
        /// name - which is what decides whether a condition on one can be sent to SQL Server.
        /// </summary>
        public IReadOnlyDictionary<string, SourceColumn>? Columns { get; init; }
    }

    /// <summary>
    /// A SQL Server column: its name as SQL Server spells it, its system type's name (int,
    /// nvarchar, datetime2...), and its collation, for text.
    /// </summary>
    private sealed record SourceColumn(string Name, string Type, string? Collation);

    /// <summary>
    /// A folder, a file or a database, and the tables it became. <see cref="Identity"/> - what was
    /// mounted, and the @schema it went in - is what makes mounting the same thing into the same
    /// place again a refresh rather than a clash with itself.
    /// </summary>
    private sealed class Mount(string name, string kind, string location, string identity)
    {
        /// <summary>
        /// What daffydb.tables shows and daffydb.unmount takes: the folder's, the file's or the
        /// database's name, or the @schema it was mounted into when it was given one.
        /// </summary>
        public string Name { get; } = name;

        /// <summary>folder, file, postgres, mysql, sqlserver, sqlite or duckdb.</summary>
        public string Kind { get; } = kind;

        /// <summary>Where it is, for people: never a password.</summary>
        public string Location { get; } = location;

        public string Identity { get; } = identity;

        public List<MountedObject> Objects { get; set; } = [];

        /// <summary>For a database, the name DuckDB has it attached under, and its password's secret.</summary>
        public string? Catalog { get; set; }

        public string? Secret { get; set; }

        /// <summary>For a database, which one it is, whatever schema it went in.</summary>
        public string? SourceKey { get; set; }
    }

    /// <summary>
    /// One pass of the folder scan: where it starts, where its tables go, what it made, and where
    /// its account of itself is written. At startup that is the log; for daffydb.mount, the
    /// warnings reach the client as well.
    /// </summary>
    private sealed class ScanRun(string root)
    {
        public string Root { get; } = root;

        /// <summary>@schema: every table goes here, rather than where its name or folder puts it.</summary>
        public string? Schema { get; init; }

        public List<MountedObject> Objects { get; } = [];

        /// <summary>Set when the folder held more than a scan will mount, so nothing was.</summary>
        public bool TooMany { get; set; }

        /// <summary>What was mounted, and the tally.</summary>
        public Action<string> Log { get; init; } = line => Console.WriteLine($"[duckdb] {line}");

        /// <summary>What was passed over, and why.</summary>
        public Action<string> Note { get; init; } = line => Console.WriteLine($"[duckdb] {line}");
    }

    /// <summary>
    /// The --data-dir folder is a mount like any other, named after itself, so daffydb.tables can
    /// say where its tables came from and daffydb.mount can read it again.
    /// </summary>
    private void RegisterStartupMount(ScanRun startup)
    {
        if (startup.Objects.Count == 0) return;

        string full = FullPath(startup.Root);
        _mounts.Add(new Mount(UniqueName(FolderName(full)), "folder", full, Identity("folder", full, schema: null))
        {
            Objects = startup.Objects,
        });
    }

    private static readonly Regex BareWord = new(@"^#{0,2}[A-Za-z_]\w*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// An argument to a daffydb.* command. T-SQL lets a procedure's string argument go unquoted
    /// when it is one word - EXEC sp_help orders - and that is how the sibling tools' commands are
    /// usually typed, so a bare word is its own text. A temp table's name is one word too, # and
    /// all. Anything else is evaluated as EXEC does.
    /// </summary>
    private object? EvaluateCommandArgument(string expression, BatchContext context)
    {
        string text = expression.Trim();

        if (BareWord.IsMatch(text) &&
            !text.Equals("NULL", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            return text;

        return EvaluateArgument(text, context);
    }

    /// <summary>
    /// Runs a daffydb.* command, however it was called: EXEC in a batch, or an RPC naming it.
    /// An admin command from a client is refused unless --allow-admin was given, and either way the
    /// attempt is logged, never with its passwords.
    /// </summary>
    private void RunCommand(string verb, IEnumerable<(string? Name, object? Value)> arguments, BatchContext context,
                            InsertTarget? insert = null)
    {
        var command = DaffyCommands.Find(verb) ?? throw DaffyCommands.Unknown(verb);
        var bound = DaffyCommands.Bind(command, arguments.ToList());
        var session = context.Request.Session;

        string call = $"{DaffyCommands.Schema}.{command.Name}" +
                      string.Concat(bound.Where(b => b.Value is not null).Select(b => $" @{b.Key}='{Flatten(b.Value!)}'"));
        if (insert is not null) call = $"INSERT {insert.Table} EXEC {call}";

        if (command.Admin)
        {
            if (!_allowAdmin && !session.IsConsole)
            {
                Console.WriteLine(Redact.Text($"[admin]  refused {Who(session)}: {call} (the server was started without --allow-admin)"));

                string does = command.Name.StartsWith("save_parquet", StringComparison.Ordinal)
                    ? "writes files on the machine DaffyDB runs on"
                    : command.Name == "run_sqlserver"
                    ? "runs T-SQL on a SQL Server"
                    : "changes what is served";
                throw DaffyCommands.Error(DaffyCommands.AdminDisabledNumber,
                    $"{DaffyCommands.Schema}.{command.Name} {does}, so a client may run it only when " +
                    "the server was started with --allow-admin. It always works at daffydb's own prompt.");
            }

            // Logged however quiet the server is, as the mounts made at startup are: it changes
            // what every client sees.
            Console.WriteLine(Redact.Text($"[admin]  {Who(session)}: {call}"));
        }
        else
        {
            Chatter.Line($"[duckdb] {call}");
        }

        // What a command makes or reads is in daffydb, whichever database its caller is in.
        string home = _current;
        UseDatabase(Databases.Data);
        try
        {
            switch (command.Name)
            {
                case "help": Help(bound["command"], context); break;
                case "status": Status(context); break;
                case "tables": Tables(context); break;
                case "mount": MountPath(bound["path"]!, bound["schema"], context); break;
                case "postgres" or "mysql" or "sqlserver": MountDatabase(command.Name, bound["connection"]!, bound["table"], bound["schema"], context); break;
                case "sqlite" or "duckdb": MountDatabase(command.Name, bound["path"]!, bound["table"], bound["schema"], context); break;
                case "run_sqlserver": RunOnSqlServer(bound["mount"]!, bound["sql"]!, bound["into"], insert, context); break;
                case "unmount": Unmount(bound["mount"]!, context); break;
                case "save_parquet": SaveParquet(bound["table"]!, bound["folder"]!, bound["file"], context); break;
                case "save_parquet_all": SaveParquetAll(bound["folder"]!, bound["mount"], context); break;
                default: throw DaffyCommands.Unknown(verb);
            }
        }
        finally
        {
            UseDatabase(home);
        }
    }

    private static string Who(SqlSession session) =>
        session.IsConsole ? "prompt" : $"session {session.SessionId} '{session.UserName}' from {session.ClientAddress}";

    // ---------------------------------------------------------------------------------------
    // help, status, tables: open to everyone

    private static void Help(string? name, BatchContext context)
    {
        if (name is null)
        {
            ColumnDef[] columns =
            [
                new("command", SqlColumnType.NVarChar, false),
                new("arguments", SqlColumnType.NVarChar, false),
                new("what_it_does", SqlColumnType.NVarChar, false),
                new("who", SqlColumnType.NVarChar, false),
            ];

            var rows = DaffyCommands.All
                .Select(c => new object?[]
                {
                    $"{DaffyCommands.Schema}.{c.Name}",
                    c.Usage[$"EXEC {DaffyCommands.Schema}.{c.Name}".Length..].Trim(),
                    c.Summary,
                    c.Admin ? "admin" : "everyone",
                })
                .ToList();

            context.ResultSets.Add(new ResultSet(columns, rows));
            context.Messages.Add("EXEC daffydb.help 'mount' shows one command in full. Admin commands work from a client " +
                                 "when the server was started with --allow-admin, and always at daffydb's own prompt.");
            return;
        }

        string bare = name.StartsWith(DaffyCommands.Schema + ".", StringComparison.OrdinalIgnoreCase)
            ? name[(DaffyCommands.Schema.Length + 1)..]
            : name;
        var command = DaffyCommands.Find(bare) ?? throw DaffyCommands.Unknown(bare);

        var lines = new List<string> { command.Usage, "", char.ToUpperInvariant(command.Summary[0]) + command.Summary[1..] + "." };
        lines.AddRange(command.Details);
        lines.Add("");
        lines.Add(command.Examples.Length == 1 ? "Example:" : "Examples:");
        lines.AddRange(command.Examples.Select(e => "  " + e));
        if (command.Admin)
        {
            lines.Add("");
            lines.Add("An admin command: a client may run it when the server was started with --allow-admin.");
        }

        context.ResultSets.Add(new ResultSet(
            [new ColumnDef("help", SqlColumnType.NVarChar, false)],
            lines.Select(line => new object?[] { line }).ToList()));
    }

    /// <summary>One row per fact, so it reads down the page in SSMS rather than across it.</summary>
    private void Status(BatchContext context)
    {
        var rows = new List<object?[]>();
        void Add(string setting, string value) => rows.Add([setting, value]);

        var startup = ServerFacts.Startup;
        var session = context.Request.Session;

        Add("version", $"DaffyDB {StartupBanner.ToolVersion()} on DuckDB {Scalar("SELECT version()")}");
        Add("listening", ServerFacts.Port > 0 ? $"127.0.0.1:{ServerFacts.Port}" : "not on a port");

        Add("encryption", startup switch
        {
            null => "unknown",
            { PublicCertificatePath: not null } => "available: Encrypt=Mandatory and Encrypt=Strict, and plaintext is accepted too",
            { CertificateProblem: { } problem } => $"unavailable: {problem}",
            _ => "off (--no-cert)",
        });

        Add("logins", startup switch
        {
            null => "unknown",
            { User: null, PasswordRequired: false } => "any user name and password",
            { User: { } user, PasswordRequired: true } => $"user '{user}', with the --password value",
            { User: { } user } => $"user '{user}', with any password",
            _ => "any user name, with the --password value",
        });

        var up = DateTime.Now - ServerFacts.StartedAt;
        Add("started", $"{ServerFacts.StartedAt:yyyy-MM-dd HH:mm:ss}, up {Uptime(up)}");
        Add("sessions", $"{ServerFacts.Sessions} connected");

        string data = FullPath(_dataDirectory);
        var dataMount = _mounts.FirstOrDefault(m => m.Identity.Equals(Identity("folder", data, schema: null), PathComparison));
        Add("data folder", dataMount is null
            ? $"{data} (nothing mounted from it)"
            : $"{data} (mount '{dataMount.Name}', {Plural(dataMount.Objects.Count, "table")})");

        Add("init script", _initScript is null ? "none" : FullPath(_initScript));

        Add("mounts", _mounts.Count == 0
            ? "none"
            : string.Join("; ", _mounts.Select(m => $"{m.Name} ({m.Kind}, {Plural(m.Objects.Count, "table")})")));

        var (tables, views) = CountObjects();
        Add("tables", $"{Plural(tables, "table")} and {Plural(views, "view")} in daffydb");

        Add("admin commands", _allowAdmin
            ? "on for every client (--allow-admin)"
            : "off for clients - start with --allow-admin; always on at daffydb's own prompt");
        Add("sp_duckdb", _allowPassthrough ? "on for every client (--allow-passthrough)" : "off - start with --allow-passthrough");

        var downloaded = Strings("SELECT extension_name FROM duckdb_extensions() " +
                                 "WHERE loaded AND install_mode = 'REPOSITORY' ORDER BY extension_name");
        Add("extensions", downloaded.Count == 0
            ? "none downloaded"
            : $"downloaded and loaded: {string.Join(", ", downloaded)}");

        Add("you", session.IsConsole
            ? $"daffydb's own prompt, in {context.Session.Database}"
            : $"session {session.SessionId}, user '{session.UserName}' from {session.ClientAddress}, " +
              $"{(session.IsEncrypted ? "encrypted" : "not encrypted")}, in {context.Session.Database}");

        context.ResultSets.Add(new ResultSet(
            [new ColumnDef("setting", SqlColumnType.NVarChar, false), new ColumnDef("value", SqlColumnType.NVarChar, false)],
            rows));
    }

    private static string Uptime(TimeSpan up) =>
        up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
        : up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m"
        : $"{(int)up.TotalMinutes}m {up.Seconds}s";

    private (int Tables, int Views) CountObjects()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FILTER (WHERE type = 'U'), COUNT(*) FILTER (WHERE type = 'V') FROM daffydb.sys.objects";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (System.Convert.ToInt32(reader.GetValue(0)), System.Convert.ToInt32(reader.GetValue(1)));
    }

    /// <summary>
    /// Every table and view, as :tables lists them, with where each came from. Something made by
    /// --init, sp_duckdb or a client's CREATE VIEW has no mount, and says so with NULLs.
    /// </summary>
    private void Tables(BatchContext context)
    {
        var origin = new Dictionary<(string, string), (Mount Mount, MountedObject Object)>(NameComparer.Instance);
        foreach (var mount in _mounts)
            foreach (var mounted in mount.Objects)
                origin[(mounted.Schema, mounted.Name)] = (mount, mounted);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, o.name, o.type,
                   (SELECT COUNT(*) FROM daffydb.sys.columns c WHERE c.object_id = o.object_id)
            FROM daffydb.sys.objects o
            JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id
            ORDER BY s.name, o.name
            """;

        var rows = new List<object?[]>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string schema = reader.GetString(0);
                string name = reader.GetString(1);
                bool found = origin.TryGetValue((schema, name), out var from);

                rows.Add(
                [
                    schema,
                    name,
                    reader.GetString(2) == "U" ? "table" : "view",
                    System.Convert.ToInt32(reader.GetValue(3)),
                    found ? from.Object.Source : null,
                    found ? from.Object.Location : null,
                    found ? from.Mount.Name : null,
                ]);
            }
        }

        ColumnDef[] columns =
        [
            new("schema", SqlColumnType.NVarChar, false),
            new("name", SqlColumnType.NVarChar, false),
            new("type", SqlColumnType.NVarChar, false),
            new("columns", SqlColumnType.Int, false),
            new("source", SqlColumnType.NVarChar, true),
            new("location", SqlColumnType.NVarChar, true),
            new("mount", SqlColumnType.NVarChar, true),
        ];

        context.ResultSets.Add(new ResultSet(columns, rows));
    }

    // ---------------------------------------------------------------------------------------
    // mount and unmount: admin

    /// <summary>
    /// A folder, scanned as --data-dir is, or a single file. Mounting the same one again reads it
    /// again. Every name it would use is checked before anything changes, and if one is taken
    /// nothing is mounted: a mount happens whole or not at all.
    /// </summary>
    private void MountPath(string path, string? schema, BatchContext context)
    {
        CheckSchema(schema);

        string full;
        try
        {
            full = FullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw DaffyCommands.Error(DaffyCommands.BadCallNumber, $"'{path}' is not a path: {ex.Message}");
        }

        bool isFile = File.Exists(full);
        if (!isFile && !Directory.Exists(full))
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"there is no file or folder at {full}. The path is on the machine DaffyDB runs on.");

        string kind = isFile ? "file" : "folder";
        string identity = Identity(kind, full, schema);
        var existing = _mounts.FirstOrDefault(m => m.Identity.Equals(identity, PathComparison));

        var notes = new List<string>();
        var run = new ScanRun(isFile ? Path.GetDirectoryName(full)! : full)
        {
            Schema = schema,
            Note = line =>
            {
                Console.WriteLine($"[duckdb] {line}");
                notes.Add(line.Trim());
            },
        };

        List<ScanEntry> entries;
        if (isFile)
        {
            int fileKind = KindOf(full);
            if (fileKind < 0)
                throw DaffyCommands.Error(DaffyCommands.BadCallNumber,
                    $"{full} is not a file DaffyDB reads. It reads .parquet, .csv, .json, .jsonl, .ndjson " +
                    "(any of the text ones gzipped as well), .xlsx and .xlsm.");

            // Taken as asked for, alone: a part-00000.parquet mounted by name is that one file,
            // not the folder it sits in.
            entries = [new ScanEntry(full, fileKind, [])];
        }
        else
        {
            entries = ScanEntries(run, out _) ?? throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"{full} holds more than the {MaxScanFiles} data files a folder scan will mount, so nothing was " +
                "mounted. Mount a narrower folder.");

            if (entries.Count == 0)
                throw DaffyCommands.Error(DaffyCommands.NotFoundNumber,
                    $"there are no .parquet, .csv, .json or .xlsx files in {full}, or in the two folders below it.");
        }

        var holder = Holders(except: existing);
        var clashes = CandidateNames(run, entries)
            .Distinct(NameComparer.Instance)
            .Select(name => (Name: name, Holder: holder(name)))
            .Where(c => c.Holder is not null)
            .ToList();

        if (clashes.Count > 0)
            throw DaffyCommands.Error(DaffyCommands.NameTakenNumber,
                "nothing was mounted, because " +
                string.Join("; ", clashes.Select(c => $"{c.Name.Item1}.{c.Name.Item2} is {c.Holder}")) +
                ". Give @schema to put the tables somewhere else, or unmount what is there first.");

        if (existing is not null) Unmake(existing.Objects);
        MountEntries(run, entries);
        if (existing is not null) DropEmptySchemas(existing.Objects.Select(o => o.Schema));

        if (run.Objects.Count == 0)
        {
            if (existing is not null) _mounts.Remove(existing);
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"nothing could be read from {full}. {string.Join(" ", notes)}".TrimEnd());
        }

        // Named after the schema it was put in, when it was given one, since that is the name the
        // tables show; otherwise after the folder or file.
        var mount = existing ?? new Mount(UniqueName(schema ?? (isFile ? StemOf(full, fileKind: entries[0].Kind) : FolderName(full))),
                                          kind, full, identity);
        mount.Objects = run.Objects;
        if (existing is null) _mounts.Add(mount);

        context.Messages.AddRange(notes);
        context.Messages.Add(existing is null
            ? $"Mounted {Plural(run.Objects.Count, "table")} from {full} as '{mount.Name}'."
            : $"Read {full} again as '{mount.Name}': {Plural(run.Objects.Count, "table")}.");
        context.ResultSets.Add(Described(mount));
    }

    private Mount NamedMount(string name) =>
        _mounts.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw DaffyCommands.Error(DaffyCommands.NotFoundNumber, _mounts.Count == 0
                ? $"there is no mount named '{name}': nothing is mounted."
                : $"there is no mount named '{name}'. The mounts are {string.Join(", ", _mounts.Select(m => $"'{m.Name}'"))}; " +
                  "daffydb.tables shows what each one holds.");

    private void Unmount(string name, BatchContext context)
    {
        var mount = NamedMount(name);

        Unmake(mount.Objects);
        DropEmptySchemas(mount.Objects.Select(o => o.Schema));
        _mounts.Remove(mount);
        if (mount.Catalog is not null) Release(mount.Catalog, mount.Secret);

        Console.WriteLine($"[duckdb] unmounted '{mount.Name}' ({mount.Location}), {Plural(mount.Objects.Count, "table")}");
        context.Messages.Add($"Unmounted '{mount.Name}': {Plural(mount.Objects.Count, "table")} taken away.");
    }

    /// <summary>What a mount holds, as the grid its command answers with.</summary>
    private static ResultSet Described(Mount mount)
    {
        ColumnDef[] columns =
        [
            new("schema", SqlColumnType.NVarChar, false),
            new("name", SqlColumnType.NVarChar, false),
            new("source", SqlColumnType.NVarChar, false),
            new("location", SqlColumnType.NVarChar, false),
            new("mount", SqlColumnType.NVarChar, false),
        ];

        return new ResultSet(columns, mount.Objects
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new object?[] { o.Schema, o.Name, o.Source, o.Location, mount.Name })
            .ToList());
    }

    /// <summary>
    /// Every name a scan could give its tables, before it gives any: each file's, and each sheet's
    /// of each workbook. That is a little more than it will use - a sheet laid out as a report is
    /// passed over when loaded - which errs on the side of refusing.
    /// </summary>
    private static IEnumerable<(string Schema, string Table)> CandidateNames(ScanRun run, List<ScanEntry> entries)
    {
        foreach (var entry in entries)
        {
            var (extension, reader) = MountableFiles[entry.Kind];
            if (reader is not null)
            {
                yield return NameFor(run, entry);
                continue;
            }

            string schema = run.Schema ?? WorkbookSchema(entry.Path, extension);
            foreach (string sheet in ExcelWorkbook.SheetNames(entry.Path)) yield return (schema, sheet);
        }
    }

    /// <summary>
    /// Who has a name already: another mount, DaffyDB's own catalog, or something made by --init,
    /// sp_duckdb or a client. A mount's own tables do not count against it, since mounting it again
    /// replaces them.
    /// </summary>
    private Func<(string Schema, string Table), string?> Holders(Mount? except)
    {
        var owners = new Dictionary<(string, string), string>(NameComparer.Instance);
        foreach (var mount in _mounts.Where(m => m != except))
            foreach (var mounted in mount.Objects)
                owners[(mounted.Schema, mounted.Name)] = $"mount '{mount.Name}'";

        var own = new HashSet<(string, string)>(except?.Objects.Select(o => (o.Schema, o.Name)) ?? [], NameComparer.Instance);
        var present = new HashSet<(string, string)>(NameComparer.Instance);

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT schema_name, table_name FROM duckdb_tables() WHERE database_name = 'daffydb'
                UNION ALL
                SELECT schema_name, view_name FROM duckdb_views() WHERE database_name = 'daffydb' AND NOT internal
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) present.Add((reader.GetString(0), reader.GetString(1)));
        }

        return name =>
        {
            if (ReservedSchemas.Contains(name.Schema)) return $"in {name.Schema}, a schema DaffyDB keeps for itself";
            if (owners.TryGetValue(name, out string? owner)) return $"taken by {owner}";
            if (present.Contains(name) && !own.Contains(name)) return "taken by a view or table made by --init, sp_duckdb or a client";
            return null;
        };
    }

    /// <summary>Drops what a mount made, and takes its tables back out of the catalog's list.</summary>
    private void Unmake(IEnumerable<MountedObject> objects)
    {
        foreach (var mounted in objects)
        {
            string name = $"{Quote(mounted.Schema)}.{Quote(mounted.Name)}";
            try
            {
                Execute(mounted.IsTable ? $"DROP TABLE IF EXISTS {name}" : $"DROP VIEW IF EXISTS {name}");
            }
            catch (DuckDBException ex)
            {
                // Replaced since by something of the other kind, through sp_duckdb; leave it be.
                Console.WriteLine($"[duckdb] warning: {mounted.Schema}.{mounted.Name} was not dropped: {FirstLine(ex.Message)}");
            }

            Execute("DELETE FROM sys.mounted_objects WHERE " +
                    $"schema_name = {Literal(mounted.Schema)} AND object_name = {Literal(mounted.Name)}");
        }
    }

    /// <summary>A schema a mount made, once nothing is left in it, goes with it. dbo always stays.</summary>
    private void DropEmptySchemas(IEnumerable<string> schemas)
    {
        foreach (string schema in schemas.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) || ReservedSchemas.Contains(schema)) continue;

            string left = Scalar(
                $"SELECT (SELECT COUNT(*) FROM duckdb_tables() WHERE database_name = 'daffydb' AND schema_name = {Literal(schema)}) + " +
                $"(SELECT COUNT(*) FROM duckdb_views() WHERE database_name = 'daffydb' AND schema_name = {Literal(schema)} AND NOT internal)");

            if (left == "0") Execute($"DROP SCHEMA IF EXISTS {Quote(schema)}");
        }
    }

    private static void CheckSchema(string? schema)
    {
        if (schema is not null && ReservedSchemas.Contains(schema))
            throw DaffyCommands.Error(DaffyCommands.BadCallNumber,
                $"@schema cannot be {schema}: DaffyDB keeps that schema for itself.");
    }

    private List<string> Strings(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>A mount's name, made unique by a number if another mount already has it.</summary>
    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int n = 2; _mounts.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{wanted}_{n}";
        return name;
    }

    /// <summary>What was mounted and where it went: the same again is a refresh, not a second mount.</summary>
    private static string Identity(string kind, string location, string? schema) =>
        $"{kind}:{location}|{schema?.ToLowerInvariant()}";

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>A folder's own name, or "data" for a drive's root, which has none.</summary>
    private static string FolderName(string full) => Path.GetFileName(full) is { Length: > 0 } name ? name : "data";

    private static string StemOf(string file, int fileKind) =>
        Path.GetFileName(file)[..^MountableFiles[fileKind].Extension.Length];

    private static string Plural(long count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>
    /// Schema and table names compare as DuckDB compares identifiers, ignoring case, so that
    /// Sales.Orders and sales.orders are the one name they are to every query.
    /// </summary>
    private sealed class NameComparer : IEqualityComparer<(string, string)>
    {
        public static readonly NameComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) name) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(name.Item1),
                             StringComparer.OrdinalIgnoreCase.GetHashCode(name.Item2));
    }
}
