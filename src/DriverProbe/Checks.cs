using System.Data;
using System.Data.OleDb;
using Microsoft.Data.SqlClient;

namespace DriverProbe;

/// <summary>Which rows of a result are compared.</summary>
internal enum RowRule
{
    /// <summary>Every row, in order.</summary>
    Exact,

    /// <summary>
    /// Only the rows whose key column holds one of the given values. A table list on a real server
    /// also names hundreds of system views, and what matters is that ours names the same tables the
    /// same way, not that it names everything SQL Server does.
    /// </summary>
    Keyed,

    /// <summary>Columns only. For a query over data the two servers do not share.</summary>
    ShapeOnly,
}

/// <summary>Where a check runs: the same three client stacks, pointed at one server or the other.</summary>
internal sealed record Target(string Label, string Database, string SqlClient, string Odbc, string OleDb);

internal sealed record Check(
    string Client, string Name, RowRule Rows, Func<Target, List<ResultSetShape>> Run,
    string? KeyColumn = null, string[]? Keys = null)
{
    public string Id => $"{Client,-6} {Name}";
}

/// <summary>
/// The calls Power BI makes, taken from DaffyTee captures of it talking to a real SQL Server:
/// once through an ODBC Driver 18 DSN, once through OLE DB with MSOLEDBSQL19.
///
/// Each is made twice over. The "sql" checks send the captured text exactly, through SqlClient,
/// which reports the SQL Server type of every column. The "odbc" and "oledb" checks make the
/// catalog calls through the real drivers, which is how Power BI reaches them - and which proves
/// the driver can consume the answer, not just that the answer has the right shape.
/// </summary>
internal static class Checks
{
    public static readonly string[] Tables = ["customers", "orders", "type_zoo"];

