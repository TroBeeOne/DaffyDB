using static TdsProxy.Sql.Shape;

namespace TdsProxy.Sql;

/// <summary>
/// The procedures behind ODBC's catalog functions: SQLTables is sp_tables, SQLColumns is
/// sp_columns_100, SQLGetTypeInfo is sp_datatype_info_100, SQLPrimaryKeys is sp_pkeys and
/// SQLForeignKeys is sp_fkeys. Power BI's ODBC connector calls exactly these, in that order, before
/// it reads a row.
///
/// Each answers in SQL Server's shape - column names, types and sizes as recorded from LocalDB in
/// src/DriverProbe/reference/localdb.json - because the driver maps these results onto ODBC's by
/// position. The contents come from DaffyDB's own sys views, so they describe whatever is mounted.
/// </summary>
internal static class OdbcProcedures
{
    public static IEnumerable<SystemProcedure> All => [Tables, Columns, DataTypeInfo, PrimaryKeys, ForeignKeys, Statistics];

    /// <summary>
    /// SQLTables. Three argument combinations are not searches but enumerations, exactly as in SQL
    /// Server: a qualifier of '%' with empty owner and name lists the databases - Power BI's first
    /// call - an owner of '%' lists the schemas, and a type of '%' lists the kinds of table. The
    /// tests are on empty strings, never on NULL: NULL means "any", and a NULL name with an owner of
    /// '%' is an ordinary search for every table.
    /// </summary>
    private static readonly SystemProcedure Tables = new("sp_tables", """
        SELECT name AS TABLE_QUALIFIER, NULL AS TABLE_OWNER, NULL AS TABLE_NAME, NULL AS TABLE_TYPE, NULL AS REMARKS
        FROM sys.databases
        WHERE $table_qualifier = '%' AND $table_owner = '' AND $table_name = ''
        UNION ALL
        SELECT NULL, name, NULL, NULL, NULL
        FROM sys.schemas
        WHERE $table_owner = '%' AND $table_qualifier = '' AND $table_name = ''
        UNION ALL
        SELECT NULL, NULL, NULL, kind, NULL
        FROM (VALUES ('SYSTEM TABLE'), ('TABLE'), ('VIEW')) kinds(kind)
        WHERE $table_type = '%' AND $table_qualifier = '' AND $table_owner = '' AND $table_name = ''
        UNION ALL
        SELECT current_database(), s.name, o.name, CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END, NULL
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE NOT coalesce($table_qualifier = '%' AND $table_owner = '' AND $table_name = '', false)
          AND NOT coalesce($table_owner = '%' AND $table_qualifier = '' AND $table_name = '', false)
          AND NOT coalesce($table_type = '%' AND $table_qualifier = '' AND $table_owner = '' AND $table_name = '', false)
          AND ($table_name IS NULL OR regexp_full_match(o.name, $table_name_like))
          AND ($table_owner IS NULL OR regexp_full_match(s.name, $table_owner_like))
          AND ($table_qualifier IS NULL OR regexp_full_match(current_database(), $table_qualifier_like))
          AND ($table_type IS NULL OR list_contains(
                list_transform(string_split(replace(upper($table_type), '''', ''), ','), k -> trim(k)),
                CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END))
        ORDER BY 4, 1, 2, 3
        """,
        Parameters: ["table_name", "table_owner", "table_qualifier", "table_type", "fUsePattern"],
        Columns: [Name("TABLE_QUALIFIER"), Name("TABLE_OWNER"), Name("TABLE_NAME"),
                  VarChar("TABLE_TYPE", 32), VarChar("REMARKS", 254)],
        Defaults: new Dictionary<string, object?> { ["fUsePattern"] = true },
        Patterns: ["table_name", "table_owner", "table_qualifier"]);

