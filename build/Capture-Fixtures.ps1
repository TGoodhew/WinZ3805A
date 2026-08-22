<#
.SYNOPSIS
    Captures §11.1 status-screen fixtures unattended, while someone moves the hardware.

.DESCRIPTION
    Issue #4 needs the receiver put into states a query cannot reach - power-up, acquiring,
    holdover, a failing health monitor. Every one of them happens on its own while the
    antenna and receiver are being moved: pulling the antenna gives holdover, powering the
    unit back up gives power-up and then acquiring, and a health line that is not [ OK ] is
    opportunistic and never more likely than during a move.

    So this watches rather than asks. Start it before touching anything, do the move, stop it
    afterwards. Every time the receiver's state changes it writes one fixture file.

    THREE THINGS IT DELIBERATELY DOES:

    1. It works on RAW BYTES and reconstructs nothing. tests/.../Fixtures/README.md says the
       exact bytes are the point - the parser derives satellite columns from token positions -
       so the framing is stripped by byte offset and what is left is written untouched. No
       text decoding, no line-ending normalisation, no trailing-whitespace trim.

    2. It EXPECTS TO LOSE THE RECEIVER, because that is what a move is. Power going away, USB
       re-enumeration and a changed port name are all normal here, not errors. It reconnects
       on its own and keeps going, and it will follow the adapter to a new COM number when
       there is exactly one candidate.

    3. It sends ONLY ':SYST:STAT?' and one '*IDN?' per connection. Both are tier S queries and
       neither disturbs a receiver or puts anything in its error queue. Nothing here writes.

    States are not enumerated in advance. The screen's own '>>' marker, its three status
    brackets and the health line form a signature, and any signature not seen before is
    captured. That is deliberate: guessing what "acquiring" prints and then only matching that
    is how a capture run ends with nothing in it.

.PARAMETER Port
    Serial port. Defaults to COM3. If it disappears and exactly one other port is present,
    that one is adopted - a re-enumerated adapter usually comes back on a different number.

.PARAMETER OutputDirectory
    Where fixtures land. Defaults to the Fixtures/captured/ folder, which is inside the tree
    .gitattributes already marks -text. Promoting one means moving it up a level and adding a
    row to Fixtures/README.md.

.PARAMETER IntervalSeconds
    Seconds between screens once connected. Default 3. The screen is about 1,900 bytes, which
    is roughly two seconds of wire time at 9600 baud, so much below 3 buys nothing.

.EXAMPLE
    pwsh build/Capture-Fixtures.ps1
    # Start it, move the hardware, press Ctrl+C when the receiver is settled again.
#>
[CmdletBinding()]
param(
    [string] $Port = 'COM3',
    [string] $OutputDirectory,
    [int]    $BaudRate = 9600,
    [int]    $IntervalSeconds = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

if (-not $OutputDirectory) {
    $root = Split-Path -Parent $PSScriptRoot
    $OutputDirectory = Join-Path $root 'tests\WinZ3805A.Tests\Fixtures\captured'
}

if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$script:serial = $null
$script:port = $Port
$script:seen = @{}
$script:written = 0

# ---------------------------------------------------------------------------
# Transport. Raw bytes throughout.
# ---------------------------------------------------------------------------

function Close-Receiver {
    if ($script:serial) {
        try { $script:serial.Close() } catch { }
        try { $script:serial.Dispose() } catch { }
        $script:serial = $null
    }
}

function Resolve-Port {
    $available = @([System.IO.Ports.SerialPort]::GetPortNames())
    if ($available -contains $script:port) { return $script:port }

    # A re-enumerated USB adapter usually returns on a different number. One candidate is
    # unambiguous; more than one is a guess, and a guess would open somebody else's device.
    if ($available.Count -eq 1) {
        Write-Host ("  {0} is gone; following the adapter to {1}." -f $script:port, $available[0]) -ForegroundColor DarkYellow
        $script:port = $available[0]
        return $script:port
    }

    return $null
}

function Open-Receiver {
    Close-Receiver

    $name = Resolve-Port
    if (-not $name) { return $false }

    try {
        $p = New-Object System.IO.Ports.SerialPort $name, $BaudRate, 'None', 8, 'One'
        $p.Handshake = 'None'
        $p.ReadTimeout = 4000
        $p.WriteTimeout = 2000
        $p.Open()
        Start-Sleep -Milliseconds 400
        $p.DiscardInBuffer()
        $script:serial = $p
        return $true
    }
    catch {
        Close-Receiver
        return $false
    }
}

# Sends one query and returns every byte of the response, framing included.
function Read-Raw {
    param([string] $Command, [int] $TimeoutMs = 8000)

    $p = $script:serial
    $p.DiscardInBuffer()
    $p.Write($Command + "`r`n")

    $buffer = New-Object System.Collections.Generic.List[byte]
    $chunk = New-Object byte[] 4096
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $quiet = $null

    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.BytesToRead -gt 0) {
            $n = $p.Read($chunk, 0, [Math]::Min($chunk.Length, $p.BytesToRead))
            for ($i = 0; $i -lt $n; $i++) { $buffer.Add($chunk[$i]) }
            $quiet = $null

            # The prompt is the terminator and carries no newline: 'scpi > ' when the error
            # queue is empty, 'E-<n>> ' while it is not. Both end '> '. Matching the word
            # would miss the error form, which is exactly when a capture matters most.
            if ($buffer.Count -ge 2 -and
                $buffer[$buffer.Count - 2] -eq 0x3E -and $buffer[$buffer.Count - 1] -eq 0x20) {
                $quiet = [DateTime]::UtcNow
            }
        }
        elseif ($quiet -and ([DateTime]::UtcNow - $quiet).TotalMilliseconds -gt 150) {
            break
        }
        else {
            Start-Sleep -Milliseconds 25
        }
    }

    return , $buffer.ToArray()
}