    public static IReadOnlyList<Check> All { get; } =
    [
        new("sql", "connect", RowRule.Exact, t => Sql(t, "SELECT 1 AS n")),
        new("odbc", "connect", RowRule.Exact, t => { using var odbc = new RawOdbc(t.Odbc); return []; }),
        new("oledb", "connect", RowRule.Exact, t => { using var c = OpenOleDb(t); return []; }),

        // --- ODBC Driver 18: the DSN wizard's test, then Power BI's navigator and import ---------

        Text("wizard: syscharsets",
             "select name from sys.syscharsets where id = convert(tinyint, databasepropertyex ( db_name() , 'sqlcharset'))"),
        Text("sp_tables catalogs", "EXEC [sys].sp_tables N'', N'', N'%', N'', @fUsePattern = 1;",
             RowRule.Keyed, "TABLE_QUALIFIER", ["master", "msdb", "daffydb"]),
        Text("sp_tables TABLE,VIEW", "EXEC [sys].sp_tables NULL, N'%', NULL, N'''TABLE'',''VIEW''', @fUsePattern = 1;",
             RowRule.Keyed, "TABLE_NAME", Tables),
        .. Tables.Select(table => Text($"sp_columns_100 {table}",
             $"EXEC [{{db}}].[sys].sp_columns_100 N'{table}', N'dbo', N'{{db}}', NULL, @ODBCVer = 3, @fUsePattern = 1;")),
        Text("sp_datatype_info_100", "EXEC [sys].sp_datatype_info_100 0, @ODBCVer = 4;"),
        Text("sp_pkeys", "EXEC [{db}].[sys].sp_pkeys N'orders', N'dbo', N'{db}';"),
        Text("sp_fkeys referencing", "EXEC [{db}].[sys].sp_fkeys NULL, NULL, N'{db}', N'orders', N'dbo', NULL;"),
        Text("sp_fkeys referenced", "EXEC [{db}].[sys].sp_fkeys N'orders', N'dbo', N'{db}', NULL, NULL, NULL;"),
        Text("import orders (odbc)",
             "select \"order_id\",\n    \"customer_id\",\n    \"product\",\n    \"quantity\",\n    \"unit_price\",\n    \"order_ts\"\nfrom \"{db}\".\"dbo\".\"orders\"",
             RowRule.ShapeOnly),

        // --- MSOLEDBSQL19: sent on every new session, then the navigator and import -------------

        Text("sp_oledb_ro_usrname", "exec [sys].sp_oledb_ro_usrname"),
        Text("collationname", "select collationname(0x0904D00034)"),
        Text("sp_catalogs_rowset2", "EXEC [sys].sp_catalogs_rowset2;", RowRule.Keyed, "CATALOG_NAME", ["master", "msdb", "daffydb"]),
        Text("sp_schemata_rowset", "EXEC [{db}].[sys].sp_schemata_rowset NULL, NULL;", RowRule.Keyed, "SCHEMA_NAME", ["dbo"]),
        Text("sp_tables_rowset2 dbo", "EXEC [{db}].[sys].sp_tables_rowset2 N'dbo', NULL;", RowRule.Keyed, "TABLE_NAME", Tables),
        Text("sp_tables_rowset2 all", "EXEC [{db}].[sys].sp_tables_rowset2 NULL, NULL;", RowRule.Keyed, "TABLE_NAME", Tables),
        Text("sp_columns_100_rowset orders", "EXEC [{db}].[sys].sp_columns_100_rowset N'orders', N'dbo', NULL;"),
        Text("sp_columns_100_rowset type_zoo", "EXEC [{db}].[sys].sp_columns_100_rowset N'type_zoo', N'dbo', NULL;"),
        Text("sp_foreign_keys_rowset", "EXEC [{db}].[sys].sp_foreign_keys_rowset N'orders', N'dbo', NULL, NULL, N'{db}';"),
        Text("sp_foreign_keys_rowset2", "EXEC [{db}].[sys].sp_foreign_keys_rowset2 N'orders', N'dbo', NULL, NULL, N'{db}';"),
        Text("sp_indexes_100_rowset", "EXEC [{db}].[sys].sp_indexes_100_rowset N'orders', NULL, N'dbo';"),
        Text("import orders (oledb)", "select * from \"{db}\".\"dbo\".\"orders\"", RowRule.ShapeOnly),

        // --- The same catalog calls, made the way Power BI makes them: through the drivers --------

        Odbc("SQLTables catalogs", (o, db) => o.Tables("%", "", "", ""), RowRule.Keyed, "TABLE_CAT", ["master", "msdb", "daffydb"]),
        Odbc("SQLTables TABLE,VIEW", (o, db) => o.Tables(null, "%", null, "'TABLE','VIEW'"), RowRule.Keyed, "TABLE_NAME", Tables),
        Odbc("SQLColumns orders", (o, db) => o.Columns(db, "dbo", "orders", null)),
        Odbc("SQLColumns type_zoo", (o, db) => o.Columns(db, "dbo", "type_zoo", null)),
        Odbc("SQLGetTypeInfo", (o, db) => o.TypeInfo(RawOdbc.AllTypes)),
        Odbc("SQLPrimaryKeys orders", (o, db) => o.PrimaryKeys(db, "dbo", "orders")),
        Odbc("SQLForeignKeys referenced", (o, db) => o.ForeignKeys(db, "dbo", "orders", null, null, null)),
        Odbc("SQLForeignKeys referencing", (o, db) => o.ForeignKeys(null, null, null, db, "dbo", "orders")),

        OleDbSchema("CATALOGS", OleDbSchemaGuid.Catalogs, null, RowRule.Keyed, "CATALOG_NAME", ["master", "msdb", "daffydb"]),
        OleDbSchema("SCHEMATA", OleDbSchemaGuid.Schemata, null, RowRule.Keyed, "SCHEMA_NAME", ["dbo"]),
        OleDbSchema("TABLES dbo", OleDbSchemaGuid.Tables, [null, "dbo", null, null], RowRule.Keyed, "TABLE_NAME", Tables),
        OleDbSchema("COLUMNS orders", OleDbSchemaGuid.Columns, [null, "dbo", "orders", null]),
        OleDbSchema("COLUMNS type_zoo", OleDbSchemaGuid.Columns, [null, "dbo", "type_zoo", null]),
        OleDbSchema("FOREIGN_KEYS orders", OleDbSchemaGuid.Foreign_Keys, [null, "dbo", "orders", null, null, null]),
        OleDbSchema("INDEXES orders", OleDbSchemaGuid.Indexes, [null, "dbo", null, null, "orders"]),

        // --- Power BI's SQL Server connector, through SqlClient: the navigator, then a table ------

        Text("pbi: databases",
             "select d.name as Name from sys.databases d where d.name not in ('master', 'model', 'msdb', 'tempdb') and d.is_distributor = 0 and d.state = 0"),
        Text("pbi: tables", """
            select t.[TABLE_CATALOG], t.[TABLE_SCHEMA], t.[TABLE_NAME], t.[TABLE_TYPE], tv.create_date [CREATED_DATE], tv.modify_date [MODIFIED_DATE], cast(e.value as nvarchar(max)) [DESCRIPTION]
            from [INFORMATION_SCHEMA].[TABLES] t
            join sys.schemas s on s.name = t.[TABLE_SCHEMA]
            join sys.objects tv on tv.name = t.[TABLE_NAME] and tv.schema_id = s.schema_id and tv.parent_object_id = 0
            left outer join sys.extended_properties e on tv.object_id = e.major_id and e.minor_id = 0 and e.class = 1 and e.name = 'MS_Description'
            where 1=1 and 1=1
            """, RowRule.Keyed, "TABLE_NAME", Tables),
        Text("pbi: routines", """
            select r.[ROUTINE_SCHEMA], r.[ROUTINE_NAME], r.[ROUTINE_TYPE], p.create_date [CREATED_DATE], p.modify_date [MODIFIED_DATE], cast(e.value as nvarchar(max)) [DESCRIPTION]
            from [INFORMATION_SCHEMA].[ROUTINES] r
            join sys.schemas s on s.name = r.[ROUTINE_SCHEMA]
            join sys.objects p on p.name = r.[ROUTINE_NAME] and p.schema_id = s.schema_id and p.parent_object_id = 0
            left outer join sys.extended_properties e on p.object_id = e.major_id and e.minor_id = 0 and e.class = 1 and e.name = 'MS_Description'
            where 1=1 and 1=1
            """),
        // As captured, but for one table: "where 1=1" lists every column of every table, in no
        // order either server promises, and column names repeat between tables.
        Text("pbi: columns orders", """
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
            where 1=1 and o.name = N'orders';
            """, RowRule.Keyed, "COLUMN_NAME", ["order_id", "customer_id", "product", "quantity", "unit_price", "order_ts"]),

        Text("pbi: foreign keys", """
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
            """),

        // --- Values: one column of every type DaffyDB maps, read back by each client -------------

        Text("type_zoo values", "select * from \"{db}\".\"dbo\".\"type_zoo\""),
        Odbc("type_zoo values", (o, db) => o.Execute($"select * from \"{db}\".\"dbo\".\"type_zoo\"")),
        new("oledb", "type_zoo values", RowRule.Exact, t => OleDbQuery(t, "select * from \"{db}\".\"dbo\".\"type_zoo\"")),
    ];