    /// <summary>
    /// SQLColumns. What ODBC reports for a type - its precision, length, radix and the rest - is a
    /// constant per type, taken from SQL Server's answer for a column of each. Only decimal, and a
    /// character or binary column with a declared length, depend on the column itself.
    /// </summary>
    private static readonly SystemProcedure Columns = new("sp_columns_100", """
        WITH odbc(system_type_id, type_name, data_type, sql_data_type, datetime_sub,
                  type_precision, type_length, type_scale, radix, ss_data_type) AS (
            VALUES (48,  'tinyint',          -6,   -6,   NULL, 3,    1,    0,    10,   38),
                   (52,  'smallint',         5,    5,    NULL, 5,    2,    0,    10,   38),
                   (56,  'int',              4,    4,    NULL, 10,   4,    0,    10,   38),
                   (127, 'bigint',           -5,   -5,   NULL, 19,   8,    0,    10,   108),
                   (59,  'real',             7,    7,    NULL, 24,   4,    NULL, 2,    109),
                   (62,  'float',            6,    6,    NULL, 53,   8,    NULL, 2,    109),
                   (106, 'decimal',          3,    3,    NULL, NULL, NULL, NULL, 10,   106),
                   (104, 'bit',              -7,   -7,   NULL, 1,    1,    NULL, NULL, 50),
                   (40,  'date',             91,   9,    1,    10,   6,    0,    NULL, 0),
                   (41,  'time',             -154, -154, 0,    16,   12,   7,    NULL, 0),
                   (42,  'datetime2',        93,   9,    3,    27,   16,   7,    NULL, 0),
                   (231, 'nvarchar',         -9,   -9,   NULL, NULL, NULL, NULL, NULL, 39),
                   (165, 'varbinary',        -3,   -3,   NULL, NULL, NULL, NULL, NULL, 37),
                   (36,  'uniqueidentifier', -11,  -11,  NULL, 36,   16,   NULL, NULL, 37))
        SELECT current_database() AS TABLE_QUALIFIER,
               s.name AS TABLE_OWNER,
               o.name AS TABLE_NAME,
               c.name AS COLUMN_NAME,
               t.data_type AS DATA_TYPE,
               t.type_name AS TYPE_NAME,
               -- A (max) column reports 0 for its precision and both lengths.
               CASE c.system_type_id
                    WHEN 106 THEN c.precision
                    WHEN 231 THEN CASE WHEN c.max_length < 0 THEN 0 ELSE c.max_length // 2 END
                    WHEN 165 THEN greatest(c.max_length, 0)
                    ELSE t.type_precision END AS PRECISION,
               CASE c.system_type_id
                    WHEN 106 THEN c.precision + 2
                    WHEN 231 THEN greatest(c.max_length, 0)
                    WHEN 165 THEN greatest(c.max_length, 0)
                    ELSE t.type_length END AS LENGTH,
               CASE c.system_type_id WHEN 106 THEN c.scale ELSE t.type_scale END AS SCALE,
               t.radix AS RADIX,
               CAST(c.is_nullable AS INTEGER) AS NULLABLE,
               NULL AS REMARKS,
               NULL AS COLUMN_DEF,
               t.sql_data_type AS SQL_DATA_TYPE,
               t.datetime_sub AS SQL_DATETIME_SUB,
               CASE WHEN c.system_type_id IN (231, 165) THEN greatest(c.max_length, 0) END AS CHAR_OCTET_LENGTH,
               c.column_id AS ORDINAL_POSITION,
               CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END AS IS_NULLABLE,
               0 AS SS_IS_SPARSE, 0 AS SS_IS_COLUMN_SET, 0 AS SS_IS_COMPUTED, 0 AS SS_IS_IDENTITY,
               NULL AS SS_UDT_CATALOG_NAME, NULL AS SS_UDT_SCHEMA_NAME, NULL AS SS_UDT_ASSEMBLY_TYPE_NAME,
               NULL AS SS_XML_SCHEMACOLLECTION_CATALOG_NAME, NULL AS SS_XML_SCHEMACOLLECTION_SCHEMA_NAME,
               NULL AS SS_XML_SCHEMACOLLECTION_NAME,
               t.ss_data_type AS SS_DATA_TYPE
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN odbc t ON t.system_type_id = c.system_type_id
        WHERE regexp_full_match(o.name, $table_name_like)
          AND ($table_owner IS NULL OR regexp_full_match(s.name, $table_owner_like))
          AND ($table_qualifier IS NULL OR lower($table_qualifier) = lower(current_database()))
          AND ($column_name IS NULL OR regexp_full_match(c.name, $column_name_like))
        ORDER BY 2, 3, 17
        """,
        Parameters: ["table_name", "table_owner", "table_qualifier", "column_name", "NameScope", "ODBCVer", "fUsePattern"],
        Columns:
        [
            Name("TABLE_QUALIFIER"), Name("TABLE_OWNER"), Name("TABLE_NAME"), Name("COLUMN_NAME"),
            Small("DATA_TYPE"), Name("TYPE_NAME"), Int("PRECISION"), Int("LENGTH"), Small("SCALE"), Small("RADIX"),
            Small("NULLABLE"), VarChar("REMARKS", 254), NVarChar("COLUMN_DEF", 4000), Small("SQL_DATA_TYPE"),
            Small("SQL_DATETIME_SUB"), Int("CHAR_OCTET_LENGTH"), Int("ORDINAL_POSITION"), VarChar("IS_NULLABLE", 254),
            Small("SS_IS_SPARSE"), Small("SS_IS_COLUMN_SET"), Small("SS_IS_COMPUTED"), Small("SS_IS_IDENTITY"),
            Name("SS_UDT_CATALOG_NAME"), Name("SS_UDT_SCHEMA_NAME"), NVarChar("SS_UDT_ASSEMBLY_TYPE_NAME", 4000),
            Name("SS_XML_SCHEMACOLLECTION_CATALOG_NAME"), Name("SS_XML_SCHEMACOLLECTION_SCHEMA_NAME"),
            Name("SS_XML_SCHEMACOLLECTION_NAME"), Tiny("SS_DATA_TYPE"),
        ],
        Defaults: new Dictionary<string, object?> { ["NameScope"] = 0, ["ODBCVer"] = 2, ["fUsePattern"] = true },
        Patterns: ["table_name", "table_owner", "column_name"]);

