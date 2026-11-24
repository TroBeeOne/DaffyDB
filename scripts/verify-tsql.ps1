# Runs every case in fixtures\tsql-compat.txt through DaffyDB and compares the rows with the
# answer SQL Server gives. A case passes only when the values match, never merely because the
# query ran: the cases that prompted this script all ran cleanly and returned the wrong answer.
#
#   -Oracle      runs the cases against SQL Server instead (LocalDB by default) and reports where
#                its answer differs from the expected rows, which is how the expectations are kept
#                honest. [daffy] cases are skipped there; [differs] cases are shown, not counted.
#   -NoServer    uses a DaffyDB already listening on -Port rather than starting one.
#   -Only text   runs just the cases whose name contains the text.
param(
    [int]$Port = 14462,
    [switch]$Oracle,
    [string]$OracleServer = '(localdb)\MSSQLLocalDB',
    [switch]$NoServer,
    [string]$Only = ''
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Read-Cases([string]$path) {
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $cases = New-Object System.Collections.Generic.List[object]
    $current = $null

    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^## (.+)$') {
            $title = $Matches[1].Trim()
            $flags = @([regex]::Matches($title, '\[(\w+)\]') | ForEach-Object { $_.Groups[1].Value })
            $current = [pscustomobject]@{
                Name = ($title -replace '\s*\[\w+\]', '').Trim(); Flags = $flags
                Sql = ''; Expected = New-Object System.Collections.Generic.List[string]; Columns = $null
            }
            $cases.Add($current)
            continue
        }
        if ($null -eq $current -or $line.StartsWith('#')) { continue }
        if ($line.StartsWith('=> ')) { $current.Expected.Add($line.Substring(3)); continue }
        if ($line.StartsWith('cols: ')) { $current.Columns = $line.Substring(6); continue }
        if ($line.Trim().Length -gt 0) { $current.Sql += $line + "`n" }
    }
    return $cases
}

# Values are compared as text, in a form both servers can produce whatever type each chose: a date
# without a time of day is just the date, decimals lose trailing zeros, bit is 1 or 0.
function Format-Value($value) {
    if ($null -eq $value -or $value -is [DBNull]) { return 'NULL' }
    if ($value -is [string]) { if ($value.Length -eq 0) { return "''" } return $value }
    if ($value -is [bool]) { if ($value) { return '1' } return '0' }
    if ($value -is [datetime]) {
        if ($value.TimeOfDay.Ticks -eq 0) { return $value.ToString('yyyy-MM-dd', $invariant) }
        return $value.ToString('yyyy-MM-dd HH:mm:ss.fffffff', $invariant).TrimEnd('0').TrimEnd('.')
    }
    if ($value -is [datetimeoffset]) { return $value.ToString('yyyy-MM-dd HH:mm:ss.fffffff zzz', $invariant) }
    if ($value -is [timespan]) { return $value.ToString('hh\:mm\:ss\.fffffff', $invariant).TrimEnd('0').TrimEnd('.') }
    if ($value -is [decimal]) { return $value.ToString('0.############################', $invariant) }
    if ($value -is [double] -or $value -is [single]) { return $value.ToString('R', $invariant) }
    if ($value -is [byte[]]) { return '0x' + [System.BitConverter]::ToString($value).Replace('-', '') }
    return [string]::Format($invariant, '{0}', $value)
}

function Invoke-Case($connection, $case) {
    $command = $connection.CreateCommand()
    $command.CommandText = $case.Sql
    $command.CommandTimeout = 30
    $rows = New-Object System.Collections.Generic.List[string]
    $columns = $null
    $reader = $command.ExecuteReader()
    try {
        do {
            if ($reader.FieldCount -gt 0 -and $null -eq $columns) {
                $columns = (0..($reader.FieldCount - 1) | ForEach-Object { $reader.GetName($_) }) -join ' | '
            }
            while ($reader.Read()) {
                $values = for ($i = 0; $i -lt $reader.FieldCount; $i++) { Format-Value $reader.GetValue($i) }
                $rows.Add(($values -join ' | '))
            }
        } while ($reader.NextResult())
    }
    finally { $reader.Close() }
    return [pscustomobject]@{ Rows = $rows; Columns = $columns }
}

