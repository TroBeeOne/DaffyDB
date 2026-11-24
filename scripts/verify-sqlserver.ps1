# Checks daffydb.sqlserver, which mounts a SQL Server database's tables as views read live through
# DaffySQL.
#
#   $env:DAFFYDB_SA_PASSWORD = "..."; powershell -File scripts/verify-sqlserver.ps1
#   powershell -File scripts/verify-sqlserver.ps1 -Server "127.0.0.1,14341" -SaPassword "..."
#
# Needs a SQL Server it may make a database on, as sa: by default DaffyDuo's docker test server
# sql1, which its compose file publishes on 127.0.0.1,14341. The sa password comes from
# -SaPassword, or else DAFFYDB_SA_PASSWORD. Without a server, or a password, the checks are reported
# as skipped. It makes a database of its own there, daffydb_verify, and a login that can only read
# it, with a new password each run; both are dropped at the end. Nothing else on the server is
# touched.
#
# Mounts the database - refused without --allow-admin, refused with a bad connection string, a
# wrong password, an unchecked certificate or a name already taken - and reads it: types, a
# SQL Server view, a name that needs brackets, a join with a parquet file, the catalog, sqlcmd and
# SMO. Checks that only the columns a query uses are asked for (a column that cannot be read is
# left alone), that a read cut short by SET ROWCOUNT gives its session straight back, that a read
# blocked by another session's lock fails after Lock Timeout, that a cancel stops one with no
# limit while it is blocked - and DaffyDB answers others straight after - and that the catalog is
# answered without asking SQL Server. Saves a mounted table as parquet, mounts again after a table
# comes and another goes, mounts one table, checks a client cannot reach DaffySQL's connection
# functions, unmounts, and checks the server has no sessions left, and the log no password.
#
# daffydb.run_sqlserver is checked against the server itself: each batch is asked of DaffyDB and,
# as the same login, of SQL Server, and the rows, messages and errors must match. Procedures run
# once however they are called, a session's state does not outlive its call, other clients are
# answered while SQL Server works, and a cancel stops it there. What it returns is kept in temp
# tables, through @into and INSERT ... EXEC, and read back against what SQL Server itself returns; a
# keep that cannot work must fail before the procedure has run.
#
# Conditions sent to SQL Server are checked two ways. The answer must be the one DuckDB gives on
# its own: every query is asked of the mounted table and of a parquet copy of it, saved by
# daffydb.save_parquet, and the two must match. And the log must say what was sent - or that
# nothing was, for each condition SQL Server would compare differently: text under a
# case-sensitive collation or with <>, float, the side of an outer join that comes back NULL. A
# read of a few rows of a big table must bring SQL Server's own count of rows sent down to those.
param([int]$Port = 14530, [string]$Server = "127.0.0.1,14341", [string]$SaPassword = $env:DAFFYDB_SA_PASSWORD)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $condition) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name" }
}

function Ask($port, $sql) {
    dotnet run --project src/TdsProbe --no-build -- --port $port --sql $sql 2>&1 | Out-String
}

# TdsProbe gives a command five seconds; a client whose timeout runs out cancels what it sent, as
# SSMS's Cancel button does.
function Run($port, $sql, $timeout = 120) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:127.0.0.1,$port;Database=daffydb;User ID=v;Password=v;Encrypt=false;Pooling=false"
    $cn.Open()
    try { $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = $timeout; [void]$cmd.ExecuteNonQuery(); "done" }
    catch { "error: $($_.Exception.Message)" }
    finally { $cn.Close() }
}

# The SQL Server itself, as sa: batches separated by GO, the last one's first value returned.
function OnServer($sql, $database = "master") {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:$Server;Database=$database;User ID=sa;Password=$SaPassword;Encrypt=True;TrustServerCertificate=True;Pooling=false;Connect Timeout=10"
    $cn.Open()
    try {
        $result = $null
        foreach ($batch in ($sql -split "(?m)^\s*GO\s*$") | Where-Object { $_.Trim() }) {
            $cmd = $cn.CreateCommand(); $cmd.CommandText = $batch; $cmd.CommandTimeout = 120
            $result = $cmd.ExecuteScalar()
        }
        $result
    }
    finally { $cn.Close() }
}

# A transaction of sa's own, left open so that its locks stay held; closing the connection rolls it back.
function Hold($sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:$Server;Database=daffydb_verify;User ID=sa;Password=$SaPassword;Encrypt=True;TrustServerCertificate=True;Pooling=false"
    $cn.Open()
    $cmd = $cn.CreateCommand(); $cmd.CommandText = "BEGIN TRAN; $sql"; [void]$cmd.ExecuteNonQuery()
    $cn
}

# A query's rows, as the probe prints them: what two answers are compared by.
function Answer($port, $sql) {
    $text = Ask $port $sql
    if ($text -match "SqlException") { return "error: $text" }
    (($text -split "`r?`n") | Where-Object { $_ -match '^\s{7}\w+\s+\S+\s' -and $_ -notmatch '^\s{7}server ' }) -join "`n"
}

# The same query of the SQL Server tables, in ms, and of their parquet copies, in pq.
function Same($sql) {
    $mounted = Answer $Port ($sql -replace '\{t\}', 'ms')
    $copied = Answer $Port ($sql -replace '\{t\}', 'pq')
    $mounted.Length -gt 0 -and $mounted -notmatch '^error' -and $mounted -eq $copied
}

# What SQL Server says it sent for the last SELECT of dbo.big that had a WHERE: rows, not guesses.
$BigRows = @"
SELECT TOP (1) qs.last_rows FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
WHERE t.text LIKE '%FROM \[dbo\].\[big\] WHERE%' ESCAPE '\' AND t.text NOT LIKE '%TOP (0)%'
  AND SUBSTRING(t.text, qs.statement_start_offset / 2 + 1, 6) = 'SELECT'
ORDER BY qs.last_execution_time DESC
"@

# All a client sees of a batch, a line each, in order: each result set's columns, its rows, its
# messages, and every error with what SQL Server said of it.
function Whole($connection, $sql, $timeout = 120, [switch]$Prepare) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $cn = New-Object System.Data.SqlClient.SqlConnection $connection
    $cn.add_InfoMessage([System.Data.SqlClient.SqlInfoMessageEventHandler] { param($s, $e) $lines.Add("info: $($e.Message)") })
    $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = $timeout
        if ($Prepare) { $cmd.Prepare() }
        $r = $cmd.ExecuteReader()
        do {
            if ($r.FieldCount -gt 0) { $lines.Add("set: " + ((0..($r.FieldCount - 1) | ForEach-Object { $r.GetName($_) }) -join "|")) }
            while ($r.Read()) { $lines.Add("row: " + ((0..($r.FieldCount - 1) | ForEach-Object { Shown $r.GetValue($_) }) -join "|")) }
        } while ($r.NextResult())
        $r.Close()
    }
    catch {
        $ex = $_.Exception
        while ($ex -and $ex -isnot [System.Data.SqlClient.SqlException]) { $ex = $ex.InnerException }
        if (-not $ex) { $lines.Add("failed: $($_.Exception.Message)") }
        else { foreach ($e in $ex.Errors) { $lines.Add("error: $($e.Number) $($e.Class) $($e.State) $($e.LineNumber) [$($e.Procedure)] $($e.Message)") } }
    }
    finally { $cn.Close() }
    ,$lines.ToArray()
}

