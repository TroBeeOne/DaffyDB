using static TdsProxy.Sql.Shape;

namespace TdsProxy.Sql;

/// <summary>
/// The procedures behind OLE DB's schema rowsets, which MSOLEDBSQL calls for CATALOGS, SCHEMATA,
/// TABLES, COLUMNS, FOREIGN_KEYS and INDEXES - Power BI's OLE DB navigator asks for all six.
///
/// Each follows SQL Server's own definition, read from LocalDB with OBJECT_DEFINITION, and answers
/// in the shape recorded in src/DriverProbe/reference/localdb.json. Two of SQL Server's sources read
/// internal views (spt_tables_view, spt_columns_view) that even a sysadmin cannot see; what those
/// contribute - OLE DB's type codes and column flags - is taken from SQL Server's answer for a
/// column of every type. Most procedures come in variants that differ only in which restriction is
/// required; the provider picks one by the restrictions it was given.
/// </summary>
internal static class OleDbProcedures
{
    public static IEnumerable<SystemProcedure> All =>
    [
        Catalogs, Catalogs2, Schemata, Tables, Tables2, Columns, Columns2,
        ForeignKeys, ForeignKeys2, ForeignKeys3, Indexes, Indexes2,
    ];

    private static readonly IReadOnlyList<Query.ColumnDef> CatalogShape = [Name("CATALOG_NAME"), NVarChar("DESCRIPTION", 1)];

    private const string CatalogSql = """
        SELECT name AS CATALOG_NAME, NULL AS DESCRIPTION
        FROM sys.databases
        WHERE {0}
        ORDER BY 1
        """;

    private static readonly SystemProcedure Catalogs = new("sp_catalogs_rowset",
        string.Format(CatalogSql, "$catalog_name IS NULL OR name = $catalog_name"),
        Parameters: ["catalog_name"], Columns: CatalogShape);

    private static readonly SystemProcedure Catalogs2 = new("sp_catalogs_rowset2",
        string.Format(CatalogSql, "true"), Columns: CatalogShape);

    /// <summary>
    /// Only schemas that hold something, as in SQL Server, whose query joins the schemas to the
    /// objects in them. Its character set name is NULL there too: CollationPropertyFromId(-1, ...)
    /// answers nothing on a current server.
    /// </summary>
    private static readonly SystemProcedure Schemata = new("sp_schemata_rowset", """
        SELECT DISTINCT current_database() AS CATALOG_NAME, s.name AS SCHEMA_NAME, s.name AS SCHEMA_OWNER,
               'master' AS DEFAULT_CHARACTER_SET_CATALOG, 'dbo' AS DEFAULT_CHARACTER_SET_SCHEMA,
               NULL AS DEFAULT_CHARACTER_SET_NAME
        FROM sys.schemas s
        JOIN sys.objects o ON o.schema_id = s.schema_id
        WHERE ($schema_name IS NULL OR s.name = $schema_name)
          AND ($schema_owner IS NULL OR s.name = $schema_owner)
        ORDER BY 2
        """,
        Parameters: ["schema_name", "schema_owner"],
        Columns: [Name("CATALOG_NAME"), Name("SCHEMA_NAME"), Name("SCHEMA_OWNER"),
                  Name("DEFAULT_CHARACTER_SET_CATALOG"), Name("DEFAULT_CHARACTER_SET_SCHEMA"),
                  Name("DEFAULT_CHARACTER_SET_NAME")]);

    private static readonly IReadOnlyList<Query.ColumnDef> TableShape =
    [
        Name("TABLE_CATALOG"), Name("TABLE_SCHEMA"), Name("TABLE_NAME"), NVarChar("TABLE_TYPE", 30),
        Guid("TABLE_GUID"), NVarChar("DESCRIPTION", 1), Int("TABLE_PROPID"), DateTime("DATE_CREATED"),
        DateTime("DATE_MODIFIED"),
    ];

    /// <summary>DATE_MODIFIED is NULL for a table in SQL Server's answer, and so it is here.</summary>
    private const string TableSql = """
        SELECT current_database() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, o.name AS TABLE_NAME,
               CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END AS TABLE_TYPE,
               NULL AS TABLE_GUID, NULL AS DESCRIPTION, NULL AS TABLE_PROPID,
               o.create_date AS DATE_CREATED, NULL AS DATE_MODIFIED
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE {0}
          AND ($table_schema IS NULL OR s.name = $table_schema)
          AND ($table_type IS NULL OR $table_type = CASE o.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END)
        ORDER BY 4, 2, 3
        """;