    private static Check Text(string name, string sql, RowRule rows = RowRule.Exact,
                              string? keyColumn = null, string[]? keys = null) =>
        new("sql", name, rows, t => Sql(t, sql), keyColumn, keys);

    private static Check Odbc(string name, Func<RawOdbc, string, List<ResultSetShape>> call, RowRule rows = RowRule.Exact,
                              string? keyColumn = null, string[]? keys = null) =>
        new("odbc", name, rows, t => { using var odbc = new RawOdbc(t.Odbc); return call(odbc, t.Database); }, keyColumn, keys);

    private static Check OleDbSchema(string name, Guid schema, object?[]? restrictions, RowRule rows = RowRule.Exact,
                                     string? keyColumn = null, string[]? keys = null) =>
        new("oledb", name, rows, t =>
        {
            using var connection = OpenOleDb(t);
            var table = connection.GetOleDbSchemaTable(schema, restrictions)
                        ?? throw new InvalidOperationException("the provider returned no schema rowset");
            return [FromTable(table)];
        }, keyColumn, keys);

    private static List<ResultSetShape> Sql(Target target, string sql)
    {
        using var connection = new SqlConnection(target.SqlClient);
        connection.Open();
        using var command = new SqlCommand(sql.Replace("{db}", target.Database), connection) { CommandTimeout = 15 };
        using var reader = command.ExecuteReader();

        var sets = new List<ResultSetShape>();
        do
        {
            if (reader.FieldCount == 0) continue;

            var columns = reader.GetColumnSchema()
                .Select(c => new ColumnShape(c.ColumnName, c.DataTypeName ?? "?", c.ColumnSize ?? 0, c.AllowDBNull ?? true))
                .ToList();

            var rows = new List<List<string?>>();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Values.Format(reader.GetValue(i))).ToList());

            sets.Add(new ResultSetShape(columns, rows.Count, rows));
        }
        while (reader.NextResult());

        return sets;
    }

    private static OleDbConnection OpenOleDb(Target target)
    {
        var connection = new OleDbConnection(target.OleDb);
        connection.Open();
        return connection;
    }

    private static List<ResultSetShape> OleDbQuery(Target target, string sql)
    {
        using var connection = OpenOleDb(target);
        using var command = new OleDbCommand(sql.Replace("{db}", target.Database), connection) { CommandTimeout = 15 };
        using var reader = command.ExecuteReader();

        var sets = new List<ResultSetShape>();
        do
        {
            if (reader.FieldCount == 0) continue;

            var schema = reader.GetSchemaTable()!;
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new ColumnShape(reader.GetName(i), reader.GetDataTypeName(i),
                                             Convert.ToInt64(schema.Rows[i]["ColumnSize"]),
                                             schema.Rows[i]["AllowDBNull"] is true))
                .ToList();

            var rows = new List<List<string?>>();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Values.Format(reader.GetValue(i))).ToList());

            sets.Add(new ResultSetShape(columns, rows.Count, rows));
        }
        while (reader.NextResult());

        return sets;
    }

    private static ResultSetShape FromTable(DataTable table) => new(
        table.Columns.Cast<DataColumn>()
             .Select(c => new ColumnShape(c.ColumnName, c.DataType.Name, c.MaxLength, c.AllowDBNull))
             .ToList(),
        table.Rows.Count,
        table.Rows.Cast<DataRow>().Select(r => r.ItemArray.Select(Values.Format).ToList()).ToList());
}
