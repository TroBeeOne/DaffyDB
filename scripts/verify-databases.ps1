# Checks the database mounts: daffydb.sqlite always, and daffydb.postgres and daffydb.mysql against
# servers you name, since there is no server to assume.
#
#   pwsh scripts/verify-databases.ps1
#   pwsh scripts/verify-databases.ps1 -Postgres "host=localhost dbname=shop user=reader password=..."
#   pwsh scripts/verify-databases.ps1 -MySql "host=127.0.0.1 database=shop user=reader password=..."
#
# A server's checks mount whatever its database holds, into schemas of their own, and assert only
# what is true of any database - that tables arrive and read, one table can be added, a bad
# connection is refused by number, and no password reaches the log, a client, or the catalog -
# so nothing about a particular database is written here. Nothing is written to any database.
#
# Unlike the other scripts this needs the network once: each extension downloads from DuckDB's
# repository on first use and is kept per user. Without it the checks for that database are
# reported as skipped, with the reason.
param([int]$Port = 14505, [string]$Postgres, [string]$MySql)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
$skipped = 0
function Check($name, $condition) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name" }
}
function Skip($name, $why) {
    $script:skipped++
    Write-Output "skip $name - $why"
}

function Ask($sql) {
    dotnet run --project src/TdsProbe --no-build -- --port $Port --sql $sql 2>&1 | Out-String
}

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-databases-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $work "data") -Force | Out-Null
Copy-Item "data/customers.parquet" (Join-Path $work "data\customers.parquet")
Copy-Item "data/orders.parquet"    (Join-Path $work "data\orders.parquet")

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
$log = Join-Path $work "server.log"
$server = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "--port $Port --data-dir `"$work\data`" --allow-admin --allow-passthrough --no-repl"

# Whether text holds a password as a connection string would carry it - password=it, or :it@ in a
# URL - rather than merely the same word, which a password like the user name would always be.
function Leaks($text, $password) {
    $p = [regex]::Escape($password)
    return ($text -match "password\s*=\s*'?$p" -or $text -match ":$p@" -or $text -match "PASSWORD\s+'$p'")
}

# The password in a connection string, so the log can be searched for it.
function PasswordOf($connection) {
    if ($connection -match "password\s*=\s*'([^']*)'") { return $Matches[1] }
    if ($connection -match "password\s*=\s*(\S+)") { return $Matches[1] }
    if ($connection -match "://[^:/@]+:([^@]+)@") { return $Matches[1] }
    return $null
}

# A server database's checks, the same for PostgreSQL and MySQL.
function CheckServer($kind, $connection, $refused) {
    $all = Ask "EXEC daffydb.$kind '$($connection.Replace("'", "''"))', @schema = '${kind}_all'"
    $count = if ($all -match "info: Mounted (\d+) tables? from \S+ as '${kind}_all'") { [int]$Matches[1] } else { 0 }
    Check "$kind`: every table mounts, named after its schema" ($count -gt 0)
    if ($count -eq 0) { Write-Output $all; return }

    $first = if ($all -match 'name\s+String\s+"([^"]+)"') { $Matches[1] } else { "" }
    Check "$kind`: a table reads, live"        ((Ask "SELECT COUNT(*) AS n FROM [${kind}_all].[$first]") -match 'n\s+Int\d+\s+\d')
    Check "$kind`: tables are catalogued as tables" ((Ask "SELECT o.type_desc FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = '${kind}_all' AND o.name = '$first'") -match '"USER_TABLE"')

    $one = Ask "EXEC daffydb.$kind '$($connection.Replace("'", "''"))', '$first', '${kind}_one'"
    Check "$kind`: one table mounts on its own" ($one -match "info: Mounted 1 table from \S+ as '${kind}_one'")

    $again = Ask "EXEC daffydb.$kind '$($connection.Replace("'", "''"))', @schema = '${kind}_all'"
    Check "$kind`: mounting it again reads it again" ($again -match "info: Read \S+ again as '${kind}_all': $count table")

    Check "$kind`: an unreachable server is refused, and says why" ((Ask "EXEC daffydb.$kind '$refused'") -match "SqlException 50303.*could not open")
    Check "$kind`: the database must be named" ((Ask "EXEC daffydb.$kind 'host=127.0.0.1 user=nobody'") -match "SqlException 50302.*name the database")

    $catalog = Ask "SELECT path FROM duckdb_databases() WHERE path IS NOT NULL; SELECT secret_string FROM duckdb_secrets()"
    $password = PasswordOf $connection
    if ($password) {
        Check "$kind`: the password is not in the catalog"  (-not (Leaks $catalog $password) -and $catalog -match "password=redacted")
    }

    $sums = Ask "SELECT COUNT(*) AS n FROM [${kind}_all].[$first]; SELECT COUNT(*) AS n FROM [${kind}_one].[$first]"
    Check "$kind`: an aggregate over a mounted table runs" ($sums -match 'n\s+Int\d+\s+\d' -and $sums -notmatch "SqlException")

    $gone = Ask "EXEC daffydb.unmount ${kind}_all; EXEC daffydb.unmount ${kind}_one"
    Check "$kind`: unmount takes it all away"  ($gone -match "info: Unmounted '${kind}_all'" -and $gone -match "info: Unmounted '${kind}_one'")
}

