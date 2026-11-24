# Checks that a directory of data files becomes a database, and that an --init script runs.
#
# Builds a throwaway directory from the seeded parquet files plus hand-written CSV and JSON, some of
# it gzipped and some of it where the scan should not look, arranged to exercise every naming and
# skipping rule at once, with tables written as folders of parts in each tool's naming. Then starts
# a proxy on its own port and queries it as a client would, and finally scans a few single folders:
# one with too many files to mount, and two named with a separator on the end.
param([int]$Port = 14435)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $condition) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name" }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-mount-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $work "Person") -Force | Out-Null

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

# One file per naming rule: bare name, dotted name, and a subdirectory.
Copy-Item "data/customers.parquet" (Join-Path $work "customers.parquet")
Copy-Item "data/customers.parquet" (Join-Path $work "Sales.Customer.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "Person/Address.parquet")

# CSV mounts by the same rules, with DuckDB sniffing the column types. Written as a string array
# rather than a here-string: Set-Content ends every element the same way, where a here-string
# keeps its own newlines and appends one more, and the mixed line endings that produces are
# themselves enough to defeat the sniffer.
@("product_id,name,price,in_stock", "1,Widget,9.99,true", "2,Gadget,24.50,false") |
    Set-Content -Path (Join-Path $work "products.csv") -Encoding ascii

# Same name as an existing parquet file. Parquet carries its own schema, so it must win.
@("customer_id,name", "999,Should Not Appear") |
    Set-Content -Path (Join-Path $work "customers.csv") -Encoding ascii

# Not parquet at all, despite the name. One unreadable file must cost its own table and nothing
# else. A junk .csv is no good as the example here - DuckDB's sniffer will read almost any text
# as a single-column CSV, which is arguably the right answer - but junk parquet has no such out.
@("this is not a parquet file") |
    Set-Content -Path (Join-Path $work "broken.parquet") -Encoding ascii

# JSON in each layout and under each name DuckDB reads it by: one array, and one object per line.
# A nested object comes back as text, since TDS has no struct type.
@('[{"id":1,"name":"launch","meta":{"by":"ada"}},', ' {"id":2,"name":"landing","meta":{"by":"grace"}}]') |
    Set-Content -Path (Join-Path $work "events.json") -Encoding ascii
@('{"page":"home","ms":120}', '{"page":"about","ms":80}') |
    Set-Content -Path (Join-Path $work "clicks.jsonl") -Encoding ascii
@('{"company":"Acme","stage":"won"}') |
    Set-Content -Path (Join-Path $work "Sales.leads.ndjson") -Encoding ascii

# Compressed files are named by what is inside: archive.csv.gz is archive, not a table named csv
# in a schema named archive.
function Write-Gzip($path, $lines) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes(($lines -join "`n") + "`n")
    $file = [System.IO.File]::Create($path)
    $gzip = New-Object System.IO.Compression.GZipStream($file, [System.IO.Compression.CompressionMode]::Compress)
    $gzip.Write($bytes, 0, $bytes.Length)
    $gzip.Dispose(); $file.Dispose()
}
Write-Gzip (Join-Path $work "archive.csv.gz") @("region,total", "north,10", "south,20")
Write-Gzip (Join-Path $work "logs.json.gz") @('{"level":"warn","text":"disk"}')

# Same name as products.csv. A csv wins over a json, as a parquet wins over a csv.
@('[{"product_id":999,"name":"Json Should Not Appear"}]') |
    Set-Content -Path (Join-Path $work "products.json") -Encoding ascii

# Unlike a junk csv, junk JSON has no reading that works, so it must be skipped.
@('{"id":1, "name": ') | Set-Content -Path (Join-Path $work "broken.json") -Encoding ascii

# Configuration that happens to be JSON is not data, and neither is anything in build output,
# tooling or hidden folders.
New-Item -ItemType Directory -Force -Path (Join-Path $work "Properties"), (Join-Path $work "bin\Debug"),
    (Join-Path $work "obj"), (Join-Path $work "node_modules\lib"), (Join-Path $work ".git"),
    (Join-Path $work "stash"), (Join-Path $work "Region\West\Deep") | Out-Null
