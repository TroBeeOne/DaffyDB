using Microsoft.Data.SqlClient;

namespace DriverProbe;

/// <summary>
/// Puts the same three tables on both servers. customers and orders mirror what daffydb --seed
/// writes, declared as the SQL Server types DaffyDB reports for them; type_zoo holds one column of
/// every type DaffyDB maps, and one row of values plus one of NULLs.
/// </summary>
internal static class Setup
{
    /// <summary>
    /// A database of its own, so nothing is created in LocalDB's master. It is left in place between
    /// runs and its tables are rebuilt each time: DROP DATABASE DaffyDbReference removes it.
    /// </summary>
    public static void LocalDb(Target target)
    {
        var master = new SqlConnectionStringBuilder(target.SqlClient) { InitialCatalog = "master" };
        using (var connection = new SqlConnection(master.ConnectionString))
        {
            connection.Open();
            Execute(connection, $"IF DB_ID('{target.Database}') IS NULL CREATE DATABASE [{target.Database}]");
        }

        using var reference = new SqlConnection(target.SqlClient);
        reference.Open();
        Execute(reference, """
            DROP TABLE IF EXISTS dbo.customers, dbo.orders, dbo.type_zoo;

            CREATE TABLE dbo.customers (
                customer_id int, name nvarchar(4000), city nvarchar(4000), is_active bit, signup_date date);

            CREATE TABLE dbo.orders (
                order_id int, customer_id int, product nvarchar(4000), quantity int,
                unit_price decimal(10,2), order_ts datetime2);

            CREATE TABLE dbo.type_zoo (
                c_tinyint tinyint, c_smallint smallint, c_int int, c_bigint bigint, c_real real, c_float float,
                c_decimal decimal(10,2), c_bit bit, c_date date, c_time time, c_datetime2 datetime2,
                c_nvarchar nvarchar(4000), c_varbinary varbinary(8000), c_guid uniqueidentifier);

            INSERT dbo.type_zoo VALUES
                (200, -12345, 123456789, 9000000000, 1.5, 2.25, 12345.67, 1, '2024-02-29', '13:14:15',
                 '2024-02-29 13:14:15.123456', N'héllo', 0xCAFE, '6F9619FF-8B86-D011-B42D-00C04FC964FF'),
                (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
            """);
    }

    /// <summary>
    /// customers and orders come from the seeded data folder. type_zoo is built in DuckDB's own
    /// dialect, because the point is to see how DaffyDB presents each native type, and T-SQL DDL
    /// would first be translated into whichever DuckDB type the translator prefers.
    /// </summary>
    public static void DaffyDb(Target target)
    {
        using var connection = new SqlConnection(target.SqlClient);
        connection.Open();

        try
        {
            Execute(connection, "SELECT COUNT(*) FROM type_zoo");
            return;
        }
        catch (SqlException)
        {
            // Not there yet: create it below.
        }

        try
        {
            Execute(connection, """
                EXEC sp_duckdb N'CREATE TABLE daffydb.dbo.type_zoo (
                    c_tinyint UTINYINT, c_smallint SMALLINT, c_int INTEGER, c_bigint BIGINT, c_real FLOAT,
                    c_float DOUBLE, c_decimal DECIMAL(10,2), c_bit BOOLEAN, c_date DATE, c_time TIME,
                    c_datetime2 TIMESTAMP, c_nvarchar VARCHAR, c_varbinary BLOB, c_guid UUID)'
                """);
            Execute(connection, """
                EXEC sp_duckdb N'INSERT INTO daffydb.dbo.type_zoo VALUES
                    (200, -12345, 123456789, 9000000000, 1.5, 2.25, 12345.67, true, ''2024-02-29'', ''13:14:15'',
                     ''2024-02-29 13:14:15.123456'', ''héllo'', ''\xCA\xFE''::BLOB, ''6F9619FF-8B86-D011-B42D-00C04FC964FF''),
                    (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)'
                """);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"could not create type_zoo - is daffydb running with --allow-passthrough? ({ex.Message})", ex);
        }
    }

    private static void Execute(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        command.ExecuteNonQuery();
    }
}
