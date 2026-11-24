using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SmoProbe;

/// <summary>
/// Replays a captured SSMS conversation and checks each request against the expectation written
/// beside it in the capture file.
///
/// The SMO steps in <see cref="Program"/> are precise, but they only cover what somebody thought to
/// call. A capture covers what SSMS really sends - every Object Explorer folder, IntelliSense, the
/// query window - including the requests nobody would have thought to write down, and it is the
/// only record of the ones DaffyDB does not answer yet.
///
/// Each request carries the database SSMS sent it from, and is replayed from there: SSMS works in
/// several databases at once, on several connections, and a query about one database's tables
/// means something else in another.
///
/// A request marked ok must still run and return as many result sets as it did; one that does not
/// is a regression and fails the run. A request can also carry a "-- types" line, the SQL Server
/// type of each column of its first result set as LocalDB returns them, for the queries whose
/// values SSMS casts directly: a column that arrives as another type fails the run as well.
/// That is how "Specified cast is not valid" in the Columns folder is kept from coming back. A request marked as a gap is reported with the error it gets,
/// and one that starts working is announced rather than failed - that is progress, and --update
/// writes it down.
/// </summary>
internal static partial class Replay
{
    private sealed class Entry
    {
        public required int Number { get; init; }
        public required string Client { get; init; }
        public required string Expectation { get; set; }

        /// <summary>The database SSMS sent the request from, which the replay moves to first.</summary>
        public string? Database { get; set; }

        public List<string> ParameterLines { get; } = [];
        public List<(string Name, string Type, string? Value)> Parameters { get; } = [];
        public List<(string Name, string Type)> Types { get; } = [];
        public StringBuilder Sql { get; } = new();
    }

    public static async Task<int> RunAsync(string connectionString, string path, bool update)
    {
        var (preamble, entries) = Parse(File.ReadAllLines(path));
        Console.WriteLine($"replaying {entries.Count} requests from {path}\n");

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        int ok = 0, gaps = 0, nowWorking = 0, regressions = 0, unknown = 0;

        foreach (var entry in entries)
        {
            var (sets, error, types) = await ExecuteAsync(connection, entry);

            // A failed request can leave the connection closed; the next one deserves a fresh start.
            if (connection.State != ConnectionState.Open)
            {
                await connection.DisposeAsync();
                connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
            }

            string outcome = error is null ? $"ok, {sets} set{(sets == 1 ? "" : "s")}" : $"gap: {Shorten(error)}";
            string label = $"#{entry.Number,-3} {entry.Client,-16}";

            var wrongTypes = error is null
                ? entry.Types.Where(t => !types.Contains(t)).Select(t => DescribeType(t, types)).ToList()
                : [];

            if (entry.Expectation.StartsWith("ok"))
            {
                if (outcome == entry.Expectation && wrongTypes.Count > 0)
                {
                    regressions++;
                    Console.WriteLine($"FAIL   {label} {outcome}, but SSMS reads these as SQL Server sends them:");
                    foreach (string line in wrongTypes) Console.WriteLine($"         {line}");
                }
                else if (outcome == entry.Expectation) { ok++; Console.WriteLine($"ok     {label} {outcome}"); }
                else
                {
                    regressions++;
                    Console.WriteLine($"FAIL   {label} expected {entry.Expectation}, got {outcome}");
                }
            }
            else if (entry.Expectation.StartsWith("gap"))
            {
                if (error is null)
                {
                    nowWorking++;
                    Console.WriteLine($"NEW OK {label} {outcome}  (was {entry.Expectation})");
                }
                else
                {
                    gaps++;
                    Console.WriteLine($"gap    {label} {Shorten(error)}");
                }
            }
            else
            {
                unknown++;
                Console.WriteLine($"?      {label} {outcome}");
            }

            if (update) entry.Expectation = outcome;
        }

        await connection.DisposeAsync();

        Console.WriteLine("========================================================");
        Console.WriteLine($"{ok} ok, {gaps} known gaps, {nowWorking} newly working, {regressions} regressed" +
                          (unknown > 0 ? $", {unknown} without an expectation" : "") +
                          $" - {entries.Count} requests");

        if (update)
        {
            File.WriteAllText(path, Render(preamble, entries));
            Console.WriteLine($"expectations rewritten in {path}; review the diff before committing it");
            return 0;
        }

        if (nowWorking > 0) Console.WriteLine("run with --update to record the requests that now work");
        return regressions == 0 && unknown == 0 ? 0 : 1;
    }

