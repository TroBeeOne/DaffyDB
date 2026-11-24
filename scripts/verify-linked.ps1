# Checks DaffyDB as a SQL Server linked server: LocalDB, which is SQL Server's own engine, reads
# DaffyDB through MSDASQL and ODBC Driver 18, as sp_addlinkedserver sets it up. Every query is
# asked twice - through the linked server, and of DaffyDB directly - and the two answers must be
# the same, column for column and value for value.
#
#   pwsh scripts/verify-linked.ps1
#
# A linked server asks for a remote table's columns before it reads it, and refuses the query if
# the two disagree in anything, a column's length included; that is most of what is checked here.
#
# It needs LocalDB and ODBC Driver 18 for SQL Server, and is skipped, with the reason, without
# them. The linked server it makes on LocalDB, named after the port, is dropped again at the end.
param([int]$Port = 14510)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $condition, $detail) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name"; if ($detail) { Write-Output "     $detail" } }
}
function Finish($message, $code) {
    Pop-Location
    Write-Output "========================================================"
    Write-Output $message
    exit $code
}

# --- what it needs --------------------------------------------------------------------------
if (-not (Get-Command sqllocaldb -ErrorAction SilentlyContinue)) { Finish "skip linked server - LocalDB is not installed" 0 }
if (-not (Get-OdbcDriver -Name "ODBC Driver 18 for SQL Server" -Platform "64-bit" -ErrorAction SilentlyContinue)) {
    Finish "skip linked server - ODBC Driver 18 for SQL Server is not installed" 0
}
sqllocaldb start MSSQLLocalDB | Out-Null

$localDb = "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Database=master;Connect Timeout=30"
$direct = "Server=tcp:127.0.0.1,$Port;Database=daffydb;User ID=verify;Password=verify;Encrypt=false;Pooling=false;Connect Timeout=10"
$link = "DaffyDB_Verify_$Port"

# Every result set a batch returns, each row as text: the columns' names and SQL Server types,
# then the values. An error comes back as the text "ERROR: " and the message.
function Answer($connection, $sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connection
    $lines = New-Object System.Collections.Generic.List[string]
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 60
        $reader = $cmd.ExecuteReader()
        do {
            if ($reader.FieldCount -eq 0) { continue }
            $lines.Add((0..($reader.FieldCount - 1) | ForEach-Object { "$($reader.GetName($_)) $($reader.GetDataTypeName($_))" }) -join " | ")
            while ($reader.Read()) {
                $lines.Add((0..($reader.FieldCount - 1) | ForEach-Object { Render $reader.GetValue($_) }) -join " | ")
            }
        } while ($reader.NextResult())
        $reader.Close()
    }
    catch {
        $x = $_.Exception; while ($x.InnerException) { $x = $x.InnerException }
        $lines.Add("ERROR: $($x.Message)")
    }
    finally { $cn.Close() }
    return ,$lines.ToArray()
}

function Render($value) {
    if ($value -is [System.DBNull]) { return "NULL" }
    if ($value -is [byte[]]) { return "0x" + [System.BitConverter]::ToString($value).Replace("-", "") }
    if ($value -is [datetime]) { return $value.ToString("o") }
    if ($value -is [double] -or $value -is [single]) { return $value.ToString("R", [cultureinfo]::InvariantCulture) }
    return [System.Convert]::ToString($value, [cultureinfo]::InvariantCulture)
}

# The same query through the linked server and of DaffyDB directly: the answers must match. With
# -AnyOrder the rows are compared sorted, for a query whose order SQL Server decides: it sorts a
# NULL first, and DuckDB last.
function Same($name, $linked, $plain, [switch]$AnyOrder) {
    $through = Answer $localDb $linked
    $own = Answer $direct $plain
    if ($AnyOrder) {
        $through = @($through | Select-Object -First 1) + @($through | Select-Object -Skip 1 | Sort-Object)
        $own = @($own | Select-Object -First 1) + @($own | Select-Object -Skip 1 | Sort-Object)
    }
    $same = ($through.Count -gt 1) -and (($through -join "`n") -eq ($own -join "`n"))
    $detail = if ($same) { $null } else { "linked: $($through -join ' / ')`n     direct: $($own -join ' / ')" }
    Check $name $same $detail
}

