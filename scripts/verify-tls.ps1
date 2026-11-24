# Checks both encrypted transports against a self-signed certificate the proxy generates itself:
# TDS 8.0 (Encrypt=Strict) and TDS 7.4 (Encrypt=Mandatory), plus plaintext, all on one port.
#
# Encryption is the default, so the last block here is the one that matters most to a new install:
# a server started with no certificate arguments at all still has to accept Encrypt=Mandatory,
# because that is what every current client asks for before anyone reads any documentation.
#
# Two results worth pinning down, because they are the opposite of each other and people assume
# the wrong one. Under Strict, TrustServerCertificate=True is IGNORED - the client validates the
# chain regardless, so a self-signed certificate only works when it is pointed at the .cer with
# ServerCertificate=. Under Mandatory, TrustServerCertificate=True is honoured and no file is
# needed at all. Neither is a guess; the checks below fail the build if either stops being true.
param([int]$Port = 14441)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$failed = 0
function Check($name, $condition) {
    if ($condition) { Write-Output "ok   $name" }
    else { $script:failed++; Write-Output "FAIL $name" }
}

# A directory of its own, so the check always exercises first-run generation.
$certDir = Join-Path ([System.IO.Path]::GetTempPath()) "tds-tls-$Port"
if (Test-Path $certDir) { Remove-Item $certDir -Recurse -Force }
$pfx = Join-Path $certDir "proxy.pfx"
$cer = Join-Path $certDir "proxy.cer"

$log = Join-Path ([System.IO.Path]::GetTempPath()) "tds-tls-$Port.log"
$proxy = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $Port --data-dir data --cert-file `"$pfx`""

