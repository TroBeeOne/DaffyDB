-- What an SSMS release before 22 sends while it connects, captured through DaffyTee from SSMS
-- talking to a real SQL Server 2025, with the server name replaced by a neutral one. Duplicates are
-- removed; order is kept. The format is object-explorer.sql's, which describes it.
--
-- Most of it is what SSMS 22 sends too. What only the older release sends is #2, which asks
-- whether this is the dedicated admin connection and whether SMO is enabled; an error there stops
-- the connection. Both answers are bits, and LocalDB gives False and True.

-- #1  Object Explorer  ok, 3 sets
-- in master
DECLARE @edition sysname;
SET @edition = cast(SERVERPROPERTY(N'EDITION') as sysname);
SELECT case when @edition = N'SQL Azure' then 2 else 1 end as 'DatabaseEngineType',
SERVERPROPERTY('EngineEdition') AS DatabaseEngineEdition,
SERVERPROPERTY('ProductVersion') AS ProductVersion,
@@MICROSOFTVERSION AS MicrosoftVersion;
select host_platform from sys.dm_os_host_info
if @edition = N'SQL Azure'
  select 'TCP' as ConnectionProtocol
else
  exec ('select CONVERT(nvarchar(40),CONNECTIONPROPERTY(''net_transport'')) as ConnectionProtocol')
GO

-- #2  Object Explorer  ok, 1 set
-- in master
-- types AdminConnection:bit, SmoEnabled:bit
IF (@@microsoftversion / 0x01000000) >= 9 AND ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0) = 1
            BEGIN
                SELECT se.is_admin_endpoint AS N'AdminConnection',
                    (SELECT CAST(cfg.value AS bit) AS [ConfigValue]
                            FROM
                            sys.configurations AS cfg WHERE configuration_id=16387)
                    AS N'SmoEnabled'
                FROM  sys.endpoints se
                INNER JOIN sys.dm_exec_connections dmc ON dmc.endpoint_id = se.endpoint_id
                                                                            WHERE
                                                                            dmc.session_id = @@spid
            END
            ELSE
            BEGIN
                IF (@@microsoftversion / 0x01000000) >= 9
                BEGIN
                    SELECT CAST(0 AS BIT) AS N'AdminConnection',
                        (SELECT CAST(cfg.value AS bit) AS [ConfigValue]
                                FROM
                                sys.configurations AS cfg WHERE configuration_id=16387)
                    AS N'SmoEnabled'
                END
                ELSE
                BEGIN
                    SELECT CAST(0 AS BIT) AS N'AdminConnection',
                    CAST(0 AS BIT) AS N'SmoEnabled'
                END
            END
GO

-- #3  Object Explorer  ok, 0 sets
-- in master
SET LOCK_TIMEOUT 10000
GO

-- #4  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(SERVERPROPERTY('EngineEdition') AS int) AS [EngineEdition]
GO

-- #5  Object Explorer  ok, 1 set
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

-- #6  Object Explorer  ok, 1 set
-- in master
select suser_sname()
GO

-- #7  Object Explorer  ok, 1 set
-- in master
select has_dbaccess('msdb')
GO

-- #8  Object Explorer  ok, 1 set
-- in master
select SERVERPROPERTY(N'servername')
GO

-- #9  Object Explorer  ok, 1 set
-- in master
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

-- #10  Object Explorer  ok, 1 set
-- in master
SELECT
CAST(SERVERPROPERTY('IsPolyBaseInstalled') AS bit) AS [IsPolyBaseInstalled]
GO
