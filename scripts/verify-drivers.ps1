# Replays the metadata conversation Power BI has with SQL Server - through ODBC Driver 18, MSOLEDBSQL19
# and SqlClient - against DaffyDB, and compares every answer with LocalDB's answer to the same call.
#
# The calls come from DaffyTee captures of Power BI talking to a real SQL Server. The reference is
# src/DriverProbe/reference/localdb.json, recorded with:
#
#   dotnet run --project src/DriverProbe -- --localdb --record src/DriverProbe/reference/localdb.json
#
# Windows only: it needs both drivers installed, and LocalDB to re-record the reference.
#
# The server runs with --allow-passthrough because the probe builds its type_zoo table in DuckDB's
# own dialect, so that every native type DaffyDB maps is present exactly once.
param([int]$Port = 14480, [switch]$Details)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$data = Join-Path ([System.IO.Path]::GetTempPath()) "tds-drivers-$Port"
$log = Join-Path ([System.IO.Path]::GetTempPath()) "tds-drivers-$Port.log"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }

dotnet run --project src/TdsProxy --no-build -- --seed --data-dir $data | Out-Null

$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir `"$data`" --allow-passthrough --no-repl"

try {
    Start-Sleep -Seconds 7
    dotnet run --project src/DriverProbe --no-build -- --port $Port --compare src/DriverProbe/reference/localdb.json $(if ($Details) { "--verbose" })
    $code = $LASTEXITCODE
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    # Stopping "dotnet run" does not stop the daffydb it launched. Whatever listens on this
    # script's own port is that instance and nothing else, so it is safe to stop by port.
    Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    # The Power BI shortcut the server writes for its port.
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
    Pop-Location
}

exit $code