try {
    Start-Sleep -Seconds 7

    Check "a certificate is generated on first run" (Test-Path $pfx)
    Check "the public half is written beside it"    (Test-Path $cer)

    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer
    $san = ($cert.Extensions | Where-Object { $_.Oid.Value -eq "2.5.29.17" }).Format($false)
    Check "the SAN covers localhost"  ($san -match "localhost")
    Check "the SAN covers 127.0.0.1"  ($san -match "127\.0\.0\.1")
    Check "it is valid for years"     ($cert.NotAfter -gt (Get-Date).AddYears(4))

    $strict = dotnet run --project src/TdsProbe --no-build -- --port $Port --server-cert $cer `
        --sql "SELECT 42 AS answer" 2>&1 | Out-String
    Check "Encrypt=Strict connects with ServerCertificate" ($strict -match "PASS ad-hoc")
    Check "and returns rows"                               ($strict -match "42")

    Check "the server negotiated TLS and ALPN tds/8.0" `
        ((Get-Content $log -Raw) -match "TLS established.*alpn=tds/8\.0")
    Check "the session is marked encrypted" `
        ((Get-Content $log -Raw) -match "encrypted=True")

    # Same port, no TLS: the first byte decides, so both clients coexist.
    $plain = dotnet run --project src/TdsProbe --no-build -- --port $Port --sql "SELECT 7 AS plain" 2>&1 | Out-String
    Check "plaintext still works on the same port" ($plain -match "PASS ad-hoc")

    # The finding worth keeping honest.
    $trustOnly = dotnet run --project src/TdsProbe --no-build -- --port $Port --strict `
        --sql "SELECT 1" 2>&1 | Out-String
    Check "TrustServerCertificate alone is NOT enough under Strict" `
        ($trustOnly -match "not trusted")

    # TDS 7.4: the handshake is tunnelled through PRELOGIN packets rather than wrapping the socket,
    # so this exercises PreLoginTlsStream and the mid-session swap of the session's transport.
    $mandatory = dotnet run --project src/TdsProbe --no-build -- --port $Port --mandatory `
        --sql "SELECT 42 AS answer" 2>&1 | Out-String
    Check "Encrypt=Mandatory connects with TrustServerCertificate alone" ($mandatory -match "PASS ad-hoc")
    Check "and needs no ServerCertificate file, unlike Strict"           ($mandatory -match "42")

    # The whole protocol loop has to keep working after the transport is replaced underneath it.
    $suite = dotnet run --project src/TdsProbe --no-build -- --port $Port --mandatory 2>&1 | Out-String
    Check "the milestone suite passes over 7.4 as well" ($suite -match "6 passed, 0 failed")

    $tail = Get-Content $log -Raw
    Check "the server negotiated 7.4 TLS, not 8.0" ($tail -match "TLS established \(TDS 7\.4\)")
    Check "TLS 1.2 is pinned for 7.4"              ($tail -match "TLS established \(TDS 7\.4\): Tls12")

    # Tie the session number from the 7.4 handshake to that same session's login line. A bare
    # "encrypted=True" anywhere in the log would also be satisfied by the Strict run above, and
    # what needs proving here is that swapping the transport mid-session actually took effect.
    $sevenFour = [regex]::Match($tail, '\[s(\d+)\] TLS established \(TDS 7\.4\)')
    Check "a 7.4 session number is logged" $sevenFour.Success
    if ($sevenFour.Success) {
        $n = $sevenFour.Groups[1].Value
        Check "that same session reaches the handler encrypted" `
            ($tail -match "session $n .*encrypted=True")
    }
}
finally {
    if (-not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
}

# A second server with no certificate at all, which now takes --no-cert to arrange. Asking it for
# encryption has to fail in a way the operator can act on, rather than hanging or half-connecting.
$bareLog = Join-Path ([System.IO.Path]::GetTempPath()) "tds-tls-$Port-bare.log"
$bare = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $bareLog `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $($Port + 1) --data-dir data --no-cert"

try {
    Start-Sleep -Seconds 7

    $refused = dotnet run --project src/TdsProbe --no-build -- --port ($Port + 1) --mandatory `
        --sql "SELECT 1" 2>&1 | Out-String
    Check "Mandatory without a certificate is refused, not hung" ($refused -match "does not support encryption")
    Check "and the log says which switch fixes it" `
        ((Get-Content $bareLog -Raw) -match "no certificate is configured - restart with --cert")

    $plainBare = dotnet run --project src/TdsProbe --no-build -- --port ($Port + 1) `
        --sql "SELECT 7 AS plain" 2>&1 | Out-String
    Check "and a plaintext client on that server is unaffected" ($plainBare -match "PASS ad-hoc")

    # The mirror image: with nothing encrypted on offer, the banner has to show the one string
    # that does work rather than three that do not.
    $bareBanner = Get-Content $bareLog -Raw
    Check "the --no-cert banner says encryption is off" ($bareBanner -match "Encryption is OFF")
    Check "and falls back to Encrypt=False"             ($bareBanner -match "Encrypt=False;TrustServerCertificate=True")
    Check "and to a plaintext JDBC url"                 ($bareBanner -match "jdbc:sqlserver://.+encrypt=false")
    Check "and offers no Mandatory string that would fail" ($bareBanner -notmatch "Encrypt=Mandatory")
}
finally {
    if (-not $bare.HasExited) { Stop-Process -Id $bare.Id -Force }
}

# No --cert, no --cert-file, no anything: the out-of-the-box experience. This uses the real
# per-user certificate directory rather than a temporary one, which is the point - it is the
# certificate a fresh install would make for itself and then keep.
$defaultLog = Join-Path ([System.IO.Path]::GetTempPath()) "tds-tls-$Port-default.log"
$default = Start-Process -FilePath "dotnet" -PassThru -WindowStyle Hidden -RedirectStandardOutput $defaultLog `
    -ArgumentList "run --project src/TdsProxy --no-build -- --port $($Port + 2) --data-dir data"

try {
    Start-Sleep -Seconds 7

    $outOfBox = dotnet run --project src/TdsProbe --no-build -- --port ($Port + 2) --mandatory `
        --sql "SELECT 42 AS answer" 2>&1 | Out-String
    Check "a server started with no arguments accepts Encrypt=Mandatory" ($outOfBox -match "PASS ad-hoc")
    Check "and returns rows over it"                                     ($outOfBox -match "42")
    Check "its certificate came from the per-user store" `
        ((Get-Content $defaultLog -Raw) -match "DaffyDB.certs")

    $plainDefault = dotnet run --project src/TdsProbe --no-build -- --port ($Port + 2) `
        --sql "SELECT 7 AS plain" 2>&1 | Out-String
    Check "and plaintext is still accepted alongside it" ($plainDefault -match "PASS ad-hoc")

    # The launch view is the first and often only documentation anyone reads, and every string on
    # it is supposed to be one that connects as the server currently stands. So: encrypted server,
    # encrypted strings, and no Encrypt=False to copy by mistake.
    $banner = Get-Content $defaultLog -Raw
    Check "the banner names the tool and version"  ($banner -match "DaffyDB \d+\.\d+\.\d+(-[0-9A-Za-z.]+)? - query parquet")
    Check "it says DuckDB is the engine"           ($banner -match "engine is DuckDB, not SQL Server")
    Check "it leads with Encrypt=Mandatory"        ($banner -match "Encrypt=Mandatory;TrustServerCertificate=True")
    Check "it offers Encrypt=Strict with the .cer" ($banner -match "Encrypt=Strict;ServerCertificate=.+\.cer")
    Check "it offers a JDBC url"                   ($banner -match "jdbc:sqlserver://127\.0\.0\.1:")
    Check "it says what Power BI's connector needs" ($banner -match "Power BI's SQL Server connector[\s\S]+Server: 127\.0\.0\.1,")
    Check "and no Encrypt=False, which would fail" ($banner -notmatch "Encrypt=False")
}
finally {
    if (-not $default.HasExited) { Stop-Process -Id $default.Id -Force }
    # The Power BI shortcut each server with a certificate writes for its port.
    Remove-Item ($Port, ($Port + 2) | ForEach-Object { Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$_.pbids" }) `
        -ErrorAction SilentlyContinue
    Pop-Location
}

Write-Output "========================================================"
if ($failed -eq 0) { Write-Output "tls checks passed"; exit 0 } else { Write-Output "$failed tls check(s) failed"; exit 1 }