    /// <summary>
    /// SQLGetTypeInfo: every type SQL Server has, not just the ones DaffyDB produces, because a
    /// client maps the TYPE_NAME it reads from SQLColumns through this list. The rows are LocalDB's,
    /// in LocalDB's order.
    /// </summary>
    private static readonly SystemProcedure DataTypeInfo = new("sp_datatype_info_100", """
        SELECT TYPE_NAME, DATA_TYPE, PRECISION, LITERAL_PREFIX, LITERAL_SUFFIX, CREATE_PARAMS, NULLABLE,
               CASE_SENSITIVE, SEARCHABLE, UNSIGNED_ATTRIBUTE, MONEY, AUTO_INCREMENT, LOCAL_TYPE_NAME,
               MINIMUM_SCALE, MAXIMUM_SCALE, SQL_DATA_TYPE, SQL_DATETIME_SUB, NUM_PREC_RADIX,
               INTERVAL_PRECISION, USERTYPE
        FROM (VALUES
            (1, 'datetimeoffset', -155, 34, '''', '''', 'scale', 1, 0, 3, NULL, 0, NULL, 'datetimeoffset', 0, 7, -155, 0, NULL, NULL, 0),
            (2, 'time', -154, 16, '''', '''', 'scale', 1, 0, 3, NULL, 0, NULL, 'time', 0, 7, -154, 0, NULL, NULL, 0),
            (3, 'xml', -152, 0, 'N''', '''', NULL, 1, 1, 0, NULL, 0, NULL, 'xml', NULL, NULL, -152, NULL, NULL, NULL, 0),
            (4, 'sql_variant', -150, 8000, NULL, NULL, NULL, 1, 0, 2, NULL, 0, NULL, 'sql_variant', 0, 0, -150, NULL, 10, NULL, 0),
            (5, 'uniqueidentifier', -11, 36, '''', '''', NULL, 1, 0, 2, NULL, 0, NULL, 'uniqueidentifier', NULL, NULL, -11, NULL, NULL, NULL, 0),
            (6, 'ntext', -10, 1073741823, 'N''', '''', NULL, 1, 0, 1, NULL, 0, NULL, 'ntext', NULL, NULL, -10, NULL, NULL, NULL, 0),
            (7, 'nvarchar', -9, 4000, 'N''', '''', 'max length', 1, 0, 3, NULL, 0, NULL, 'nvarchar', NULL, NULL, -9, NULL, NULL, NULL, 0),
            (8, 'sysname', -9, 128, 'N''', '''', NULL, 0, 0, 3, NULL, 0, NULL, 'sysname', NULL, NULL, -9, NULL, NULL, NULL, 18),
            (9, 'nchar', -8, 4000, 'N''', '''', 'length', 1, 0, 3, NULL, 0, NULL, 'nchar', NULL, NULL, -8, NULL, NULL, NULL, 0),
            (10, 'bit', -7, 1, NULL, NULL, NULL, 1, 0, 2, NULL, 0, NULL, 'bit', 0, 0, -7, NULL, NULL, NULL, 16),
            (11, 'tinyint', -6, 3, NULL, NULL, NULL, 1, 0, 2, 1, 0, 0, 'tinyint', 0, 0, -6, NULL, 10, NULL, 5),
            (12, 'tinyint identity', -6, 3, NULL, NULL, NULL, 0, 0, 2, 1, 0, 1, 'tinyint identity', 0, 0, -6, NULL, 10, NULL, 5),
            (13, 'bigint', -5, 19, NULL, NULL, NULL, 1, 0, 2, 0, 0, 0, 'bigint', 0, 0, -5, NULL, 10, NULL, 0),
            (14, 'bigint identity', -5, 19, NULL, NULL, NULL, 0, 0, 2, 0, 0, 1, 'bigint identity', 0, 0, -5, NULL, 10, NULL, 0),
            (15, 'image', -4, 2147483647, '0x', NULL, NULL, 1, 0, 0, NULL, 0, NULL, 'image', NULL, NULL, -4, NULL, NULL, NULL, 20),
            (16, 'varbinary', -3, 8000, '0x', NULL, 'max length', 1, 0, 2, NULL, 0, NULL, 'varbinary', NULL, NULL, -3, NULL, NULL, NULL, 4),
            (17, 'binary', -2, 8000, '0x', NULL, 'length', 1, 0, 2, NULL, 0, NULL, 'binary', NULL, NULL, -2, NULL, NULL, NULL, 3),
            (18, 'timestamp', -2, 8, '0x', NULL, NULL, 0, 0, 2, NULL, 0, NULL, 'timestamp', NULL, NULL, -2, NULL, NULL, NULL, 80),
            (19, 'text', -1, 2147483647, '''', '''', NULL, 1, 0, 1, NULL, 0, NULL, 'text', NULL, NULL, -1, NULL, NULL, NULL, 19),
            (20, 'char', 1, 8000, '''', '''', 'length', 1, 0, 3, NULL, 0, NULL, 'char', NULL, NULL, 1, NULL, NULL, NULL, 1),
            (21, 'numeric', 2, 38, NULL, NULL, 'precision,scale', 1, 0, 2, 0, 0, 0, 'numeric', 0, 38, 2, NULL, 10, NULL, 10),
            (22, 'numeric() identity', 2, 38, NULL, NULL, 'precision', 0, 0, 2, 0, 0, 1, 'numeric() identity', 0, 0, 2, NULL, 10, NULL, 10),
            (23, 'decimal', 3, 38, NULL, NULL, 'precision,scale', 1, 0, 2, 0, 0, 0, 'decimal', 0, 38, 3, NULL, 10, NULL, 24),
            (24, 'money', 3, 19, '$', NULL, NULL, 1, 0, 2, 0, 1, 0, 'money', 4, 4, 3, NULL, 10, NULL, 11),
            (25, 'smallmoney', 3, 10, '$', NULL, NULL, 1, 0, 2, 0, 1, 0, 'smallmoney', 4, 4, 3, NULL, 10, NULL, 21),
            (26, 'decimal() identity', 3, 38, NULL, NULL, 'precision', 0, 0, 2, 0, 0, 1, 'decimal() identity', 0, 0, 3, NULL, 10, NULL, 24),
            (27, 'int', 4, 10, NULL, NULL, NULL, 1, 0, 2, 0, 0, 0, 'int', 0, 0, 4, NULL, 10, NULL, 7),
            (28, 'int identity', 4, 10, NULL, NULL, NULL, 0, 0, 2, 0, 0, 1, 'int identity', 0, 0, 4, NULL, 10, NULL, 7),
            (29, 'smallint', 5, 5, NULL, NULL, NULL, 1, 0, 2, 0, 0, 0, 'smallint', 0, 0, 5, NULL, 10, NULL, 6),
            (30, 'smallint identity', 5, 5, NULL, NULL, NULL, 0, 0, 2, 0, 0, 1, 'smallint identity', 0, 0, 5, NULL, 10, NULL, 6),
            (31, 'float', 6, 53, NULL, NULL, NULL, 1, 0, 2, 0, 0, 0, 'float', NULL, NULL, 6, NULL, 2, NULL, 8),
            (32, 'real', 7, 24, NULL, NULL, NULL, 1, 0, 2, 0, 0, 0, 'real', NULL, NULL, 7, NULL, 2, NULL, 23),
            (33, 'varchar', 12, 8000, '''', '''', 'max length', 1, 0, 3, NULL, 0, NULL, 'varchar', NULL, NULL, 12, NULL, NULL, NULL, 2),
            (34, 'date', 91, 10, '''', '''', NULL, 1, 0, 3, NULL, 0, NULL, 'date', NULL, 0, 9, 1, NULL, NULL, 0),
            (35, 'datetime2', 93, 27, '''', '''', 'scale', 1, 0, 3, NULL, 0, NULL, 'datetime2', 0, 7, 9, 3, NULL, NULL, 0),
            (36, 'datetime', 93, 23, '''', '''', NULL, 1, 0, 3, NULL, 0, NULL, 'datetime', 3, 3, 9, 3, NULL, NULL, 12),
            (37, 'smalldatetime', 93, 16, '''', '''', NULL, 1, 0, 3, NULL, 0, NULL, 'smalldatetime', 0, 0, 9, 3, NULL, NULL, 22)
        ) types(seq, TYPE_NAME, DATA_TYPE, PRECISION, LITERAL_PREFIX, LITERAL_SUFFIX, CREATE_PARAMS, NULLABLE,
                CASE_SENSITIVE, SEARCHABLE, UNSIGNED_ATTRIBUTE, MONEY, AUTO_INCREMENT, LOCAL_TYPE_NAME,
                MINIMUM_SCALE, MAXIMUM_SCALE, SQL_DATA_TYPE, SQL_DATETIME_SUB, NUM_PREC_RADIX,
                INTERVAL_PRECISION, USERTYPE)
        WHERE coalesce($data_type, 0) = 0 OR DATA_TYPE = $data_type
        ORDER BY seq
        """,
        Parameters: ["data_type", "ODBCVer"],
        Columns:
        [
            Name("TYPE_NAME"), Small("DATA_TYPE"), Int("PRECISION"), VarChar("LITERAL_PREFIX", 32),
            VarChar("LITERAL_SUFFIX", 32), VarChar("CREATE_PARAMS", 32), Small("NULLABLE"), Small("CASE_SENSITIVE"),
            Small("SEARCHABLE"), Small("UNSIGNED_ATTRIBUTE"), Small("MONEY"), Small("AUTO_INCREMENT"),
            Name("LOCAL_TYPE_NAME"), Small("MINIMUM_SCALE"), Small("MAXIMUM_SCALE"), Small("SQL_DATA_TYPE"),
            Small("SQL_DATETIME_SUB"), Int("NUM_PREC_RADIX"), Small("INTERVAL_PRECISION"), Small("USERTYPE"),
        ],
        Defaults: new Dictionary<string, object?> { ["data_type"] = 0, ["ODBCVer"] = 2 });

