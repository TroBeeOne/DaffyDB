# Checks daffydb.save_parquet and daffydb.save_parquet_all, which write tables out as parquet files.
#
# Starts one server with --allow-admin and one without, both serving a small folder, with a second
# folder mounted into a schema of its own and an --init view. Saves one table, a temp table and a
# table under a name of the caller's own; then everything, with a view whose name no file can
# have, a mounted file that has gone, and #temp tables. Checks that nothing is ever replaced, that
# two tables that would share a file are refused, that no half-written file is left behind, and
# that a server started on the saved folder serves every table back under its own name. Saves big
# temp tables while another client queries, and checks the query is answered before the save is
# done: the connection is let go between tables. And cancels a save part way, which must leave no
# file behind, whole or half-written. Nothing here touches the network.
param([int]$Port = 14520)

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

function Call($port, $procedure, [string[]]$params) {
    $arguments = @("run", "--project", "src/TdsProbe", "--no-build", "--", "--port", $port, "--proc", $procedure)
    foreach ($p in $params) { $arguments += @("--param", $p) }
    & dotnet @arguments 2>&1 | Out-String
}

# TdsProbe gives a command five seconds; the big save at the end needs longer, and the cancelled
# one less. A client whose timeout runs out cancels what it sent, as SSMS's Cancel button does.
function Run($port, $sql, $timeout = 120) {
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:127.0.0.1,$port;Database=daffydb;User ID=v;Password=v;Encrypt=false;Pooling=false"
    $cn.Open()
    try { $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = $timeout; [void]$cmd.ExecuteNonQuery(); "done" }
    catch { "error: $($_.Exception.Message)" }
    finally { $cn.Close() }
}

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-parquet-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
foreach ($folder in "data", "extra", "gone") { New-Item -ItemType Directory -Path (Join-Path $work $folder) -Force | Out-Null }