function Shown($value) {
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    if ($value -is [DBNull]) { return "NULL" }
    if ($value -is [byte[]]) { return "0x" + [System.BitConverter]::ToString($value).Replace("-", "") }
    if ($value -is [datetime]) { return $value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", $invariant) }
    if ($value -is [IFormattable]) { return $value.ToString($null, $invariant) }
    "$value"
}

# What daffydb.run_sqlserver sends for this T-SQL, as a literal.
function Literal($sql) { "N'" + $sql.Replace("'", "''") + "'" }
function Through($sql) { "EXEC daffydb.run_sqlserver 'ms', $(Literal $sql)" }
function ThroughDaffy($sql) { Whole $Daffy (Through $sql) }

# The line a count of changed rows adds is DaffyDB's, where a client counts for itself; messages
# all come first here, as DaffyDB sends them, so they are compared apart from the rest.
function Answered($lines) {
    $lines = @($lines | Where-Object { $_ -notmatch '^info: \(\d+ rows? affected\)$' })
    (@($lines | Where-Object { $_ -notmatch '^info:' }) + @($lines | Where-Object { $_ -match '^info:' } | Sort-Object)) -join "`n"
}

# The batch asked of DaffyDB, and of SQL Server as the same login: nothing but the same answer will do.
function AsServer($sql) {
    $through = Answered (ThroughDaffy $sql)
    $direct = Answered (Whole $Reader $sql)
    if ($through -ne $direct) { Write-Output "    through: $through`n    direct:  $direct" }
    $through.Length -gt 0 -and $through -eq $direct
}

# The log is read once the server has stopped; these are what it must, and must not, say.
$logged = [System.Collections.Generic.List[object]]::new()
function Logged($name, $pattern, [switch]$Not) { $logged.Add(@($name, $pattern, [bool]$Not)) }

# The reader login's requests and sessions on the server: what DaffySQL has open there.
$Requests = "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id WHERE s.login_name = N'daffydb_reader'"
$Sessions = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE login_name = N'daffydb_reader'"

$Drop = @"
IF SUSER_ID(N'daffydb_reader') IS NOT NULL
BEGIN
    DECLARE @kill nvarchar(max) = N'';
    SELECT @kill += N'KILL ' + CAST(session_id AS nvarchar(10)) + N';' FROM sys.dm_exec_sessions WHERE login_name = N'daffydb_reader';
    EXEC (@kill);
END
GO
IF DB_ID(N'daffydb_verify') IS NOT NULL
BEGIN
    ALTER DATABASE daffydb_verify SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE daffydb_verify;
END
GO
IF SUSER_ID(N'daffydb_reader') IS NOT NULL DROP LOGIN daffydb_reader;
"@

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

if (-not $SaPassword) {
    Write-Output "skip sqlserver - no sa password: pass -SaPassword, or set DAFFYDB_SA_PASSWORD"
    Write-Output "========================================================"
    Write-Output "sqlserver checks passed (all skipped)"
    Pop-Location; exit 0
}

try { [void](OnServer "SELECT 1") }
catch {
    Write-Output "skip sqlserver - could not reach $Server as sa: $($_.Exception.Message)"
    Write-Output "========================================================"
    Write-Output "sqlserver checks passed (all skipped)"
    Pop-Location; exit 0
}

# --- the database to mount ----------------------------------------------------------------------
# Text beyond ASCII is made from its code points, so this file reads the same in any code page.
$zoe = "Zo$([char]0xEB) $([char]0x2713)"
$readerPassword = "Rd!" + [guid]::NewGuid().ToString("N").Substring(0, 20)
[void](OnServer $Drop)
[void](OnServer @"
CREATE DATABASE daffydb_verify;
GO
CREATE LOGIN daffydb_reader WITH PASSWORD = N'$readerPassword', CHECK_POLICY = OFF, DEFAULT_DATABASE = daffydb_verify;
"@)
[void](OnServer @"
CREATE USER daffydb_reader FOR LOGIN daffydb_reader;
ALTER ROLE db_datareader ADD MEMBER daffydb_reader;
GO
CREATE SCHEMA sales;
GO
CREATE TABLE dbo.orders (order_id int PRIMARY KEY, customer_id int NOT NULL, total money NOT NULL, placed datetime NOT NULL, note nvarchar(100) NULL);
INSERT dbo.orders VALUES (1, 1, 19.99, '2026-01-02T10:00:00', N'$zoe'), (2, 1, 5.00, '2026-01-03T11:30:00', NULL),
                         (3, 2, 120.50, '2026-02-01T09:15:00', N'big'), (4, 3, 7.25, '2026-02-14T18:00:00', NULL),
                         (5, 8, 64.00, '2026-03-01T12:00:00', N'last');
CREATE TABLE dbo.kinds (id int PRIMARY KEY, t tinyint, b bigint, f bit, n decimal(38,10), m money, dt date, tm time(7),
                        d2 datetime2(7), dto datetimeoffset(7), g uniqueidentifier, c char(5), v varchar(20), nv nvarchar(max), vb varbinary(16));
INSERT dbo.kinds VALUES (1, 200, 9223372036854775807, 1, 12345678901234567.0123456789, 922337203685477.5807, '2026-10-21',
                         '23:59:59.1234567', '2026-10-21T08:30:00.1234567', '2026-10-21T08:30:00+02:00', '6F9619FF-8B86-D011-B42D-00C04FC964FF',
                         'ab', 'plain', N'wide', 0x0102FF);
CREATE TABLE dbo.[odd ]]name] ([col one] int);
INSERT dbo.[odd ]]name] VALUES (1), (2);
CREATE TABLE dbo.trap (id int PRIMARY KEY, boom AS (1 / (id - id)));
INSERT dbo.trap (id) VALUES (1), (2), (3);
CREATE TABLE dbo.locked (id int PRIMARY KEY, v int NOT NULL);
INSERT dbo.locked VALUES (1, 10), (2, 20), (3, 30);
CREATE TABLE dbo.big (id int PRIMARY KEY, pad char(200) NOT NULL);
INSERT dbo.big SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'x' FROM sys.all_columns a CROSS JOIN sys.all_columns b;
CREATE TABLE dbo.gone_soon (x int);
CREATE TABLE dbo.people (id int PRIMARY KEY, name nvarchar(40) NULL, code varchar(10) COLLATE Latin1_General_CS_AS NULL,
                         born date NULL, seen datetime NULL, stamp datetime2(3) NULL, score float NULL,
                         amount decimal(10,2) NULL, flag bit NULL, tag uniqueidentifier NULL);
INSERT dbo.people VALUES
    (1, N'Ada',  'ABC', '1815-12-10', '2026-01-01T00:00:00.003', '2026-01-01T00:00:00.123', 1.5, 10.50, 1, '6F9619FF-8B86-D011-B42D-00C04FC964FF'),
    (2, N'ada ', 'abc', '1906-12-09', '2026-01-01T00:00:00.000', '2026-01-02T00:00:00.000', 2.5, 20.00, 0, NULL),
    (3, N'ADA',  NULL,  NULL,         '2026-01-01T00:00:01.000', NULL,                      NULL, -5.25, NULL, '00000000-0000-0000-0000-000000000001'),
    (4, N'Bob',  'Bob', '2000-02-29', NULL,                      '2026-03-01T12:00:00.000', 0.1, 0.00, 1, NULL),
    (5, NULL,    'x',   '2000-01-01', '2025-12-31T23:59:59.997', '2025-12-31T23:59:59.999', 3.0, 99999999.99, 0, NULL);
CREATE TABLE sales.regions (region_id int, region nvarchar(20));
INSERT sales.regions VALUES (1, N'North'), (2, N'South');
GO
CREATE VIEW sales.big_orders AS SELECT order_id, total FROM dbo.orders WHERE total > 50;
"@ "daffydb_verify")

