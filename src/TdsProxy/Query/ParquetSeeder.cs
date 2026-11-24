using DuckDB.NET.Data;

namespace TdsProxy.Query;

/// <summary>
/// Writes the two parquet files the DuckDB handler mounts. Data is fixed rather than random so the
/// probe can assert exact values.
/// </summary>
internal static class ParquetSeeder
{
    public const string CustomersFile = "customers.parquet";
    public const string OrdersFile = "orders.parquet";

    public static void Seed(string directory)
    {
        Directory.CreateDirectory(directory);

        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();

        Execute(connection, """
            CREATE TABLE customers (
                customer_id INTEGER,
                name        VARCHAR,
                city        VARCHAR,
                is_active   BOOLEAN,
                signup_date DATE
            );
            INSERT INTO customers VALUES
                (1, 'Ada Lovelace',      'London',     true,  DATE '2023-01-15'),
                (2, 'Grace Hopper',      'New York',   true,  DATE '2023-02-20'),
                (3, 'Alan Turing',       'Manchester', false, DATE '2023-03-05'),
                (4, 'Katherine Johnson', 'Hampton',    true,  DATE '2023-04-11'),
                (5, 'Linus Torvalds',    'Helsinki',   true,  DATE '2023-05-30'),
                (6, 'Barbara Liskov',    'Boston',     false, DATE '2023-06-18'),
                (7, 'Tim Berners-Lee',   NULL,         true,  DATE '2023-07-22'),
                (8, 'Margaret Hamilton', 'Cambridge',  true,  DATE '2023-08-09');
            """);

        // Customer 8 deliberately has no orders, so LEFT JOIN and NULL aggregates are exercised.
        Execute(connection, """
            CREATE TABLE orders (
                order_id    INTEGER,
                customer_id INTEGER,
                product     VARCHAR,
                quantity    INTEGER,
                unit_price  DECIMAL(10,2),
                order_ts    TIMESTAMP
            );
            INSERT INTO orders VALUES
                ( 1, 1, 'Keyboard',  2,   49.99, TIMESTAMP '2024-01-05 09:15:00'),
                ( 2, 1, 'Monitor',   1,  229.50, TIMESTAMP '2024-01-18 14:02:00'),
                ( 3, 1, 'Mouse',     3,   19.95, TIMESTAMP '2024-02-02 11:30:00'),
                ( 4, 2, 'Laptop',    1, 1299.00, TIMESTAMP '2024-01-09 08:45:00'),
                ( 5, 2, 'Dock',      1,  189.00, TIMESTAMP '2024-02-14 16:20:00'),
                ( 6, 2, 'Cable',     5,    9.99, TIMESTAMP '2024-03-01 10:05:00'),
                ( 7, 3, 'Monitor',   2,  229.50, TIMESTAMP '2024-01-22 13:40:00'),
                ( 8, 3, 'Webcam',    1,   79.00, TIMESTAMP '2024-03-12 09:00:00'),
                ( 9, 4, 'Laptop',    2, 1299.00, TIMESTAMP '2024-02-07 15:10:00'),
                (10, 4, 'Keyboard',  1,   49.99, TIMESTAMP '2024-02-28 12:25:00'),
                (11, 4, 'Headset',   2,  129.00, TIMESTAMP '2024-04-03 17:45:00'),
                (12, 5, 'Mouse',     4,   19.95, TIMESTAMP '2024-01-30 07:55:00'),
                (13, 5, 'Cable',    10,    9.99, TIMESTAMP '2024-03-21 19:30:00'),
                (14, 6, 'Monitor',   1,  229.50, TIMESTAMP '2024-04-15 11:11:00'),
                (15, 7, 'Laptop',    1, 1299.00, TIMESTAMP '2024-05-02 10:00:00'),
                (16, 7, 'Dock',      2,  189.00, TIMESTAMP '2024-05-20 14:35:00'),
                (17, 1, 'Headset',   1,  129.00, TIMESTAMP '2024-06-01 08:20:00'),
                (18, 2, 'Webcam',    2,   79.00, TIMESTAMP '2024-06-11 09:50:00'),
                (19, 3, 'Cable',     3,    9.99, TIMESTAMP '2024-06-25 16:05:00'),
                (20, 5, 'Keyboard',  2,   49.99, TIMESTAMP '2024-07-04 13:15:00'),
                (21, 4, 'Mouse',     1,   19.95, TIMESTAMP '2024-07-19 10:40:00'),
                (22, 7, 'Monitor',   1,  229.50, TIMESTAMP '2024-08-02 12:00:00'),
                (23, 2, 'Laptop',    1, 1299.00, TIMESTAMP '2024-08-23 15:55:00'),
                (24, 6, 'Cable',     6,    9.99, TIMESTAMP '2024-09-09 18:10:00');
            """);

        string customers = Path.Combine(directory, CustomersFile).Replace('\\', '/');
        string orders = Path.Combine(directory, OrdersFile).Replace('\\', '/');

        Execute(connection, $"COPY customers TO '{customers}' (FORMAT PARQUET)");
        Execute(connection, $"COPY orders TO '{orders}' (FORMAT PARQUET)");

        Console.WriteLine($"[seed] wrote {customers} (8 rows)");
        Console.WriteLine($"[seed] wrote {orders} (24 rows)");
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