foreach ($config in "appsettings.json", "appsettings.Development.json", "package.json", "package-lock.json",
                    "Properties\launchSettings.json") {
    @('{"logging":{"level":"info"}}') | Set-Content -Path (Join-Path $work $config) -Encoding ascii
}
foreach ($stray in "bin\Debug\binstuff.csv", "obj\objstuff.csv", "node_modules\lib\nmstuff.json",
                   ".git\gitstuff.csv", "stash\stashstuff.csv") {
    @("a,b", "1,2") | Set-Content -Path (Join-Path $work $stray) -Encoding ascii
}
$stash = Get-Item (Join-Path $work "stash")
$stash.Attributes = $stash.Attributes -bor [System.IO.FileAttributes]::Hidden

# Two folders down is scanned, and named by its nearest folder; three is not.
@("a,b", "1,2") | Set-Content -Path (Join-Path $work "Region\West\shallow.csv") -Encoding ascii
@("a,b", "1,2") | Set-Content -Path (Join-Path $work "Region\West\Deep\toodeep.csv") -Encoding ascii

# Workbooks, from scripts/fixtures (make-workbooks.py there says what each sheet is for). A
# workbook is a schema whatever folder it is in and however many dots its name has.
$fixtures = Join-Path $root "scripts\fixtures"
New-Item -ItemType Directory -Force -Path (Join-Path $work "Finance") | Out-Null
Copy-Item (Join-Path $fixtures "Budget.xlsx") $work
Copy-Item (Join-Path $fixtures "Macro.xlsm") $work
Copy-Item (Join-Path $fixtures "Plan.2024.xlsx") (Join-Path $work "Finance")

# Found before the workbook, so it keeps the name Budget.Clash over the sheet of that name.
@("source", "from csv") | Set-Content -Path (Join-Path $work "Budget.Clash.csv") -Encoding ascii

# Not a workbook, whatever the name says; and the lock file Excel leaves beside an open one.
@("this is not a workbook") | Set-Content -Path (Join-Path $work "broken.xlsx") -Encoding ascii
@("owner") | Set-Content -LiteralPath (Join-Path $work '~$Budget.xlsx') -Encoding ascii

# Tables written as a folder of parts, the way sqlcarbon, Spark, pyarrow and DuckDB write a table
# too big for one file. Copied with System.IO rather than Copy-Item, which reads [1] in a path as
# a wildcard.
$orders = Join-Path $root "data\orders.parquet"          # 24 rows
$customers = Join-Path $root "data\customers.parquet"    # 8 rows
function Write-Parts($folder, [string[]]$names, $source = $orders) {
    $dir = [System.IO.Directory]::CreateDirectory((Join-Path $work $folder)).FullName
    foreach ($name in $names) { [System.IO.File]::Copy($source, (Join-Path $dir $name), $true) }
}
function Set-Written($relative, $when) { [System.IO.File]::SetLastWriteTime((Join-Path $work $relative), $when) }

Write-Parts "FactInv" "part-00000.parquet", "part-00001.parquet"
[System.IO.File]::WriteAllText((Join-Path $work "FactInv\_SUCCESS"), "")
Write-Parts "Warehouse\Stock" "data_0.parquet", "data_1.parquet", "data_2.parquet"
Write-Parts "Warehouse" "Bins.parquet" $customers
Write-Parts "Sales.Returns" "part-00000-1f2e3d4c-5b6a-4789-8a9b-0c1d2e3f4a5b-c000.snappy.parquet"

# The marker makes a folder of parquet one table whatever the files are called; without it, a
# folder that mixes parts with other files is files, and part- with no number is not a part.
Write-Parts "Marked" "alpha.parquet", "beta.parquet"
[System.IO.File]::WriteAllText((Join-Path $work "Marked\_SUCCESS"), "")
Write-Parts "Mixed" "part-00000.parquet", "notes.parquet"
Write-Parts "Inventory" "part-numbers.parquet"

