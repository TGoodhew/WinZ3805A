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

.PARAMETER ColdStartAfterMinutes
    Sends a u-blox UBX-CFG-RST cold start this many minutes into the capture, then keeps reading.
    Zero, the default, sends nothing and leaves the receiver alone.

    This exists because #420's last outstanding case was "a fix lost while powered", and the obvious
    way to get one does not work: an inverted metal cover managed about 5 dB and a microwave oven
    with its door shut about 10 dB, and neither dented an 11-satellite fix. A cold start does not
    attenuate the signal, it throws the ephemeris away - so the receiver drops to no fix and
    reacquires with the link never going down. Measured on a VK-162 at 87 consecutive void cycles,
    then reacquisition through 2D to 3D to differential.

    resetMode is 0x02, a controlled software reset of the GNSS subsystem ONLY. That is the part that
    matters for a USB puck: a full reset re-enumerates the device and takes the port with it, which
    would end the capture rather than continue it.

.PARAMETER EnableSentences
    NMEA sentence identifiers to switch on with UBX-CFG-MSG before the capture starts - `GST`, `GBS`,
    `GRS`, `ZDA`, `VLW` and the rest of the standard set. Empty, the default, sends nothing.

    This exists for #516, whose two wanted sentences no receiver here emits by default. `GST` carries
    the position's actual uncertainty and `GBS` its RAIM integrity, and #435 filed both as waiting
    for hardware that sends them. Neither unit on the bench does, and both will if asked: measured on
    the forM8N on 12 Sep 2026, GST and GBS at 1 Hz within a second of the frames going out.

    Written to RAM, so a power cycle undoes it and the receiver returns to its factory output. That
    is the whole reason this is acceptable in a capture script - nothing is left changed for the next
    person to discover.

    A control is sent too. A CFG-MSG frame the receiver does not like is ignored in silence, so a
    sentence that never appears has two explanations - the receiver will not, or the frame was wrong
    - and they look identical in the capture. Enabling one sentence that is ALREADY arriving
    separates them: if it keeps arriving while the wanted one stays silent, the frames were
    understood and the answer is genuinely no.

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

.EXAMPLE
    pwsh build/Capture-Talker.ps1 -Port COM7 -Label form8n-fix-lost -DurationMinutes 10 -ColdStartAfterMinutes 2
    # two minutes of fix, then the fix is taken away and the reacquisition is captured
