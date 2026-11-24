using TdsProxy.Sql;

namespace TdsProxy.Query;

/// <summary>
/// A stand-in for the SQL Server system catalog, projected from DuckDB's own metadata.
///
/// Client tools do not ask "what tables are there" — they ask sys.objects joined to sys.schemas
/// filtered on sys.columns. None of that exists in DuckDB, so enough of it is faked here to let a
/// tool describe the database. Values that have no DuckDB equivalent (owners, recovery models, file
/// groups) are reported as whatever a simple single-database server would report.
///
/// Every column SQL Server declares as bit is a 0/1 DECIMAL(1,0) here, which has to satisfy two
/// clients at once. Object Explorer does arithmetic on these flags - it decides whether an index
/// backs a primary key with "is_primary_key + 2*is_unique_constraint = 1" - which a DuckDB BOOLEAN
/// cannot even bind, there being no *(INTEGER, BOOLEAN). And a query that selects a flag as it is,
/// as the Columns folder does with "clmns.is_nullable AS [Nullable]", has to receive a bit, because
/// SSMS casts the value straight to bool; an integer fails that cast with "Specified cast is not
/// valid". A DECIMAL(1,0) adds and compares like the integer it is, and no parquet or CSV file
/// produces one, so the result writer can send a DECIMAL(1,0) holding only 0 and 1 as the bit it
/// stands for. It was TINYINT until that cast turned up; a TINYINT is indistinguishable from data.
///
/// Each database gets its own copy, created inside its own catalog, as SQL Server gives each
/// database its own sys views: sys.objects in daffydb lists the data, and in master lists nothing.
/// A view resolves the names in its body against the catalog it lives in, so master.sys.objects
/// describes master even when asked from daffydb. Server-wide views such as sys.databases say the
/// same thing in both.
/// </summary>
internal static class SystemCatalog
{
    /// <summary>Every database as a VALUES row of name and id, for sys.databases.</summary>
    private static string DatabaseRows =>
        string.Join(", ", Databases.All.Select(d => $"('{d.Name}', {d.Id})"));

    /// <summary>
    /// A view with SQL Server's columns and no rows, for things DaffyDB has none of. Each column is
    /// given as "NAME TYPE", in DuckDB's type names.
    /// </summary>
    private static string Empty(string view, params string[] columns) =>
        $"CREATE OR REPLACE VIEW {view} AS SELECT " +
        string.Join(", ", columns.Select(c => c.Split(' ', 2)).Select(c => $"CAST(NULL AS {c[1]}) AS {c[0]}")) +
        " WHERE false";