try {
    Start-Sleep -Seconds 5

    # --- SQLite: a file written here, through DuckDB's own sqlite extension -------------------
    $sqlite = Join-Path $work "music.sqlite"
    $made = Ask ("EXEC sp_duckdb N'INSTALL sqlite; LOAD sqlite; ATTACH ''$sqlite'' AS s (TYPE sqlite); " +
                 "CREATE TABLE s.artists (artist_id INTEGER, name VARCHAR); " +
                 "INSERT INTO s.artists VALUES (1, ''Ada''), (2, ''Grace''), (3, ''Linus''); " +
                 "CREATE TABLE s.albums (album_id INTEGER, artist_id INTEGER, title VARCHAR, released DATE, price DOUBLE); " +
                 "INSERT INTO s.albums VALUES (1, 1, ''First'', DATE ''2020-01-02'', 9.99), (2, 1, ''Second'', DATE ''2021-03-04'', 12.5), (3, 2, ''Only'', DATE ''2019-05-06'', 7.25); " +
                 "CREATE VIEW s.album_count AS SELECT a.name, COUNT(b.album_id) AS albums FROM artists a LEFT JOIN albums b ON b.artist_id = a.artist_id GROUP BY a.name; " +
                 "DETACH s'")

    if ($made -notmatch "PASS ad-hoc") {
        Skip "sqlite" "DuckDB's sqlite extension could not be installed: $(($made -split "`n" | Select-String 'SqlException' | Select-Object -First 1))"
    }
    else {
        $mounted = Ask "EXEC daffydb.sqlite '$sqlite'"
        Check "sqlite: every table and view mounts, in dbo" ($mounted -match "info: Mounted 3 tables from .*music\.sqlite as 'music'" -and
                                                             $mounted -match 'schema\s+String\s+"dbo"')
        # DuckDB writes a DATE into SQLite as text, there being no date type there, so only the
        # number is checked for its type; a file whose own schema declares DATE reads as a date.
        Check "sqlite: a table reads, typed"     ((Ask "SELECT title, price FROM albums WHERE album_id = 1") -match 'price\s+Double\s+9\.99')
        Check "sqlite: a view reads"             ((Ask "SELECT albums FROM album_count WHERE name = 'Ada'") -match 'albums\s+Int\d+\s+2')
        Check "sqlite: they are tables to SSMS"  ((Ask "SELECT type_desc FROM sys.objects WHERE name = 'album_count'") -match '"USER_TABLE"')
        Check "sqlite: tables shows the source"  ((Ask "EXEC daffydb.tables") -match 'source\s+String\s+"sqlite"')

        $one = Ask "EXEC daffydb.sqlite '$sqlite', 'artists', 'people'"
        Check "sqlite: one table into a schema, named after it" ($one -match "info: Mounted 1 table from .*music\.sqlite as 'people'")
        Check "sqlite: the same table again is a refresh of its mount" ((Ask "EXEC daffydb.sqlite '$sqlite', 'artists'") -match "info: Added artists to 'music', which now has 3 tables")
        Check "sqlite: a name another mount has is refused" ((Ask "EXEC daffydb.sqlite '$sqlite', 'artists', 'dbo'") -match "SqlException 50304.*dbo\.artists is taken by mount 'music'")

        $missing = Ask "EXEC daffydb.sqlite '$work\none.sqlite'"
        Check "sqlite: a file that is not there says so" ($missing -match "SqlException 50303.*no file at")
        Check "sqlite: and is not created"        (-not (Test-Path (Join-Path $work "none.sqlite")))

        $gone = Ask "EXEC daffydb.unmount music; EXEC daffydb.unmount people; SELECT COUNT(*) AS n FROM duckdb_databases() WHERE database_name LIKE 'daffy%' AND database_name <> 'daffydb'"
        Check "sqlite: unmount lets the file go"  ($gone -match "info: Unmounted 'music'" -and $gone -match 'n\s+Int\d+\s+0')
    }

    # --- the servers, when named ----------------------------------------------------------------
    if ($Postgres) { CheckServer "postgres" $Postgres "host=127.0.0.1 port=1 dbname=x user=y connect_timeout=3" }
    else { Skip "postgres" "no -Postgres connection string given" }

    if ($MySql) { CheckServer "mysql" $MySql "host=127.0.0.1 port=1 database=x user=y" }
    else { Skip "mysql" "no -MySql connection string given" }
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
}

$text = Get-Content $log -Raw
foreach ($connection in @($Postgres, $MySql)) {
    if (-not $connection) { continue }
    $password = PasswordOf $connection
    if ($password) { Check "no password reaches the log" (-not (Leaks $text $password) -and $text -match "password=\*\*\*|:\*\*\*@") }
}

Pop-Location
Write-Output "========================================================"
$note = if ($skipped -gt 0) { " ($skipped skipped)" } else { "" }
if ($failed -eq 0) { Write-Output "database checks passed$note"; exit 0 } else { Write-Output "$failed check(s) failed$note"; exit 1 }
