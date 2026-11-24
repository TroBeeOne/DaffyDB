using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// daffydb.save_parquet and daffydb.save_parquet_all: tables written out as parquet files, each by
/// one COPY through DuckDB's own parquet writer, from the table as it is at that moment.
///
/// Two things DuckDB's COPY does not do, and these do. COPY replaces a file that is already there
/// without a word, and OVERWRITE false does not stop it; so every name is checked first, and if
/// one is taken nothing is written. And a file is written under a name of its own and renamed into
/// place only once it is whole, by a rename that will not replace anything, so a failure or a
/// cancel part way leaves no half-written file where the table's should be.
///
/// EXPORT DATABASE, DuckDB's way of writing out a whole database, is not used: it writes data only
/// for real tables, and nearly everything mounted is a view.
/// </summary>
internal sealed partial class DuckDbQueryHandler
{
    /// <summary>
    /// One table to save: how T-SQL names it, what DuckDB reads it from, and the file it goes to.
    /// <paramref name="Schema"/> is null for a temp table, which has no schema a client can name.
    /// <paramref name="Renamed"/> says why the file is not named after the table exactly, when it is not.
    /// </summary>
    private sealed record SaveTarget(string? Schema, string Name, string Source, string File, string? Renamed)
    {
        public string Display => Schema is null ? Name : $"{Schema}.{Name}";
    }

    private const string ParquetExtension = ".parquet";

    /// <summary>
    /// The characters Windows refuses in a file name. They are refused on Linux too, which allows
    /// all but the slash, so that a folder saved on one can be copied to the other.
    /// </summary>
    private static readonly char[] ForbiddenInFileNames = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>Names Windows keeps for devices, which no file can have, with or without an extension.</summary>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private void SaveParquet(string table, string folder, string? file, BatchContext context)
    {
        var target = FindSavable(table);
        if (file is not null) target = target with { File = OwnFileName(file), Renamed = null };

        string full = SaveFolder(folder);
        CheckFiles(full, [target]);
        MakeFolder(full);

        long rows, bytes;
        try
        {
            (rows, bytes) = Save(target, full);
        }
        catch (DuckDBException ex)
        {
            StopIfCancelled();
            throw DaffyCommands.Error(Unsaved(ex, target, full), $"nothing was written, because {target.Display} {Failure(ex, target, full)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DaffyCommands.Error(DaffyCommands.FileTakenNumber,
                $"nothing was written, because {Path.Combine(full, target.File)} could not be put in place: {ex.Message}");
        }

        string path = Path.Combine(full, target.File);
        Console.WriteLine($"[duckdb] saved {target.Display,-40} -> {path}, {rows:N0} row(s)");

        if (target.Renamed is not null) context.Messages.Add($"{target.Display} is saved as {target.File}, because {target.Renamed}.");
        context.Messages.Add($"Saved {target.Display} to {path}: {Plural(rows, "row")}, {Size(bytes)}.");
        context.ResultSets.Add(SaveResults([Outcome(target, path, rows, bytes, Status(target))]));
    }

    /// <summary>
    /// Every table, view and temp table - or one mount's tables - each to a file of its own.
    ///
    /// The connection every session shares is let go between one table and the next, so other
    /// clients' queries are not held up for the whole of a long save, only for the table being
    /// written. Something they drop meanwhile is left out, as anything that cannot be read is.
    /// </summary>
    private void SaveParquetAll(string folder, string? mount, BatchContext context)
    {
        var targets = mount is null ? AllSavable() : MountSavable(mount);
        if (targets.Count == 0)
            throw DaffyCommands.Error(DaffyCommands.NotFoundNumber,
                "there is nothing to save: daffydb has no tables or views, and there are no temp tables.");

        string full = SaveFolder(folder);
        CheckFiles(full, targets);
        MakeFolder(full);

        var outcomes = new List<object?[]>();
        var leftOut = new List<string>();
        int saved = 0;
        long total = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            string path = Path.Combine(full, target.File);

            if (i > 0) LetOthersRun();
            StopIfCancelled();

            try
            {
                var (rows, bytes) = Save(target, full);
                saved++;
                total += bytes;

                Console.WriteLine($"[duckdb] saved {target.Display,-40} -> {path}, {rows:N0} row(s)");
                if (target.Renamed is not null) context.Messages.Add($"{target.Display} is saved as {target.File}, because {target.Renamed}.");
                outcomes.Add(Outcome(target, path, rows, bytes, Status(target)));
            }
            catch (Exception ex) when (ex is DuckDBException or IOException or UnauthorizedAccessException)
            {
                // A cancel interrupts the COPY, and is not a table that could not be read.
                StopIfCancelled();

                string why = ex is DuckDBException duck
                    ? Failure(duck, target, full)
                    : $"could not be put in place as {target.File}: {ex.Message}";

                Console.WriteLine($"[duckdb] warning: {target.Display} was not saved: it {why}");
                context.Messages.Add($"Left out {target.Display}, which {why}");
                leftOut.Add($"{target.Display} {why}");
                outcomes.Add(Outcome(target, path, null, null, "left out: it " + why));
            }
        }

