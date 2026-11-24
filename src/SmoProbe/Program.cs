using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace SmoProbe;

/// <summary>
/// Reproduces what SQL Server Management Studio does when it attaches to a server, without the GUI.
///
/// SSMS drives SMO, and the connect failure it reports ("Cannot find table 0") comes from
/// ServerInformation.GetServerInformation — the same call this probe makes. Running it against the
/// proxy shows exactly which queries SSMS needs answered: whatever appears in the proxy log just
/// before this fails is the next thing to implement.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        int port = 1433;
        string? replay = null;
        bool update = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--replay" when i + 1 < args.Length: replay = args[++i]; break;
                case "--update": update = true; break;
                default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2;
            }
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:127.0.0.1,{port}",
            Encrypt = false,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = 15,
            CommandTimeout = 15,
            UserID = "smo",
            Password = "smo",
        };

        // A captured conversation instead of the SMO steps: replay/object-explorer.sql.
        if (replay is not null) return await Replay.RunAsync(builder.ConnectionString, replay, update);

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        Console.WriteLine($"connected to {builder.DataSource}\n");

        var serverConnection = new ServerConnection(connection);
        var server = new Server(serverConnection);

        // Each of these is a distinct thing SSMS asks for while attaching. They are reported one at
        // a time so the first failure names the missing capability instead of a stack trace.
        Step("DatabaseEngineType", () => server.DatabaseEngineType);
        Step("DatabaseEngineEdition", () => server.DatabaseEngineEdition);
        Step("Version", () => server.Version);
        Step("Edition", () => server.Information.Edition);
        Step("ProductLevel", () => server.Information.ProductLevel);
        Step("Collation", () => server.Information.Collation);
        Step("Databases", () => Expect(
            string.Join(", ", server.Databases.Cast<Database>().Select(d => d.Name)),
            v => v.Contains("daffydb")));

        // These come from xp_msver via a temp table, which is the next thing SSMS does after
        // connecting.
        Step("Platform", () => server.Information.Platform);
        Step("Processors", () => server.Information.Processors);
        Step("PhysicalMemory", () => server.Information.PhysicalMemory);
        Step("Language", () => server.Information.Language);

        // Everything above is the connect handshake. Everything below is the object tree, which
        // Object Explorer builds with URN requests rather than the SMO collections - and it asks
        // for different fields, so the collections passing does not mean the tree does. Both are
        // checked, because it was exactly that difference that hid the gap.
        Console.WriteLine();

        // The Databases folder lists user databases with its own request, which reads the Always
        // On views as well; the system databases come from another. With those views missing, the
        // folder showed master and msdb and left daffydb out.
        Step("OE Databases", () => Expect(
            Enumerate(serverConnection, "Server/Database[@IsSystemObject=false()]",
                      ["Name", "Status", "Owner", "RecoveryModel", "MirroringStatus",
                       "AvailabilityDatabaseSynchronizationState"], "Name"),
            v => v.Contains("daffydb") && !v.Contains("master")));

        var database = server.Databases["daffydb"];

        Step("db.Tables", () => Expect(
            string.Join(", ", database.Tables.Cast<Table>().Select(t => $"{t.Schema}.{t.Name}")),
            v => v.Contains("dbo.customers") && v.Contains("dbo.orders")));

        Step("db.Views", () => string.Join(", ", database.Views.Cast<View>().Select(v => $"{v.Schema}.{v.Name}")));

        Step("db.Schemas", () => Expect(
            string.Join(", ", database.Schemas.Cast<Schema>().Select(x => x.Name)),
            v => v.Contains("dbo")));

        // IsSystemObject is how the tree splits Tables from System Tables, and reading it means
        // looking the object up in sys.extended_properties.
        // The seeded pair must be there and nothing may be flagged system, which is what decides
        // the Tables / System Tables split. The count is deliberately not asserted: --data-dir is
        // whatever the operator pointed it at, and one extra file there is not a protocol failure.
        Step("OE Tables", () => Expect(
            Enumerate(serverConnection, TableUrn, ["Name", "Schema", "IsSystemObject"], "Name"),
            v => v.Contains("customers,dbo,False") && v.Contains("orders,dbo,False")
                 && !v.Contains(",True")));

        // Empty and not an error: nothing served here is ms-shipped, so nothing qualifies.
        Step("OE System Tables", () => Expect(
            Enumerate(serverConnection, $"{TableUrn}[@IsSystemObject=true()]", ["Name"], "Name"),
            v => v.StartsWith("0:")));

        Step("OE Views", () => Enumerate(serverConnection, $"{DatabaseUrn}/View[@IsSystemObject=false()]",
                                         ["Name", "Schema"], "Name"));

        // A column with no type renders as a blank in the tree, so the type is asserted and not
        // just the column count: it only resolves if sys.columns hands out ids sys.types knows.
        Step("OE Columns", () => Expect(
            Enumerate(serverConnection, $"{TableUrn}[@Name='customers' and @Schema='dbo']/Column",
                      ["Name", "DataType", "Nullable", "InPrimaryKey"], "ID"),
            v => v.StartsWith("5:") && v.Contains("customer_id,int") && v.Contains("name,nvarchar")));

        // The first thing "Select Top 1000 Rows" reads. It loads every server property at once, from
        // a query that reads the registry, msdb's policy settings and the Always On cluster, and
        // needs all of it answered before it will script the SELECT.
        Step("ServerType", () => server.ServerType);

        // VS Code's Object Explorer loads every property of a database before it lists what is in
        // it, and every property of a table before it scripts "Select Top 1000 Rows"; SSMS asks for
        // a few. One missing view fails the whole load, and so does one value of the wrong type:
        // ReplicationOptions arrived as a decimal, which SMO cannot make an enum of. Each is loaded
        // into a fresh Server, so nothing read above answers for it.
        Step("db all properties", () =>
        {
            var fresh = new Server(serverConnection).Databases["daffydb"];
            fresh.Initialize(true);
            return Expect($"{fresh.ContainmentType}, {fresh.DefaultLanguage.Name}, {fresh.LastGoodCheckDbTime:yyyy-MM-dd}",
                v => v == "None, us_english, 1900-01-01");
        });
        Step("table all properties", () =>
        {
            var table = new Server(serverConnection).Databases["daffydb"].Tables["customers", "dbo"];
            table.Initialize(true);
            return Expect($"{table.Owner}, {table.TemporalType}, {table.ChangeTrackingEnabled}",
                v => v == "dbo, None, False");
        });

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static int _passed;
    private static int _failed;

    private static void Step(string name, Func<object?> read)
    {
        try
        {
            object? value = read();
            _passed++;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("PASS ");
            Console.ResetColor();
            Console.WriteLine($"{name,-22} {value}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("FAIL ");
            Console.ResetColor();
            Console.WriteLine($"{name,-22} {Innermost(ex).GetType().Name}: {FirstLine(Innermost(ex).Message)}");
        }
    }

    private const string DatabaseUrn = "Server/Database[@Name='daffydb']";
    private const string TableUrn = DatabaseUrn + "/Table";

    /// <summary>
    /// Runs the kind of request Object Explorer makes: a URN with an explicit field list, which is
    /// a different query from the one the matching SMO collection issues.
    /// </summary>
    private static string Enumerate(ServerConnection connection, string urn, string[] fields, string orderBy)
    {
        var request = new Request(new Urn(urn), fields)
        {
            OrderByList = [new OrderBy(orderBy, OrderBy.Direction.Asc)],
        };

        var table = (DataTable)new Enumerator().Process(connection, request).Data;
        return $"{table.Rows.Count}: " + string.Join(" | ", table.Rows.Cast<DataRow>()
            .Select(row => string.Join(",", table.Columns.Cast<DataColumn>().Select(c => row[c]))));
    }

    /// <summary>A query that answers without throwing can still answer wrongly, which is how both
    /// the collapsed schemas and the blank column types went unnoticed.</summary>
    private static string Expect(string value, Func<string, bool> holds) =>
        holds(value) ? value : throw new InvalidOperationException($"unexpected result: {value}");

    private static Exception Innermost(Exception ex) => ex.InnerException is null ? ex : Innermost(ex.InnerException);

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
