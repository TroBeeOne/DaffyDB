using System.Text;
using DaffySQL;
using DuckDB.NET.Data;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// daffydb.postgres, daffydb.mysql, daffydb.sqlite and daffydb.duckdb: a database attached to
/// DuckDB, read-only, with a view in daffydb over each of its tables, recorded as a table the way
/// a mounted file is. Nothing is copied. A query reads the database as it is then, through
/// DuckDB's own scanner for it, so the T-SQL translation is the same as for any file.
///
/// A password never goes into the ATTACH string, because DuckDB keeps that string as written and
/// any client can read it back with SELECT path FROM duckdb_databases(). It goes into a secret of
/// its own, which DuckDB merges in when it connects and never shows; the rest of the connection
/// string is harmless to show, and is what an error quotes when the connection fails.
///
/// daffydb.sqlserver is the same from the outside, but DuckDB has no SQL Server of its own to
/// attach. DaffySQL is given the connection string instead, under a name like an attached
/// database's, and each view reads its table with daffy_scan under that name. The connection
/// string stays in DaffySQL's memory and never reaches DuckDB, so there is no secret to make.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    /// <summary>Numbers the attached databases, whose names are DaffyDB's own: daffy_1, daffy_2 ...</summary>
    private int _attachments;

    /// <summary>The DuckDB extension each kind of database needs. DuckDB reads its own files itself.</summary>
    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["postgres"] = "postgres_scanner",
        ["mysql"] = "mysql_scanner",
        ["sqlite"] = "sqlite_scanner",
    };

    /// <summary>
    /// What a database command reaches. <paramref name="Attach"/> is the ATTACH string, never with
    /// a password in it - except for SQL Server, where it is the connection string DaffySQL is
    /// given, password and all, which never reaches DuckDB. <paramref name="Home"/> is the source
    /// schema that becomes dbo: public, main, dbo itself, or the MySQL database.
    /// <paramref name="Key"/> identifies the database, so that mounting it again is a refresh.
    /// </summary>
    private sealed record Source(string Kind, string Attach, string? Password, string Location, string Name,
                                 string Key, string Home);

    private sealed record SourceTable(string Schema, string Name);

    private void MountDatabase(string kind, string target, string? table, string? schema, BatchContext context)
    {
        CheckSchema(schema);

        var source = kind switch
        {
            "postgres" or "mysql" => ServerSource(kind, target),
            "sqlserver" => SqlServerSource(target),
            _ => FileSource(kind, target),
        };
        string identity = Identity(kind, source.Key, schema);
        var existing = _mounts.FirstOrDefault(m => m.Identity.Equals(identity, PathComparison));

        if (Extensions.ContainsKey(kind)) EnsureExtension(kind, context);

        var notes = new List<string>();
        var (catalog, secret) = Attach(source, notes);
        bool fresh = catalog != existing?.Catalog;

        List<MountedObject> desired;
        try
        {
            var found = SourceTables(catalog, source);
            if (found.Count == 0)
                throw DaffyCommands.Error(DaffyCommands.NotFoundNumber, $"{source.Location} has no tables to mount.");

            var chosen = table is null ? found : [Pick(found, table, source)];
            var made = Readable(chosen, catalog, source, table is not null, notes)
                .Select(t => Viewed(t, source, schema))
                .ToList();

            // One table adds to what the mount already has; every table replaces it. What the
            // mount had that the database no longer has goes, and says so.
            desired = [];
            if (table is not null && existing is not null)
            {
                foreach (var kept in existing.Objects)
                {
                    if (made.Any(m => NameComparer.Instance.Equals((m.Schema, m.Name), (kept.Schema, kept.Name)))) continue;

                    if (!found.Any(t => SameName(t.Schema, kept.SourceSchema) && SameName(t.Name, kept.SourceTable)))
                    {
                        notes.Add($"{kept.Schema}.{kept.Name} is no longer in {source.Location}, so it was taken away.");
                        continue;
                    }

                    desired.Add(kept);
                }
            }
            desired.AddRange(made);
            if (source.Kind == "sqlserver") desired = WithSqlServerColumns(desired, catalog, source);

            CheckNames(desired, existing, source);
            Create(desired, existing, catalog, source);
        }
        catch
        {
            if (fresh) Release(catalog, secret);
            throw;
        }

        string? oldCatalog = existing?.Catalog;
        string? oldSecret = existing?.Secret;
        var oldSchemas = existing?.Objects.Select(o => o.Schema).ToList() ?? [];

        var mount = existing ?? new Mount(UniqueName(schema ?? source.Name), kind, source.Location, identity);
        mount.Objects = desired;
        mount.Catalog = catalog;
        mount.Secret = secret;
        mount.SourceKey = source.Key;
        if (existing is null) _mounts.Add(mount);

        if (oldCatalog is not null && oldCatalog != catalog) Release(oldCatalog, oldSecret);
        DropEmptySchemas(oldSchemas);

        Console.WriteLine($"[duckdb] mounted '{mount.Name}' <- {source.Location}, {Plural(desired.Count, "table")}");
        context.Messages.AddRange(notes);
        context.Messages.Add(existing is null
            ? $"Mounted {Plural(desired.Count, "table")} from {source.Location} as '{mount.Name}'."
            : table is null
            ? $"Read {source.Location} again as '{mount.Name}': {Plural(desired.Count, "table")}."
            : $"Added {table} to '{mount.Name}', which now has {Plural(desired.Count, "table")}.");
        context.ResultSets.Add(Described(mount));
    }

    /// <summary>
    /// The tables DuckDB can read, found by binding a query over each without running it. One it
    /// cannot - a view of the source's own that refers to something gone, a column of a type its
    /// scanner does not know - is left out and said so, as an unreadable file is in a folder scan,
    /// rather than costing every other table. Asked for by name, it is an error instead.
    /// </summary>
    private List<SourceTable> Readable(List<SourceTable> tables, string catalog, Source source, bool named,
                                       List<string> notes)
    {
        var readable = new List<SourceTable>();
        foreach (var table in tables)
        {
            try
            {
                Execute($"DESCRIBE SELECT * FROM {Relation(catalog, source.Kind, table.Schema, table.Name)}");
                readable.Add(table);
            }
            catch (DuckDBException ex)
            {
                string why = $"{table.Schema}.{table.Name} in {source.Location} could not be read: {FirstLine(ex.Message)}";
                if (named) throw DaffyCommands.Error(DaffyCommands.UnreachableNumber, why);

                Console.WriteLine($"[duckdb] warning: {why}; skipped");
                notes.Add($"Left out {table.Schema}.{table.Name}, which could not be read: {FirstLine(ex.Message)}");
            }
        }

        if (readable.Count == 0)
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"none of the tables in {source.Location} could be read. {string.Join(" ", notes)}");

        return readable;
    }

    /// <summary>The view a source table becomes, and where it lands.</summary>
    private static MountedObject Viewed(SourceTable table, Source source, string? schema) =>
        new(schema ?? (SameName(table.Schema, source.Home) ? "dbo" : table.Schema), table.Name, source.Kind,
            $"{source.Location} {table.Schema}.{table.Name}")
        {
            SourceSchema = table.Schema,
            SourceTable = table.Name,
        };

    /// <summary>
    /// Refuses, before anything is made, a mount that would use a name twice - two source schemas
    /// put in one by @schema - or a name that something else already has.
    /// </summary>
    private void CheckNames(List<MountedObject> desired, Mount? existing, Source source)
    {
        var twice = desired
            .GroupBy(o => (o.Schema, o.Name), NameComparer.Instance)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(o => $"{o.SourceSchema}.{o.SourceTable}"))} would all be {g.Key.Item1}.{g.Key.Item2}")
            .ToList();

        if (twice.Count > 0)
            throw DaffyCommands.Error(DaffyCommands.NameTakenNumber,
                $"nothing was mounted, because in {source.Location} {string.Join("; ", twice)}. " +
                "Mount them without @schema, or one at a time with @table and a schema each.");

        var holder = Holders(except: existing);
        var clashes = desired
            .Select(o => (o.Schema, o.Name, Holder: holder((o.Schema, o.Name))))
            .Where(c => c.Holder is not null)
            .ToList();

        if (clashes.Count > 0)
            throw DaffyCommands.Error(DaffyCommands.NameTakenNumber,
                "nothing was mounted, because " +
                string.Join("; ", clashes.Select(c => $"{c.Schema}.{c.Name} is {c.Holder}")) +
                ". Give @schema to put the tables somewhere else, or unmount what is there first.");
    }

    /// <summary>
    /// Makes the views, in one transaction with taking away the ones a refreshed mount had, so a
    /// failure part way leaves things as they were: a table whose type DuckDB cannot read, say.
    /// </summary>
    private void Create(List<MountedObject> desired, Mount? existing, string catalog, Source source)
    {
        MountedObject? current = null;
        Execute("BEGIN TRANSACTION");
        try
        {
            if (existing is not null) Unmake(existing.Objects);

            foreach (var mounted in desired)
            {
                current = mounted;
                if (!mounted.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    Execute($"CREATE SCHEMA IF NOT EXISTS {Quote(mounted.Schema)}");

                Execute($"CREATE VIEW {Quote(mounted.Schema)}.{Quote(mounted.Name)} AS " +
                        $"SELECT * FROM {Relation(catalog, source.Kind, mounted.SourceSchema!, mounted.SourceTable!)}");
                Execute("INSERT INTO sys.mounted_objects VALUES " +
                        $"({Literal(mounted.Schema)}, {Literal(mounted.Name)})");
            }

            Execute("COMMIT");
        }
        catch (DuckDBException ex)
        {
            try { Execute("ROLLBACK"); } catch (DuckDBException) { }

            string what = current is null ? "" : $" {current.SourceSchema}.{current.SourceTable} could not be read:";
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"nothing was mounted from {source.Location}, because{what} {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// The table @table names: orders, or sales.orders, as the source spells its schema or as
    /// DaffyDB does - dbo.orders finds PostgreSQL's public.orders too.
    /// </summary>
    private static SourceTable Pick(List<SourceTable> found, string table, Source source)
    {
        string? wantedSchema = null;
        string wantedName = table.Trim();

        int dot = wantedName.IndexOf('.');
        if (dot > 0 && dot < wantedName.Length - 1)
        {
            wantedSchema = Unbracket(wantedName[..dot]);
            wantedName = wantedName[(dot + 1)..];
        }
        wantedName = Unbracket(wantedName);

        var matches = found
            .Where(t => SameName(t.Name, wantedName) &&
                        (wantedSchema is null || SameName(t.Schema, wantedSchema) ||
                         (SameName(wantedSchema, "dbo") && SameName(t.Schema, source.Home))))
            .ToList();

        if (matches.Count == 1) return matches[0];

        if (matches.Count > 1)
            throw DaffyCommands.Error(DaffyCommands.BadCallNumber,
                $"there is a {wantedName} in more than one schema of {source.Location}: " +
                $"{string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"))}. Name one of them.");

        var some = found.Take(10).Select(t => $"{t.Schema}.{t.Name}");
        throw DaffyCommands.Error(DaffyCommands.NotFoundNumber,
            $"there is no table {table} in {source.Location}. It has {string.Join(", ", some)}" +
            (found.Count > 10 ? $" and {found.Count - 10:N0} more." : "."));
    }

    private static string Unbracket(string name) => name.Trim().Trim('[', ']', '"', '`');

    private static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a view over a source table reads: the attached database's own table, or for SQL
    /// Server, daffy_scan over it under DaffySQL's name for the server. daffy_scan asks SQL Server
    /// for only the columns a query uses. The table is named as T-SQL brackets it, which DaffySQL
    /// parses back and quotes again itself, so nothing of a name is ever pasted into T-SQL.
    /// </summary>
    private static string Relation(string catalog, string kind, string schema, string table) =>
        kind == "sqlserver"
            ? $"daffy_scan({Literal(catalog)}, {Literal($"{Bracket(schema)}.{Bracket(table)}")})"
            : $"{catalog}.{Quote(schema)}.{Quote(table)}";

    private static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>
    /// A SQL Server mount's tables with their columns as SQL Server has them - type and collation -
    /// read once, from its own catalog, through daffy_scan: what decides which conditions on them
    /// may be sent to SQL Server (see DuckDbQueryHandler.Pushdown.cs). If they cannot be read, the
    /// tables mount all the same, and no condition on them is ever sent.
    /// </summary>
    private List<MountedObject> WithSqlServerColumns(List<MountedObject> objects, string catalog, Source source)
    {
        string Scan(string view, string? filter = null) =>
            $"daffy_scan({Literal(catalog)}, {Literal(view)}{(filter is null ? "" : $", filter := {Literal(filter)}")})";

        const string UserObjects = "type IN ('U', 'V') AND is_ms_shipped = 0";
        var found = new Dictionary<(string, string), Dictionary<string, SourceColumn>>(NameComparer.Instance);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT s.name, o.name, c.name, t.name, c.collation_name
                FROM {Scan("sys.columns", $"object_id IN (SELECT object_id FROM sys.objects WHERE {UserObjects})")} c
                JOIN {Scan("sys.objects", UserObjects)} o ON o.object_id = c.object_id
                JOIN {Scan("sys.schemas")} s ON s.schema_id = o.schema_id
                JOIN {Scan("sys.types", "user_type_id = system_type_id")} t ON t.user_type_id = c.system_type_id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!found.TryGetValue(key, out var columns))
                    found[key] = columns = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);
                string name = reader.GetString(2);
                columns[name] = new SourceColumn(name, reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }
        catch (DuckDBException ex)
        {
            Console.WriteLine($"[duckdb] warning: the columns of {source.Location} could not be read, so no condition on its " +
                              $"tables will be sent to it: {FirstLine(ex.Message)}");
            return objects;
        }

        return objects
            .Select(o => o with { Columns = found.GetValueOrDefault((o.SourceSchema!, o.SourceTable!)) })
            .ToList();
    }

    /// <summary>
    /// Every table and view in an attached database that a mount could make a view of. A database's
    /// own catalog schemas are left out, and of a MySQL server only the database that was named.
    /// SQL Server's are what daffy_tables lists: the tables and views it did not ship itself.
    /// </summary>
    private List<SourceTable> SourceTables(string catalog, Source source)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = source.Kind == "sqlserver"
            ? $"SELECT table_schema, table_name FROM daffy_tables({Literal(catalog)}) ORDER BY 1, 2"
            : $"""
            SELECT schema_name, table_name FROM duckdb_tables() WHERE database_name = {Literal(catalog)}
            UNION
            SELECT schema_name, view_name FROM duckdb_views() WHERE database_name = {Literal(catalog)} AND NOT internal
            ORDER BY 1, 2
            """;

        var tables = new List<SourceTable>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = new SourceTable(reader.GetString(0), reader.GetString(1));

            if (table.Schema is "information_schema" or "pg_catalog" || table.Schema.StartsWith("pg_", StringComparison.Ordinal))
                continue;
            if (source.Kind == "mysql" && !SameName(table.Schema, source.Home)) continue;

            tables.Add(table);
        }

        return tables;
    }

    /// <summary>
    /// Attaches a database read-only, under a name of DaffyDB's own, with its password - if it has
    /// one - in a secret of the same name. A DuckDB file another mount already has is shared: DuckDB
    /// will not open one file twice, and a file opened read-only cannot change under it. A SQL
    /// Server is named to DaffySQL instead, which logs in once to prove the connection string.
    /// </summary>
    private (string Catalog, string? Secret) Attach(Source source, List<string> notes)
    {
        if (source.Kind == "sqlserver") return (ConnectSqlServer(source, notes), null);

        if (source.Kind == "duckdb" &&
            _mounts.FirstOrDefault(m => m.Kind == "duckdb" && m.Catalog is not null &&
                                        string.Equals(m.SourceKey, source.Key, PathComparison)) is { } sharing)
            return (sharing.Catalog!, sharing.Secret);

        string catalog = $"daffy_{++_attachments}";
        string? secret = null;

        try
        {
            if (source.Password is not null)
            {
                secret = catalog;
                Execute($"CREATE SECRET {secret} (TYPE {source.Kind}, PASSWORD {Literal(source.Password)})");
            }

            string options = source.Kind == "duckdb"
                ? "READ_ONLY"
                : $"TYPE {source.Kind}{(secret is null ? "" : $", SECRET {secret}")}, READ_ONLY";

            Execute($"ATTACH {Literal(source.Attach)} AS {catalog} ({options})");
        }
        catch (DuckDBException ex)
        {
            Release(catalog, secret);
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber, $"could not open {source.Location}: {FirstLine(ex.Message)}");
        }

        return (catalog, secret);
    }

    /// <summary>
    /// A SQL Server, named to DaffySQL as daffy_N - one name, like an attached database's, for one
    /// mount - after logging in to it, so a wrong password or an unreachable server fails the
    /// command rather than the first query. What it says of itself goes to the log and the client:
    /// the server's version, the encryption, whether the certificate was checked. Never the password.
    /// </summary>
    private string ConnectSqlServer(Source source, List<string> notes)
    {
        string name = $"daffy_{++_attachments}";
        string described;
        try
        {
            described = DaffySql.Connect(name, source.Attach);
        }
        catch (Exception ex)
        {
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber, $"could not log in to {source.Location}: {FirstLine(ex.Message)}");
        }

        // DaffySQL starts a description with the name it was given, which is only DaffyDB's own.
        if (described.StartsWith(name + ": ", StringComparison.Ordinal)) described = described[(name.Length + 2)..];
        Console.WriteLine($"[duckdb] logged in to {described}");
        notes.Add($"Logged in to {described}.");
        return name;
    }

    /// <summary>Lets an attached database go, and its secret, once no mount is using it.</summary>
    private void Release(string catalog, string? secret)
    {
        if (_mounts.Any(m => m.Catalog == catalog)) return;

        // A SQL Server's name is DaffySQL's, not DuckDB's: forgetting it closes its idle sessions,
        // and the busy ones as their queries end.
        if (DaffySql.Disconnect(catalog)) return;

        try { Execute($"DETACH DATABASE IF EXISTS {catalog}"); }
        catch (DuckDBException ex) { Console.WriteLine($"[duckdb] warning: {catalog} was not detached: {FirstLine(ex.Message)}"); }

        if (secret is null) return;
        try { Execute($"DROP SECRET IF EXISTS {secret}"); }
        catch (DuckDBException) { }
    }

    /// <summary>
    /// Loads the extension a kind of database needs, downloading it the first time. It comes from
    /// DuckDB's own repository, signed, and is kept per DuckDB version in the user's extension folder,
    /// so it is never part of DaffyDB's own package.
    /// </summary>
    private void EnsureExtension(string kind, BatchContext context)
    {
        string extension = Extensions[kind];
        bool had = Scalar($"SELECT COUNT(*) FROM duckdb_extensions() WHERE extension_name = {Literal(extension)} AND installed") != "0";

        if (!had) Console.WriteLine($"[duckdb] downloading DuckDB's {kind} extension into {ExtensionFolder()}");

        try
        {
            Execute($"INSTALL {kind}");
            Execute($"LOAD {kind}");

            // The MySQL scanner hands COUNT and SUM to MySQL itself where it can, and through a
            // view - which is what every mounted table is - the result no longer binds: DuckDB
            // 1.5.5 fails with an internal error. Filters and ORDER BY are still pushed down;
            // only aggregates are left to DuckDB.
            if (kind == "mysql") Execute("SET GLOBAL mysql_aggregate_pushdown_enabled = false");
        }
        catch (DuckDBException ex)
        {
            throw DaffyCommands.Error(DaffyCommands.ExtensionNumber,
                $"daffydb.{kind} needs DuckDB's {kind} extension, which could not be {(had ? "loaded" : "downloaded")}: " +
                $"{FirstLine(ex.Message)} It downloads once, from DuckDB's own extension repository, into " +
                $"{ExtensionFolder()}; on a machine without internet access, put {extension}.duckdb_extension there by hand.");
        }

        if (!had) context.Messages.Add($"Downloaded DuckDB's {kind} extension, once, into {ExtensionFolder()}.");
    }

    private string ExtensionFolder()
    {
        string configured = Scalar("SELECT current_setting('extension_directory')");
        string root = configured.Length > 0
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".duckdb", "extensions");

        return Path.Combine(root, Scalar("SELECT version()"), Scalar("SELECT platform FROM pragma_platform()"));
    }

    // ---------------------------------------------------------------------------------------
    // Where a source is, from what the command was given

    private Source FileSource(string kind, string path)
    {
        string full;
        try
        {
            full = FullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw DaffyCommands.Error(DaffyCommands.BadCallNumber, $"'{path}' is not a path: {ex.Message}");
        }

        // Checked first, because attaching a file that is not there creates an empty database.
        if (!File.Exists(full))
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"there is no file at {full}. The path is on the machine DaffyDB runs on.");

        return new Source(kind, full, Password: null, full, Path.GetFileNameWithoutExtension(full), full, Home: "main");
    }

    /// <summary>
    /// A PostgreSQL or MySQL connection, given as key=value pairs or as a URL. The password is
    /// taken out of it; the database must be named, since naming it is the point.
    /// </summary>
    private static Source ServerSource(string kind, string text)
    {
        var command = DaffyCommands.Find(kind)!;
        var pairs = text.Contains("://") ? FromUrl(kind, text, command) : KeyValues(text, command);

        // Each kind's own spelling of the database, whichever the caller used.
        string database = kind == "postgres" ? "dbname" : "database";
        foreach (string alias in new[] { "dbname", "database", "db" })
            if (alias != database) Rename(pairs, alias, database);

        string? password = Take(pairs, "password") ?? Take(pairs, "passwd");
        string db = Get(pairs, database)
            ?? throw DaffyCommands.BadCall(command, $"name the database to mount, with {database}=... in @connection.");

        string host = Get(pairs, "host") ?? "localhost";
        string port = Get(pairs, "port") ?? (kind == "postgres" ? "5432" : "3306");
        string user = Get(pairs, "user") ?? Environment.UserName;
        string scheme = kind == "postgres" ? "postgresql" : "mysql";

        return new Source(kind, Format(pairs), password, $"{scheme}://{user}@{host}:{port}/{db}", db,
                          $"{host.ToLowerInvariant()}:{port}/{db}/{user}", Home: kind == "postgres" ? "public" : db);
    }

    /// <summary>
    /// A SQL Server connection string, as SqlClient takes one. DaffySQL reads it, and refuses a
    /// keyword it does not know, by name, rather than ignore it. The database must be named, since
    /// naming it is the point; the login's own default database is not something to mount by.
    /// </summary>
    private static Source SqlServerSource(string text)
    {
        var command = DaffyCommands.Find("sqlserver")!;

        SqlServerAddress address;
        try
        {
            address = DaffySql.Address(text);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // FormatException is DaffySQL's, naming the keyword; ArgumentException is the BCL's
            // parser, for text that is not key=value pairs at all.
            throw DaffyCommands.BadCall(command, $"@connection is a SQL Server connection string, and {ex.Message.TrimEnd('.')}.");
        }

        string db = address.Database
            ?? throw DaffyCommands.BadCall(command, "name the database to mount, with Database=... in @connection.");

        return new Source("sqlserver", text, Password: null, $"sqlserver://{address.User}@{address.Host}:{address.Port}/{db}", db,
                          $"{address.Host}:{address.Port}/{db}/{address.User}".ToLowerInvariant(), Home: "dbo");
    }

    /// <summary>
    /// libpq's key=value form, which DuckDB's MySQL extension reads too: pairs separated by spaces,
    /// a value in single quotes when it has spaces in it, with \' and \\ inside.
    /// </summary>
    private static List<KeyValuePair<string, string>> KeyValues(string text, DaffyCommand command)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        int i = 0;

        while (true)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;

            int start = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            string key = text[start..i];
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            if (key.Length == 0 || i >= text.Length || text[i] != '=')
                throw DaffyCommands.BadCall(command, "@connection is key=value pairs, such as host=localhost dbname=shop.");
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var value = new StringBuilder();
            if (i < text.Length && text[i] == '\'')
            {
                for (i++; i < text.Length && text[i] != '\''; i++)
                    value.Append(text[i] == '\\' && i + 1 < text.Length ? text[++i] : text[i]);

                if (i >= text.Length)
                    throw DaffyCommands.BadCall(command, $"the value of {key} in @connection has no closing quote.");
                i++;
            }
            else
            {
                for (; i < text.Length && !char.IsWhiteSpace(text[i]); i++)
                    value.Append(text[i] == '\\' && i + 1 < text.Length ? text[++i] : text[i]);
            }

            pairs.Add(new(key.ToLowerInvariant(), value.ToString()));
        }

        return pairs;
    }

    /// <summary>postgresql://user:password@host:port/database?key=value, and mysql:// the same.</summary>
    private static List<KeyValuePair<string, string>> FromUrl(string kind, string text, DaffyCommand command)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) ||
            !(kind == "postgres" ? url.Scheme is "postgresql" or "postgres" : url.Scheme == "mysql"))
            throw DaffyCommands.BadCall(command,
                $"@connection is a {(kind == "postgres" ? "postgresql" : "mysql")}:// URL or key=value pairs.");

        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) pairs.Add(new(key, value));
        }

        Add("host", url.Host);
        if (!url.IsDefaultPort && url.Port > 0) Add("port", url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string[] userInfo = url.UserInfo.Split(':', 2);
        Add("user", Uri.UnescapeDataString(userInfo[0]));
        if (userInfo.Length > 1) Add("password", Uri.UnescapeDataString(userInfo[1]));

        Add(kind == "postgres" ? "dbname" : "database", Uri.UnescapeDataString(url.AbsolutePath.Trim('/')));

        foreach (string part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            Add(Uri.UnescapeDataString(pair[0]).ToLowerInvariant(), pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "");
        }

        return pairs;
    }

    private static string Format(List<KeyValuePair<string, string>> pairs) =>
        string.Join(" ", pairs.Select(p => $"{p.Key}={FormatValue(p.Value)}"));

    private static string FormatValue(string value) =>
        value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '\'' or '\\')
            ? value
            : "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string? Get(List<KeyValuePair<string, string>> pairs, string key) =>
        pairs.LastOrDefault(p => p.Key == key) is { Key: not null } found && found.Value.Length > 0 ? found.Value : null;

    private static string? Take(List<KeyValuePair<string, string>> pairs, string key)
    {
        string? value = Get(pairs, key);
        pairs.RemoveAll(p => p.Key == key);
        return value;
    }

    private static void Rename(List<KeyValuePair<string, string>> pairs, string from, string to)
    {
        for (int i = 0; i < pairs.Count; i++)
            if (pairs[i].Key == from) pairs[i] = new(to, pairs[i].Value);
    }
}
