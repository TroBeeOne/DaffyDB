# Checks EXEC sp_duckdb: refused when the gate is shut, and genuinely native DuckDB when it is not.
#
# Needs two proxies with different flags, which is why this is a script rather than a DuckProbe
# check. Nothing here touches the network — INSTALL is deliberately left out so the suite stays
# runnable offline.
param([int]$Port = 14438)

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

$open = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path ([System.IO.Path]::GetTempPath()) "pt-open-$Port.log") `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir data --allow-passthrough"

$shut = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path ([System.IO.Path]::GetTempPath()) "pt-shut-$Port.log") `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $($Port + 1) --data-dir data"

try {
    Start-Sleep -Seconds 7

    Check "refused without --allow-passthrough" `
        ((Ask ($Port + 1) "EXEC sp_duckdb N'SELECT 1'") -match "sp_duckdb is disabled")

    # version() has no T-SQL equivalent, so answering it proves nothing was translated.
    Check "a DuckDB-only function runs untranslated" `
        ((Ask $Port "EXEC sp_duckdb N'SELECT version() AS v'") -match "v1\.")

    # Something the T-SQL path would have swallowed as a session option.
    Check "SET reaches DuckDB" `
        ((Ask $Port "EXEC sp_duckdb N'SET default_null_order = ''nulls_last''; SELECT current_setting(''default_null_order'') AS o'") -match "nulls_last")

    Check "an S3 secret can be created" `
        ((Ask $Port "EXEC sp_duckdb N'CREATE OR REPLACE SECRET probe (TYPE s3, KEY_ID ''AKIAEXAMPLE'', SECRET ''x'', REGION ''us-east-1''); SELECT name FROM duckdb_secrets() WHERE name = ''probe'''") -match "probe")

    Ask $Port "EXEC sp_duckdb N'CREATE SCHEMA IF NOT EXISTS live; CREATE OR REPLACE VIEW live.spend AS SELECT customer_id, SUM(quantity * unit_price) AS total FROM daffydb.dbo.orders GROUP BY customer_id'" | Out-Null

    Check "an object it creates is queryable as T-SQL" `
        ((Ask $Port "SELECT TOP 1 customer_id, total FROM live.spend ORDER BY total DESC") -match "customer_id")

    Check "and shows up in the sys catalog" `
        ((Ask $Port "SELECT o.name FROM daffydb.sys.objects o JOIN daffydb.sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = 'live'") -match "spend")

    # DuckDB's USE moves the connection every session shares, not this session; T-SQL's USE is
    # the one that moves a session. The statement after it still runs in daffydb.
    Check "a DuckDB USE does not move the session" `
        ((Ask $Port "EXEC sp_duckdb N'USE master.dbo'; SELECT CASE WHEN COUNT(*) > 0 THEN 'still in daffydb' ELSE 'moved' END AS here FROM sys.objects") -match "still in daffydb")

    Check "the argument may be a variable" `
        ((Ask $Port "DECLARE @s nvarchar(200) = 'SELECT 7 AS built'; EXEC sp_duckdb @s") -match "built")

    Check "an empty argument is rejected" `
        ((Ask $Port "EXEC sp_duckdb N''") -match "needs a string of DuckDB SQL")
}
finally {
    foreach ($p in @($open, $shut)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    # The Power BI shortcut each server writes for its port.
    Remove-Item ($Port, ($Port + 1) | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$_.pbids" }) `
        -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "pass-through checks passed"; exit 0 } else { Write-Output "$failed check(s) failed"; exit 1 }
