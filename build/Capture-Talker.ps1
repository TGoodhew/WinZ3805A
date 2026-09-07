<#
.SYNOPSIS
    Logs a broadcast talker's raw bytes to a file, verbatim, while it runs (#420).

.DESCRIPTION
    Capture-Fixtures.ps1 cannot do this. That script is built around query and response - it sends
    a mnemonic, strips the echoed command from the front and the 'scpi > ' prompt from the back,
    and its whole notion of a capture is one framed answer. A talker answers nothing. It broadcasts
    a cycle a second whether anyone is listening or not, and what is worth keeping is the byte
    stream itself.

    #420 stage 2 says the raw log is worth more than the sitting, because it turns 'we saw it once
    outdoors' into a fixture that runs in CI for ever. This exists so that log can be taken on the
    day rather than improvised while the hardware is finally on the bench.

    WHAT IT DELIBERATELY DOES:

    1. WRITES BYTES, NOT LINES. The stream is copied to the file with no decoding, no end-of-line
       conversion and no trimming. A talker that emits a bare LF, a truncated sentence or a burst
       of noise is exactly what the parser has to survive (§11.1), so the capture must not tidy any
       of it away. The output folder is marked -text in .gitattributes for the same reason the
       SmartClock fixtures are.

    2. SUMMARISES A COPY, NEVER THE FILE. The live progress below decodes a duplicate of the buffer
       so the operator can see it is working and can tell when a state has been reached. Nothing it
       does can reach the bytes on disk.

    3. WRITES PROVENANCE AS .md, NOT .txt OR .log. Both other extensions have already gone wrong
       here: Capture-Fixtures wrote a capture-log.txt into the folder its fixtures live in and the
       corpus test collected the log as though it were a screen, and .log was tried as the fix and
       is gitignored, so the provenance never reached the repository (#221). The sidecar is .md.

    WHERE IT WRITES, AND WHY NOT Fixtures/. FixtureCorpusTests globs every *.txt under
    tests/WinZ3805A.Tests/Fixtures/ and asserts each one IS a SmartClock status screen. That check
    exists so junk cannot pad the corpus, and it would fire on an NMEA log - correctly. Talker
    captures therefore live beside the driver's own tests, in Nmea/Captures/.

    IT DOES NOT PARSE, JUDGE OR RENAME. Capture-Fixtures names files after the state it recognises,
    because a SmartClock screen announces its mode. A talker's state is spread across a cycle and
    changes continuously, so a capture here is one file for one sitting, named by the label given.
    What state it contains is a question for the tests, afterwards, against the bytes.

.PARAMETER Port
    Serial port to listen on. No default: a talker is a USB device whose port number changes, and
    guessing wrong wastes the sitting. Run without it to list the ports present.

.PARAMETER BaudRate
    Default 9600, which is what most USB modules ship at. NMEA 0183's own standard is 4800 and some
    modules use 38400; the driver's auto-detect walks all three, and this does not - it captures at
    one rate, because a capture that silently changed rate half way through would be unusable.

.PARAMETER OutputDirectory
    Where the log lands. Defaults to tests/WinZ3805A.Tests/Nmea/Captures/, inside the tree, so the
    capture is a file someone can commit rather than something in a temp folder they have to find.

.PARAMETER Label
    A short name for the sitting, used in the file name. Defaults to a timestamp.

.PARAMETER DurationMinutes
    Stops after this long. Default 30, which is #420's suggested run covering cold start,
    acquisition and steady state. Ctrl+C stops it early and still writes the provenance.

.PARAMETER SelfTest
    Exercises everything that does not need a serial port - the summariser, the sentence and talker
    accounting, and the provenance writer - against synthetic input, and says plainly which half was
    not checked. Run in CI beside Capture-Fixtures' own self-test, for the same reason: this is used
    once and the day it is used the hardware cannot be borrowed again.

.EXAMPLE
    pwsh build/Capture-Talker.ps1
    # lists the serial ports present, and stops

.EXAMPLE
    pwsh build/Capture-Talker.ps1 -Port COM7 -Label vk162-patio -DurationMinutes 30

.EXAMPLE
    pwsh build/Capture-Talker.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Port,
    [int] $BaudRate = 9600,
    [string] $OutputDirectory,
    [string] $Label,
    [int] $DurationMinutes = 30,
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# The summariser. Pure, so the self-test can drive it without a port.
# ---------------------------------------------------------------------------------------------

<#
.SYNOPSIS
    What a chunk of talker output contains, for the live display and the provenance note.
#>
function Get-TalkerSummary {
    [CmdletBinding()]
    param([string] $Text)

    $talkers = [System.Collections.Generic.SortedSet[string]]::new()
    $identifiers = [System.Collections.Generic.SortedSet[string]]::new()
    $good = 0
    $bad = 0
    $fix = $null

    foreach ($raw in ($Text -split "`n")) {
        $line = $raw.Trim("`r", ' ')
        if ($line.Length -lt 9 -or $line[0] -ne '$') { continue }

        $star = $line.LastIndexOf('*')
        if ($star -lt 0 -or $star + 2 -ge $line.Length) { $bad++; continue }

        # The checksum is an XOR of everything between the '$' and the '*'.
        $sum = 0
        for ($i = 1; $i -lt $star; $i++) { $sum = $sum -bxor [byte][char]$line[$i] }
        $stated = 0
        if (-not [int]::TryParse($line.Substring($star + 1, 2), 'HexNumber', $null, [ref]$stated)) {
            $bad++
            continue
        }

        if ($sum -ne $stated) { $bad++; continue }

        $good++
        $head = $line.Substring(1, [Math]::Min(5, $star - 1))
        if ($head.Length -ge 5) {
            [void]$talkers.Add($head.Substring(0, 2))
            [void]$identifiers.Add($head.Substring(2, 3))
        }

        # RMC field 2 is the status: A active, V void. The cheapest fix indicator there is.
        if ($head.Length -ge 5 -and $head.Substring(2, 3) -eq 'RMC') {
            $fields = $line.Substring(1, $star - 1) -split ','
            if ($fields.Count -gt 2) { $fix = $fields[2] }
        }
    }

    [pscustomobject]@{
        Good        = $good
        Bad         = $bad
        Talkers     = @($talkers)
        Identifiers = @($identifiers)
        RmcStatus   = $fix
    }
}

<#
.SYNOPSIS
    The provenance sidecar. A capture nobody can date or attribute is a file, not evidence.
#>
function Write-Provenance {
    [CmdletBinding()]
    param(
        [string] $Path,
        [string] $LogName,
        [string] $Port,
        [int] $BaudRate,
        [long] $Bytes,
        [datetime] $StartedAt,
        [datetime] $EndedAt,
        [object] $Summary)

    $lines = @(
        "# $LogName",
        '',
        'Raw talker output, byte for byte. Written by `build/Capture-Talker.ps1`; nothing has been',
        'decoded, re-terminated or trimmed.',
        '',
        '| | |',
        '|---|---|',
        "| Port | $Port at $BaudRate-8-N-1 |",
        "| Started | $($StartedAt.ToString('yyyy-MM-dd HH:mm:ss K')) |",
        "| Ended | $($EndedAt.ToString('yyyy-MM-dd HH:mm:ss K')) |",
        "| Duration | $([Math]::Round(($EndedAt - $StartedAt).TotalMinutes, 1)) min |",
        "| Bytes | $Bytes |",
        "| Sentences, checksum good | $($Summary.Good) |",
        "| Sentences, rejected | $($Summary.Bad) |",
        "| Talkers seen | $(($Summary.Talkers -join ', ')) |",
        "| Sentences seen | $(($Summary.Identifiers -join ', ')) |",
        '',
        '## What was happening',
        '',
        '_Fill this in by hand: where the antenna was, what was done to the receiver and when,',
        'and anything seen on screen that the bytes alone will not explain. The capture is worth',
        'much less without it, and only the person who was there can write it._'
    )

    Set-Content -LiteralPath $Path -Value $lines -Encoding utf8
}

# ---------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------

if ($SelfTest) {
    $failures = @()

    # A cycle with two constellations, one deliberately corrupt sentence, and a bare LF ending -
    # all three of which a real talker produces and a tidying capture would destroy.
    $good1 = 'GPRMC,200000.00,A,3726.2550,N,12210.5017,W,0.02,0.00,060926,,,A'
    $good2 = 'GLGSV,1,1,02,65,30,100,38,66,55,200,44'
    function Checksum([string] $body) {
        $sum = 0
        foreach ($c in $body.ToCharArray()) { $sum = $sum -bxor [byte][char]$c }
        '{0:X2}' -f $sum
    }
    $sample = "`$$good1*$(Checksum $good1)`n`$$good2*$(Checksum $good2)`n`$GPGGA,BROKEN*00`n"

    $summary = Get-TalkerSummary -Text $sample

    if ($summary.Good -ne 2) { $failures += "expected 2 good sentences, got $($summary.Good)" }
    if ($summary.Bad -ne 1) { $failures += "expected 1 rejected sentence, got $($summary.Bad)" }
    if (($summary.Talkers -join ',') -ne 'GL,GP') { $failures += "talkers were $($summary.Talkers -join ',')" }
    if (($summary.Identifiers -join ',') -ne 'GSV,RMC') { $failures += "identifiers were $($summary.Identifiers -join ',')" }
    if ($summary.RmcStatus -ne 'A') { $failures += "RMC status read as '$($summary.RmcStatus)'" }

    # A checksum that is merely present must not be trusted: the whole point of recording a
    # rejected count is that a bad rate produces sentences that LOOK like sentences.
    $wrong = Get-TalkerSummary -Text "`$$good1*00`n"
    if ($wrong.Good -ne 0 -or $wrong.Bad -ne 1) {
        $failures += 'a sentence with a wrong checksum was accepted'
    }

    # Nothing at all is not a crash.
    $empty = Get-TalkerSummary -Text ''
    if ($empty.Good -ne 0 -or $empty.Bad -ne 0) { $failures += 'empty input did not summarise to zero' }

    # The provenance writer round-trips.
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "talker-selftest-$([guid]::NewGuid()).md"
    try {
        Write-Provenance -Path $tmp -LogName 'x.nmea' -Port 'COM9' -BaudRate 9600 -Bytes 123 `
            -StartedAt (Get-Date) -EndedAt (Get-Date) -Summary $summary
        $written = Get-Content -LiteralPath $tmp -Raw
        if ($written -notmatch 'COM9' -or $written -notmatch 'GL, GP') {
            $failures += 'the provenance note did not carry the port and talkers'
        }
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }

    if ($failures.Count -gt 0) {
        Write-Host 'Capture-Talker self-test FAILED.' -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host 'Capture-Talker self-test passed.'
    Write-Host '  The summariser, the checksum accounting and the provenance note are checked.'
    Write-Host '  Opening a port, reading a real talker and writing its bytes are NOT, and cannot'
    Write-Host '  be here. That half is exercised the day a receiver is on the bench.'
    exit 0
}

# ---------------------------------------------------------------------------------------------
# Capture
# ---------------------------------------------------------------------------------------------

if (-not $Port) {
    # @() because GetPortNames returns a bare string when there is exactly one port, and .Count on
    # a string is an error under StrictMode - which is the state this machine is usually in.
    $ports = @([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object)
    Write-Host 'No -Port given. Serial ports present:'
    if ($ports.Count -eq 0) { Write-Host '  (none)' } else { $ports | ForEach-Object { Write-Host "  $_" } }
    Write-Host ''
    Write-Host 'A USB talker appears as a new port when it is plugged in; compare this list before'
    Write-Host 'and after plugging it in rather than guessing.'
    exit 2
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/WinZ3805A.Tests/Nmea/Captures'
}
if (-not (Test-Path $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

if (-not $Label) { $Label = Get-Date -Format 'yyyyMMdd-HHmmss' }
$safeLabel = ($Label -replace '[^A-Za-z0-9._-]', '-')
$logPath = Join-Path $OutputDirectory "$safeLabel.nmea"
$notePath = Join-Path $OutputDirectory "$safeLabel.md"

$serial = [System.IO.Ports.SerialPort]::new($Port, $BaudRate, 'None', 8, 'One')
$serial.ReadTimeout = 500

try { $serial.Open() }
catch {
    Write-Host "Could not open ${Port}: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'If another program holds it - the app itself, a terminal - close that first.' -ForegroundColor Yellow
    exit 1
}

Write-Host "Capturing $Port at $BaudRate-8-N-1 into $logPath"
Write-Host "Stopping after $DurationMinutes min; Ctrl+C stops early and still writes the note."
Write-Host ''

$startedAt = Get-Date
$deadline = $startedAt.AddMinutes($DurationMinutes)
$file = [System.IO.File]::Open($logPath, 'Create', 'Write', 'Read')
$buffer = [byte[]]::new(4096)
$total = 0L
$tail = [System.Text.StringBuilder]::new()
$lastReport = $startedAt
$cumulative = $null

try {
    while ((Get-Date) -lt $deadline) {
        $read = 0
        try { $read = $serial.BaseStream.Read($buffer, 0, $buffer.Length) }
        catch [TimeoutException] { $read = 0 }
        catch [System.IO.IOException] { $read = 0 }

        if ($read -gt 0) {
            # The file gets the bytes and nothing else touches them.
            $file.Write($buffer, 0, $read)
            $file.Flush()
            $total += $read

            # The summary works on a decoded COPY.
            [void]$tail.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $read))
        }
        else {
            Start-Sleep -Milliseconds 100
        }

        if (((Get-Date) - $lastReport).TotalSeconds -ge 5) {
            $chunk = $tail.ToString()
            [void]$tail.Clear()
            $s = Get-TalkerSummary -Text $chunk
            $cumulative = if ($null -eq $cumulative) { $s } else {
                [pscustomobject]@{
                    Good        = $cumulative.Good + $s.Good
                    Bad         = $cumulative.Bad + $s.Bad
                    Talkers     = @(($cumulative.Talkers + $s.Talkers) | Sort-Object -Unique)
                    Identifiers = @(($cumulative.Identifiers + $s.Identifiers) | Sort-Object -Unique)
                    RmcStatus   = if ($s.RmcStatus) { $s.RmcStatus } else { $cumulative.RmcStatus }
                }
            }

            $elapsed = [Math]::Round(((Get-Date) - $startedAt).TotalMinutes, 1)
            $fixWord = switch ($cumulative.RmcStatus) {
                'A' { 'FIX' }
                'V' { 'no fix' }
                default { 'no RMC yet' }
            }
            Write-Host ("  {0,5} min  {1,8} bytes  {2,5} good  {3,3} rejected  {4}  talkers: {5}" -f `
                    $elapsed, $total, $cumulative.Good, $cumulative.Bad, $fixWord,
                (($cumulative.Talkers -join ',')))
            $lastReport = Get-Date
        }
    }
}
finally {
    $endedAt = Get-Date
    $file.Dispose()
    $serial.Close()
    $serial.Dispose()

    if ($null -eq $cumulative) {
        $cumulative = Get-TalkerSummary -Text $tail.ToString()
    }

    Write-Provenance -Path $notePath -LogName (Split-Path $logPath -Leaf) -Port $Port `
        -BaudRate $BaudRate -Bytes $total -StartedAt $startedAt -EndedAt $endedAt -Summary $cumulative

    Write-Host ''
    Write-Host "Wrote $total bytes to $logPath"
    Write-Host "Provenance in $notePath - FILL IN 'What was happening' WHILE YOU REMEMBER IT."

    if ($total -eq 0) {
        Write-Host ''
        Write-Host 'NOTHING WAS RECEIVED. The port opened, so it exists; the talker is either at a' -ForegroundColor Yellow
        Write-Host 'different rate (try 4800 and 38400), not powered, or not this port.' -ForegroundColor Yellow
    }
    elseif ($cumulative.Good -eq 0) {
        Write-Host ''
        Write-Host 'BYTES ARRIVED BUT NO SENTENCE PASSED ITS CHECKSUM. That is what a wrong baud' -ForegroundColor Yellow
        Write-Host 'rate looks like: framing errors produce plausible-looking rubbish. Try another.' -ForegroundColor Yellow
    }
}
