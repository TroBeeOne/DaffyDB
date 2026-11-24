-- What SQL Server Management Studio sends while it connects, expands Databases, a database's
-- Tables and one table's Columns, and runs "Select Top 1000 Rows" - captured through DaffyTee from
-- SSMS talking to a real SQL Server, with every database, table, column, login and server name
-- replaced by the seed data's or a neutral one. Duplicates are removed; order is kept.
--
-- Each request is a header line - its number, the part of SSMS that sent it, and what DaffyDB is
-- expected to do with it - then its parameters, then its SQL, then GO. A parameterised request
-- is replayed as the sp_executesql RPC SMO sent, anything else as a batch.
--
--   ok, N sets   must run and return N result sets; anything else is a regression
--   gap: ...     known not to work yet, with the error it gets; it is reported, not failed
--
-- A "-- in" line gives the database the request was sent from - SSMS moves between them with USE,
-- and on more than one connection - and the replay switches there first. The user's own
-- databases are daffydb, where DaffyDB keeps the data.
--
-- A "-- types" line gives the SQL Server type of each column of the first result set, as LocalDB
-- returns them, for a query whose values SSMS casts directly: a different type fails the run.
--
-- SmoProbe --replay <this file> --update rewrites the expectations from a run; review the diff.

-- #1  Object Explorer  ok, 3 sets
-- in daffydb
DECLARE @edition sysname;
SET @edition = cast(SERVERPROPERTY(N'EDITION') as sysname);
SELECT case when @edition = N'SQL Azure' then 2 else 1 end as 'DatabaseEngineType',
SERVERPROPERTY('EngineEdition') AS DatabaseEngineEdition,
SERVERPROPERTY('ProductVersion') AS ProductVersion,
@@MICROSOFTVERSION AS MicrosoftVersion,
case when serverproperty('EngineEdition') = 12 then 1 when serverproperty('EngineEdition') = 11 and @@version like 'Microsoft Azure SQL Data Warehouse%' then 1 else 0 end as IsFabricServer,
convert(sysname, SERVERPROPERTY(N'Collation')) AS Collation;
select host_platform from sys.dm_os_host_info
if @edition = N'SQL Azure' 
  select 'TCP' as ConnectionProtocol
else
  exec ('select CONVERT(nvarchar(40),CONNECTIONPROPERTY(''net_transport'')) as ConnectionProtocol')
GO

-- #2  Object Explorer  ok, 1 set
-- in daffydb
use [daffydb];
if (db_id() = 1)
begin
select 0
end
else
begin
exec('select case when authenticating_database_id = 1 then 0 else 1 end from
sys.dm_exec_sessions where session_id = @@SPID')
end;use [daffydb]; --resetting the context
GO

-- #3  Object Explorer  ok, 0 sets
-- in daffydb
SET LOCK_TIMEOUT 10000
GO

-- #4  Object Explorer  ok, 1 set
-- in daffydb
SELECT
CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition],
CAST(0 AS bit) AS [IsFabricServer]
GO

-- #5  Object Explorer  ok, 0 sets
-- in daffydb
use [master]
GO

-- #6  Object Explorer  ok, 1 set
-- in master
create table #SVer(ID int,  Name  sysname, Internal_Value int, Value nvarchar(512))
          insert #SVer exec master.dbo.xp_msver
          insert #SVer select t.*
          from sys.dm_os_host_info
          CROSS APPLY (
          VALUES
          (1001, 'host_platform', 0, host_platform),
          (1002, 'host_distribution', 0, host_distribution),
          (1003, 'host_release', 0, host_release),
          (1004, 'host_service_pack_level', 0, host_service_pack_level),
          (1005, 'host_sku', host_sku, ''),
          (1006, 'HardwareGeneration', '', ''),
          (1007, 'ServiceTier', '', ''),
          (1008, 'ReservedStorageSizeMB', '0', '0'),
          (1009, 'UsedStorageSizeMB', '0', '0')
          ) t(id, [name], internal_value, [value])

          -- Managed Instance-specific properties
          if (SERVERPROPERTY('EngineEdition') = 8)
          begin
            DECLARE @gen4memoryPerCoreMB float = 7168.0
            DECLARE @gen5memoryPerCoreMB float = 5223.0
            DECLARE @physicalMemory float
            DECLARE @virtual_core_count int
            DECLARE @reservedStorageSize bigint
            DECLARE @usedStorageSize decimal(18,2)
            DECLARE @hwGeneration nvarchar(128)
            DECLARE @serviceTier nvarchar(128)

            SET @physicalMemory = (SELECT TOP 1 [virtual_core_count] *
              (
                CASE WHEN [hardware_generation] = 'Gen4' THEN @gen4memoryPerCoreMB
                WHEN [hardware_generation] = 'Gen5' THEN @gen5memoryPerCoreMB
                ELSE 0 END
               )
               FROM master.sys.server_resource_stats 
               ORDER BY start_time DESC)
      
            IF (@physicalMemory <> 0) 
            BEGIN
              UPDATE #SVer SET [Internal_Value] =  @physicalMemory WHERE Name = N'PhysicalMemory'
              UPDATE #SVer SET [Value] = CONCAT( @physicalMemory, ' (',  @physicalMemory * 1024, ')') WHERE Name = N'PhysicalMemory'
            END

            UPDATE #SVer SET [Internal_Value] = (SELECT TOP 1 [virtual_core_count] FROM master.sys.server_resource_stats ORDER BY start_time desc) WHERE Name = N'ProcessorCount'
            UPDATE #SVer SET [Value] = [Internal_Value] WHERE Name = N'ProcessorCount'

            SELECT TOP 1
              @hwGeneration = [hardware_generation],
              @serviceTier =[sku],
              @virtual_core_count = [virtual_core_count],
              @reservedStorageSize = [reserved_storage_mb],
              @usedStorageSize = [storage_space_used_mb]
            FROM master.sys.server_resource_stats
            ORDER BY [start_time] DESC

            UPDATE #SVer SET [Value] = @hwGeneration WHERE Name = N'HardwareGeneration'
            UPDATE #SVer SET [Value] = @serviceTier WHERE Name = N'ServiceTier'
            UPDATE #SVer SET [Value] = @reservedStorageSize WHERE Name = N'ReservedStorageSizeMB'
            UPDATE #SVer SET [Value] = @usedStorageSize WHERE Name = N'UsedStorageSizeMB'
          end
      


SELECT
CAST(
        serverproperty(N'Servername')
       AS sysname) AS [Server_Name],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' AS [Server_Urn],