# A table exported once as a file and again as a folder leaves both; the newer one is served.
$old = (Get-Date).AddDays(-2)
Write-Parts "." "Stale.parquet" $customers;           Set-Written "Stale.parquet" $old
Write-Parts "Stale" "part-00000.parquet";             Set-Written "Stale\part-00000.parquet" (Get-Date)
Write-Parts "." "Fresh.parquet";                      Set-Written "Fresh.parquet" (Get-Date)
Write-Parts "Fresh" "part-00000.parquet" $customers;  Set-Written "Fresh\part-00000.parquet" $old

# More parts than the 500-file limit, which a folder of parts counts as one; and a folder whose
# name DuckDB would read as a glob.
Write-Parts "Bulk" (0..500 | ForEach-Object { "part-{0:D5}.parquet" -f $_ })
Write-Parts "Odd[1]" "part-00000.parquet", "part-00001.parquet"

@'
CREATE SCHEMA IF NOT EXISTS reporting;

CREATE OR REPLACE VIEW reporting.big_spenders AS
SELECT c.name, SUM(o.quantity * o.unit_price) AS total
FROM daffydb."Person"."Address" o
JOIN daffydb.dbo.customers c ON c.customer_id = o.customer_id
GROUP BY c.name
HAVING SUM(o.quantity * o.unit_price) > 1000;
'@ | Set-Content -Path (Join-Path $work "startup.sql") -Encoding utf8

# Held open for writing, sharing only reads - the way Excel holds a workbook it has open.
$held = [System.IO.File]::Open((Join-Path $work "Finance\Plan.2024.xlsx"), "Open", "ReadWrite", "Read")

