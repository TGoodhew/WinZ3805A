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

.PARAMETER SelfTest
    Exercises the framing, signature and file-naming logic against the delivered fixture and
    exits, without opening a serial port. Run this BEFORE the day: the states this harness
    exists to catch happen once, while the hardware is being moved, and a parsing bug found
    afterwards cannot be retried without moving it again.

.EXAMPLE
    pwsh build/Capture-Fixtures.ps1 -SelfTest
    # Checks the half that does not need hardware. Run it before the move.

.EXAMPLE
    pwsh build/Capture-Fixtures.ps1
    # Start it, move the hardware, press Ctrl+C when the receiver is settled again.
#>
[CmdletBinding()]
param(
    [string] $Port = 'COM3',
    [string] $OutputDirectory,
    [int]    $BaudRate = 9600,
    [int]    $IntervalSeconds = 3,
    [switch] $SelfTest
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

    # The Position MODE field, which is a state in its own right and was invisible here until
    # 27 Aug 2026. A screen taken during a site survey differs from one taken while holding only
    # on this line - everything the signature looked at is identical - so the harness reported
    # "(seen)" and would never capture a survey, which is one of the states the outside sitting
    # exists to collect. The surveying fixture in the corpus had to be taken by hand.
    #
    # NORMALISED, and that is the whole trick. The line reads "Survey:    1.9% complete", so
    # carrying it verbatim would make every poll a new signature and fill the corpus with a
    # screen a second. What is a distinct state is *that* a survey is running, not how far along.
    $position = ''
    if ($text -match 'MODE\s+Survey') { $position = 'Survey' }
    elseif ($text -match 'MODE\s+Hold') { $position = 'Hold' }

    return [pscustomobject]@{
        Mode        = $mode
        Sync        = Get-Bracket -Lines $lines -Prefix 'SYNCHRONIZATION'
        Acquisition = Get-Bracket -Lines $lines -Prefix 'ACQUISITION'
        Health      = Get-Bracket -Lines $lines -Prefix 'HEALTH MONITOR'
        Position    = $position
        Tracking    = $tracking
    }
}

<#
.SYNOPSIS
    What makes one captured screen a different STATE from another.
.DESCRIPTION
    Mode, synchronization, acquisition and health. Deliberately not the satellite count or any
    reading: those change every poll and would make every screen a new state, which is the failure
    this whole harness is built to avoid.