CAST(null AS int) AS [Server_ServerType],
CAST(0x0001 AS int) AS [Server_Status],
0 AS [Server_IsContainedAuthentication],
(@@microsoftversion / 0x1000000) & 0xff AS [VersionMajor],
(@@microsoftversion / 0x10000) & 0xff AS [VersionMinor],
@@microsoftversion & 0xffff AS [BuildNumber],
CAST(SERVERPROPERTY('IsSingleUser') AS bit) AS [IsSingleUser],
CAST(SERVERPROPERTY(N'Edition') AS sysname) AS [Edition],
CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition],
CAST(ISNULL(SERVERPROPERTY(N'IsXTPSupported'), 0) AS bit) AS [IsXTPSupported],
SERVERPROPERTY(N'ProductVersion') AS [VersionString],
( select Value from #SVer where Name =N'host_platform') AS [HostPlatform],
CAST(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS bit) AS [IsFullTextInstalled]
ORDER BY
[Server_Name] ASC

        drop table #SVer
GO

-- #7  Object Explorer  ok, 1 set
-- in daffydb
SELECT 
  SERVERPROPERTY('EngineEdition') EngineEdition,
  SERVERPROPERTY('ProductVersion') ProductVersion
GO

-- #8  Object Explorer  ok, 1 set
-- in msdb
select 
    case 
        when object_id('dbo.sysdac_instances') is not null then 1             
    else
        0 
    end
GO

-- #9  Object Explorer  ok, 1 set
-- in master
select suser_sname()
GO

-- #10  Object Explorer  ok, 1 set
-- in master
select has_dbaccess('msdb')
GO

-- #11  Object Explorer  ok, 1 set
-- in daffydb
select SERVERPROPERTY(N'servername')
GO

-- #12  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'server'
SELECT
'PolicyStore[@Name=' + quotename(CAST(serverproperty(N'Servername') AS sysname),'''') + ']' AS [Urn],
CAST(serverproperty(N'Servername') AS sysname) AS [Name],
CAST(
		(SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'Enabled')
	 AS bit) AS [Enabled],
CAST(
		(SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'HistoryRetentionInDays')
	 AS int) AS [HistoryRetentionInDays],
CAST(
    (SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = 'LogOnSuccess')
   AS bit) AS [LogOnSuccess]
WHERE
(CAST(serverproperty(N'Servername') AS sysname)=@_msparam_0)
GO

-- #13  Object Explorer  ok, 1 set
-- in daffydb
SELECT
case when 1=msdb.dbo.fn_syspolicy_is_automation_enabled() and exists (select * from msdb.dbo.syspolicy_system_health_state  where target_query_expression_with_id like 'Server%' ) then 1 else 0 end AS [PolicyHealthState]
GO

-- #14  Object Explorer  ok, 0 sets
-- in daffydb
USE [master]
GO

-- #15  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(SERVERPROPERTY('IsPolyBaseInstalled') AS bit) AS [IsPolyBaseInstalled]
GO

-- #16  Object Explorer  ok, 1 set
-- in master
DECLARE @alwayson INT
        EXECUTE @alwayson = master.dbo.xp_qv N'3641190370', @@SERVICENAME;
      


SELECT
ISNULL(@alwayson, -1) AS [AlwaysOn]
GO

-- #17  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(SERVERPROPERTY(N'IsHadrEnabled') AS bit) AS [IsHadrEnabled]
GO

-- #18  Object Explorer  ok, 1 set
-- in master
IF OBJECT_ID(N'sys.sp_MSIsContainedAGSession', N'P') IS NOT NULL
BEGIN DECLARE @x int; EXECUTE @x = sys.sp_MSIsContainedAGSession; SELECT @x END
ELSE SELECT 0
GO

-- #19  Object Explorer  ok, 1 set
-- in master
SELECT SERVERPROPERTY(N'Edition') AS [Edition], SERVERPROPERTY(N'EngineEdition') AS [EngineEdition]
GO

-- #20  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(SERVERPROPERTY(N'Servername') AS sysname) AS [Name],
'IntegrationServices[@Name=' + quotename(CAST(SERVERPROPERTY(N'Servername') AS sysname),'''') + ']' AS [Urn]
ORDER BY
[Name] ASC
GO

-- #21  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'master'
SELECT
dtb.collation_name AS [Collation],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #22  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'SSISDB'
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #23  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(serverproperty(N'Servername') AS sysname) AS [Name],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/JobServer' AS [Urn]
ORDER BY
[Name] ASC
GO

-- #24  Object Explorer  ok, 1 set
-- in daffydb
select value_in_use from sys.configurations where configuration_id = 16384
GO

-- #25  Object Explorer  ok, 1 set
-- in daffydb
select is_srvrolemember('sysadmin') * 1 +is_srvrolemember('serveradmin') * 2 +is_srvrolemember('setupadmin') * 4 +is_srvrolemember('securityadmin') * 8 +is_srvrolemember('processadmin') * 16 +is_srvrolemember('dbcreator') * 32 +is_srvrolemember('diskadmin') * 64+ is_srvrolemember('bulkadmin') * 128
GO

-- #26  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'msdb'
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #27  Object Explorer  ok, 0 sets
-- in daffydb
USE [msdb]
GO

-- #28  Object Explorer  ok, 1 set
-- in msdb
IF IS_MEMBER('SQLAgentOperatorRole') = 1 BEGIN SELECT 1 END ELSE BEGIN SELECT 0 END
GO

-- #29  Object Explorer  ok, 1 set
-- in msdb
IF IS_MEMBER('SQLAgentReaderRole') = 1 BEGIN SELECT 1 END ELSE BEGIN SELECT 0 END
GO

-- #30  Object Explorer  ok, 1 set
-- in msdb
IF IS_MEMBER('SQLAgentUserRole') = 1 BEGIN SELECT 1 END ELSE BEGIN SELECT 0 END
GO

-- #31  Object Explorer  ok, 1 set
-- in msdb
SELECT
CAST(SERVERPROPERTY(N'Edition') AS sysname) AS [Edition]
GO

-- #32  Object Explorer  ok, 1 set
-- in master
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'0'
        declare @HasViewPermission int
        select @HasViewPermission = HAS_PERMS_BY_NAME(null, null, 'VIEW SERVER STATE')
      


        create table #tmp_db_ars (replica_id uniqueidentifier, group_id uniqueidentifier, replica_server_name sysname)
        create table #tmp_db_ags (group_id uniqueidentifier, name sysname)
      


        create table #tmp_db_hadr_dbrs (group_database_id uniqueidentifier, synchronization_state tinyint, is_local bit, group_id uniqueidentifier, database_id int)
      


        create table #tmp_sync_states (database_id int, sync_state tinyint)
      


        if (@HasViewPermission = 1)
        begin
        insert into #tmp_db_ars select replica_id, group_id, replica_server_name  from  master.sys.availability_replicas
        insert into #tmp_db_ags select group_id, name from master.sys.availability_groups
        end
      


        if (@HasViewPermission = 1)
        begin
        insert into #tmp_db_hadr_dbrs select group_database_id, synchronization_state, is_local, group_id, database_id from master.sys.dm_hadr_database_replica_states
        end
      


        if (@HasViewPermission = 1)
        begin
        if serverproperty('EngineEdition') = 8
        begin
        insert into #tmp_sync_states
        select dtb.database_id, hadrd.synchronization_state as synchronization_state
        from sys.databases AS dtb
        join #tmp_db_hadr_dbrs AS hadrd ON hadrd.database_id = dtb.database_id AND hadrd.is_local = 1
        join #tmp_db_ags AS avag ON hadrd.group_id = avag.group_id
        join #tmp_db_ars AS avar ON avag.name = avar.replica_server_name
        join #tmp_db_ags AS avag2 ON avar.group_id = avag2.group_id
        where hadrd.database_id = dtb.database_id
        end
        else
        begin
        insert into #tmp_sync_states
        select dtb.database_id, hadrd.synchronization_state
        from sys.databases AS dtb
        join #tmp_db_hadr_dbrs AS hadrd ON hadrd.database_id = dtb.database_id AND hadrd.is_local = 1
        end
        end
      





SELECT
dtb.name AS [Database_Name],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(dtb.name,'''') + ']' AS [Database_Urn],
dtb.containment AS [Database_ContainmentType],
dtb.recovery_model AS [Database_RecoveryModel],
ISNULL(suser_sname(dtb.owner_sid),'') AS [Database_Owner],

        case
        when dtb.collation_name is null then 0x200
        else 0
        end |
        case
        when 1 = dtb.is_in_standby then 0x40
        else 0
        end |
        case dtb.state
        when 1 then 0x2
        when 2 then 0x8
        when 3 then 0x4
        when 4 then 0x10
        when 5 then 0x100
        when 6 then 0x20
        else 1
        end
       AS [Database_Status],
dtb.compatibility_level AS [Database_CompatibilityLevel],
ISNULL(dmi.mirroring_role,0) AS [Database_MirroringRole],
ISNULL(dmi.mirroring_state + 1, 0) AS [Database_MirroringStatus],
sync_state.sync_state AS [Database_AvailabilityDatabaseSynchronizationState],
0 AS [Database_HasMemoryOptimizedObjects],
CAST(0 AS bit) AS [Database_RemoteDataArchiveEnabled],
CAST(
        case
        when SERVERPROPERTY('EngineEdition') = 6 then cast(1 as bit)
        else cast(0 as bit)
        end
       AS bit) AS [Database_IsSqlDw],
case when SERVERPROPERTY('EngineEdition') = 8 then SERVERPROPERTY('IsFullTextInstalled') else dtb.is_fulltext_enabled end AS [Database_IsFullTextEnabled],
CAST(ISNULL(dtb.is_ledger_on, 0) AS bit) AS [Database_IsLedger],
dtb.recovery_model AS [RecoveryModel],
dtb.user_access AS [UserAccess],
dtb.is_read_only AS [ReadOnly],
dtb.name AS [Database_DatabaseName2],
dtb.name AS [Database_DatabaseName3]
FROM
master.sys.databases AS dtb
LEFT OUTER JOIN sys.database_mirroring AS dmi ON dmi.database_id = dtb.database_id
LEFT OUTER JOIN #tmp_sync_states AS sync_state ON dtb.database_id = sync_state.database_id

WHERE
(CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=@_msparam_0 and CAST(isnull(dtb.source_database_id, 0) AS bit)=@_msparam_1)
ORDER BY
[Database_Name] ASC

        drop table #tmp_db_ars
        drop table #tmp_db_ags        
      


        drop table #tmp_db_hadr_dbrs
      


        drop table #tmp_sync_states
      
GO

-- #33  Object Explorer  ok, 1 set
-- in master
SELECT CASE WHEN has_dbaccess(N'daffydb') = 1 THEN 'true' ELSE 'false' END
GO

-- #34  Object Explorer  ok, 1 set
-- in master
-- param @dbname nvarchar(6) = N'master'
SELECT COLLATIONPROPERTY((select collation_name from sys.databases where name = ISNULL(@dbname, db_name())), 'LCID'), COLLATIONPROPERTY((select collation_name from sys.databases where name = ISNULL(@dbname, db_name())), 'ComparisonStyle')
GO

-- #35  Object Explorer  gap: Catalog Error: Table with name master_files does not exist!
-- in daffydb
SELECT
db.name as HasMemoryOptimizedObjects from master.sys.master_files mf join master.sys.databases db on mf.database_id = db.database_id where mf.[type] = 2
GO

-- #36  Object Explorer  ok, 1 set
-- in master
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
SELECT
CAST(has_dbaccess(dtb.name) AS bit) AS [IsAccessible]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #37  Object Explorer  ok, 1 set
-- in daffydb
-- param @DbName nvarchar(12) = N'daffydb'
SELECT ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(@DbName, ']'), 'DATABASE', 'VIEW DATABASE STATE'), -1)
GO

-- #38  Object Explorer  ok, 0 sets
-- in daffydb
IF OBJECT_ID (N'[sys].[database_query_store_options]') IS NOT NULL BEGIN SELECT ISNULL(actual_state, -2) FROM sys.database_query_store_options; IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats) SELECT 1 ELSE SELECT 0; END
GO

-- #39  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
-- param @_msparam_3 nvarchar(4000) = N'0'
-- param @_msparam_4 nvarchar(4000) = N'0'
-- param @_msparam_5 nvarchar(4000) = N'0'
-- param @_msparam_6 nvarchar(4000) = N'0'
-- param @_msparam_7 nvarchar(4000) = N'0'
-- param @_msparam_8 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
CAST(CASE idx.type WHEN 5 THEN 1 ELSE 0 END AS bit) AS [HasClusteredColumnStoreIndex],
CAST(0 AS bit) AS [RemoteDataArchiveEnabled],
tbl.temporal_type AS [TemporalType],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
CAST(CASE WHEN 'PS'=dsidx.type THEN 1 ELSE 0 END AS bit) AS [IsPartitioned],
CAST(
        ISNULL((SELECT distinct 1 from sys.all_columns
        WHERE object_id = tbl.object_id
        AND is_sparse = 1), 0)
       AS bit) AS [HasSparseColumn],
CAST(tbl.is_node AS bit) AS [IsNode],
CAST(tbl.is_edge AS bit) AS [IsEdge],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
LEFT OUTER JOIN sys.data_spaces AS dsidx ON dsidx.data_space_id = idx.data_space_id
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and tbl.is_filetable=@_msparam_2 and CAST(tbl.is_memory_optimized AS bit)=@_msparam_3 and tbl.temporal_type=@_msparam_4 and CAST(tbl.is_external AS bit)=@_msparam_5 and CAST(tbl.is_node AS bit)=@_msparam_6 and CAST(tbl.is_edge AS bit)=@_msparam_7 and CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_8)
GO

-- #40  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
-- param @_msparam_3 nvarchar(4000) = N'0'
-- param @_msparam_4 nvarchar(4000) = N'2'
-- param @_msparam_5 nvarchar(4000) = N'0'
-- param @_msparam_6 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
CAST(CASE idx.type WHEN 5 THEN 1 ELSE 0 END AS bit) AS [HasClusteredColumnStoreIndex],
CAST(ISNULL(historyTable.object_id, 0) AS int) AS [HistoryTableID],
CAST(ISNULL(historyTable.name, N'') AS sysname) AS [HistoryTableName],
CAST(ISNULL(SCHEMA_NAME(historyTable.schema_id), N'') AS sysname) AS [HistoryTableSchema],
tbl.temporal_type AS [TemporalType],
CAST(CASE WHEN 'PS'=dsidx.type THEN 1 ELSE 0 END AS bit) AS [IsPartitioned],
CAST(
        ISNULL((SELECT distinct 1 from sys.all_columns
        WHERE object_id = tbl.object_id
        AND is_sparse = 1), 0)
       AS bit) AS [HasSparseColumn],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
LEFT OUTER JOIN sys.tables as historyTable ON historyTable.object_id = tbl.history_table_id
LEFT OUTER JOIN sys.data_spaces AS dsidx ON dsidx.data_space_id = idx.data_space_id
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and tbl.is_filetable=@_msparam_2 and CAST(tbl.is_memory_optimized AS bit)=@_msparam_3 and tbl.temporal_type=@_msparam_4 and CAST(tbl.is_external AS bit)=@_msparam_5 and CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_6)
GO

-- #41  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
-- param @_msparam_3 nvarchar(4000) = N'1'
-- param @_msparam_4 nvarchar(4000) = N'2'
-- param @_msparam_5 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
case when (tbl.durability=1) then 0 else 1 end AS [Durability],
CAST(ISNULL(historyTable.object_id, 0) AS int) AS [HistoryTableID],
CAST(ISNULL(historyTable.name, N'') AS sysname) AS [HistoryTableName],
CAST(ISNULL(SCHEMA_NAME(historyTable.schema_id), N'') AS sysname) AS [HistoryTableSchema],
tbl.temporal_type AS [TemporalType],
CAST(0 AS bit) AS [RemoteDataArchiveEnabled],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
LEFT OUTER JOIN sys.tables as historyTable ON historyTable.object_id = tbl.history_table_id
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and tbl.is_filetable=@_msparam_2 and CAST(tbl.is_memory_optimized AS bit)=@_msparam_3 and tbl.temporal_type=@_msparam_4 and CAST(tbl.is_external AS bit)=@_msparam_5)
GO

-- #42  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
-- param @_msparam_3 nvarchar(4000) = N'1'
-- param @_msparam_4 nvarchar(4000) = N'0'
-- param @_msparam_5 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
case when (tbl.durability=1) then 0 else 1 end AS [Durability],
tbl.temporal_type AS [TemporalType],
CAST(0 AS bit) AS [RemoteDataArchiveEnabled],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and tbl.is_filetable=@_msparam_2 and CAST(tbl.is_memory_optimized AS bit)=@_msparam_3 and tbl.temporal_type=@_msparam_4 and CAST(tbl.is_external AS bit)=@_msparam_5)
GO

-- #43  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'2'
-- param @_msparam_3 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
CAST(CASE idx.type WHEN 5 THEN 1 ELSE 0 END AS bit) AS [HasClusteredColumnStoreIndex],
CAST(ISNULL(historyTable.object_id, 0) AS int) AS [HistoryTableID],
CAST(ISNULL(historyTable.name, N'') AS sysname) AS [HistoryTableName],
CAST(ISNULL(SCHEMA_NAME(historyTable.schema_id), N'') AS sysname) AS [HistoryTableSchema],
tbl.temporal_type AS [TemporalType],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit) AS [IsDroppedLedgerTable],
CAST(CASE WHEN 'PS'=dsidx.type THEN 1 ELSE 0 END AS bit) AS [IsPartitioned],
CAST(
        ISNULL((SELECT distinct 1 from sys.all_columns
        WHERE object_id = tbl.object_id
        AND is_sparse = 1), 0)
       AS bit) AS [HasSparseColumn],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
LEFT OUTER JOIN sys.tables as historyTable ON historyTable.object_id = tbl.history_table_id
LEFT OUTER JOIN sys.data_spaces AS dsidx ON dsidx.data_space_id = idx.data_space_id
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_2 and CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit)=@_msparam_3)
GO

-- #44  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'3'
-- param @_msparam_3 nvarchar(4000) = N'0'
SELECT
tbl.name AS [Name],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' AS [Urn],
tbl.create_date AS [CreateDate],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
CAST(CASE idx.type WHEN 5 THEN 1 ELSE 0 END AS bit) AS [HasClusteredColumnStoreIndex],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit) AS [IsDroppedLedgerTable],
CAST(CASE WHEN 'PS'=dsidx.type THEN 1 ELSE 0 END AS bit) AS [IsPartitioned],
CAST(
        ISNULL((SELECT distinct 1 from sys.all_columns
        WHERE object_id = tbl.object_id
        AND is_sparse = 1), 0)
       AS bit) AS [HasSparseColumn],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex]
FROM
sys.tables AS tbl
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
LEFT OUTER JOIN sys.data_spaces AS dsidx ON dsidx.data_space_id = idx.data_space_id
WHERE
(CAST(
 case 
    when tbl.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = tbl.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit)=@_msparam_1 and CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_2 and CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit)=@_msparam_3)
GO

-- #45  Object Explorer  ok, 1 set
-- in daffydb
-- param @dbname nvarchar(12) = N'daffydb'
SELECT COLLATIONPROPERTY((select collation_name from sys.databases where name = ISNULL(@dbname, db_name())), 'LCID'), COLLATIONPROPERTY((select collation_name from sys.databases where name = ISNULL(@dbname, db_name())), 'ComparisonStyle')
GO

-- #46  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'2'
-- param @_msparam_3 nvarchar(4000) = N'5'
-- param @_msparam_4 nvarchar(4000) = N'8'
-- param @_msparam_5 nvarchar(4000) = N'0'
-- param @_msparam_6 nvarchar(4000) = N'customers'
-- param @_msparam_7 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
WHERE
(CAST(clmns.is_sparse AS bit)=@_msparam_0 and (ISNULL(clmns.graph_type, 0)=@_msparam_1 or ISNULL(clmns.graph_type, 0)=@_msparam_2 or ISNULL(clmns.graph_type, 0)=@_msparam_3 or ISNULL(clmns.graph_type, 0)=@_msparam_4) and CAST(clmns.is_dropped_ledger_column AS bit)=@_msparam_5)and((tbl.name=@_msparam_6 and SCHEMA_NAME(tbl.schema_id)=@_msparam_7))
ORDER BY
clmns.column_id ASC
GO

-- #47  Object Explorer  ok, 1 set
-- in daffydb
declare @HkeyLocal nvarchar(18)
        declare @ServicesRegPath nvarchar(34)
        declare @SqlServiceRegPath sysname
        declare @BrowserServiceRegPath sysname
        declare @MSSqlServerRegPath nvarchar(31)
        declare @InstanceNamesRegPath nvarchar(59)
        declare @InstanceRegPath sysname
        declare @SetupRegPath sysname
        declare @NpRegPath sysname
        declare @TcpRegPath sysname
        declare @RegPathParams sysname
        declare @FilestreamRegPath sysname

        select @HkeyLocal=N'HKEY_LOCAL_MACHINE'

        -- Instance-based paths
        select @MSSqlServerRegPath=N'SOFTWARE\Microsoft\MSSQLServer'
        select @InstanceRegPath=@MSSqlServerRegPath + N'\MSSQLServer'
        select @FilestreamRegPath=@InstanceRegPath + N'\Filestream'
        select @SetupRegPath=@MSSqlServerRegPath + N'\Setup'
        select @RegPathParams=@InstanceRegPath+'\Parameters'

        -- Services
        select @ServicesRegPath=N'SYSTEM\CurrentControlSet\Services'
        select @SqlServiceRegPath=@ServicesRegPath + N'\MSSQLSERVER'
        select @BrowserServiceRegPath=@ServicesRegPath + N'\SQLBrowser'

        -- InstanceId setting
        select @InstanceNamesRegPath=N'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'

        -- Network settings
        select @NpRegPath=@InstanceRegPath + N'\SuperSocketNetLib\Np'
        select @TcpRegPath=@InstanceRegPath + N'\SuperSocketNetLib\Tcp'
      


        declare @SmoAuditLevel int
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'AuditLevel', @SmoAuditLevel OUTPUT
      


        declare @NumErrorLogs int
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'NumErrorLogs', @NumErrorLogs OUTPUT
      


        declare @SmoLoginMode int
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'LoginMode', @SmoLoginMode OUTPUT
      


        declare @SmoMailProfile nvarchar(512)
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'MailAccountName', @SmoMailProfile OUTPUT
      


        declare @BackupDirectory nvarchar(512)
        if 1=isnull(cast(SERVERPROPERTY('IsLocalDB') as bit), 0)
        select @BackupDirectory=cast(SERVERPROPERTY('instancedefaultdatapath') as nvarchar(512))
        else
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'BackupDirectory', @BackupDirectory OUTPUT
      


        declare @SmoPerfMonMode int
        exec master.dbo.xp_instance_regread @HkeyLocal, @InstanceRegPath, N'Performance', @SmoPerfMonMode OUTPUT

        if @SmoPerfMonMode is null
        begin
        set @SmoPerfMonMode = 1000
        end
      


        declare @InstallSqlDataDir nvarchar(512)
        exec master.dbo.xp_instance_regread @HkeyLocal, @SetupRegPath, N'SQLDataRoot', @InstallSqlDataDir OUTPUT
      


        declare @MasterPath nvarchar(512)
        declare @LogPath nvarchar(512)
        declare @ErrorLog nvarchar(512)
        declare @ErrorLogPath nvarchar(512)
        declare @Slash varchar = convert(varchar, serverproperty('PathSeparator'))
        select @MasterPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where file_id = 1
        select @LogPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where file_id = 2
        select @ErrorLog=cast(SERVERPROPERTY(N'errorlogfilename') as nvarchar(512))
        select @ErrorLogPath=IIF(@ErrorLog IS NULL, N'', substring(@ErrorLog, 1, len(@ErrorLog) - charindex(@Slash, reverse(@ErrorLog))))
      


        declare @SmoRoot nvarchar(512)
        exec master.dbo.xp_instance_regread @HkeyLocal, @SetupRegPath, N'SQLPath', @SmoRoot OUTPUT
      


        declare @ServiceStartMode int
        EXEC master.sys.xp_instance_regread @HkeyLocal, @SqlServiceRegPath, N'Start', @ServiceStartMode OUTPUT
      


        declare @ServiceAccount nvarchar(512)
        EXEC master.sys.xp_instance_regread @HkeyLocal, @SqlServiceRegPath, N'ObjectName', @ServiceAccount OUTPUT
      


        declare @NamedPipesEnabled int
        exec master.dbo.xp_instance_regread @HkeyLocal, @NpRegPath, N'Enabled', @NamedPipesEnabled OUTPUT
      


        declare @TcpEnabled int
        EXEC master.sys.xp_instance_regread @HkeyLocal, @TcpRegPath, N'Enabled', @TcpEnabled OUTPUT
      


        declare @InstallSharedDirectory nvarchar(512)
        EXEC master.sys.xp_instance_regread @HkeyLocal, @SetupRegPath, N'SQLPath', @InstallSharedDirectory OUTPUT
      


        declare @SqlGroup nvarchar(512)
        exec master.dbo.xp_instance_regread @HkeyLocal, @SetupRegPath, N'SQLGroup', @SqlGroup OUTPUT
      


        declare @FilestreamLevel int
        exec master.dbo.xp_instance_regread @HkeyLocal, @FilestreamRegPath, N'EnableLevel', @FilestreamLevel OUTPUT
      


        declare @FilestreamShareName nvarchar(512)
        exec master.dbo.xp_instance_regread @HkeyLocal, @FilestreamRegPath, N'ShareName', @FilestreamShareName OUTPUT
      


        declare @cluster_name nvarchar(128)
        declare @quorum_type tinyint
        declare @quorum_state tinyint
        BEGIN TRY
        SELECT @cluster_name = cluster_name,
        @quorum_type = quorum_type,
        @quorum_state = quorum_state
        FROM sys.dm_hadr_cluster
        END TRY
        BEGIN CATCH
          IF(ERROR_NUMBER() NOT IN (297,300, 15562, 371))
        BEGIN
        THROW
        END
        END CATCH
      

SELECT
@SmoAuditLevel AS [AuditLevel],
ISNULL(@NumErrorLogs, -1) AS [NumberOfLogFiles],
(case when @SmoLoginMode < 3 then @SmoLoginMode else 9 end) AS [LoginMode],
ISNULL(@SmoMailProfile,N'') AS [MailProfile],
@BackupDirectory AS [BackupDirectory],
@SmoPerfMonMode AS [PerfMonMode],
ISNULL(@InstallSqlDataDir,N'') AS [InstallDataDirectory],
CAST(@@SERVICENAME AS sysname) AS [ServiceName],
@ErrorLogPath AS [ErrorLogPath],
@SmoRoot AS [RootDirectory],
CAST(case when 'a' <> 'A' then 1 else 0 end AS bit) AS [IsCaseSensitive],
CAST(ISNULL(SERVERPROPERTY(N'IsLocalDB'), 0) AS bit) AS [IsLocalDB],
@@MAX_PRECISION AS [MaxPrecision],
CAST(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS bit) AS [IsFullTextInstalled],
SERVERPROPERTY(N'ProductVersion') AS [VersionString],
CAST(SERVERPROPERTY(N'Edition') AS sysname) AS [Edition],
CAST(SERVERPROPERTY(N'ProductLevel') AS sysname) AS [ProductLevel],
CAST(ISNULL(SERVERPROPERTY(N'ProductUpdateLevel'), N'') AS sysname) AS [ProductUpdateLevel],
CAST(SERVERPROPERTY('IsSingleUser') AS bit) AS [IsSingleUser],
CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition],
CAST(ISNULL(SERVERPROPERTY('IsClustered'), 0) AS bit) AS [IsClustered],
CAST(ISNULL(SERVERPROPERTY(N'MachineName'), N'') AS sysname) AS [NetName],
ISNULL(SERVERPROPERTY(N'ComputerNamePhysicalNetBIOS'),N'') AS [ComputerNamePhysicalNetBIOS],
ISNULL(@ServiceStartMode,2) AS [ServiceStartMode],
@LogPath AS [MasterDBLogPath],
@MasterPath AS [MasterDBPath],
SERVERPROPERTY('instancedefaultdatapath') AS [DefaultFile],
SERVERPROPERTY('instancedefaultlogpath') AS [DefaultLog],
SERVERPROPERTY(N'ResourceVersion') AS [ResourceVersionString],
SERVERPROPERTY(N'ResourceLastUpdateDateTime') AS [ResourceLastUpdateDateTime],
SERVERPROPERTY(N'CollationID') AS [CollationID],
SERVERPROPERTY(N'ComparisonStyle') AS [ComparisonStyle],
SERVERPROPERTY(N'SqlCharSet') AS [SqlCharSet],
SERVERPROPERTY(N'SqlCharSetName') AS [SqlCharSetName],
SERVERPROPERTY(N'SqlSortOrder') AS [SqlSortOrder],
SERVERPROPERTY(N'SqlSortOrderName') AS [SqlSortOrderName],
SERVERPROPERTY(N'BuildClrVersion') AS [BuildClrVersionString],
ISNULL(@ServiceAccount,N'') AS [ServiceAccount],
CAST(@NamedPipesEnabled AS bit) AS [NamedPipesEnabled],
CAST(@TcpEnabled AS bit) AS [TcpEnabled],
ISNULL(@InstallSharedDirectory,N'') AS [InstallSharedDirectory],
ISNULL(suser_sname(sid_binary(ISNULL(@SqlGroup,N''))),N'') AS [SqlDomainGroup],
case when 1=msdb.dbo.fn_syspolicy_is_automation_enabled() and exists (select * from msdb.dbo.syspolicy_system_health_state  where target_query_expression_with_id like 'Server%' ) then 1 else 0 end AS [PolicyHealthState],
@FilestreamLevel AS [FilestreamLevel],
ISNULL(@FilestreamShareName,N'') AS [FilestreamShareName],
-1 AS [TapeLoadWaitTime],
CAST(SERVERPROPERTY(N'IsHadrEnabled') AS bit) AS [IsHadrEnabled],
SERVERPROPERTY(N'HADRManagerStatus') AS [HadrManagerStatus],
ISNULL(@cluster_name, '') AS [ClusterName],
ISNULL(@quorum_type, 4) AS [ClusterQuorumType],
ISNULL(@quorum_state, 3) AS [ClusterQuorumState],
SUSER_SID(@ServiceAccount, 0) AS [ServiceAccountSid],
CAST(SERVERPROPERTY('IsPolyBaseInstalled') AS bit) AS [IsPolyBaseInstalled],
CAST(
        serverproperty(N'Servername')
       AS sysname) AS [Name],
CAST(
        ISNULL(serverproperty(N'instancename'),N'')
       AS sysname) AS [InstanceName],
CAST(0x0001 AS int) AS [Status],
SERVERPROPERTY('PathSeparator') AS [PathSeparator],
0 AS [IsContainedAuthentication],
CAST(null AS int) AS [ServerType]
GO

-- #48  Query window  ok, 0 sets
-- in daffydb
SET ROWCOUNT 0 SET TEXTSIZE 2147483647 SET NOCOUNT OFF SET CONCAT_NULL_YIELDS_NULL ON SET ARITHABORT ON SET LOCK_TIMEOUT -1 SET QUERY_GOVERNOR_COST_LIMIT 0 SET DEADLOCK_PRIORITY NORMAL SET TRANSACTION ISOLATION LEVEL READ COMMITTED  SET ANSI_NULLS ON SET ANSI_NULL_DFLT_ON ON SET ANSI_PADDING ON SET ANSI_WARNINGS ON SET CURSOR_CLOSE_ON_COMMIT OFF SET IMPLICIT_TRANSACTIONS OFF SET QUOTED_IDENTIFIER ON
GO

-- #49  Query window  ok, 4 sets
-- in daffydb
select SERVERPROPERTY('ProductLevel');
if serverproperty('EngineEdition') = 5
begin
    exec sp_executesql N'select CONVERT(NVARCHAR(36), CONTEXT_INFO())'
end
else
begin
select ''
end
if serverproperty('EngineEdition') not in (6)
begin
  begin try
    exec sp_executesql N'select case when encrypt_option = N''TRUE'' then 1 else 0 end, protocol_version from sys.dm_exec_connections where session_id = @@SPID'
  end try
  begin catch
    select -1, 0
  end catch
end
else
begin
select 100, 0
end
BEGIN TRY
    EXEC sp_executesql N'BEGIN TRAN SELECT @@TRANCOUNT COMMIT TRAN'
END TRY
BEGIN CATCH
    -- -1 is used to indicate that the server doesn't support transactions
    SELECT -1
END CATCH
GO

-- #50  Query window  ok, 1 set
-- in daffydb
SELECT TOP (1000) [customer_id]
      ,[name]
      ,[city]
      ,[is_active]
      ,[signup_date]
  FROM [daffydb].[dbo].[customers]
GO

-- #51  Query window  ok, 1 set
-- in daffydb
SELECT dtb.name AS [Name], dtb.state AS [State] FROM master.sys.databases dtb
GO

-- #52  IntelliSense  ok, 1 set
-- in master
SELECT
CAST(
        serverproperty(N'Servername')
       AS sysname) AS [Name]
GO

-- #53  IntelliSense  ok, 1 set
-- in master
declare @MasterPath nvarchar(512)
        declare @LogPath nvarchar(512)
        declare @ErrorLog nvarchar(512)
        declare @ErrorLogPath nvarchar(512)
        declare @Slash varchar = convert(varchar, serverproperty('PathSeparator'))
        if (SERVERPROPERTY('EngineEdition') = 8 /* SQL Managed Instance */)
        begin
          select @MasterPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where file_id = 1
          select @LogPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where file_id = 2
        end
        else
        begin
          select @MasterPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where name=N'master'
          select @LogPath=substring(physical_name, 1, len(physical_name) - charindex(@Slash, reverse(physical_name))) from master.sys.database_files where name=N'mastlog'
        end
        select @ErrorLog=cast(SERVERPROPERTY(N'errorlogfilename') as nvarchar(512))
        select @ErrorLogPath=IIF(@ErrorLog IS NULL, N'', substring(@ErrorLog, 1, len(@ErrorLog) - charindex(@Slash, reverse(@ErrorLog))))
      


SELECT
CAST(case when 'a' <> 'A' then 1 else 0 end AS bit) AS [IsCaseSensitive],
@@MAX_PRECISION AS [MaxPrecision],
@ErrorLogPath AS [ErrorLogPath],
SERVERPROPERTY('PathSeparator') AS [PathSeparator],
CAST(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS bit) AS [IsFullTextInstalled],
@LogPath AS [MasterDBLogPath],
@MasterPath AS [MasterDBPath],
SERVERPROPERTY(N'ProductVersion') AS [VersionString],
CAST(SERVERPROPERTY(N'Edition') AS sysname) AS [Edition],
CAST(SERVERPROPERTY(N'ProductLevel') AS sysname) AS [ProductLevel],
CAST(SERVERPROPERTY('IsSingleUser') AS bit) AS [IsSingleUser],
CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition],
convert(sysname, serverproperty(N'collation')) AS [Collation],
CAST(ISNULL(SERVERPROPERTY(N'MachineName'),N'') AS sysname) AS [NetName],
CAST(ISNULL(SERVERPROPERTY('IsClustered'),N'') AS bit) AS [IsClustered],
SERVERPROPERTY(N'ResourceVersion') AS [ResourceVersionString],
SERVERPROPERTY(N'ResourceLastUpdateDateTime') AS [ResourceLastUpdateDateTime],
SERVERPROPERTY(N'CollationID') AS [CollationID],
SERVERPROPERTY(N'ComparisonStyle') AS [ComparisonStyle],
SERVERPROPERTY(N'SqlCharSet') AS [SqlCharSet],
SERVERPROPERTY(N'SqlCharSetName') AS [SqlCharSetName],
SERVERPROPERTY(N'SqlSortOrder') AS [SqlSortOrder],
SERVERPROPERTY(N'SqlSortOrderName') AS [SqlSortOrderName],
SERVERPROPERTY(N'BuildClrVersion') AS [BuildClrVersionString],
ISNULL(SERVERPROPERTY(N'ComputerNamePhysicalNetBIOS'),N'') AS [ComputerNamePhysicalNetBIOS],
CAST(SERVERPROPERTY('IsPolyBaseInstalled') AS bit) AS [IsPolyBaseInstalled],
CAST(0 AS bit) AS [IsFabricServer]
GO

-- #54  IntelliSense  ok, 1 set
-- in master
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],
dtb.database_id AS [ID],
CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit) AS [IsSystemObject],
CAST(has_dbaccess(dtb.name) AS bit) AS [IsAccessible],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
ORDER BY
[Name] ASC
GO

-- #55  IntelliSense  ok, 1 set
-- in master
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],
dtb.database_id AS [ID],
CAST(has_dbaccess(dtb.name) AS bit) AS [IsAccessible],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
ORDER BY
[Name] ASC
GO

-- #56  IntelliSense  gap: Catalog Error: Table with name asymmetric_keys does not exist!
-- in master
SELECT
log.name AS [Name],
log.principal_id AS [ID],
ISNULL(ak.name,N'') AS [AsymmetricKey],
ISNULL(cert.name,N'') AS [Certificate],
ISNULL(c.name,N'') AS [Credential],
CASE WHEN N'U' = log.type THEN 0 WHEN N'G' = log.type THEN 1 WHEN N'S' = log.type THEN 2 WHEN N'C' = log.type THEN 3 WHEN N'K' = log.type THEN 4  WHEN N'E' = log.type THEN 5 WHEN N'X' = log.type THEN 6 END AS [LoginType]
FROM
sys.server_principals AS log
LEFT OUTER JOIN master.sys.asymmetric_keys AS ak ON ak.sid = log.sid
LEFT OUTER JOIN master.sys.certificates AS cert ON cert.sid = log.sid
LEFT OUTER JOIN sys.credentials AS c ON c.credential_id = log.credential_id
WHERE
(log.type in ('U', 'G', 'S', 'C', 'K', 'E', 'X') AND log.principal_id not between 101 and 255 AND log.name <> N'##MS_AgentSigningCertificate##')
ORDER BY
[Name] ASC
GO

-- #57  IntelliSense  gap: Catalog Error: Table with name server_triggers does not exist!
-- in master
SELECT
tr.name AS [Name],
tr.object_id AS [ID],
CAST(
                tr.is_ms_shipped
            AS bit) AS [IsSystemObject],
CASE WHEN tr.type = N'TR' THEN 1 WHEN tr.type = N'TA' THEN 2 ELSE 1 END AS [ImplementationType],
CAST(CASE WHEN ssmod.definition IS NULL THEN 1 ELSE 0 END AS bit) AS [IsEncrypted]
FROM
master.sys.server_triggers AS tr
LEFT OUTER JOIN sys.server_sql_modules AS ssmod ON ssmod.object_id = tr.object_id
WHERE
(tr.parent_class = 100)
ORDER BY
[Name] ASC
GO

-- #58  IntelliSense  ok, 1 set
-- in master
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
SELECT
dtb.containment AS [ContainmentType]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #59  IntelliSense  ok, 1 set
-- in daffydb
SELECT
rl.name AS [Name]
FROM
sys.database_principals AS rl
WHERE
(rl.type = 'A')
ORDER BY
[Name] ASC
GO

-- #60  IntelliSense  ok, 1 set
-- in daffydb
SELECT
rl.name AS [Name]
FROM
sys.database_principals AS rl
WHERE
(rl.type = 'R')
ORDER BY
[Name] ASC
GO

-- #61  IntelliSense  ok, 1 set
-- in daffydb
SELECT
(@@microsoftversion / 0x1000000) & 0xff AS [VersionMajor]
GO

-- #62  IntelliSense  ok, 1 set
-- in daffydb
SELECT
s.name AS [Name]
FROM
sys.schemas AS s
ORDER BY
[Name] ASC
GO

-- #63  IntelliSense  gap: Catalog Error: Table with name triggers does not exist!
-- in daffydb
SELECT
tr.name AS [Name],
tr.object_id AS [ID],
CAST(
                tr.is_ms_shipped
            AS bit) AS [IsSystemObject],
CASE WHEN tr.type = N'TR' THEN 1 WHEN tr.type = N'TA' THEN 2 ELSE 1 END AS [ImplementationType],
CAST(CASE WHEN ISNULL(smtr.definition, ssmtr.definition) IS NULL THEN 1 ELSE 0 END AS bit) AS [IsEncrypted]
FROM
sys.triggers AS tr
LEFT OUTER JOIN sys.sql_modules AS smtr ON smtr.object_id = tr.object_id
LEFT OUTER JOIN sys.system_sql_modules AS ssmtr ON ssmtr.object_id = tr.object_id
WHERE
(tr.parent_class = 0)
ORDER BY
[Name] ASC
GO

-- #64  IntelliSense  gap: Catalog Error: Table with name asymmetric_keys does not exist!
-- in daffydb
SELECT
u.name AS [Name],
u.principal_id AS [ID],
ISNULL(ak.name,N'') AS [AsymmetricKey],
ISNULL(cert.name,N'') AS [Certificate],

        CASE
        WHEN N'C' = u.type THEN 1
        WHEN N'K' = u.type THEN 2
        WHEN N'S' = u.type AND SUSER_SNAME(u.sid) is null AND u.authentication_type != 2 THEN 3
        WHEN N'E' = u.type or N'X' = u.type THEN 4
        ELSE 0 END
       AS [UserType]
FROM
sys.database_principals AS u
LEFT OUTER JOIN sys.asymmetric_keys AS ak ON ak.sid = u.sid
LEFT OUTER JOIN sys.certificates AS cert ON cert.sid = u.sid
WHERE
(u.type in ('U', 'S', 'G', 'C', 'K' ,'E', 'X'))
ORDER BY
[Name] ASC
GO

-- #65  IntelliSense  gap: Catalog Error: Table with name database_permissions does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'CO'
-- param @_msparam_1 nvarchar(4000) = N'db_user'
SELECT
u.name AS [Name],
u.principal_id AS [ID],
CAST(CASE WHEN u.principal_id < 5 OR (u.principal_id >= 16384 and u.principal_id < 16400) THEN 1 ELSE 0 END AS bit) AS [IsSystemObject],
CASE WHEN N'U' = u.type THEN 0 WHEN N'G' = u.type THEN 1 WHEN N'S' = u.type THEN 2 WHEN N'C' = u.type THEN 3 WHEN N'K' = u.type THEN 4 WHEN N'E' = u.type THEN 5 WHEN N'X' = u.type THEN 6 END AS [LoginType],
CAST(CASE dp.state WHEN N'G' THEN 1 WHEN 'W' THEN 1 ELSE 0 END AS bit) AS [HasDBAccess],
u.sid AS [Sid],
u.create_date AS [CreateDate],
u.modify_date AS [DateLastModified],
ISNULL(u.default_schema_name,N'') AS [DefaultSchema],
ISNULL(ak.name,N'') AS [AsymmetricKey],
ISNULL(cert.name,N'') AS [Certificate],
ISNULL(suser_sname(u.sid),N'') AS [Login],
ISNULL(u.default_language_lcid, -1) AS [DefaultLanguageLcid],
ISNULL(u.default_language_name,N'') AS [DefaultLanguageName],
u.authentication_type AS [AuthenticationType],

        CASE
        WHEN N'C' = u.type THEN 1
        WHEN N'K' = u.type THEN 2
        WHEN N'S' = u.type AND SUSER_SNAME(u.sid) is null AND u.authentication_type != 2 THEN 3
        WHEN N'E' = u.type or N'X' = u.type THEN 4
        ELSE 0 END
       AS [UserType]
FROM
sys.database_principals AS u
LEFT OUTER JOIN sys.database_permissions AS dp ON dp.grantee_principal_id = u.principal_id and dp.type = @_msparam_0
LEFT OUTER JOIN sys.asymmetric_keys AS ak ON ak.sid = u.sid
LEFT OUTER JOIN sys.certificates AS cert ON cert.sid = u.sid
WHERE
(u.type in ('U', 'S', 'G', 'C', 'K' ,'E', 'X'))and(u.name=@_msparam_1)
GO

-- #66  IntelliSense  gap: Catalog Error: Table with name database_permissions does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'CO'
-- param @_msparam_1 nvarchar(4000) = N'dbo'
SELECT
u.name AS [Name],
u.principal_id AS [ID],
CAST(CASE WHEN u.principal_id < 5 OR (u.principal_id >= 16384 and u.principal_id < 16400) THEN 1 ELSE 0 END AS bit) AS [IsSystemObject],
CASE WHEN N'U' = u.type THEN 0 WHEN N'G' = u.type THEN 1 WHEN N'S' = u.type THEN 2 WHEN N'C' = u.type THEN 3 WHEN N'K' = u.type THEN 4 WHEN N'E' = u.type THEN 5 WHEN N'X' = u.type THEN 6 END AS [LoginType],
CAST(CASE dp.state WHEN N'G' THEN 1 WHEN 'W' THEN 1 ELSE 0 END AS bit) AS [HasDBAccess],
u.sid AS [Sid],
u.create_date AS [CreateDate],
u.modify_date AS [DateLastModified],
ISNULL(u.default_schema_name,N'') AS [DefaultSchema],
ISNULL(ak.name,N'') AS [AsymmetricKey],
ISNULL(cert.name,N'') AS [Certificate],
ISNULL(suser_sname(u.sid),N'') AS [Login],
ISNULL(u.default_language_lcid, -1) AS [DefaultLanguageLcid],
ISNULL(u.default_language_name,N'') AS [DefaultLanguageName],
u.authentication_type AS [AuthenticationType],

        CASE
        WHEN N'C' = u.type THEN 1
        WHEN N'K' = u.type THEN 2
        WHEN N'S' = u.type AND SUSER_SNAME(u.sid) is null AND u.authentication_type != 2 THEN 3
        WHEN N'E' = u.type or N'X' = u.type THEN 4
        ELSE 0 END
       AS [UserType]
FROM
sys.database_principals AS u
LEFT OUTER JOIN sys.database_permissions AS dp ON dp.grantee_principal_id = u.principal_id and dp.type = @_msparam_0
LEFT OUTER JOIN sys.asymmetric_keys AS ak ON ak.sid = u.sid
LEFT OUTER JOIN sys.certificates AS cert ON cert.sid = u.sid
WHERE
(u.type in ('U', 'S', 'G', 'C', 'K' ,'E', 'X'))and(u.name=@_msparam_1)
GO

-- #67  IntelliSense  gap: Catalog Error: Table with name database_permissions does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'CO'
-- param @_msparam_1 nvarchar(4000) = N'app_login'
SELECT
u.name AS [Name],
u.principal_id AS [ID],
CAST(CASE WHEN u.principal_id < 5 OR (u.principal_id >= 16384 and u.principal_id < 16400) THEN 1 ELSE 0 END AS bit) AS [IsSystemObject],
CASE WHEN N'U' = u.type THEN 0 WHEN N'G' = u.type THEN 1 WHEN N'S' = u.type THEN 2 WHEN N'C' = u.type THEN 3 WHEN N'K' = u.type THEN 4 WHEN N'E' = u.type THEN 5 WHEN N'X' = u.type THEN 6 END AS [LoginType],
CAST(CASE dp.state WHEN N'G' THEN 1 WHEN 'W' THEN 1 ELSE 0 END AS bit) AS [HasDBAccess],
u.sid AS [Sid],
u.create_date AS [CreateDate],
u.modify_date AS [DateLastModified],
ISNULL(u.default_schema_name,N'') AS [DefaultSchema],
ISNULL(ak.name,N'') AS [AsymmetricKey],
ISNULL(cert.name,N'') AS [Certificate],
ISNULL(suser_sname(u.sid),N'') AS [Login],
ISNULL(u.default_language_lcid, -1) AS [DefaultLanguageLcid],
ISNULL(u.default_language_name,N'') AS [DefaultLanguageName],
u.authentication_type AS [AuthenticationType],

        CASE
        WHEN N'C' = u.type THEN 1
        WHEN N'K' = u.type THEN 2
        WHEN N'S' = u.type AND SUSER_SNAME(u.sid) is null AND u.authentication_type != 2 THEN 3
        WHEN N'E' = u.type or N'X' = u.type THEN 4
        ELSE 0 END
       AS [UserType]
FROM
sys.database_principals AS u
LEFT OUTER JOIN sys.database_permissions AS dp ON dp.grantee_principal_id = u.principal_id and dp.type = @_msparam_0
LEFT OUTER JOIN sys.asymmetric_keys AS ak ON ak.sid = u.sid
LEFT OUTER JOIN sys.certificates AS cert ON cert.sid = u.sid
WHERE
(u.type in ('U', 'S', 'G', 'C', 'K' ,'E', 'X'))and(u.name=@_msparam_1)
GO

-- #68  IntelliSense  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
SELECT
'' AS [DefaultSchema],
dtb.name AS [DatabaseName]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #69  IntelliSense  ok, 1 set
-- in daffydb
SELECT
(select schema_name()) AS [DefaultSchema]
GO

-- #70  IntelliSense  ok, 1 set
-- in daffydb
SELECT
SCHEMA_NAME(tbl.schema_id) AS [Schema],
tbl.name AS [Name],
tbl.object_id AS [ID]
FROM
sys.tables AS tbl
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #71  IntelliSense  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'V'
SELECT
SCHEMA_NAME(v.schema_id) AS [Schema],
v.name AS [Name],
v.object_id AS [ID]
FROM
sys.all_views AS v
WHERE
(v.type = @_msparam_0)
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #72  IntelliSense  gap: Catalog Error: Table with name all_parameters does not exist!
-- in daffydb
SELECT
SCHEMA_NAME(obj.schema_id) AS [Schema],
obj.name AS [Name],
obj.object_id AS [ID],
usrt.name AS [DataType]
FROM
sys.objects AS obj
LEFT OUTER JOIN sys.all_parameters AS ret_param ON ret_param.object_id = obj.object_id and ret_param.is_output = 1
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = ret_param.user_type_id
WHERE
(obj.type=N'AF')
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #73  IntelliSense  gap: Catalog Error: Table with name sql_modules does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'1'
SELECT
SCHEMA_NAME(udf.schema_id) AS [Schema],
udf.name AS [Name],
udf.object_id AS [ID],
(case when 'FN' = udf.type then 1 when 'FS' = udf.type then 1 when 'IF' = udf.type then 3 when 'TF' = udf.type then 2 when 'FT' = udf.type then 2 else 0 end) AS [FunctionType],
CASE WHEN udf.type IN ('FN','IF','TF') THEN 1 WHEN udf.type IN ('FS','FT') THEN 2 ELSE 1 END AS [ImplementationType],
CAST(
 case 
    when udf.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = udf.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit) AS [IsSystemObject],
CAST(CASE WHEN ISNULL(smudf.definition, ssmudf.definition) IS NULL THEN 1 ELSE 0 END AS bit) AS [IsEncrypted],
CAST(ISNULL(OBJECTPROPERTYEX(udf.object_id, N'IsSchemaBound'),0) AS bit) AS [IsSchemaBound],
usrt.name AS [DataType]
FROM
sys.all_objects AS udf
LEFT OUTER JOIN sys.sql_modules AS smudf ON smudf.object_id = udf.object_id
LEFT OUTER JOIN sys.system_sql_modules AS ssmudf ON ssmudf.object_id = udf.object_id
LEFT OUTER JOIN sys.all_parameters AS ret_param ON ret_param.object_id = udf.object_id and ret_param.is_output = @_msparam_0
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = ret_param.user_type_id
WHERE
(udf.type in ('TF', 'FN', 'IF', 'FS', 'FT'))
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #74  IntelliSense  gap: Catalog Error: Table with name sql_modules does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'P'
-- param @_msparam_1 nvarchar(4000) = N'RF'
-- param @_msparam_2 nvarchar(4000) = N'PC'
SELECT
SCHEMA_NAME(sp.schema_id) AS [Schema],
sp.name AS [Name],
sp.object_id AS [ID],
CAST(
 case 
    when sp.is_ms_shipped = 1 then 1
    when exists (
        select 
            1
        from 
            sys.extended_properties 
        where 
            major_id = sp.object_id and 
            minor_id = 0 and 
            class = 1 and 
            name = N'microsoft_database_tools_support') 
        then 1
    else 0
end          
             AS bit) AS [IsSystemObject],
CASE WHEN sp.type = N'P' THEN 1 WHEN sp.type = N'PC' THEN 2 ELSE 1 END AS [ImplementationType],
CAST(CASE WHEN ISNULL(smsp.definition, ssmsp.definition) IS NULL THEN 1 ELSE 0 END AS bit) AS [IsEncrypted]
FROM
sys.all_objects AS sp
LEFT OUTER JOIN sys.sql_modules AS smsp ON smsp.object_id = sp.object_id
LEFT OUTER JOIN sys.system_sql_modules AS ssmsp ON ssmsp.object_id = sp.object_id
WHERE
(sp.type = @_msparam_0 OR sp.type = @_msparam_1 OR sp.type=@_msparam_2)
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #75  IntelliSense  gap: Catalog Error: Table with name synonyms does not exist!
-- in daffydb
SELECT
SCHEMA_NAME(s.schema_id) AS [Schema],
s.name AS [Name],
s.object_id AS [ID],
N'' AS [BaseDatabase],
N'' AS [BaseObject],
N'' AS [BaseSchema],
N'' AS [BaseServer],

			CASE OBJECTPROPERTYEX(s.object_id,'BaseType') 
				WHEN N'U' THEN 1 
				WHEN N'V' THEN 2 
				WHEN N'P' THEN 3 
				WHEN N'FN' THEN 4 
				WHEN N'TF' THEN 5 
				WHEN N'IF' THEN 6 
				WHEN N'X' THEN 7 
				WHEN N'RF' THEN 8 
				WHEN N'PC' THEN 9 
				WHEN N'FS' THEN 10 
				WHEN N'FT' THEN 11 
				WHEN N'AF' THEN 12 ELSE 0 END
		 AS [BaseType],
s.base_object_name AS [BaseObjectName]
FROM
sys.synonyms AS s
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #76  IntelliSense  ok, 1 set
-- in daffydb
SELECT
SCHEMA_NAME(xproc.schema_id) AS [Schema],
xproc.name AS [Name],
xproc.object_id AS [ID],
CAST(
                xproc.is_ms_shipped
            AS bit) AS [IsSystemObject]
FROM
sys.all_objects AS xproc
WHERE
(xproc.type='X')
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #77  IntelliSense  ok, 1 set
-- in daffydb
SELECT
sst.name AS [Schema],
st.name AS [Name]
FROM
sys.types AS st
INNER JOIN sys.schemas AS sst ON sst.schema_id = st.schema_id
WHERE
(st.schema_id!=4 and st.system_type_id!=240 and st.user_type_id != st.system_type_id and st.is_table_type != 1)
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #78  IntelliSense  gap: Catalog Error: Table with name table_types does not exist!
-- in daffydb
SELECT
SCHEMA_NAME(tt.schema_id) AS [Schema],
tt.name AS [Name]
FROM
sys.table_types AS tt
INNER JOIN sys.schemas AS stt ON stt.schema_id = tt.schema_id
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #79  IntelliSense  gap: Catalog Error: Table with name assembly_types does not exist!
-- in daffydb
SELECT
satypes.name AS [Schema],
atypes.name AS [Name]
FROM
sys.assembly_types AS atypes
INNER JOIN sys.assemblies AS asmbl ON (asmbl.assembly_id = atypes.assembly_id) and (atypes.is_user_defined = 1)
INNER JOIN sys.schemas AS satypes ON satypes.schema_id = atypes.schema_id
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #80  IntelliSense  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customers'
-- param @_msparam_1 nvarchar(4000) = N'dbo'
SELECT
clmns.column_id AS [ID],
clmns.name AS [Name],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
clmns.is_nullable AS [Nullable],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_identity AS [Identity],
usrt.name AS [DataType],
clmns.is_computed AS [Computed]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
WHERE
(tbl.name=@_msparam_0 and SCHEMA_NAME(tbl.schema_id)=@_msparam_1)
ORDER BY
[ID] ASC
GO

-- #81  IntelliSense  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customer_id'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
CAST(ISNULL(ic.seed_value,0) AS bigint) AS [IdentitySeed]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #82  IntelliSense  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customer_id'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
CAST(ISNULL(ic.increment_value,0) AS bigint) AS [IdentityIncrement]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #83  IntelliSense  gap: Catalog Error: Table with name column_encryption_keys does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customer_id'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name],
clmns.column_id AS [ID],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_ansi_padded AS [AnsiPaddingStatus],
CAST(clmns.is_rowguidcol AS bit) AS [RowGuidCol],
CAST(ISNULL(cc.is_persisted, 0) AS bit) AS [IsPersisted],
ISNULL(clmns.collation_name, N'') AS [Collation],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
clmns.is_identity AS [Identity],
CAST(ISNULL(ic.seed_value,0) AS numeric(38)) AS [IdentitySeedAsDecimal],
CAST(ISNULL(ic.increment_value,0) AS numeric(38)) AS [IdentityIncrementAsDecimal],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else d.name end) AS [Default],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else schema_name(d.schema_id) end) AS [DefaultSchema],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
(case when clmns.rule_object_id = 0 then N'' else r.name end) AS [Rule],
(case when clmns.rule_object_id = 0 then N'' else schema_name(r.schema_id) end) AS [RuleSchema],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsDeterministic'),0) AS bit) AS [IsDeterministic],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsPrecise'),0) AS bit) AS [IsPrecise],
ISNULL(ic.is_not_for_replication, 0) AS [NotForReplication],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsFulltextIndexed') AS bit) AS [IsFullTextIndexed],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'StatisticalSemantics') AS int) AS [StatisticalSemantics],
CAST(clmns.encryption_type AS int) AS [EncryptionType],
clmns.encryption_algorithm_name AS [EncryptionAlgorithm],
clmns.column_encryption_key_id AS [ColumnEncryptionKeyID],
ceks.name AS [ColumnEncryptionKeyName],
CAST(clmns.is_filestream AS bit) AS [IsFileStream],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
usrt.name AS [DataType],
s1clmns.name AS [DataTypeSchema],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CASE WHEN usrt.is_table_type = 1 THEN N'structured' ELSE N'' END AS [UserType],
clmns.generated_always_type AS [GeneratedAlwaysType],
CAST(clmns.is_hidden AS bit) AS [IsHidden],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn],
CAST(clmns.is_masked AS bit) AS [IsMasked],
CAST(
        CASE
        WHEN [sc].[label] IS NOT NULL or [sc].[label_id] IS NOT NULL or [sc].[information_type] IS NOT NULL or [sc].[information_type_id] IS NOT NULL or [sc].[rank] IS NOT NULL THEN 1
        ELSE 0
        END
       AS bit) AS [IsClassified],
ISNULL(clmns.graph_type, 0) AS [GraphType]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.computed_columns AS cc ON cc.object_id = clmns.object_id and cc.column_id = clmns.column_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
LEFT OUTER JOIN sys.objects AS d ON d.object_id = clmns.default_object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.objects AS r ON r.object_id = clmns.rule_object_id
LEFT OUTER JOIN sys.column_encryption_keys AS ceks ON (ceks.column_encryption_key_id = clmns.column_encryption_key_id)
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.schemas AS s1clmns ON s1clmns.schema_id = usrt.schema_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
LEFT OUTER JOIN sys.sensitivity_classifications AS sc ON sc.major_id = clmns.object_id and sc.minor_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #84  IntelliSense  gap: Catalog Error: Table with name column_encryption_keys does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'name'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name],
clmns.column_id AS [ID],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_ansi_padded AS [AnsiPaddingStatus],
CAST(clmns.is_rowguidcol AS bit) AS [RowGuidCol],
CAST(ISNULL(cc.is_persisted, 0) AS bit) AS [IsPersisted],
ISNULL(clmns.collation_name, N'') AS [Collation],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
clmns.is_identity AS [Identity],
CAST(ISNULL(ic.seed_value,0) AS numeric(38)) AS [IdentitySeedAsDecimal],
CAST(ISNULL(ic.increment_value,0) AS numeric(38)) AS [IdentityIncrementAsDecimal],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else d.name end) AS [Default],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else schema_name(d.schema_id) end) AS [DefaultSchema],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
(case when clmns.rule_object_id = 0 then N'' else r.name end) AS [Rule],
(case when clmns.rule_object_id = 0 then N'' else schema_name(r.schema_id) end) AS [RuleSchema],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsDeterministic'),0) AS bit) AS [IsDeterministic],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsPrecise'),0) AS bit) AS [IsPrecise],
ISNULL(ic.is_not_for_replication, 0) AS [NotForReplication],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsFulltextIndexed') AS bit) AS [IsFullTextIndexed],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'StatisticalSemantics') AS int) AS [StatisticalSemantics],
CAST(clmns.encryption_type AS int) AS [EncryptionType],
clmns.encryption_algorithm_name AS [EncryptionAlgorithm],
clmns.column_encryption_key_id AS [ColumnEncryptionKeyID],
ceks.name AS [ColumnEncryptionKeyName],
CAST(clmns.is_filestream AS bit) AS [IsFileStream],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
usrt.name AS [DataType],
s1clmns.name AS [DataTypeSchema],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CASE WHEN usrt.is_table_type = 1 THEN N'structured' ELSE N'' END AS [UserType],
clmns.generated_always_type AS [GeneratedAlwaysType],
CAST(clmns.is_hidden AS bit) AS [IsHidden],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn],
CAST(clmns.is_masked AS bit) AS [IsMasked],
CAST(
        CASE
        WHEN [sc].[label] IS NOT NULL or [sc].[label_id] IS NOT NULL or [sc].[information_type] IS NOT NULL or [sc].[information_type_id] IS NOT NULL or [sc].[rank] IS NOT NULL THEN 1
        ELSE 0
        END
       AS bit) AS [IsClassified],
ISNULL(clmns.graph_type, 0) AS [GraphType]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.computed_columns AS cc ON cc.object_id = clmns.object_id and cc.column_id = clmns.column_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
LEFT OUTER JOIN sys.objects AS d ON d.object_id = clmns.default_object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.objects AS r ON r.object_id = clmns.rule_object_id
LEFT OUTER JOIN sys.column_encryption_keys AS ceks ON (ceks.column_encryption_key_id = clmns.column_encryption_key_id)
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.schemas AS s1clmns ON s1clmns.schema_id = usrt.schema_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
LEFT OUTER JOIN sys.sensitivity_classifications AS sc ON sc.major_id = clmns.object_id and sc.minor_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #85  IntelliSense  gap: Catalog Error: Table with name column_encryption_keys does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'city'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name],
clmns.column_id AS [ID],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_ansi_padded AS [AnsiPaddingStatus],
CAST(clmns.is_rowguidcol AS bit) AS [RowGuidCol],
CAST(ISNULL(cc.is_persisted, 0) AS bit) AS [IsPersisted],
ISNULL(clmns.collation_name, N'') AS [Collation],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
clmns.is_identity AS [Identity],
CAST(ISNULL(ic.seed_value,0) AS numeric(38)) AS [IdentitySeedAsDecimal],
CAST(ISNULL(ic.increment_value,0) AS numeric(38)) AS [IdentityIncrementAsDecimal],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else d.name end) AS [Default],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else schema_name(d.schema_id) end) AS [DefaultSchema],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
(case when clmns.rule_object_id = 0 then N'' else r.name end) AS [Rule],
(case when clmns.rule_object_id = 0 then N'' else schema_name(r.schema_id) end) AS [RuleSchema],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsDeterministic'),0) AS bit) AS [IsDeterministic],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsPrecise'),0) AS bit) AS [IsPrecise],
ISNULL(ic.is_not_for_replication, 0) AS [NotForReplication],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsFulltextIndexed') AS bit) AS [IsFullTextIndexed],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'StatisticalSemantics') AS int) AS [StatisticalSemantics],
CAST(clmns.encryption_type AS int) AS [EncryptionType],
clmns.encryption_algorithm_name AS [EncryptionAlgorithm],
clmns.column_encryption_key_id AS [ColumnEncryptionKeyID],
ceks.name AS [ColumnEncryptionKeyName],
CAST(clmns.is_filestream AS bit) AS [IsFileStream],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
usrt.name AS [DataType],
s1clmns.name AS [DataTypeSchema],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CASE WHEN usrt.is_table_type = 1 THEN N'structured' ELSE N'' END AS [UserType],
clmns.generated_always_type AS [GeneratedAlwaysType],
CAST(clmns.is_hidden AS bit) AS [IsHidden],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn],
CAST(clmns.is_masked AS bit) AS [IsMasked],
CAST(
        CASE
        WHEN [sc].[label] IS NOT NULL or [sc].[label_id] IS NOT NULL or [sc].[information_type] IS NOT NULL or [sc].[information_type_id] IS NOT NULL or [sc].[rank] IS NOT NULL THEN 1
        ELSE 0
        END
       AS bit) AS [IsClassified],
ISNULL(clmns.graph_type, 0) AS [GraphType]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.computed_columns AS cc ON cc.object_id = clmns.object_id and cc.column_id = clmns.column_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
LEFT OUTER JOIN sys.objects AS d ON d.object_id = clmns.default_object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.objects AS r ON r.object_id = clmns.rule_object_id
LEFT OUTER JOIN sys.column_encryption_keys AS ceks ON (ceks.column_encryption_key_id = clmns.column_encryption_key_id)
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.schemas AS s1clmns ON s1clmns.schema_id = usrt.schema_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
LEFT OUTER JOIN sys.sensitivity_classifications AS sc ON sc.major_id = clmns.object_id and sc.minor_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #86  IntelliSense  gap: Catalog Error: Table with name column_encryption_keys does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'is_active'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name],
clmns.column_id AS [ID],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_ansi_padded AS [AnsiPaddingStatus],
CAST(clmns.is_rowguidcol AS bit) AS [RowGuidCol],
CAST(ISNULL(cc.is_persisted, 0) AS bit) AS [IsPersisted],
ISNULL(clmns.collation_name, N'') AS [Collation],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
clmns.is_identity AS [Identity],
CAST(ISNULL(ic.seed_value,0) AS numeric(38)) AS [IdentitySeedAsDecimal],
CAST(ISNULL(ic.increment_value,0) AS numeric(38)) AS [IdentityIncrementAsDecimal],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else d.name end) AS [Default],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else schema_name(d.schema_id) end) AS [DefaultSchema],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
(case when clmns.rule_object_id = 0 then N'' else r.name end) AS [Rule],
(case when clmns.rule_object_id = 0 then N'' else schema_name(r.schema_id) end) AS [RuleSchema],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsDeterministic'),0) AS bit) AS [IsDeterministic],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsPrecise'),0) AS bit) AS [IsPrecise],
ISNULL(ic.is_not_for_replication, 0) AS [NotForReplication],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsFulltextIndexed') AS bit) AS [IsFullTextIndexed],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'StatisticalSemantics') AS int) AS [StatisticalSemantics],
CAST(clmns.encryption_type AS int) AS [EncryptionType],
clmns.encryption_algorithm_name AS [EncryptionAlgorithm],
clmns.column_encryption_key_id AS [ColumnEncryptionKeyID],
ceks.name AS [ColumnEncryptionKeyName],
CAST(clmns.is_filestream AS bit) AS [IsFileStream],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
usrt.name AS [DataType],
s1clmns.name AS [DataTypeSchema],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CASE WHEN usrt.is_table_type = 1 THEN N'structured' ELSE N'' END AS [UserType],
clmns.generated_always_type AS [GeneratedAlwaysType],
CAST(clmns.is_hidden AS bit) AS [IsHidden],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn],
CAST(clmns.is_masked AS bit) AS [IsMasked],
CAST(
        CASE
        WHEN [sc].[label] IS NOT NULL or [sc].[label_id] IS NOT NULL or [sc].[information_type] IS NOT NULL or [sc].[information_type_id] IS NOT NULL or [sc].[rank] IS NOT NULL THEN 1
        ELSE 0
        END
       AS bit) AS [IsClassified],
ISNULL(clmns.graph_type, 0) AS [GraphType]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.computed_columns AS cc ON cc.object_id = clmns.object_id and cc.column_id = clmns.column_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
LEFT OUTER JOIN sys.objects AS d ON d.object_id = clmns.default_object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.objects AS r ON r.object_id = clmns.rule_object_id
LEFT OUTER JOIN sys.column_encryption_keys AS ceks ON (ceks.column_encryption_key_id = clmns.column_encryption_key_id)
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.schemas AS s1clmns ON s1clmns.schema_id = usrt.schema_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
LEFT OUTER JOIN sys.sensitivity_classifications AS sc ON sc.major_id = clmns.object_id and sc.minor_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #87  IntelliSense  gap: Catalog Error: Table with name column_encryption_keys does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'signup_date'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name],
clmns.column_id AS [ID],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
clmns.is_ansi_padded AS [AnsiPaddingStatus],
CAST(clmns.is_rowguidcol AS bit) AS [RowGuidCol],
CAST(ISNULL(cc.is_persisted, 0) AS bit) AS [IsPersisted],
ISNULL(clmns.collation_name, N'') AS [Collation],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
clmns.is_identity AS [Identity],
CAST(ISNULL(ic.seed_value,0) AS numeric(38)) AS [IdentitySeedAsDecimal],
CAST(ISNULL(ic.increment_value,0) AS numeric(38)) AS [IdentityIncrementAsDecimal],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else d.name end) AS [Default],
(case when clmns.default_object_id = 0 then N'' when d.parent_object_id > 0 then N'' else schema_name(d.schema_id) end) AS [DefaultSchema],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
(case when clmns.rule_object_id = 0 then N'' else r.name end) AS [Rule],
(case when clmns.rule_object_id = 0 then N'' else schema_name(r.schema_id) end) AS [RuleSchema],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsDeterministic'),0) AS bit) AS [IsDeterministic],
CAST(ISNULL(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsPrecise'),0) AS bit) AS [IsPrecise],
ISNULL(ic.is_not_for_replication, 0) AS [NotForReplication],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'IsFulltextIndexed') AS bit) AS [IsFullTextIndexed],
CAST(COLUMNPROPERTY(clmns.object_id, clmns.name, N'StatisticalSemantics') AS int) AS [StatisticalSemantics],
CAST(clmns.encryption_type AS int) AS [EncryptionType],
clmns.encryption_algorithm_name AS [EncryptionAlgorithm],
clmns.column_encryption_key_id AS [ColumnEncryptionKeyID],
ceks.name AS [ColumnEncryptionKeyName],
CAST(clmns.is_filestream AS bit) AS [IsFileStream],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
usrt.name AS [DataType],
s1clmns.name AS [DataTypeSchema],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CASE WHEN usrt.is_table_type = 1 THEN N'structured' ELSE N'' END AS [UserType],
clmns.generated_always_type AS [GeneratedAlwaysType],
CAST(clmns.is_hidden AS bit) AS [IsHidden],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn],
CAST(clmns.is_masked AS bit) AS [IsMasked],
CAST(
        CASE
        WHEN [sc].[label] IS NOT NULL or [sc].[label_id] IS NOT NULL or [sc].[information_type] IS NOT NULL or [sc].[information_type_id] IS NOT NULL or [sc].[rank] IS NOT NULL THEN 1
        ELSE 0
        END
       AS bit) AS [IsClassified],
ISNULL(clmns.graph_type, 0) AS [GraphType]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.computed_columns AS cc ON cc.object_id = clmns.object_id and cc.column_id = clmns.column_id
LEFT OUTER JOIN sys.identity_columns AS ic ON ic.object_id = clmns.object_id and ic.column_id = clmns.column_id
LEFT OUTER JOIN sys.objects AS d ON d.object_id = clmns.default_object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.objects AS r ON r.object_id = clmns.rule_object_id
LEFT OUTER JOIN sys.column_encryption_keys AS ceks ON (ceks.column_encryption_key_id = clmns.column_encryption_key_id)
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.schemas AS s1clmns ON s1clmns.schema_id = usrt.schema_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
LEFT OUTER JOIN sys.sensitivity_classifications AS sc ON sc.major_id = clmns.object_id and sc.minor_id = clmns.column_id
WHERE
(clmns.name=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
GO

-- #88  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
-- types Name:nvarchar, Urn:nvarchar, InPrimaryKey:bit, IsForeignKey:bit, DataType:nvarchar, SystemType:nvarchar, Length:int, NumericPrecision:int, NumericScale:int, Nullable:bit, Computed:bit, XmlSchemaNamespaceSchema:nvarchar, XmlSchemaNamespace:nvarchar, XmlDocumentConstraint:int, IsSparse:bit, IsColumnSet:bit, ID:int, IsDroppedLedgerColumn:bit
SELECT
clmns.name AS [Name],
'Server[@Name=' + quotename(CAST(
        serverproperty(N'Servername')
       AS sysname),'''') + ']' + '/Database[@Name=' + quotename(db_name(),'''') + ']' + '/Table[@Name=' + quotename(tbl.name,'''') + ' and @Schema=' + quotename(SCHEMA_NAME(tbl.schema_id),'''') + ']' + '/Column[@Name=' + quotename(clmns.name,'''') + ']' AS [Urn],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
usrt.name AS [DataType],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
clmns.is_nullable AS [Nullable],
clmns.is_computed AS [Computed],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CAST(clmns.is_sparse AS bit) AS [IsSparse],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
clmns.column_id AS [ID],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
WHERE
(CAST(clmns.is_dropped_ledger_column AS bit)=@_msparam_0)and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
ORDER BY
[ID] ASC
GO