$conn = "Server=tcp:$Server;Database=daffydb_verify;User Id=daffydb_reader;Password=$readerPassword;TrustServerCertificate=True"
$where = "sqlserver://daffydb_reader@$($Server -replace ',', ':')/daffydb_verify"

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-sqlserver-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $work "data") -Force | Out-Null
Copy-Item "data/customers.parquet" (Join-Path $work "data\customers.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "data\orders.parquet")

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
$openLog = Join-Path $work "open.log"
$shutLog = Join-Path $work "shut.log"
$holder = $null

$open = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $openLog `
    -ArgumentList "--port $Port --data-dir `"$work\data`" --allow-admin --no-repl"
$shut = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $shutLog `
    -ArgumentList "--port $($Port + 1) --data-dir `"$work\data`" --no-repl"

try {
    Start-Sleep -Seconds 5

    # --- who may, and what is refused ------------------------------------------------------------
    $refused = Ask ($Port + 1) "EXEC daffydb.sqlserver '$conn'"
    Check "sqlserver is refused without --allow-admin" ($refused -match "SqlException 50301" -and $refused -match "changes what is served")
    Check "help lists it"                     ((Ask ($Port + 1) "EXEC daffydb.help") -match '"daffydb\.sqlserver"')
    Check "help for it shows its usage"       ((Ask ($Port + 1) "EXEC daffydb.help 'sqlserver'") -match 'EXEC daffydb\.sqlserver @connection \[, @table\] \[, @schema\]')

    Check "the database must be named"        ((Ask $Port "EXEC daffydb.sqlserver 'Server=tcp:$Server;User Id=daffydb_reader;Password=x'") -match "SqlException 50302.*name the database")
    Check "text that is not a connection string is refused" ((Ask $Port "EXEC daffydb.sqlserver 'not a connection string'") -match "SqlException 50302.*@connection is a SQL Server connection string")
    Check "an unknown keyword is refused by name" ((Ask $Port "EXEC daffydb.sqlserver '$conn;Colour=blue'") -match "SqlException 50302.*unsupported connection string keyword\(s\): colour")
    Check "so is a Lock Timeout that is not seconds" ((Ask $Port "EXEC daffydb.sqlserver '$conn;Lock Timeout=soon'") -match "SqlException 50302.*Lock Timeout must be a number of seconds")
    $wrong = Ask $Port "EXEC daffydb.sqlserver '$($conn -replace [regex]::Escape($readerPassword), 'wrong')'"
    Check "a wrong password fails the command" ($wrong -match "SqlException 50303" -and $wrong -match [regex]::Escape("could not log in to $where") -and
                                                $wrong -match "Login failed for user 'daffydb_reader'")
    $unchecked = Ask $Port "EXEC daffydb.sqlserver '$($conn -replace ';TrustServerCertificate=True', '')'"
    Check "a certificate is trusted only when the string says so" ($unchecked -match "SqlException 50303" -and $unchecked -match "TrustServerCertificate")
    Check "a name already taken mounts nothing" ((Ask $Port "EXEC daffydb.sqlserver '$conn'") -match "SqlException 50304.*dbo\.orders is taken by mount")
    Check "and none of that mounted anything" ((Ask $Port "EXEC daffydb.tables") -notmatch '"sqlserver"')

    # --- mounting and reading --------------------------------------------------------------------
    $mounted = Ask $Port "EXEC daffydb.sqlserver '$conn', @schema = 'ms'"
    Check "a database mounts, every table and view" ($mounted -match ("info: Mounted 10 tables from " + [regex]::Escape($where) + " as 'ms'"))
    Check "saying what it logged in to, and how" ($mounted -match "info: Logged in to daffydb_reader@.*/daffydb_verify, Microsoft SQL Server \d+.*Mandatory \(TDS 7\.4\), certificate trusted without validation.*lock timeout 30 s")
    $tables = Ask $Port "EXEC daffydb.tables"
    Check "daffydb.tables says where each comes from" ($tables -match 'source\s+String\s+"sqlserver"' -and
                                                       $tables -match ('location\s+String\s+"' + [regex]::Escape("$where dbo.orders") + '"'))

    $order = Ask $Port "SELECT order_id, total, hex(encode(note)) AS note FROM ms.orders WHERE order_id = 1"
    Check "a row reads, money as decimal, and nvarchar beyond ASCII" ($order -match 'total\s+Decimal\s+19\.99' -and $order -match 'note\s+String\s+"5A6FC3AB20E29C93"')
    Check "every row counts"                  ((Ask $Port "SELECT COUNT(*) AS n FROM ms.big") -match 'n\s+Int64\s+300000')
    $kinds = Ask $Port "SELECT *, hex(vb) AS vb_hex FROM ms.kinds"
    Check "types arrive as they were"         ($kinds -match 't\s+Byte\s+200' -and $kinds -match 'b\s+Int64\s+9223372036854775807' -and
                                               $kinds -match 'n\s+Decimal\s+12345678901234567\.0123456789' -and
                                               $kinds -match 'm\s+Decimal\s+922337203685477\.5807' -and
                                               $kinds -match 'g\s+Guid\s+6f9619ff-8b86-d011-b42d-00c04fc964ff' -and
                                               $kinds -match 'tm\s+TimeSpan\s+23:59:59\.123456' -and
                                               $kinds -match 'nv\s+String\s+"wide"' -and $kinds -match 'vb_hex\s+String\s+"0102FF"')
    Check "a SQL Server view mounts too"      ((Ask $Port "SELECT COUNT(*) AS n FROM ms.big_orders") -match 'n\s+Int64\s+2')
    Check "a name that needs brackets reads"  ((Ask $Port "SELECT SUM([col one]) AS s FROM ms.[odd ]]name]") -match 's\s+\w+\s+3')
    Check "only the columns used are asked for" ((Ask $Port "SELECT COUNT(*) AS n FROM ms.trap") -match 'n\s+Int64\s+3' -and
                                                 (Ask $Port "SELECT MAX(id) AS m FROM ms.trap") -match 'm\s+Int32\s+3')
    Check "and the one that cannot be read is SQL Server's error" ((Ask $Port "SELECT * FROM ms.trap") -match "Msg 8134.*Divide by zero")
    Check "a mounted table joins a parquet file" ((Ask $Port "SELECT COUNT(*) AS n FROM dbo.customers c JOIN ms.orders o ON o.customer_id = c.customer_id") -match 'n\s+Int64\s+5')

    $columns = Ask $Port ("SELECT c.name, t.name AS type FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                          "JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                          "WHERE s.name = 'ms' AND o.name = 'orders' ORDER BY c.column_id")
    Check "the catalog describes it as it is sent" ($columns -match '(?s)"order_id".*"int".*"total".*"decimal".*"placed".*"datetime2".*"note".*"nvarchar"')

    $top = & sqlcmd -S "tcp:127.0.0.1,$Port" -U v -P v -d daffydb -C -h -1 -W -Q "SET NOCOUNT ON; SELECT TOP 10 id FROM ms.big ORDER BY id" 2>&1 | Out-String
    Check "sqlcmd reads it"                   ((($top -split "\s+") | Where-Object { $_ -match '^\d+$' }) -join "," -eq "1,2,3,4,5,6,7,8,9,10")
    # Its stderr is a failure to report, not one to stop at: the script still has to drop what it made.
    & { $ErrorActionPreference = "Continue"; dotnet run --project src/SmoProbe --no-build -- --port $Port *> (Join-Path $work "smo.log") }
    Check "SMO walks the tree with it mounted" ($LASTEXITCODE -eq 0)

    $saved = Ask $Port "EXEC daffydb.save_parquet 'ms.orders', '$work\saved'"
    Check "a mounted table saves as parquet"  ($saved -match "info: Saved ms\.orders to .*saved\\ms\.orders\.parquet: 5 rows")

    $ids = Ask $Port "SELECT OBJECT_ID('dbo.orders') AS d, OBJECT_ID('ms.orders') AS m, OBJECT_ID('orders') AS bare, OBJECT_ID('[ms].[orders]') AS br, OBJECT_ID('nope.orders') AS nope"
    $id = @{}; foreach ($n in "d", "m", "bare", "br", "nope") { if ($ids -match "(?m)^\s+$n\s+\w+\s+(\S+)") { $id[$n] = $Matches[1] } }
    Check "OBJECT_ID tells two schemas' tables apart" ($id.d -match '^\d+$' -and $id.m -match '^\d+$' -and $id.d -ne $id.m -and $id.br -eq $id.m)
    Check "and a name without a schema is dbo's" ($id.bare -eq $id.d -and $id.nope -eq "NULL")

    # --- conditions sent to SQL Server -----------------------------------------------------------
    [void](Ask $Port "EXEC daffydb.save_parquet 'ms.people', '$work\pq'")
    [void](Ask $Port "EXEC daffydb.save_parquet 'ms.orders', '$work\pq'")
    Check "the parquet copies to compare with mount" ((Ask $Port "EXEC daffydb.mount '$work\pq', 'pq'") -match "info: Mounted 2 tables")

    Check "a condition on a column is sent, and answers as DuckDB does" (Same "SELECT id, name FROM {t}.people WHERE id = 3")
    Logged "  and it was sent" "sent to SQL Server, ms\.people: \[id\] = 3$"
    Check "text under a case-insensitive collation" (Same "SELECT id FROM {t}.people WHERE name = 'ada' ORDER BY id")
    Logged "  was sent" "ms\.people: \[name\] = 'ada'$"
    Check "text under a case-sensitive collation" (Same "SELECT id FROM {t}.people WHERE code = 'abc' ORDER BY id")
    Logged "  was not" "\[code\]" -Not
    Check "text compared with <>"             (Same "SELECT id FROM {t}.people WHERE name <> 'ada' ORDER BY id")
    Logged "  was not" "\[name\] <>" -Not
    Check "a datetime, by 1/300ths of a second" (Same "SELECT id FROM {t}.people WHERE seen > '2026-01-01 00:00:00.002' ORDER BY id")
    Logged "  was sent 4 ms wider" "ms\.people: \[seen\] >= CAST\('2025-12-31T23:59:59\.9980000' AS datetime2\(7\)\)$"
    Check "a date range"                      (Same "SELECT id FROM {t}.people WHERE born BETWEEN '1900-01-01' AND '2000-01-01' ORDER BY id")
    Logged "  was sent, as dates nothing misreads" "ms\.people: \[born\] BETWEEN CAST\('19000101' AS date\) AND CAST\('20000101' AS date\)$"
    Check "a datetime2"                       (Same "SELECT id FROM {t}.people WHERE stamp >= '2026-01-02' ORDER BY id")
    Logged "  was sent" "ms\.people: \[stamp\] >= CAST\('2026-01-02T00:00:00\.0000000' AS datetime2\(7\)\)$"
    Check "a float"                           (Same "SELECT id FROM {t}.people WHERE score > 1.0 ORDER BY id")
    Logged "  was not" "\[score\]" -Not
    Check "an OR of decimal and bit"          (Same "SELECT id FROM {t}.people WHERE amount < 0 OR flag = 1 ORDER BY id")
    Logged "  was sent" "ms\.people: \(\[amount\] < 0 OR \[flag\] = 1\)$"
    Check "a uniqueidentifier"                (Same "SELECT id FROM {t}.people WHERE tag = '6f9619ff-8b86-d011-b42d-00c04fc964ff'")
    Logged "  was sent" "ms\.people: \[tag\] = CAST\('6f9619ff-8b86-d011-b42d-00c04fc964ff' AS uniqueidentifier\)$"
    Check "NOT IN and IS NOT NULL"            (Same "SELECT id FROM {t}.people WHERE id NOT IN (1, 2) AND name IS NOT NULL ORDER BY id")
    Logged "  were sent" "ms\.people: \[id\] NOT IN \(1, 2\) AND \[name\] IS NOT NULL$"

    Check "a LEFT JOIN's NULL side keeps its WHERE" (Same "SELECT c.customer_id FROM dbo.customers c LEFT JOIN {t}.orders o ON o.customer_id = c.customer_id WHERE o.order_id IS NULL ORDER BY 1")
    Logged "  in DuckDB" "\[order_id\] IS NULL" -Not
    Check "but its ON goes to the right-hand table" (Same "SELECT c.customer_id, o.order_id FROM dbo.customers c LEFT JOIN {t}.orders o ON o.customer_id = c.customer_id AND o.total > 50 ORDER BY 1, 2")
    Logged "  and was sent" "ms\.orders o: \[total\] > 50$"
    Check "and not to the left-hand one"      (Same "SELECT o.order_id, c.customer_id FROM {t}.orders o LEFT JOIN dbo.customers c ON c.customer_id = o.customer_id AND o.total > 60 ORDER BY 1")
    Logged "  which kept it" "\[total\] > 60" -Not
    Check "a table joined to itself gets one condition each" (Same "SELECT a.order_id, b.order_id AS other FROM {t}.orders a JOIN {t}.orders b ON b.customer_id = a.customer_id AND b.order_id <> a.order_id WHERE a.order_id = 1 AND b.total > 4")
    Logged "  one" "ms\.orders a: \[order_id\] = 1$"
    Logged "  and the other" "ms\.orders b: \[total\] > 4$"
    Check "a column named in three parts"     (Same "SELECT {t}.people.id FROM {t}.people WHERE {t}.people.id = 4")
    Logged "  is left in DuckDB" "\[id\] = 4" -Not
    Check "and so is a CTE's"                 (Same "WITH x AS (SELECT * FROM {t}.people) SELECT id FROM x WHERE id = 5")
    Check "and a table given column names of its own" (Same "SELECT x.amount FROM {t}.people AS x(amount, name, code, born, seen, stamp, score, id) WHERE x.id > 100")
    Logged "  condition" "\[id\] = 5" -Not

    Check "a few rows of a big table"         ((Ask $Port "SELECT COUNT(*) AS n FROM ms.big WHERE id <= 10") -match 'n\s+Int64\s+10')
    Check "are all SQL Server sends"          ((OnServer $BigRows) -eq 10)

    $scan = if ((Ask $Port "SELECT sql FROM duckdb_views() WHERE schema_name = 'ms' AND view_name = 'orders'") -match "daffy_scan\('(daffy_\d+)'") { $Matches[1] } else { "daffy_0" }
    $smuggled = Ask $Port "SELECT COUNT(*) AS n FROM daffy_scan('$scan', 'dbo.orders', filter := '1=1) DELETE FROM dbo.orders WHERE (1=1')"
    Check "a filter of a client's own is one condition, or refused" ($smuggled -match "is one T-SQL condition" -and $smuggled -match "closes a parenthesis it did not open")
    Check "and one that is runs"              ((Ask $Port "SELECT COUNT(*) AS n FROM daffy_scan('$scan', 'dbo.orders', filter := '[total] > 50')") -match 'n\s+Int64\s+2')
    [void](Ask $Port "EXEC daffydb.unmount 'pq'")

    # --- sessions, locks and cancels -------------------------------------------------------------
    $cut = Ask $Port "SET ROWCOUNT 5; SELECT id, pad FROM ms.big"
    Start-Sleep -Seconds 1
    Check "SET ROWCOUNT cuts a read short"    (([regex]::Matches($cut, 'id\s+Int32\s+\d+')).Count -eq 5)
    Check "and SQL Server is told to stop at once" ((OnServer $Requests) -eq 0)

    $quick = Ask $Port "EXEC daffydb.sqlserver '$conn;Lock Timeout=2', @schema = 'quick'"
    Check "Lock Timeout is taken"             ($quick -match "info: Mounted 10 tables" -and $quick -match "lock timeout 2 s")
    Check "and 0 is no limit"                 ((Ask $Port "EXEC daffydb.sqlserver '$conn;Lock Timeout=0', @schema = 'nolimit'") -match "no lock timeout")

    $holder = Hold "UPDATE dbo.locked SET v = v + 1 WHERE id = 2"
    $elapsed = Measure-Command { $blocked = Ask $Port "SELECT SUM(v) AS s FROM quick.locked" }
    Check "a read blocked by a lock fails after Lock Timeout" ($blocked -match "Msg 1222" -and $elapsed.TotalSeconds -lt 15)
    Check "and DaffyDB answers others straight after" ((Ask $Port "SELECT COUNT(*) AS n FROM dbo.customers") -match 'n\s+Int64\s+8')

    $cancelled = Run $Port "SELECT SUM(v) AS s FROM nolimit.locked" 3
    $elapsed = Measure-Command { $after = Ask $Port "SELECT COUNT(*) AS n FROM dbo.customers" }
    Check "a cancel stops a read blocked with no limit" ($cancelled -match "Timeout")
    Check "and DaffyDB answers others while the lock is still held" ($after -match 'n\s+Int64\s+8' -and $elapsed.TotalSeconds -lt 15)
    Start-Sleep -Seconds 1
    Check "and nothing is left waiting on SQL Server" ((OnServer $Requests) -eq 0)
    $holder.Close(); $holder = $null

    $holder = Hold "ALTER TABLE dbo.locked ADD extra int NULL"
    $elapsed = Measure-Command {
        $described = Ask $Port ("SELECT COUNT(*) AS n FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id " +
                                "JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = 'quick' AND o.name = 'locked'")
    }
    Check "the catalog is answered without asking SQL Server" ($described -match 'n\s+Int64\s+2' -and $elapsed.TotalSeconds -lt 15)
    Check "while a query of the table waits for its schema, and gives up" ((Ask $Port "SELECT * FROM quick.locked") -match "Msg 1222")
    $holder.Close(); $holder = $null

    # --- mounting again, one table, and what a client cannot do ----------------------------------
    [void](OnServer "CREATE TABLE dbo.added (a int); DROP TABLE dbo.gone_soon;" "daffydb_verify")
    $again = Ask $Port "EXEC daffydb.sqlserver '$conn', @schema = 'ms'"
    Check "mounting again reads it again"     ($again -match ("info: Read " + [regex]::Escape($where) + " again as 'ms': 10 tables"))
    Check "a table that came is there"        ((Ask $Port "SELECT COUNT(*) AS n FROM ms.added") -match 'n\s+Int64\s+0')
    Check "and one that went is gone"         ((Ask $Port "SELECT COUNT(*) AS n FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = 'ms' AND o.name = 'gone_soon'") -match 'n\s+Int64\s+0')

    $one = Ask $Port "EXEC daffydb.sqlserver '$conn', 'sales.regions', 'one'"
    Check "@table mounts just that one"       ($one -match ("info: Mounted 1 table from " + [regex]::Escape($where) + " as 'one'"))
    Check "and it reads"                      ((Ask $Port "SELECT region FROM one.regions WHERE region_id = 2") -match 'region\s+String\s+"South"')
    Check "a table that is not there is named" ((Ask $Port "EXEC daffydb.sqlserver '$conn', 'nope', 'two'") -match "SqlException 50306.*there is no table nope")

    Check "a client cannot repoint a mount"   ((Ask $Port "SELECT daffy_connect('daffy_1', 'Server=tcp:elsewhere;User Id=x;Password=y') AS x") -match "daffy_connect does not exist")
    Check "or break one"                      ((Ask $Port "SELECT daffy_disconnect('daffy_1') AS x") -match "daffy_disconnect does not exist")
    Check "or run T-SQL of its own through DaffySQL" ((Ask $Port "SELECT * FROM daffy_query('daffy_1', 'SELECT 1 AS a')") -match "daffy_query does not exist" -and
                                                      (Ask $Port "SELECT * FROM daffy_exec('daffy_1', 'SELECT 1')") -match "daffy_exec does not exist")
    Check "and the mounts still read"         ((Ask $Port "SELECT COUNT(*) AS n FROM ms.orders") -match 'n\s+Int64\s+5')

    # --- running T-SQL on it ----------------------------------------------------------------------
    [void](OnServer @"
CREATE TABLE dbo.ran (id int IDENTITY PRIMARY KEY, what nvarchar(40) NOT NULL);
GO
CREATE PROCEDURE dbo.p_rows @min int = 0, @name nvarchar(40) = NULL AS
    INSERT dbo.ran (what) VALUES (N'p_rows');
    CREATE TABLE #t (order_id int, total money);
    INSERT #t SELECT order_id, total FROM dbo.orders WHERE order_id >= @min;
    PRINT N'p_rows ran';
    SELECT order_id, total, @name AS said FROM #t ORDER BY order_id;
GO
CREATE PROCEDURE dbo.p_two AS SET NOCOUNT ON; SELECT 1 AS a; SELECT N'two' AS b, 2 AS c;
GO
CREATE PROCEDURE dbo.p_late AS SET NOCOUNT ON; SELECT 1 AS a; RAISERROR(N'late failure', 16, 7); SELECT 2 AS b;
GO
CREATE PROCEDURE dbo.p_none AS SET NOCOUNT ON; INSERT dbo.ran (what) VALUES (N'p_none');
GO
CREATE PROCEDURE dbo.p_out @o int OUTPUT AS SET NOCOUNT ON; SET @o = 42; SELECT 5 AS a; RETURN 3;
GO
CREATE PROCEDURE dbo.p_denied AS SELECT 1 AS a;
GO
GRANT EXECUTE ON dbo.p_rows TO daffydb_reader;
GRANT EXECUTE ON dbo.p_two TO daffydb_reader;
GRANT EXECUTE ON dbo.p_late TO daffydb_reader;
GRANT EXECUTE ON dbo.p_none TO daffydb_reader;
GRANT EXECUTE ON dbo.p_out TO daffydb_reader;
"@ "daffydb_verify")
    $Daffy = "Server=tcp:127.0.0.1,$Port;Database=daffydb;User ID=v;Password=v;Encrypt=false;Pooling=false"
    $Reader = "Server=tcp:$Server;Database=daffydb_verify;User ID=daffydb_reader;Password=$readerPassword;Encrypt=True;TrustServerCertificate=True;Pooling=false"
    $Ran = "SELECT COUNT(*) FROM dbo.ran"

    $refused = Ask ($Port + 1) (Through "SELECT 1")
    Check "run_sqlserver is refused without --allow-admin" ($refused -match "SqlException 50301" -and $refused -match "runs T-SQL on a SQL Server")
    Check "help lists it"                     ((Ask ($Port + 1) "EXEC daffydb.help") -match '"daffydb\.run_sqlserver"|"run_sqlserver"')
    Check "help for it shows its usage"       ((Ask ($Port + 1) "EXEC daffydb.help 'run_sqlserver'") -match 'EXEC daffydb\.run_sqlserver @mount, @sql')
    Check "a mount must be named that exists" ((Ask $Port "EXEC daffydb.run_sqlserver 'nope', N'SELECT 1'") -match "SqlException 50306.*no mount named 'nope'")
    Check "and be a SQL Server's"             ((Ask $Port "EXEC daffydb.run_sqlserver 'data', N'SELECT 1'") -match "SqlException 50302.*'data' is a folder mount")
    Check "and the T-SQL must be given"       ((Ask $Port "EXEC daffydb.run_sqlserver 'ms'") -match "SqlException 50302.*needs @sql")

    Check "a query answers as SQL Server does" (AsServer "SELECT order_id, total, placed, note FROM dbo.orders ORDER BY order_id")
    Check "a procedure, with its parameters, temp table and PRINT" (AsServer "EXEC dbo.p_rows @min = 3, @name = N'$zoe'")
    Check "so does one without SET NOCOUNT"   (AsServer "EXEC dbo.p_rows")
    Check "every result set comes back"       (AsServer "EXEC dbo.p_two")
    Check "and two batches' worth of statements" (AsServer "SELECT 1 AS a; SELECT 'x' AS b; PRINT 'said'; SELECT 3 AS c")
    Check "an output parameter and a return value, after the rows" (AsServer "DECLARE @o int, @r int; EXEC @r = dbo.p_out @o OUTPUT; SELECT @o AS o, @r AS r")
    Check "a column with no name"             (AsServer "SELECT 1, 2 AS b, 3")
    Check "columns with the same name"        (AsServer "SELECT 1 AS a, 2 AS a")
    Check "no rows at all"                    (AsServer "SELECT TOP (0) order_id, note FROM dbo.orders")
    Check "every type, as SQL Server sends it" (AsServer "SELECT id, t, b, f, n, m, dt, tm, d2, g, c, v, nv, vb FROM dbo.kinds")
    Check "and NULL in each"                  (AsServer "SELECT CAST(NULL AS tinyint) a, CAST(NULL AS bigint) b, CAST(NULL AS bit) c, CAST(NULL AS real) d, CAST(NULL AS float) e, CAST(NULL AS decimal(9,2)) f, CAST(NULL AS money) g, CAST(NULL AS date) h, CAST(NULL AS time) i, CAST(NULL AS datetime2) j, CAST(NULL AS datetime) k, CAST(NULL AS uniqueidentifier) l, CAST(NULL AS varchar(9)) m, CAST(NULL AS nvarchar(max)) n, CAST(NULL AS varbinary(max)) o, CAST(NULL AS xml) p, CAST(NULL AS sql_variant) q")
    Check "real, float and the old date types" (AsServer "SELECT CAST(1.5 AS real) a, CAST(2.25 AS float) b, CAST('2026-01-01T00:00:00.003' AS datetime) c, CAST('2026-01-01T10:30:00' AS smalldatetime) d, CAST(1.2345 AS smallmoney) e")
    Check "a long value, across packets"      (AsServer "SELECT HASHBYTES('SHA2_256', x) AS h, LEN(x) AS n, x FROM (SELECT REPLICATE(CAST(N'$zoe' AS nvarchar(max)), 40000) AS x) AS y")
    Check "a long binary one"                 (AsServer "SELECT CAST(REPLICATE(CAST('ab' AS varchar(max)), 30000) AS varbinary(max)) AS x")
    Check "varchar in the collation's code page" (AsServer "SELECT CAST(N'caf$([char]0xE9)' AS varchar(10)) COLLATE Latin1_General_CI_AS AS x, N'$zoe' AS y")

    $dto = ThroughDaffy "SELECT dto FROM dbo.kinds"
    Check "a datetimeoffset is datetime2 in UTC, as a mounted one is" ($dto -contains "row: 2026-10-21T06:30:00.0000000")
    $wide = ThroughDaffy "SELECT CAST('12345678901234567890123456.123456789012' AS decimal(38,12)) AS x, CAST(1.5 AS decimal(38,12)) AS y"
    Check "a decimal too wide for .NET's is its exact text" ($wide -contains "row: 12345678901234567890123456.123456789012|1.500000000000")
    $variant = ThroughDaffy "SELECT CAST(7 AS sql_variant) AS a, CAST(N'x' AS sql_variant) AS b, CAST('<a/>' AS xml) AS c"
    Check "sql_variant and xml are text"      ($variant -contains "row: 7|x|<a />" -or $variant -contains "row: 7|x|<a/>")
    $old = ThroughDaffy "CREATE TABLE #old (a text, b ntext, c image); INSERT #old VALUES ('x', N'$zoe', 0x01FF); INSERT #old VALUES (NULL, NULL, NULL); SELECT a, b, c FROM #old"
    Check "text, ntext and image"             ($old -contains "row: x|$zoe|0x01FF" -and $old -contains "row: NULL|NULL|NULL")
    $many = ThroughDaffy "SELECT TOP (100000) id FROM dbo.big ORDER BY id"
    Check "a hundred thousand rows"           (@($many | Where-Object { $_ -like "row:*" }).Count -eq 100000 -and $many[-1] -eq "row: 100000")

    Check "an error is SQL Server's own"      (AsServer "SELECT * FROM dbo.nope")
    Check "one after rows reaches the client, with the rows, and the batch goes on" (AsServer "EXEC dbo.p_late")
    Check "more than one all arrive"          (AsServer "RAISERROR(N'first', 16, 1); RAISERROR(N'second', 11, 2)")
    Check "a procedure's EXECUTE is the login's to have" ((ThroughDaffy "EXEC dbo.p_denied") -match "^error: 229 14 ")
    Check "and so is a write"                 ((ThroughDaffy "DELETE FROM dbo.orders") -match "^error: 229 14 ")
    Check "which changed nothing"             ((OnServer "SELECT COUNT(*) FROM dbo.orders" "daffydb_verify") -eq 5)

    $before = OnServer $Ran "daffydb_verify"
    $none = ThroughDaffy "EXEC dbo.p_none"
    Check "a procedure with no rows is no error" ($none.Count -eq 0)
    Check "and it ran, once"                  ((OnServer $Ran "daffydb_verify") -eq $before + 1)
    $counted = ThroughDaffy "EXEC dbo.p_rows @min = 99"
    Check "rows it changed are counted"       ($counted -contains "info: (1 row affected)")
    Check "and a procedure with rows ran once" ((OnServer $Ran "daffydb_verify") -eq $before + 2)
    [void](Whole $Daffy (Through "EXEC dbo.p_none") -Prepare)
    Check "preparing the command does not run it again" ((OnServer $Ran "daffydb_verify") -eq $before + 3)

    $rpc = New-Object System.Data.SqlClient.SqlConnection $Daffy
    $rpc.Open()
    $cmd = $rpc.CreateCommand(); $cmd.CommandType = "StoredProcedure"; $cmd.CommandText = "daffydb.run_sqlserver"
    [void]$cmd.Parameters.AddWithValue("@sql", "EXEC dbo.p_late"); [void]$cmd.Parameters.AddWithValue("@mount", "ms")
    $firstRows = $null; $rpcError = $null
    try { $r = $cmd.ExecuteReader(); [void]$r.Read(); $firstRows = $r.GetValue(0); while ($r.NextResult()) { }; $r.Close() }
    catch { $ex = $_.Exception; while ($ex -and $ex -isnot [System.Data.SqlClient.SqlException]) { $ex = $ex.InnerException }; $rpcError = $ex }
    $rpc.Close()
    Check "an RPC gets the rows and the error" ($firstRows -eq 1 -and $rpcError.Number -eq 50000 -and $rpcError.State -eq 7)

    $prepared = Whole $Daffy (Through "EXEC dbo.p_late") -Prepare
    Check "a prepared statement gets the rows and the error" ($prepared -contains "row: 1" -and [bool]($prepared -match "^error: 50000 16 7 "))
    $wrapped = Whole $Daffy "EXEC sp_executesql $(Literal (Through 'EXEC dbo.p_late'))"
    Check "and so does sp_executesql"         ($wrapped -contains "row: 1" -and [bool]($wrapped -match "^error: 50000 16 7 "))

    $byName = Whole $Daffy "EXEC daffydb.run_sqlserver @sql = N'SELECT 8 AS n', @mount = 'ms'"
    Check "arguments go by name"              ($byName -contains "row: 8")
    $variable = Whole $Daffy "DECLARE @s nvarchar(max) = N'SELECT 9 AS n'; EXEC daffydb.run_sqlserver 'ms', @s"
    Check "the T-SQL may be a variable"       ($variable -contains "row: 9")
    $lines = Whole $Daffy "EXEC daffydb.run_sqlserver 'ms', N'SELECT 1 AS a;`nSELECT ''GO; not a batch'' AS b -- done`n'"
    Check "and run over lines, with its own semicolons" ($lines -contains "row: GO; not a batch")
    $mixed = Whole $Daffy "$(Through 'SELECT 10 AS n'); SELECT COUNT(*) AS n FROM dbo.customers"
    Check "it sits in a batch with other statements" ($mixed -contains "row: 10" -and $mixed -contains "row: 8")
    $caught = Whole $Daffy "BEGIN TRY $(Through 'EXEC dbo.p_late') END TRY BEGIN CATCH SELECT ERROR_NUMBER() AS n, ERROR_MESSAGE() AS m END CATCH"
    Check "a CATCH block catches it, and keeps the rows" ($caught -contains "row: 1" -and $caught -contains "row: 50000|late failure")

    [void](ThroughDaffy "USE master; CREATE TABLE #left (a int); SET LOCK_TIMEOUT 1")
    $state = ThroughDaffy "SELECT DB_NAME() AS d, OBJECT_ID('tempdb..#left') AS t, @@LOCK_TIMEOUT AS l"
    Check "a USE, a temp table and a SET end with the call" ($state -contains "row: daffydb_verify|NULL|-1")
    Check "and a mounted table still reads"   ((Whole $Daffy "SELECT COUNT(*) AS n FROM ms.orders") -contains "row: 5")
    $before = OnServer $Ran "daffydb_verify"
    [void](ThroughDaffy "BEGIN TRAN; EXEC dbo.p_none")
    Check "a transaction left open is rolled back" ((OnServer $Ran "daffydb_verify") -eq $before)
    Check "and its session is fit to use again" ((ThroughDaffy "SELECT @@TRANCOUNT AS n") -contains "row: 0")

    # A batch that takes a while, on a connection of its own, while another client asks for something.
    $slow = New-Object System.Data.SqlClient.SqlConnection "$Daffy;Asynchronous Processing=true"
    $slow.Open()
    $cmd = $slow.CreateCommand(); $cmd.CommandTimeout = 60; $cmd.CommandText = Through "WAITFOR DELAY '00:00:06'; SELECT 1 AS done"
    $running = $cmd.BeginExecuteNonQuery()
    Start-Sleep -Milliseconds 500
    $elapsed = Measure-Command { $meanwhile = Whole $Daffy "SELECT COUNT(*) AS n FROM dbo.customers" }
    Check "other clients are answered while SQL Server works" ($meanwhile -contains "row: 8" -and $elapsed.TotalSeconds -lt 3)
    Check "and it was still working then"     (-not $running.IsCompleted)
    $slowMount = Whole $Daffy "SELECT COUNT(*) AS n FROM ms.orders"
    Check "mounted tables read meanwhile too" ($slowMount -contains "row: 5")
    [void]$cmd.EndExecuteNonQuery($running)
    $slow.Close()

    $elapsed = Measure-Command { $cancelled = Run $Port (Through "WAITFOR DELAY '00:05:00'") 3 }
    Check "a cancel stops it"                 ($cancelled -match "Timeout" -and $elapsed.TotalSeconds -lt 15)
    Start-Sleep -Seconds 1
    Check "and nothing is left running on SQL Server" ((OnServer $Requests) -eq 0)
    $elapsed = Measure-Command { $cancelled = Run $Port (Through "SELECT a.id FROM dbo.big a CROSS JOIN dbo.big b") 3 }
    Check "a cancel stops one sending rows"   ($cancelled -match "Timeout" -and $elapsed.TotalSeconds -lt 15)
    Start-Sleep -Seconds 1
    Check "and that leaves nothing running either" ((OnServer $Requests) -eq 0)
    Check "and DaffyDB goes on"               ((ThroughDaffy "SELECT 11 AS n") -contains "row: 11")

    # --- keeping what it returned -----------------------------------------------------------------
    function Into($sql, $table) { Whole $Daffy "$(Through $sql), @into = '$table'" }
    function Rows($lines) { @($lines | Where-Object { $_ -like "row:*" }) -join "`n" }
    function SameRows($kept, $sql) {
        $here = Rows (Whole $Daffy $kept)
        $there = Rows (Whole $Reader $sql)
        if ($here -ne $there) { Write-Output "    kept:   $here`n    direct: $there" }
        $here.Length -gt 0 -and $here -eq $there
    }
    function RanCount { OnServer $Ran "daffydb_verify" }

    $kept = Into "EXEC dbo.p_rows @min = 3, @name = N'$zoe'" "#kept"
    Check "@into sends none of the rows it keeps" (-not [bool]($kept -match "^(set|row):"))
    Check "and says what it put where"        ($kept -contains "info: Put 3 rows in #kept.")
    Check "with the procedure's messages"     ($kept -contains "info: p_rows ran")
    Check "the rows read back as SQL Server returned them" (SameRows "SELECT order_id, total, said FROM #kept ORDER BY order_id" "EXEC dbo.p_rows @min = 3, @name = N'$zoe'")
    [void](Into "SELECT id, t, b, f, n, m, dt, tm, d2, dto, g, c, v, nv, vb FROM dbo.kinds" "#k")
    Check "every type keeps its value"        (SameRows "SELECT id, t, b, f, n, m, dt, g, c, v, nv, vb FROM #k" "SELECT id, t, b, f, n, m, dt, g, c, v, nv, vb FROM dbo.kinds")
    Check "time and datetime2 to the microsecond, datetimeoffset in UTC" ((Whole $Daffy "SELECT tm, d2, dto FROM #k") -contains "row: 23:59:59.1234560|2026-10-21T08:30:00.1234560|2026-10-21T06:30:00.0000000")
    [void](Into "SELECT CAST(NULL AS tinyint) a, CAST(NULL AS bigint) b, CAST(NULL AS bit) c, CAST(NULL AS real) d, CAST(NULL AS float) e, CAST(NULL AS decimal(9,2)) f, CAST(NULL AS money) g, CAST(NULL AS date) h, CAST(NULL AS time) i, CAST(NULL AS datetime2) j, CAST(NULL AS datetime) k, CAST(NULL AS uniqueidentifier) l, CAST(NULL AS varchar(9)) m, CAST(NULL AS nvarchar(max)) n, CAST(NULL AS varbinary(max)) o, CAST(NULL AS xml) p, CAST(NULL AS sql_variant) q" "#nulls")
    Check "NULL in each type stays NULL"      ((Rows (Whole $Daffy "SELECT * FROM #nulls")) -eq ("row: " + ((1..17 | ForEach-Object { "NULL" }) -join "|")))
    [void](Into "SELECT CAST(1.5 AS real) a, CAST(2.25 AS float) b, CAST('2026-01-01T10:30:00' AS smalldatetime) c, CAST(1.2345 AS smallmoney) d, CAST(200 AS tinyint) e, CAST(-5 AS smallint) f, CAST('2026-01-01T00:00:00.003' AS datetime) g" "#olds")
    Check "real, float and the old types"     ((Whole $Daffy "SELECT a, b, c, d, e, f, g FROM #olds") -contains "row: 1.5|2.25|2026-01-01T10:30:00.0000000|1.2345|200|-5|2026-01-01T00:00:00.0033330")
    [void](Into "SELECT CAST('12345678901234567890123456.123456789012' AS decimal(38,12)) AS x, CAST(12345678901234567.0123456789 AS decimal(38,10)) AS y, REPLICATE(CAST(N'$zoe' AS nvarchar(max)), 40000) AS z" "#wide")
    Check "a decimal too wide for .NET's stays exact, and a long value whole" ((Whole $Daffy "SELECT x, y, LEN(z) AS n FROM #wide") -contains "row: 12345678901234567890123456.123456789012|12345678901234567.0123456789|200000")
    [void](Into "SELECT TOP (100000) id FROM dbo.big ORDER BY id" "#many")
    Check "a hundred thousand rows"           ((Whole $Daffy "SELECT COUNT(*) AS n, MAX(id) AS m FROM #many") -contains "row: 100000|100000")
    [void](Into "SELECT 1, 2 AS a, 3 AS a" "#names")
    Check "columns with no name, or the same name, get ones a table can have" ((Whole $Daffy "SELECT column0, a, a_1 FROM #names") -contains "row: 1|2|3")
    $empty = Into "SELECT TOP (0) order_id, note FROM dbo.orders" "#empty"
    Check "no rows still makes the table"     ($empty -contains "info: Put 0 rows in #empty." -and (Whole $Daffy "SELECT order_id, note FROM #empty") -contains "set: order_id|note")
    [void](Into "SELECT 5 AS other" "#kept")
    Check "a table of that name is replaced"  ((Whole $Daffy "SELECT other FROM #kept") -contains "row: 5" -and [bool]((Whole $Daffy "SELECT order_id FROM #kept") -match "^(error|failed)"))
    $two = Into "EXEC dbo.p_two" "#two"
    Check "the first result set is kept, and the rest come back" ($two -contains "row: two|2" -and $two -notcontains "row: 1" -and (Whole $Daffy "SELECT a FROM #two") -contains "row: 1")
    $late = Into "EXEC dbo.p_late" "#late"
    Check "rows before an error are kept, and the error reported" ([bool]($late -match "^error: 50000 16 7 ") -and (Whole $Daffy "SELECT a FROM #late") -contains "row: 1")
    $before = RanCount
    $none = Into "EXEC dbo.p_none" "#kept"
    Check "T-SQL with no result set says so"  ([bool]($none -match "^error: 50309 .*returned no result set, so #kept was not made"))
    Check "though it did run"                 ((RanCount) -eq $before + 1)
    Check "and the table is as it was"        ((Whole $Daffy "SELECT other FROM #kept") -contains "row: 5")
    Check "an error and no rows makes no table" ([bool]((Into "SELECT * FROM dbo.nope" "#nope") -match "^error: 208 ") -and [bool]((Whole $Daffy "SELECT * FROM #nope") -match "^(error|failed)"))
    $before = RanCount
    Check "@into must name a temp table"      ((Into "EXEC dbo.p_none" "orders") -match "^error: 50302 .*@into names a temp table")
    Check "and if not, nothing runs"          ((RanCount) -eq $before)
    [void](Into "SELECT 1 AS customer_id" "#j")
    Check "a kept table joins a parquet file" ((Whole $Daffy "SELECT COUNT(*) AS n FROM #j j JOIN dbo.customers c ON c.customer_id = j.customer_id") -contains "row: 1")
    Check "and saves as parquet"              ((Ask $Port "EXEC daffydb.save_parquet '#j', '$work\keptsaved'") -match "info: Saved #j to .*j\.parquet: 1 row")
    Check "@into is refused without --allow-admin" ((Ask ($Port + 1) "$(Through 'SELECT 1 AS a'), @into = '#x'") -match "SqlException 50301")

    function Insert($table, $sql) { "INSERT $table $(Through $sql)" }
    $ins = Whole $Daffy "CREATE TABLE #ins (order_id int, total money, said nvarchar(40)); $(Insert '#ins' "EXEC dbo.p_rows @min = 3, @name = N'$zoe'")"
    Check "INSERT ... EXEC sends none of its rows" (-not [bool]($ins -match "^(set|row):") -and $ins -contains "info: (3 rows affected)" -and $ins -contains "info: p_rows ran")
    Check "and they read back as SQL Server returned them" (SameRows "SELECT order_id, total, said FROM #ins ORDER BY order_id" "EXEC dbo.p_rows @min = 3, @name = N'$zoe'")
    [void](Whole $Daffy ("INSERT INTO #ins EXECUTE daffydb.run_sqlserver 'ms', " + (Literal "EXEC dbo.p_rows @min = 5")))
    Check "INSERT INTO and EXECUTE add to it" ((Whole $Daffy "SELECT COUNT(*) AS n FROM #ins") -contains "row: 4")
    [void](Whole $Daffy "CREATE TABLE #cols (a int, b nvarchar(10), c int); $(Insert '#cols (c, a)' 'SELECT 1 AS x, 2 AS y')")
    Check "it may list its columns"           ((Whole $Daffy "SELECT a, b, c FROM #cols") -contains "row: 2|NULL|1")
    [void](Whole $Daffy "CREATE TABLE #both (a int); $(Insert '#both' 'SELECT 1 AS x; SELECT ''7'' AS y')")
    Check "every result set goes in, converted to the table's types" ((Rows (Whole $Daffy "SELECT a FROM #both ORDER BY a")) -eq "row: 1`nrow: 7")
    $bad = Whole $Daffy "CREATE TABLE #bad (a int); $(Insert '#bad' 'SELECT 1 AS x; SELECT ''abc'' AS y')"
    Check "a value that does not fit fails it, saying the T-SQL ran" ([bool]($bad -match "^error: 50309 .*though the T-SQL did run"))
    Check "and nothing goes in, not even the part that fits" ((Whole $Daffy "SELECT COUNT(*) AS n FROM #bad") -contains "row: 0")
    Check "and the rows come back rather than be lost" ($bad -contains "row: 1" -and $bad -contains "row: abc")
    $wrong = Whole $Daffy "CREATE TABLE #wrong (a int); $(Insert '#wrong' 'SELECT 1 AS x, 2 AS y')"
    Check "so does the wrong number of columns" ([bool]($wrong -match "^error: 50309 ") -and (Whole $Daffy "SELECT COUNT(*) AS n FROM #wrong") -contains "row: 0")
    Check "and the connection goes on"        ((Whole $Daffy "SELECT COUNT(*) AS n FROM dbo.customers") -contains "row: 8" -and (Into "SELECT 1 AS a" "#after") -contains "info: Put 1 row in #after.")
    $before = RanCount
    Check "a table that is not there fails"   ([bool]((Whole $Daffy (Insert '#notthere' 'EXEC dbo.p_none')) -match "^(error|failed)"))
    Check "a column that is not there fails"  ([bool]((Whole $Daffy (Insert '#ins (nope)' 'EXEC dbo.p_none')) -match "^(error|failed)"))
    Check "a mounted table cannot be written to" ([bool]((Whole $Daffy (Insert 'ms.orders' 'EXEC dbo.p_none')) -match "^(error|failed)"))
    Check "@into and INSERT together are refused" ((Whole $Daffy "$(Insert '#ins' 'EXEC dbo.p_none'), @into = '#x'") -match "^error: 50302 .*use one")
    Check "and none of those ran the procedure" ((RanCount) -eq $before)
    $lateIns = Whole $Daffy "CREATE TABLE #lateins (a int); $(Insert '#lateins' 'EXEC dbo.p_late')"
    Check "rows before an error go in, and the error is reported" ([bool]($lateIns -match "^error: 50000 16 7 ") -and (Rows (Whole $Daffy "SELECT a FROM #lateins ORDER BY a")) -eq "row: 1`nrow: 2")
    $caughtIns = Whole $Daffy "CREATE TABLE #c (a int); BEGIN TRY $(Insert '#c' 'EXEC dbo.p_late') END TRY BEGIN CATCH SELECT ERROR_NUMBER() AS n END CATCH"
    Check "a CATCH block catches that too"    ($caughtIns -contains "row: 50000" -and (Whole $Daffy "SELECT COUNT(*) AS n FROM #c") -contains "row: 2")
    [void](Whole $Daffy "CREATE TABLE #p (x int)")
    $before = RanCount
    [void](Whole $Daffy (Insert '#p' 'SET NOCOUNT ON; EXEC dbo.p_none; SELECT 1 AS x') -Prepare)
    Check "preparing INSERT ... EXEC does not run it again" ((RanCount) -eq $before + 1 -and (Whole $Daffy "SELECT COUNT(*) AS n FROM #p") -contains "row: 1")
    Check "INSERT ... EXEC is refused without --allow-admin" ((Ask ($Port + 1) "CREATE TABLE #x (a int); $(Insert '#x' 'SELECT 1 AS a')") -match "SqlException 50301")

    # The prompt needs no flag, and shows an error after the rows it came with.
    $session = Join-Path $work "session.txt"
    @(
        ':tables'
        "EXEC daffydb.sqlserver '$conn', 'sales.regions', 'atprompt';"
        "EXEC daffydb.run_sqlserver 'atprompt', N'EXEC dbo.p_late';"
        ':quit'
    ) | Set-Content -Path $session -Encoding ascii
    $prompt = & { $ErrorActionPreference = "Continue"; (Get-Content $session | & $exe --port ($Port + 2) --data-dir "$work\data" --repl 2>&1) | Out-String }
    Check "the prompt runs it, and shows the rows and then the error" ($prompt -match '(?s)\b1\b.*error: Msg 50000, Level 16: late failure')
    Check "with the password masked there too" ($prompt.IndexOf($readerPassword) -lt 0)

    [void](ThroughDaffy "ALTER LOGIN daffydb_reader WITH PASSWORD = N'Shh''NotThis1!'")

    # --- unmounting ------------------------------------------------------------------------------
    $gone = foreach ($m in "ms", "quick", "nolimit", "one") { Ask $Port "EXEC daffydb.unmount '$m'" }
    Check "every mount unmounts"              (@($gone | Where-Object { $_ -match "info: Unmounted '\w+': 10 tables taken away|info: Unmounted 'one': 1 table taken away" }).Count -eq 4)
    Check "its tables are gone"               ((Ask $Port "SELECT COUNT(*) AS n FROM ms.orders") -match "SqlException")
    Start-Sleep -Seconds 1
    Check "and SQL Server has no sessions of it left" ((OnServer $Sessions) -eq 0)
    Check "and there is nothing to run T-SQL on" ((Ask $Port (Through "SELECT 1")) -match "SqlException 50306.*no mount named 'ms'")
}
finally {
    if ($holder) { $holder.Close() }
    foreach ($p in @($open, $shut)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    try { [void](OnServer $Drop) } catch { Write-Output "warning: daffydb_verify was not dropped: $($_.Exception.Message)" }
}

$opened = Get-Content $openLog -Raw
foreach ($entry in $logged) {
    $name, $pattern, $not = $entry
    $sent = @($opened -split "`r?`n" | Where-Object { $_ -match '\[sqlserver\] sent to SQL Server' -and $_ -match $pattern }).Count -gt 0
    Check $name ($sent -ne $not)
}
Check "every mount is logged, its password masked" ($opened -match "\[admin\]  session \d+ .*daffydb\.sqlserver @connection='Server=tcp:.*Password=\*\*\*;")
Check "and what it logged in to"            ($opened -match "\[duckdb\] logged in to daffydb_reader@")
Check "the password is nowhere in the log"  ($opened.IndexOf($readerPassword) -lt 0)
Check "a refused mount is logged too"       ((Get-Content $shutLog -Raw) -match "\[admin\]  refused session \d+ .*daffydb\.sqlserver")
Check "T-SQL that ran is logged, on a line"  ($opened -match "\[admin\]  session \d+ .*daffydb\.run_sqlserver @mount='ms' @sql='SELECT 1 AS a; SELECT 'GO; not a batch")
Check "a password in it is masked"          ($opened -match "\[admin\]  session \d+ .*ALTER LOGIN daffydb_reader WITH PASSWORD = \*\*\*" -and $opened.IndexOf("NotThis1") -lt 0)
Check "a refused run is logged too"         ((Get-Content $shutLog -Raw) -match "\[admin\]  refused session \d+ .*daffydb\.run_sqlserver")
Check "INSERT ... EXEC is logged as that"   ($opened -match "\[admin\]  session \d+ .*INSERT #ins EXEC daffydb\.run_sqlserver @mount='ms'")

Remove-Item ($Port, ($Port + 1), ($Port + 2) | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$_.pbids" }) -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Pop-Location

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "sqlserver checks passed"; exit 0 } else { Write-Output "$failed check(s) failed"; exit 1 }