# Removes the echoed command from the front and the prompt from the back, by offset. What is
# returned is untouched device output.
function Remove-Framing {
    param([byte[]] $Raw, [string] $Command)

    if ($Raw.Length -eq 0) { return , [byte[]]@() }

    $start = 0
    $echo = [System.Text.Encoding]::ASCII.GetBytes($Command)

    # Echo is not on by default on every unit (#78), so it is detected, never assumed.
    $isEcho = $Raw.Length -gt $echo.Length
    if ($isEcho) {
        for ($i = 0; $i -lt $echo.Length; $i++) {
            if ($Raw[$i] -ne $echo[$i]) { $isEcho = $false; break }
        }
    }
    if ($isEcho) {
        $start = $echo.Length
        while ($start -lt $Raw.Length -and ($Raw[$start] -eq 0x0D -or $Raw[$start] -eq 0x0A)) { $start++ }
    }

    # The prompt follows the final CRLF. Keeping that CRLF is what makes the file end the way
    # the delivered fixture ends.
    $end = -1
    for ($i = $Raw.Length - 2; $i -ge $start; $i--) {
        if ($Raw[$i] -eq 0x0D -and $Raw[$i + 1] -eq 0x0A) { $end = $i + 2; break }
    }
    if ($end -lt 0) { return , [byte[]]@() }

    $length = $end - $start
    if ($length -le 0) { return , [byte[]]@() }

    $out = New-Object byte[] $length
    [Array]::Copy($Raw, $start, $out, 0, $length)
    return , $out
}

# ---------------------------------------------------------------------------
# What makes one screen different from another.
# ---------------------------------------------------------------------------

function Get-Bracket {
    param([string[]] $Lines, [string] $Prefix)

    $line = $Lines | Where-Object { $_ -match ('^' + $Prefix) } | Select-Object -First 1
    if ($line -and $line -match '\[(?<v>[^\]]*)\]') { return $Matches['v'].Trim() }
    return ''
}

function Get-ScreenFacts {
    param([byte[]] $Screen)

    # Latin1 for inspection only. The bytes written to disk are never round-tripped.
    $text = [System.Text.Encoding]::Latin1.GetString($Screen)
    $lines = $text -split "`r`n"

    $mode = ($lines | Where-Object { $_ -match '^\s*>>' } | Select-Object -First 1)
    if ($mode) {
        $mode = ($mode -replace '^\s*>>\s*', '') -replace '\s{2,}.*$', ''
        $mode = $mode.Trim()
    }
    else {
        $mode = ''
    }

    $tracking = '?'
    if ($text -match 'Tracking:\s*(?<n>\d+)') { $tracking = $Matches['n'] }

    return [pscustomobject]@{
        Mode        = $mode
        Sync        = Get-Bracket -Lines $lines -Prefix 'SYNCHRONIZATION'
        Acquisition = Get-Bracket -Lines $lines -Prefix 'ACQUISITION'
        Health      = Get-Bracket -Lines $lines -Prefix 'HEALTH MONITOR'
        Tracking    = $tracking
    }
}

