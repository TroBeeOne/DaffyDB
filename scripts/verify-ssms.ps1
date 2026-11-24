# Replays what SQL Server Management Studio sends - connecting, expanding Databases, a database's
# Tables and a table's Columns, and "Select Top 1000 Rows" - and checks every request against the
# expectation recorded beside it in src/SmoProbe/replay/object-explorer.sql. Then the same for
# connect-before-22.sql, what an SSMS release before 22 sends while it connects, and vscode.sql,
# what VS Code's SQL Server extension sends for the same steps as SSMS.
#
# A request marked ok that stops working fails the run. A known gap is listed with the error it
# gets, and one that starts working is announced; record it with:
#
#   pwsh scripts/verify-ssms.ps1 -Update
#
# and review the diff to the capture file, which shows each gap turning into an ok.
param([int]$Port = 14490, [switch]$Update)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$data = Join-Path ([System.IO.Path]::GetTempPath()) "tds-ssms-$Port"
$log = Join-Path ([System.IO.Path]::GetTempPath()) "tds-ssms-$Port.log"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }

dotnet run --project src/TdsProxy --no-build -- --seed --data-dir $data | Out-Null

$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir `"$data`" --no-repl"

try {
    Start-Sleep -Seconds 7
    $code = 0
    foreach ($capture in "object-explorer.sql", "connect-before-22.sql", "vscode.sql") {
        dotnet run --project src/SmoProbe --no-build -- --port $Port --replay src/SmoProbe/replay/$capture `
            $(if ($Update) { "--update" })
        if ($LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
    }
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    # Stopping "dotnet run" does not stop the daffydb it launched; this port is that instance alone.
    Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    # The Power BI shortcut the server writes for its port.
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
    Pop-Location
}

exit $code