    /// <summary>
    /// SQLPrimaryKeys. Nothing mounted from a file has a key, so the answer is always empty - but
    /// in the right shape, which is what the driver needs to report "none" rather than an error.
    /// </summary>
    private static readonly SystemProcedure PrimaryKeys = new("sp_pkeys", """
        SELECT NULL, NULL, NULL, NULL, NULL, NULL WHERE false
        """,
        Parameters: ["table_name", "table_owner", "table_qualifier"],
        Columns: [Name("TABLE_QUALIFIER"), Name("TABLE_OWNER"), Name("TABLE_NAME"), Name("COLUMN_NAME"),
                  Small("KEY_SEQ"), Name("PK_NAME")]);

    /// <summary>SQLForeignKeys, in both directions. Empty for the same reason, and in the same way.</summary>
    private static readonly SystemProcedure ForeignKeys = new("sp_fkeys", """
        SELECT NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL WHERE false
        """,
        Parameters: ["pktable_name", "pktable_owner", "pktable_qualifier",
                     "fktable_name", "fktable_owner", "fktable_qualifier"],
        Columns:
        [
            Name("PKTABLE_QUALIFIER"), Name("PKTABLE_OWNER"), Name("PKTABLE_NAME"), Name("PKCOLUMN_NAME"),
            Name("FKTABLE_QUALIFIER"), Name("FKTABLE_OWNER"), Name("FKTABLE_NAME"), Name("FKCOLUMN_NAME"),
            Small("KEY_SEQ"), Small("UPDATE_RULE"), Small("DELETE_RULE"), Name("FK_NAME"), Name("PK_NAME"),
            Small("DEFERRABILITY"),
        ]);