# A case whose only expected row is "error: text" expects the query to fail with a message
# containing that text - SQL Server's own wording, which DaffyDB reports inside DuckDB's.
function Test-Case($connection, $case) {
    $wantsError = $case.Expected.Count -eq 1 -and $case.Expected[0].StartsWith('error: ')
    try { $result = Invoke-Case $connection $case }
    catch {
        $ex = $_.Exception
        while ($ex.InnerException) { $ex = $ex.InnerException }
        $message = ($ex.Message -split "`n")[0]
        if ($wantsError -and $message.Contains($case.Expected[0].Substring(7))) { return "" }
        return "error: $message"
    }
    if ($wantsError) { return "expected $($case.Expected[0]), but it ran" }
    $problems = @()
    if ($case.Flags -contains 'names' -and $result.Columns -ne $case.Columns) {
        $problems += "columns: expected [$($case.Columns)], got [$($result.Columns)]"
    }
    $expected = $case.Expected -join "`n"
    $actual = $result.Rows -join "`n"
    if ($expected -ne $actual) {
        $problems += "expected:`n      " + ($case.Expected -join "`n      ") + "`n    got:`n      " + ($result.Rows -join "`n      ")
    }
    return ($problems -join "`n    ")
}

$cases = Read-Cases (Join-Path $PSScriptRoot "fixtures\tsql-compat.txt")
if ($Only) { $cases = @($cases | Where-Object { $_.Name -like "*$Only*" }) }
Write-Output "found $($cases.Count) cases"

$proxy = $null
if ($Oracle) {
    $connectionString = "Server=$OracleServer;Integrated Security=true;Connect Timeout=60"
}
else {
    $connectionString = "Server=tcp:127.0.0.1,$Port;User ID=tsql;Password=tsql;Encrypt=False;TrustServerCertificate=True;Pooling=False;Connect Timeout=15"
    if (-not $NoServer) {
        $exe = Join-Path $root "src\TdsProxy\bin\Debug\net10.0\daffydb.exe"
        $empty = Join-Path ([System.IO.Path]::GetTempPath()) "daffydb-tsql-$Port"
        New-Item -ItemType Directory -Force $empty | Out-Null
        $log = Join-Path ([System.IO.Path]::GetTempPath()) "daffydb-tsql-$Port.log"
        $proxy = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $log `
            -ArgumentList "--port $Port --data-dir `"$empty`" --no-repl"
        Start-Sleep -Seconds 4
    }
}

$failed = 0
$skipped = 0
try {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()

    foreach ($case in $cases) {
        if ($Oracle -and $case.Flags -contains 'daffy') { $skipped++; continue }

        # A case that breaks the connection fails on its own; the next one starts afresh.
        if ($connection.State -ne 'Open') {
            $connection.Dispose()
            $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
            $connection.Open()
        }

        $problem = Test-Case $connection $case
        if (-not $problem) { Write-Output "ok   $($case.Name)"; continue }

        if ($Oracle -and $case.Flags -contains 'differs') {
            Write-Output "diff $($case.Name)  (known difference)`n    $problem"
            continue
        }
        $failed++
        Write-Output "FAIL $($case.Name)`n    $problem"
    }
    $connection.Close()
}
finally {
    if ($proxy -and -not $proxy.HasExited) { Stop-Process -Id $proxy.Id -Force }
    if ($proxy) { Remove-Item (Join-Path $env:LOCALAPPDATA "DaffyDB\daffydb-$Port.pbids") -ErrorAction SilentlyContinue }
}

$target = if ($Oracle) { "SQL Server ($OracleServer)" } else { "DaffyDB" }
Write-Output "========================================================"
Write-Output "$($cases.Count - $failed - $skipped) of $($cases.Count - $skipped) cases match on $target"
exit $(if ($failed -eq 0) { 0 } else { 1 })