# --- a DaffyDB with every type in it ----------------------------------------------------------
$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-linked-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null
dotnet run --project src/TdsProxy --no-build -- --seed --data-dir "$work\data" | Out-Null

$init = Join-Path $work "init.sql"
@"
CREATE TABLE daffydb.dbo.type_zoo (
    c_tinyint UTINYINT, c_smallint SMALLINT, c_int INTEGER, c_bigint BIGINT, c_real FLOAT,
    c_float DOUBLE, c_decimal DECIMAL(10,2), c_bit BOOLEAN, c_date DATE, c_time TIME,
    c_datetime2 TIMESTAMP, c_nvarchar VARCHAR, c_varbinary BLOB, c_guid UUID);
INSERT INTO daffydb.dbo.type_zoo VALUES
    (200, -12345, 123456789, 9000000000, 1.5, 2.25, 12345.67, true, '2024-02-29', '13:14:15',
     '2024-02-29 13:14:15.123456', 'h$([char]0xE9)llo', '\xCA\xFE'::BLOB, '6F9619FF-8B86-D011-B42D-00C04FC964FF'),
    (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
CREATE TABLE daffydb.dbo.long_text (id INTEGER, note VARCHAR);
INSERT INTO daffydb.dbo.long_text VALUES (1, 'short'), (2, repeat('x', 4001));
"@ | Set-Content -Path $init -Encoding UTF8

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
$log = Join-Path $work "server.log"
$server = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "--port $Port --data-dir `"$work\data`" --init `"$init`" --no-repl --trace"

try {
    Start-Sleep -Seconds 6

    # --- the linked server, as the README sets it up -----------------------------------------
    $made = Answer $localDb @"
IF EXISTS (SELECT 1 FROM sys.servers WHERE name = N'$link') EXEC sp_dropserver @server = N'$link', @droplogins = 'droplogins';
EXEC sp_addlinkedserver @server = N'$link', @srvproduct = N'', @provider = N'MSDASQL',
    @provstr = N'Driver={ODBC Driver 18 for SQL Server};Server=127.0.0.1,$Port;Database=daffydb;Encrypt=yes;TrustServerCertificate=yes;';
EXEC sp_addlinkedsrvlogin @rmtsrvname = N'$link', @useself = 'false', @rmtuser = N'verify', @rmtpassword = N'verify';
SELECT name FROM sys.servers WHERE name = N'$link';
"@
    Check "the linked server is made" (($made -join "`n") -match [regex]::Escape($link)) ($made -join " / ")

    $remote = "[$link].[daffydb].[dbo]"

    # --- four-part names ----------------------------------------------------------------------
    Same "a table, by its four-part name" `
        "SELECT * FROM $remote.[customers] ORDER BY customer_id" `
        "SELECT * FROM customers ORDER BY customer_id"
    Same "TOP and WHERE" `
        "SELECT TOP 3 order_id, product, quantity FROM $remote.[orders] WHERE quantity > 1 ORDER BY order_id" `
        "SELECT TOP 3 order_id, product, quantity FROM orders WHERE quantity > 1 ORDER BY order_id"
    # SQL Server does the counting here, and its COUNT is an int.
    Same "two remote tables joined" `
        "SELECT c.name, COUNT(*) AS n FROM $remote.[orders] o JOIN $remote.[customers] c ON c.customer_id = o.customer_id GROUP BY c.name ORDER BY c.name" `
        "SELECT c.name, CAST(COUNT(*) AS int) AS n FROM orders o JOIN customers c ON c.customer_id = o.customer_id GROUP BY c.name ORDER BY c.name"
    Same "a local variable as a parameter" `
        "DECLARE @customer int = 3; SELECT order_id, product FROM $remote.[orders] WHERE customer_id = @customer ORDER BY order_id" `
        "SELECT order_id, product FROM orders WHERE customer_id = 3 ORDER BY order_id"
    Same "joined to rows on the local server" `
        "SELECT v.id, c.name FROM (VALUES (2), (5)) v(id) JOIN $remote.[customers] c ON c.customer_id = v.id ORDER BY v.id" `
        "SELECT customer_id AS id, name FROM customers WHERE customer_id IN (2, 5) ORDER BY customer_id"
    $types = "c_tinyint, c_smallint, c_int, c_bigint, c_real, c_float, c_decimal, c_bit, c_date, " +
             "c_datetime2, c_nvarchar, c_varbinary, c_guid"
    Same "every type DaffyDB sends, but time" `
        "SELECT $types FROM $remote.[type_zoo]" `
        "SELECT $types FROM type_zoo" -AnyOrder

    # MSDASQL cannot read a time column: SQL Server fails on one of its own exactly this way.
    $time = Answer $localDb "SELECT c_time FROM $remote.[type_zoo]"
    Check "a time column fails, as it does from SQL Server itself" `
        (($time -join "`n") -match "expected data length is 16, while the returned data length is 12") ($time -join " / ")
    Same "and reads as text in an OPENQUERY" `
        "SELECT * FROM OPENQUERY([$link], 'select c_int, cast(c_time as varchar(16)) as t from type_zoo')" `
        "select c_int, cast(c_time as varchar(16)) as t from type_zoo" -AnyOrder

    # --- OPENQUERY, which sends the query as written ------------------------------------------
    Same "OPENQUERY: a query" `
        "SELECT * FROM OPENQUERY([$link], 'select top 3 customer_id, name, city from customers order by customer_id desc')" `
        "select top 3 customer_id, name, city from customers order by customer_id desc"
    Same "OPENQUERY: an aggregate" `
        "SELECT * FROM OPENQUERY([$link], 'select customer_id, sum(quantity) as units from orders group by customer_id order by customer_id')" `
        "select customer_id, sum(quantity) as units from orders group by customer_id order by customer_id"
    Same "OPENQUERY: the README's example" `
        "SELECT * FROM OPENQUERY([$link], 'select city, count(*) as n from customers group by city')" `
        "select city, count(*) as n from customers group by city" -AnyOrder

    # --- the limit: text is nvarchar(4000), as the catalog says ------------------------------
    Same "text up to 4000 characters" `
        "SELECT id, note FROM $remote.[long_text] WHERE id = 1" `
        "SELECT id, note FROM long_text WHERE id = 1"
    # It goes out as nvarchar(max), which is not the length SQL Server was told.
    $long = Answer $localDb "SELECT id, LEN(note) AS n FROM $remote.[long_text] WHERE id = 2"
    Check "a longer value fails the query, as the README says" `
        (($long -join "`n") -match "expected data length is 8000|inconsistent metadata") ($long -join " / ")
    Same "and LEFT(note, 4000) in an OPENQUERY reads it, cut" `
        "SELECT id, LEN(note) AS n FROM OPENQUERY([$link], 'select id, left(note, 4000) as note from long_text where id = 2')" `
        "SELECT 2 AS id, 4000 AS n"
}
finally {
    Answer $localDb "IF EXISTS (SELECT 1 FROM sys.servers WHERE name = N'$link') EXEC sp_dropserver @server = N'$link', @droplogins = 'droplogins';" | Out-Null
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
}

# Whatever the linked server asked, DaffyDB answered without an error of its own.
$errors = @(Select-String -Path $log -Pattern "-> ERROR" | ForEach-Object { $_.Line.Trim() })
Check "DaffyDB answered every request it was sent" ($errors.Count -eq 0) ($errors -join " / ")
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

if ($failed -eq 0) { Finish "linked server checks passed" 0 } else { Finish "$failed check(s) failed" 1 }