    private static readonly SystemProcedure Tables = new("sp_tables_rowset",
        string.Format(TableSql, "o.name = $table_name"),
        Parameters: ["table_name", "table_schema", "table_type"], Columns: TableShape);

    private static readonly SystemProcedure Tables2 = new("sp_tables_rowset2",
        string.Format(TableSql, "true"),
        Parameters: ["table_schema", "table_type"], Columns: TableShape);

    private static readonly IReadOnlyList<Query.ColumnDef> ColumnShape =
    [
        Name("TABLE_CATALOG"), Name("TABLE_SCHEMA"), Name("TABLE_NAME"), Name("COLUMN_NAME"),
        Guid("COLUMN_GUID"), Int("COLUMN_PROPID"), Int("ORDINAL_POSITION"), Bit("COLUMN_HASDEFAULT"),
        NVarChar("COLUMN_DEFAULT", 2000), Int("COLUMN_FLAGS"), Bit("IS_NULLABLE"), Small("DATA_TYPE"),
        Guid("TYPE_GUID"), Int("CHARACTER_MAXIMUM_LENGTH"), Int("CHARACTER_OCTET_LENGTH"),
        Small("NUMERIC_PRECISION"), Small("NUMERIC_SCALE"), Int("DATETIME_PRECISION"),
        Name("CHARACTER_SET_CATALOG"), Name("CHARACTER_SET_SCHEMA"), Name("CHARACTER_SET_NAME"),
        Name("COLLATION_CATALOG"), Name("COLLATION_SCHEMA"), Name("COLLATION_NAME"),
        Name("DOMAIN_CATALOG"), Name("DOMAIN_SCHEMA"), Name("DOMAIN_NAME"), NVarChar("DESCRIPTION", 1),
        Int("COLUMN_LCID"), Int("COLUMN_COMPFLAGS"), Int("COLUMN_SORTID"), Binary("COLUMN_TDSCOLLATION", 5),
        Bit("IS_COMPUTED"), Name("SS_XML_SCHEMACOLLECTION_CATALOGNAME"), Name("SS_XML_SCHEMACOLLECTION_SCHEMANAME"),
        Name("SS_XML_SCHEMACOLLECTIONNAME"), Name("SS_UDT_CATALOGNAME"), Name("SS_UDT_SCHEMANAME"),
        Name("SS_UDT_NAME"), NVarChar("SS_UDT_ASSEMBLY_TYPENAME", 4000), Bit("SS_IS_SPARSE"),
        Bit("SS_IS_COLUMN_SET"),
    ];