        if (saved == 0)
            throw DaffyCommands.Error(DaffyCommands.UnreachableNumber,
                $"nothing was saved, because {string.Join("; ", leftOut)}");

        context.Messages.Add($"Saved {Plural(saved, "table")} to {full}, {Size(total)} in all." +
                             (leftOut.Count == 0 ? "" : $" {Plural(leftOut.Count, "table")} could not be saved."));
        context.ResultSets.Add(SaveResults(outcomes));
    }

    /// <summary>
    /// What @table names: orders or sales.orders in daffydb, as daffydb.tables lists them, or a
    /// temp table, #orders. tempdb..#orders and dbo.#orders name the same temp table, as they do
    /// on SQL Server.
    /// </summary>
    private SaveTarget FindSavable(string table)
    {
        var command = DaffyCommands.Find("save_parquet")!;
        var parts = NameParts(table);
        if (parts is null || parts.Count > 3 || parts[^1].Length == 0)
            throw DaffyCommands.BadCall(command, $"'{table}' is not the name of a table.");

        string name = parts[^1];
        if (name.StartsWith('#'))
        {
            var temps = TempSavable();
            string found = temps.FirstOrDefault(t => SameName(t, name))
                ?? throw DaffyCommands.Error(DaffyCommands.NotFoundNumber, temps.Count == 0
                       ? $"there is no temp table {name}: there are none."
                       : $"there is no temp table {name}. There are {Listed(temps)}.");
            return Temporary(found);
        }

        if (parts.Count == 3 && !SameName(parts[0], Databases.Data))
            throw DaffyCommands.Error(DaffyCommands.NotFoundNumber,
                $"{table} is not in {Databases.Data}: only {Databases.Data}'s tables and views, and temp tables, can be saved.");

        string schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : "dbo";
        var match = PersistentSavable().FirstOrDefault(t => SameName(t.Schema, schema) && SameName(t.Name, name));
        if (match.Name is null)
            throw DaffyCommands.Error(DaffyCommands.NotFoundNumber,
                $"there is no table or view {schema}.{name} in {Databases.Data}. EXEC daffydb.tables lists them.");

        return Persistent(match.Schema, match.Name);
    }

    private List<SaveTarget> AllSavable() =>
    [
        .. PersistentSavable().Select(t => Persistent(t.Schema, t.Name)),
        .. TempSavable().Select(Temporary),
    ];

    private List<SaveTarget> MountSavable(string name)
    {
        var mount = _mounts.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw DaffyCommands.Error(DaffyCommands.NotFoundNumber, _mounts.Count == 0
                ? $"there is no mount named '{name}': nothing is mounted."
                : $"there is no mount named '{name}'. The mounts are {string.Join(", ", _mounts.Select(m => $"'{m.Name}'"))}; " +
                  "daffydb.tables shows what each one holds.");

        return mount.Objects
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => Persistent(o.Schema, o.Name))
            .ToList();
    }

    /// <summary>Every table and view daffydb.tables lists, which is every one a client can see in daffydb.</summary>
    private List<(string Schema, string Name)> PersistentSavable()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, o.name
            FROM daffydb.sys.objects o
            JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id
            ORDER BY s.name, o.name
            """;

        var tables = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));
        return tables;
    }

    /// <summary>
    /// Every temp table, whichever session made it: they live in DuckDB's temp catalog, and every
    /// session shares the one connection that catalog belongs to. A temp view made through
    /// sp_duckdb is there too, and saved like one.
    /// </summary>
    private List<string> TempSavable() => Strings("""
        SELECT table_name FROM duckdb_tables() WHERE database_name = 'temp'
        UNION
        SELECT view_name FROM duckdb_views() WHERE database_name = 'temp' AND NOT internal
        ORDER BY 1
        """);

    private static SaveTarget Persistent(string schema, string name)
    {
        var (file, renamed) = FileNameFor($"{schema}.{name}");
        return new SaveTarget(schema, name, $"{Databases.Data}.{Quote(schema)}.{Quote(name)}", file, renamed);
    }

    /// <summary>A temp table's file is its name without the # - or the ## of a global one.</summary>
    private static SaveTarget Temporary(string name)
    {
        string bare = name.TrimStart('#');
        var (file, renamed) = FileNameFor(bare.Length > 0 ? bare : "temp");
        return new SaveTarget(null, name, $"temp.main.{Quote(name)}", file, renamed);
    }

    /// <summary>
    /// The file a table's name makes, with what no file name can hold put right: a character
    /// Windows refuses becomes _, and a device name has _ added. The dot between schema and table
    /// is what DaffyDB's own folder scan reads them back from.
    /// </summary>
    private static (string File, string? Renamed) FileNameFor(string stem)
    {
        var refused = stem.Where(c => char.IsControl(c) || ForbiddenInFileNames.Contains(c)).Distinct().ToList();
        string safe = new(stem.Select(c => refused.Contains(c) ? '_' : c).ToArray());
        string? why = refused.Count == 0
            ? null
            : $"{string.Join(", ", refused.Select(Shown))} cannot be in a file name";

        string first = safe.Split('.')[0];
        if (DeviceNames.Contains(first.TrimEnd()))
        {
            safe = first + "_" + safe[first.Length..];
            why = $"{first} is a name Windows keeps for a device";
        }

        return (safe + ParquetExtension, why);
    }

    private static string Shown(char c) => char.IsControl(c) ? $"character {(int)c}" : $"'{c}'";

    /// <summary>@file: a name of the caller's own choosing, in @folder, with .parquet added if it was left off.</summary>
    private static string OwnFileName(string file)
    {
        var command = DaffyCommands.Find("save_parquet")!;
        string name = file.Trim();

        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw DaffyCommands.BadCall(command, $"@file is a file name, not a path: '{file}'. The folder goes in @folder.");

        var refused = name.Where(c => char.IsControl(c) || ForbiddenInFileNames.Contains(c)).Distinct().ToList();
        if (refused.Count > 0)
            throw DaffyCommands.BadCall(command, $"@file cannot have {string.Join(", ", refused.Select(Shown))} in it: '{file}'.");

        if (name.Trim('.').Length == 0)
            throw DaffyCommands.BadCall(command, $"'{file}' is not a file name.");

        if (!name.EndsWith(ParquetExtension, StringComparison.OrdinalIgnoreCase)) name += ParquetExtension;

        string first = name.Split('.')[0];
        if (DeviceNames.Contains(first.TrimEnd()))
            throw DaffyCommands.BadCall(command, $"@file cannot be {first}, a name Windows keeps for a device.");

        return name;
    }

    private static string SaveFolder(string folder)
    {
        string full;
        try
        {
            full = FullPath(folder.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw DaffyCommands.Error(DaffyCommands.BadCallNumber, $"'{folder}' is not a path: {ex.Message}");
        }

        if (File.Exists(full))
            throw DaffyCommands.Error(DaffyCommands.UnwritableNumber,
                $"{full} is a file, not a folder. @folder is the folder the files go in, on the machine DaffyDB runs on.");

        return full;
    }

    /// <summary>
    /// Refuses, before anything is written, a save that would put two tables in one file or a file
    /// where one already is. The names are compared ignoring case, as Windows compares them.
    /// </summary>
    private static void CheckFiles(string folder, List<SaveTarget> targets)
    {
        var shared = targets
            .GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(t => t.Display))} would all be {g.Key}")
            .ToList();

        if (shared.Count > 0)
            throw DaffyCommands.Error(DaffyCommands.FileTakenNumber,
                $"nothing was written, because {string.Join("; ", shared)}. Save them one at a time with " +
                "daffydb.save_parquet, giving each a @file of its own.");

        var there = targets
            .Select(t => t.File)
            .Where(file => File.Exists(Path.Combine(folder, file)) || Directory.Exists(Path.Combine(folder, file)))
            .ToList();

        if (there.Count > 0)
            throw DaffyCommands.Error(DaffyCommands.FileTakenNumber,
                $"nothing was written, because {Listed(there)} {(there.Count == 1 ? "is" : "are")} already in {folder}, " +
                $"and DaffyDB never replaces a file. Save to another folder, or move {(there.Count == 1 ? "it" : "them")} first.");
    }

    private static void MakeFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw DaffyCommands.Error(DaffyCommands.UnwritableNumber, $"the folder {folder} could not be made: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes one table into a file of its own name in <paramref name="folder"/>, by way of a name
    /// nothing reads as a table - a folder scan takes only names ending in .parquet - so that the
    /// table's file appears whole or not at all. Returns the rows written and the file's size.
    /// </summary>
    private (long Rows, long Bytes) Save(SaveTarget target, string folder)
    {
        string final = Path.Combine(folder, target.File);
        string partial = $"{final}.{Guid.NewGuid().ToString("N")[..8]}.saving";

        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"COPY (SELECT * FROM {target.Source}) TO {Literal(partial.Replace('\\', '/'))} (FORMAT parquet)";
            long rows = command.ExecuteNonQuery();

            // Will not replace anything: a file that turned up at the name since it was checked is
            // left as it is, and this one goes instead.
            File.Move(partial, final, overwrite: false);
            return (rows, new FileInfo(final).Length);
        }
        finally
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[duckdb] warning: {partial} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// What went wrong with a COPY, in words. DuckDB names the file it could not open when the
    /// trouble was writing, and the table or file behind it when the trouble was reading.
    /// </summary>
    private static string Failure(DuckDBException ex, SaveTarget target, string folder) =>
        IsWriteFailure(ex, target)
            ? $"could not be written to {folder}: {FirstLine(ex.Message)}"
            : $"could not be read: {FirstLine(ex.Message)}";

    private static int Unsaved(DuckDBException ex, SaveTarget target, string folder) =>
        IsWriteFailure(ex, target) ? DaffyCommands.UnwritableNumber : DaffyCommands.UnreachableNumber;

    private static bool IsWriteFailure(DuckDBException ex, SaveTarget target) =>
        ex.Message.Contains(target.File + ".", StringComparison.OrdinalIgnoreCase) &&
        ex.Message.Contains(".saving", StringComparison.OrdinalIgnoreCase);

    private static string Status(SaveTarget target) =>
        target.Renamed is null ? "saved" : $"saved, renamed because {target.Renamed}";

    private static object?[] Outcome(SaveTarget target, string path, long? rows, long? bytes, string status) =>
        [target.Schema, target.Name, path, rows, bytes, status];

    private static ResultSet SaveResults(List<object?[]> rows)
    {
        ColumnDef[] columns =
        [
            new("schema", SqlColumnType.NVarChar, true),
            new("name", SqlColumnType.NVarChar, false),
            new("file", SqlColumnType.NVarChar, false),
            new("rows", SqlColumnType.BigInt, true),
            new("bytes", SqlColumnType.BigInt, true),
            new("status", SqlColumnType.NVarChar, false),
        ];

        return new ResultSet(columns, rows);
    }

    /// <summary>
    /// The parts of a T-SQL name - sales.orders, [Budget.2024].[Q1], tempdb..#t - unquoted, an empty
    /// part where two dots meet. Null when a bracket or quote is never closed.
    /// </summary>
    private static List<string>? NameParts(string name)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        string text = name.Trim();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '[' or '"')
            {
                char close = c == '[' ? ']' : '"';
                for (i++; ; i++)
                {
                    if (i >= text.Length) return null;
                    if (text[i] != close) { part.Append(text[i]); continue; }
                    if (i + 1 < text.Length && text[i + 1] == close) { part.Append(close); i++; continue; }
                    break;
                }
            }
            else if (c == '.')
            {
                parts.Add(part.ToString().Trim());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        parts.Add(part.ToString().Trim());
        return parts;
    }

    private static string Listed(List<string> names) =>
        string.Join(", ", names.Take(10)) + (names.Count > 10 ? $" and {names.Count - 10:N0} more" : "");

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
    };
}
