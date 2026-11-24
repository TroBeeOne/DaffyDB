using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace TdsProbe;

/// <summary>
/// Client-side milestone harness. Points a real Microsoft.Data.SqlClient at the proxy and runs
/// the phase-1 checks in order, stopping at the first failure.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        int port = 1433;
        bool integrated = false;
        bool stopOnFailure = true;
        string? adHocSql = null;
        string? procedure = null;
        var procedureParameters = new List<(string Name, string Value)>();
        string user = "spike", password = "spike";
        bool strict = false;
        bool mandatory = false;
        string? serverCertificate = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--integrated": integrated = true; break;
                case "--continue": stopOnFailure = false; break;
                case "--sql" when i + 1 < args.Length: adHocSql = args[++i]; break;
                case "--proc" when i + 1 < args.Length: procedure = args[++i]; break;
                case "--param" when i + 1 < args.Length:
                    string pair = args[++i];
                    int equals = pair.IndexOf('=');
                    if (equals < 1) { Console.Error.WriteLine($"--param wants @name=value, not {pair}"); return 1; }
                    procedureParameters.Add((pair[..equals], pair[(equals + 1)..]));
                    break;
                case "--user" when i + 1 < args.Length: user = args[++i]; break;
                case "--password" when i + 1 < args.Length: password = args[++i]; break;
                case "--strict": strict = true; break;
                case "--mandatory": mandatory = true; break;
                case "--server-cert" when i + 1 < args.Length: serverCertificate = args[++i]; strict = true; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    return 1;
            }
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:127.0.0.1,{port}",
            // Three transports, one flag. Optional leaves the connection in the clear, which is
            // what the proxy answers when no certificate is configured. Mandatory is TDS 7.4:
            // PRELOGIN in the clear, then TLS tunnelled through it and everything after encrypted.
            // Strict is TDS 8.0: TLS from byte zero, no PRELOGIN negotiation to downgrade.
            Encrypt = (strict, mandatory) switch
            {
                (true, _) => SqlConnectionEncryptOption.Strict,
                (_, true) => SqlConnectionEncryptOption.Mandatory,
                _ => SqlConnectionEncryptOption.Optional,
            },
            TrustServerCertificate = true,
            // Pooling would let a reused connection set the RESETCONNECTION status bit and send
            // sp_reset_connection, neither of which phase 1 handles.
            Pooling = false,
            ConnectTimeout = 15,
            CommandTimeout = 5,
        };

        if (integrated) builder.IntegratedSecurity = true;
        else { builder.UserID = user; builder.Password = password; }

        // Under Strict, pointing at the server's own .cer is the honest way to trust a self-signed
        // certificate: the client validates the chain against exactly that file.
        if (serverCertificate is not null) builder.ServerCertificate = serverCertificate;

        Console.WriteLine($"connection string: {builder.ConnectionString}");
        Console.WriteLine();

        // Ad-hoc mode runs one statement and prints what came back, which is how the verify
        // scripts query the proxy without a second client. --proc does the same for a stored
        // procedure called the way application code calls one: an RPC, CommandType.StoredProcedure.
        var checks = procedure is not null
            ? [("procedure call", (SqlConnection c) => RunAdHoc(c, procedure, procedureParameters))]
            : adHocSql is not null
            ? [("ad-hoc query", (SqlConnection c) => RunAdHoc(c, adHocSql))]
            : new List<(string Name, Func<SqlConnection, Task> Run)>
        {
            ("M4  batch: SELECT 1", BatchSelectOne),
            ("M6  reader: single int column", ReadSingleInt),
            ("M7  reader: typed columns and NULL", ReadTypedColumns),
            ("M5  rpc: parameterized command", ParameterizedCommand),
        };

        if (adHocSql is null && procedure is null && !integrated)
            checks.Add(("M3.5 unsupported input raises SqlException", UnsupportedSurfacesAsError));

        await using var connection = new SqlConnection(builder.ConnectionString);
        var stopwatch = Stopwatch.StartNew();

        // PRINT output and the like: what SSMS shows on its Messages tab.
        connection.InfoMessage += (_, e) => Console.WriteLine($"       info: {e.Message}");

        try
        {
            await connection.OpenAsync();
            Report("M3  connection opens", true, $"server {connection.ServerVersion}");
        }
        catch (Exception ex)
        {
            Report("M3  connection opens", false, $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return 1;
        }

        foreach (var (name, run) in checks)
        {
            try
            {
                await run(connection);
                Report(name, true, null);
            }
            catch (Exception ex)
            {
                // The number is how a client tells one refusal from another.
                Report(name, false, ex is SqlException sql
                    ? $"SqlException {sql.Number}: {FirstLine(ex.Message)}"
                    : $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
                foreach (string frame in (ex.StackTrace ?? "").Split('\n').Take(6))
                    Console.WriteLine($"         {frame.Trim()}");
                if (stopOnFailure) break;
            }
        }

        Console.WriteLine($"\n{_passed} passed, {_failed} failed in {stopwatch.ElapsedMilliseconds}ms");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs one statement or batch and prints every result set it produced. Walking all of them
    /// matters: a batch that returns two grids and stops after one looks healthy from the first.
    /// </summary>
    private static async Task RunAdHoc(SqlConnection connection, string sql,
                                       List<(string Name, string Value)>? procedureParameters = null)
    {
        await using var command = new SqlCommand(sql, connection);
        if (procedureParameters is not null)
        {
            command.CommandType = CommandType.StoredProcedure;
            foreach (var (name, value) in procedureParameters) command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync();

        int set = 0;
        do
        {
            if (reader.FieldCount == 0) continue;

            int rows = 0;
            Console.WriteLine($"       -- result set {++set} --");

            while (await reader.ReadAsync())
            {
                rows++;
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string value = reader.IsDBNull(i) ? "NULL" : Format(reader.GetValue(i));
                    Console.WriteLine($"       {reader.GetName(i),-14} {reader.GetFieldType(i)?.Name,-12} {value}");
                }
            }

            if (rows == 0) Console.WriteLine("       (no rows)");
        }
        while (await reader.NextResultAsync());
    }

    private static async Task BatchSelectOne(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT 1", connection);
        object? value = await command.ExecuteScalarAsync();
        Console.WriteLine($"       scalar = {Format(value)}");
    }

    private static async Task ReadSingleInt(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT CAST(42 AS int) AS n", connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) throw new InvalidOperationException("no rows returned");
        Console.WriteLine($"       {reader.GetName(0)} = {reader.GetValue(0)} ({reader.GetFieldType(0)?.Name})");
    }

    private static async Task ReadTypedColumns(SqlConnection connection)
    {
        const string sql = """
            SELECT CAST(7 AS int) AS n,
                   N'hello world' AS s,
                   CAST(1 AS bit) AS flag,
                   CAST(NULL AS int) AS missing
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) throw new InvalidOperationException("no rows returned");

        for (int i = 0; i < reader.FieldCount; i++)
        {
            string value = reader.IsDBNull(i) ? "NULL" : Format(reader.GetValue(i));
            Console.WriteLine($"       {reader.GetName(i),-12} {reader.GetFieldType(i)?.Name,-10} {value}");
        }

        int missing = reader.GetOrdinal("missing");
        if (!reader.IsDBNull(missing)) throw new InvalidOperationException("expected column 'missing' to be NULL");
    }

    /// <summary>
    /// Proxy-only: an unsupported request must arrive as a SqlException carrying the proxy's own
    /// message, not as a hang. A real server would simply execute this, so integrated runs skip it.
    ///
    /// The example has to be something still unsupported. It was an nvarchar(max) parameter until
    /// those were implemented; sql_variant is not, and nothing a client sends needs it.
    /// </summary>
    private static async Task UnsupportedSurfacesAsError(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT @anything AS b", connection);
        command.Parameters.Add("@anything", SqlDbType.Variant).Value = 42;

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }
            throw new InvalidOperationException("expected a SqlException from the proxy");
        }
        catch (SqlException ex)
        {
            Console.WriteLine($"       SqlException as expected: {FirstLine(ex.Message)}");
        }
    }

    private static async Task ParameterizedCommand(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT @year AS y, @label AS l", connection);
        command.Parameters.Add("@year", SqlDbType.Int).Value = 2025;
        command.Parameters.Add("@label", SqlDbType.NVarChar, 50).Value = "spike";

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("no rows returned");

        for (int i = 0; i < reader.FieldCount; i++)
            Console.WriteLine($"       {reader.GetName(i),-10} {Format(reader.GetValue(i))}");
    }

    private static void Report(string name, bool ok, string? detail)
    {
        if (ok) _passed++; else _failed++;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "PASS " : "FAIL ");
        Console.ForegroundColor = previous;
        Console.WriteLine(name);

        if (detail is not null) Console.WriteLine($"       {detail}");
    }

    private static string Format(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "",
    };

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
