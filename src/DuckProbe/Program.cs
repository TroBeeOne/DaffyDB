using System.Data;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DuckProbe;

/// <summary>
/// Sends T-SQL through the TDS proxy to the DuckDB handler and checks the rows that come back.
///
/// Every statement here is written the way a SQL Server client would write it — TOP, bracketed
/// identifiers, ISNULL, IIF, CONVERT, DATEADD, N'' literals — and none of it is valid DuckDB. The
/// assertions are against the fixed contents of the two parquet files.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        int port = 1433;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
                case "--verbose" or "-v": verbose = true; break;
                default: Console.Error.WriteLine($"unknown argument: {args[i]}"); return 1;
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
            UserID = "duck",
            Password = "duck",
        };

        Console.WriteLine($"connecting to {builder.DataSource}\n");

        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not connect: {ex.Message}");
            Console.WriteLine("is the proxy running?  dotnet run --project src/TdsProxy");
            return 1;
        }

        await Check(connection, verbose,
            "TOP + bracketed identifiers",
            "SELECT TOP 3 [customer_id], [name], [city] FROM customers ORDER BY [name]",
            rows =>
            {
                Expect(rows.Count == 3, $"expected 3 rows, got {rows.Count}");
                Expect((string)rows[0]["name"]! == "Ada Lovelace", "first row should be Ada Lovelace");
                Expect((string)rows[2]["name"]! == "Barbara Liskov", "third row should be Barbara Liskov");
            });

        await Check(connection, verbose,
            "ISNULL + LEN + CHARINDEX",
            """
            SELECT name,
                   ISNULL(city, 'unknown') AS city,
                   LEN(name) AS name_len,
                   CHARINDEX('Berners', name) AS berners_at
            FROM customers
            WHERE customer_id = 7
            """,
            rows =>
            {
                Expect(rows.Count == 1, $"expected 1 row, got {rows.Count}");
                Expect((string)rows[0]["city"]! == "unknown", "NULL city should become 'unknown'");
                Expect(System.Convert.ToInt32(rows[0]["name_len"]) == 15, "LEN('Tim Berners-Lee') should be 15");
                Expect(System.Convert.ToInt32(rows[0]["berners_at"]) == 5, "CHARINDEX('Berners', ...) should be 5");
            });

        await Check(connection, verbose,
            "join + GROUP BY + aggregates (BIGINT and DECIMAL)",
            """
            SELECT c.name,
                   COUNT(*) AS order_count,
                   SUM(o.quantity) AS units,
                   SUM(o.quantity * o.unit_price) AS total
            FROM orders o
            JOIN customers c ON c.customer_id = o.customer_id
            GROUP BY c.name
            ORDER BY total DESC
            """,
            rows =>
            {
                Expect(rows.Count == 7, $"expected 7 customers with orders, got {rows.Count}");
                Expect((string)rows[0]["name"]! == "Grace Hopper", "top spender should be Grace Hopper");
                Expect(System.Convert.ToInt64(rows[0]["order_count"]) == 5, "Grace Hopper should have 5 orders");
                // 1299.00 + 189.00 + (5 x 9.99) + (2 x 79.00) + 1299.00
                Expect(System.Convert.ToDecimal(rows[0]["total"]) == 2994.95m, "her total should be 2994.95");
                Expect(System.Convert.ToDecimal(rows[0]["units"]) == 10m, "she bought 10 units");
                Expect(rows[0]["order_count"] is long, "COUNT(*) should arrive as a 64-bit integer");
                Expect(rows[0]["total"] is decimal, "SUM of a DECIMAL should arrive as a decimal");
                // SUM over an INTEGER widens to DuckDB's 128-bit HUGEINT, carried as DECIMAL(38,0).
                Expect(rows[0]["units"] is decimal, "SUM of an INTEGER should arrive as a decimal");
            });

        await Check(connection, verbose,
            "parameterized command (RPC path)",
            """
            SELECT order_id, product, quantity
            FROM orders
            WHERE customer_id = @cust AND quantity >= @minQty
            ORDER BY order_id
            """,
            rows =>
            {
                Expect(rows.Count == 2, $"expected 2 rows, got {rows.Count}");
                Expect(System.Convert.ToInt32(rows[0]["order_id"]) == 9, "first order should be 9");
                Expect((string)rows[1]["product"]! == "Headset", "second product should be Headset");
            },
            ("@cust", SqlDbType.Int, 4), ("@minQty", SqlDbType.Int, 2));

        await Check(connection, verbose,
            "date functions: DATEPART, DATEADD, DATEDIFF",
            """
            SELECT TOP 3 order_id,
                   DATEPART(year, order_ts) AS yr,
                   DATEADD(day, 7, order_ts) AS plus_week,
                   DATEDIFF(day, CONVERT(date, '2024-01-01'), order_ts) AS days_in
            FROM orders
            ORDER BY order_id
            """,
            rows =>
            {
                Expect(rows.Count == 3, $"expected 3 rows, got {rows.Count}");
                Expect(System.Convert.ToInt32(rows[0]["yr"]) == 2024, "year should be 2024");
                Expect(((DateTime)rows[0]["plus_week"]!).Date == new DateTime(2024, 1, 12), "order 1 + 7 days is 2024-01-12");
                Expect(System.Convert.ToInt64(rows[0]["days_in"]) == 4, "order 1 is 4 days into 2024");
                Expect(System.Convert.ToInt64(rows[2]["days_in"]) == 32, "order 3 is 32 days into 2024");
            });

        await Check(connection, verbose,
            "IIF + CONVERT + N'' literals",
            """
            SELECT TOP 4 order_id,
                   product,
                   IIF(quantity > 2, N'bulk', N'single') AS order_size,
                   CONVERT(int, quantity * 10) AS qty_x10
            FROM orders
            ORDER BY order_id
            """,
            rows =>
            {
                Expect(rows.Count == 4, $"expected 4 rows, got {rows.Count}");
                Expect((string)rows[0]["order_size"]! == "single", "order 1 has quantity 2, so 'single'");
                Expect((string)rows[2]["order_size"]! == "bulk", "order 3 has quantity 3, so 'bulk'");
                Expect(System.Convert.ToInt32(rows[0]["qty_x10"]) == 20, "order 1 quantity * 10 is 20");
            });

        await Check(connection, verbose,
            "LEFT JOIN keeps the customer with no orders",
            """
            SELECT c.name, COUNT(o.order_id) AS order_count
            FROM customers c
            LEFT JOIN orders o ON o.customer_id = c.customer_id
            GROUP BY c.name
            HAVING COUNT(o.order_id) = 0
            """,
            rows =>
            {
                Expect(rows.Count == 1, $"expected 1 customer with no orders, got {rows.Count}");
                Expect((string)rows[0]["name"]! == "Margaret Hamilton", "that customer is Margaret Hamilton");
            });

        await Check(connection, verbose,
            "table hints + OFFSET/FETCH paging",
            """
            SELECT order_id, product
            FROM orders WITH (NOLOCK)
            ORDER BY order_id
            OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY
            """,
            rows =>
            {
                Expect(rows.Count == 3, $"expected 3 rows, got {rows.Count}");
                Expect(System.Convert.ToInt32(rows[0]["order_id"]) == 3, "paging should start at order 3");
                Expect((string)rows[2]["product"]! == "Dock", "third row should be the Dock");
            });

        await Check(connection, verbose,
            "string functions: REPLICATE, SPACE, STUFF, LEN, DATALENGTH, CHARINDEX(3-arg)",
            """
            SELECT REPLICATE('ab', 3) AS repeated,
                   LEN(SPACE(4) + 'x') AS padded_len,
                   STUFF('abcdef', 2, 3, 'XY') AS stuffed,
                   LEN('trailing   ') AS trimmed_len,
                   DATALENGTH('abc') AS byte_count,
                   CHARINDEX('a', 'banana', 3) AS a_from_third
            """,
            rows =>
            {
                Expect((string)rows[0]["repeated"]! == "ababab", "REPLICATE('ab', 3) should be ababab");
                Expect((string)rows[0]["stuffed"]! == "aXYef", "STUFF('abcdef',2,3,'XY') should be aXYef");
                Expect(System.Convert.ToInt64(rows[0]["trimmed_len"]) == 8, "LEN ignores trailing spaces");
                Expect(System.Convert.ToInt64(rows[0]["byte_count"]) == 3, "DATALENGTH('abc') should be 3");
                Expect(System.Convert.ToInt64(rows[0]["a_from_third"]) == 4, "CHARINDEX from position 3 should be 4");
            });

        await Check(connection, verbose,
            "numeric functions: SQUARE and the LOG/LOG10 trap",
            """
            SELECT SQUARE(7) AS squared,
                   ROUND(LOG(EXP(2)), 6) AS natural_log,
                   ROUND(LOG10(1000), 6) AS base_ten_log,
                   CEILING(4.2) AS rounded_up
            """,
            rows =>
            {
                Expect(System.Convert.ToDouble(rows[0]["squared"]) == 49, "SQUARE(7) should be 49");
                Expect(System.Convert.ToDouble(rows[0]["natural_log"]) == 2, "T-SQL LOG is the natural log");
                Expect(System.Convert.ToDouble(rows[0]["base_ten_log"]) == 3, "LOG10(1000) should be 3");
            });

        await Check(connection, verbose,
            "date functions: EOMONTH, DATENAME, DATEFROMPARTS",
            """
            SELECT EOMONTH(CONVERT(date, '2024-02-10')) AS end_of_feb,
                   DATENAME(month, CONVERT(date, '2024-02-10')) AS month_name,
                   DATEFROMPARTS(2024, 7, 4) AS built_date
            """,
            rows =>
            {
                Expect(((DateTime)rows[0]["end_of_feb"]!).Date == new DateTime(2024, 2, 29), "2024 is a leap year");
                Expect((string)rows[0]["month_name"]! == "February", "DATENAME(month, ...) should be February");
                Expect(((DateTime)rows[0]["built_date"]!).Date == new DateTime(2024, 7, 4), "DATEFROMPARTS should build 2024-07-04");
            });

        await Check(connection, verbose,
            "CHOOSE",
            "SELECT TOP 3 order_id, CHOOSE(quantity, N'one', N'two', N'three') AS qty_word FROM orders ORDER BY order_id",
            rows =>
            {
                Expect(rows.Count == 3, $"expected 3 rows, got {rows.Count}");
                Expect((string)rows[0]["qty_word"]! == "two", "order 1 has quantity 2");
                Expect((string)rows[1]["qty_word"]! == "one", "order 2 has quantity 1");
                Expect((string)rows[2]["qty_word"]! == "three", "order 3 has quantity 3");
            });

        await Check(connection, verbose,
            "CROSS APPLY becomes a lateral join",
            """
            SELECT c.name, x.units
            FROM customers c
            CROSS APPLY (SELECT SUM(o.quantity) AS units FROM orders o WHERE o.customer_id = c.customer_id) x
            WHERE c.customer_id = 1
            """,
            rows =>
            {
                Expect(rows.Count == 1, $"expected 1 row, got {rows.Count}");
                Expect(System.Convert.ToDecimal(rows[0]["units"]) == 7m, "Ada Lovelace bought 7 units");
            });

        await Check(connection, verbose,
            "server metadata functions",
            """
            SELECT @@VERSION AS version,
                   SERVERPROPERTY('ProductVersion') AS product_version,
                   SERVERPROPERTY('Edition') AS edition,
                   DB_NAME() AS database_name,
                   @@SPID AS spid
            """,
            rows =>
            {
                Expect(((string)rows[0]["version"]!).StartsWith("DaffyDB - 17.0.4025.3 (X64)"),
                       "@@VERSION's first line should be SQL Server's layout, with DaffyDB's name and SQL Server's version");
                Expect(((string)rows[0]["version"]!).Contains("DaffyDB") && ((string)rows[0]["version"]!).Contains("This is not SQL Server"),
                       "@@VERSION should say what is really answering on its second line");
                Expect((string)rows[0]["product_version"]! == "17.0.4025.3", "ProductVersion should be reported");
                Expect((string)rows[0]["database_name"]! == "daffydb", "DB_NAME() should be daffydb, where a login naming none lands");
            });

        await CheckMultiple(connection, verbose,
            "multiple result sets from one batch",
            "SELECT CAST(1 AS int) AS a; SELECT CAST(2 AS int) AS b, CAST(3 AS int) AS c",
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                Expect(sets[0].Count == 1 && sets[0][0].Count == 1, "first set should be one row of one column");
                Expect(sets[1][0].Count == 2, "second set should have two columns");
                Expect(System.Convert.ToInt32(sets[1][0]["c"]) == 3, "second set should carry c = 3");
            });

        await CheckMultiple(connection, verbose,
            "local variables drive a later query",
            """
            DECLARE @cust int
            SET @cust = 2
            SELECT order_id, product FROM orders WHERE customer_id = @cust ORDER BY order_id
            """,
            sets =>
            {
                Expect(sets.Count == 1, $"expected 1 result set, got {sets.Count}");
                Expect(sets[0].Count == 5, $"Grace Hopper has 5 orders, got {sets[0].Count}");
                Expect(System.Convert.ToInt32(sets[0][0]["order_id"]) == 4, "first order should be 4");
            });

        await CheckMultiple(connection, verbose,
            "declared type, initialiser, and a variable built from another",
            """
            DECLARE @label nvarchar(20) = 'top', @minQty int = 2;
            DECLARE @doubled int;
            SET @doubled = @minQty * 2;
            SELECT @label AS label, @minQty AS min_qty, @doubled AS doubled;
            SELECT COUNT(*) AS matching FROM orders WHERE quantity >= @doubled
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                Expect((string)sets[0][0]["label"]! == "top", "@label should be 'top'");
                Expect(System.Convert.ToInt32(sets[0][0]["doubled"]) == 4, "@doubled should be 4");
                // Orders with quantity >= 4: order 6 (5), 12 (4), 13 (10), 24 (6)
                Expect(System.Convert.ToInt64(sets[1][0]["matching"]) == 4, "4 orders have quantity >= 4");
            });

        await CheckMultiple(connection, verbose,
            "IF/ELSE picks a branch",
            """
            DECLARE @mode nvarchar(10) = 'bulk';
            IF @mode = 'bulk'
                SELECT COUNT(*) AS n FROM orders WHERE quantity > 2
            ELSE
                SELECT COUNT(*) AS n FROM orders WHERE quantity <= 2
            """,
            sets =>
            {
                Expect(sets.Count == 1, $"only the taken branch should run, got {sets.Count} result sets");
                // Orders with quantity > 2: 3, 6, 12, 13, 19, 24
                Expect(System.Convert.ToInt64(sets[0][0]["n"]) == 6, "6 orders have quantity > 2");
            });

        await CheckMultiple(connection, verbose,
            "IF/ELSE takes the ELSE branch, with BEGIN/END blocks",
            """
            DECLARE @mode nvarchar(10) = 'small';
            IF @mode = 'bulk'
            BEGIN
                SELECT 'wrong branch' AS which
            END
            ELSE
            BEGIN
                SELECT 'else branch' AS which;
                SELECT COUNT(*) AS n FROM customers
            END
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"the ELSE block has two statements, got {sets.Count} result sets");
                Expect((string)sets[0][0]["which"]! == "else branch", "should have taken the ELSE branch");
                Expect(System.Convert.ToInt64(sets[1][0]["n"]) == 8, "there are 8 customers");
            });

        await CheckMultiple(connection, verbose,
            "EXEC runs a string, and SELECT assigns a variable",
            """
            DECLARE @sql nvarchar(200);
            DECLARE @city nvarchar(50);
            SELECT @city = city FROM customers WHERE customer_id = 1;
            SET @sql = 'SELECT ''' + @city + ''' AS chosen_city';
            EXEC (@sql)
            """,
            sets =>
            {
                Expect(sets.Count == 1, $"expected 1 result set, got {sets.Count}");
                Expect((string)sets[0][0]["chosen_city"]! == "London", "customer 1 is in London");
            });

        await CheckMultiple(connection, verbose,
            "the sys catalog describes the mounted data",
            """
            SELECT o.name AS table_name, COUNT(c.name) AS column_count
            FROM daffydb.sys.objects o
            JOIN daffydb.sys.columns c ON c.object_id = o.object_id
            WHERE o.name IN ('customers', 'orders')
            GROUP BY o.name
            ORDER BY o.name
            """,
            sets =>
            {
                Expect(sets[0].Count >= 2, $"expected both tables, got {sets[0].Count}");
                Expect((string)sets[0][0]["table_name"]! == "customers", "first should be customers");
                Expect(System.Convert.ToInt64(sets[0][0]["column_count"]) == 5, "customers has 5 columns");
            });

        // Exactly the shape SSMS uses to read server facts on connect.
        await CheckMultiple(connection, verbose,
            "temp table + INSERT ... EXEC xp_msver",
            """
            create table #SVer(ID int, Name sysname, Internal_Value int, Value nvarchar(512))
            insert #SVer exec master.dbo.xp_msver
            select Name, Value from #SVer where Name in ('ProductName','Platform') order by ID
            drop table #SVer
            """,
            sets =>
            {
                Expect(sets.Count == 1, $"only the SELECT should return rows, got {sets.Count} result sets");
                Expect(sets[0].Count == 2, $"expected 2 rows, got {sets[0].Count}");
                Expect((string)sets[0][0]["Value"]! == "DaffyDB", "first row is the product name");
                Expect((string)sets[0][1]["Value"]! == "NT x64", "second row is the platform");
            });

        // sp_server_info in its own shape, not xp_msver's, and one attribute when asked for one.
        await CheckMultiple(connection, verbose,
            "sp_server_info",
            "EXEC sp_server_info; EXEC sp_server_info 2",
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                Expect(sets[0].Count == 29, $"expected 29 attributes, got {sets[0].Count}");
                Expect(string.Join(",", sets[0][0].Keys) == "attribute_id,attribute_name,attribute_value",
                       $"columns are {string.Join(",", sets[0][0].Keys)}");
                Expect((string)sets[0][0]["attribute_name"]! == "DBMS_NAME" && (string)sets[0][0]["attribute_value"]! == "DaffyDB",
                       $"first attribute is {sets[0][0]["attribute_name"]} = {sets[0][0]["attribute_value"]}");
                Expect(sets[1].Count == 1 && System.Convert.ToInt32(sets[1][0]["attribute_id"]) == 2,
                       $"@attribute_id = 2 should return that one row, got {sets[1].Count}");
                Expect((string)sets[1][0]["attribute_value"]! == "DaffyDB - 17.0.4025.3",
                       $"DBMS_VER is {sets[1][0]["attribute_value"]}");
            });

        // The shape Object Explorer uses to build the server node: hex literals, integer division
        // to take @@microsoftversion apart, QUOTENAME, and string concatenation with +.
        await CheckMultiple(connection, verbose,
            "hex literals, integer division and QUOTENAME",
            """
            SELECT CAST(serverproperty(N'Servername') AS sysname) AS [Server_Name],
                   'Server[@Name=' + quotename(CAST(serverproperty(N'Servername') AS sysname),'''') + ']' AS [Server_Urn],
                   CAST(0x0001 AS int) AS [Server_Status],
                   (@@microsoftversion / 0x1000000) & 0xff AS [VersionMajor],
                   (@@microsoftversion / 0x10000) & 0xff AS [VersionMinor],
                   @@microsoftversion & 0xffff AS [BuildNumber]
            """,
            sets =>
            {
                var row = sets[0][0];
                Expect((string)row["Server_Urn"]! == "Server[@Name='DAFFY_PROXY_DUCKDB']", "QUOTENAME should wrap in quotes");
                Expect(System.Convert.ToInt32(row["Server_Status"]) == 1, "0x0001 should be 1");
                Expect(System.Convert.ToInt32(row["VersionMajor"]) == 17, "major version should be 17");
                Expect(System.Convert.ToInt32(row["VersionMinor"]) == 0, "minor version should be 0");
                Expect(System.Convert.ToInt32(row["BuildNumber"]) == 4025, "build should be 4025");
            });

        await CheckMultiple(connection, verbose,
            "catalog and permission functions",
            """
            SELECT has_dbaccess('msdb') AS db_access,
                   CASE WHEN object_id('dbo.sysdac_instances') IS NOT NULL THEN 1 ELSE 0 END AS has_dac,
                   object_name(object_id('orders')) AS orders_name,
                   is_srvrolemember('sysadmin') AS is_admin,
                   DATABASEPROPERTYEX('master', 'Status') AS db_status
            """,
            sets =>
            {
                var row = sets[0][0];
                Expect(System.Convert.ToInt32(row["db_access"]) == 1, "the single user has access to everything");
                // OBJECT_ID answers NULL for something that does not exist, which is how a client
                // asks whether a table is there.
                Expect(System.Convert.ToInt32(row["has_dac"]) == 0, "sysdac_instances does not exist");
                Expect((string)row["orders_name"]! == "orders", "OBJECT_NAME should round-trip OBJECT_ID");
                Expect((string)row["db_status"]! == "ONLINE", "the database reports as online");
            });

        // The select-list prefix has to survive: without it "@v = c.name" reads as a comparison,
        // the variable stays NULL, and the batch quietly does the wrong thing.
        await CheckMultiple(connection, verbose,
            "SELECT TOP 1 @var = ... assigns rather than compares",
            """
            DECLARE @top_customer nvarchar(50);
            SELECT TOP 1 @top_customer = c.name
            FROM orders o
            JOIN customers c ON c.customer_id = o.customer_id
            GROUP BY c.name
            ORDER BY SUM(o.quantity * o.unit_price) DESC;

            IF @top_customer IS NOT NULL
            BEGIN
                SELECT @top_customer AS best_customer;
                SELECT COUNT(*) AS their_orders
                FROM orders o
                JOIN customers c ON c.customer_id = o.customer_id
                WHERE c.name = @top_customer;
            END;
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"the IF should have run both statements, got {sets.Count} result sets");
                Expect((string)sets[0][0]["best_customer"]! == "Grace Hopper", "the top spender is Grace Hopper");
                Expect(System.Convert.ToInt64(sets[1][0]["their_orders"]) == 5, "she placed 5 orders");
            });

        // T-SQL assigns once per row, so the last row the query returns is the one that sticks.
        await Check(connection, verbose,
            "SELECT @var = ... over many rows keeps the last",
            """
            DECLARE @name nvarchar(50);
            SELECT @name = name FROM customers ORDER BY customer_id;
            SELECT @name AS last_name_seen;
            """,
            rows => Expect((string)rows[0]["last_name_seen"]! == "Margaret Hamilton",
                           $"expected customer 8, got {rows[0]["last_name_seen"]}"));

        await CheckMultiple(connection, verbose,
            "BEGIN TRY / BEGIN CATCH handles an error",
            """
            BEGIN TRY
                SELECT COUNT(*) AS ran_first FROM orders;
                SELECT * FROM no_such_table;
                SELECT 'unreachable' AS never;
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS number, ERROR_LINE() AS line, ERROR_MESSAGE() AS message;
            END CATCH
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"the statement after the error must not run, got {sets.Count} result sets");
                Expect(System.Convert.ToInt64(sets[0][0]["ran_first"]) == 24, "the TRY block's first result survives");
                Expect(System.Convert.ToInt32(sets[1][0]["number"]) == 50000, "errors report as user errors");
                Expect(System.Convert.ToInt32(sets[1][0]["line"]) == 3, "ERROR_LINE points at the failing statement");
                Expect(((string)sets[1][0]["message"]!).Contains("no_such_table"), "the message names the table");
            });

        await CheckMultiple(connection, verbose,
            "a TRY block that succeeds skips its CATCH",
            """
            BEGIN TRY
                SELECT COUNT(*) AS customers FROM customers;
            END TRY
            BEGIN CATCH
                SELECT 'the catch block ran' AS wrong;
            END CATCH
            SELECT ERROR_MESSAGE() AS outside_any_catch;
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"expected the TRY result and one more, got {sets.Count}");
                Expect(System.Convert.ToInt64(sets[0][0]["customers"]) == 8, "all 8 customers");
                Expect(sets[1][0]["outside_any_catch"] is null, "ERROR_MESSAGE is NULL outside a CATCH block");
            });

        await Check(connection, verbose,
            "THROW raises an error a CATCH block can read",
            """
            BEGIN TRY
                THROW 51000, 'not enough widgets', 7;
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS number, ERROR_STATE() AS state, ERROR_MESSAGE() AS message;
            END CATCH
            """,
            rows =>
            {
                Expect(System.Convert.ToInt32(rows[0]["number"]) == 51000, "THROW's own number is reported");
                Expect(System.Convert.ToInt32(rows[0]["state"]) == 7, "THROW's own state is reported");
                Expect((string)rows[0]["message"]! == "not enough widgets", "THROW's own message is reported");
            });

        await CheckError(connection,
            "a bare THROW rethrows out of the CATCH block",
            """
            BEGIN TRY
                THROW 51001, 'inner failure', 3;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """);

        await CheckError(connection,
            "an uncaught RAISERROR reaches the client",
            "RAISERROR('the widget is out of widgets', 16, 42)");

        // The context functions answer from the connection that asked, not from a constant. Note
        // the deliberate split: SUSER_SNAME is the login, USER_NAME is the database user.
        await Check(connection, verbose,
            "identity functions report the live session",
            """
            SELECT SUSER_SNAME()   AS login,
                   ORIGINAL_LOGIN() AS original,
                   HOST_NAME()     AS host,
                   APP_NAME()      AS app,
                   USER_NAME()     AS db_user,
                   DB_NAME()       AS db
            """,
            rows =>
            {
                var row = rows[0];
                Expect((string)row["login"]! == "duck", $"the probe logs in as duck, got {row["login"]}");
                Expect((string)row["original"]! == "duck", "ORIGINAL_LOGIN matches the login");
                Expect(((string)row["host"]!).Length > 0, "HOST_NAME is the real client host");
                Expect(((string)row["app"]!).Contains("SqlClient"), $"APP_NAME is the real client, got {row["app"]}");
                Expect((string)row["db_user"]! == "dbo", "USER_NAME is the database user, which stays dbo");
                Expect((string)row["db"]! == "daffydb", "DB_NAME is the connected database");
            });

        // System procedures are reached by EXEC however the name is qualified, and the ones DaffyDB
        // lacks fail with SQL Server's own number and wording - which the ODBC driver, for one,
        // recognises and reports in its own words - rather than with a DuckDB parser error.
        await Check(connection, verbose,
            "EXEC reaches a system procedure however it is qualified",
            "EXEC [master].[dbo].sp_databases",
            rows => Expect(string.Join(", ", rows.Select(r => r["DATABASE_NAME"])) == "master, msdb, daffydb",
                           "sp_databases lists master, msdb and daffydb"));

        // Catalog flags are SQL Server's bit: SSMS casts a selected flag straight to bool, and an
        // integer there failed every column label with "Specified cast is not valid". They still
        // have to add up, because Object Explorer does arithmetic on them.
        await Check(connection, verbose,
            "catalog flags arrive as bit and still add up",
            """
            SELECT TOP 1 c.is_nullable, c.is_computed,
                   (SELECT TOP 1 i.is_primary_key + 2*i.is_unique_constraint FROM sys.indexes i) AS key_kind
            FROM sys.columns c
            """,
            rows =>
            {
                Expect(rows[0]["is_nullable"] is bool && rows[0]["is_computed"] is bool,
                       $"flags are bool, got {rows[0]["is_nullable"]?.GetType().Name} / {rows[0]["is_computed"]?.GetType().Name}");
                Expect(System.Convert.ToInt32(rows[0]["key_kind"]) == 0, $"a heap is neither key kind, got {rows[0]["key_kind"]}");
            });

        // Variables are numbered before functions are rewritten, and SUSER_SNAME(...) is replaced
        // whole - the variable inside it with it. The ones after it used to bind one place off.
        await Check(connection, verbose,
            "a variable swallowed by a rewritten function does not shift the others",
            """
            DECLARE @swallowed nvarchar(10) = N'x';
            DECLARE @kept int = 42;
            SELECT ISNULL(suser_sname(sid_binary(ISNULL(@swallowed, N''))), N'') AS who, @kept AS kept
            """,
            rows => Expect(System.Convert.ToInt32(rows[0]["kept"]) == 42, $"@kept is 42, got {rows[0]["kept"]}"));

        await Check(connection, verbose,
            "an unknown SERVERPROPERTY is NULL, as in SQL Server",
            "SELECT SERVERPROPERTY('NoSuchProperty') AS p, CAST(ISNULL(SERVERPROPERTY('IsLocalDB'), 0) AS bit) AS localdb",
            rows =>
            {
                Expect(rows[0]["p"] is null, $"expected NULL, got '{rows[0]["p"]}'");
                Expect(rows[0]["localdb"] is false, $"DaffyDB is not LocalDB, got {rows[0]["localdb"]}");
            });

        // The catalog procedures behind ODBC's SQLTables and SQLColumns. Their arguments are LIKE
        // patterns, and [o] is exactly what DuckDB's own LIKE cannot express.
        await Check(connection, verbose,
            "sp_tables finds tables by a T-SQL LIKE pattern",
            "EXEC sp_tables N'cust[o]mer%', N'dbo', NULL, N'''TABLE'',''VIEW'''",
            rows =>
            {
                Expect(rows.Count == 1, $"expected just customers, got {rows.Count} rows");
                Expect((string)rows[0]["TABLE_NAME"]! == "customers" && (string)rows[0]["TABLE_TYPE"]! == "TABLE",
                       $"got {rows[0]["TABLE_NAME"]} as {rows[0]["TABLE_TYPE"]}");
            });

        await Check(connection, verbose,
            "sp_columns_100 describes columns as SQL Server does",
            "EXEC [daffydb].[sys].sp_columns_100 N'orders', N'dbo', NULL, NULL, @ODBCVer = 3, @fUsePattern = 1",
            rows =>
            {
                Expect(rows.Count == 6, $"orders has six columns, got {rows.Count}");
                var price = rows.Single(r => (string)r["COLUMN_NAME"]! == "unit_price");
                Expect((string)price["TYPE_NAME"]! == "decimal" && System.Convert.ToInt32(price["PRECISION"]) == 10
                       && System.Convert.ToInt16(price["SCALE"]) == 2,
                       $"unit_price is decimal(10,2), got {price["TYPE_NAME"]}({price["PRECISION"]},{price["SCALE"]})");
                var id = rows.Single(r => (string)r["COLUMN_NAME"]! == "order_id");
                Expect(System.Convert.ToInt16(id["DATA_TYPE"]) == 4 && System.Convert.ToInt32(id["PRECISION"]) == 10,
                       $"order_id is SQL_INTEGER with precision 10, got {id["DATA_TYPE"]} / {id["PRECISION"]}");
            });

        await Check(connection, verbose,
            "an unknown procedure fails as it does in SQL Server",
            """
            BEGIN TRY
                EXEC [sys].sp_no_such_thing N'orders', @ODBCVer = 3
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS number, ERROR_MESSAGE() AS message
            END CATCH
            """,
            rows =>
            {
                Expect(System.Convert.ToInt32(rows[0]["number"]) == 2812, $"expected error 2812, got {rows[0]["number"]}");
                Expect((string)rows[0]["message"]! == "Could not find stored procedure 'sp_no_such_thing'.",
                       $"SQL Server's wording, got {rows[0]["message"]}");
            });

        await Check(connection, verbose,
            "procedure arguments bind as SQL Server binds them",
            """
            BEGIN TRY
                EXEC sp_databases N'one too many'
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS number, ERROR_MESSAGE() AS message
            END CATCH
            """,
            rows => Expect(System.Convert.ToInt32(rows[0]["number"]) == 8144,
                           $"a surplus argument is error 8144, got {rows[0]["number"]}: {rows[0]["message"]}"));

        // A value too long for a plain nvarchar column goes out as nvarchar(max). It used to be cut
        // at 4000 characters without any error at all, which an import would never notice.
        await Check(connection, verbose,
            "a string longer than 4000 characters arrives whole",
            "SELECT k, s FROM (VALUES (1, REPLICATE(N'ab', 6000)), (2, NULL), (3, N'')) v(k, s) ORDER BY k",
            rows =>
            {
                string whole = (string)rows[0]["s"]!;
                Expect(whole.Length == 12000, $"expected 12000 characters, got {whole.Length}");
                Expect(whole.EndsWith("abab"), "the end of the value arrived too");
                Expect(rows[1]["s"] is null, "a NULL in a (max) column stays NULL");
                Expect((string)rows[2]["s"]! == "", "an empty string in a (max) column stays empty");
            });

        // The same limits on the way in. SqlClient sends a string parameter over 4000 characters as
        // nvarchar(max), in PLP chunks; an explicit ntext parameter in the legacy long format; and -
        // the case SSMS depends on - an sp_executesql statement over 4000 characters as NTEXT, which
        // is how SMO's query for a table's columns arrives when Object Explorer expands them.
        await Check(connection, verbose,
            "a parameter longer than 4000 characters arrives whole",
            "SELECT LEN(@long) AS n, RIGHT(@long, 3) AS tail",
            rows =>
            {
                Expect(System.Convert.ToInt32(rows[0]["n"]) == 5000, $"expected 5000 characters, got {rows[0]["n"]}");
                Expect((string)rows[0]["tail"]! == "xyz", "the end of the value arrived too");
            },
            ("@long", SqlDbType.NVarChar, new string('a', 4997) + "xyz"));

        await Check(connection, verbose,
            "an ntext parameter is read",
            "SELECT @legacy AS legacy",
            rows => Expect((string)rows[0]["legacy"]! == "old-style text", $"got {rows[0]["legacy"]}"),
            ("@legacy", SqlDbType.NText, "old-style text"));

        await Check(connection, verbose,
            "a parameterised statement longer than 4000 characters runs",
            "SELECT @name AS name /* " + new string('-', 4500) + " */",
            rows => Expect((string)rows[0]["name"]! == "orders", $"got {rows[0]["name"]}"),
            ("@name", SqlDbType.NVarChar, "orders"));

        // BLOB values used to reach the client as the text "System.IO.UnmanagedMemoryStream".
        await Check(connection, verbose,
            "binary values arrive as bytes, long ones whole",
            "SELECT CAST('ab' AS VARBINARY(10)) AS small, CAST(REPLICATE('ab', 5000) AS VARBINARY(MAX)) AS big",
            rows =>
            {
                Expect(rows[0]["small"] is byte[] { Length: 2 } small && small[0] == 0x61 && small[1] == 0x62,
                       $"expected 0x6162, the bytes of 'ab', got {Render(rows[0]["small"])}");
                Expect(rows[0]["big"] is byte[] { Length: 10000 }, $"expected 10000 bytes, got {Render(rows[0]["big"])}");
            });

        // --- Databases: the data lives in daffydb, and master holds only the system catalog ------

        await Check(connection, verbose,
            "DB_ID and DB_NAME know every database",
            "SELECT DB_ID() AS here, DB_ID('master') AS master_id, DB_NAME(5) AS five, DB_NAME(1) AS one, DB_ID('nosuch') AS missing",
            rows =>
            {
                var row = rows[0];
                Expect(System.Convert.ToInt32(row["here"]) == 5, $"DB_ID() is daffydb's 5, got {row["here"]}");
                Expect(System.Convert.ToInt32(row["master_id"]) == 1, $"master is database 1, got {row["master_id"]}");
                Expect((string)row["five"]! == "daffydb" && (string)row["one"]! == "master",
                       $"DB_NAME(5) and DB_NAME(1) are {row["five"]} and {row["one"]}");
                Expect(row["missing"] is null, "DB_ID of a database that does not exist is NULL");
            });

        await Check(connection, verbose,
            "USE moves the session to master, and the client is told",
            "USE master; SELECT DB_NAME() AS db, COUNT(*) AS objects FROM sys.objects",
            rows =>
            {
                Expect((string)rows[0]["db"]! == "master", $"DB_NAME() after USE master is {rows[0]["db"]}");
                Expect(System.Convert.ToInt64(rows[0]["objects"]) == 0, "master holds no tables");
                Expect(connection.Database == "master", $"SqlConnection.Database should follow, got {connection.Database}");
            });

        await Check(connection, verbose,
            "USE [daffydb] moves it back",
            "USE [daffydb]; SELECT DB_NAME() AS db, COUNT(*) AS objects FROM sys.objects",
            rows =>
            {
                Expect((string)rows[0]["db"]! == "daffydb", $"DB_NAME() after USE [daffydb] is {rows[0]["db"]}");
                Expect(System.Convert.ToInt64(rows[0]["objects"]) >= 2, "the tables are back");
                Expect(connection.Database == "daffydb", $"SqlConnection.Database should follow, got {connection.Database}");
            });

        await CheckError(connection,
            "USE of a database that does not exist is error 911",
            "USE nosuch",
            number: 911);

        await CheckLogin(builder, "master",
            "a login naming master starts there, with the data a three-part name away",
            async inMaster =>
            {
                Expect(inMaster.Database == "master", $"SqlConnection.Database is {inMaster.Database}");
                var row = (await Rows(inMaster,
                    "SELECT DB_NAME() AS db, (SELECT COUNT(*) FROM sys.objects) AS here, " +
                    "(SELECT COUNT(*) FROM daffydb.dbo.orders) AS orders"))[0];
                Expect((string)row["db"]! == "master", $"DB_NAME() is {row["db"]}");
                Expect(System.Convert.ToInt64(row["here"]) == 0, "master holds no tables");
                Expect(System.Convert.ToInt64(row["orders"]) > 0, "daffydb.dbo.orders is reachable from master");
            });

        await CheckLogin(builder, "master",
            "a procedure named with a database runs there, and the caller stays put",
            async inMaster =>
            {
                var tables = await Rows(inMaster, "EXEC [daffydb].[sys].sp_tables_rowset2 N'dbo', NULL");
                Expect(tables.Any(r => (string)r["TABLE_NAME"]! == "orders"), "daffydb's tables are listed from master");
                var row = (await Rows(inMaster, "SELECT DB_NAME() AS db, (SELECT COUNT(*) FROM sys.objects) AS here"))[0];
                Expect((string)row["db"]! == "master" && System.Convert.ToInt64(row["here"]) == 0,
                       $"the caller should still be in an empty master, is in {row["db"]}");
            });

        await CheckLoginRefused(builder, "nosuch",
            "a login naming a database that does not exist is refused with 4060",
            4060);

        // Power BI's SQL Server connector, captured through DaffyTee. It lists the databases that
        // are not system databases, and with master the only one there was nothing to show; then
        // it reads TABLES and ROUTINES to build the navigator.
        await Check(connection, verbose,
            "Power BI's database list finds daffydb",
            "select d.name as Name from sys.databases d where d.name not in ('master', 'model', 'msdb', 'tempdb') and d.is_distributor = 0 and d.state = 0",
            rows => Expect(rows.Count == 1 && (string)rows[0]["Name"]! == "daffydb",
                           $"expected daffydb alone, got {string.Join(", ", rows.Select(r => r["Name"]))}"));

        await Check(connection, verbose,
            "Power BI's navigator lists the tables as tables",
            """
            select t.[TABLE_CATALOG], t.[TABLE_SCHEMA], t.[TABLE_NAME], t.[TABLE_TYPE], tv.create_date [CREATED_DATE], tv.modify_date [MODIFIED_DATE], cast(e.value as nvarchar(max)) [DESCRIPTION]
            from [INFORMATION_SCHEMA].[TABLES] t
            join sys.schemas s on s.name = t.[TABLE_SCHEMA]
            join sys.objects tv on tv.name = t.[TABLE_NAME] and tv.schema_id = s.schema_id and tv.parent_object_id = 0
            left outer join sys.extended_properties e on tv.object_id = e.major_id and e.minor_id = 0 and e.class = 1 and e.name = 'MS_Description'
            where 1=1 and 1=1
            """,
            rows =>
            {
                Expect(rows.Any(r => (string)r["TABLE_NAME"]! == "orders") && rows.Any(r => (string)r["TABLE_NAME"]! == "customers"),
                       "customers and orders are listed");
                Expect(rows.All(r => (string)r["TABLE_TYPE"]! == "BASE TABLE" && (string)r["TABLE_CATALOG"]! == "daffydb"),
                       "each is a BASE TABLE in daffydb");
            });

        await Check(connection, verbose,
            "Power BI's navigator finds no routines, rather than an error",
            """
            select r.[ROUTINE_SCHEMA], r.[ROUTINE_NAME], r.[ROUTINE_TYPE], p.create_date [CREATED_DATE], p.modify_date [MODIFIED_DATE], cast(e.value as nvarchar(max)) [DESCRIPTION]
            from [INFORMATION_SCHEMA].[ROUTINES] r
            join sys.schemas s on s.name = r.[ROUTINE_SCHEMA]
            join sys.objects p on p.name = r.[ROUTINE_NAME] and p.schema_id = s.schema_id and p.parent_object_id = 0
            left outer join sys.extended_properties e on p.object_id = e.major_id and e.minor_id = 0 and e.class = 1 and e.name = 'MS_Description'
            where 1=1 and 1=1
            """,
            rows => Expect(rows.Count == 0, $"expected no routines, got {rows.Count}"));

        // What Power BI asks when a table is picked in the navigator: every column, with the
        // facets it builds the table's type from. Two tables it joins to - defaults and computed
        // columns - have nothing in them here, and each value has to arrive in SQL Server's type:
        // the length of an nvarchar(4000), which a text column is, is floor(8000 / 2), the int 4000.
        await Check(connection, verbose,
            "Power BI's column query answers in SQL Server's types",
            """
            select
                s.name [TABLE_SCHEMA],
                o.name [TABLE_NAME],
                c.name [COLUMN_NAME],
                cast(c.column_id as bigint) [ORDINAL_POSITION],
                c.is_nullable [IS_NULLABLE],
                case when (t.is_user_defined = 0 and t.name is not null) then t.name when (c.system_type_id = 240 or t.name is null) then 'udt' else t_system.name end [DATA_TYPE],
                case when (c.system_type_id in (59, 62)) then 2 when (c.system_type_id in (48, 52, 56, 60, 104, 106, 108, 122, 127)) then 10 else null end [NUMERIC_PRECISION_RADIX],
                c.precision [NUMERIC_PRECISION],
                case when (c.system_type_id in (59, 62)) then null else c.scale end [NUMERIC_SCALE],
                case when (c.system_type_id in (40, 41, 42, 43, 58, 61)) then c.scale else null end [DATETIME_PRECISION],
                case when (c.system_type_id in (231, 239)) then floor(c.max_length / 2) when (c.system_type_id in (165, 167, 173, 175)) then c.max_length else null end [CHARACTER_MAXIMUM_LENGTH],
                cast(e.value as nvarchar(max)) [DESCRIPTION],
                d.definition [COLUMN_DEFAULT],
                cc.definition [COLUMN_EXPRESSION],
                case when c.is_identity = 1 or c.is_computed = 1 or t.system_type_id = 189 or c.generated_always_type > 0 then 0 else 1 end [IS_WRITABLE],
                null FIELD_CAPTION
            from sys.objects o
            join sys.schemas s on s.schema_id = o.schema_id
            join sys.columns c on o.object_id = c.object_id
            left join sys.types t on c.user_type_id = t.user_type_id
            left join sys.types t_system on t.system_type_id = t_system.user_type_id
            left join sys.default_constraints d on d.object_id = c.default_object_id
            left join sys.computed_columns cc on c.object_id = cc.object_id and c.column_id = cc.column_id
            left join sys.extended_properties e on o.object_id = e.major_id and c.column_id = e.minor_id and e.class = 1 and e.name = 'MS_Description'
            where 1=1;
            """,
            rows =>
            {
                var product = rows.Single(r => (string)r["TABLE_NAME"]! == "orders" && (string)r["COLUMN_NAME"]! == "product");
                Expect(product["CHARACTER_MAXIMUM_LENGTH"] is 4000, $"an nvarchar(4000) is the int 4000, got {Render(product["CHARACTER_MAXIMUM_LENGTH"])} " +
                       $"({product["CHARACTER_MAXIMUM_LENGTH"]?.GetType().Name})");
                Expect((string)product["DATA_TYPE"]! == "nvarchar" && product["IS_NULLABLE"] is true, "product is a nullable nvarchar");
                var price = rows.Single(r => (string)r["TABLE_NAME"]! == "orders" && (string)r["COLUMN_NAME"]! == "unit_price");
                Expect(price["NUMERIC_PRECISION"] is (byte)10 && price["NUMERIC_SCALE"] is (byte)2,
                       $"precision and scale are tinyint 10 and 2, got {price["NUMERIC_PRECISION"]?.GetType().Name} {price["NUMERIC_PRECISION"]}");
                Expect(rows.All(r => r["COLUMN_DEFAULT"] is null && r["COLUMN_EXPRESSION"] is null), "nothing has a default or an expression");
            });

        // And then its relationships, which it reads for the table being previewed and again on
        // Load. A file has no foreign keys, so the answer is none - not an error.
        await Check(connection, verbose,
            "Power BI's foreign key query finds none, rather than an error",
            """
            select
                convert(nvarchar, fk.object_id) [FK_NAME], cast(f.constraint_column_id as bigint) [ORDINAL],
                s1.name [TABLE_SCHEMA_1], o1.name [TABLE_NAME_1], c1.name [PK_COLUMN_NAME_1],
                s2.name [TABLE_SCHEMA_2], o2.name [TABLE_NAME_2], c2.name [PK_COLUMN_NAME_2],
                f.constraint_object_id, f.constraint_column_id
            from sys.foreign_key_columns f join sys.foreign_keys fk on f.constraint_object_id = fk.object_id
            join sys.objects o1 on o1.object_id = f.parent_object_id join sys.schemas s1 on s1.schema_id = o1.schema_id
            join sys.objects o2 on o2.object_id = f.referenced_object_id join sys.schemas s2 on s2.schema_id = o2.schema_id
            join sys.columns c1 on c1.object_id = o1.object_id and c1.column_id = f.parent_column_id
            join sys.columns c2 on c2.object_id = o2.object_id and c2.column_id = f.referenced_column_id
            where 1=1
            union
            select
                convert(nvarchar, fk.object_id) [FK_NAME], cast(f.constraint_column_id as bigint) [ORDINAL],
                s1.name [TABLE_SCHEMA_1], o1.name [TABLE_NAME_1], c1.name [PK_COLUMN_NAME_1],
                s2.name [TABLE_SCHEMA_2], o2.name [TABLE_NAME_2], c2.name [PK_COLUMN_NAME_2],
                f.constraint_object_id, f.constraint_column_id
            from sys.foreign_key_columns f join sys.foreign_keys fk on f.constraint_object_id = fk.object_id
            join sys.objects o1 on o1.object_id = f.parent_object_id join sys.schemas s1 on s1.schema_id = o1.schema_id
            join sys.objects o2 on o2.object_id = f.referenced_object_id join sys.schemas s2 on s2.schema_id = o2.schema_id
            join sys.columns c1 on c1.object_id = o1.object_id and c1.column_id = f.parent_column_id
            join sys.columns c2 on c2.object_id = o2.object_id and c2.column_id = f.referenced_column_id
            where 1=1
            order by f.constraint_object_id, f.constraint_column_id;
            """,
            rows => Expect(rows.Count == 0, $"expected no foreign keys, got {rows.Count}"));

        await Check(connection, verbose,
            "an integer divided by an integer is an integer, columns included",
            "SELECT 7 / 2 AS seven_halves, -1 / 2 AS minus_half, quantity / 2 AS half_quantity, 7.0 / 2 AS decimal_half FROM orders WHERE order_id = 1",
            rows =>
            {
                Expect(rows[0]["seven_halves"] is 3, $"7 / 2 is the int 3, got {Render(rows[0]["seven_halves"])}");
                Expect(System.Convert.ToInt64(rows[0]["minus_half"]) == 0, "-1 / 2 truncates toward zero");
                Expect(System.Convert.ToInt64(rows[0]["half_quantity"]) == 1, $"order 1 has quantity 2, so 1; got {Render(rows[0]["half_quantity"])}");
                Expect(System.Convert.ToDouble(rows[0]["decimal_half"]) == 3.5, "a decimal on either side keeps the fraction");
            });

        await Check(connection, verbose,
            "FLOOR and CEILING keep their argument's type",
            "SELECT FLOOR(7) AS int_floor, CEILING(7) AS int_ceiling, FLOOR(CAST(-2.5 AS float)) AS float_floor, CEILING(CAST(-2.5 AS float)) AS float_ceiling, CEILING(4.2) AS rounded_up, FLOOR(NULL) AS nothing",
            rows =>
            {
                Expect(rows[0]["int_floor"] is 7 && rows[0]["int_ceiling"] is 7,
                       $"an int stays an int, got {rows[0]["int_floor"]?.GetType().Name}");
                Expect(rows[0]["float_floor"] is -3.0 && rows[0]["float_ceiling"] is -2.0, "a float goes down and up");
                Expect(System.Convert.ToDouble(rows[0]["rounded_up"]) == 5, "CEILING(4.2) is 5");
                Expect(rows[0]["nothing"] is null, "FLOOR(NULL) is NULL");
            });

        await Check(connection, verbose,
            "INFORMATION_SCHEMA.COLUMNS describes columns as SQL Server does",
            """
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                   DATETIME_PRECISION, CHARACTER_SET_NAME
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION
            """,
            rows =>
            {
                Expect(rows.Count == 6, $"orders has six columns, got {rows.Count}");
                var price = rows.Single(r => (string)r["COLUMN_NAME"]! == "unit_price");
                Expect((string)price["DATA_TYPE"]! == "decimal" && System.Convert.ToInt32(price["NUMERIC_PRECISION"]) == 10
                       && System.Convert.ToInt32(price["NUMERIC_SCALE"]) == 2, "unit_price is decimal(10,2)");
                var product = rows.Single(r => (string)r["COLUMN_NAME"]! == "product");
                Expect((string)product["DATA_TYPE"]! == "nvarchar" && System.Convert.ToInt32(product["CHARACTER_MAXIMUM_LENGTH"]) == 4000
                       && (string)product["CHARACTER_SET_NAME"]! == "UNICODE", "product is nvarchar(4000)");
                var placed = rows.Single(r => (string)r["COLUMN_NAME"]! == "order_ts");
                Expect((string)placed["DATA_TYPE"]! == "datetime2" && System.Convert.ToInt32(placed["DATETIME_PRECISION"]) == 7,
                       "order_ts is datetime2(7)");
            });

        await Check(connection, verbose,
            "daffydb says what it is, and master says nothing",
            """
            SELECT e.class, e.name, CAST(e.value AS nvarchar(400)) AS value,
                   (SELECT COUNT(*) FROM master.sys.extended_properties) AS in_master
            FROM sys.extended_properties e
            """,
            rows =>
            {
                Expect(rows.Count == 1, $"expected one property, got {rows.Count}");
                Expect(System.Convert.ToInt32(rows[0]["class"]) == 0 && (string)rows[0]["name"]! == "MS_Description",
                       "it is the database's MS_Description");
                Expect(((string)rows[0]["value"]!).Contains("This is not SQL Server"), $"it says {rows[0]["value"]}");
                Expect(System.Convert.ToInt64(rows[0]["in_master"]) == 0, "master carries none");
            });

        // SSMS 22's first question on connect, word for word from its ConnectionInfo.dll. SMO reads
        // each number with Convert.ToInt64, so a NULL in any of them - EditionID was new in 22 -
        // fails the connection outright.
        await CheckMultiple(connection, verbose,
            "SSMS 22's connect query",
            """
            DECLARE @edition sysname;
            SET @edition = cast(SERVERPROPERTY(N'EDITION') as sysname);
            SELECT case when @edition = N'SQL Azure' then 2 else 1 end as 'DatabaseEngineType',
            @edition AS Edition,
            CONVERT(bigint, SERVERPROPERTY(N'EditionID')) AS EditionID,
            SERVERPROPERTY('EngineEdition') AS DatabaseEngineEdition,
            SERVERPROPERTY('ProductVersion') AS ProductVersion,
            @@MICROSOFTVERSION AS MicrosoftVersion,
            case when serverproperty('EngineEdition') = 12 then 1 when serverproperty('EngineEdition') = 11 and @@version like 'Microsoft Azure SQL Data Warehouse%' then 1 else 0 end as IsFabricServer,
            convert(sysname, SERVERPROPERTY(N'Collation')) AS Collation;
            select host_platform from sys.dm_os_host_info
            """,
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                var server = sets[0][0];
                foreach (string number in new[] { "DatabaseEngineType", "EditionID", "DatabaseEngineEdition", "MicrosoftVersion", "IsFabricServer" })
                    Expect(server[number] is not null, $"{number} is NULL, which SMO cannot convert");
                Expect(System.Convert.ToInt64(server["EditionID"]) == -2117995310, "EditionID is Developer's");
                Expect(System.Convert.ToInt64(server["DatabaseEngineType"]) == 1, "an engine, not Azure");
                Expect(System.Convert.ToInt64(server["IsFabricServer"]) == 0, "not Fabric");
                Expect((string)server["Collation"]! == "SQL_Latin1_General_CP1_CI_AS", $"collation is {server["Collation"]}");
                Expect(sets[1].Count == 1, "one host platform");
            });

        // T-SQL needs no semicolon between statements, nor a new line: the ODBC driver sends
        // "set implicit_transactions off select USER_NAME() select ..." on one line.
        await CheckMultiple(connection, verbose,
            "statements need no semicolon, even on one line",
            "set nocount on select 1 AS one select 2 AS two union all select 3",
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                Expect(sets[0].Count == 1 && sets[1].Count == 2, "the UNION ALL stays one statement");
            });

        // The ON of SET NOCOUNT ON is not a join's ON, after which a SELECT would carry on the
        // same statement. Read as one, the SELECT on the next line vanished into the SET.
        await CheckMultiple(connection, verbose,
            "a SELECT on the line after SET NOCOUNT ON runs",
            "SET NOCOUNT ON\nSELECT 1 AS one",
            sets => Expect(sets.Count == 1 && sets[0].Count == 1, $"expected one result set, got {sets.Count}"));

        // A linked server sets ROWCOUNT to 1 to read one row of a table, and back to 0 after.
        await CheckMultiple(connection, verbose,
            "SET ROWCOUNT limits the queries after it, until SET ROWCOUNT 0",
            "SET ROWCOUNT 2; SELECT order_id FROM orders ORDER BY order_id DESC; SET ROWCOUNT 0; SELECT order_id FROM orders",
            sets =>
            {
                Expect(sets.Count == 2, $"expected 2 result sets, got {sets.Count}");
                Expect(sets[0].Count == 2 && System.Convert.ToInt32(sets[0][0]["order_id"]) == 24,
                       $"the first two rows, in order; got {sets[0].Count}");
                Expect(sets[1].Count == 24, $"then every row; got {sets[1].Count}");
            });

        await CheckPrepared(connection,
            "a prepared statement runs again with new values",
            "SELECT product FROM orders WHERE order_id = @id",
            [1, 2, 1],
            products => Expect(products.SequenceEqual(["Keyboard", "Monitor", "Keyboard"]),
                               $"got {string.Join(", ", products)}"));

        await CheckError(connection,
            "assigning an undeclared variable is rejected",
            "SET @never_declared = 1; SELECT @never_declared AS x");

        await CheckError(connection,
            "a bad query returns a readable error",
            "SELECT * FROM widgets_that_do_not_exist");

        // Long enough that it is still running when the client gives up after two seconds.
        const string longQuery = "SELECT SUM(a.range * b.range) FROM range(200000) a, range(200000) b";

        await CheckCancel(builder, "a cancel stops a long query and frees the server", longQuery);
        await CheckCancel(builder, "a cancel stops a recursive CTE that never ends",
            "WITH r AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM r) SELECT COUNT(*) FROM r");
        await CheckCancelLeavesOthers(builder, "a cancel stops only the query it was sent for", longQuery);

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Check(
        SqlConnection connection, bool verbose, string name, string sql,
        Action<List<Dictionary<string, object?>>> assert,
        params (string Name, SqlDbType Type, object Value)[] parameters)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (parameterName, type, value) in parameters)
                command.Parameters.Add(parameterName, type).Value = value;

            var rows = new List<Dictionary<string, object?>>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
            }

            try
            {
                assert(rows);
                Report(name, true);
                if (verbose) Console.WriteLine(Indent(sql));
            }
            catch (Exception ex)
            {
                Report(name, false);
                Console.WriteLine(Indent(sql));
                Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }

            PrintRows(rows);
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine(Indent(sql));
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// SqlCommand.Prepare(), then the command run once per id: SqlClient sends the first as
    /// sp_prepexec, which hands back a handle, and every one after it as sp_execute with that handle.
    /// </summary>
    private static async Task CheckPrepared(SqlConnection connection, string name, string sql, int[] ids,
                                            Action<List<string?>> assert)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@id", SqlDbType.Int);
            command.Prepare();

            var values = new List<string?>();
            foreach (int id in ids)
            {
                command.Parameters["@id"].Value = id;
                values.Add(await command.ExecuteScalarAsync() as string);
            }

            assert(values);
            Report(name, true);
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine(Indent(sql));
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// Like <see cref="Check"/>, but walks every result set the batch produced via NextResult,
    /// which is how a client sees more than one.
    /// </summary>
    private static async Task CheckMultiple(
        SqlConnection connection, bool verbose, string name, string sql,
        Action<List<List<Dictionary<string, object?>>>> assert)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            var sets = new List<List<Dictionary<string, object?>>>();

            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    var rows = new List<Dictionary<string, object?>>();
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
                    sets.Add(rows);
                }
                while (await reader.NextResultAsync());
            }

            try
            {
                assert(sets);
                Report(name, true);
            }
            catch (Exception ex)
            {
                Report(name, false);
                Console.WriteLine(Indent(sql));
                Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }

            for (int i = 0; i < sets.Count; i++)
            {
                Console.WriteLine($"       result set {i + 1}:");
                PrintRows(sets[i]);
            }
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine(Indent(sql));
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    private static async Task CheckError(SqlConnection connection, string name, string sql, int? number = null)
    {
        try
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) { }

            Report(name, false);
            Console.WriteLine("       expected a SqlException, but the query succeeded");
        }
        catch (SqlException ex) when (number is not null && ex.Number != number)
        {
            Report(name, false);
            Console.WriteLine($"       expected error {number}, got {ex.Number}: {FirstLine(ex.Message)}");
        }
        catch (SqlException ex)
        {
            Report(name, true);
            Console.WriteLine($"       SqlException {ex.Number}: {FirstLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine($"       expected SqlException, got {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// A query the client gives up on has to stop on the server too. A command timeout sends a TDS
    /// attention; every session shares one DuckDB connection, so a query left running would hold up
    /// every client. The proof that it stopped is that the next query on the same connection
    /// answers at once.
    /// </summary>
    private static async Task CheckCancel(SqlConnectionStringBuilder builder, string name, string sql)
    {
        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            var clock = Stopwatch.StartNew();
            try
            {
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 2 };
                await command.ExecuteScalarAsync();

                Report(name, false);
                Console.WriteLine("       expected the query to time out, but it finished");
                return;
            }
            catch (SqlException) { }

            double cancelledAfter = clock.Elapsed.TotalSeconds;
            clock.Restart();

            await using var next = new SqlCommand("SELECT 42", connection) { CommandTimeout = 10 };
            object? answer = await next.ExecuteScalarAsync();
            double answeredAfter = clock.Elapsed.TotalSeconds;

            Report(name, cancelledAfter < 5 && answeredAfter < 2 && System.Convert.ToInt32(answer) == 42);
            Console.WriteLine($"       cancelled after {cancelledAfter:N1}s; the next query answered in {answeredAfter:N1}s");
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// With one connection shared, a cancel must stop only its own query. Client A runs a long query
    /// with a six-second timeout; client B, queued behind it, gives up after two. A has to run on
    /// until its own timeout, not stop when B does.
    /// </summary>
    private static async Task CheckCancelLeavesOthers(SqlConnectionStringBuilder builder, string name, string sql)
    {
        try
        {
            await using var first = new SqlConnection(builder.ConnectionString);
            await using var second = new SqlConnection(builder.ConnectionString);
            await first.OpenAsync();
            await second.OpenAsync();

            var clock = Stopwatch.StartNew();
            var running = Task.Run(async () =>
            {
                try
                {
                    await using var command = new SqlCommand(sql, first) { CommandTimeout = 6 };
                    await command.ExecuteScalarAsync();
                }
                catch (SqlException) { }
                return clock.Elapsed.TotalSeconds;
            });

            await Task.Delay(1000);
            try
            {
                await using var queued = new SqlCommand("SELECT 1", second) { CommandTimeout = 2 };
                await queued.ExecuteScalarAsync();
            }
            catch (SqlException) { }
            double secondGaveUp = clock.Elapsed.TotalSeconds;

            double firstEnded = await running;
            Report(name, firstEnded > 5);
            Console.WriteLine($"       the queued client gave up at {secondGaveUp:N1}s; the running one went on to {firstEnded:N1}s");
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>A check made on a connection of its own, logged in to the database named.</summary>
    private static async Task CheckLogin(SqlConnectionStringBuilder builder, string database, string name,
                                         Func<SqlConnection, Task> assert)
    {
        try
        {
            await using var connection = new SqlConnection(
                new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = database }.ConnectionString);
            await connection.OpenAsync();
            await assert(connection);
            Report(name, true);
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine($"       {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>A login that must fail, with SQL Server's number for why.</summary>
    private static async Task CheckLoginRefused(SqlConnectionStringBuilder builder, string database, string name, int number)
    {
        try
        {
            await using var connection = new SqlConnection(
                new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = database }.ConnectionString);
            await connection.OpenAsync();

            Report(name, false);
            Console.WriteLine($"       expected the login to fail, but it connected to {connection.Database}");
        }
        catch (SqlException ex) when (ex.Number == number)
        {
            Report(name, true);
            Console.WriteLine($"       SqlException {ex.Number}: {FirstLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            Report(name, false);
            Console.WriteLine($"       expected SqlException {number}, got {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    private static async Task<List<Dictionary<string, object?>>> Rows(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void PrintRows(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) { Console.WriteLine("       (no rows)"); return; }

        var columns = rows[0].Keys.ToList();
        var widths = columns.ToDictionary(
            c => c,
            c => Math.Max(c.Length, rows.Max(r => Render(r[c]).Length)));

        Console.WriteLine("       " + string.Join("  ", columns.Select(c => c.PadRight(widths[c]))));
        Console.WriteLine("       " + string.Join("  ", columns.Select(c => new string('-', widths[c]))));

        foreach (var row in rows.Take(8))
            Console.WriteLine("       " + string.Join("  ", columns.Select(c => Render(row[c]).PadRight(widths[c]))));

        if (rows.Count > 8) Console.WriteLine($"       ... {rows.Count - 8} more rows");
        Console.WriteLine();
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero
            ? dateTime.ToString("yyyy-MM-dd")
            : dateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        decimal number => number.ToString("0.##"),
        bool flag => flag ? "true" : "false",
        string text when text.Length > 60 => $"{text[..30]}... ({text.Length} chars)",
        byte[] bytes => bytes.Length > 16
            ? $"0x{Convert.ToHexString(bytes, 0, 8)}... ({bytes.Length} bytes)"
            : "0x" + Convert.ToHexString(bytes),
        _ => value.ToString() ?? "",
    };

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Report(string name, bool ok)
    {
        if (ok) _passed++; else _failed++;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "PASS " : "FAIL ");
        Console.ForegroundColor = previous;
        Console.WriteLine(name);
    }

    private static string Indent(string text)
    {
        var sb = new StringBuilder();
        foreach (string line in text.Split('\n')) sb.Append("       ").Append(line.TrimEnd()).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