    /// <summary>
    /// The COLUMNS rowset. DATA_TYPE is OLE DB's DBTYPE, not ODBC's code: int is DBTYPE_I4 (3),
    /// nvarchar DBTYPE_WSTR (130), datetime2 DBTYPE_DBTIMESTAMP (135). COLUMN_FLAGS is built from
    /// DBCOLUMNFLAGS exactly as SQL Server builds it: WRITE (0x4) always; ISFIXEDLENGTH (0x10) for a
    /// fixed-size type; ISLONG and MAYDEFER (0x82) for a (max) one; ISNULLABLE and MAYBENULL (0x60)
    /// for a nullable column; and SQL Server's own 0x40000000 on time and datetime2, whose scale is
    /// variable. Text columns carry the server collation's name, LCID, comparison flags and the five
    /// collation bytes TDS uses. Every row gets sort id 52, binary and numeric ones included -
    /// SQL Server's answer does the same.
    /// </summary>
    private const string ColumnSql = """
        WITH oledb(system_type_id, data_type, fixed, variable_scale, numeric_precision, datetime_precision) AS (
            VALUES (48,  17,  true,  false, 3,    NULL),
                   (52,  2,   true,  false, 5,    NULL),
                   (56,  3,   true,  false, 10,   NULL),
                   (127, 20,  true,  false, 19,   NULL),
                   (59,  4,   true,  false, 7,    NULL),
                   (62,  5,   true,  false, 15,   NULL),
                   (106, 131, true,  false, NULL, NULL),
                   (104, 11,  true,  false, NULL, NULL),
                   (40,  133, true,  false, NULL, 0),
                   (41,  145, true,  true,  NULL, 7),
                   (42,  135, true,  true,  NULL, 7),
                   (231, 130, false, false, NULL, NULL),
                   (165, 128, false, false, NULL, NULL),
                   (36,  72,  true,  false, NULL, NULL))
        SELECT current_database() AS TABLE_CATALOG, s.name AS TABLE_SCHEMA, o.name AS TABLE_NAME,
               c.name AS COLUMN_NAME, NULL AS COLUMN_GUID, NULL AS COLUMN_PROPID,
               c.column_id AS ORDINAL_POSITION, false AS COLUMN_HASDEFAULT, NULL AS COLUMN_DEFAULT,
               4 | CASE WHEN t.fixed THEN 16 ELSE 0 END
                 | CASE WHEN NOT t.fixed AND c.max_length < 0 THEN 130 ELSE 0 END
                 | CASE WHEN c.is_nullable = 1 THEN 96 ELSE 0 END
                 | CASE WHEN t.variable_scale THEN 1073741824 ELSE 0 END AS COLUMN_FLAGS,
               c.is_nullable = 1 AS IS_NULLABLE,
               t.data_type AS DATA_TYPE,
               NULL AS TYPE_GUID,
               CASE c.system_type_id WHEN 231 THEN greatest(c.max_length, 0) // 2
                                     WHEN 165 THEN greatest(c.max_length, 0) END AS CHARACTER_MAXIMUM_LENGTH,
               CASE WHEN c.system_type_id IN (231, 165) THEN greatest(c.max_length, 0) END AS CHARACTER_OCTET_LENGTH,
               CASE c.system_type_id WHEN 106 THEN c.precision ELSE t.numeric_precision END AS NUMERIC_PRECISION,
               CASE c.system_type_id WHEN 106 THEN c.scale END AS NUMERIC_SCALE,
               t.datetime_precision AS DATETIME_PRECISION,
               CASE WHEN c.system_type_id = 231 THEN 'master' END AS CHARACTER_SET_CATALOG,
               CASE WHEN c.system_type_id = 231 THEN 'dbo' END AS CHARACTER_SET_SCHEMA,
               CASE WHEN c.system_type_id = 231 THEN 'iso_1' END AS CHARACTER_SET_NAME,
               CASE WHEN c.system_type_id = 231 THEN 'master' END AS COLLATION_CATALOG,
               CASE WHEN c.system_type_id = 231 THEN 'dbo' END AS COLLATION_SCHEMA,
               CASE WHEN c.system_type_id = 231 THEN 'SQL_Latin1_General_CP1_CI_AS' END AS COLLATION_NAME,
               NULL AS DOMAIN_CATALOG, NULL AS DOMAIN_SCHEMA, NULL AS DOMAIN_NAME, NULL AS DESCRIPTION,
               CASE WHEN c.system_type_id = 231 THEN 1033 END AS COLUMN_LCID,
               CASE WHEN c.system_type_id = 231 THEN 196609 END AS COLUMN_COMPFLAGS,
               52 AS COLUMN_SORTID,
               CASE WHEN c.system_type_id = 231 THEN unhex('0904D00034') END AS COLUMN_TDSCOLLATION,
               false AS IS_COMPUTED,
               NULL AS SS_XML_SCHEMACOLLECTION_CATALOGNAME, NULL AS SS_XML_SCHEMACOLLECTION_SCHEMANAME,
               NULL AS SS_XML_SCHEMACOLLECTIONNAME, NULL AS SS_UDT_CATALOGNAME, NULL AS SS_UDT_SCHEMANAME,
               NULL AS SS_UDT_NAME, NULL AS SS_UDT_ASSEMBLY_TYPENAME,
               false AS SS_IS_SPARSE, false AS SS_IS_COLUMN_SET
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN oledb t ON t.system_type_id = c.system_type_id
        WHERE {0}
          AND ($table_schema IS NULL OR s.name = $table_schema)
          AND ($column_name IS NULL OR c.name = $column_name)
        ORDER BY 1, 2, 3, 7
        """;