    public static IReadOnlyList<string> Views(string databaseName) =>
    [
        "CREATE SCHEMA IF NOT EXISTS sys",

        // The one piece of the catalog that is not a projection. Everything mounted from a file is
        // a DuckDB view over read_parquet or read_csv_auto, but a client came looking for tables,
        // so the mount loop records what it mounted and sys.objects reports those as tables. A
        // view someone writes in an init script is absent from here and stays a view.
        "CREATE TABLE IF NOT EXISTS sys.mounted_objects (schema_name VARCHAR, object_name VARCHAR)",

        // Host and connection facts. SMO reads these immediately after login.
        """
        CREATE OR REPLACE VIEW sys.dm_os_host_info AS
        SELECT 'Windows' AS host_platform,
               'Windows' AS host_distribution,
               '10.0'    AS host_release,
               ''        AS host_service_pack_level,
               CAST(0 AS INTEGER)    AS host_sku,
               CAST(1033 AS INTEGER) AS os_language_version
        """,

        """
        CREATE OR REPLACE VIEW sys.dm_exec_connections AS
        SELECT CAST(51 AS INTEGER) AS session_id,
               'TCP'               AS net_transport,
               'TSQL'              AS protocol_type,
               CAST(4 AS INTEGER)  AS endpoint_id,
               '127.0.0.1'         AS local_net_address,
               CAST(1433 AS INTEGER) AS local_tcp_port,
               '127.0.0.1'         AS client_net_address
        """,

        // The endpoint every connection arrives on. SSMS before 22 asks, while connecting, whether
        // its connection is the dedicated admin connection, by joining sys.dm_exec_connections to
        // this on endpoint_id. SQL Server lists five endpoints, but DaffyDB has TCP alone, so the
        // one row is SQL Server's own for it: "TSQL Default TCP", id 4, not an admin endpoint.
        """
        CREATE OR REPLACE VIEW sys.endpoints AS
        SELECT 'TSQL Default TCP'   AS name,
               CAST(4 AS INTEGER)   AS endpoint_id,
               CAST(1 AS INTEGER)   AS principal_id,
               CAST(2 AS UTINYINT)  AS protocol,
               'TCP'                AS protocol_desc,
               CAST(2 AS UTINYINT)  AS type,
               'TSQL'               AS type_desc,
               CAST(0 AS UTINYINT)  AS state,
               'STARTED'            AS state_desc,
               0::DECIMAL(1,0)      AS is_admin_endpoint
        """,

        // SQL Server's session view, for the one fact SSMS reads from it on connecting to anything
        // but master: whether the login was authenticated by the database itself, which it asks
        // as "authenticating_database_id = 1". It was not; master did.
        """
        CREATE OR REPLACE VIEW sys.dm_exec_sessions AS
        SELECT CAST(51 AS SMALLINT)  AS session_id,
               CAST('2024-01-01' AS TIMESTAMP) AS login_time,
               'client'              AS host_name,
               'TDS proxy client'    AS program_name,
               'dbo'                 AS login_name,
               'running'             AS status,
               CAST(5 AS SMALLINT)   AS database_id,
               CAST(1 AS INTEGER)    AS authenticating_database_id,
               1::DECIMAL(1,0)       AS is_user_process
        """,

        $"""
        CREATE OR REPLACE VIEW sys.databases AS
        SELECT d.name,
               CAST(d.database_id AS INTEGER) AS database_id,
               CAST(NULL AS INTEGER) AS source_database_id,
               CAST(NULL AS BLOB)   AS owner_sid,
               CAST('2024-01-01' AS TIMESTAMP) AS create_date,
               CAST(170 AS SMALLINT) AS compatibility_level,
               'SQL_Latin1_General_CP1_CI_AS' AS collation_name,
               CAST(0 AS SMALLINT)  AS user_access,
               'MULTI_USER'         AS user_access_desc,
               0::DECIMAL(1,0)           AS is_read_only,
               0::DECIMAL(1,0)           AS is_auto_close_on,
               0::DECIMAL(1,0)           AS is_auto_shrink_on,
               CAST(0 AS SMALLINT)  AS state,
               'ONLINE'             AS state_desc,
               0::DECIMAL(1,0)           AS is_in_standby,
               0::DECIMAL(1,0)           AS is_cleanly_shutdown,
               0::DECIMAL(1,0)           AS is_supplemental_logging_enabled,
               CAST(0 AS SMALLINT)  AS snapshot_isolation_state,
               'OFF'                AS snapshot_isolation_state_desc,
               0::DECIMAL(1,0)           AS is_read_committed_snapshot_on,
               CAST(3 AS SMALLINT)  AS recovery_model,
               'SIMPLE'             AS recovery_model_desc,
               CAST(2 AS SMALLINT)  AS page_verify_option,
               'CHECKSUM'           AS page_verify_option_desc,
               1::DECIMAL(1,0)           AS is_auto_create_stats_on,
               0::DECIMAL(1,0)           AS is_auto_create_stats_incremental_on,
               1::DECIMAL(1,0)           AS is_auto_update_stats_on,
               0::DECIMAL(1,0)           AS is_auto_update_stats_async_on,
               0::DECIMAL(1,0)           AS is_ansi_null_default_on,
               0::DECIMAL(1,0)           AS is_ansi_nulls_on,
               0::DECIMAL(1,0)           AS is_ansi_padding_on,
               0::DECIMAL(1,0)           AS is_ansi_warnings_on,
               0::DECIMAL(1,0)           AS is_arithabort_on,
               0::DECIMAL(1,0)           AS is_concat_null_yields_null_on,
               0::DECIMAL(1,0)           AS is_numeric_roundabort_on,
               1::DECIMAL(1,0)           AS is_quoted_identifier_on,
               0::DECIMAL(1,0)           AS is_recursive_triggers_on,
               0::DECIMAL(1,0)           AS is_cursor_close_on_commit_on,
               0::DECIMAL(1,0)           AS is_local_cursor_default,
               0::DECIMAL(1,0)           AS is_fulltext_enabled,
               0::DECIMAL(1,0)           AS is_trustworthy_on,
               0::DECIMAL(1,0)           AS is_db_chaining_on,
               0::DECIMAL(1,0)           AS is_parameterization_forced,
               0::DECIMAL(1,0)           AS is_master_key_encrypted_by_server,
               -- Integers, not the DECIMAL(1,0) every other flag is. SMO reads these three only as
               -- is_published*1 + is_subscribed*2 + is_merge_published*4, its ReplicationOptions,
               -- which SQL Server answers as an int and SMO turns into an enum, and a decimal fails
               -- that ("must be an enum base or an underlying type"). No client known selects them
               -- on their own, where SQL Server would send a bit.
               CAST(0 AS INTEGER)        AS is_published,
               CAST(0 AS INTEGER)        AS is_subscribed,
               CAST(0 AS INTEGER)        AS is_merge_published,
               0::DECIMAL(1,0)           AS is_distributor,
               0::DECIMAL(1,0)           AS is_sync_with_backup,
               -- No Service Broker, so every database has the GUID SQL Server gives master: zeros.
               CAST('00000000-0000-0000-0000-000000000000' AS UUID) AS service_broker_guid,
               0::DECIMAL(1,0)           AS is_broker_enabled,
               CAST(0 AS SMALLINT)  AS log_reuse_wait,
               'NOTHING'            AS log_reuse_wait_desc,
               0::DECIMAL(1,0)           AS is_date_correlation_on,
               0::DECIMAL(1,0)           AS is_cdc_enabled,
               0::DECIMAL(1,0)           AS is_encrypted,
               0::DECIMAL(1,0)           AS is_honor_broker_priority_on,
               CAST(NULL AS INTEGER) AS replica_id,
               -- SQL Server fills these in only for a contained database, and none here is one.
               CAST(NULL AS SMALLINT) AS default_language_lcid,
               CAST(NULL AS VARCHAR) AS default_language_name,
               CAST(NULL AS INTEGER) AS default_fulltext_language_lcid,
               CAST(NULL AS VARCHAR) AS default_fulltext_language_name,
               CAST(NULL AS DECIMAL(1,0)) AS is_nested_triggers_on,
               CAST(NULL AS DECIMAL(1,0)) AS is_transform_noise_words_on,
               CAST(NULL AS SMALLINT) AS two_digit_year_cutoff,
               CAST(0 AS SMALLINT)  AS containment,
               'NONE'               AS containment_desc,
               CAST(0 AS INTEGER)   AS target_recovery_time_in_seconds,
               CAST(0 AS INTEGER)   AS delayed_durability,
               'DISABLED'           AS delayed_durability_desc,
               CAST(0 AS SMALLINT)  AS catalog_collation_type,
               'DATABASE_DEFAULT'   AS catalog_collation_type_desc,
               CAST(NULL AS VARCHAR) AS physical_database_name,
               0::DECIMAL(1,0)           AS is_temporal_history_retention_enabled,
               0::DECIMAL(1,0)           AS is_memory_optimized_enabled,
               0::DECIMAL(1,0)           AS is_ledger_on,
               0::DECIMAL(1,0)           AS is_accelerated_database_recovery_on,
               -- SQL Server 2025's, which a newer SMO than VS Code's reads with a database's properties.
               0::DECIMAL(1,0)           AS is_optimized_locking_on
        FROM (VALUES {DatabaseRows}) AS d(name, database_id)
        """,

        // Tools derive the server's default data directory from the master file's path.
        $"""
        CREATE OR REPLACE VIEW sys.database_files AS
        SELECT CAST(1 AS INTEGER) AS file_id,
               '{databaseName}'   AS name,
               'C:\Data\{databaseName}.mdf' AS physical_name,
               CAST(0 AS SMALLINT) AS type,
               'ROWS'             AS type_desc,
               CAST(8192 AS INTEGER) AS size,
               CAST(0 AS INTEGER) AS max_size,
               CAST(0 AS INTEGER) AS growth,
               CAST(0 AS SMALLINT) AS state,
               'ONLINE'           AS state_desc,
               0::DECIMAL(1,0)         AS is_read_only,
               CAST(1 AS INTEGER) AS data_space_id
        """,

        // Filtered to this catalog on purpose. DuckDB always has memory, system and temp attached
        // besides master, each with a main schema, so an unfiltered list repeats the name four
        // times - and since schema_id is a row number, the ties made every id unstable.
        //
        // main and sys are dropped for the same reason sys.objects skips them: nothing a client
        // should see lives there, and a schema listed with no objects under it is just a puzzle.
        $"""
        CREATE OR REPLACE VIEW sys.schemas AS
        SELECT schema_name AS name,
               CAST(row_number() OVER (ORDER BY schema_name) AS INTEGER) AS schema_id,
               CAST(1 AS INTEGER) AS principal_id
        FROM information_schema.schemata
        WHERE catalog_name = '{databaseName}'
          AND schema_name NOT IN ('main', 'sys')
        """,

        // Only user objects: the catalog's own views and DuckDB's internal schemas are not things a
        // client should be shown.
        //
        // A mounted file counts as a table even though DuckDB holds it as a view, and so does a
        // real CREATE TABLE. What is left - a view someone wrote - is reported as a view, which is
        // what makes sys.tables and sys.views two different lists rather than one and an empty one.
        $"""
        CREATE OR REPLACE VIEW sys.objects AS
        SELECT t.table_name AS name,
               CAST(row_number() OVER (ORDER BY t.table_schema, t.table_name) AS INTEGER) AS object_id,
               CAST(NULL AS INTEGER) AS principal_id,
               CAST(s.schema_id AS INTEGER) AS schema_id,
               CAST(0 AS INTEGER) AS parent_object_id,
               -- SQL Server types this char(2) and returns 'U ', but its comparisons ignore
               -- trailing blanks and DuckDB's do not, so 'U ' = 'U' is false here. A client sends
               -- the unpadded form as a parameter, so the unpadded form is what is stored.
               CASE WHEN m.object_name IS NOT NULL OR t.table_type = 'BASE TABLE'
                    THEN 'U' ELSE 'V' END AS type,
               CASE WHEN m.object_name IS NOT NULL OR t.table_type = 'BASE TABLE'
                    THEN 'USER_TABLE' ELSE 'VIEW' END AS type_desc,
               CAST('2024-01-01' AS TIMESTAMP) AS create_date,
               CAST('2024-01-01' AS TIMESTAMP) AS modify_date,
               0::DECIMAL(1,0) AS is_ms_shipped,
               0::DECIMAL(1,0) AS is_published,
               0::DECIMAL(1,0) AS is_schema_published
        FROM information_schema.tables t
        JOIN sys.schemas s ON s.name = t.table_schema
        LEFT JOIN sys.mounted_objects m
               ON m.schema_name = t.table_schema AND m.object_name = t.table_name
        WHERE t.table_schema NOT IN ('information_schema', 'pg_catalog', 'sys', 'main')
          -- information_schema spans every attached catalog: msdb's, and any an init script adds.
          AND t.table_catalog = '{databaseName}'
        """,

        """
        CREATE OR REPLACE VIEW sys.tables AS
        SELECT *,
               -- No data space of its own for large values, no FILESTREAM, ANSI NULLs on as DuckDB
               -- has them, and not replicated: LocalDB's answers for a plain table. VS Code reads
               -- them with a table's properties, before it will script "Select Top 1000 Rows".
               CAST(0 AS INTEGER) AS lob_data_space_id, CAST(NULL AS INTEGER) AS filestream_data_space_id,
               1::DECIMAL(1,0) AS uses_ansi_nulls, 0::DECIMAL(1,0) AS is_replicated,
               CAST(0 AS SMALLINT) AS lock_escalation, 'TABLE' AS lock_escalation_desc,
               0::DECIMAL(1,0) AS is_filetable, 0::DECIMAL(1,0) AS is_memory_optimized, 0::DECIMAL(1,0) AS is_external,
               CAST(0 AS SMALLINT) AS durability, 'SCHEMA_AND_DATA' AS durability_desc,
               CAST(0 AS SMALLINT) AS temporal_type, 'NON_TEMPORAL_TABLE' AS temporal_type_desc,
               CAST(NULL AS INTEGER) AS history_table_id, 0::DECIMAL(1,0) AS is_node, 0::DECIMAL(1,0) AS is_edge,
               -- Ledger tables are a SQL Server 2022 feature nothing here has, but Object Explorer
               -- reads ledger_type on every table it lists, so the column has to exist to say no.
               CAST(0 AS SMALLINT) AS ledger_type, 'NON_LEDGER_TABLE' AS ledger_type_desc,
               CAST(NULL AS INTEGER) AS ledger_view_id, 0::DECIMAL(1,0) AS is_dropped_ledger_table
        FROM sys.objects WHERE type = 'U'
        """,

        // A view's own columns beyond sys.objects. All of them are the boring answer, but a client
        // that selects them by name needs them to exist.
        """
        CREATE OR REPLACE VIEW sys.views AS
        SELECT *, CAST(0 AS SMALLINT) AS with_check_option,
               0::DECIMAL(1,0) AS is_date_correlation_view, 0::DECIMAL(1,0) AS is_tracked_by_cdc,
               0::DECIMAL(1,0) AS has_opaque_metadata, 0::DECIMAL(1,0) AS has_unchecked_assembly_data,
               0::DECIMAL(1,0) AS has_snapshot,
               -- As with tables: no ledger views, but VS Code's Views folder filters on these, and
               -- without them the folder fails rather than listing the views there are.
               CAST(0 AS SMALLINT) AS ledger_view_type, 'NON_LEDGER_VIEW' AS ledger_view_type_desc,
               0::DECIMAL(1,0) AS is_dropped_ledger_view
        FROM sys.objects WHERE type = 'V'
        """,

        // One property, on daffydb itself: its description, saying what it is. Nothing else carries
        // one, but the view has to exist regardless - a client works out whether an object is a
        // system object by looking for the microsoft_database_tools_support property on it, and
        // that lookup is a scalar subquery, not an optional join.
        //
        // The property is on the database (class 0, major_id 0), so it can never answer a lookup
        // for an object: those ask for class 1, and object ids start at 1. SQL Server types value
        // as sql_variant, which DaffyDB does not send; it goes out as nvarchar, which is what the
        // clients that read it cast it to anyway.
        $"""
        CREATE OR REPLACE VIEW sys.extended_properties AS
        SELECT CAST(0 AS SMALLINT) AS class,
               'DATABASE'          AS class_desc,
               CAST(0 AS INTEGER)  AS major_id,
               CAST(0 AS INTEGER)  AS minor_id,
               'MS_Description'    AS name,
               '{Databases.Description}' AS value
        WHERE {(databaseName == Databases.Data ? "true" : "false")}
        """,

        $"""
        CREATE OR REPLACE VIEW sys.columns AS
        SELECT o.object_id,
               c.column_name AS name,
               CAST(c.ordinal_position AS INTEGER) AS column_id,
               CAST(tid.id AS INTEGER) AS system_type_id,
               CAST(tid.id AS INTEGER) AS user_type_id,
               CAST(tid.length AS SMALLINT) AS max_length,
               -- SQL Server's precision for each type, not DuckDB's: information_schema gives an
               -- INTEGER a numeric_precision of 32, in bits, where sys.columns says 10.
               CAST(CASE tid.id
                      WHEN 104 THEN 1  WHEN 48 THEN 3  WHEN 52 THEN 5  WHEN 56 THEN 10 WHEN 127 THEN 19
                      WHEN 59 THEN 24  WHEN 62 THEN 53 WHEN 40 THEN 10 WHEN 41 THEN 16 WHEN 42 THEN 27
                      WHEN 106 THEN CASE upper(split_part(c.data_type, '(', 1))
                                      WHEN 'UBIGINT' THEN 20 WHEN 'HUGEINT' THEN 38 WHEN 'UHUGEINT' THEN 38
                                      ELSE coalesce(c.numeric_precision, 18) END
                      ELSE 0 END AS UTINYINT) AS precision,
               CAST(CASE tid.id
                      WHEN 41 THEN 7 WHEN 42 THEN 7
                      WHEN 106 THEN CASE WHEN upper(split_part(c.data_type, '(', 1)) IN ('UBIGINT', 'HUGEINT', 'UHUGEINT')
                                         THEN 0 ELSE coalesce(c.numeric_scale, 0) END
                      ELSE 0 END AS UTINYINT) AS scale,
               CAST(CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END AS DECIMAL(1,0)) AS is_nullable,
               0::DECIMAL(1,0) AS is_identity,
               0::DECIMAL(1,0) AS is_computed,
               0::DECIMAL(1,0) AS is_rowguidcol,
               CAST(NULL AS INTEGER) AS default_object_id,
               CAST(NULL AS INTEGER) AS rule_object_id,
               CAST(NULL AS VARCHAR) AS collation_name,
               0::DECIMAL(1,0) AS is_ansi_padded,
               0::DECIMAL(1,0) AS is_filestream,
               0::DECIMAL(1,0) AS is_replicated,
               0::DECIMAL(1,0) AS is_non_sql_subscribed,
               0::DECIMAL(1,0) AS is_merge_published,
               0::DECIMAL(1,0) AS is_dts_replicated,
               0::DECIMAL(1,0) AS is_xml_document,
               CAST(0 AS INTEGER) AS xml_collection_id,
               0::DECIMAL(1,0) AS is_sparse,
               0::DECIMAL(1,0) AS is_column_set,
               CAST(0 AS SMALLINT) AS generated_always_type,
               'NOT_APPLICABLE' AS generated_always_type_desc,
               -- The rest of SQL Server 2025's columns, each with the value an ordinary column has.
               -- SSMS reads several: the Columns folder filters on is_dropped_ledger_column and
               -- graph_type, and a missing column fails the whole query rather than one field.
               CAST(NULL AS INTEGER) AS encryption_type,
               CAST(NULL AS VARCHAR) AS encryption_type_desc,
               CAST(NULL AS VARCHAR) AS encryption_algorithm_name,
               CAST(NULL AS INTEGER) AS column_encryption_key_id,
               CAST(NULL AS VARCHAR) AS column_encryption_key_database_name,
               0::DECIMAL(1,0) AS is_hidden,
               0::DECIMAL(1,0) AS is_masked,
               CAST(NULL AS INTEGER) AS graph_type,
               CAST(NULL AS VARCHAR) AS graph_type_desc,
               0::DECIMAL(1,0) AS is_data_deletion_filter_column,
               CAST(NULL AS INTEGER) AS ledger_view_column_type,
               CAST(NULL AS VARCHAR) AS ledger_view_column_type_desc,
               0::DECIMAL(1,0) AS is_dropped_ledger_column,
               CAST(NULL AS INTEGER) AS vector_dimensions,
               CAST(NULL AS UTINYINT) AS vector_base_type,
               CAST(NULL AS VARCHAR) AS vector_base_type_desc,
               c.data_type AS duckdb_type
        FROM information_schema.columns c
        JOIN sys.schemas s ON s.name = c.table_schema
        JOIN sys.objects o ON o.name = c.table_name AND o.schema_id = s.schema_id
        -- DuckDB names its own types, so they are translated to the ids sys.types publishes.
        -- Parameterised names arrive as DECIMAL(10,2), hence the split before matching.
        --
        -- Each type is reported as what the response writer actually sends for it, so a client
        -- that reads the catalog and then the data sees one type, not two. SQL Server's tinyint
        -- is unsigned: a signed TINYINT and a USMALLINT both go out as int. Unsigned 64-bit and
        -- 128-bit integers overflow bigint and go out as decimal.
        CROSS JOIN LATERAL (
            SELECT CASE upper(split_part(c.data_type, '(', 1))
                     WHEN 'BOOLEAN'   THEN 104 WHEN 'TINYINT'  THEN 56  WHEN 'UTINYINT'  THEN 48
                     WHEN 'SMALLINT'  THEN 52  WHEN 'USMALLINT' THEN 56
                     WHEN 'INTEGER'   THEN 56  WHEN 'UINTEGER' THEN 127
                     WHEN 'BIGINT'    THEN 127 WHEN 'UBIGINT'  THEN 106 WHEN 'HUGEINT' THEN 106
                     WHEN 'UHUGEINT'  THEN 106
                     WHEN 'FLOAT'     THEN 59  WHEN 'REAL'     THEN 59  WHEN 'DOUBLE'  THEN 62
                     WHEN 'DECIMAL'   THEN 106 WHEN 'NUMERIC'  THEN 106
                     WHEN 'DATE'      THEN 40  WHEN 'TIME'     THEN 41
                     WHEN 'TIMESTAMP' THEN 42  WHEN 'TIMESTAMP WITH TIME ZONE' THEN 42
                     WHEN 'TIMESTAMP_S' THEN 42 WHEN 'TIMESTAMP_MS' THEN 42 WHEN 'TIMESTAMP_NS' THEN 42
                     WHEN 'BLOB'      THEN 165 WHEN 'UUID'     THEN 36
                     ELSE 231
                   END AS id,
                   CASE upper(split_part(c.data_type, '(', 1))
                     WHEN 'BOOLEAN'   THEN 1  WHEN 'TINYINT'  THEN 4  WHEN 'UTINYINT'  THEN 1
                     WHEN 'SMALLINT'  THEN 2  WHEN 'USMALLINT' THEN 4
                     WHEN 'INTEGER'   THEN 4  WHEN 'UINTEGER' THEN 8
                     WHEN 'BIGINT'    THEN 8  WHEN 'UBIGINT'  THEN 17 WHEN 'HUGEINT' THEN 17
                     WHEN 'UHUGEINT'  THEN 17
                     WHEN 'FLOAT'     THEN 4  WHEN 'REAL'     THEN 4  WHEN 'DOUBLE'  THEN 8
                     WHEN 'DECIMAL'   THEN 17 WHEN 'NUMERIC'  THEN 17
                     WHEN 'DATE'      THEN 3  WHEN 'TIME'     THEN 5
                     WHEN 'TIMESTAMP' THEN 8  WHEN 'TIMESTAMP WITH TIME ZONE' THEN 8
                     WHEN 'TIMESTAMP_S' THEN 8 WHEN 'TIMESTAMP_MS' THEN 8 WHEN 'TIMESTAMP_NS' THEN 8
                     WHEN 'UUID'      THEN 16
                     -- A DuckDB VARCHAR or BLOB carries no declared length. It is sent as wide as
                     -- a column can be short of max, nvarchar(4000) or varbinary(8000), and said to
                     -- be that here: a linked server refuses a query whose columns arrive at any
                     -- other length than the catalog gave, and it cannot read a max column at all.
                     ELSE 8000
                   END AS length
        ) tid
        WHERE c.table_catalog = '{databaseName}'
        """,

        // Every storage container in the database. There is exactly one, and it is the default,
        // which is enough for the LEFT JOIN Object Explorer uses to decide a table is not
        // partitioned - it looks for type 'PS', and finds 'FG'.
        """
        CREATE OR REPLACE VIEW sys.data_spaces AS
        SELECT 'PRIMARY' AS name,
               CAST(1 AS INTEGER) AS data_space_id,
               -- SQL Server types this char(2). Stored unpadded for the same reason
               -- sys.objects.type is: DuckDB's comparisons do not ignore trailing blanks.
               'FG' AS type,
               'ROWS_FILEGROUP' AS type_desc,
               1::DECIMAL(1,0) AS is_default,
               0::DECIMAL(1,0) AS is_system
        """,

        // A parquet or CSV file carries no keys and no real indexes, but it is still a heap, and
        // SQL Server gives every heap a row here with index_id 0 and type 0. That row is not a
        // detail: Object Explorer lists tables with an INNER JOIN from sys.tables to sys.indexes,
        // so leaving this view empty does not report "no indexes", it reports no tables.
        """
        CREATE OR REPLACE VIEW sys.indexes AS
        SELECT o.object_id,
               CAST(NULL AS VARCHAR) AS name,
               CAST(0 AS INTEGER) AS index_id,
               CAST(0 AS SMALLINT) AS type,
               'HEAP' AS type_desc,
               0::DECIMAL(1,0) AS is_unique,
               CAST(1 AS INTEGER) AS data_space_id,
               0::DECIMAL(1,0) AS ignore_dup_key,
               0::DECIMAL(1,0) AS is_primary_key,
               0::DECIMAL(1,0) AS is_unique_constraint,
               CAST(0 AS SMALLINT) AS fill_factor,
               0::DECIMAL(1,0) AS is_padded,
               0::DECIMAL(1,0) AS is_disabled,
               0::DECIMAL(1,0) AS is_hypothetical,
               1::DECIMAL(1,0) AS allow_row_locks,
               1::DECIMAL(1,0) AS allow_page_locks,
               0::DECIMAL(1,0) AS has_filter,
               CAST(NULL AS VARCHAR) AS filter_definition
        -- Tables only. A view with no index on it has no row here on a real server either.
        FROM sys.objects o WHERE o.type = 'U'
        """,

        """
        CREATE OR REPLACE VIEW sys.index_columns AS
        SELECT CAST(0 AS INTEGER) AS object_id,
               CAST(0 AS INTEGER) AS index_id,
               CAST(0 AS INTEGER) AS index_column_id,
               CAST(0 AS INTEGER) AS column_id,
               CAST(0 AS SMALLINT) AS key_ordinal,
               CAST(0 AS SMALLINT) AS partition_ordinal,
               0::DECIMAL(1,0) AS is_descending_key,
               0::DECIMAL(1,0) AS is_included_column
        WHERE false
        """,

        // The all_* views are the same lists including system objects. There are none here, so
        // each is its own base view - but a client asks for these by name when it wants everything,
        // and a missing view is an error rather than an empty node.
        "CREATE OR REPLACE VIEW sys.all_objects AS SELECT * FROM sys.objects",
        "CREATE OR REPLACE VIEW sys.all_views AS SELECT * FROM sys.views",
        "CREATE OR REPLACE VIEW sys.all_columns AS SELECT * FROM sys.columns",

        // The type catalogue, in the full shape SQL Server publishes rather than the four columns
        // a column list strictly needs. The extra thirteen are all constants, and emitting them
        // costs nothing next to discovering them one client error at a time - DBeaver opens by
        // asking for is_user_defined, and an absent column is a hard error, not an empty answer.
        //
        // Only system types exist here, so is_user_defined is false throughout. The day a user
        // type can be declared, this becomes a UNION with a second source.
        """
        CREATE OR REPLACE VIEW sys.types AS
        SELECT t.name,
               CAST(t.system_type_id AS INTEGER) AS system_type_id,
               -- A built-in type numbers its user_type_id the same as its system_type_id; only a
               -- user-defined one gets an id of its own, starting at 256.
               CAST(t.system_type_id AS INTEGER) AS user_type_id,
               -- The sys schema, which SQL Server always numbers 4.
               CAST(4 AS INTEGER) AS schema_id,
               CAST(NULL AS INTEGER) AS principal_id,
               CAST(t.max_length AS SMALLINT) AS max_length,
               CAST(t.precision AS SMALLINT) AS precision,
               CAST(t.scale AS SMALLINT) AS scale,
               t.collation_name,
               1::DECIMAL(1,0) AS is_nullable,
               0::DECIMAL(1,0) AS is_user_defined,
               0::DECIMAL(1,0) AS is_assembly_type,
               CAST(0 AS INTEGER) AS default_object_id,
               CAST(0 AS INTEGER) AS rule_object_id,
               0::DECIMAL(1,0) AS is_table_type
        -- max_length is the type's own width in bytes, which is why the string types say 8000 and
        -- not -1: -1 means "(max)", and that belongs to a column declaration, not to the type.
        FROM (VALUES
            ('bit',              104, 1,    1,  0, CAST(NULL AS VARCHAR)),
            ('tinyint',           48, 1,    3,  0, NULL),
            ('smallint',          52, 2,    5,  0, NULL),
            ('int',               56, 4,   10,  0, NULL),
            ('bigint',           127, 8,   19,  0, NULL),
            ('decimal',          106, 17,  38,  0, NULL),
            ('real',              59, 4,   24,  0, NULL),
            ('float',             62, 8,   53,  0, NULL),
            ('date',              40, 3,   10,  0, NULL),
            ('time',              41, 5,   16,  7, NULL),
            ('datetime',          61, 8,   23,  3, NULL),
            ('datetime2',         42, 8,   27,  7, NULL),
            ('varchar',          167, 8000,  0, 0, 'SQL_Latin1_General_CP1_CI_AS'),
            ('nvarchar',         231, 8000,  0, 0, 'SQL_Latin1_General_CP1_CI_AS'),
            ('varbinary',        165, 8000,  0, 0, NULL),
            ('uniqueidentifier',  36, 16,    0, 0, NULL)
        ) AS t(name, system_type_id, max_length, precision, scale, collation_name)
        """,

        // The SQL Server 2000 name for the types, which the ODBC driver still reads when a linked
        // server connects: "select usertype,type,name from systypes where usertype>=257", the user
        // types. There are none, and every type here numbers itself below 257, so it finds none.
        """
        CREATE OR REPLACE VIEW sys.systypes AS
        SELECT name,
               CAST(system_type_id AS UTINYINT) AS xtype,
               CAST(user_type_id AS SMALLINT) AS xusertype,
               max_length AS length,
               CAST(precision AS UTINYINT) AS xprec,
               CAST(scale AS UTINYINT) AS xscale,
               CAST(user_type_id AS SMALLINT) AS usertype,
               CAST(system_type_id AS UTINYINT) AS type,
               precision AS prec,
               collation_name AS collation
        FROM sys.types
        """,

        // Two things SSMS's Columns folder joins to, and DaffyDB has none of: XML schema collections,
        // and foreign keys, which nothing mounted from a file can have. Empty, with SQL Server's
        // columns, so the join finds nothing rather than failing the query.
        """
        CREATE OR REPLACE VIEW sys.xml_schema_collections AS
        SELECT CAST(NULL AS INTEGER)   AS xml_collection_id,
               CAST(NULL AS INTEGER)   AS schema_id,
               CAST(NULL AS INTEGER)   AS principal_id,
               CAST(NULL AS VARCHAR)   AS name,
               CAST(NULL AS TIMESTAMP) AS create_date,
               CAST(NULL AS TIMESTAMP) AS modify_date
        WHERE false
        """,
        """
        CREATE OR REPLACE VIEW sys.foreign_key_columns AS
        SELECT CAST(NULL AS INTEGER) AS constraint_object_id,
               CAST(NULL AS INTEGER) AS constraint_column_id,
               CAST(NULL AS INTEGER) AS parent_object_id,
               CAST(NULL AS INTEGER) AS parent_column_id,
               CAST(NULL AS INTEGER) AS referenced_object_id,
               CAST(NULL AS INTEGER) AS referenced_column_id
        WHERE false
        """,

        // Defaults and computed columns: a file has neither. Power BI's column query joins to both
        // for every column of the table being previewed, so both have to exist, empty, with SQL
        // Server's columns.
        """
        CREATE OR REPLACE VIEW sys.default_constraints AS
        SELECT CAST(NULL AS VARCHAR)   AS name,
               CAST(NULL AS INTEGER)   AS object_id,
               CAST(NULL AS INTEGER)   AS principal_id,
               CAST(NULL AS INTEGER)   AS schema_id,
               CAST(NULL AS INTEGER)   AS parent_object_id,
               CAST(NULL AS VARCHAR)   AS type,
               CAST(NULL AS VARCHAR)   AS type_desc,
               CAST(NULL AS TIMESTAMP) AS create_date,
               CAST(NULL AS TIMESTAMP) AS modify_date,
               CAST(NULL AS DECIMAL(1,0)) AS is_ms_shipped,
               CAST(NULL AS DECIMAL(1,0)) AS is_published,
               CAST(NULL AS DECIMAL(1,0)) AS is_schema_published,
               CAST(NULL AS INTEGER)   AS parent_column_id,
               CAST(NULL AS VARCHAR)   AS definition,
               CAST(NULL AS DECIMAL(1,0)) AS is_system_named
        WHERE false
        """,
        """
        CREATE OR REPLACE VIEW sys.computed_columns AS
        SELECT *, CAST(NULL AS VARCHAR) AS definition,
               CAST(NULL AS DECIMAL(1,0)) AS uses_database_collation,
               CAST(NULL AS DECIMAL(1,0)) AS is_persisted
        FROM sys.columns WHERE false
        """,

        // Identity columns: none either, and SSMS's IntelliSense joins to them beside the other two.
        """
        CREATE OR REPLACE VIEW sys.identity_columns AS
        SELECT *, CAST(NULL AS BIGINT) AS seed_value,
               CAST(NULL AS BIGINT) AS increment_value,
               CAST(NULL AS BIGINT) AS last_value,
               CAST(NULL AS DECIMAL(1,0)) AS is_not_for_replication
        FROM sys.columns WHERE false
        """,

        // Foreign keys: none, since a file has none. Power BI's navigator reads them for the table
        // being previewed, joining this to sys.foreign_key_columns, and an absent view fails the
        // preview where an empty one just means no relationships.
        Empty("sys.foreign_keys",
            "name VARCHAR", "object_id INTEGER", "principal_id INTEGER", "schema_id INTEGER",
            "parent_object_id INTEGER", "type VARCHAR", "type_desc VARCHAR", "create_date TIMESTAMP",
            "modify_date TIMESTAMP", "is_ms_shipped DECIMAL(1,0)", "is_published DECIMAL(1,0)",
            "is_schema_published DECIMAL(1,0)", "referenced_object_id INTEGER", "key_index_id INTEGER",
            "is_disabled DECIMAL(1,0)", "is_not_for_replication DECIMAL(1,0)", "is_not_trusted DECIMAL(1,0)",
            "delete_referential_action UTINYINT", "delete_referential_action_desc VARCHAR",
            "update_referential_action UTINYINT", "update_referential_action_desc VARCHAR",
            "is_system_named DECIMAL(1,0)"),

        // Availability groups and mirroring: none. Object Explorer's Databases folder reads all
        // four in the batch that lists user databases - the system databases come from another
        // query - so without them master and msdb appear and daffydb does not.
        Empty("sys.availability_replicas",
            "replica_id UUID", "group_id UUID", "replica_metadata_id INTEGER", "replica_server_name VARCHAR",
            "owner_sid BLOB", "endpoint_url VARCHAR", "availability_mode UTINYINT", "availability_mode_desc VARCHAR",
            "failover_mode UTINYINT", "failover_mode_desc VARCHAR", "session_timeout INTEGER",
            "primary_role_allow_connections UTINYINT", "primary_role_allow_connections_desc VARCHAR",
            "secondary_role_allow_connections UTINYINT", "secondary_role_allow_connections_desc VARCHAR",
            "create_date TIMESTAMP", "modify_date TIMESTAMP", "backup_priority INTEGER",
            "read_only_routing_url VARCHAR", "seeding_mode UTINYINT", "seeding_mode_desc VARCHAR"),
        Empty("sys.availability_groups",
            "group_id UUID", "name VARCHAR", "resource_id VARCHAR", "resource_group_id VARCHAR",
            "failure_condition_level INTEGER", "health_check_timeout INTEGER", "automated_backup_preference UTINYINT",
            "automated_backup_preference_desc VARCHAR", "version SMALLINT", "basic_features DECIMAL(1,0)",
            "dtc_support DECIMAL(1,0)", "db_failover DECIMAL(1,0)", "is_distributed DECIMAL(1,0)",
            "cluster_type UTINYINT", "cluster_type_desc VARCHAR", "required_synchronized_secondaries_to_commit INTEGER",
            "sequence_number BIGINT", "is_contained DECIMAL(1,0)"),
        Empty("sys.dm_hadr_database_replica_states",
            "database_id INTEGER", "group_id UUID", "replica_id UUID", "group_database_id UUID",
            "is_local DECIMAL(1,0)", "is_primary_replica DECIMAL(1,0)", "synchronization_state UTINYINT",
            "synchronization_state_desc VARCHAR", "is_commit_participant DECIMAL(1,0)",
            "synchronization_health UTINYINT", "synchronization_health_desc VARCHAR", "database_state UTINYINT",
            "database_state_desc VARCHAR", "is_suspended DECIMAL(1,0)", "suspend_reason UTINYINT",
            "suspend_reason_desc VARCHAR"),

        // SQL Server gives every database a row here, with nothing in it unless it is mirrored.
        """
        CREATE OR REPLACE VIEW sys.database_mirroring AS
        SELECT database_id,
               CAST(NULL AS UUID)     AS mirroring_guid,
               CAST(NULL AS UTINYINT) AS mirroring_state,
               CAST(NULL AS VARCHAR)  AS mirroring_state_desc,
               CAST(NULL AS UTINYINT) AS mirroring_role,
               CAST(NULL AS VARCHAR)  AS mirroring_role_desc,
               CAST(NULL AS INTEGER)  AS mirroring_role_sequence,
               CAST(NULL AS UTINYINT) AS mirroring_safety_level,
               CAST(NULL AS VARCHAR)  AS mirroring_safety_level_desc,
               CAST(NULL AS INTEGER)  AS mirroring_safety_sequence,
               CAST(NULL AS VARCHAR)  AS mirroring_partner_name,
               CAST(NULL AS VARCHAR)  AS mirroring_partner_instance,
               CAST(NULL AS VARCHAR)  AS mirroring_witness_name,
               CAST(NULL AS UTINYINT) AS mirroring_witness_state,
               CAST(NULL AS VARCHAR)  AS mirroring_witness_state_desc
        FROM sys.databases
        """,

        // VS Code's Object Explorer loads every property of a database before it lists what is in
        // it, from one query that joins these three to sys.databases - SSMS asks for fewer, and
        // never reads them. Any one missing fails the whole query, and the database shows nothing
        // under it. Each says what SQL Server says of a database that has none of it.
        //
        // Every database has a row in the first two. A database's GUID is never NULL on SQL Server,
        // and SMO will not read one that is, so each has its own, made from its name so it stays the
        // same from run to run. It is the family and recovery fork too, as on a database that was
        // created once and never restored. DaffyDB keeps no transaction log, so there are no log
        // backups or fork points for the LSNs to name, and with no last log backup, SMO's
        // HasFullBackup is false. FILESTREAM is off, as LocalDB has it.
        """
        CREATE OR REPLACE VIEW sys.database_recovery_status AS
        SELECT database_id,
               guid                         AS database_guid,
               guid                         AS family_guid,
               CAST(NULL AS DECIMAL(25,0))  AS last_log_backup_lsn,
               guid                         AS recovery_fork_guid,
               CAST(NULL AS UUID)           AS first_recovery_fork_guid,
               CAST(NULL AS DECIMAL(25,0))  AS fork_point_lsn
        FROM (SELECT database_id,
                     CAST(regexp_replace(md5(name), '^(.{8})(.{4})(.{4})(.{4})(.{12})$', '\1-\2-\3-\4-\5') AS UUID) AS guid
              FROM sys.databases)
        """,

        """
        CREATE OR REPLACE VIEW sys.database_filestream_options AS
        SELECT database_id,
               CAST(0 AS UTINYINT)   AS non_transacted_access,
               'OFF'                 AS non_transacted_access_desc,
               CAST(NULL AS VARCHAR) AS directory_name
        FROM sys.databases
        """,

        // Change tracking: on for no database and no table. The table view is read when VS Code
        // loads a table's properties.
        Empty("sys.change_tracking_databases",
            "database_id INTEGER", "is_auto_cleanup_on UTINYINT", "retention_period INTEGER",
            "retention_period_units UTINYINT", "retention_period_units_desc VARCHAR", "max_cleanup_version BIGINT"),
        Empty("sys.change_tracking_tables",
            "object_id INTEGER", "is_track_columns_updated_on DECIMAL(1,0)", "min_valid_version BIGINT",
            "begin_version BIGINT", "cleanup_version BIGINT"),

        // FileTables and system-versioned tables: none. VS Code joins both into a table's
        // properties.
        Empty("sys.filetables",
            "object_id INTEGER", "is_enabled DECIMAL(1,0)", "directory_name VARCHAR",
            "filename_collation_id INTEGER", "filename_collation_name VARCHAR"),
        Empty("sys.periods",
            "name VARCHAR", "period_type UTINYINT", "period_type_desc VARCHAR", "object_id INTEGER",
            "start_column_id INTEGER", "end_column_id INTEGER"),

        // The database's principals: its one user, dbo, whom every schema belongs to, and the
        // public role every user is in. VS Code names a table's owner from here. SQL Server lists
        // guest, the fixed roles and two more users besides, but none of them means anything to
        // DaffyDB. dbo is a SQL user, as the login is a SQL login.
        """
        CREATE OR REPLACE VIEW sys.database_principals AS
        SELECT p.name, p.principal_id, p.type, p.type_desc, p.default_schema_name,
               CAST('2024-01-01' AS TIMESTAMP) AS create_date,
               CAST('2024-01-01' AS TIMESTAMP) AS modify_date,
               p.owning_principal_id,
               CAST(NULL AS BLOB)    AS sid,
               0::DECIMAL(1,0)       AS is_fixed_role,
               p.authentication_type, p.authentication_type_desc,
               CAST(NULL AS VARCHAR) AS default_language_name,
               CAST(NULL AS INTEGER) AS default_language_lcid,
               0::DECIMAL(1,0)       AS allow_encrypted_value_modifications,
               CAST(NULL AS UUID)    AS tenant_id
        FROM (VALUES
            ('public', CAST(0 AS INTEGER), 'R', 'DATABASE_ROLE', CAST(NULL AS VARCHAR), CAST(1 AS INTEGER), CAST(0 AS INTEGER), 'NONE'),
            ('dbo',    1,                  'S', 'SQL_USER',      'dbo',                 NULL,               1,                  'INSTANCE')
        ) AS p(name, principal_id, type, type_desc, default_schema_name, owning_principal_id,
               authentication_type, authentication_type_desc)
        """,

        // The four settings SMO reads with a database's properties, as SQL Server ships them: no
        // MAXDOP limit, today's cardinality estimator, parameter sniffing on, no optimizer
        // hotfixes. Like sys.configurations, it holds what a client reads and nothing more.
        // SQL Server types value as sql_variant, which DaffyDB does not send; MAXDOP is an int
        // there and the rest are bits, and they go out as int, which SMO converts as it does the
        // sql_variant.
        """
        CREATE OR REPLACE VIEW sys.database_scoped_configurations AS
        SELECT configuration_id, name, value,
               CAST(NULL AS INTEGER) AS value_for_secondary,
               1::DECIMAL(1,0)       AS is_value_default
        FROM (VALUES
            (CAST(1 AS INTEGER), 'MAXDOP',                        CAST(0 AS INTEGER)),
            (2,                  'LEGACY_CARDINALITY_ESTIMATION', 0),
            (3,                  'PARAMETER_SNIFFING',            1),
            (4,                  'QUERY_OPTIMIZER_HOTFIXES',      0)
        ) AS c(configuration_id, name, value)
        """,

        // Always On availability groups: none. SMO reads this inside a TRY/CATCH that forgives
        // only SQL Server's own "not available" errors, so it has to exist, empty, to be forgiven.
        """
        CREATE OR REPLACE VIEW sys.dm_hadr_cluster AS
        SELECT CAST(NULL AS VARCHAR)  AS cluster_name,
               CAST(NULL AS UTINYINT) AS quorum_type,
               CAST(NULL AS VARCHAR)  AS quorum_type_desc,
               CAST(NULL AS UTINYINT) AS quorum_state,
               CAST(NULL AS VARCHAR)  AS quorum_state_desc
        WHERE false
        """,

        // The character sets the server knows, of which there is one: iso_1, the set behind
        // SQL_Latin1_General_CP1_CI_AS. The ODBC Driver 18 setup wizard's "Test Data Source" reads
        // it by the id DATABASEPROPERTYEX(db, 'SqlCharSet') reports.
        """
        CREATE OR REPLACE VIEW sys.syscharsets AS
        SELECT CAST(1001 AS SMALLINT) AS type,
               CAST(1 AS UTINYINT)    AS id,
               CAST(0 AS UTINYINT)    AS csid,
               CAST(0 AS SMALLINT)    AS status,
               'iso_1'                AS name,
               'ISO 8859-1 (Latin-1) - Western European 8-bit character set.' AS description,
               CAST(NULL AS BLOB)     AS binarydefinition,
               CAST(NULL AS BLOB)     AS definition
        """,

        // The languages the server speaks, of which there is one: us_english, what @@LANGUAGE
        // answers, with LocalDB's row for it. VS Code reads it to name the server's default
        // language. Full-text search is not installed, so it knows no full-text languages - nor
        // does LocalDB.
        """
        CREATE OR REPLACE VIEW sys.syslanguages AS
        SELECT CAST(0 AS SMALLINT)     AS langid,
               'mdy'                   AS dateformat,
               CAST(7 AS UTINYINT)     AS datefirst,
               CAST(0 AS INTEGER)      AS upgrade,
               'us_english'            AS name,
               'English'               AS alias,
               'January,February,March,April,May,June,July,August,September,October,November,December' AS months,
               'Jan,Feb,Mar,Apr,May,Jun,Jul,Aug,Sep,Oct,Nov,Dec' AS shortmonths,
               'Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday' AS days,
               CAST(1033 AS INTEGER)   AS lcid,
               CAST(1033 AS SMALLINT)  AS msglangid
        """,
        Empty("sys.fulltext_languages", "lcid INTEGER", "name VARCHAR"),

        // xp_msver's shape: a numbered list of name/value facts about the server build. Clients
        // load it into a temp table and pick rows out by name. The names are DaffyDB's own; the
        // version numbers are the ones every other answer reports.
        $$"""
        CREATE OR REPLACE VIEW sys.xp_msver AS
        SELECT * FROM (VALUES
            (1,  'ProductName',        CAST(NULL AS INTEGER), '{{TSqlTranslator.ProductName}}'),
            (2,  'ProductVersion',     17,                    '17.0.4025.3'),
            (3,  'Language',           1033,                  'English (United States)'),
            (4,  'Platform',           CAST(NULL AS INTEGER), 'NT x64'),
            (5,  'Comments',           CAST(NULL AS INTEGER), '{{TSqlTranslator.Identity}}'),
            (6,  'CompanyName',        CAST(NULL AS INTEGER), 'TroBeeOne LLC'),
            (7,  'FileDescription',    CAST(NULL AS INTEGER), '{{TSqlTranslator.ProductName}}'),
            (8,  'FileVersion',        CAST(NULL AS INTEGER), '17.0.4025.3'),
            (9,  'InternalName',       CAST(NULL AS INTEGER), 'daffydb'),
            (10, 'LegalCopyright',     CAST(NULL AS INTEGER), ''),
            (11, 'LegalTrademarks',    CAST(NULL AS INTEGER), ''),
            (12, 'OriginalFilename',   CAST(NULL AS INTEGER), 'daffydb.exe'),
            (13, 'PrivateBuild',       CAST(NULL AS INTEGER), ''),
            (14, 'SpecialBuild',       CAST(NULL AS INTEGER), ''),
            (15, 'WindowsVersion',     CAST(NULL AS INTEGER), '10.0 (26200)'),
            (16, 'ProcessorCount',     4,                     '4'),
            (17, 'ProcessorActiveMask', 15,                   '15'),
            (18, 'ProcessorType',      8664,                  'x64'),
            (19, 'PhysicalMemory',     16384,                 '16384 (17179869184)'),
            (20, 'Product ID',         CAST(NULL AS INTEGER), 'NULL')
        ) AS t(ID, Name, Internal_Value, Character_Value)
        """,

        """
        CREATE OR REPLACE VIEW sys.server_principals AS
        SELECT 'dbo' AS name,
               CAST(1 AS INTEGER) AS principal_id,
               'S' AS type,
               'SQL_LOGIN' AS type_desc,
               0::DECIMAL(1,0) AS is_disabled
        """,

        // The settings a client needs, and no others: any other is absent rather than given a value
        // DaffyDB would be making up. SSMS before 22 reads "SMO and DMO XPs" while connecting, as
        // SmoEnabled, on as SQL Server ships it. SMO reads the other five with a database's
        // properties, as the server's defaults for it, and picks them out of the answer by
        // position, so all five have to be there: VS Code's Object Explorer fails on "There is no
        // row at position 1" without them. The values are LocalDB's, and true here: CONVERT reads
        // a two-digit year by the 2049 cutoff (ReadWithStyle), and @@LANGUAGE is us_english.
        """
        CREATE OR REPLACE VIEW sys.configurations AS
        SELECT CAST(configuration_id AS INTEGER) AS configuration_id, name,
               CAST(value AS INTEGER)   AS value,
               CAST(minimum AS INTEGER) AS minimum,
               CAST(maximum AS INTEGER) AS maximum,
               CAST(value AS INTEGER)   AS value_in_use,
               description,
               CAST(is_dynamic AS DECIMAL(1,0))  AS is_dynamic,
               CAST(is_advanced AS DECIMAL(1,0)) AS is_advanced
        FROM (VALUES
            (115,   'nested triggers',            1,    0,    1,          'Allow triggers to be invoked within triggers', 1, 0),
            (124,   'default language',           0,    0,    9999,       'default language',                          1, 0),
            (1126,  'default full-text language', 1033, 0,    2147483647, 'default full-text language',                1, 1),
            (1127,  'two digit year cutoff',      2049, 1753, 9999,       'two digit year cutoff',                     1, 1),
            (1555,  'transform noise words',      0,    0,    1,          'Transform noise words for full-text query', 1, 1),
            (16387, 'SMO and DMO XPs',            1,    0,    1,          'Enable or disable SMO and DMO XPs',         1, 1)
        ) AS c(configuration_id, name, value, minimum, maximum, description, is_dynamic, is_advanced)
        """,

        // SQL Server's INFORMATION_SCHEMA, which the translator reaches here: see
        // TSqlTranslator.RewriteInformationSchema for why these cannot simply be called
        // information_schema.tables. Each is a projection of the sys views above, so the two ways
        // of asking agree, and every column is SQL Server's - the names, the order, the types, and
        // the values LocalDB gives for the same columns.
        $"""
        CREATE OR REPLACE VIEW sys.information_schema_tables AS
        SELECT '{databaseName}' AS TABLE_CATALOG,
               s.name AS TABLE_SCHEMA,
               o.name AS TABLE_NAME,
               CASE o.type WHEN 'U' THEN 'BASE TABLE' ELSE 'VIEW' END AS TABLE_TYPE
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        """,

        $"""
        CREATE OR REPLACE VIEW sys.information_schema_views AS
        SELECT '{databaseName}' AS TABLE_CATALOG,
               s.name AS TABLE_SCHEMA,
               o.name AS TABLE_NAME,
               v.sql  AS VIEW_DEFINITION,
               'NONE' AS CHECK_OPTION,
               'NO'   AS IS_UPDATABLE
        FROM sys.views o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN duckdb_views() v
               ON v.database_name = '{databaseName}' AND v.schema_name = s.name AND v.view_name = o.name
        """,

        $"""
        CREATE OR REPLACE VIEW sys.information_schema_columns AS
        SELECT '{databaseName}' AS TABLE_CATALOG,
               s.name AS TABLE_SCHEMA,
               o.name AS TABLE_NAME,
               c.name AS COLUMN_NAME,
               c.column_id AS ORDINAL_POSITION,
               CAST(NULL AS VARCHAR) AS COLUMN_DEFAULT,
               CASE WHEN c.is_nullable = 1 THEN 'YES' ELSE 'NO' END AS IS_NULLABLE,
               t.name AS DATA_TYPE,
               -- Characters, not bytes: an nvarchar(50) is 50 long and 100 wide. A (max) is -1 in both.
               CAST(CASE WHEN t.name NOT IN ('nvarchar', 'varchar', 'varbinary') THEN NULL
                         WHEN c.max_length = -1 THEN -1
                         WHEN t.name = 'nvarchar' THEN c.max_length // 2
                         ELSE c.max_length END AS INTEGER) AS CHARACTER_MAXIMUM_LENGTH,
               CAST(CASE WHEN t.name IN ('nvarchar', 'varchar', 'varbinary') THEN c.max_length END AS INTEGER)
                   AS CHARACTER_OCTET_LENGTH,
               CAST(CASE WHEN t.name IN ('tinyint', 'smallint', 'int', 'bigint', 'decimal', 'real', 'float')
                         THEN c.precision END AS UTINYINT) AS NUMERIC_PRECISION,
               CAST(CASE WHEN t.name IN ('tinyint', 'smallint', 'int', 'bigint', 'decimal') THEN 10
                         WHEN t.name IN ('real', 'float') THEN 2 END AS SMALLINT) AS NUMERIC_PRECISION_RADIX,
               CAST(CASE WHEN t.name IN ('tinyint', 'smallint', 'int', 'bigint', 'decimal')
                         THEN c.scale END AS INTEGER) AS NUMERIC_SCALE,
               CAST(CASE WHEN t.name = 'date' THEN 0
                         WHEN t.name = 'datetime' THEN 3
                         WHEN t.name IN ('time', 'datetime2') THEN c.scale END AS SMALLINT) AS DATETIME_PRECISION,
               CAST(NULL AS VARCHAR) AS CHARACTER_SET_CATALOG,
               CAST(NULL AS VARCHAR) AS CHARACTER_SET_SCHEMA,
               CASE t.name WHEN 'nvarchar' THEN 'UNICODE' WHEN 'varchar' THEN 'iso_1' END AS CHARACTER_SET_NAME,
               CAST(NULL AS VARCHAR) AS COLLATION_CATALOG,
               CAST(NULL AS VARCHAR) AS COLLATION_SCHEMA,
               CASE WHEN t.name IN ('nvarchar', 'varchar') THEN 'SQL_Latin1_General_CP1_CI_AS' END AS COLLATION_NAME,
               CAST(NULL AS VARCHAR) AS DOMAIN_CATALOG,
               CAST(NULL AS VARCHAR) AS DOMAIN_SCHEMA,
               CAST(NULL AS VARCHAR) AS DOMAIN_NAME
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        """,

        $"""
        CREATE OR REPLACE VIEW sys.information_schema_schemata AS
        SELECT '{databaseName}' AS CATALOG_NAME,
               s.name AS SCHEMA_NAME,
               s.name AS SCHEMA_OWNER,
               CAST(NULL AS VARCHAR) AS DEFAULT_CHARACTER_SET_CATALOG,
               CAST(NULL AS VARCHAR) AS DEFAULT_CHARACTER_SET_SCHEMA,
               'iso_1' AS DEFAULT_CHARACTER_SET_NAME
        FROM sys.schemas s
        """,

        // No procedures or functions exist here to be listed, but Power BI's navigator reads this
        // straight after the table list, and a missing view is an error where an empty one is not.
        Empty("sys.information_schema_routines",
            "SPECIFIC_CATALOG VARCHAR", "SPECIFIC_SCHEMA VARCHAR", "SPECIFIC_NAME VARCHAR",
            "ROUTINE_CATALOG VARCHAR", "ROUTINE_SCHEMA VARCHAR", "ROUTINE_NAME VARCHAR", "ROUTINE_TYPE VARCHAR",
            "MODULE_CATALOG VARCHAR", "MODULE_SCHEMA VARCHAR", "MODULE_NAME VARCHAR",
            "UDT_CATALOG VARCHAR", "UDT_SCHEMA VARCHAR", "UDT_NAME VARCHAR", "DATA_TYPE VARCHAR",
            "CHARACTER_MAXIMUM_LENGTH INTEGER", "CHARACTER_OCTET_LENGTH INTEGER",
            "COLLATION_CATALOG VARCHAR", "COLLATION_SCHEMA VARCHAR", "COLLATION_NAME VARCHAR",
            "CHARACTER_SET_CATALOG VARCHAR", "CHARACTER_SET_SCHEMA VARCHAR", "CHARACTER_SET_NAME VARCHAR",
            "NUMERIC_PRECISION UTINYINT", "NUMERIC_PRECISION_RADIX SMALLINT", "NUMERIC_SCALE INTEGER",
            "DATETIME_PRECISION SMALLINT", "INTERVAL_TYPE VARCHAR", "INTERVAL_PRECISION SMALLINT",
            "TYPE_UDT_CATALOG VARCHAR", "TYPE_UDT_SCHEMA VARCHAR", "TYPE_UDT_NAME VARCHAR",
            "SCOPE_CATALOG VARCHAR", "SCOPE_SCHEMA VARCHAR", "SCOPE_NAME VARCHAR",
            "MAXIMUM_CARDINALITY BIGINT", "DTD_IDENTIFIER VARCHAR", "ROUTINE_BODY VARCHAR",
            "ROUTINE_DEFINITION VARCHAR", "EXTERNAL_NAME VARCHAR", "EXTERNAL_LANGUAGE VARCHAR",
            "PARAMETER_STYLE VARCHAR", "IS_DETERMINISTIC VARCHAR", "SQL_DATA_ACCESS VARCHAR",
            "IS_NULL_CALL VARCHAR", "SQL_PATH VARCHAR", "SCHEMA_LEVEL_ROUTINE VARCHAR",
            "MAX_DYNAMIC_RESULT_SETS SMALLINT", "IS_USER_DEFINED_CAST VARCHAR",
            "IS_IMPLICITLY_INVOCABLE VARCHAR", "CREATED TIMESTAMP", "LAST_ALTERED TIMESTAMP"),

        Empty("sys.information_schema_parameters",
            "SPECIFIC_CATALOG VARCHAR", "SPECIFIC_SCHEMA VARCHAR", "SPECIFIC_NAME VARCHAR",
            "ORDINAL_POSITION INTEGER", "PARAMETER_MODE VARCHAR", "IS_RESULT VARCHAR", "AS_LOCATOR VARCHAR",
            "PARAMETER_NAME VARCHAR", "DATA_TYPE VARCHAR",
            "CHARACTER_MAXIMUM_LENGTH INTEGER", "CHARACTER_OCTET_LENGTH INTEGER",
            "COLLATION_CATALOG VARCHAR", "COLLATION_SCHEMA VARCHAR", "COLLATION_NAME VARCHAR",
            "CHARACTER_SET_CATALOG VARCHAR", "CHARACTER_SET_SCHEMA VARCHAR", "CHARACTER_SET_NAME VARCHAR",
            "NUMERIC_PRECISION UTINYINT", "NUMERIC_PRECISION_RADIX SMALLINT", "NUMERIC_SCALE INTEGER",
            "DATETIME_PRECISION SMALLINT", "INTERVAL_TYPE VARCHAR", "INTERVAL_PRECISION SMALLINT",
            "USER_DEFINED_TYPE_CATALOG VARCHAR", "USER_DEFINED_TYPE_SCHEMA VARCHAR", "USER_DEFINED_TYPE_NAME VARCHAR",
            "SCOPE_CATALOG VARCHAR", "SCOPE_SCHEMA VARCHAR", "SCOPE_NAME VARCHAR"),
    ];