    /// <summary>
    /// Sends a request the way SSMS did: with parameters it is the sp_executesql RPC SMO issues,
    /// declared with the same types; without, a plain batch. Every result set is read to the end.
    /// </summary>
    private static async Task<(int Sets, string? Error, List<(string Name, string Type)> Types)> ExecuteAsync(
        SqlConnection connection, Entry entry)
    {
        try
        {
            // SSMS spreads its requests over several connections, each in a database of its own, and
            // moves them about with USE. One connection replays them all, so it goes where each one
            // was sent from - which is itself a USE, and so part of what is being tested.
            if (entry.Database is not null && !string.Equals(connection.Database, entry.Database, StringComparison.OrdinalIgnoreCase))
                await connection.ChangeDatabaseAsync(entry.Database);

            await using var command = new SqlCommand(entry.Sql.ToString(), connection) { CommandTimeout = 15 };
            foreach (var (name, type, value) in entry.Parameters)
                command.Parameters.Add(Parameter(name, type, value));

            await using var reader = await command.ExecuteReaderAsync();
            int sets = 0;
            var types = new List<(string Name, string Type)>();
            do
            {
                if (reader.FieldCount > 0 && sets++ == 0)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        types.Add((reader.GetName(i), reader.GetDataTypeName(i)));
                }
                while (await reader.ReadAsync()) { }
            }
            while (await reader.NextResultAsync());

            return (sets, null, types);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return (0, ex.Message, []);
        }
    }

    private static string DescribeType((string Name, string Type) expected, List<(string Name, string Type)> actual)
    {
        var got = actual.FirstOrDefault(a => a.Name == expected.Name);
        return got.Name is null
            ? $"{expected.Name} is missing; SQL Server sends it as {expected.Type}"
            : $"{expected.Name} is {got.Type}; SQL Server sends it as {expected.Type}";
    }

    private static SqlParameter Parameter(string name, string type, string? value)
    {
        var match = TypePattern().Match(type);
        string baseType = match.Groups["base"].Value.ToLowerInvariant();

        var parameter = baseType switch
        {
            "int" => new SqlParameter(name, SqlDbType.Int) { Value = value is null ? DBNull.Value : int.Parse(value) },
            "bit" => new SqlParameter(name, SqlDbType.Bit) { Value = value is null ? DBNull.Value : value != "0" },
            "sysname" => new SqlParameter(name, SqlDbType.NVarChar, 128) { Value = (object?)value ?? DBNull.Value },
            _ => new SqlParameter(name, SqlDbType.NVarChar) { Value = (object?)value ?? DBNull.Value },
        };

        if (baseType == "nvarchar" && match.Groups["size"].Success)
            parameter.Size = match.Groups["size"].Value.Equals("max", StringComparison.OrdinalIgnoreCase)
                ? -1 : int.Parse(match.Groups["size"].Value);

        return parameter;
    }

    private static (List<string> Preamble, List<Entry> Entries) Parse(string[] lines)
    {
        var preamble = new List<string>();
        var entries = new List<Entry>();
        Entry? current = null;
        bool inSql = false;

        foreach (string line in lines)
        {
            if (current is null)
            {
                var header = HeaderPattern().Match(line);
                if (!header.Success)
                {
                    if (entries.Count == 0) preamble.Add(line);
                    continue;
                }

                current = new Entry
                {
                    Number = int.Parse(header.Groups["number"].Value),
                    Client = header.Groups["client"].Value,
                    Expectation = header.Groups["expect"].Value.Trim(),
                };
                entries.Add(current);
                inSql = false;
                continue;
            }

            if (line == "GO")
            {
                current = null;
                continue;
            }

            if (!inSql && line.StartsWith("-- in "))
            {
                current.ParameterLines.Add(line);
                current.Database = line["-- in ".Length..].Trim();
                continue;
            }

            if (!inSql && line.StartsWith("-- types "))
            {
                current.ParameterLines.Add(line);
                foreach (string pair in line["-- types ".Length..].Split(", "))
                {
                    int colon = pair.LastIndexOf(':');
                    current.Types.Add((pair[..colon], pair[(colon + 1)..]));
                }
                continue;
            }

            var parameter = inSql ? Match.Empty : ParameterPattern().Match(line);
            if (parameter.Success)
            {
                current.ParameterLines.Add(line);
                string raw = parameter.Groups["value"].Value;
                string? value = raw == "NULL" ? null
                    : raw.StartsWith("N'") ? raw[2..^1].Replace("''", "'")
                    : raw;
                current.Parameters.Add((parameter.Groups["name"].Value, parameter.Groups["type"].Value, value));
                continue;
            }

            inSql = true;
            if (current.Sql.Length > 0) current.Sql.Append('\n');
            current.Sql.Append(line);
        }

        return (preamble, entries);
    }

    private static string Render(List<string> preamble, List<Entry> entries)
    {
        var text = new StringBuilder();
        foreach (string line in preamble) text.Append(line).Append("\r\n");

        foreach (var entry in entries)
        {
            text.Append($"-- #{entry.Number}  {entry.Client}  {entry.Expectation}\r\n");
            foreach (string line in entry.ParameterLines) text.Append(line).Append("\r\n");
            text.Append(entry.Sql.ToString().Replace("\n", "\r\n")).Append("\r\n");
            text.Append("GO\r\n\r\n");
        }

        return text.ToString();
    }

    private static string Shorten(string message)
    {
        string first = message.Split('\n')[0].Trim();
        return first.Length <= 110 ? first : first[..107] + "...";
    }

    [GeneratedRegex(@"^-- #(?<number>\d+)  (?<client>.+?)  (?<expect>.+)$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^-- param (?<name>@\w+) (?<type>.+?) = (?<value>NULL|N'.*'|\S+)$")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^(?<base>\w+)(\((?<size>\w+)\))?")]
    private static partial Regex TypePattern();
}
