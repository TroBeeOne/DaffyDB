# Starts a proxy that requires credentials and checks the login hook accepts and refuses correctly.
#
# This needs its own proxy instance with its own flags, which is why it is a script rather than
# another DuckProbe check: DuckProbe talks to whatever proxy is already listening.
param([int]$Port = 14433, [string]$User = "alice", [string]$Password = "s3cret pass!")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $expected, $actual) {
    if ($actual -eq $expected) {
        Write-Output "ok   $name"
    }
    else {
        $script:failed++
        Write-Output "FAIL $name : expected $expected, got $actual"
    }
}

# --port is passed through so this never collides with a proxy already serving 1433.
$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir data --user `"$User`" --password `"$Password`""

try {
    Start-Sleep -Seconds 5

    # The password is the interesting one: it travels nibble-swapped and XORed, so a correct
    # accept proves the decode, punctuation and spaces included.
    $right = dotnet run --project src/TdsProbe --no-build -- `
        --port $Port --user $User --password $Password --sql "SELECT SUSER_SNAME() AS login" 2>&1 | Out-String
    Check "correct credentials are accepted" $true ($right -match 'PASS ad-hoc')
    Check "the accepted login is reported"   $true ($right -match [regex]::Escape($User))

    $badPassword = dotnet run --project src/TdsProbe --no-build -- `
        --port $Port --user $User --password "wrong" --sql "SELECT 1" 2>&1 | Out-String
    Check "a wrong password is refused" $true ($badPassword -match "Login failed for user")

    $badUser = dotnet run --project src/TdsProbe --no-build -- `
        --port $Port --user "mallory" --password $Password --sql "SELECT 1" 2>&1 | Out-String
    Check "a wrong user is refused" $true ($badUser -match "Login failed for user")

    # The refusal must not leak which half was wrong.
    Check "the refusal does not say which half failed" $true `
        (-not ($badPassword -match "password" -and $badPassword -match "incorrect"))
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    Get-Process TdsProxy -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "*TdsProxy*" } | ForEach-Object {
            # Only the instance this script started listens on $Port; leave any other alone.
        }
    # The Power BI shortcut the server writes for its port.
    Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "auth checks passed"; exit 0 } else { Write-Output "$failed auth check(s) failed"; exit 1 }
