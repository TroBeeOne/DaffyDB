# Checks the prompt: the commands, the paging, and that quitting really does stop the server.
#
# Two harnesses, because they answer different questions. Most checks pipe a script into stdin and
# assert the transcript. The last few drive the process directly through System.Diagnostics, which
# is the only way to hold stdin open long enough to prove a client can still connect while someone
# is sitting at the prompt.
#
# Note on piping: a PowerShell pipeline does not start a native command until the left side
# finishes, so "sleep then quit | daffydb" does not do what it looks like. Hence the second form.
param([int]$Port = 14520)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $condition) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name" }
}

if (-not (Test-Path "data/customers.parquet")) {
    Write-Output "data/customers.parquet missing; run: dotnet run --project src/TdsProxy -- --seed"
    Pop-Location; exit 1
}

$exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
if (-not (Test-Path $exe)) {
    Write-Output "$exe missing; run: dotnet build"
    Pop-Location; exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "tds-repl-$Port"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    # ---------------------------------------------------------------- piped session
    $script = Join-Path $work "session.txt"
    @(
        ':tables'
        ':columns customers'
        'SELECT TOP 3 name, city FROM customers ORDER BY name;'
        'SELECT TOP 2 name FROM customers; SELECT TOP 2 product FROM orders;'
        ':wide'
        'SELECT TOP 1 * FROM customers;'
        ':wide'
        ':rows 3'
        'SELECT name FROM customers ORDER BY name;'
        'SELECT * FROM does_not_exist;'
        'SELECT TOP 1 name FROM customers;'
        ':log 4'
        ':nonsense'
        'USE master;'
        "SELECT DB_NAME() + '-is-current' AS db;"
        ':quit'
    ) | Set-Content -Path $script -Encoding ascii

    # Set-Content writes no BOM, but the pipe does: $OutputEncoding is UTF8-with-preamble, so the
    # first line arrives as "<BOM>:tables". That is exactly what a user piping a script would hit,
    # so it is left in place deliberately rather than worked around here.
    $out = (Get-Content $script | & $exe --port $Port --data-dir data --repl 2>&1) | Out-String

    Check "the prompt appears"                   ($out -match 'daffydb>')
    Check ":tables lists a mounted file"         ($out -match 'customers\s+table')
    Check ":tables offers a sample query"        ($out -match 'SELECT TOP 5 \* FROM \[dbo\]\.\[customers\];')
    Check ":columns names the object once"       ($out -match 'dbo\.customers' -and $out -match 'signup_date\s+date')
    Check "a query returns rows"                 ($out -match 'Ada Lovelace')
    Check "two statements give two result sets"  ($out -match '-- result set 1 of 2 --' -and
                                                  $out -match '-- result set 2 of 2 --')
    Check ":wide prints one row per record"      ($out -match '-\[ row 1 \]' -and $out -match 'signup_date \|')
    Check ":rows caps the page"                  ($out -match 'showing 3 of 8 rows')
    Check "a bad query is reported, not fatal"   ($out -match 'error: .*does_not_exist')
    Check "and the prompt survives it"           ($out -match 'error: .*does_not_exist[\s\S]*Ada Lovelace')
    Check ":log shows the translation"           ($out -match '\[duckdb\] DuckDB:')
    Check "an unknown command is refused"        ($out -match "unknown command ':nonsense'")
    Check "USE at the prompt says where it went" ($out -match "Changed database context to 'master'")
    Check "and the prompt stays there"           ($out -match 'master-is-current')
    Check ":quit says it is stopping"            ($out -match 'stopping the server')

    # Nothing may page when there is no one to press a key, so the key hints must stay hidden.
    Check "no key prompt without a reader"       ($out -notmatch '\[space\] next page')

    # ---------------------------------------------------------------- live session
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--port $($Port + 1) --data-dir data --repl"
    $psi.WorkingDirectory = $root
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $live = [System.Diagnostics.Process]::Start($psi)
    $reader = $live.StandardOutput.ReadToEndAsync()

    Start-Sleep -Seconds 8
    $listening = [bool](Get-NetTCPConnection -LocalPort ($Port + 1) -State Listen -ErrorAction SilentlyContinue)
    Check "the listener is up while at a prompt" $listening

    $client = dotnet run --project src/TdsProbe --no-build -- --port ($Port + 1) `
        --sql "SELECT TOP 1 name FROM customers" 2>&1 | Out-String
    Check "a client can query while at a prompt" ($client -match 'PASS ad-hoc')

    $live.StandardInput.WriteLine(":quit")
    $live.StandardInput.Flush()
    $exited = $live.WaitForExit(30000)
    Start-Sleep -Seconds 1

    Check ":quit exits the process"              ($exited -and $live.HasExited)
    Check "and exits zero"                       ($live.ExitCode -eq 0)
    Check "and releases the port"                (-not [bool](Get-NetTCPConnection -LocalPort ($Port + 1) `
                                                     -State Listen -ErrorAction SilentlyContinue))

    # The client's session was logged while the prompt held the terminal, not printed over it.
    $session = $reader.Result
    Check "client traffic is buffered, not shown" ($session -notmatch 'session 1 accepted[\s\S]*daffydb> $')

    # ---------------------------------------------------------------- plain listener
    $plainLog = Join-Path $work "plain.log"
    $plain = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $plainLog `
        -WorkingDirectory $root -ArgumentList "--port $($Port + 2) --data-dir data --no-repl"
    Start-Sleep -Seconds 7
    if (-not $plain.HasExited) { Stop-Process -Id $plain.Id -Force }
    Start-Sleep -Seconds 1
    $plainOut = Get-Content $plainLog -Raw
    Check "--no-repl gives no prompt"            ($plainOut -notmatch 'daffydb>')
    Check "--no-repl still listens"              ($plainOut -match 'listening on 127\.0\.0\.1')

    # Redirected output means a script or a log file, and a prompt in front of one helps nobody.
    $autoLog = Join-Path $work "auto.log"
    $auto = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $autoLog `
        -WorkingDirectory $root -ArgumentList "--port $($Port + 3) --data-dir data"
    Start-Sleep -Seconds 7
    if (-not $auto.HasExited) { Stop-Process -Id $auto.Id -Force }
    Start-Sleep -Seconds 1
    $autoOut = Get-Content $autoLog -Raw
    Check "redirected output falls back to plain" ($autoOut -notmatch 'daffydb>')

    # ---------------------------------------------------------------- Power BI shortcut
    # A certificate made for this run alone, which Windows does not trust, so :pbi stops short of
    # opening Power BI Desktop - which a test must never do. Piped input also means it cannot ask.
    $pbiPort = $Port + 4
    $pbiFile = Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$pbiPort.pbids"
    if (Test-Path $pbiFile) { Remove-Item $pbiFile -Force }

    $pbiOut = (@(':help', ':pbi', ':quit') |
        & $exe --port $pbiPort --data-dir data --repl --cert-file (Join-Path $work "pbi.pfx") 2>&1) | Out-String

    Check ":help lists :pbi"                     ($pbiOut -match ':pbi\s+open Power BI Desktop')
    Check "the banner points at the shortcut"    ($pbiOut.Contains("type :pbi, or open $pbiFile"))
    Check ":pbi says the certificate is untrusted" ($pbiOut -match 'Windows does not trust it yet')
    Check "and does not open Power BI"           ($pbiOut -match 'Not opened: Power BI would refuse the certificate' -and
                                                  $pbiOut -notmatch 'opening .*\.pbids')

    $pbids = if (Test-Path $pbiFile) { Get-Content $pbiFile -Raw | ConvertFrom-Json } else { $null }
    Check "the shortcut names this port and daffydb" ($pbids -and
        $pbids.connections[0].details.address.server -eq "127.0.0.1,$pbiPort" -and
        $pbids.connections[0].details.address.database -eq 'daffydb')
    Check "in Import mode, with no credentials"  ($pbids -and $pbids.connections[0].mode -eq 'Import' -and
                                                  $null -eq $pbids.connections[0].details.authentication)
    if (Test-Path $pbiFile) { Remove-Item $pbiFile -Force }
}
finally {
    # Only the copies this script started. Anyone may have their own daffydb running on 1433.
    foreach ($started in @($live, $plain, $auto)) {
        if ($started -and -not $started.HasExited) { Stop-Process -Id $started.Id -Force }
    }
    # The Power BI shortcut each server writes for its port.
    Remove-Item (0..4 | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$($Port + $_).pbids" }) `
        -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "repl checks passed"; exit 0 } else { Write-Output "$failed repl check(s) failed"; exit 1 }