    /// <summary>
    /// SQLStatistics: a table's indexes, and a first row of TYPE 0 about the table itself. A linked
    /// server asks for it before it plans a query on a remote table. Nothing here has an index, so
    /// that first row is all there is - and its row and page counts are NULL, which SQL Server
    /// takes to mean "not known", because counting would mean reading every file mounted.
    /// </summary>
    private static readonly SystemProcedure Statistics = new("sp_statistics_100", """
        SELECT current_database(), s.name, o.name, NULL, NULL, NULL, 0, NULL, NULL, NULL, NULL, NULL, NULL
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE lower(o.name) = lower($table_name)
          AND ($table_owner IS NULL OR lower(s.name) = lower($table_owner))
          AND ($table_qualifier IS NULL OR lower($table_qualifier) = lower(current_database()))
        ORDER BY 2, 3
        """,
        Parameters: ["table_name", "table_owner", "table_qualifier", "index_name", "is_unique", "accuracy"],
        Columns:
        [
            NVarChar("TABLE_QUALIFIER", 128), NVarChar("TABLE_OWNER", 128), Name("TABLE_NAME"), Small("NON_UNIQUE"),
            Name("INDEX_QUALIFIER"), Name("INDEX_NAME"), Small("TYPE"), Small("SEQ_IN_INDEX"), Name("COLUMN_NAME"),
            VarChar("COLLATION", 1), Int("CARDINALITY"), Int("PAGES"), VarChar("FILTER_CONDITION", 128),
        ],
        Defaults: new Dictionary<string, object?> { ["index_name"] = "%", ["is_unique"] = "N", ["accuracy"] = "Q" });
}
