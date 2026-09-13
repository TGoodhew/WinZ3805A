<#
.SYNOPSIS
    Listens to a UCCM continuously through a power cycle and an antenna reconnect, and records
    every state transition it can see (#416, #418, #481, #483).

.DESCRIPTION
    Capture-Uccm.ps1 takes ONE pass of the catalogue and stops. That is the right shape for
    identification, and the wrong shape for a transition: power-up, acquisition, survey and lock
    happen once, take minutes, and cannot be re-run without moving the hardware again.

    So this listens instead. Three things at once:

      1. PASSIVELY collects every 44-byte C5..CA time code. They arrive unsolicited about every
         two seconds whether anyone asks or not, and byte 35 is the lock state - which is the only
         oracle the project has for Heather's Trimble table. Nothing needs to be sent to get them,
         so the record survives a module that is not answering commands yet.
      2. Announces a transition the moment a watched byte changes, with a timestamp, so the log
         says WHEN rather than only what.
      3. Every -ProbeEverySeconds, asks a short list of catalogued queries and records the replies.
         :ROSC:HOLD:DUR? is in that list deliberately: asked in a state the module refuses and in
         one it does not, its two answers separate "unsupported" from "invalid in this state",
         which is the question #483 is stuck on.

    THE MODULE GOING AWAY IS DATA, NOT AN ERROR. A power cycle takes the module off the wire while
    the USB adapter stays put, so reads simply return nothing. The gap is logged with its length -
    that is the power-cycle timestamp - and listening resumes by itself.

    Everything is written as it arrives rather than at the end, so killing this script, or a laptop
    going to sleep, costs only the seconds since the last line.

.PARAMETER Probe
    Send catalogued queries as well as listening. Pass -Probe:$false for a pure listen, which is
    what you want if you suspect the questions are perturbing the thing being measured.
#>
[CmdletBinding()]
param(
    [string] $Port = 'COM3',
    [int] $BaudRate = 57600,
    [string] $OutputDirectory,
    [string] $Label = 'transitions',
    [int] $ProbeEverySeconds = 60,
    [int] $MaxMinutes = 180,
    [bool] $Probe = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot 'transitions'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$framesPath = Join-Path $OutputDirectory "$Label-$stamp.frames.txt"
$eventsPath = Join-Path $OutputDirectory "$Label-$stamp.events.txt"
$repliesPath = Join-Path $OutputDirectory "$Label-$stamp.replies.txt"

# Catalogued queries only. Short ones, so a probe costs little wire time, plus the status screen
# because it is the one reply that names the state in words.
$probeQueries = @(
    'LED:GPSL?'
    'DIAG:LOOP?'
    ':ROSC:HOLD:DUR?'
    ':GPS:POS:SURV:STAT?'
    ':GPS:POS:SURV:PROG?'
    'SYST:STAT?'
)

# Trimble's table, from Lady Heather and now partly confirmed on this bench.
function Get-LockName([int] $value) {
    switch ($value) {
        0x45 { 'LOCKED' }
        0x4F { 'SETTLING' }
        0x41 { 'POWER-UP' }
        0x85 { 'LOCKED (Symmetricom value)' }
        0x8F { 'SETTLING (Symmetricom value)' }
        default { ('unknown 0x{0:X2}' -f $value) }
    }
}

# A LOGGER MUST NOT DIE OF ITS OWN LOG. On 13 Sep 2026 this script was killed at the exact instant
# the power cycle began, because something else - a `tail -f` watching for that very event - held
# the events file open, Add-Content threw, and $ErrorActionPreference = 'Stop' turned a cosmetic
# write failure into a lost capture of a once-a-season transition. The serial read is the asset;
# every write to disk is now best-effort, retried briefly, and never fatal.
function Write-Line([string] $path, [string] $text) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Add-Content -LiteralPath $path -Value $text -ErrorAction Stop
            return
        }
        catch {
            Start-Sleep -Milliseconds 60
        }
    }
    Write-Host "  (could not write to $(Split-Path -Leaf $path); continuing - the port matters more)" -ForegroundColor DarkYellow
}

