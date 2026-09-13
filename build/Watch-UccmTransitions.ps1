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

.PARAMETER SelfTest
    Checks the framing, the GPS epoch decode, the lock table, the watched-byte events and the
    silence logic, and then replays the committed 13 Sep 2026 capture back through the framing to
    the events file recorded beside it. Needs no port.

.NOTES
    WHY A SELF-TEST FOR A SCRIPT THAT RUNS ONCE A SEASON. The states this exists to record -
    power-up, cold acquisition, holdover - happen while someone is standing at the bench with
    their hand on the antenna, and a bug found afterwards cannot be retried without borrowing the
    hardware again. So the half that needs no serial port is checked on every push. The serial
    half cannot be, and the self-test says so when it passes. Same bargain as Capture-Fixtures.ps1.
#>
[CmdletBinding()]
param(
    [string] $Port = 'COM3',
    [int] $BaudRate = 57600,
    [string] $OutputDirectory,
    [string] $Label = 'transitions',
    [int] $ProbeEverySeconds = 60,
    [int] $MaxMinutes = 180,
    [bool] $Probe = $true,
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'



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
# ---------------------------------------------------------------------------------------------
# The reading. Pure, so -SelfTest can drive it with no port - and so the capture this script
# already produced can be replayed back through it. Lifted out of the loop below on 14 Sep 2026
# (#544) without changing what any of it does; the replay is what proves that rather than asserts
# it, because it reproduces the committed .events.txt from the committed .frames.txt.
# ---------------------------------------------------------------------------------------------

$script:FrameLength = 44

<#
.SYNOPSIS
    Takes the first complete C5..CA time code out of the buffer, or $null if there is not one yet.
.DESCRIPTION
    ANCHORED ON LENGTH, NOT ON THE TERMINATOR. A frame is a 0xC5 with a 0xCA exactly 43 bytes
    after it. Scanning forward to the first 0xCA instead would cut six of the 785 frames in the
    13 Sep 2026 sitting in half, because offset 30 is the low byte of the GPS seconds counter and
    reaches 0xCA once every 256 seconds - so the defect would appear twice an hour and never on
    the bench in the ten minutes anyone was watching.

    Whatever precedes the frame is discarded with it. That is deliberate: it is how the listener
    picks itself up after a power cycle leaves half a frame on the wire.
#>
function Read-UccmFrame {
    param([System.Collections.Generic.List[byte]] $Buffer)

    $start = -1
    for ($i = 0; $i -le $Buffer.Count - $script:FrameLength; $i++) {
        if ($Buffer[$i] -eq 0xC5 -and $Buffer[$i + ($script:FrameLength - 1)] -eq 0xCA) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }

    $frame = New-Object byte[] $script:FrameLength
    $Buffer.CopyTo($start, $frame, 0, $script:FrameLength)
    $Buffer.RemoveRange(0, $start + $script:FrameLength)

    # The leading comma is load-bearing. Without it PowerShell unrolls the array and the caller
    # receives 44 loose bytes instead of one frame.
    , $frame
}

<#
.SYNOPSIS
    The GPS seconds counter, offsets 27-30, big-endian. Confirmed against hardware.
#>
function Get-FrameSeconds {
    param([byte[]] $Frame)

    ([uint32]$Frame[27] -shl 24) -bor ([uint32]$Frame[28] -shl 16) -bor
    ([uint32]$Frame[29] -shl 8) -bor [uint32]$Frame[30]
}

<#
.SYNOPSIS
    That counter as a wall-clock GPS time.
.DESCRIPTION
    THE EPOCH MUST NOT BE PARSED AS A LOCAL TIME. This read `[datetime]'1980-01-06T00:00:00Z'`
    until 13 Sep 2026, and PowerShell converts that to local time: 6 January falls inside
    Australian daylight saving, so the epoch was taken as UTC+11 and every annotation came out 11
    hours late. transitions-13sep2026.events.txt still carries the wrong ones and should - they
    are what the run produced, and only this convenience line was ever affected.

    IT IS INVISIBLE WHEREVER LOCAL TIME IS UTC, which is most build agents. So the self-test pins
    the returned type and offset as well as the value: the old form returns a [datetime] of
    Kind=Local, which fails everywhere, where the value alone fails only in a timezone.
#>
function Get-GpsEpochTime {
    param([uint32] $Seconds)

    [DateTimeOffset]::new(1980, 1, 6, 0, 0, 0, [TimeSpan]::Zero).AddSeconds($Seconds)
}

<#
.SYNOPSIS
    What moved in the watched bytes since the last frame, as the lines to log.
.DESCRIPTION
    Reads $Previous and never writes it, so the caller alone decides when a gap wipes the record.
    Returns nothing for almost every frame, which is the point - 785 frames carried 12 changes.
#>
function Get-WatchedByteEvents {
    param([byte[]] $Frame, [hashtable] $Previous, [hashtable] $Watched)

    $events = @()
    foreach ($offset in $Watched.Keys) {
        $value = $Frame[$offset]
        if ($Previous.ContainsKey($offset) -and $Previous[$offset] -ne $value) {
            $name = $Watched[$offset]
            $events += $(if ($offset -eq 35) {
                '*** {0} [{1}] 0x{2:X2} -> 0x{3:X2}   {4} -> {5}' -f $name, $offset,
                    $Previous[$offset], $value,
                    (Get-LockName $Previous[$offset]), (Get-LockName $value)
            }
            else {
                '    {0} [{1}] 0x{2:X2} -> 0x{3:X2}' -f $name, $offset, $Previous[$offset], $value
            })
        }
        elseif (-not $Previous.ContainsKey($offset)) {
            if ($offset -eq 35) {
                $events += ('first frame: LockState [35] 0x{0:X2} = {1}' -f $value, (Get-LockName $value))
            }
        }
    }

    # No wrapping comma here, unlike Read-UccmFrame: every caller collects this with @() or
    # foreach, and a wrapped array would arrive as ONE element that stringifies its contents
    # space-joined - two transitions in one frame silently becoming one line of nonsense.
    $events
}

<#
.SYNOPSIS
    Whether the wire has gone quiet, or come back. Returns the state to carry forward and the line
    to log, if any.
.DESCRIPTION
    THE MODULE GOING AWAY IS DATA, NOT AN ERROR. Eight seconds is four missed broadcasts at one
    every two - long enough not to fire on a late frame, short enough that the gap length is a
    usable power-cycle timestamp. The 13 Sep sitting logged 18.2 s and the capture note reads that
    as the down time.

    Strictly greater than, so a module exactly at the threshold is not yet missing.
#>
function Get-ListenerState {
    param(
        [int] $BytesRead,
        [datetime] $Now,
        [datetime] $LastByteAt,
        [bool] $InGap,
        [double] $SilenceThresholdSeconds = 8
    )

    $state = @{ InGap = $InGap; LastByteAt = $LastByteAt; Event = $null; Resumed = $false }

    if ($BytesRead -gt 0) {
        if ($InGap) {
            $silence = ($Now - $LastByteAt).TotalSeconds
            $state.Event = 'DATA RESUMED after {0:N1} s of silence - the module is back on the wire.' -f $silence
            $state.InGap = $false
            $state.Resumed = $true
        }
        $state.LastByteAt = $Now
    }
    elseif (-not $InGap -and ($Now - $LastByteAt).TotalSeconds -gt $SilenceThresholdSeconds) {
        $state.Event = 'SILENCE - no bytes for 8 s. If this is the power cycle, the gap length is the down time.'
        $state.InGap = $true
    }

    $state
}

<#
.SYNOPSIS
    Reads a .frames.txt written by this script back into frames, for the replay below.
.DESCRIPTION
    Each line is `HH:mm:ss.fff` then 44 hex bytes. Returns the timestamp beside the bytes, because
    the gap that resets the watched-byte record is visible only in the timestamps.
#>
function Read-CapturedFrames {
    param([string] $Path)

    $frames = @()
    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        if ($line -notmatch '^(?<time>\d\d:\d\d:\d\d\.\d\d\d)\s+(?<hex>[0-9A-F]{2}(\s[0-9A-F]{2})+)\s*$') { continue }
        $bytes = [byte[]]($Matches['hex'] -split '\s+' | ForEach-Object { [Convert]::ToByte($_, 16) })
        $frames += [pscustomobject]@{ Time = [TimeSpan]::Parse($Matches['time']); Bytes = $bytes }
    }
    , $frames
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

# ---------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------

if ($SelfTest) {
    $failures = @()

    # A well-formed frame in the shape this module sends, with the five watched bytes settable.
    $newFrame = {
        param([byte] $Lock = 0x4F, [uint32] $Seconds = 0, [byte] $Byte32 = 0x12, [byte] $Pps = 0x60,
              [byte] $Byte34 = 0x04, [byte] $DateValidity = 0x80)

        $f = New-Object byte[] 44
        $f[0] = 0xC5
        $f[43] = 0xCA
        $f[27] = [byte](($Seconds -shr 24) -band 0xFF)
        $f[28] = [byte](($Seconds -shr 16) -band 0xFF)
        $f[29] = [byte](($Seconds -shr 8) -band 0xFF)
        $f[30] = [byte]($Seconds -band 0xFF)
        $f[32] = $Byte32
        $f[33] = $Pps
        $f[34] = $Byte34
        $f[35] = $Lock
        $f[36] = $DateValidity
        , $f
    }

    $newBuffer = {
        param([byte[]] $Bytes)
        $list = New-Object System.Collections.Generic.List[byte]
        $list.AddRange($Bytes)
        , $list
    }

    # --- Framing -----------------------------------------------------------------------------

    $one = & $newFrame
    $buf = & $newBuffer $one
    $read = Read-UccmFrame -Buffer $buf
    if ($null -eq $read) { $failures += 'a well-formed frame was not found at all' }
    elseif ($read -isnot [byte[]]) { $failures += "a frame came back as $($read.GetType().Name) rather than byte[] - the array was unrolled" }
    elseif ($read.Length -ne 44) { $failures += "a frame came back $($read.Length) bytes long" }
    elseif (@(Compare-Object $read $one -SyncWindow 0).Count -ne 0) { $failures += 'the frame read back is not the frame put in' }
    if ($buf.Count -ne 0) { $failures += "the frame was not consumed: $($buf.Count) byte(s) left" }
    if ($null -ne (Read-UccmFrame -Buffer $buf)) { $failures += 'an empty buffer produced a frame' }

    # THE CASE THE REAL CAPTURE CONTAINS. Offset 30 is the low byte of the GPS seconds counter, so
    # it is 0xCA once every 256 seconds - six times in the 13 Sep sitting. A scan that stopped at
    # the first 0xCA would halve those frames and hand the tail on as fresh bytes.
    $innerCa = & $newFrame 0x4F 0x000000CA
    if ($innerCa[30] -ne 0xCA) { $failures += 'the inner-0xCA fixture does not actually contain one' }
    $buf = & $newBuffer $innerCa
    $read = Read-UccmFrame -Buffer $buf
    if ($null -eq $read -or $read.Length -ne 44 -or $read[43] -ne 0xCA) {
        $failures += 'a frame carrying 0xCA at offset 30 was mis-framed'
    }
    if ($buf.Count -ne 0) { $failures += 'a frame carrying an inner 0xCA left bytes behind' }

    # Noise before the frame is discarded with it - that is how the listener recovers after a
    # power cycle leaves half a frame on the wire.
    $buf = & $newBuffer ([byte[]](@(0x11, 0x22, 0x33) + $one))
    $read = Read-UccmFrame -Buffer $buf
    if ($null -eq $read -or @(Compare-Object $read $one -SyncWindow 0).Count -ne 0) {
        $failures += 'a frame behind three bytes of noise was not recovered'
    }
    if ($buf.Count -ne 0) { $failures += 'the noise ahead of a frame was not discarded with it' }

    # A 0xC5 that is not a frame must not swallow the frame behind it. The stray byte stays at the
    # head of the buffer, which is as-run behaviour and harmless: the scan starts from zero every
    # time, so the real frame is still found.
    $stray = [byte[]](@(0xC5, 0x00, 0x00) + $one)
    if ($stray[43] -eq 0xCA) { $failures += 'the stray-0xC5 fixture accidentally frames up at offset 0' }
    $buf = & $newBuffer $stray
    $read = Read-UccmFrame -Buffer $buf
    if ($null -eq $read -or @(Compare-Object $read $one -SyncWindow 0).Count -ne 0) {
        $failures += 'a stray 0xC5 ahead of a real frame lost the frame'
    }

    # Two frames back to back, then nothing. A scan that failed to advance would return the first
    # one forever.
    $second = & $newFrame 0x45
    $buf = & $newBuffer ([byte[]]($one + $second))
    $a = Read-UccmFrame -Buffer $buf
    $b = Read-UccmFrame -Buffer $buf
    if ($null -eq $a -or $null -eq $b) { $failures += 'two adjacent frames were not both found' }
    elseif ($a[35] -ne 0x4F -or $b[35] -ne 0x45) { $failures += 'two adjacent frames came back in the wrong order or as copies' }
    if ($null -ne (Read-UccmFrame -Buffer $buf)) { $failures += 'a third frame appeared from two' }

    # A frame still arriving is not a frame yet, and its bytes must survive until the rest lands.
    $buf = & $newBuffer ([byte[]]$one[0..42])
    if ($null -ne (Read-UccmFrame -Buffer $buf)) { $failures += '43 bytes were read as a whole frame' }
    if ($buf.Count -ne 43) { $failures += 'a partial frame was consumed and lost' }
    $buf.Add(0xCA)
    if ($null -eq (Read-UccmFrame -Buffer $buf)) { $failures += 'a frame completed by a later byte was not found' }

    # --- The GPS seconds counter, and the epoch defect this script shipped with ----------------

    $secs = Get-FrameSeconds -Frame (& $newFrame 0x4F 619315254)
    if ($secs -ne 619315254) { $failures += "the seconds counter read back as $secs" }

    # The high bit set. The shift must stay unsigned; a signed one would go negative here and the
    # annotation would be a date in 1912.
    $high = Get-FrameSeconds -Frame (& $newFrame 0x4F 2147483649)
    if ($high -ne 2147483649) { $failures += "a counter with the high bit set read back as $high" }

    # THE DEFECT ITSELF. 619315254 is the first frame of the 13 Sep sitting. The old form printed
    # 11:00:54 for it, which is what the committed .events.txt still says.
    $epoch = Get-GpsEpochTime -Seconds 619315254
    if ($epoch -isnot [DateTimeOffset]) {
        $failures += "the GPS epoch came back as $($epoch.GetType().Name) - a [datetime] is the local-time form"
    }
    elseif ($epoch.Offset -ne [TimeSpan]::Zero) {
        $failures += "the GPS epoch carries an offset of $($epoch.Offset) rather than UTC"
    }
    if ($epoch.ToString('yyyy-MM-dd HH:mm:ss') -ne '1999-08-22 00:00:54') {
        $failures += "the GPS epoch decoded to $($epoch.ToString('yyyy-MM-dd HH:mm:ss'))"
    }

    # The other decode the capture note publishes by hand, from the holdover frame. It agrees with
    # that frame's own capture timestamp once the 18 s leap offset is taken off, which is what
    # confirms offsets 27-30 and the byte order independently of the 12 Sep sitting.
    $holdover = Get-GpsEpochTime -Seconds (Get-FrameSeconds -Frame (& $newFrame 0x4F 0x57D0B062))
    if ($holdover.ToString('yyyy-MM-dd HH:mm:ss') -ne '2026-09-13 00:27:14') {
        $failures += "the holdover frame decoded to $($holdover.ToString('yyyy-MM-dd HH:mm:ss'))"
    }

    # --- Heather's lock table ------------------------------------------------------------------

    foreach ($pair in @(@(0x45, 'LOCKED'), @(0x4F, 'SETTLING'), @(0x41, 'POWER-UP'),
                        @(0x85, 'LOCKED (Symmetricom value)'), @(0x8F, 'SETTLING (Symmetricom value)'))) {
        $got = Get-LockName $pair[0]
        if ($got -ne $pair[1]) { $failures += ("0x{0:X2} named '{1}' rather than '{2}'" -f $pair[0], $got, $pair[1]) }
    }

    # An unrecorded value must say so and show its hex rather than being folded into a known state.
    # This is not hypothetical: the antenna byte read 0x08 on this module, a value in nobody's list.
    if ((Get-LockName 0x08) -ne 'unknown 0x08') { $failures += 'an unrecorded lock value was not reported as unknown' }

    # --- Watched bytes -------------------------------------------------------------------------

    $watchedBytes = @{ 32 = 'byte32'; 33 = 'PpsState'; 34 = 'byte34'; 35 = 'LockState'; 36 = 'DateValidity' }

    # The first frame announces the lock state and nothing else. Announcing all five would bury the
    # one line that matters under four that say only that the script has started.
    $prev = @{}
    $firstEvents = @(Get-WatchedByteEvents -Frame (& $newFrame 0x4F) -Previous $prev -Watched $watchedBytes)
    if ($firstEvents.Count -ne 1) { $failures += "the first frame produced $($firstEvents.Count) event(s), expected 1" }
    elseif ($firstEvents[0] -ne 'first frame: LockState [35] 0x4F = SETTLING') {
        $failures += "the first frame announced '$($firstEvents[0])'"
    }

    # PURITY. The caller wipes $Previous when the module comes back after a gap, which is the only
    # reason the second power-up gets a 'first frame' line of its own. A function that wrote to it
    # would make that reset unreachable.
    if ($prev.Count -ne 0) { $failures += 'Get-WatchedByteEvents wrote to $Previous; the gap reset depends on it not doing so' }

    foreach ($offset in $watchedBytes.Keys) { $prev[$offset] = (& $newFrame 0x4F)[$offset] }

    # Almost every frame says nothing at all.
    if (@(Get-WatchedByteEvents -Frame (& $newFrame 0x4F) -Previous $prev -Watched $watchedBytes).Count -ne 0) {
        $failures += 'an unchanged frame produced an event'
    }

    # The lock byte gets the marker and both names; this is the line a reader scans the log for.
    $lockEvents = @(Get-WatchedByteEvents -Frame (& $newFrame 0x45) -Previous $prev -Watched $watchedBytes)
    if ($lockEvents.Count -ne 1) { $failures += "a lock change produced $($lockEvents.Count) event(s)" }
    elseif ($lockEvents[0] -ne '*** LockState [35] 0x4F -> 0x45   SETTLING -> LOCKED') {
        $failures += "a lock change read '$($lockEvents[0])'"
    }

    # Any other byte gets the quiet form, with no names, because nobody knows what they mean.
    $quiet = @(Get-WatchedByteEvents -Frame (& $newFrame 0x4F 0 0x12 0x60 0x0C) -Previous $prev -Watched $watchedBytes)
    if ($quiet.Count -ne 1) { $failures += "a byte34 change produced $($quiet.Count) event(s)" }
    elseif ($quiet[0] -ne '    byte34 [34] 0x04 -> 0x0C') { $failures += "a byte34 change read '$($quiet[0])'" }

    # Two bytes moving in one frame are two lines. The 13 Sep capture has three such frames and
    # they are the transitions: the lock byte never moved on its own.
    $both = @(Get-WatchedByteEvents -Frame (& $newFrame 0x45 0 0x12 0x60 0x04 0x90) -Previous $prev -Watched $watchedBytes)
    if ($both.Count -ne 2) { $failures += "two bytes changing produced $($both.Count) event(s), expected 2" }

    # --- Silence, which is how a power cycle is timestamped -------------------------------------

    $t0 = Get-Date '2026-09-13T10:02:55'
    $flowing = Get-ListenerState -BytesRead 44 -Now $t0 -LastByteAt $t0.AddSeconds(-2) -InGap $false
    if ($flowing.Event) { $failures += "bytes arriving normally logged '$($flowing.Event)'" }
    if ($flowing.InGap -or $flowing.LastByteAt -ne $t0) { $failures += 'a normal read did not advance the last-byte time' }

    # Exactly at the threshold is not yet missing.
    $atFloor = Get-ListenerState -BytesRead 0 -Now $t0 -LastByteAt $t0.AddSeconds(-8) -InGap $false
    if ($atFloor.Event -or $atFloor.InGap) { $failures += 'silence of exactly 8 s was called a gap' }

    $gone = Get-ListenerState -BytesRead 0 -Now $t0 -LastByteAt $t0.AddSeconds(-9) -InGap $false
    if (-not $gone.InGap) { $failures += 'nine seconds of silence was not reported as a gap' }
    if ($gone.Event -notmatch '^SILENCE') { $failures += "silence logged '$($gone.Event)'" }

    # Once only. A gap that re-announced itself every 120 ms would bury the capture under itself.
    $still = Get-ListenerState -BytesRead 0 -Now $t0.AddSeconds(30) -LastByteAt $t0.AddSeconds(-9) -InGap $true
    if ($still.Event) { $failures += 'an ongoing gap announced itself a second time' }
    if (-not $still.InGap) { $failures += 'an ongoing gap ended itself' }

    # The number in this line is the down time, and it is what the capture note quotes.
    $back = Get-ListenerState -BytesRead 44 -Now $t0.AddSeconds(18.2) -LastByteAt $t0 -InGap $true
    if (-not $back.Resumed) { $failures += 'the module coming back was not reported as a resume' }
    if ($back.InGap) { $failures += 'the gap did not end when bytes returned' }
    if ($back.Event -notmatch '18\.2 s of silence') { $failures += "the resume logged '$($back.Event)'" }

    # --- THE REPLAY: the committed capture, back through the reader it came out of --------------
    #
    # Everything above is a fixture this script's author invented. This part is not: it is the
    # 785 frames of the 13 Sep 2026 sitting, pushed back through the framing as one contiguous
    # stream and expected to produce the .events.txt that is committed beside them. It is the only
    # check here that could notice the reading of a real module changing.

    $captureDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/WinZ3805A.Tests/Uccm/Captures'
    $framesFile = Join-Path $captureDirectory 'transitions-13sep2026.frames.txt'
    $eventsFile = Join-Path $captureDirectory 'transitions-13sep2026.events.txt'

    if (-not (Test-Path $framesFile) -or -not (Test-Path $eventsFile)) {
        $failures += "the 13 Sep capture is not where this expects it: $captureDirectory"
    }
    else {
        $captured = Read-CapturedFrames -Path $framesFile
        if ($captured.Count -ne 785) { $failures += "the capture parsed to $($captured.Count) frames, not 785" }

        # One contiguous stream, as it arrived, rather than one buffer per frame - so the framing
        # has to find every boundary itself.
        $stream = New-Object System.Collections.Generic.List[byte]
        foreach ($f in $captured) { $stream.AddRange($f.Bytes) }

        $recovered = @()
        while ($true) {
            $frame = Read-UccmFrame -Buffer $stream
            if ($null -eq $frame) { break }
            $recovered += , $frame
        }
        if ($recovered.Count -ne $captured.Count) {
            $failures += "framing the capture as a stream recovered $($recovered.Count) of $($captured.Count) frames"
        }
        if ($stream.Count -ne 0) { $failures += "$($stream.Count) byte(s) of the capture were left unframed" }

        $mismatched = 0
        for ($i = 0; $i -lt [Math]::Min($recovered.Count, $captured.Count); $i++) {
            if (@(Compare-Object $recovered[$i] $captured[$i].Bytes -SyncWindow 0).Count -ne 0) { $mismatched++ }
        }
        if ($mismatched -ne 0) { $failures += "$mismatched frame(s) came back different from the file" }

        # The eight state-byte combinations the capture note publishes, with their counts. This is
        # the table #534 turned on, and a change to any offset here would move it.
        $census = @{}
        foreach ($f in $captured) {
            $key = ($f.Bytes[32..36] | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
            $census[$key] = 1 + $(if ($census.ContainsKey($key)) { $census[$key] } else { 0 })
        }
        $published = @{
            '00 41 08 4F 90' = 111; '00 41 00 4F 90' = 64; '12 60 0C 4F 90' = 462
            '12 41 04 4F 80' = 50;  '12 60 04 45 80' = 59; '12 41 00 4F 80' = 25
            '12 60 04 4F 90' = 9;   '00 41 00 4F 80' = 5
        }
        if ($census.Count -ne $published.Count) {
            $failures += "the capture holds $($census.Count) state-byte combinations; the note publishes $($published.Count)"
        }
        foreach ($key in $published.Keys) {
            $seen = $(if ($census.ContainsKey($key)) { $census[$key] } else { 0 })
            if ($seen -ne $published[$key]) { $failures += "combination '$key' appears $seen times; the note says $($published[$key])" }
        }

        # Now the events, replayed. The only state the loop carries between frames is $previous and
        # the gap, and the gap is visible here in the frame timestamps.
        $replayed = @()
        $prev = @{}
        $lastTime = $null
        foreach ($f in $captured) {
            if ($null -ne $lastTime -and ($f.Time - $lastTime).TotalSeconds -gt 8) { $prev.Clear() }
            $lastTime = $f.Time
            $replayed += @(Get-WatchedByteEvents -Frame $f.Bytes -Previous $prev -Watched $watchedBytes)
            foreach ($offset in $watchedBytes.Keys) { $prev[$offset] = $f.Bytes[$offset] }
        }

        # The committed file, minus its header, its probe chatter and its gap lines - what is left
        # is exactly what Get-WatchedByteEvents produces. The timestamp is 12 characters and two
        # spaces.
        $expected = @()
        foreach ($line in [System.IO.File]::ReadAllLines($eventsFile)) {
            if ($line.Length -lt 15 -or $line.StartsWith('#')) { continue }
            $text = $line.Substring(14)
            if ($text -match '^(\*\*\* |    |first frame: LockState)') { $expected += $text }
        }

        if ($replayed.Count -ne $expected.Count) {
            $failures += "the replay produced $($replayed.Count) watched-byte events; the capture recorded $($expected.Count)"
        }

        # Compared as a set, because two bytes moving in one frame are logged in hashtable
        # enumeration order, which is not a contract and differs between PowerShell versions. The
        # order that IS asserted is the one below, which no two of share a frame.
        $replaySorted = @($replayed | Sort-Object)
        $expectSorted = @($expected | Sort-Object)
        if (@(Compare-Object $replaySorted $expectSorted -SyncWindow 0).Count -ne 0) {
            $failures += 'the replayed events are not the events in the capture'
            foreach ($d in Compare-Object $replaySorted $expectSorted -SyncWindow 0) {
                $failures += ('  {0} {1}' -f $(if ($d.SideIndicator -eq '<=') { 'replay only:' } else { 'capture only:' }), $d.InputObject)
            }
        }

        # The lock-state story in order: settling at power-on, settling again after the power
        # cycle, locked at 10:11, unlocked when the antenna came off at 10:12, locked again at
        # 10:28. That sequence is the sitting, and it is what UccmTransitionStateTests pins.
        $storyReplay = @($replayed | Where-Object { $_ -match '^(\*\*\* |first frame:)' })
        $storyExpect = @($expected | Where-Object { $_ -match '^(\*\*\* |first frame:)' })
        if (@(Compare-Object $storyReplay $storyExpect -SyncWindow 0).Count -ne 0 -or
            $storyReplay.Count -ne $storyExpect.Count) {
            $failures += 'the lock-state sequence did not replay in the order the capture recorded'
        }
        if ($storyExpect.Count -ne 5) { $failures += "expected 5 lock-state lines in the capture, found $($storyExpect.Count)" }

        # THE CAPTURE IS EVIDENCE AND MUST NOT BE CORRECTED. Its GPS annotations are 11 hours late,
        # for the reason Get-GpsEpochTime describes. If someone ever "fixes" them in the file, the
        # file stops being what the run produced - so this asserts the wrong value is still there.
        $annotation = @([System.IO.File]::ReadAllLines($eventsFile) | Where-Object { $_ -match 'GPS seconds 619315254' })
        if ($annotation.Count -ne 1) { $failures += 'the first GPS annotation is no longer in the capture' }
        elseif ($annotation[0] -notmatch '1999-08-22 11:00:54') {
            $failures += 'the capture GPS annotation has been edited; it recorded 11:00:54, wrongly, and that is the record'
        }
    }

    if ($failures.Count -gt 0) {
        Write-Host 'FAIL' -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host 'PASS - the framing, the GPS epoch, the lock table, the watched-byte events and the'
    Write-Host '  silence logic are checked, and the 13 Sep 2026 capture replays back through the'
    Write-Host '  framing to the events file committed beside it.'
    Write-Host ''
    Write-Host '  THE SERIAL HALF IS NOT CHECKED HERE AND CANNOT BE. Opening the port, reading the'
    Write-Host '  module and probing it are exercised only on the day a receiver is on the bench,'
    Write-Host '  and the states this script exists to catch - power-up, acquisition, holdover -'
    Write-Host '  happen once and cannot be re-run without moving the hardware again. That is the'
    Write-Host '  same bargain Capture-Fixtures.ps1 makes, and the reason for checking this half on'
    Write-Host '  every push rather than on the day.'
    Write-Host ''
    Write-Host '  One sitting has been taken, on 13 Sep 2026, against a Trimble UCCM-P. A plain'
    Write-Host '  UCCM and any Symmetricom module have still never been seen, so the two'
    Write-Host '  Symmetricom values in the lock table remain Lady Heather''s claim rather than'
    Write-Host '  this bench''s observation.'
    exit 0
}

# Beside the other UCCM captures, which is where the 13 Sep sitting ended up by hand anyway.
# This read $PSScriptRoot/transitions while the script lived in a session scratchpad; now that it
# lives in build/ that default would write three files into a source directory on every run.
#
# Deliberately NOT tests/WinZ3805A.Tests/Fixtures: FixtureCorpusTests globs every *.txt there and
# asserts each one is a SmartClock status screen, so a .frames.txt written into it fails the test
# run rather than the capture. See that folder's README.
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\tests\WinZ3805A.Tests\Uccm\Captures'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$framesPath = Join-Path $OutputDirectory "$Label-$stamp.frames.txt"
$eventsPath = Join-Path $OutputDirectory "$Label-$stamp.events.txt"
$repliesPath = Join-Path $OutputDirectory "$Label-$stamp.replies.txt"
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

        $state = Get-ListenerState -BytesRead $got -Now (Get-Date) -LastByteAt $lastByteAt -InGap $inGap
        if ($state.Event) { Write-Event $state.Event }
        if ($state.Resumed) { $previous.Clear() }
        $inGap = $state.InGap
        $lastByteAt = $state.LastByteAt

        # ---- pull out any complete C5..CA frames ------------------------------------------
        while ($true) {
            $frame = Read-UccmFrame -Buffer $buffer
            if ($null -eq $frame) { break }
            $frameCount++

            $hex = ($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
            Write-Line $framesPath ('{0}  {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $hex)

            foreach ($text in (Get-WatchedByteEvents -Frame $frame -Previous $previous -Watched $watched)) {
                Write-Event $text
            }
            foreach ($offset in $watched.Keys) { $previous[$offset] = $frame[$offset] }

            if (-not $previous.ContainsKey('epoch')) {
                $seconds = Get-FrameSeconds -Frame $frame
                Write-Event ('first frame: GPS seconds {0} = {1:yyyy-MM-dd HH:mm:ss} GPS' -f $seconds,
                    (Get-GpsEpochTime -Seconds $seconds))
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
