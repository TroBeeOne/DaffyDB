# Checks DaffyDB's own commands, EXEC daffydb.*: help, status and tables for everyone, and mount,
# duckdb and unmount behind --allow-admin.
#
# Starts one server with the flag and one without, both serving a small folder, and an --init
# script on the first that makes a view a mount must not replace. Mounts a second folder into it -
# refused when a name is taken, then into a schema of its own - reads it again after a file comes
# and another goes, mounts single files, calls a command as a stored procedure the way application
# code does, and unmounts. Mounts a DuckDB file it writes through sp_duckdb, whole and a table at a
# time. Then checks the log for every admin command and for no password, and that daffydb's own
# prompt may mount without the flag. Nothing here touches the network: DuckDB reads its own files
# without an extension. verify-databases.ps1 covers SQLite, PostgreSQL and MySQL.
param([int]$Port = 14500)

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

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-admin-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
foreach ($folder in "data", "extra\Person", "single") { New-Item -ItemType Directory -Path (Join-Path $work $folder) -Force | Out-Null }

# What the server starts with.
Copy-Item "data/customers.parquet" (Join-Path $work "data\customers.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "data\orders.parquet")

# What gets mounted later: one name already taken (orders), a dotted name, a schema folder, a CSV
# and a workbook.
Copy-Item "data/orders.parquet"    (Join-Path $work "extra\orders.parquet")
Copy-Item "data/customers.parquet" (Join-Path $work "extra\Sales.Customer.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "extra\Person\Address.parquet")
Copy-Item "scripts/fixtures/Budget.xlsx" (Join-Path $work "extra\Budget.xlsx")
@("product_id,name", "1,Widget", "2,Gadget") | Set-Content -Path (Join-Path $work "extra\products.csv") -Encoding ascii

@("note_id,text", "1,first", "2,second", "3,third") | Set-Content -Path (Join-Path $work "single\notes.csv") -Encoding ascii
@("x", "1") | Set-Content -Path (Join-Path $work "single\legacy.csv") -Encoding ascii

# A view the init script makes, which no mount may replace, and a password in a statement, which
# no log may show.
@("CREATE VIEW dbo.legacy AS SELECT 1 AS x;",
  "CREATE VIEW dbo.secretive AS SELECT 'password=hunter3' AS note;") |
    Set-Content -Path (Join-Path $work "startup.sql") -Encoding ascii

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
$openLog = Join-Path $work "open.log"
$shutLog = Join-Path $work "shut.log"

$open = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $openLog `
    -ArgumentList "--port $Port --data-dir `"$work\data`" --init `"$work\startup.sql`" --allow-admin --allow-passthrough --no-repl"
$shut = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $shutLog `
    -ArgumentList "--port $($Port + 1) --data-dir `"$work\data`" --no-repl"

try {
    Start-Sleep -Seconds 5

    # --- open to everyone, flag or not -------------------------------------------------------
    $help = Ask ($Port + 1) "EXEC daffydb.help"
    Check "help lists the commands without --allow-admin" ($help -match '"daffydb\.mount"' -and $help -match '"daffydb\.unmount"' -and
                                                           $help -match '"daffydb\.status"')
    Check "help marks which need an admin"   ($help -match '"admin"' -and $help -match '"everyone"')
    Check "help for one command shows its usage" ((Ask ($Port + 1) "daffydb.help mount") -match 'EXEC daffydb\.mount @path \[, @schema\]')

    $status = Ask ($Port + 1) "EXEC daffydb.status"
    Check "status works without --allow-admin" ($status -match '"admin commands"' -and $status -match 'off for clients')
    Check "status names the data folder's mount" ($status -match "mount 'data', 2 tables")
    Check "status counts the session asking"   ($status -match '"[1-9]\d* connected"')

    $tables = Ask ($Port + 1) "EXEC daffydb.tables"
    Check "tables says where a table comes from" ($tables -match '"parquet"' -and $tables -match 'data/orders\.parquet"' -and
                                                   $tables -match 'mount\s+String\s+"data"')

    $refused = Ask ($Port + 1) "EXEC daffydb.mount '$work\extra', 'x'"
    Check "mount is refused without --allow-admin" ($refused -match "SqlException 50301" -and $refused -match "--allow-admin")

    # --- the admin server --------------------------------------------------------------------
    $tables = Ask $Port "EXEC daffydb.tables"
    Check "an --init view has no mount"       ($tables -match '"legacy"' -and $tables -match 'source\s+String\s+NULL')

    $clash = Ask $Port "EXEC daffydb.mount '$work\extra'"
    Check "a mount whose names are taken is refused" ($clash -match "SqlException 50304" -and $clash -match "dbo\.orders is taken by mount 'data'")
    Check "and nothing of it is mounted"     ((Ask $Port "SELECT COUNT(*) AS n FROM sys.objects WHERE name = 'Customer'") -match 'n\s+Int\d+\s+0')

    $mounted = Ask $Port "EXEC daffydb.mount '$work\extra', 'x'"
    Check "with @schema it mounts, named after the schema" ($mounted -match "info: Mounted \d+ tables from .*extra as 'x'")
    Check "every table goes in that schema"   ((Ask $Port "SELECT COUNT(*) AS n FROM x.Customer") -match 'n\s+Int\d+\s+8')
    Check "a workbook's sheets go there too" ((Ask $Port "SELECT COUNT(*) AS n FROM x.Q1") -match 'n\s+Int\d+\s+\d')
    Check "and a skipped sheet is a message"  ($mounted -match "info: note: .*Budget\.xlsx \[Report\]")
    Check "the result lists what was mounted" ($mounted -match '"Address"' -and $mounted -match '"products"' -and $mounted -match 'mount\s+String\s+"x"')

    Remove-Item (Join-Path $work "extra\products.csv")
    Copy-Item "data/customers.parquet" (Join-Path $work "extra\newone.parquet")
    $again = Ask $Port "EXEC daffydb.mount '$work\extra', 'x'"
    Check "mounting it again reads it again"  ($again -match "info: Read .*extra again as 'x'")
    Check "a new file appears"                ($again -match '"newone"')
    Check "a file that went goes"             ((Ask $Port "SELECT COUNT(*) AS n FROM sys.objects WHERE name = 'products'") -match 'n\s+Int\d+\s+0')

    $single = Ask $Port "USE master; EXEC daffydb.mount '$work\single\notes.csv', staging; SELECT DB_NAME() AS db"
    Check "a single file mounts, from any database" ($single -match "info: Mounted 1 table from .*notes\.csv as 'staging'")
    Check "into daffydb, under a bare-word schema" ((Ask $Port "SELECT COUNT(*) AS n FROM daffydb.staging.notes") -match 'n\s+Int\d+\s+3')
    Check "and the caller stays where it was"  ($single -match 'db\s+String\s+"master"')

    $legacy = Ask $Port "EXEC daffydb.mount '$work\single\legacy.csv'"
    Check "a view made by --init is not replaced" ($legacy -match "SqlException 50304" -and $legacy -match "made by --init")

    $data = Ask $Port "EXEC daffydb.mount '$work\data'"
    Check "the --data-dir folder can be read again" ($data -match "again as 'data'")

    $rpc = Call $Port "daffydb.mount" @("@path=$work\single\legacy.csv", "@schema=old")
    Check "a command can be called as a stored procedure" ($rpc -match "info: Mounted 1 table from .*legacy\.csv as 'old'")
    Check "by any spelling of its name" ((Call $Port "[daffydb].[help]" @("@command=unmount")) -match 'EXEC daffydb\.unmount @mount')

    Check "an unknown command is named"       ((Ask $Port "EXEC daffydb.nosuch") -match "SqlException 50302.*no command daffydb\.nosuch")
    Check "a missing argument shows the usage" ((Ask $Port "EXEC daffydb.mount") -match "SqlException 50302.*needs @path.*Usage: EXEC daffydb\.mount")
    Check "too many arguments are refused"    ((Ask $Port "EXEC daffydb.unmount 'a', 'b'") -match "SqlException 50302")
    Check "an unknown parameter is named"     ((Ask $Port "EXEC daffydb.mount @folder = 'a'") -match "SqlException 50302.*@folder is not a parameter")
    Check "DaffyDB's own schemas are refused" ((Ask $Port "EXEC daffydb.mount '$work\single', 'sys'") -match "SqlException 50302.*cannot be sys")
    Check "a path that is not there says so"  ((Ask $Port "EXEC daffydb.mount '$work\nowhere'") -match "SqlException 50303.*no file or folder")
    Check "a file DaffyDB cannot read says so" ((Ask $Port "EXEC daffydb.mount '$work\startup.sql'") -match "SqlException 50302.*not a file DaffyDB reads")

    $intoX = Ask $Port "EXEC daffydb.mount '$work\single\notes.csv'"
    Check "a file mounted without @schema is named after itself" ($intoX -match "info: Mounted 1 table from .*notes\.csv as 'notes'")
    Check "the same file in another schema is a mount of its own" ((Ask $Port "SELECT COUNT(*) AS n FROM dbo.notes") -match 'n\s+Int\d+\s+3' -and
                                                            (Ask $Port "SELECT COUNT(*) AS n FROM staging.notes") -match 'n\s+Int\d+\s+3')

    $unmount = Ask $Port "EXEC daffydb.unmount x; SELECT COUNT(*) AS n FROM sys.schemas WHERE name = 'x'"
    Check "unmount takes a mount's tables away" ($unmount -match "info: Unmounted 'x': \d+ tables taken away")
    Check "and the schema it made"            ($unmount -match 'n\s+Int\d+\s+0')
    Check "an unknown mount is named, with the ones there are" (
        (Ask $Port "EXEC daffydb.unmount nope") -match "SqlException 50306.*no mount named 'nope'.*'data'")

    $masked = Ask $Port "EXEC daffydb.mount 'Z:\nowhere\password=hunter2'"
    Check "a password in an error is masked"  ($masked -match "password=\*\*\*" -and $masked -notmatch "hunter2")

    # --- a DuckDB file: no extension needed ---------------------------------------------------
    # Written through sp_duckdb: a table that will clash with dbo.orders, a schema of its own, a
    # view written inside the file, and one naming the file by the alias it was written under,
    # which no other attachment of it can resolve.
    $duck = Join-Path $work "warehouse.duckdb"
    $write = Ask $Port ("EXEC sp_duckdb N'ATTACH ''$duck'' AS w; CREATE TABLE w.main.orders AS SELECT * FROM daffydb.dbo.orders; " +
                        "CREATE SCHEMA w.sales; CREATE TABLE w.sales.regions AS SELECT * FROM (VALUES (1, ''North''), (2, ''South'')) t(id, name); " +
                        "USE w; CREATE VIEW big_orders AS SELECT * FROM orders WHERE quantity > 2; " +
                        "CREATE VIEW broken AS SELECT * FROM w.main.orders; USE daffydb; DETACH w'")
    Check "a DuckDB file can be written to mount" ($write -match "PASS ad-hoc")

    Check "a DuckDB file's main schema clashes as dbo" ((Ask $Port "EXEC daffydb.duckdb '$duck'") -match "SqlException 50304.*dbo\.orders is taken by mount 'data'")

    $whole = Ask $Port "EXEC daffydb.duckdb '$duck', @schema = 'wh'"
    Check "a DuckDB file mounts into a schema"   ($whole -match "info: Mounted 3 tables from .*warehouse\.duckdb as 'wh'")
    Check "and a view it cannot read is left out" ($whole -match "info: Left out main\.broken")
    Check "its tables and views read"            ((Ask $Port "SELECT COUNT(*) AS n FROM wh.orders") -match 'n\s+Int\d+\s+24' -and
                                                   (Ask $Port "SELECT name FROM wh.regions WHERE id = 2") -match '"South"' -and
                                                   (Ask $Port "SELECT COUNT(*) AS n FROM wh.big_orders") -match 'n\s+Int\d+\s+\d')

    $one = Ask $Port "EXEC daffydb.duckdb '$duck', 'sales.regions'"
    Check "one table keeps its own schema"       ($one -match "info: Mounted 1 table from .*warehouse\.duckdb as 'warehouse'" -and $one -match 'schema\s+String\s+"sales"')
    $two = Ask $Port "EXEC daffydb.duckdb '$duck', 'big_orders'"
    Check "and another is added to the same mount, main as dbo" ($two -match "info: Added big_orders to 'warehouse', which now has 2 tables" -and
                                                                 $two -match 'schema\s+String\s+"dbo"')
    Check "a table asked for by name must be readable" ((Ask $Port "EXEC daffydb.duckdb '$duck', 'broken'") -match "SqlException 50303.*main\.broken")
    Check "a table that is not there is named, with what is" ((Ask $Port "EXEC daffydb.duckdb '$duck', 'nope'") -match "SqlException 50306.*no table nope.*sales\.regions")

    $shared = Ask $Port "EXEC daffydb.unmount wh; SELECT COUNT(*) AS n FROM sales.regions"
    Check "two mounts of one file share it, and one unmount leaves the other" ($shared -match "info: Unmounted 'wh'" -and $shared -match 'n\s+Int\d+\s+2')

    $gone = Ask $Port "EXEC daffydb.unmount warehouse; SELECT COUNT(*) AS n FROM duckdb_databases() WHERE database_name LIKE 'daffy%' AND database_name <> 'daffydb'"
    Check "the last unmount lets the file go"    ($gone -match "info: Unmounted 'warehouse'" -and $gone -match 'n\s+Int\d+\s+0')

    $missing = Ask $Port "EXEC daffydb.duckdb '$work\nothing.duckdb'"
    Check "a database file that is not there says so" ($missing -match "SqlException 50303.*no file at")
    Check "and is not created"                    (-not (Test-Path (Join-Path $work "nothing.duckdb")))
}
finally {
    foreach ($p in @($open, $shut)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
}

$opened = Get-Content $openLog -Raw
$closed = Get-Content $shutLog -Raw
Check "the banner says admin commands are on" ($opened -match "Admin commands\s+: ON")
Check "and suggests the flag when they are not" ($closed -match "--allow-admin\s+let clients mount")
Check "every admin command is logged"       ($opened -match "\[admin\]  session \d+ .*daffydb\.mount @path=" -and
                                              $opened -match "\[admin\]  session \d+ .*daffydb\.unmount @mount='x'")
Check "and so is a refused one"             ($closed -match "\[admin\]  refused session \d+ .*daffydb\.mount")
Check "no password reaches the log"         ($opened -notmatch "hunter2" -and $opened -notmatch "hunter3" -and $opened -match "password=\*\*\*")

# daffydb's own prompt was started by whoever is at it, so it needs no flag.
$typed = @("EXEC daffydb.mount '$work\single\notes.csv', z;", ":quit") -join "`n"
$prompt = $typed | & $exe --repl --port ($Port + 2) --data-dir "$work\data" 2>&1 | Out-String
Check "the prompt may mount without --allow-admin" ($prompt -match "Mounted 1 table from .*notes\.csv")

Remove-Item ($Port, ($Port + 1), ($Port + 2) | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$_.pbids" }) `
    -ErrorAction SilentlyContinue
Pop-Location

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "admin command checks passed"; exit 0 } else { Write-Output "$failed check(s) failed"; exit 1 }
