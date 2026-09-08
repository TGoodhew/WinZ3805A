<#
.SYNOPSIS
    Identifies a UCCM module and records what it actually answers (#416).

.DESCRIPTION
    THE UCCM DRIVER HAS NEVER MET A RECEIVER. Every command, timeout, field
    meaning and state code in `src/WinZ3805A.Device/Drivers/Uccm/` is read out of
    Lady Heather's source rather than a vendor document or a capture, and the
    driver says so at length. #416 is explicit that step one is IDENTIFICATION,
    not code: find out what the unit is and what it says, before believing
    anything about it.

    This script is that step. It walks the baud rates, asks `*IDN?`, and then
    puts every catalogued query to the module in turn, writing each raw reply
    byte for byte with nothing stripped.

    NEITHER EXISTING CAPTURE SCRIPT CAN DO THIS, and the reasons are worth
    stating because they are the whole design.

    `Capture-Fixtures.ps1` is built for the SmartClock: it sends a mnemonic and
    strips the echoed command and the `scpi > ` prompt to leave a status screen.
    A UCCM has no such prompt, and its echo is evidence rather than noise.

    `Capture-Talker.ps1` is built for a broadcast talker, which answers nothing
    and is never asked. A UCCM is query/response.

    A UCCM is a third shape, and two of its properties are HYPOTHESES that this
    script exists to confirm or refute:

      1. THE MODULE ECHOES THE COMMAND BEFORE ANSWERING IT. Heather's
         `decode_uccm_msg()` is built around this - with no pending message id it
         matches incoming text against the mnemonics and reads the NEXT message
         as the answer. If true, the first line back is the question.

      2. UNSOLICITED `C5` TIME CODES INTERLEAVE WITH REPLIES. Heather's
         `uccm_time_line()` exists because "the Symmetricom units send a time code
         packet in the middle of another message's response".

    NOTHING HERE DEPENDS ON EITHER BEING TRUE. The script records raw bytes and
    then REPORTS what it found - how many replies began with an echo, how many
    carried an interleaved time code, whether `COMMAND COMPLETE` terminates them.
    A script that assumed the hypotheses would confirm them by construction,
    which is the one outcome that would be worthless.

.PARAMETER Port
    The serial port. Omit to list the ports present and stop, which is how to
    find out what appeared when the module was plugged in.

.PARAMETER BaudRate
    A single rate to use. Omit to walk the driver's own AutoDetectSequence
    (9600, 19200, 57600) and stop at the first that answers `*IDN?`.

.PARAMETER OutputDirectory
    Defaults to `tests/WinZ3805A.Tests/Uccm/Captures`.

.PARAMETER Label
    Names the output files. Required for a real run.

.PARAMETER SelfTest
    Checks the reply-anatomy analysis against replies whose shape is known,
    including deliberately malformed ones. Needs no port.

.NOTES
    THE PROVENANCE NOTE IS `.md` ON PURPOSE, for the reason #221 established:
    `.txt` gets collected by the fixture corpus and `.log` is gitignored, so a
    note written to either never reaches the repository. Both have happened.

    WHAT THE SELF-TEST CANNOT CHECK is opening a port and reading a module -
    that half is exercised the day one is on the bench, and the self-test says
    so when it passes.
#>

param(
    [string] $Port,
    [int] $BaudRate,
    [string] $OutputDirectory,
    [string] $Label,
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# The analysis. Pure, so the self-test can drive it without a port.
# ---------------------------------------------------------------------------------------------

<#
.SYNOPSIS
    What one line of a UCCM reply appears to be.
.DESCRIPTION
    Deliberately mirrors `UccmReply.Classify` and `UccmTimeCode.IsTimeCodeLine`. If the two ever
    disagree about a captured line, that disagreement is the finding - which is why this is a
    second implementation rather than a call into the assembly.
#>
function Get-UccmLineKind {
    [CmdletBinding()]
    param([string] $Line, [string] $Sent)

    $text = if ($null -eq $Line) { '' } else { $Line.Trim() }
    if ($text.Length -eq 0) { return 'Payload' }

    # `C5 ` anywhere, or `C5` at the start. Anywhere, because the whole point of hypothesis 2 is
    # that it arrives in the middle of something else.
    if ($text.IndexOf('C5 ', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.StartsWith('C5', [StringComparison]::OrdinalIgnoreCase)) {
        return 'TimeCode'
    }

    if ($text.IndexOf('COMMAND COMPLETE', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'Complete'
    }

    foreach ($marker in @('COMMAND ERROR', 'UNDEFINED HEADER', 'INVALID PARAMETER', 'CORRUPT')) {
        if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'Error' }
    }

    if (-not [string]::IsNullOrWhiteSpace($Sent)) {
        # Canonical form: letters, digits and '?' only, upper-cased. The module may echo with
        # different spacing or case from what was sent, and neither difference makes it not an echo.
        $canon = { param($s) (($s.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) -or $_ -eq '?' }) -join '').ToUpperInvariant() }
        if ((& $canon $text) -eq (& $canon $Sent) -and (& $canon $Sent).Length -gt 0) { return 'Echo' }
    }

    return 'Payload'
}

<#
.SYNOPSIS
    The shape of one whole reply: what came back, in what order, and what it means.
.DESCRIPTION
    This is the object the whole exercise produces. `EchoFirst`, `TimeCodeInterleaved` and
    `Terminated` are the three hypotheses stated as measurements.
#>
function Get-UccmReplyAnatomy {
    [CmdletBinding()]
    param([string] $Response, [string] $Sent)

    $lines = @()
    if (-not [string]::IsNullOrEmpty($Response)) {
        $lines = @($Response -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    }

    $kinds = @()
    foreach ($line in $lines) { $kinds += Get-UccmLineKind -Line $line -Sent $Sent }

    $payload = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($kinds[$i] -eq 'Payload') { $payload += $lines[$i].Trim() }
    }

    # "Interleaved" means a time code with a payload line after it, not merely a time code
    # present. A trailing one is an unsolicited broadcast landing after the reply finished,
    # which is ordinary; one in the MIDDLE is hypothesis 2 and is what breaks a naive reader.
    $interleaved = $false
    for ($i = 0; $i -lt $kinds.Count - 1; $i++) {
        if ($kinds[$i] -eq 'TimeCode') {
            for ($j = $i + 1; $j -lt $kinds.Count; $j++) {
                if ($kinds[$j] -eq 'Payload' -or $kinds[$j] -eq 'Complete') { $interleaved = $true; break }
            }
        }
        if ($interleaved) { break }
    }

    [pscustomobject]@{
        Sent                = $Sent
        Lines               = $lines
        Kinds               = $kinds
        PayloadLines        = $payload
        EchoFirst           = ($kinds.Count -gt 0 -and $kinds[0] -eq 'Echo')
        EchoAnywhere        = ($kinds -contains 'Echo')
        TimeCodeInterleaved = $interleaved
        TimeCodeAnywhere    = ($kinds -contains 'TimeCode')
        Terminated          = ($kinds -contains 'Complete')
        Errored             = ($kinds -contains 'Error')
    }
}

<#
.SYNOPSIS
    Renders the raw bytes of a reply as hex beside their printable form.
.DESCRIPTION
    Because the first question about an unknown module is what its line endings are, and a reply
    printed as text answers everything except that. Heather's source says nothing about them.
#>
function Format-UccmBytes {
    [CmdletBinding()]
    param([byte[]] $Bytes, [int] $Width = 16)

    $out = @()
    for ($i = 0; $i -lt $Bytes.Length; $i += $Width) {
        $slice = $Bytes[$i..([Math]::Min($i + $Width - 1, $Bytes.Length - 1))]
        $hex = ($slice | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        $txt = ($slice | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } }) -join ''
        $out += ('{0:X4}  {1,-47}  {2}' -f $i, $hex, $txt)
    }
    return $out -join "`n"
}