#>
function Get-Signature {
    param([object] $Facts)
    return '{0}|{1}|{2}|{3}|{4}' -f `
        $Facts.Mode, $Facts.Sync, $Facts.Acquisition, $Facts.Health, $Facts.Position
}

<#
.SYNOPSIS
    Seeds the seen-set from screens already on disk.
.DESCRIPTION
    The seen-set is in memory, so before this the harness re-captured every state it had already
    written the moment it was restarted — and it gets restarted, because a sitting is long and the
    port has to be handed to the application and back. On 27 Aug that produced three duplicate
    files, each removed by hand, each one a chance to delete the wrong one.

    Reads each screen back and asks the same question of it that the capture loop asks of a live
    one, so a file written by an older run is recognised by what it CONTAINS rather than by what it
    was named. That matters: the slug comes from the mode alone, so two genuinely different states
    can want the same name and be told apart only by their signature.
#>
function Initialize-Seen {
    param([string] $Directory)

    if (-not (Test-Path $Directory)) { return }

    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter '*.txt' -File -ErrorAction SilentlyContinue)) {
        try {
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            if ($bytes.Length -lt 200) { continue }

            $facts = Get-ScreenFacts -Screen $bytes
            $signature = Get-Signature -Facts $facts
            if (-not $script:seen.ContainsKey($signature)) {
                $script:seen[$signature] = $true
            }
        }
        catch {
            # A file that will not parse is not a reason to refuse to start. The worst case is one
            # duplicate capture, which is what this function exists to reduce rather than to promise.
            continue
        }
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

    # A survey qualifies the name from the front, matching the one fixture that had to be captured
    # by hand before the harness could see this state at all.
    if ($Facts.Position -eq 'Survey') { $slug = 'surveying-' + $slug }

    return $slug
}

# ---------------------------------------------------------------------------
# Self-test. Everything above this line is pure, and tomorrow is a one-shot.
# ---------------------------------------------------------------------------

if ($SelfTest) {
    # The watch loop wants 'Continue' - losing the receiver mid-move is normal there. A check
    # does not: the first version of this block threw four PropertyNotFoundExceptions and then
    # printed "All checks passed", because the exceptions never reached the failure counter.
    # A pre-flight that can go green while erroring is worse than no pre-flight at all.
    $ErrorActionPreference = 'Stop'
    trap {
        Write-Host ("FAIL  unhandled: {0}" -f $_.Exception.Message) -ForegroundColor Red
        exit 1
    }

    $fixture = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\WinZ3805A.Tests\Fixtures\locked-stabilizing.txt'

    Write-Host ''
    Write-Host 'Capture-Fixtures self-test - no serial port is opened.' -ForegroundColor Cyan
    Write-Host ("  fixture   {0}" -f $fixture)
    Write-Host ''

    if (-not (Test-Path $fixture)) {
        Write-Host "FAIL  the delivered fixture is missing; there is nothing to test against." -ForegroundColor Red
        exit 1
    }

    $screen = [System.IO.File]::ReadAllBytes($fixture)
    $failures = 0

    function Assert-True {
        param([string] $What, [bool] $Condition, [string] $Detail = '')

        if ($Condition) {
            Write-Host ("  ok    {0}" -f $What) -ForegroundColor DarkGray
        }
        else {
            Write-Host ("  FAIL  {0}{1}" -f $What, $(if ($Detail) { " - $Detail" } else { '' })) -ForegroundColor Red
            $script:failures++
        }
    }

    function Join-Bytes {
        param([byte[]] $A, [byte[]] $B, [byte[]] $C)

        $out = New-Object byte[] ($A.Length + $B.Length + $C.Length)
        [Array]::Copy($A, 0, $out, 0, $A.Length)
        [Array]::Copy($B, 0, $out, $A.Length, $B.Length)
        [Array]::Copy($C, 0, $out, $A.Length + $B.Length, $C.Length)
        return , $out
    }

    $ascii = [System.Text.Encoding]::ASCII
    $prompt = $ascii.GetBytes('scpi > ')

    # -----------------------------------------------------------------------
    # 1. The framing comes off and the bytes are unchanged. This is the whole
    #    contract: Fixtures/README.md says the exact bytes are the point,
    #    because the parser derives satellite columns from token positions.
    # -----------------------------------------------------------------------

    Write-Host 'Framing' -ForegroundColor White

    $noEcho = Join-Bytes @() $screen $prompt
    $stripped = Remove-Framing -Raw $noEcho -Command ':SYST:STAT?'
    Assert-True 'a screen with no echo survives byte for byte' `
        ([System.Linq.Enumerable]::SequenceEqual([byte[]]$stripped, [byte[]]$screen)) `
        ("got {0} bytes, expected {1}" -f $stripped.Length, $screen.Length)

    # #78: the bench unit does not echo, but the guide says FDUPlex ON is the
    # default, so both shapes have to work and neither may be assumed.
    $withEcho = Join-Bytes ($ascii.GetBytes(":SYST:STAT?`r`n")) $screen $prompt
    $stripped = Remove-Framing -Raw $withEcho -Command ':SYST:STAT?'
    Assert-True 'an echoed command is removed and the screen survives byte for byte' `
        ([System.Linq.Enumerable]::SequenceEqual([byte[]]$stripped, [byte[]]$screen)) `
        ("got {0} bytes, expected {1}" -f $stripped.Length, $screen.Length)

    $stripped = Remove-Framing -Raw ([byte[]]@()) -Command ':SYST:STAT?'
    Assert-True 'an empty read yields an empty screen rather than throwing' ($stripped.Length -eq 0)

    $stripped = Remove-Framing -Raw ($ascii.GetBytes('scpi > ')) -Command ':SYST:STAT?'
    Assert-True 'a prompt with no screen behind it yields nothing' ($stripped.Length -eq 0)

    # -----------------------------------------------------------------------
    # 2. The facts come off the real screen.
    # -----------------------------------------------------------------------

    Write-Host 'Facts' -ForegroundColor White

    $facts = Get-ScreenFacts -Screen $screen
    Assert-True 'mode reads from the >> marker' ($facts.Mode -eq 'Locked to GPS: stabilizing frequency') $facts.Mode
    Assert-True 'SYNCHRONIZATION bracket' ($facts.Sync -eq 'Outputs Valid/Reduced Accuracy') $facts.Sync
    Assert-True 'ACQUISITION bracket' ($facts.Acquisition -eq 'GPS 1PPS Valid') $facts.Acquisition
    Assert-True 'HEALTH MONITOR bracket' ($facts.Health -eq 'OK') $facts.Health
    Assert-True 'tracking count' ($facts.Tracking -eq '1') $facts.Tracking

    # -----------------------------------------------------------------------
    # 3. States that differ are told apart. Guessing what "acquiring" prints
    #    and only matching that is how a capture run ends with nothing in it,
    #    so the signature is what has to discriminate - mutating a real screen
    #    is the only way to check that without the states themselves.
    # -----------------------------------------------------------------------

    Write-Host 'Discrimination' -ForegroundColor White

    # Delegates to the real one: a self-test that reimplements what it is testing proves only that
    # the copy agrees with itself.
    function Signature {
        param([object] $F)
        return Get-Signature -Facts $F
    }

    function Mutate {
        param([string] $From, [string] $To)

        $text = [System.Text.Encoding]::Latin1.GetString($screen)
        if (-not $text.Contains($From)) { throw "self-test is stale: '$From' is not in the fixture" }
        return , [System.Text.Encoding]::Latin1.GetBytes($text.Replace($From, $To))
    }

    $base = Signature $facts

    $holdover = Get-ScreenFacts -Screen (Mutate '>> Locked to GPS: stabilizing frequency' '>> Holdover                           ')
    Assert-True 'a different mode is a different state' ((Signature $holdover) -ne $base)
    Assert-True 'and slugs to its own file name' ((Get-Slug -Facts $holdover) -eq 'holdover') (Get-Slug -Facts $holdover)

    $reduced = Get-ScreenFacts -Screen (Mutate '[ Outputs Valid/Reduced Accuracy ]' '[ Outputs Valid                 ]')
    Assert-True 'the same mode with a different SYNCHRONIZATION bracket is a different state' `
        ((Signature $reduced) -ne $base)

    $sick = Get-ScreenFacts -Screen (Mutate '[ OK ]' '[ FAIL ]')
    Assert-True 'a failing health monitor is a different state' ((Signature $sick) -ne $base)
    Assert-True 'and qualifies the slug rather than replacing it' `
        ((Get-Slug -Facts $sick) -eq 'locked-to-gps-stabilizing-frequency-health-fail') (Get-Slug -Facts $sick)

    # Tracking deliberately stays out of the signature: it changes every reading
    # and would write a fixture per satellite count.
    $moreSats = Get-ScreenFacts -Screen (Mutate 'Tracking: 1 ____' 'Tracking: 6 ____')
    Assert-True 'a changed satellite count is NOT a new state' ((Signature $moreSats) -eq $base)
    Assert-True 'though it is still read, for the log' ($moreSats.Tracking -eq '6') $moreSats.Tracking

    # The Position MODE field (#242). A survey and a hold differ on this line and on nothing else
    # the signature reads, so before it was included the harness reported "(seen)" for a surveying
    # screen and the corpus's one surveying fixture had to be captured by hand.
    Assert-True 'the sample screen is a receiver holding a position' ($facts.Position -eq 'Hold') $facts.Position

    $surveying = Get-ScreenFacts -Screen (Mutate 'MODE     Hold' 'MODE     Survey:    1.9% complete')
    Assert-True 'a survey is read as a survey' ($surveying.Position -eq 'Survey') $surveying.Position
    Assert-True 'and IS a new state' ((Signature $surveying) -ne $base)
    Assert-True 'and says so from the front of the slug' `
        ((Get-Slug -Facts $surveying) -eq 'surveying-locked-to-gps-stabilizing-frequency') (Get-Slug -Facts $surveying)

    # The percentage is deliberately normalised away. Carrying it would make every poll a new
    # signature and write a screen a second for two hours - the same trap tracking count is kept
    # out of the signature for, and a worse one, because a survey is exactly when it would fire.
    $laterInTheSurvey = Get-ScreenFacts -Screen (Mutate 'MODE     Hold' 'MODE     Survey:   62.7% complete')
    Assert-True 'a survey further along is the SAME state' `
        ((Signature $laterInTheSurvey) -eq (Signature $surveying))

    # -----------------------------------------------------------------------
    # 4. Every slug is a legal file name, whatever the receiver prints.
    # -----------------------------------------------------------------------

    Write-Host 'File names' -ForegroundColor White

    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($f in @($facts, $holdover, $reduced, $sick)) {
        $slug = Get-Slug -Facts $f
        # @() around the pipeline: under Set-StrictMode -Version Latest an empty pipeline is
        # $null, and $null.Count throws rather than being zero. That is the whole bug above.
        Assert-True ("'{0}' is a legal file name" -f $slug) `
            (@($slug.ToCharArray() | Where-Object { $invalid -contains $_ }).Count -eq 0)
    }

    $blank = Get-ScreenFacts -Screen ($ascii.GetBytes("nothing useful here`r`n"))
    Assert-True 'a screen with no >> marker still yields a usable name' ((Get-Slug -Facts $blank) -eq 'unknown-mode') (Get-Slug -Facts $blank)

    # -----------------------------------------------------------------------
    # 5. A restart does not re-capture what is already on disk.
    # -----------------------------------------------------------------------

    Write-Host 'Restart safety' -ForegroundColor White

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("wz-seen-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    try {
        [System.IO.File]::WriteAllBytes((Join-Path $scratch 'one.txt'), $screen)
        [System.IO.File]::WriteAllBytes((Join-Path $scratch 'two.txt'), (Mutate '>> Locked to GPS' '>> Holdover      '))

        # A log, a readme and a short file all sit in that folder in real use and none is a screen.
        Set-Content -LiteralPath (Join-Path $scratch 'capture-log.md') -Value 'not a screen'
        Set-Content -LiteralPath (Join-Path $scratch 'tiny.txt') -Value 'too short to be a screen'

        $script:seen = @{}
        Initialize-Seen -Directory $scratch

        Assert-True 'two screens on disk seed two states' ($script:seen.Count -eq 2) $script:seen.Count
        Assert-True 'a state already on disk is recognised' ($script:seen.ContainsKey((Get-Signature -Facts $facts)))
        Assert-True 'a state NOT on disk is not' (-not $script:seen.ContainsKey((Get-Signature -Facts $sick)))

        $script:seen = @{}
        Initialize-Seen -Directory (Join-Path $scratch 'does-not-exist')
        Assert-True 'a missing output directory is not an error' ($script:seen.Count -eq 0) $script:seen.Count
    }
    finally {
        $script:seen = @{}
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ''
    if ($failures -gt 0) {
        Write-Host ("{0} check(s) failed. Do not rely on this harness until they pass." -f $failures) -ForegroundColor Red
        exit 1
    }

    Write-Host 'All checks passed. The parsing half of the harness works; the serial half needs the receiver.' -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Watch.
# ---------------------------------------------------------------------------

Initialize-Seen -Directory $OutputDirectory

Write-Host ''
Write-Host 'Capturing status-screen fixtures for #4. Reads only - nothing is sent that writes.' -ForegroundColor Cyan
Write-Host ("  port      {0} at {1}-8-N-1" -f $script:port, $BaudRate)
Write-Host ("  output    {0}" -f $OutputDirectory)
if ($script:seen.Count -gt 0) {
    Write-Host ("  already   {0} state(s) recognised on disk - these will not be captured again" -f $script:seen.Count)
}
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
            $signature = Get-Signature -Facts $facts

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
                # .md, and neither .txt nor .log. The default output directory is the fixture
                # corpus, and FixtureCorpusTests globs *.txt through every subdirectory - so a
                # .txt log is collected as though it were a captured screen, and passes
                # vacuously, because §11.1 says the parser never throws and unparseable fields
                # become null (found by dry-running this on 27 Aug, before the sitting).
                #
                # .log was the first fix and was worse in a quieter way: .gitignore ignores *.log,
                # so the log was never committed and the fixtures arrived with no record of which
                # state each one was. That provenance is the whole reason this file exists, and a
                # captured screen without it is a wall of text nobody can date. .md is ignored by
                # neither, and sits beside the README already in that folder.
                Add-Content -LiteralPath (Join-Path $OutputDirectory 'capture-log.md') -Value $note
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