    private static readonly SystemProcedure Columns = new("sp_columns_100_rowset",
        string.Format(ColumnSql, "o.name = $table_name"),
        Parameters: ["table_name", "table_schema", "column_name"], Columns: ColumnShape);

    private static readonly SystemProcedure Columns2 = new("sp_columns_100_rowset2",
        string.Format(ColumnSql, "true"),
        Parameters: ["table_schema", "column_name"], Columns: ColumnShape);

    /// <summary>
    /// FOREIGN_KEYS. Nothing mounted from a file has one, so all three variants answer with no rows -
    /// in the right shape, so that "none" is what the provider reports. The variants differ only in
    /// which table is named first.
    /// </summary>
    private static readonly IReadOnlyList<Query.ColumnDef> ForeignKeyShape =
    [
        Name("PK_TABLE_CATALOG"), Name("PK_TABLE_SCHEMA"), Name("PK_TABLE_NAME"), Name("PK_COLUMN_NAME"),
        Guid("PK_COLUMN_GUID"), Int("PK_COLUMN_PROPID"), Name("FK_TABLE_CATALOG"), Name("FK_TABLE_SCHEMA"),
        Name("FK_TABLE_NAME"), Name("FK_COLUMN_NAME"), Guid("FK_COLUMN_GUID"), Int("FK_COLUMN_PROPID"),
        Int("ORDINAL"), NVarChar("UPDATE_RULE", 11), NVarChar("DELETE_RULE", 11), Name("PK_NAME"),
        Name("FK_NAME"), Small("DEFERRABILITY"),
    ];

    private static readonly string NoForeignKeys = Empty(18);

    private static readonly SystemProcedure ForeignKeys = new("sp_foreign_keys_rowset", NoForeignKeys,
        Parameters: ["pk_table_name", "pk_table_schema", "foreignkey_tab_name", "foreignkey_tab_schema",
                     "foreignkey_tab_catalog"],
        Columns: ForeignKeyShape);

    private static readonly SystemProcedure ForeignKeys2 = new("sp_foreign_keys_rowset2", NoForeignKeys,
        Parameters: ["foreignkey_tab_name", "foreignkey_tab_schema", "pk_table_name", "pk_table_schema",
                     "pk_table_catalog"],
        Columns: ForeignKeyShape);

    private static readonly SystemProcedure ForeignKeys3 = new("sp_foreign_keys_rowset3", NoForeignKeys,
        Parameters: ["pk_table_schema", "pk_table_catalog", "foreignkey_tab_schema", "foreignkey_tab_catalog"],
        Columns: ForeignKeyShape);

    /// <summary>
    /// INDEXES. A mounted file is a heap: SQL Server lists no index rows for a heap, and neither
    /// does DaffyDB.
    /// </summary>
    private static readonly IReadOnlyList<Query.ColumnDef> IndexShape =
    [
        Name("TABLE_CATALOG"), Name("TABLE_SCHEMA"), Name("TABLE_NAME"), Name("INDEX_CATALOG"),
        Name("INDEX_SCHEMA"), Name("INDEX_NAME"), Bit("PRIMARY_KEY"), Bit("UNIQUE"), Bit("CLUSTERED"),
        Small("TYPE"), Int("FILL_FACTOR"), Int("INITIAL_SIZE"), Int("NULLS"), Bit("SORT_BOOKMARKS"),
        Bit("AUTO_UPDATE"), Int("NULL_COLLATION"), Int("ORDINAL_POSITION"), Name("COLUMN_NAME"),
        Guid("COLUMN_GUID"), Int("COLUMN_PROPID"), Small("COLLATION"), Int("CARDINALITY"), Int("PAGES"),
        NVarCharMax("FILTER_CONDITION"), Bit("INTEGRATED"), Int("STATUS"),
    ];

    private static readonly SystemProcedure Indexes = new("sp_indexes_100_rowset", Empty(26),
        Parameters: ["table_name", "index_name", "table_schema"], Columns: IndexShape);

    private static readonly SystemProcedure Indexes2 = new("sp_indexes_100_rowset2", Empty(26),
        Parameters: ["index_name", "table_schema"], Columns: IndexShape);

    /// <summary>A query with the right number of columns and no rows; the declared shape does the rest.</summary>
    private static string Empty(int columns) =>
        "SELECT " + string.Join(", ", Enumerable.Repeat("NULL", columns)) + " WHERE false";
}