#>
[CmdletBinding()]
param(
    [string] $Port,
    [int] $BaudRate = 9600,
    [string] $OutputDirectory,
    [string] $Label,
    [int] $DurationMinutes = 30,

    # See the .PARAMETER block above: 0 sends nothing.
    [int] $ColdStartAfterMinutes = 0,

    # See the .PARAMETER block above: empty sends nothing.
    [string[]] $EnableSentences = @(),

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
    Takes the complete lines out of the live buffer, leaving a partial sentence behind.

.DESCRIPTION
    The live progress summarises what has arrived since the last report, and a report falls where
    the clock says rather than where a sentence ends. Clearing the whole buffer therefore threw
    away the half sentence at the boundary AND counted it as rejected - once per report, for the
    life of the capture. Measured against the first real sitting: the file held 14,868 good
    sentences and 0 rejected read whole, and 14,688 good with 158 REJECTED read in 5,000-byte
    chunks, none of which was a rejected sentence.

    That is not a cosmetic error in a progress line. The rejected count is the one number here
    that says a capture is worthless - it is how a wrong baud rate announces itself, because
    framing errors produce sentences that look like sentences and fail their checksum. A count
    that manufactures rejects from nothing is the diagnostic crying wolf.
#>
function Split-CompleteLines {
    [CmdletBinding()]
    param([System.Text.StringBuilder] $Buffer)

    $text = $Buffer.ToString()
    $cut = $text.LastIndexOf("`n")
    if ($cut -lt 0) { return '' }

    [void]$Buffer.Clear()
    [void]$Buffer.Append($text.Substring($cut + 1))
    $text.Substring(0, $cut + 1)
}

<#
.SYNOPSIS
    Adds one summary to a running total, so that summarising a stream in pieces and summarising it
    whole give the same answer.
#>
function Add-TalkerSummary {
    [CmdletBinding()]
    param([object] $Total, [object] $Next)

    if ($null -eq $Total) { return $Next }

    [pscustomobject]@{
        Good        = $Total.Good + $Next.Good
        Bad         = $Total.Bad + $Next.Bad
        Talkers     = @(($Total.Talkers + $Next.Talkers) | Sort-Object -Unique)
        Identifiers = @(($Total.Identifiers + $Next.Identifiers) | Sort-Object -Unique)
        RmcStatus   = if ($Next.RmcStatus) { $Next.RmcStatus } else { $Total.RmcStatus }
    }
}

<#
.SYNOPSIS
    The provenance sidecar. A capture nobody can date or attribute is a file, not evidence.
#>
<#
.SYNOPSIS
    Wraps a UBX payload in its frame: sync bytes, class, id, little-endian length, 8-bit Fletcher.
.DESCRIPTION
    Kept as a function so the checksum is reachable from -SelfTest. A wrong checksum is silently
    ignored by the receiver, which is the worst failure mode available here: the capture would run
    its full length, the cold start would never happen, and the file would look like an ordinary
    sitting with nothing to say it had failed.
#>
function New-UbxFrame {
    param(
        [Parameter(Mandatory)] [byte] $Class,
        [Parameter(Mandatory)] [byte] $Id,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [byte[]] $Payload
    )

    $length = $Payload.Length
    $body = @($Class, $Id, [byte]($length -band 0xFF), [byte](($length -shr 8) -band 0xFF)) + $Payload

    $a = 0
    $b = 0
    foreach ($byte in $body) {
        $a = ($a + $byte) -band 0xFF
        $b = ($b + $a) -band 0xFF
    }

    return [byte[]](@(0xB5, 0x62) + $body + @([byte]$a, [byte]$b))
}

<#
.SYNOPSIS
    UBX-CFG-RST, cold start, GNSS subsystem only.
.DESCRIPTION
    navBbrMask 0xFFFF clears everything in battery-backed RAM - ephemeris, almanac, position, clock
    - which is what makes the fix go away rather than merely degrade. resetMode 0x02 is a controlled
    software reset of the GNSS subsystem and leaves the host interface alone, so a USB puck keeps
    its port. 0x00, a full hardware reset, would re-enumerate and end the capture.
#>
function New-ColdStartFrame {
    return New-UbxFrame -Class 0x06 -Id 0x04 -Payload @(0xFF, 0xFF, 0x02, 0x00)
}

<#
.SYNOPSIS
    The UBX message ids of the standard NMEA sentences, for UBX-CFG-MSG.
.DESCRIPTION
    Class 0xF0 is "standard NMEA", and the id within it is the sentence. The table is written out
    rather than computed because there is no rule to compute: the ids follow the order the sentences
    were added to the protocol, not the alphabet, and GST at 0x07 sits between GRS and ZDA.

    Only the sentences this repository has a reason to ask for are listed. A shorter list is a
    better one here - an unrecognised name stops the script before it opens the port, which is
    kinder than a sitting that captures nothing and cannot say why.
#>
$script:NmeaMessageIds = @{
    DTM = 0x0A
    GBS = 0x09
    GGA = 0x00
    GLL = 0x01
    GNS = 0x0D
    GRS = 0x06
    GSA = 0x02
    GST = 0x07
    GSV = 0x03
    RMC = 0x04
    VLW = 0x0F
    VTG = 0x05
    ZDA = 0x08
}

<#
.SYNOPSIS
    UBX-CFG-MSG, setting one standard NMEA sentence's rate on the port the frame arrives on.
.DESCRIPTION
    The three-byte form is deliberate. CFG-MSG also has an eight-byte form carrying a rate for every
    port, and using it here would rewrite the configuration of five ports nobody asked about in
    order to change the one being listened to. The short form changes exactly the port the frame
    came in on.

    RAM only. Persisting it would need a CFG-CFG save afterwards, which this never sends, so the
    receiver is back to its factory output at the next power cycle.
#>
function New-MessageRateFrame {
    param(
        [Parameter(Mandatory)] [byte] $MessageId,
        [Parameter(Mandatory)] [byte] $Rate)

    return New-UbxFrame -Class 0x06 -Id 0x01 -Payload @(0xF0, $MessageId, $Rate)
}

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
        [object] $Summary,
        [int] $PartialBytes = 0,
        [object] $ColdStartAt = $null,
        [object] $ColdStartOffset = $null,
        [string[]] $Enabled = @(),
        [string] $StoppedEarly = $null)

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
        # A capture stops on a clock, not on a sentence boundary, so a file ending mid-sentence is
        # the normal case rather than damage - and it is one of the things the parser has to
        # survive (§11.1). It is reported as its own row instead of being counted as a rejected
        # sentence, because a reader who sees "rejected" reaches for the baud rate.
        "| Ends mid-sentence | $(if ($PartialBytes -gt 0) { "yes, $PartialBytes byte(s)" } else { 'no' }) |",
        # Recorded as a byte offset, not only a time, because the offset is the one thing a reader
        # can act on: everything before it is the receiver as it was, everything after it is the
        # receiver recovering, and a test that wants one or the other can split the file there.
        $(if ($null -ne $ColdStartAt) {
                "| **Cold start sent** | $(([datetime]$ColdStartAt).ToString('yyyy-MM-dd HH:mm:ss zzz')), at byte $ColdStartOffset |"
            }),
        # A run that did not reach its deadline is not a shorter run: whatever the receiver was
        # about to do next is missing, and a reader comparing this against its stated duration
        # deserves to be told rather than left to notice.
        $(if ($StoppedEarly) {
                "| **Stopped early** | yes - $StoppedEarly |"
            }),
        # Stated in the table rather than left to the reader to infer from the sentence list,
        # because the inference runs the wrong way: a capture containing GST looks like evidence
        # that this receiver sends GST, and here it is evidence only that it can be made to.
        $(if ($Enabled.Count -gt 0) {
                "| **Sentences switched on** | $($Enabled -join ', '), by UBX-CFG-MSG to RAM before the capture |"
            }),
        '',
        $(if ($Enabled.Count -gt 0) {
                @(
                    '> **This receiver does not send these by default.** They were enabled for the sitting',
                    # SINGLE quotes: a backtick inside a double-quoted PowerShell string is the escape
                    # character, so the markdown code span here came out as bare text the first time.
                    '> with `UBX-CFG-MSG` and the configuration was written to RAM, so a power cycle undoes',
                    '> it. Read the capture as what the hardware is *capable* of, not as what arrives from',
                    '> it out of the box.',
                    ''
                )
            }),
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

    # THE DEFECT THIS CHECK EXISTS FOR. Summarising a stream in pieces must total the same as
    # summarising it whole. It did not: the live buffer was cleared at every report, so the
    # sentence straddling the boundary was lost from the good count and counted as rejected. The
    # first real sitting read 14,868 good and 0 rejected whole, and 14,688 good with 158 REJECTED
    # in 5,000-byte pieces - a wrong-baud-rate alarm raised by a receiver that was working
    # perfectly. A serial read ends where the bytes stop arriving, never where a sentence ends, so
    # the boundary case is the normal one here rather than the awkward one.
    $stream = ''
    foreach ($ignored in 1..20) { $stream += "`$$good1*$(Checksum $good1)`n`$$good2*$(Checksum $good2)`n" }

    $whole = Get-TalkerSummary -Text $stream
    if ($whole.Good -ne 40 -or $whole.Bad -ne 0) {
        $failures += "the whole stream summarised to $($whole.Good) good and $($whole.Bad) rejected, not 40 and 0"
    }

    $buffer = [System.Text.StringBuilder]::new()
    $piecewise = $null
    for ($i = 0; $i -lt $stream.Length; $i += 37) {
        [void]$buffer.Append($stream.Substring($i, [Math]::Min(37, $stream.Length - $i)))
        $piecewise = Add-TalkerSummary -Total $piecewise -Next (Get-TalkerSummary -Text (Split-CompleteLines -Buffer $buffer))
    }

    if ($piecewise.Good -ne $whole.Good -or $piecewise.Bad -ne $whole.Bad) {
        $failures += "37-byte pieces gave $($piecewise.Good) good and $($piecewise.Bad) rejected, where the whole stream gives $($whole.Good) and $($whole.Bad)"
    }

    if ($buffer.Length -ne 0) {
        $failures += "$($buffer.Length) byte(s) were held back from a stream that ended on a line ending"
    }

    # A capture stops on a clock rather than on a sentence, so ending mid-sentence is the normal
    # case. The half sentence is held back, not counted as a reject.
    $cut = [System.Text.StringBuilder]::new()
    [void]$cut.Append("`$$good1*$(Checksum $good1)`n`$GPGGA,0007")
    $ended = Get-TalkerSummary -Text (Split-CompleteLines -Buffer $cut)
    if ($ended.Good -ne 1 -or $ended.Bad -ne 0) {
        $failures += "a capture ending mid-sentence summarised to $($ended.Good) good and $($ended.Bad) rejected, not 1 and 0"
    }

    if ($cut.Length -ne 11) {
        $failures += "the half sentence left behind was $($cut.Length) byte(s), not the 11 it holds"
    }

    # THE COLD-START FRAME. Checked because its failure mode is silence: a receiver ignores a frame
    # whose checksum is wrong without complaining, so a broken frame would produce a capture that
    # ran its full length, never lost its fix, and looked exactly like an ordinary sitting. Nothing
    # in the file would say the experiment had not happened.
    $frame = New-ColdStartFrame
    $hex = ($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' '

    # The bytes, stated rather than recomputed - recomputing them here with the same arithmetic
    # would agree with itself however wrong it was. B5 62 sync, 06 04 CFG-RST, 04 00 length,
    # FF FF cold start, 02 controlled GNSS-only reset, 00 reserved, then the two checksum bytes.
    if ($hex -ne 'B5 62 06 04 04 00 FF FF 02 00 0E 61') {
        $failures += "the cold-start frame is $hex, not the expected B5 62 06 04 04 00 FF FF 02 00 0E 61"
    }

    # And the checksum is over class, id, length and payload - NOT over the sync bytes. Including
    # them is the classic UBX mistake and produces a frame that is ignored.
    $noSync = New-UbxFrame -Class 0x06 -Id 0x04 -Payload @(0xFF, 0xFF, 0x02, 0x00)
    if ($noSync[0] -ne 0xB5 -or $noSync[1] -ne 0x62) {
        $failures += 'the frame does not start with the UBX sync bytes'
    }
    if ($noSync[4] -ne 0x04 -or $noSync[5] -ne 0x00) {
        $failures += 'the payload length is not little-endian in the frame'
    }

    # An empty payload still frames and still checksums, so the helper is usable for the poll
    # frames a later sitting may want.
    $empty = New-UbxFrame -Class 0x06 -Id 0x04 -Payload @()
    if ($empty.Length -ne 8) { $failures += "an empty payload framed to $($empty.Length) bytes, not 8" }

    # THE MESSAGE-RATE FRAMES (#516), for the same reason the cold-start frame is checked: a frame
    # the receiver dislikes is dropped without a word, so a wrong one produces a full-length capture
    # missing the sentence it was taken for, and a provenance note saying the sentence was enabled.
    #
    # The bytes are stated, not recomputed. B5 62 sync, 06 01 CFG-MSG, 03 00 length, F0 the standard
    # NMEA class, the sentence's id, 01 once per cycle, then the two checksum bytes. These two were
    # sent to the bench forM8N on 12 Sep 2026 and both sentences began arriving.
    $gstHex = ((New-MessageRateFrame -MessageId $script:NmeaMessageIds['GST'] -Rate 1) |
        ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    if ($gstHex -ne 'B5 62 06 01 03 00 F0 07 01 02 1E') {
        $failures += "the GST enable frame is $gstHex, not the expected B5 62 06 01 03 00 F0 07 01 02 1E"
    }

    $gbsHex = ((New-MessageRateFrame -MessageId $script:NmeaMessageIds['GBS'] -Rate 1) |
        ForEach-Object { '{0:X2}' -f $_ }) -join ' '
    if ($gbsHex -ne 'B5 62 06 01 03 00 F0 09 01 04 22') {
        $failures += "the GBS enable frame is $gbsHex, not the expected B5 62 06 01 03 00 F0 09 01 04 22"
    }

    # A rate of zero is how a sentence is switched OFF, and it must frame rather than being mistaken
    # for an absent payload byte - which is what a length computed from a truthiness test would do.
    $off = New-MessageRateFrame -MessageId $script:NmeaMessageIds['GST'] -Rate 0
    if ($off.Length -ne 11) { $failures += "a rate-0 frame is $($off.Length) bytes, not 11" }
    if ($off[4] -ne 0x03) { $failures += 'a rate-0 frame does not carry a 3-byte payload length' }

    # The ids are the protocol's, and getting one wrong enables a different sentence in silence -
    # a capture full of plausible NMEA that does not contain what was asked for.
    if ($script:NmeaMessageIds['GGA'] -ne 0x00 -or $script:NmeaMessageIds['GST'] -ne 0x07 -or
        $script:NmeaMessageIds['GBS'] -ne 0x09 -or $script:NmeaMessageIds['ZDA'] -ne 0x08) {
        $failures += 'the standard NMEA message id table does not match the protocol'
    }

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
    Write-Host '  The summariser, the checksum accounting, the cold-start and message-rate frames'
    Write-Host '  and the provenance note are checked.'
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

# Checked before the port is opened, so a typo costs a second rather than a sitting. A sentence
# name this table does not know is refused rather than skipped: silently ignoring it would produce
# a capture missing the very thing it was taken for, with a provenance note claiming otherwise.
#
# Split on commas as well as taking an array, because the two ways of running this script disagree
# about what a comma means. `pwsh build/Capture-Talker.ps1 -EnableSentences GST,GBS` from a
# PowerShell prompt binds two elements; the same text after `pwsh -File` binds ONE element spelt
# "GST,GBS", because -File does not evaluate PowerShell syntax. Accepting both costs a Split and
# removes a way to lose a sitting to an invocation that looked right.
$wanted = @(
    $EnableSentences |
        ForEach-Object { $_ -split '[,\s]+' } |
        ForEach-Object { $_.Trim().ToUpperInvariant() } |
        Where-Object { $_ })
$unknown = @($wanted | Where-Object { -not $script:NmeaMessageIds.ContainsKey($_) })
if ($unknown.Count -gt 0) {
    Write-Host "Unknown sentence(s) for -EnableSentences: $($unknown -join ', ')" -ForegroundColor Red
    Write-Host "Known: $(($script:NmeaMessageIds.Keys | Sort-Object) -join ', ')" -ForegroundColor Yellow
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
if ($ColdStartAfterMinutes -gt 0) {
    Write-Host "Sending a UBX-CFG-RST COLD START after $ColdStartAfterMinutes min - the fix will go away." -ForegroundColor Yellow
}
Write-Host ''

# The frames go out before the first byte is read, so the whole file has the same sentence set and
# a test does not have to find the point where it changed. The receiver acts on CFG-MSG within a
# cycle, and the settling pause is there so the capture does not open with a partial cycle that
# reads as a gap.
if ($wanted.Count -gt 0) {
    Write-Host "Enabling $($wanted -join ', ') with UBX-CFG-MSG (RAM only; a power cycle undoes it)." -ForegroundColor Yellow
    foreach ($sentence in $wanted) {
        $frame = New-MessageRateFrame -MessageId $script:NmeaMessageIds[$sentence] -Rate 1
        $serial.Write($frame, 0, $frame.Length)
        Write-Host ("  {0}  {1}" -f $sentence, (($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))
        Start-Sleep -Milliseconds 400
    }

    Start-Sleep -Milliseconds 1200
    $serial.DiscardInBuffer()
    Write-Host ''
}

$startedAt = Get-Date
$deadline = $startedAt.AddMinutes($DurationMinutes)
$file = [System.IO.File]::Open($logPath, 'Create', 'Write', 'Read')
$buffer = [byte[]]::new(4096)
$total = 0L
$tail = [System.Text.StringBuilder]::new()
$lastReport = $startedAt
$cumulative = $null
$coldStartSentAt = $null
$coldStartOffset = $null
$stoppedEarly = $null

try {
    while ((Get-Date) -lt $deadline) {
        $read = 0
        try { $read = $serial.BaseStream.Read($buffer, 0, $buffer.Length) }
        catch [TimeoutException] { $read = 0 }
        catch [System.IO.IOException] { $read = 0 }
        catch [System.OperationCanceledException] {
            # A cancelled read is the capture being stopped from outside rather than a device fault,
            # and the cause found on 12 Sep 2026 was ANOTHER PROCESS OPENING THE SAME PORT: a second
            # opener aborts the first's pending I/O, and the receiver is present and talking
            # throughout. Three sittings were lost to it at 78 s, 30 s and 4 s before the overlap
            # was spotted, because the failure names the read rather than the port.
            #
            # It is not a reason to throw: the bytes already written are a real sitting, and the
            # finally below writes the note that makes them usable. Breaking rather than continuing
            # keeps Ctrl+C meaning what the help says it means.
            #
            # A capture needs the port to itself for its whole length. Nothing here can enforce
            # that, so the note records the early stop and a reader is told.
            Write-Host ''
            Write-Host 'The read was cancelled - stopping early and writing the note.' -ForegroundColor Yellow
            $stoppedEarly = 'the read was cancelled'
            break
        }

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

        # The cold start goes out between reads, so the byte offset it is recorded at is exactly
        # where the receiver's old behaviour stops and its new behaviour starts. Sent once.
        if ($ColdStartAfterMinutes -gt 0 -and $null -eq $coldStartSentAt -and
            ((Get-Date) - $startedAt).TotalMinutes -ge $ColdStartAfterMinutes) {
            $frame = New-ColdStartFrame
            $serial.Write($frame, 0, $frame.Length)
            $coldStartSentAt = Get-Date
            $coldStartOffset = $total
            Write-Host ("  COLD START sent at byte {0}: {1}" -f $total,
                (($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')) -ForegroundColor Yellow
        }

        if (((Get-Date) - $lastReport).TotalSeconds -ge 5) {
            # Complete lines only. Whatever sentence is half-arrived stays in the buffer for the
            # next report rather than being summarised as a broken one and thrown away.
            $s = Get-TalkerSummary -Text (Split-CompleteLines -Buffer $tail)
            $cumulative = Add-TalkerSummary -Total $cumulative -Next $s

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

    # Whatever arrived since the last report still counts, including on a capture that ended before
    # the first one. What is left in the buffer afterwards is the half sentence the receiver was
    # mid-way through when the clock ran out; it is reported as such rather than as a reject.
    $cumulative = Add-TalkerSummary -Total $cumulative -Next (Get-TalkerSummary -Text (Split-CompleteLines -Buffer $tail))
    $partialBytes = $tail.Length

    Write-Provenance -Path $notePath -LogName (Split-Path $logPath -Leaf) -Port $Port `
        -BaudRate $BaudRate -Bytes $total -StartedAt $startedAt -EndedAt $endedAt -Summary $cumulative `
        -PartialBytes $partialBytes -ColdStartAt $coldStartSentAt -ColdStartOffset $coldStartOffset `
        -Enabled $wanted -StoppedEarly $stoppedEarly

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
