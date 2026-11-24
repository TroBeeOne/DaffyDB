-- What Visual Studio Code's SQL Server extension (mssql) sends while it connects, expands Databases,
-- a database, its Tables and one table's Columns, and runs "Select Top 1000 Rows" - captured through
-- DaffyTee from VS Code talking to a real SQL Server, with every database, table, column and user
-- name replaced by the seed data's or a neutral one. Duplicates are removed; order is kept.
--
-- Its Object Explorer is not SSMS's: before it lists what is in a database it loads every property
-- of the database, and before it scripts "Select Top 1000 Rows" every property of the table, from
-- queries SSMS never sends. Either one failing leaves the tree, or the script, empty.
--
-- The format is object-explorer.sql's: each request is a header line - its number, the part of VS
-- Code that sent it, and what DaffyDB is expected to do with it - then the database it was sent
-- from, its parameters, its SQL, then GO. A parameterised request is replayed as the
-- sp_executesql RPC SMO sent, anything else as a batch.
--
--   ok, N sets   must run and return N result sets; anything else is a regression
--   gap: ...     known not to work yet, with the error it gets; it is reported, not failed
--
-- #75, the Views folder, is from DaffyDB's own log of the same VS Code, which a user opened
-- later: the capture never did. It was failing on a ledger column sys.views did not have.
--
-- SmoProbe --replay <this file> --update rewrites the expectations from a run; review the diff.

-- #1  Query window  ok, 1 set
-- in daffydb
SELECT SERVERPROPERTY('EngineEdition'), SERVERPROPERTY('productversion'), SERVERPROPERTY ('productlevel'), SERVERPROPERTY ('edition'), SERVERPROPERTY ('MachineName'), SERVERPROPERTY ('ServerName'), (SELECT CASE WHEN EXISTS (SELECT TOP 1 1 from [sys].[all_columns] WITH (NOLOCK) WHERE name = N'xml_index_type' AND OBJECT_ID(N'sys.xml_indexes') = object_id) THEN 1 ELSE 0 END AS SXI_PRESENT)
GO

-- #2  Query window  ok, 1 set
-- in daffydb
SELECT ISNULL(SESSIONPROPERTY ('ANSI_NULLS'), 0), ISNULL(SESSIONPROPERTY ('QUOTED_IDENTIFIER'), 1)
GO

-- #3  Query window  ok, 1 set
-- in daffydb
SELECT CONVERT(NVARCHAR(36), CONTEXT_INFO())
GO

-- #4  Query window  ok, 0 sets
-- in daffydb
set LOCK_TIMEOUT 5000
GO

-- #5  Query window  ok, 0 sets
-- in daffydb
SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- #6  Query window  ok, 1 set
-- in daffydb
SELECT [host_platform], [host_distribution], [host_release], [host_service_pack_level], [host_sku], [os_language_version] FROM sys.dm_os_host_info
GO

-- #7  Query window  ok, 3 sets
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

-- #8  Query window  ok, 1 set
-- in daffydb
select SERVERPROPERTY(N'servername')
GO