function Write-Event([string] $text) {
    $line = '{0}  {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $text
    Write-Line $eventsPath $line
    Write-Host $line
}

Write-Host ''
Write-Host "Listening on $Port at $BaudRate-8-N-1 for up to $MaxMinutes minutes." -ForegroundColor Cyan
Write-Host "  frames  -> $framesPath" -ForegroundColor DarkGray
Write-Host "  events  -> $eventsPath" -ForegroundColor DarkGray
Write-Host "  replies -> $repliesPath" -ForegroundColor DarkGray
Write-Host ''
Write-Host 'A gap in the data is the power cycle and is logged as such. Ctrl+C to stop.' -ForegroundColor DarkGray
Write-Host ''

$serial = New-Object -TypeName System.IO.Ports.SerialPort
$serial.PortName = $Port
$serial.BaudRate = $BaudRate
$serial.Parity = [System.IO.Ports.Parity]::None
$serial.DataBits = 8
$serial.StopBits = [System.IO.Ports.StopBits]::One
$serial.Handshake = [System.IO.Ports.Handshake]::None
$serial.ReadTimeout = 500
$serial.WriteTimeout = 2000
$serial.Open()

Set-Content -LiteralPath $framesPath -Value @(
    "# UCCM C5 time codes through a power cycle and antenna reconnect."
    "# $Port at $BaudRate-8-N-1. Started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')."
    "# One 44-byte frame per line: HH:mm:ss.fff then the bytes as hex."
    "#"
)
Set-Content -LiteralPath $eventsPath -Value "# Transitions and gaps. Started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')."
Set-Content -LiteralPath $repliesPath -Value "# Probe replies. Started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')."

$deadline = (Get-Date).AddMinutes($MaxMinutes)
$nextProbe = (Get-Date).AddSeconds(5)
$buffer = New-Object System.Collections.Generic.List[byte]
$lastByteAt = Get-Date
$inGap = $false
$frameCount = 0

# The bytes worth announcing. 35 is the lock state, 36 the date validity, 33 the PPS state; 32 and
# 34 moved between locked and warming and nobody knows what they are, which is reason to watch them.
$watched = @{ 32 = 'byte32'; 33 = 'PpsState'; 34 = 'byte34'; 35 = 'LockState'; 36 = 'DateValidity' }
$previous = @{}