function Get-Slug {
    param([object] $Facts)

    $slug = $Facts.Mode.ToLowerInvariant()
    $slug = $slug -replace '[^a-z0-9]+', '-'
    $slug = $slug.Trim('-')
    if (-not $slug) { $slug = 'unknown-mode' }

    # A failing health monitor is a state in its own right (#4) and can coincide with any
    # mode, so it qualifies the name rather than replacing it.
    if ($Facts.Health -and $Facts.Health -ne 'OK') { $slug = $slug + '-health-fail' }

    return $slug
}

# ---------------------------------------------------------------------------
# Watch.
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Capturing status-screen fixtures for #4. Reads only - nothing is sent that writes.' -ForegroundColor Cyan
Write-Host ("  port      {0} at {1}-8-N-1" -f $script:port, $BaudRate)
Write-Host ("  output    {0}" -f $OutputDirectory)
Write-Host '  stop      Ctrl+C, once the receiver has settled again'
Write-Host ''
Write-Host 'Losing the receiver is expected here. Pull the antenna, cut the power, move it,' -ForegroundColor DarkGray
Write-Host 'plug it back in - this reconnects on its own and follows the adapter if the port' -ForegroundColor DarkGray
Write-Host 'number changes.' -ForegroundColor DarkGray
Write-Host ''

$connected = $false

try {
    while ($true) {
        if (-not $connected) {
            if (Open-Receiver) {
                $connected = $true
                $idn = Remove-Framing -Raw (Read-Raw '*IDN?' 3000) -Command '*IDN?'
                $who = ([System.Text.Encoding]::Latin1.GetString($idn)).Trim()
                Write-Host ("{0}  connected on {1}: {2}" -f (Get-Date -Format 'HH:mm:ss'), $script:port, $who) -ForegroundColor Green
            }
            else {
                Write-Host ("{0}  waiting for the receiver ..." -f (Get-Date -Format 'HH:mm:ss')) -ForegroundColor DarkGray
                Start-Sleep -Seconds 2
                continue
            }
        }

        try {
            $raw = Read-Raw ':SYST:STAT?' 12000
            $screen = Remove-Framing -Raw $raw -Command ':SYST:STAT?'

            if ($screen.Length -lt 200) { throw 'short read' }

            $facts = Get-ScreenFacts -Screen $screen
            $signature = '{0}|{1}|{2}|{3}' -f $facts.Mode, $facts.Sync, $facts.Acquisition, $facts.Health

            if ($script:seen.ContainsKey($signature)) {
                Write-Host ("{0}  {1,-34} tracking {2}   (seen)" -f `
                    (Get-Date -Format 'HH:mm:ss'), $facts.Mode, $facts.Tracking) -ForegroundColor DarkGray
            }
            else {
                $script:seen[$signature] = $true
                $slug = Get-Slug -Facts $facts

                $path = Join-Path $OutputDirectory ($slug + '.txt')
                $n = 2
                while (Test-Path $path) {
                    $path = Join-Path $OutputDirectory ('{0}-{1}.txt' -f $slug, $n)
                    $n++
                }

                [System.IO.File]::WriteAllBytes($path, $screen)
                $script:written++

                Write-Host ("{0}  {1,-34} tracking {2}   -> {3}  [NEW, {4} bytes]" -f `
                    (Get-Date -Format 'HH:mm:ss'), $facts.Mode, $facts.Tracking, `
                    (Split-Path -Leaf $path), $screen.Length) -ForegroundColor Yellow

                $note = '{0}  {1}  mode="{2}" sync="{3}" acquisition="{4}" health="{5}" tracking={6}' -f `
                    (Get-Date -Format 'o'), (Split-Path -Leaf $path), $facts.Mode, $facts.Sync, `
                    $facts.Acquisition, $facts.Health, $facts.Tracking
                Add-Content -LiteralPath (Join-Path $OutputDirectory 'capture-log.txt') -Value $note
            }
        }
        catch {
            Write-Host ("{0}  lost the receiver - reconnecting" -f (Get-Date -Format 'HH:mm:ss')) -ForegroundColor DarkYellow
            Close-Receiver
            $connected = $false
            Start-Sleep -Seconds 1
            continue
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    Close-Receiver
    Write-Host ''
    Write-Host ("Captured {0} new state(s) into {1}" -f $script:written, $OutputDirectory) -ForegroundColor Cyan
    if ($script:written -gt 0) {
        Write-Host 'Promote one by moving it up a level into Fixtures/ and adding a row to its README.' -ForegroundColor DarkGray
    }
}