-- #9  Query window  ok, 1 set
-- in daffydb
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
(select Internal_Value from #SVer where Name = N'ProcessorCount') AS [Processors]

        drop table #SVer
GO

-- #10  Query window  ok, 1 set
-- in daffydb
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
(select Internal_Value from #SVer where Name = N'PhysicalMemory') AS [PhysicalMemory]

        drop table #SVer
GO

-- #11  Language service  ok, 1 set
-- in master
SELECT SERVERPROPERTY('EngineEdition'), SERVERPROPERTY('productversion'), SERVERPROPERTY ('productlevel'), SERVERPROPERTY ('edition'), SERVERPROPERTY ('MachineName'), SERVERPROPERTY ('ServerName'), (SELECT CASE WHEN EXISTS (SELECT TOP 1 1 from [sys].[all_columns] WITH (NOLOCK) WHERE name = N'xml_index_type' AND OBJECT_ID(N'sys.xml_indexes') = object_id) THEN 1 ELSE 0 END AS SXI_PRESENT)
GO

-- #12  Language service  ok, 1 set
-- in master
SELECT ISNULL(SESSIONPROPERTY ('ANSI_NULLS'), 0), ISNULL(SESSIONPROPERTY ('QUOTED_IDENTIFIER'), 1)
GO

-- #13  Language service  ok, 1 set
-- in master
SELECT CONVERT(NVARCHAR(36), CONTEXT_INFO())
GO

-- #14  Language service  ok, 0 sets
-- in master
set LOCK_TIMEOUT 5000
GO

-- #15  Language service  ok, 0 sets
-- in master
SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO

-- #16  Language service  ok, 1 set
-- in master
SELECT name FROM sys.databases WHERE state_desc='ONLINE' ORDER BY name ASC
GO

-- #17  Language service  ok, 1 set
-- in daffydb
use [daffydb];
if (db_id() = 1)
begin
-- contained auth is 0 when connected to master
select 0
end
else
begin
-- need dynamic sql so that we compile this query only when we know resource db is available
exec('select case when authenticating_database_id = 1 then 0 else 1 end from
sys.dm_exec_sessions where session_id = @@SPID')
end;use [daffydb]; --resetting the context
-- now in database daffydb
GO

-- #18  Language service  ok, 1 set
-- in daffydb
SELECT
CAST(
        serverproperty(N'Servername')
       AS sysname) AS [Name]
GO

-- #19  Language service  ok, 1 set
-- in daffydb
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
      


        declare @SmoRoot nvarchar(512)
        exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath', @SmoRoot OUTPUT
      


SELECT
CAST(case when 'a' <> 'A' then 1 else 0 end AS bit) AS [IsCaseSensitive],
@@MAX_PRECISION AS [MaxPrecision],
@ErrorLogPath AS [ErrorLogPath],
@SmoRoot AS [RootDirectory],
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

-- #20  Language service  ok, 1 set
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

-- #21  Language service  ok, 1 set
-- in daffydb
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

-- #22  Language service  ok, 1 set
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

-- #23  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],

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
       AS [Status],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
((CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=@_msparam_0))
ORDER BY
[Name] ASC
GO

-- #24  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'1'
SELECT
dtb.name AS [Name],
CAST(0 AS bit) AS [IsFabricDatabase],
dtb.catalog_collation_type AS [CatalogCollation],
dtb.collation_name AS [Collation],

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
       AS [Status],
CAST(ISNULL(dtb.is_ledger_on, 0) AS bit) AS [IsLedger],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
((CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit)=@_msparam_0) and 
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
      =@_msparam_1)
ORDER BY
[Name] ASC
GO

-- #25  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
            create table #temp_data_retention_property (db_id int not null, is_data_retention_enabled bit)
            if serverproperty('engineedition') = 9
            exec sp_executesql N'insert into #temp_data_retention_property
            select dr_db.database_id, dr_db.is_data_retention_enabled
            from sys.databases dr_db'
        


SELECT
dtb.is_ansi_null_default_on AS [AnsiNullDefault],
dtb.is_ansi_nulls_on AS [AnsiNullsEnabled],
dtb.is_ansi_padding_on AS [AnsiPaddingEnabled],
dtb.is_ansi_warnings_on AS [AnsiWarningsEnabled],
dtb.is_arithabort_on AS [ArithmeticAbortEnabled],
dtb.is_auto_shrink_on AS [AutoShrink],
dtb.is_cursor_close_on_commit_on AS [CloseCursorsOnCommitEnabled],
dtb.is_concat_null_yields_null_on AS [ConcatenateNullYieldsNull],
dtb.is_numeric_roundabort_on AS [NumericRoundAbortEnabled],
dtb.is_quoted_identifier_on AS [QuotedIdentifiersEnabled],
dtb.is_read_only AS [ReadOnly],
dtb.is_recursive_triggers_on AS [RecursiveTriggersEnabled],
dtb.user_access AS [UserAccess],
dtb.is_db_chaining_on AS [DatabaseOwnershipChaining],
dtb.is_auto_update_stats_async_on AS [AutoUpdateStatisticsAsync],
dtb.is_date_correlation_on AS [DateCorrelationOptimization],
dtb.is_trustworthy_on AS [Trustworthy],
dtb.name AS [Name],
dtb.database_id AS [ID],
dtb.create_date AS [CreateDate],
dtb.is_auto_create_stats_on AS [AutoCreateStatisticsEnabled],
dtb.is_auto_update_stats_on AS [AutoUpdateStatisticsEnabled],
dtb.is_parameterization_forced AS [IsParameterizationForced],
dtb.is_read_committed_snapshot_on AS [IsReadCommittedSnapshotOn],
CAST(isnull(dtb.source_database_id, 0) AS bit) AS [IsDatabaseSnapshot],
ISNULL(DB_NAME(dtb.source_database_id), N'') AS [DatabaseSnapshotBaseName],
case when SERVERPROPERTY('EngineEdition') = 8 then SERVERPROPERTY('IsFullTextInstalled') else dtb.is_fulltext_enabled end AS [IsFullTextEnabled],
dtb.service_broker_guid AS [ServiceBrokerGuid],
dtb.snapshot_isolation_state AS [SnapshotIsolationState],
(dtb.is_published*1+dtb.is_subscribed*2+dtb.is_merge_published*4) AS [ReplicationOptions],
dtb.is_local_cursor_default AS [LocalCursorsDefault],
dtb.page_verify_option AS [PageVerify],
dtb.recovery_model AS [RecoveryModel],
dtb.is_auto_close_on AS [AutoClose],
dtb.is_broker_enabled AS [BrokerEnabled],
ISNULL(suser_sname(dtb.owner_sid),'') AS [Owner],
ISNULL(dtb.log_reuse_wait,0) AS [LogReuseWaitStatus],
drs.recovery_fork_guid AS [RecoveryForkGuid],
drs.database_guid AS [DatabaseGuid],
CAST((case when drs.last_log_backup_lsn is not null then 1 else 0 end) AS bit) AS [HasFullBackup],
CAST(case when dtb.name in ('master','model','msdb','tempdb') then 1 else dtb.is_distributor end AS bit) AS [IsSystemObject],
CAST(case when ctb.database_id is null then 0 else 1  end AS bit) AS [ChangeTrackingEnabled],
CAST(ISNULL(ctb.is_auto_cleanup_on,0) AS bit) AS [ChangeTrackingAutoCleanUp],
ISNULL(ctb.retention_period,0) AS [ChangeTrackingRetentionPeriod],
CAST(ISNULL(ctb.retention_period_units,0) AS tinyint) AS [ChangeTrackingRetentionPeriodUnits],
dtb.containment AS [ContainmentType],
dtb.default_language_lcid AS [DefaultLanguageLcid],
dtb.default_language_name AS [DefaultLanguageName],
dtb.default_fulltext_language_lcid AS [DefaultFullTextLanguageLcid],
ISNULL(dtb.default_fulltext_language_name,N'') AS [DefaultFullTextLanguageName],
CAST(dtb.is_nested_triggers_on AS bit) AS [NestedTriggersEnabled],
CAST(dtb.is_transform_noise_words_on AS bit) AS [TransformNoiseWords],
dtb.two_digit_year_cutoff AS [TwoDigitYearCutoff],
dtb.target_recovery_time_in_seconds AS [TargetRecoveryTime],
dtb.delayed_durability AS [DelayedDurability],
dtb.is_auto_create_stats_incremental_on AS [AutoCreateIncrementalStatisticsEnabled],
CAST(ISNULL(dtb.is_ledger_on, 0) AS bit) AS [IsLedger],

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
       AS [Status],
CAST(( case LOWER(convert( nvarchar(128), DATABASEPROPERTYEX(dtb.name, 'Updateability'))) when 'read_write' then 1 else 0 end) AS bit) AS [IsUpdateable],
CAST(dtb.is_encrypted AS bit) AS [EncryptionEnabled],
CAST(dtb.is_honor_broker_priority_on AS bit) AS [HonorBrokerPriority],
CAST(
        case
        when SERVERPROPERTY('EngineEdition') = 6 then cast(1 as bit)
        else cast(0 as bit)
        end
       AS bit) AS [IsSqlDw],
CAST(has_dbaccess(dtb.name) AS bit) AS [IsAccessible],
ISNULL(fsopt.directory_name , N'') AS [FilestreamDirectoryName],
ISNULL(fsopt.non_transacted_access , 0) AS [FilestreamNonTransactedAccess],
CAST(0 AS bit) AS [RemoteDataArchiveEnabled],
0 AS [MaxDop],
NULL AS [MaxDopForSecondary],
0 AS [LegacyCardinalityEstimation],
2 AS [LegacyCardinalityEstimationForSecondary],
0 AS [ParameterSniffing],
2 AS [ParameterSniffingForSecondary],
0 AS [QueryOptimizerHotfixes],
2 AS [QueryOptimizerHotfixesForSecondary],
dtb.catalog_collation_type AS [CatalogCollation],
CAST(ISNULL(DATABASEPROPERTYEX(dtb.name, 'lastgoodcheckdbtime'), 0) AS datetime) AS [LastGoodCheckDbTime],
CAST(ISNULL((select is_data_retention_enabled from #temp_data_retention_property as tdrp where tdrp.db_id = dtb.database_id),0) AS bit) AS [DataRetentionEnabled],
CAST(ISNULL(dtb.is_accelerated_database_recovery_on, 0) AS bit) AS [AcceleratedRecoveryEnabled],
dtb.name AS [DatabaseName2],
dtb.containment AS [ContainmentType2],
dtb.name AS [DatabaseName6]
FROM
master.sys.databases AS dtb
LEFT OUTER JOIN sys.database_recovery_status AS drs ON drs.database_id = dtb.database_id
LEFT OUTER JOIN sys.change_tracking_databases AS ctb ON ctb.database_id = dtb.database_id 
LEFT OUTER JOIN sys.database_filestream_options AS fsopt ON fsopt.database_id = dtb.database_id
WHERE
(dtb.name=@_msparam_0)

            drop table #temp_data_retention_property
        

GO

-- #26  Object Explorer  ok, 1 set
-- in daffydb
select 
case 
    when cfg.configuration_id = 124 -- configuration id for default language
    then (select lcid from sys.syslanguages as sl where sl.langid = cfg.value_in_use) -- getting default language LCID from default language langid
    else cfg.value_in_use
end as value,
case 
    when cfg.configuration_id = 124 -- configuration id for default language
    then (select name collate catalog_default from sys.syslanguages as sl where sl.langid = cfg.value_in_use) -- getting default language name from default language langid
    when cfg.configuration_id = 1126 -- configuration id for default fulltext language
    then ISNULL((select name collate catalog_default from sys.fulltext_languages as fl where fl.lcid = cfg.value_in_use), N'') -- getting default fulltext language name from default fulltext language lcid
    else null
end as name,
cfg.configuration_id as configuration_id
from sys.configurations as cfg
where cfg.configuration_id in (115, 124, 1126, 1127, 1555) 
order by cfg.configuration_id asc
GO

-- #27  Object Explorer  ok, 1 set
-- in daffydb
SELECT CASE WHEN has_dbaccess(N'daffydb') = 1 THEN 'true' ELSE 'false' END
GO

-- #28  Object Explorer  ok, 1 set
-- in daffydb
SELECT
(select value from sys.database_scoped_configurations as dsc where dsc.name = 'MAXDOP') AS [MaxDop],
(select value_for_secondary from sys.database_scoped_configurations as dsc where dsc.name = 'MAXDOP') AS [MaxDopForSecondary],
(select value from sys.database_scoped_configurations as dsc where dsc.name = 'LEGACY_CARDINALITY_ESTIMATION') AS [LegacyCardinalityEstimation],
(select ISNULL(value_for_secondary, 2) from sys.database_scoped_configurations as dsc where dsc.name = 'LEGACY_CARDINALITY_ESTIMATION') AS [LegacyCardinalityEstimationForSecondary],
(select value from sys.database_scoped_configurations as dsc where dsc.name = 'PARAMETER_SNIFFING') AS [ParameterSniffing],
(select ISNULL(value_for_secondary, 2) from sys.database_scoped_configurations as dsc where dsc.name = 'PARAMETER_SNIFFING') AS [ParameterSniffingForSecondary],
(select value from sys.database_scoped_configurations as dsc where dsc.name = 'QUERY_OPTIMIZER_HOTFIXES') AS [QueryOptimizerHotfixes],
(select ISNULL(value_for_secondary, 2) from sys.database_scoped_configurations as dsc where dsc.name = 'QUERY_OPTIMIZER_HOTFIXES') AS [QueryOptimizerHotfixesForSecondary]
GO

-- #29  Object Explorer  ok, 0 sets
-- in daffydb
use [daffydb]
-- now in database daffydb
GO

-- #30  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'2'
-- param @_msparam_3 nvarchar(4000) = N'0'
-- param @_msparam_4 nvarchar(4000) = N'3'
-- param @_msparam_5 nvarchar(4000) = N'2'
-- param @_msparam_6 nvarchar(4000) = N'0'
SELECT
SCHEMA_NAME(tbl.schema_id) AS [Schema],
tbl.name AS [Name],
tbl.object_id AS [ID],
tbl.is_filetable AS [IsFileTable],
CAST(CASE WHEN (tbl.temporal_type = 2) OR (tbl.ledger_type = 2) THEN 1 ELSE 0 END AS bit) AS [IsSystemVersioned],
tbl.temporal_type AS [TemporalType],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
CAST(tbl.is_external AS bit) AS [IsExternal],
CAST(tbl.is_edge AS bit) AS [IsEdge],
CAST(tbl.is_node AS bit) AS [IsNode]
FROM
sys.tables AS tbl
WHERE
((CAST(
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
             AS bit)=@_msparam_0) and (tbl.temporal_type=@_msparam_1 or tbl.temporal_type=@_msparam_2) and (CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_3 or CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_4 or CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_5) and (CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit)=@_msparam_6))
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #31  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'1'
-- param @_msparam_1 nvarchar(4000) = N'1'
SELECT
SCHEMA_NAME(tbl.schema_id) AS [Schema],
tbl.name AS [Name],
tbl.object_id AS [ID],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
tbl.temporal_type AS [TemporalType]
FROM
sys.tables AS tbl
WHERE
(((tbl.temporal_type=@_msparam_0) or (CAST(ISNULL(tbl.ledger_type, 0) AS int)=@_msparam_1)))
ORDER BY
[Schema] ASC,[Name] ASC
GO

-- #32  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
SELECT
clmns.column_id AS [ID],
clmns.name AS [Name],
ISNULL(dc.Name, N'') AS [DefaultConstraintName],
clmns.is_computed AS [Computed],
CAST(clmns.is_column_set AS bit) AS [IsColumnSet],
clmns.is_nullable AS [Nullable],
usrt.name AS [DataType],
CAST(ISNULL(cik.index_column_id, 0) AS bit) AS [InPrimaryKey],
CAST(ISNULL((select TOP 1 1 from sys.foreign_key_columns AS colfk where colfk.parent_column_id = clmns.column_id and colfk.parent_object_id = clmns.object_id), 0) AS bit) AS [IsForeignKey],
ISNULL(baset.name, N'') AS [SystemType],
CAST(CASE WHEN baset.name IN (N'nchar', N'nvarchar') AND clmns.max_length <> -1 THEN clmns.max_length/2 ELSE clmns.max_length END AS int) AS [Length],
CAST(clmns.precision AS int) AS [NumericPrecision],
CAST(clmns.scale AS int) AS [NumericScale],
ISNULL(s2clmns.name, N'') AS [XmlSchemaNamespaceSchema],
ISNULL(xscclmns.name, N'') AS [XmlSchemaNamespace],
ISNULL( (case clmns.is_xml_document when 1 then 2 else 1 end), 0) AS [XmlDocumentConstraint],
CAST(clmns.is_dropped_ledger_column AS bit) AS [IsDroppedLedgerColumn]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
LEFT OUTER JOIN sys.default_constraints as dc ON clmns.default_object_id = dc.object_id
LEFT OUTER JOIN sys.types AS usrt ON usrt.user_type_id = clmns.user_type_id
LEFT OUTER JOIN sys.indexes AS ik ON ik.object_id = clmns.object_id and 1=ik.is_primary_key
LEFT OUTER JOIN sys.index_columns AS cik ON cik.index_id = ik.index_id and cik.column_id = clmns.column_id and cik.object_id = clmns.object_id and 0 = cik.is_included_column
LEFT OUTER JOIN sys.types AS baset ON (baset.user_type_id = clmns.system_type_id and baset.user_type_id = baset.system_type_id) or ((baset.system_type_id = clmns.system_type_id) and (baset.user_type_id = clmns.user_type_id) and (baset.is_user_defined = 0) and (baset.is_assembly_type = 1)) 
LEFT OUTER JOIN sys.xml_schema_collections AS xscclmns ON xscclmns.xml_collection_id = clmns.xml_collection_id
LEFT OUTER JOIN sys.schemas AS s2clmns ON s2clmns.schema_id = xscclmns.schema_id
WHERE
((CAST(clmns.is_dropped_ledger_column AS bit)=@_msparam_0))and((tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2))
ORDER BY
[ID] ASC
GO

-- #33  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
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

-- #34  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'0'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
-- param @_msparam_3 nvarchar(4000) = N'0'
-- param @_msparam_4 nvarchar(4000) = N'2'
-- param @_msparam_5 nvarchar(4000) = N'5'
-- param @_msparam_6 nvarchar(4000) = N'8'
-- param @_msparam_7 nvarchar(4000) = N'customers'
-- param @_msparam_8 nvarchar(4000) = N'dbo'
SELECT
clmns.name AS [Name]
FROM
sys.tables AS tbl
INNER JOIN sys.all_columns AS clmns ON clmns.object_id=tbl.object_id
WHERE
(CAST(clmns.is_sparse AS bit)=@_msparam_0 and clmns.generated_always_type=@_msparam_1 and CAST(clmns.is_dropped_ledger_column AS bit)=@_msparam_2 and (ISNULL(clmns.graph_type, 0)=@_msparam_3 or ISNULL(clmns.graph_type, 0)=@_msparam_4 or ISNULL(clmns.graph_type, 0)=@_msparam_5 or ISNULL(clmns.graph_type, 0)=@_msparam_6))and((tbl.name=@_msparam_7 and SCHEMA_NAME(tbl.schema_id)=@_msparam_8))
ORDER BY
clmns.column_id ASC
GO

-- #35  Language service  ok, 1 set
-- in daffydb
SELECT SERVERPROPERTY('IsXTPSupported')
GO

-- #36  Language service  ok, 1 set
-- in daffydb
SELECT
CAST(ISNULL(SERVERPROPERTY(N'IsXTPSupported'), 0) AS bit) AS [IsXTPSupported]
GO

-- #37  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customers'
-- param @_msparam_1 nvarchar(4000) = N'dbo'
SELECT
SCHEMA_NAME(tbl.schema_id) AS [Schema],
tbl.name AS [Name],
tbl.object_id AS [ID]
FROM
sys.tables AS tbl
WHERE
(tbl.name=@_msparam_0 and SCHEMA_NAME(tbl.schema_id)=@_msparam_1)
GO

-- #38  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'customers'
-- param @_msparam_1 nvarchar(4000) = N'dbo'
SELECT
SCHEMA_NAME(tbl.schema_id) AS [Schema],
tbl.name AS [Name]
FROM
sys.tables AS tbl
WHERE
(tbl.name=@_msparam_0 and SCHEMA_NAME(tbl.schema_id)=@_msparam_1)
GO

-- #39  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'2'
-- param @_msparam_1 nvarchar(4000) = N'customers'
-- param @_msparam_2 nvarchar(4000) = N'dbo'
            create table #change_tracking_tables
            (object_id int not null primary key, is_track_columns_updated_on bit null)
            insert into #change_tracking_tables
            select t.object_id, c.is_track_columns_updated_on
            from sys.tables t
            inner join sys.change_tracking_tables as c on c.object_id = t.object_id
        


            CREATE TABLE #file_tables
            (object_id int not null primary key, is_enabled bit null, directory_name nvarchar(256) null, filename_collation_name nvarchar(129) null)
            insert into #file_tables
            select t.object_id, f.is_enabled, f.directory_name, f.filename_collation_name
            from sys.tables as t
            inner join sys.filetables as f on t.object_id = f.object_id
        


SELECT
tbl.name AS [Name],
tbl.object_id AS [ID],
tbl.create_date AS [CreateDate],
tbl.modify_date AS [DateLastModified],
ISNULL(stbl.name, N'') AS [Owner],
CAST(case when tbl.principal_id is null then 1 else 0 end AS bit) AS [IsSchemaOwned],
SCHEMA_NAME(tbl.schema_id) AS [Schema],
CAST(
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
             AS bit) AS [IsSystemObject],
CAST(OBJECTPROPERTY(tbl.object_id, N'HasAfterTrigger') AS bit) AS [HasAfterTrigger],
CAST(OBJECTPROPERTY(tbl.object_id, N'HasInsertTrigger') AS bit) AS [HasInsertTrigger],
CAST(OBJECTPROPERTY(tbl.object_id, N'HasDeleteTrigger') AS bit) AS [HasDeleteTrigger],
CAST(OBJECTPROPERTY(tbl.object_id, N'HasInsteadOfTrigger') AS bit) AS [HasInsteadOfTrigger],
CAST(OBJECTPROPERTY(tbl.object_id, N'HasUpdateTrigger') AS bit) AS [HasUpdateTrigger],
CAST(OBJECTPROPERTY(tbl.object_id, N'IsIndexed') AS bit) AS [HasIndex],
CAST(OBJECTPROPERTY(tbl.object_id, N'IsIndexable') AS bit) AS [IsIndexable],
CAST(CASE idx.index_id WHEN 1 THEN 1 ELSE 0 END AS bit) AS [HasClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type > 1 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredIndex],
CAST(case idx.index_id when 1 then case when (idx.is_primary_key + 2*idx.is_unique_constraint = 1) then 1 else 0 end else 0 end AS bit) AS [HasPrimaryClusteredIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 6 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasNonClusteredColumnStoreIndex],
CAST(ISNULL((select top 1 1 from sys.indexes ind where ind.object_id = tbl.object_id and ind.type = 3 and ind.is_hypothetical = 0 ), 0) AS bit) AS [HasXmlIndex],
CAST(CASE idx.type WHEN 0 THEN 1 ELSE 0 END AS bit) AS [HasHeapIndex],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name = N'xml'), 0) AS bit) AS [HasXmlData],
CAST(ISNULL((select top 1 1 from sys.all_columns as clmns join sys.types as usrt on usrt.user_type_id = clmns.user_type_id where clmns.object_id = tbl.object_id and usrt.name in (N'geometry', N'geography')), 0) AS bit) AS [HasSpatialData],
tbl.uses_ansi_nulls AS [AnsiNullsStatus],
CAST(ISNULL(OBJECTPROPERTY(tbl.object_id,N'IsQuotedIdentOn'),1) AS bit) AS [QuotedIdentifierStatus],
CAST(0 AS bit) AS [FakeSystemTable],
ISNULL(dstext.name,N'') AS [TextFileGroup],
CAST(tbl.is_memory_optimized AS bit) AS [IsMemoryOptimized],
case when (tbl.durability=1) then 0 else 1 end AS [Durability],
tbl.is_replicated AS [Replicated],
tbl.lock_escalation AS [LockEscalation],
CAST(case when ctt.object_id is null then 0 else 1  end AS bit) AS [ChangeTrackingEnabled],
CAST(ISNULL(ctt.is_track_columns_updated_on,0) AS bit) AS [TrackColumnsUpdatedEnabled],
CAST(CASE tbl.ledger_type WHEN 2 THEN 1 WHEN 3 THEN 1 ELSE 0 END AS bit) AS [IsLedger],
CAST(ISNULL(tbl.ledger_type, 0) AS int) AS [LedgerType],
CAST(ISNULL(tbl.is_dropped_ledger_table, 0) AS bit) AS [IsDroppedLedgerTable],
CAST(CASE WHEN (tbl.temporal_type = 2) OR (tbl.ledger_type = 2) THEN 1 ELSE 0 END AS bit) AS [IsSystemVersioned],
tbl.is_filetable AS [IsFileTable],
ISNULL(ft.directory_name,N'') AS [FileTableDirectoryName],
ISNULL(ft.filename_collation_name,N'') AS [FileTableNameColumnCollation],
CAST(ISNULL(ft.is_enabled,0) AS bit) AS [FileTableNamespaceEnabled],
CASE WHEN 'PS'=dsidx.type THEN dsidx.name ELSE N'' END AS [PartitionScheme],
CAST(CASE WHEN 'PS'=dsidx.type THEN 1 ELSE 0 END AS bit) AS [IsPartitioned],
CASE WHEN 'FD'=dstbl.type THEN dstbl.name ELSE N'' END AS [FileStreamFileGroup],
CASE WHEN 'PS'=dstbl.type THEN dstbl.name ELSE N'' END AS [FileStreamPartitionScheme],
CAST(CASE idx.type WHEN 5 THEN 1 ELSE 0 END AS bit) AS [HasClusteredColumnStoreIndex],
CAST(ISNULL(historyTable.name, N'') AS sysname) AS [HistoryTableName],
CAST(ISNULL(SCHEMA_NAME(historyTable.schema_id), N'') AS sysname) AS [HistoryTableSchema],
CAST(ISNULL(historyTable.object_id, 0) AS int) AS [HistoryTableID],
CAST(CASE WHEN periods.start_column_id IS NULL THEN 0 ELSE 1 END AS bit) AS [HasSystemTimePeriod],
CAST(
        ISNULL((SELECT cols.name
        FROM sys.columns cols
        WHERE periods.object_id = tbl.object_id
        AND cols.object_id = tbl.object_id
        AND cols.column_id = periods.start_column_id), N'')
       AS sysname) AS [SystemTimePeriodStartColumn],
CAST(
        ISNULL((SELECT cols.name
        FROM sys.columns cols
        WHERE periods.object_id = tbl.object_id
        AND cols.object_id = tbl.object_id
        AND cols.column_id = periods.end_column_id), N'')
       AS sysname) AS [SystemTimePeriodEndColumn],
tbl.temporal_type AS [TemporalType],
CAST(0 AS bit) AS [RemoteDataArchiveEnabled],
CAST(0 AS tinyint) AS [RemoteDataArchiveDataMigrationState],
CAST('' AS varchar(4000)) AS [RemoteDataArchiveFilterPredicate],
CAST(N'' AS sysname) AS [RemoteTableName],
CAST(0 AS bit) AS [RemoteTableProvisioned],
CAST(tbl.is_external AS bit) AS [IsExternal],
CAST(tbl.is_node AS bit) AS [IsNode],
CAST(tbl.is_edge AS bit) AS [IsEdge]
FROM
sys.tables AS tbl
LEFT OUTER JOIN sys.database_principals AS stbl ON stbl.principal_id = ISNULL(tbl.principal_id, (OBJECTPROPERTY(tbl.object_id, 'OwnerId')))
INNER JOIN sys.indexes AS idx ON 
        idx.object_id = tbl.object_id and (idx.index_id < @_msparam_0  or (tbl.is_memory_optimized = 1 and idx.index_id = (select min(index_id) from sys.indexes where object_id = tbl.object_id)))
      
LEFT OUTER JOIN sys.data_spaces AS dstext  ON tbl.lob_data_space_id = dstext.data_space_id
LEFT OUTER JOIN #change_tracking_tables AS ctt ON ctt.object_id = tbl.object_id 
LEFT OUTER JOIN #file_tables AS ft ON ft.object_id = tbl.object_id
LEFT OUTER JOIN sys.data_spaces AS dsidx ON dsidx.data_space_id = idx.data_space_id
LEFT OUTER JOIN sys.tables AS t ON t.object_id = idx.object_id
LEFT OUTER JOIN sys.data_spaces AS dstbl ON dstbl.data_space_id = t.Filestream_data_space_id and (idx.index_id < 2 or (idx.type = 7 and idx.index_id < 3))
LEFT OUTER JOIN sys.tables as historyTable ON historyTable.object_id = tbl.history_table_id
LEFT OUTER JOIN sys.periods as periods ON periods.object_id = tbl.object_id
WHERE
(tbl.name=@_msparam_1 and SCHEMA_NAME(tbl.schema_id)=@_msparam_2)

            DROP TABLE #change_tracking_tables
        


            DROP TABLE #file_tables
        

GO

-- #40  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'master'
SELECT
dtb.compatibility_level AS [CompatibilityLevel],
dtb.name AS [DatabaseName2]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #41  Language service  ok, 1 set
-- in daffydb
SELECT
(@@microsoftversion / 0x1000000) & 0xff AS [VersionMajor]
GO

-- #42  Query window  ok, 0 sets
-- in daffydb
SET ROWCOUNT 0 SET TEXTSIZE 2147483647 SET NOCOUNT OFF SET CONCAT_NULL_YIELDS_NULL ON SET ARITHABORT ON SET LOCK_TIMEOUT -1 SET QUERY_GOVERNOR_COST_LIMIT -1 SET DEADLOCK_PRIORITY NORMAL SET TRANSACTION ISOLATION LEVEL READ COMMITTED  SET ANSI_NULLS ON SET ANSI_NULL_DFLT_ON ON SET ANSI_PADDING ON SET ANSI_WARNINGS ON SET CURSOR_CLOSE_ON_COMMIT OFF SET IMPLICIT_TRANSACTIONS OFF SET QUOTED_IDENTIFIER ON
GO

-- #43  Language service  ok, 1 set
-- in daffydb
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

-- #44  Query window  ok, 1 set
-- in daffydb
SELECT TOP (1000) [customer_id]
      ,[name]
      ,[city]
      ,[is_active]
      ,[signup_date]
  FROM [daffydb].[dbo].[customers]
GO

-- #45  Language service  gap: Catalog Error: Table with name asymmetric_keys does not exist!
-- in daffydb
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

-- #46  Language service  gap: Catalog Error: Table with name server_triggers does not exist!
-- in daffydb
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

-- #47  Language service  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'daffydb'
SELECT
dtb.containment AS [ContainmentType]
FROM
master.sys.databases AS dtb
WHERE
(dtb.name=@_msparam_0)
GO

-- #48  Language service  ok, 1 set
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

-- #49  Language service  ok, 1 set
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

-- #50  Language service  ok, 1 set
-- in daffydb
SELECT
s.name AS [Name]
FROM
sys.schemas AS s
ORDER BY
[Name] ASC
GO

-- #51  Language service  gap: Catalog Error: Table with name triggers does not exist!
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

-- #52  Language service  gap: Catalog Error: Table with name asymmetric_keys does not exist!
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

-- #53  Language service  gap: Catalog Error: Table with name database_permissions does not exist!
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'CO'
-- param @_msparam_1 nvarchar(4000) = N'guest'
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

-- #54  Language service  gap: Catalog Error: Table with name database_permissions does not exist!
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

-- #55  Language service  ok, 1 set
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

-- #56  Language service  ok, 1 set
-- in daffydb
SELECT
(select schema_name()) AS [DefaultSchema]
GO

-- #57  Language service  ok, 1 set
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

-- #58  Language service  ok, 1 set
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

-- #59  Language service  gap: Catalog Error: Table with name all_parameters does not exist!
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

-- #60  Language service  gap: Catalog Error: Table with name sql_modules does not exist!
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

-- #61  Language service  gap: Catalog Error: Table with name sql_modules does not exist!
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

-- #62  Language service  gap: Catalog Error: Table with name synonyms does not exist!
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

-- #63  Language service  ok, 1 set
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

-- #64  Language service  ok, 1 set
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

-- #65  Language service  gap: Catalog Error: Table with name table_types does not exist!
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

-- #66  Language service  gap: Catalog Error: Table with name assembly_types does not exist!
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

-- #67  Language service  ok, 1 set
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

-- #68  Language service  ok, 1 set
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

-- #69  Language service  ok, 1 set
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

-- #70  Language service  gap: Catalog Error: Table with name column_encryption_keys does not exist!
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

-- #71  Language service  gap: Catalog Error: Table with name column_encryption_keys does not exist!
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

-- #72  Language service  gap: Catalog Error: Table with name column_encryption_keys does not exist!
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

-- #73  Language service  gap: Catalog Error: Table with name column_encryption_keys does not exist!
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

-- #74  Language service  gap: Catalog Error: Table with name column_encryption_keys does not exist!
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

-- #75  Object Explorer  ok, 1 set
-- in daffydb
-- param @_msparam_0 nvarchar(4000) = N'V'
-- param @_msparam_1 nvarchar(4000) = N'0'
-- param @_msparam_2 nvarchar(4000) = N'0'
SELECT SCHEMA_NAME(v.schema_id) AS [Schema], v.name AS [Name], v.object_id AS [ID], CAST(ISNULL(v.ledger_view_type, 0) AS tinyint) AS [LedgerViewType] FROM sys.all_views AS v WHERE (v.type = @_msparam_0)and((CAST( case when v.is_ms_shipped = 1 then 1 when exists ( select 1 from sys.extended_properties where major_id = v.object_id and minor_id = 0 and class = 1 and name = N'microsoft_database_tools_support') then 1 else 0 end AS bit)=@_msparam_1) and (CAST(ISNULL(v.is_dropped_ledger_view, 0) AS bit)=@_msparam_2)) ORDER BY [Schema] ASC,[Name] ASC
GO

