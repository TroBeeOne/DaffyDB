# Extracts every ```sql block from a document and runs it through the proxy. QUERIES.md by
# default; -Doc NUGET.md checks the examples on the package page, which are the ones a stranger
# tries first and so the worst ones to have rot.
#
# A block that merely fails to throw is not proof of anything: the bug that prompted this script's
# last revision was a batch that quietly returned one grid where it should have returned two. So
# the shape of what came back is reported alongside the pass, for eyeballing against the doc.
#
# The same trap caught the script itself. It used to target whatever happened to be on 1433 and
# start nothing, and it only looked for "FAIL ad-hoc" - but a probe that cannot connect never gets
# as far as the query, and reports "FAIL M3 connection opens" instead. With no server there, every
# block was counted as a clean pass after a fifteen-second connect timeout: eight minutes to prove
# nothing whatsoever. It now starts its own proxy, as every other script here does and as
# README_PROBE already claimed this one did, and a block that never reached the server fails.
param([string]$Doc = "QUERIES.md", [int]$Port = 14460)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$text = Get-Content $Doc -Raw
$blocks = [regex]::Matches($text, '(?s)```sql\r?\n(.*?)```') | ForEach-Object { $_.Groups[1].Value.Trim() }

Write-Output "found $($blocks.Count) sql blocks"
$failed = 0
$index = 0

$log = Join-Path ([System.IO.Path]::GetTempPath()) "tds-queries-$Port.log"
$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir data --allow-passthrough"

try {

Start-Sleep -Seconds 7

foreach ($sql in $blocks) {
    $index++
    $output = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql $sql 2>&1 | Out-String
    $firstLine = ($sql -split "`n")[0].Trim()

    # Never reached the server. Called out separately because the cause is the harness rather than
    # the SQL, and because counting it as a pass is the exact failure this script exists to avoid.
    if ($output -notmatch 'PASS M3  connection opens') {
        $failed++
        Write-Output "--------------------------------------------------------"
        Write-Output "FAIL block $index : never reached the proxy on port $Port"
        Write-Output "     $(($output -split "`n" | Where-Object { $_ -match 'Exception' } | Select-Object -First 1))"
        continue
    }

    if ($output -match 'FAIL ad-hoc') {
        $failed++
        Write-Output "--------------------------------------------------------"
        Write-Output "FAIL block $index : $firstLine"
        $detail = ($output -split "`n" | Where-Object { $_ -match 'Exception|Error' } | Select-Object -First 2) -join " | "
        Write-Output "     $detail"
        continue
    }

    # The probe prints one "-- result set N --" header per grid, and "(no rows)" for an empty one.
    $sets = ([regex]::Matches($output, '-- result set \d+ --')).Count
    $empty = ([regex]::Matches($output, '\(no rows\)')).Count

    $shape = switch ($sets) {
        0       { "no result sets" }
        default { "$sets result set(s)" + $(if ($empty -gt 0) { ", $empty empty" } else { "" }) }
    }

    Write-Output ("ok   block {0,2} : {1,-22} {2}" -f $index, $shape, $firstLine)
}

}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    # The Power BI shortcut the server writes for its port.
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
Write-Output "$($blocks.Count - $failed) of $($blocks.Count) blocks ran clean"
exit $(if ($failed -eq 0) { 0 } else { 1 })