Copy-Item "data/customers.parquet" (Join-Path $work "data\customers.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "data\orders.parquet")
Copy-Item "data/customers.parquet" (Join-Path $work "extra\Customer.parquet")
@("product_id,name", "1,Widget", "2,Gadget") | Set-Content -Path (Join-Path $work "extra\products.csv") -Encoding ascii
@("x", "1") | Set-Content -Path (Join-Path $work "gone\vanished.csv") -Encoding ascii
@("CREATE VIEW dbo.legacy AS SELECT 1 AS x;") | Set-Content -Path (Join-Path $work "startup.sql") -Encoding ascii

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
$openLog = Join-Path $work "open.log"
$shutLog = Join-Path $work "shut.log"
$backLog = Join-Path $work "back.log"
$one  = Join-Path $work "one"
$all  = Join-Path $work "all"
$back = $null

$open = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $openLog `
    -ArgumentList "--port $Port --data-dir `"$work\data`" --init `"$work\startup.sql`" --allow-admin --allow-passthrough --no-repl"
$shut = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $shutLog `
    -ArgumentList "--port $($Port + 1) --data-dir `"$work\data`" --no-repl"

try {
    Start-Sleep -Seconds 5

    # --- who may ------------------------------------------------------------------------------
    $refused = Ask ($Port + 1) "EXEC daffydb.save_parquet 'orders', '$one'"
    Check "save_parquet is refused without --allow-admin" ($refused -match "SqlException 50301" -and $refused -match "writes files")
    Check "and writes nothing"                ((Test-Path $one) -eq $false)
    Check "help lists both commands"          ((Ask ($Port + 1) "EXEC daffydb.help") -match '"daffydb\.save_parquet"' -and
                                               (Ask ($Port + 1) "EXEC daffydb.help") -match '"daffydb\.save_parquet_all"')
    Check "help for one shows its usage"      ((Ask ($Port + 1) "EXEC daffydb.help 'save_parquet'") -match 'EXEC daffydb\.save_parquet @table, @folder \[, @file\]')

    # --- one table at a time -------------------------------------------------------------------
    $saved = Ask $Port "EXEC daffydb.save_parquet 'dbo.orders', '$one'"
    Check "a table saves, into a folder it makes" ($saved -match "info: Saved dbo\.orders to .*one\\dbo\.orders\.parquet: 24 rows")
    Check "the result says what was written"  ($saved -match 'file\s+String\s+".*dbo\.orders\.parquet"' -and $saved -match 'rows\s+Int64\s+24' -and
                                               $saved -match 'status\s+String\s+"saved"')
    Check "the file holds the table"          ((Ask $Port "SELECT COUNT(*) AS n FROM read_parquet('$one\dbo.orders.parquet')") -match 'n\s+Int64\s+24')

    $before = (Get-Item (Join-Path $one "dbo.orders.parquet")).LastWriteTimeUtc
    $again = Ask $Port "EXEC daffydb.save_parquet orders, '$one'"
    Check "a file already there is never replaced" ($again -match "SqlException 50307" -and $again -match "dbo\.orders\.parquet is already in")
    Check "and is left as it was"             ((Get-Item (Join-Path $one "dbo.orders.parquet")).LastWriteTimeUtc -eq $before)

    $named = Ask $Port "EXEC daffydb.save_parquet 'customers', '$one', 'people'"
    Check "@file names the file, .parquet added" ($named -match "info: Saved dbo\.customers to .*one\\people\.parquet: 8 rows")
    Check "@file with its extension keeps it" ((Ask $Port "EXEC daffydb.save_parquet 'customers', '$one', 'people2.parquet'") -match "one\\people2\.parquet: 8 rows")
    Check "@file is a name, not a path"       ((Ask $Port "EXEC daffydb.save_parquet 'customers', '$one', 'sub\x'") -match "SqlException 50302.*not a path")

    $temp = Ask $Port "CREATE TABLE #totals (n int); INSERT INTO #totals VALUES (1), (2), (3); EXEC daffydb.save_parquet #totals, '$one'"
    Check "a temp table saves, without its #" ($temp -match "info: Saved #totals to .*one\\totals\.parquet: 3 rows" -and $temp -match 'schema\s+String\s+NULL')
    Check "so does a global one, named in quotes" (
        (Ask $Port "CREATE TABLE ##grand (n int); INSERT INTO ##grand VALUES (7); EXEC daffydb.save_parquet '##grand', '$one'") -match "one\\grand\.parquet: 1 row")

    $mounted = Ask $Port "EXEC daffydb.mount '$work\extra', 'x'"
    Check "a second folder mounts into schema x" ($mounted -match "info: Mounted 2 tables")
    $rpc = Call $Port "daffydb.save_parquet" @("@table=[x].[Customer]", "@folder=$one")
    Check "called as a stored procedure, by a bracketed name" ($rpc -match "info: Saved x\.Customer to .*one\\x\.Customer\.parquet: 8 rows")

    Check "a table that is not there is named" ((Ask $Port "EXEC daffydb.save_parquet 'nope', '$one'") -match "SqlException 50306.*no table or view dbo\.nope")
    Check "so is a temp table, with the ones there are" ((Ask $Port "EXEC daffydb.save_parquet '#nope', '$one'") -match "SqlException 50306.*no temp table #nope.*#totals")
    Check "only daffydb's tables are saved"   ((Ask $Port "EXEC daffydb.save_parquet 'master.sys.databases', '$one'") -match "SqlException 50306.*only daffydb")
    Check "a folder that is a file is refused" ((Ask $Port "EXEC daffydb.save_parquet 'orders', '$one\dbo.orders.parquet'") -match "SqlException 50308.*is a file, not a folder")
    Check "a missing argument shows the usage" ((Ask $Port "EXEC daffydb.save_parquet 'orders'") -match "SqlException 50302.*needs @folder")

    # --- everything ----------------------------------------------------------------------------
    $shared = Ask $Port "CREATE TABLE #grand (n int); EXEC daffydb.save_parquet_all '$all'"
    Check "two tables that would share a file are refused" ($shared -match "SqlException 50307" -and $shared -match "##grand and #grand would all be grand\.parquet")
    Check "and nothing is written"            ((Test-Path $all) -eq $false)

    [void](Ask $Port "DROP TABLE #grand; CREATE VIEW dbo.[a:b] AS SELECT 1 AS x")
    $gone = Ask $Port "EXEC daffydb.mount '$work\gone\vanished.csv', 'gone'"
    Check "a file to lose mounts"             ($gone -match "info: Mounted 1 table")
    Remove-Item (Join-Path $work "gone\vanished.csv")

    $everything = Ask $Port "EXEC daffydb.save_parquet_all '$all'"
    Check "save_parquet_all saves every table, view and temp table" ($everything -match "info: Saved 8 tables to .*\\all, .* in all\. 1 table could not be saved\.")
    Check "a view made by --init is among them" (Test-Path (Join-Path $all "dbo.legacy.parquet"))
    Check "so are temp tables, without the #" ((Test-Path (Join-Path $all "totals.parquet")) -and (Test-Path (Join-Path $all "grand.parquet")))
    Check "a name no file can have is put right, and said" ((Test-Path (Join-Path $all "dbo.a_b.parquet")) -and
                                                            $everything -match "info: dbo\.a:b is saved as dbo\.a_b\.parquet, because ':' cannot be in a file name")
    Check "a table that cannot be read is left out, and said" ($everything -match "info: Left out gone\.vanished, which could not be read" -and
                                                               $everything -match 'status\s+String\s+"left out: it could not be read')
    Check "and is not written"                (-not (Test-Path (Join-Path $all "gone.vanished.parquet")))
    Check "no half-written file is left anywhere" (@(Get-ChildItem $work -Recurse -Filter "*.saving").Count -eq 0)

    $mountOnly = Ask $Port "EXEC daffydb.save_parquet_all '$work\onlyx', @mount = 'x'"
    Check "@mount saves just that mount's tables" ($mountOnly -match "info: Saved 2 tables" -and
                                                   (@(Get-ChildItem (Join-Path $work "onlyx")).Name -join ",") -eq "x.Customer.parquet,x.products.parquet")
    Check "an unknown mount is named, with the ones there are" (
        (Ask $Port "EXEC daffydb.save_parquet_all '$work\none', @mount = 'nope'") -match "SqlException 50306.*no mount named 'nope'.*'x'")

    # --- a big save, with another client querying meanwhile ------------------------------------
    # The big tables are temp tables, so they are saved last, after everything else, and a query
    # sent once the first table is written lands while they are being written.
    [void](Run $Port ("CREATE TABLE #big1 (n bigint, s varchar(20)); INSERT INTO #big1 SELECT range, CAST(range AS VARCHAR) FROM range(4000000); " +
               "CREATE TABLE #big2 (n bigint, s varchar(20)); INSERT INTO #big2 SELECT * FROM #big1; " +
               "CREATE TABLE #big3 (n bigint, s varchar(20)); INSERT INTO #big3 SELECT * FROM #big1"))

    $job = Start-Job -ArgumentList $Port, "$work\bigall" -ScriptBlock {
        param($port, $folder)
        $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:127.0.0.1,$port;Database=daffydb;User ID=v;Password=v;Encrypt=false;Pooling=false"
        $cn.Open()
        $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 120
        $cmd.CommandText = "EXEC daffydb.save_parquet_all '$folder'"
        try { [void]$cmd.ExecuteNonQuery(); "done" } catch { "error: $($_.Exception.Message)" }
        $cn.Close()
        (Get-Date).ToUniversalTime().Ticks
    }

    # Once the first table is written, ask something small of the server.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Select-String -Path $openLog -Pattern "saved .*bigall" -Quiet)) { Start-Sleep -Milliseconds 50 }
    $cn = New-Object System.Data.SqlClient.SqlConnection "Server=tcp:127.0.0.1,$Port;Database=daffydb;User ID=v;Password=v;Encrypt=false;Pooling=false"
    $cn.Open(); $cmd = $cn.CreateCommand(); $cmd.CommandText = "SELECT COUNT(*) FROM orders"; $cmd.CommandTimeout = 120
    $count = $cmd.ExecuteScalar(); $answered = (Get-Date).ToUniversalTime().Ticks; $cn.Close()

    [void]($job | Wait-Job -Timeout 120)
    $result = Receive-Job $job; Remove-Job $job
    Check "save_parquet_all of big tables completes" ($result[0] -eq "done")
    Check "another client is answered before it finishes" ($count -eq 24 -and $answered -lt [long]$result[1])
    Check "and every big table is whole"      ((Ask $Port "SELECT COUNT(*) AS n FROM read_parquet('$work\bigall\big*.parquet')") -match 'n\s+Int64\s+12000000')
    [void](Run $Port "DROP TABLE #big1; DROP TABLE #big2; DROP TABLE #big3")

    # --- a cancelled save ------------------------------------------------------------------------
    # A view that takes many seconds to read and has one row, so the cancel lands mid-COPY.
    [void](Run $Port "CREATE VIEW dbo.slow AS SELECT COUNT(*) AS n FROM range(60000) a, range(60000) b WHERE (a.range + b.range) % 7 = 3")
    $cancelled = Run $Port "EXEC daffydb.save_parquet 'slow', '$work\cancelled'" 1
    Start-Sleep -Seconds 1
    Check "a save can be cancelled"           ($cancelled -match "Timeout")
    Check "and leaves no file, whole or half-written" (@(Get-ChildItem (Join-Path $work "cancelled") -ErrorAction SilentlyContinue).Count -eq 0)
    Check "and the server answers straight after" ((Run $Port "SELECT COUNT(*) FROM orders" 5) -eq "done")
    [void](Run $Port "DROP VIEW dbo.slow")

    # --- what was saved comes back under its own names -----------------------------------------
    Remove-Item (Join-Path $all "dbo.a_b.parquet")   # a table named a_b, not a:b, so it could not match
    $back = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $backLog `
        -ArgumentList "--port $($Port + 2) --data-dir `"$all`" --no-repl"
    Start-Sleep -Seconds 5
    Check "a server on the saved folder serves dbo.orders" ((Ask ($Port + 2) "SELECT COUNT(*) AS n FROM dbo.orders") -match 'n\s+Int\d+\s+24')
    Check "x.Customer in its own schema"      ((Ask ($Port + 2) "SELECT COUNT(*) AS n FROM x.Customer") -match 'n\s+Int\d+\s+8')
    Check "the --init view's rows"            ((Ask ($Port + 2) "SELECT x FROM dbo.legacy") -match 'x\s+Int32\s+1')
    Check "and a temp table's, in dbo"        ((Ask ($Port + 2) "SELECT COUNT(*) AS n FROM dbo.totals") -match 'n\s+Int\d+\s+3')
}
finally {
    foreach ($p in @($open, $shut, $back)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
}

$opened = Get-Content $openLog -Raw
Check "every save is logged"                ($opened -match "\[admin\]  session \d+ .*daffydb\.save_parquet @table='dbo\.orders' @folder=" -and
                                             $opened -match "\[admin\]  session \d+ .*daffydb\.save_parquet_all @folder=")
Check "and every table it writes"           ($opened -match "\[duckdb\] saved dbo\.orders\s+-> .*dbo\.orders\.parquet, 24 row")
Check "a refused one is logged too"         ((Get-Content $shutLog -Raw) -match "\[admin\]  refused session \d+ .*daffydb\.save_parquet")

Remove-Item ($Port, ($Port + 1), ($Port + 2) | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$_.pbids" }) `
    -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Pop-Location

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "parquet checks passed"; exit 0 } else { Write-Output "$failed check(s) failed"; exit 1 }