# ---------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------

if ($SelfTest) {
    $failures = @()

    # --- The shape Heather's source predicts, in full ---------------------------------------
    $predicted = @(
        'SYNC:TINT?',
        '-1.234E-008',
        'COMMAND COMPLETE'
    ) -join "`r`n"

    $a = Get-UccmReplyAnatomy -Response $predicted -Sent 'SYNC:TINT?'
    if (-not $a.EchoFirst) { $failures += 'the echoed command was not recognised as the first line' }
    if (-not $a.Terminated) { $failures += 'COMMAND COMPLETE was not recognised as the terminator' }
    if ($a.PayloadLines.Count -ne 1) { $failures += "expected 1 payload line, got $($a.PayloadLines.Count)" }
    if ($a.PayloadLines.Count -ge 1 -and $a.PayloadLines[0] -ne '-1.234E-008') { $failures += 'the value was not the payload' }
    if ($a.TimeCodeInterleaved) { $failures += 'a reply with no time code reported one interleaved' }

    # --- Hypothesis 2: a time code landing MID-reply -----------------------------------------
    $interleaved = @(
        'SYNC:TINT?',
        'C5 01 02 03 04 05 06',
        '-1.234E-008',
        'COMMAND COMPLETE'
    ) -join "`r`n"

    $b = Get-UccmReplyAnatomy -Response $interleaved -Sent 'SYNC:TINT?'
    if (-not $b.TimeCodeInterleaved) { $failures += 'a time code in the middle of a reply was not reported as interleaved' }
    if ($b.PayloadLines.Count -ne 1) { $failures += 'the interleaved time code was counted as a value' }
    if ($b.PayloadLines.Count -ge 1 -and $b.PayloadLines[0] -ne '-1.234E-008') { $failures += 'the value was lost to the time code' }

    # THE DISTINCTION THIS CHECK EXISTS FOR. A time code AFTER the reply is an ordinary
    # unsolicited broadcast; one in the middle is the defect-shaped case. Reporting the first as
    # the second would make every sitting look like it confirmed hypothesis 2.
    $trailing = @('SYNC:TINT?', '-1.234E-008', 'COMMAND COMPLETE', 'C5 01 02 03 04 05 06') -join "`r`n"
    $c = Get-UccmReplyAnatomy -Response $trailing -Sent 'SYNC:TINT?'
    if ($c.TimeCodeInterleaved) { $failures += 'a trailing time code was misreported as interleaved' }
    if (-not $c.TimeCodeAnywhere) { $failures += 'a trailing time code was not seen at all' }

    # --- The module NOT echoing, which is the outcome that refutes hypothesis 1 ---------------
    $noEcho = @('-1.234E-008', 'COMMAND COMPLETE') -join "`r`n"
    $d = Get-UccmReplyAnatomy -Response $noEcho -Sent 'SYNC:TINT?'
    if ($d.EchoFirst -or $d.EchoAnywhere) { $failures += 'a reply with no echo was reported as echoing' }
    if ($d.PayloadLines.Count -ne 1) { $failures += 'a reply with no echo lost its value' }

    # --- An echo whose spacing and case differ from what was sent ----------------------------
    $loose = @('sync:tint ?', '-1.234E-008') -join "`r`n"
    $e = Get-UccmReplyAnatomy -Response $loose -Sent 'SYNC:TINT?'
    if (-not $e.EchoFirst) { $failures += 'an echo differing only in case and spacing was missed' }

    # --- Errors ------------------------------------------------------------------------------
    foreach ($marker in @('COMMAND ERROR', 'UNDEFINED HEADER', 'INVALID PARAMETER', 'CORRUPT')) {
        $err = Get-UccmReplyAnatomy -Response "GPS:POS?`r`n$marker" -Sent 'GPS:POS?'
        if (-not $err.Errored) { $failures += "'$marker' was not read as an error" }
        if ($err.PayloadLines.Count -ne 0) { $failures += "'$marker' was counted as a value" }
    }

    # --- Nothing at all is not a crash -------------------------------------------------------
    foreach ($empty in @($null, '', "`r`n`r`n")) {
        $f = Get-UccmReplyAnatomy -Response $empty -Sent 'SYNC:TINT?'
        if ($f.PayloadLines.Count -ne 0 -or $f.EchoFirst -or $f.Errored) {
            $failures += 'an empty reply did not analyse to nothing'
        }
    }

    # --- A reply with no command known, which is what an unsolicited broadcast is -------------
    $unsolicited = Get-UccmReplyAnatomy -Response 'C5 01 02 03 04 05 06' -Sent $null
    if (-not $unsolicited.TimeCodeAnywhere) { $failures += 'an unsolicited time code was not recognised' }
    if ($unsolicited.EchoAnywhere) { $failures += 'a line was called an echo with no command to echo' }

    # --- The hex dump, which is how line endings get answered ---------------------------------
    $dump = Format-UccmBytes -Bytes ([byte[]][Text.Encoding]::ASCII.GetBytes("AB`r`n"))
    if ($dump -notmatch '41 42 0D 0A') { $failures += "the hex dump did not render CRLF: $dump" }
    if ($dump -notmatch 'AB\.\.') { $failures += 'the hex dump did not render unprintables as dots' }

    if ($failures.Count -gt 0) {
        Write-Host 'FAIL' -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host 'PASS - the reply anatomy, the echo rule and the time-code distinction are checked.'
    Write-Host '  Opening a port, identifying a module and reading its answers are NOT, and'
    Write-Host '  cannot be here. That half is exercised the day a UCCM is on the bench, and'
    Write-Host '  until then every claim in the UCCM driver remains a hypothesis with a citation.'
    exit 0
}

# ---------------------------------------------------------------------------------------------
# The live half
# ---------------------------------------------------------------------------------------------

if (-not $Port) {
    Write-Host 'Ports present:'
    $names = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object
    if ($names) { $names | ForEach-Object { Write-Host "  $_" } } else { Write-Host '  (none)' }
    Write-Host ''
    Write-Host 'Plug the module in, run this again, and compare - a USB adapter is enumerated per'
    Write-Host 'socket, so the same unit in a different socket can appear as a different COM.'
    Write-Host ''
    Write-Host 'Then: pwsh build/Capture-Uccm.ps1 -Port COMn -Label <what-this-sitting-is>'
    exit 0
}

if (-not $Label) {
    throw 'A -Label is required for a real run: it names the capture and its provenance note.'
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\tests\WinZ3805A.Tests\Uccm\Captures'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

# The driver's own sequence, so this script and the application walk the same rates.
$rates = if ($BaudRate) { @($BaudRate) } else { @(9600, 19200, 57600) }

# Every catalogued query, identity first. UccmCommands.All, kept in this order deliberately:
# *IDN? establishes the unit before anything else is believed about it (#416 step one).
$commands = @(
    '*IDN?',
    'SYST:STAT?',
    'SYNC:TINT?',
    'DIAG:ROSC:EFC:REL?',
    'DIAG:LOOP?',
    'LED:GPSL?',
    'SYNC:HOLD:DUR?',
    'GPS:POS:SURV:STAT?',
    'GPS:POS:SURV:PROG?'
)

function Invoke-UccmCommand {
    param([System.IO.Ports.SerialPort] $Serial, [string] $Command, [int] $WaitMs = 3000)

    $Serial.DiscardInBuffer()
    $Serial.Write($Command + "`r`n")

    $buf = New-Object System.Collections.Generic.List[byte]
    $deadline = (Get-Date).AddMilliseconds($WaitMs)
    $lastByteAt = Get-Date
    while ((Get-Date) -lt $deadline) {
        if ($Serial.BytesToRead -gt 0) {
            $chunk = New-Object byte[] $Serial.BytesToRead
            $n = $Serial.Read($chunk, 0, $chunk.Length)
            for ($i = 0; $i -lt $n; $i++) { $buf.Add($chunk[$i]) }
            $lastByteAt = Get-Date
        } else {
            # Quiet for 400 ms after something arrived means the reply is over. NOT a fixed wait:
            # the reply length is unknown and a status dump is much longer than a scalar.
            if ($buf.Count -gt 0 -and ((Get-Date) - $lastByteAt).TotalMilliseconds -gt 400) { break }
            Start-Sleep -Milliseconds 20
        }
    }
    return , $buf.ToArray()
}

$serial = $null
$found = $null
foreach ($rate in $rates) {
    Write-Host ("Trying {0} at {1}-8-N-1 ..." -f $Port, $rate) -NoNewline
    try {
        $serial = New-Object System.IO.Ports.SerialPort $Port, $rate, 'None', 8, 'One'
        $serial.ReadTimeout = 2000
        $serial.Open()
        Start-Sleep -Milliseconds 400
        $bytes = Invoke-UccmCommand -Serial $serial -Command '*IDN?' -WaitMs 3000
        $text = [Text.Encoding]::ASCII.GetString($bytes)
        if ($bytes.Length -gt 0 -and $text -match '[\x20-\x7E]{4,}') {
            Write-Host ' answered.'
            $found = $rate
            break
        }
        Write-Host ' nothing.'
        $serial.Close()
        $serial = $null
    } catch {
        Write-Host (' ' + $_.Exception.Message)
        if ($serial) { try { $serial.Close() } catch { } ; $serial = $null }
    }
}

if (-not $serial) {
    throw "Nothing answered on $Port at any of: $($rates -join ', '). A UCCM may need a different rate, or the adapter may not be wired for it."
}

$transcript = New-Object System.Text.StringBuilder
$results = @()

[void]$transcript.AppendLine("# UCCM capture: $Label")
[void]$transcript.AppendLine("# Port $Port at $found-8-N-1")
[void]$transcript.AppendLine("# Taken $(Get-Date -Format o)")
[void]$transcript.AppendLine('#')
[void]$transcript.AppendLine('# Raw, byte for byte. Nothing stripped: the echo and any interleaved time code')
[void]$transcript.AppendLine('# are the evidence, not noise.')
[void]$transcript.AppendLine('')

foreach ($command in $commands) {
    Write-Host ("  {0,-22} " -f $command) -NoNewline
    $bytes = Invoke-UccmCommand -Serial $serial -Command $command -WaitMs 5000
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    $anatomy = Get-UccmReplyAnatomy -Response $text -Sent $command

    $verdict = if ($bytes.Length -eq 0) { 'SILENT' }
    elseif ($anatomy.Errored) { 'error' }
    else { "$($anatomy.PayloadLines.Count) payload line(s)" }
    Write-Host $verdict

    $results += [pscustomobject]@{ Command = $command; Anatomy = $anatomy; Bytes = $bytes }

    [void]$transcript.AppendLine("==== SENT: $command")
    [void]$transcript.AppendLine('---- BYTES')
    [void]$transcript.AppendLine((Format-UccmBytes -Bytes $bytes))
    [void]$transcript.AppendLine('---- LINES')
    for ($i = 0; $i -lt $anatomy.Lines.Count; $i++) {
        [void]$transcript.AppendLine(('  [{0}] {1}' -f $anatomy.Kinds[$i], $anatomy.Lines[$i]))
    }
    [void]$transcript.AppendLine('')
}

$serial.Close()

$capturePath = Join-Path $OutputDirectory "$Label.txt"
[System.IO.File]::WriteAllText($capturePath, $transcript.ToString())

# --- The three hypotheses, as measurements ---------------------------------------------------
$answered = @($results | Where-Object { $_.Bytes.Length -gt 0 })
$echoed = @($answered | Where-Object { $_.Anatomy.EchoFirst })
$interleaved = @($answered | Where-Object { $_.Anatomy.TimeCodeInterleaved })
$terminated = @($answered | Where-Object { $_.Anatomy.Terminated })

$identity = ($results | Where-Object { $_.Command -eq '*IDN?' } | Select-Object -First 1)
$identityText = if ($identity -and $identity.Anatomy.PayloadLines.Count -gt 0) { $identity.Anatomy.PayloadLines[0] } else { '(none)' }

$note = @"
# $Label

Raw UCCM output, byte for byte. Written by ``build/Capture-Uccm.ps1``; nothing has been decoded,
re-terminated or trimmed. The echo and any interleaved time code are evidence, not noise.

| | |
|---|---|
| Port | $Port at $found-8-N-1 |
| Taken | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K') |
| Identity (``*IDN?``) | ``$identityText`` |
| Commands asked | $($commands.Count) |
| Commands answered | $($answered.Count) |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **$($echoed.Count) of $($answered.Count)** replies began with an echo |
| Unsolicited ``C5`` time codes interleave with replies | **$($interleaved.Count) of $($answered.Count)** replies had one mid-reply |
| ``COMMAND COMPLETE`` terminates a reply | **$($terminated.Count) of $($answered.Count)** replies carried it |

## What was happening

_Fill this in by hand: which module this is, how it was wired, whether it had an antenna and had
been running long enough to have a fix, and anything seen that the bytes alone will not explain.
Only the person who was there can write it, and a capture nobody can attribute is a file rather
than evidence._

## What to do with this

If the echo count is 0, ``UccmReply.Classify``'s echo handling is answering a question the hardware
does not ask, and the driver's remarks should be corrected rather than the code kept "just in case".
If it is $($answered.Count) of $($answered.Count), the hypothesis is confirmed and can stop being
hedged. Anything in between is the interesting case and needs reading line by line.

The same applies to the time codes: **a count of 0 does not refute hypothesis 2**, because an
interleaved broadcast depends on timing. It means this sitting did not see one, which is a weaker
statement and should be recorded as such.
"@

$notePath = Join-Path $OutputDirectory "$Label.md"
[System.IO.File]::WriteAllText($notePath, $note)

Write-Host ''
Write-Host ("Identity: {0}" -f $identityText)
Write-Host ("Echoed first:        {0} of {1}" -f $echoed.Count, $answered.Count)
Write-Host ("Time code mid-reply: {0} of {1}" -f $interleaved.Count, $answered.Count)
Write-Host ("COMMAND COMPLETE:    {0} of {1}" -f $terminated.Count, $answered.Count)
Write-Host ''
Write-Host "Wrote $capturePath"
Write-Host "Provenance in $notePath - FILL IN 'What was happening' WHILE YOU REMEMBER IT."