    /// <summary>
    /// Just enough of msdb for SSMS's Policy-Based Management checks, which it runs while
    /// connecting and again - inside the query SMO needs answered before it will script "Select
    /// Top 1000 Rows" - when it reads the server's properties. Policy management is off, as on a
    /// fresh SQL Server: the values are LocalDB's.
    ///
    /// msdb is a database like the others, with its own sys views, so msdb.dbo.* resolves as
    /// written and a session can USE it; these are what it holds beyond them.
    /// </summary>
    public static IReadOnlyList<string> Msdb() =>
    [
        "CREATE SCHEMA IF NOT EXISTS msdb.dbo",

        "CREATE OR REPLACE MACRO msdb.dbo.fn_syspolicy_is_automation_enabled() AS 0::DECIMAL(1,0)",

        """
        CREATE OR REPLACE VIEW msdb.dbo.syspolicy_configuration AS
        SELECT * FROM (VALUES ('Enabled', 0), ('HistoryRetentionInDays', 0), ('LogOnSuccess', 0))
               AS v(name, current_value)
        """,

        """
        CREATE OR REPLACE VIEW msdb.dbo.syspolicy_system_health_state AS
        SELECT CAST(NULL AS BIGINT)    AS health_state_id,
               CAST(NULL AS INTEGER)   AS policy_id,
               CAST(NULL AS TIMESTAMP) AS last_run_date,
               CAST(NULL AS VARCHAR)   AS target_query_expression_with_id,
               CAST(NULL AS VARCHAR)   AS target_query_expression,
               CAST(NULL AS DECIMAL(1,0)) AS result
        WHERE false
        """,
    ];
}