try {
    while ((Get-Date) -lt $deadline) {

        # ---- read whatever is there -------------------------------------------------------
        $got = 0
        try {
            $waiting = $serial.BytesToRead
            if ($waiting -gt 0) {
                $chunk = New-Object byte[] $waiting
                $got = $serial.Read($chunk, 0, $waiting)
                for ($i = 0; $i -lt $got; $i++) { $buffer.Add($chunk[$i]) }
            }
        }
        catch [TimeoutException] { }

        $now = Get-Date
        if ($got -gt 0) {
            if ($inGap) {
                $silence = ($now - $lastByteAt).TotalSeconds
                Write-Event ("DATA RESUMED after {0:N1} s of silence - the module is back on the wire." -f $silence)
                $inGap = $false
                $previous.Clear()
            }
            $lastByteAt = $now
        }
        elseif (-not $inGap -and ($now - $lastByteAt).TotalSeconds -gt 8) {
            Write-Event 'SILENCE - no bytes for 8 s. If this is the power cycle, the gap length is the down time.'
            $inGap = $true
        }

        # ---- pull out any complete C5..CA frames ------------------------------------------
        while ($true) {
            $start = -1
            for ($i = 0; $i -le $buffer.Count - 44; $i++) {
                if ($buffer[$i] -eq 0xC5 -and $buffer[$i + 43] -eq 0xCA) { $start = $i; break }
            }
            if ($start -lt 0) { break }

            $frame = New-Object byte[] 44
            $buffer.CopyTo($start, $frame, 0, 44)
            $buffer.RemoveRange(0, $start + 44)
            $frameCount++

            $hex = ($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
            Write-Line $framesPath ('{0}  {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $hex)

            foreach ($offset in $watched.Keys) {
                $value = $frame[$offset]
                if ($previous.ContainsKey($offset) -and $previous[$offset] -ne $value) {
                    $name = $watched[$offset]
                    $text = if ($offset -eq 35) {
                        '*** {0} [{1}] 0x{2:X2} -> 0x{3:X2}   {4} -> {5}' -f $name, $offset,
                            $previous[$offset], $value,
                            (Get-LockName $previous[$offset]), (Get-LockName $value)
                    }
                    else {
                        '    {0} [{1}] 0x{2:X2} -> 0x{3:X2}' -f $name, $offset, $previous[$offset], $value
                    }
                    Write-Event $text
                }
                elseif (-not $previous.ContainsKey($offset)) {
                    if ($offset -eq 35) {
                        Write-Event ('first frame: LockState [35] 0x{0:X2} = {1}' -f $value, (Get-LockName $value))
                    }
                }
                $previous[$offset] = $value
            }

            # The GPS seconds counter, offsets 27-30, confirmed against hardware.
            $seconds = ([uint32]$frame[27] -shl 24) -bor ([uint32]$frame[28] -shl 16) -bor
                       ([uint32]$frame[29] -shl 8) -bor [uint32]$frame[30]
            if (-not $previous.ContainsKey('epoch')) {
                $utc = ([datetime]'1980-01-06T00:00:00Z').AddSeconds($seconds)
                Write-Event ('first frame: GPS seconds {0} = {1:yyyy-MM-dd HH:mm:ss} GPS' -f $seconds, $utc)
                $previous['epoch'] = $true
            }
        }

        # Never let the buffer grow without bound if nothing ever frames up.
        if ($buffer.Count -gt 20000) { $buffer.RemoveRange(0, 10000) }

        # ---- probe ------------------------------------------------------------------------
        if ($Probe -and (Get-Date) -ge $nextProbe) {
            $nextProbe = (Get-Date).AddSeconds($ProbeEverySeconds)
            Write-Line $repliesPath ''
            Write-Line $repliesPath ("===== PROBE at $(Get-Date -Format 'HH:mm:ss') =====")

            foreach ($query in $probeQueries) {
                try {
                    $serial.DiscardInBuffer()
                    $serial.Write("$query`r`n")
                    Start-Sleep -Milliseconds 700
                    $text = ''
                    if ($serial.BytesToRead -gt 0) {
                        $n = $serial.BytesToRead
                        $chunk = New-Object byte[] $n
                        [void]$serial.Read($chunk, 0, $n)
                        # Keep the bytes for framing too - a code can arrive inside a reply.
                        for ($i = 0; $i -lt $n; $i++) { $buffer.Add($chunk[$i]) }
                        $text = -join ($chunk | ForEach-Object {
                            if ($_ -ge 32 -and $_ -lt 127) { [char]$_ }
                            elseif ($_ -eq 10) { "`n" }
                            elseif ($_ -eq 13) { '' }
                            else { '.' }
                        })
                    }
                    Write-Line $repliesPath "---- $query"
                    Write-Line $repliesPath $(if ($text) { $text } else { '(no answer)' })

                    if ($query -eq 'LED:GPSL?' -and $text) {
                        $short = ($text -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 2) -join ' | '
                        Write-Event "probe LED:GPSL? -> $short"
                    }
                }
                catch {
                    Write-Line $repliesPath "---- $query"
                    Write-Line $repliesPath "(threw: $($_.Exception.Message))"
                }
            }
            Write-Event "probe complete; $frameCount frame(s) so far"
        }

        Start-Sleep -Milliseconds 120
    }
}
finally {
    if ($serial.IsOpen) { $serial.Close() }
    $serial.Dispose()
    Write-Host ''
    Write-Host "Stopped. $frameCount frame(s) captured." -ForegroundColor Green
    Write-Host "  $framesPath"
    Write-Host "  $eventsPath"
    Write-Host "  $repliesPath"
}