$log = Join-Path $work "proxy.log"
$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir `"$work`" --init `"$work\startup.sql`""

try {
    Start-Sleep -Seconds 6

    $catalog = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT s.name AS [schema], o.name AS [table] FROM daffydb.sys.objects o JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id ORDER BY s.name, o.name" 2>&1 | Out-String

    Check "a bare file name lands in dbo"            ($catalog -match '"dbo"')
    Check "a dotted file name becomes a schema"      ($catalog -match '"Sales"' -and $catalog -match '"Customer"')
    Check "a subdirectory becomes a schema"          ($catalog -match '"Person"' -and $catalog -match '"Address"')
    Check "an init-script view reaches the catalog"  ($catalog -match '"big_spenders"')
    Check "a csv file mounts alongside parquet"      ($catalog -match '"products"')
    Check "an unreadable file is left out"           ($catalog -notmatch '"broken"')
    Check "json, jsonl and ndjson files mount"       ($catalog -match '"events"' -and $catalog -match '"clicks"' -and
                                                      $catalog -match '"leads"')
    Check "a .gz file is named without its extensions" ($catalog -match '"archive"' -and $catalog -match '"logs"' -and
                                                        $catalog -notmatch '"csv"' -and $catalog -notmatch '"json"')
    Check "configuration json is not mounted"        ($catalog -notmatch '"(appsettings|Development|package|package-lock|launchSettings)"')
    Check "bin, obj, node_modules and dot-folders are not scanned" (
                                                      $catalog -notmatch '"(binstuff|objstuff|nmstuff|gitstuff)"')
    Check "a hidden folder is not scanned"           ($catalog -notmatch '"stashstuff"')
    Check "two folders down is scanned"              ($catalog -match '"West"' -and $catalog -match '"shallow"')
    Check "three folders down is not"                ($catalog -notmatch '"toodeep"')
    Check "a workbook's sheets load into a schema of its name" (
                                                      $catalog -match '"Budget"' -and $catalog -match '"Q1"' -and
                                                      $catalog -match '"Summary Q1"' -and $catalog -match '"NoHeader"')
    Check "report, empty, hidden and chart sheets do not" ($catalog -notmatch '"(Report|Empty|Lookup|Chart)"')
    Check "a dotted workbook in a folder is still its own schema" (
                                                      $catalog -match '"Plan\.2024"' -and $catalog -match '"Targets"' -and
                                                      $catalog -notmatch '"Finance"')
    Check "a workbook held open elsewhere still loads" ($catalog -match '"Targets"')
    Check "an .xlsm workbook loads"                  ($catalog -match '"Macro"' -and $catalog -match '"Data"')

    $csv = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT name, price FROM products ORDER BY product_id" 2>&1 | Out-String
    Check "T-SQL can query a csv mount"              ($csv -match "Widget" -and $csv -match "9.99")

    $clash = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT name FROM customers WHERE customer_id = 999" 2>&1 | Out-String
    Check "parquet wins a name clash with csv"       ($clash -notmatch "Should Not Appear")

    $jsonClash = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT name FROM products WHERE product_id = 999" 2>&1 | Out-String
    Check "csv wins a name clash with json"          ($jsonClash -notmatch "Json Should Not Appear")

    $json = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT e.id + 1 AS next_id, e.meta, c.page, l.company FROM events e, clicks c, Sales.leads l WHERE e.name = 'launch' AND c.ms = 120" 2>&1 | Out-String
    Check "T-SQL can query json mounts, nested values as text" (
                                                      $json -match '\b2\b' -and $json -match "ada" -and
                                                      $json -match "home" -and $json -match "Acme")

    $gz = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT a.total, g.text FROM archive a, logs g WHERE a.region = 'south'" 2>&1 | Out-String
    Check "T-SQL can query .csv.gz and .json.gz mounts" ($gz -match "20" -and $gz -match "disk")

    # Every column is typed from all 600 rows, and the rows that decide it are past row 500.
    $types = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'Budget' AND TABLE_NAME = 'Q1' ORDER BY ORDINAL_POSITION" 2>&1 | Out-String
    Check "a sheet's columns are typed from every row" (
                                                      $types -match '"amount"\s+\S+\s+\S+\s+"float"' -and
                                                      $types -match '"units"\s+\S+\s+\S+\s+"bigint"' -and
                                                      $types -match '"booked"\s+\S+\s+\S+\s+"datetime2"' -and
                                                      $types -match '"note"\s+\S+\s+\S+\s+"nvarchar"' -and
                                                      $types -match '"active"\s+\S+\s+\S+\s+"bit"' -and
                                                      $types -match '"start"\s+\S+\s+\S+\s+"time"')

    $late = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT units, amount, note, CAST(booked AS varchar(30)) AS booked FROM Budget.Q1 WHERE units IN (560, 580, 590) ORDER BY units" 2>&1 | Out-String
    Check "and no late value is rounded or lost"     ($late -match '12\.5' -and $late -match '"N/A"' -and
                                                      $late -match '2025-08-13 13:30:00')

    $count = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT COUNT(*) AS n FROM Budget.Q1" 2>&1 | Out-String
    Check "a row hidden by a filter is still loaded" ($count -match '\bn\s+\S+\s+600\b')

    $sheets = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT (SELECT total FROM [Budget].[Summary Q1] WHERE region = 'south') AS spaced, (SELECT column_B FROM Budget.NoHeader WHERE column_A = 2) AS lettered, (SELECT target FROM [Plan.2024].Targets) AS dotted, (SELECT value FROM Macro.Data) AS macro" 2>&1 | Out-String
    Check "T-SQL can query sheets by any of those names" ($sheets -match '\bspaced\s+\S+\s+200\b' -and $sheets -match '"beta"' -and
                                                      $sheets -match '\bdotted\s+\S+\s+1000\b' -and $sheets -match '\bmacro\s+\S+\s+42\b')

    $messy = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT COUNT(*) AS n, COUNT(amount) AS amounts, MAX(column_D) AS unlabelled, MAX(amount_column_E) AS second_amount, MAX([Total Cost]) AS wrapped FROM Budget.Messy" 2>&1 | Out-String
    Check "blank, repeated and wrapped headers get usable names" (
                                                      $messy -match '\bn\s+\S+\s+2\b' -and $messy -match '\bamounts\s+\S+\s+1\b' -and
                                                      $messy -match '"unlabelled"' -and $messy -match '\bsecond_amount\s+\S+\s+8\b' -and
                                                      $messy -match '\bwrapped\s+\S+\s+13\b')

    $sheetClash = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT source FROM Budget.Clash" 2>&1 | Out-String
    Check "a file found first wins a name clash with a sheet" ($sheetClash -match "from csv" -and $sheetClash -notmatch "from excel")

    $names = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT CONCAT(s.name, '.', o.name) AS qualified FROM daffydb.sys.objects o JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id ORDER BY 1" 2>&1 | Out-String
    Check "a folder of parts is one table, named after the folder" (
                                                      $names -match '"dbo\.FactInv"' -and $names -notmatch '"FactInv\.')
    Check "DuckDB's data_0 parts, inside a schema folder" ($names -match '"Warehouse\.Stock"' -and
                                                      $names -match '"Warehouse\.Bins"' -and $names -notmatch '"Stock\.')
    Check "Spark's part names, in a dotted folder"   ($names -match '"Sales\.Returns"')
    Check "_SUCCESS makes a folder of parquet one table" ($names -match '"dbo\.Marked"' -and $names -notmatch '"Marked\.')
    Check "a folder mixing parts and files is files" ($names -match '"Mixed\.part-00000"' -and $names -match '"Mixed\.notes"')
    Check "part- needs a number to be a part"        ($names -match '"Inventory\.part-numbers"')

    $parts = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT (SELECT COUNT(*) FROM FactInv) AS factinv, (SELECT COUNT(*) FROM Warehouse.Stock) AS stock, (SELECT COUNT(*) FROM Sales.Returns) AS spark, (SELECT COUNT(*) FROM Marked) AS marked, (SELECT COUNT(*) FROM Stale) AS stale, (SELECT COUNT(*) FROM Fresh) AS fresh, (SELECT COUNT(*) FROM Bulk) AS bulk, (SELECT COUNT(*) FROM [Odd[1]]]) AS odd" 2>&1 | Out-String
    Check "a folder of parts reads every part"       ($parts -match '\bfactinv\s+\S+\s+48\b' -and $parts -match '\bstock\s+\S+\s+72\b' -and
                                                      $parts -match '\bspark\s+\S+\s+24\b' -and $parts -match '\bmarked\s+\S+\s+48\b')
    Check "a newer folder of parts wins over an older file" ($parts -match '\bstale\s+\S+\s+24\b')
    Check "a newer file wins over an older folder of parts" ($parts -match '\bfresh\s+\S+\s+24\b')
    Check "501 parts count once toward the 500"      ($parts -match '\bbulk\s+\S+\s+12024\b')
    Check "a folder named with a glob character reads" ($parts -match '\bodd\s+\S+\s+48\b')

    # The tree Object Explorer draws: a mounted file is reported as a table even though DuckDB
    # holds it as a view, while a view written in an init script stays a view.
    $kinds = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT name, type_desc FROM daffydb.sys.objects ORDER BY name" 2>&1 | Out-String
    Check "a mounted file is catalogued as a table"  ($kinds -match '"products"[\s\S]{0,120}?"USER_TABLE"')
    Check "an init-script view stays a view"         ($kinds -match '"big_spenders"[\s\S]{0,120}?"VIEW"')

    $rows = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT TOP 3 [customer_id], [name] FROM [Sales].[Customer] ORDER BY [name]" 2>&1 | Out-String
    Check "T-SQL can query a schema-qualified mount" ($rows -match "Ada Lovelace")

    $view = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql `
        "SELECT name, total FROM reporting.big_spenders ORDER BY total DESC" 2>&1 | Out-String
    Check "T-SQL can query an init-script view"      ($view -match "Grace Hopper")

    # A broken init script must stop the server rather than serve half a database. Start-Process
    # with redirected streams, because Windows PowerShell turns a native command's stderr into a
    # terminating error and this run is *meant* to fail.
    "SELECT * FROM nothing_at_all;" | Set-Content -Path (Join-Path $work "bad.sql") -Encoding utf8
    $badOut = Join-Path $work "bad.out"
    $badErr = Join-Path $work "bad.err"
    $run = Start-Process -FilePath "dotnet" -PassThru -Wait -WindowStyle Hidden `
        -RedirectStandardOutput $badOut -RedirectStandardError $badErr `
        -ArgumentList "run --project src/TdsProxy --no-build -- --port $($Port + 1) --data-dir `"$work`" --init `"$work\bad.sql`""

    $bad = (Get-Content $badOut -Raw) + (Get-Content $badErr -Raw)
    Check "a failing init script refuses to start"   ($bad -match "init script failed at")
    Check "and exits non-zero"                       ($run.ExitCode -ne 0)

    # The two failures are deliberately not symmetric: a script the operator wrote is fatal, a
    # stray file in a scanned folder is not.
    $startup = Get-Content $log -Raw
    Check "an unreadable file only warns"            ($startup -match "could not be read; skipped")
    Check "and the server still comes up"            ($startup -match "531 data file" -and
                                                      $startup -match "3 skipped")
    Check "an unreadable workbook only warns"        ($startup -match "broken\.xlsx could not be read; skipped")
    Check "a report-shaped sheet is named in a note" ($startup -match "\[Report\] has values to the right of its header row")
    Check "Excel's lock file is not read at all"     ($startup -notmatch '~\$Budget')
    Check "the older of a file and a folder is named" ($startup -match 'Stale\.parquet would also be dbo\.Stale, and \S+\\Stale is newer; skipped' -and
                                                      $startup -match 'Fresh would also be dbo\.Fresh, and \S+\\Fresh\.parquet is newer; skipped')

    # The rest each scan one folder and only read what it mounted: start, wait for the listener,
    # stop. The exe is started directly so that stopping it stops the server, not just a dotnet run
    # in front of it.
    $exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
    function Scan($dataDir, $offset) {
        $scanLog = Join-Path $work "scan-$offset.log"
        $scan = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $scanLog `
            -ArgumentList "--port $($Port + $offset) --data-dir `"$dataDir`" --no-repl"
        $out = ""
        try {
            foreach ($attempt in 1..30) {
                Start-Sleep -Seconds 1
                $out = Get-Content $scanLog -Raw -ErrorAction SilentlyContinue
                if ($out -match "listening") { break }
            }
        }
        finally { if (-not $scan.HasExited) { Stop-Process -Id $scan.Id -Force } }
        return $out
    }

    # A folder with too many data files is refused whole, not half-mounted.
    $many = Join-Path ([System.IO.Path]::GetTempPath()) "tds-mount-many-$Port"
    if (Test-Path $many) { Remove-Item $many -Recurse -Force }
    New-Item -ItemType Directory -Path $many | Out-Null
    foreach ($i in 1..501) { [System.IO.File]::WriteAllText((Join-Path $many "t$i.csv"), "a`n$i`n") }

    $manyOut = Scan $many 2
    Check "more than 500 data files are refused"     ($manyOut -match "found 501 data files" -and
                                                      $manyOut -match "nothing was mounted")
    Check "and none of them is mounted"              ($manyOut -notmatch "mounted dbo\.")
    Check "and it is not called an empty folder"     ($manyOut -notmatch "warning: no \.parquet")

    # Typed with a separator on the end, as tab completion leaves it. Neither the data folder nor a
    # folder of parts directly in it names a schema.
    $partsOut = Scan "$work/FactInv/" 3
    Check "--data-dir at a folder of parts serves one table" ($partsOut -match 'mounted dbo\.FactInv\s+<- \S+/FactInv/, 2 part\(s\)')
    $slashOut = Scan "$work/Warehouse/" 4
    Check "a trailing separator on --data-dir is not a schema" ($slashOut -match 'mounted dbo\.Bins\s' -and
                                                      $slashOut -match 'mounted dbo\.Stock\s')
}
finally {
    $held.Dispose()
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    # The Power BI shortcut each server writes for its port.
    Remove-Item (0..4 | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$($Port + $_).pbids" }) `
        -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "mount checks passed"; exit 0 } else { Write-Output "$failed mount check(s) failed"; exit 1 }
