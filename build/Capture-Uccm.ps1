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
    A UCCM-P has a prompt of its own, `UCCM-P >`, and its echo would be evidence
    rather than noise - but see below: it does not echo.

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

    WHAT A REAL MODULE SAID, 10 Sep 2026 - a Trimble UCCM-P,
    `TRIMBLE,57964-80,40896646,V2.0.1.6-01`, at 57600-8-N-1:

      1. ECHO IS REFUTED. 0 of 9 replies echoed. Replies begin with the answer.
      3. `COMMAND COMPLETE` IS CONFIRMED, 6 of 9, spelled `Command complete`.
      2. TIME CODES ARE BINARY, and this script could not see them. It matched
         the CHARACTERS `C5` against text decoded with `Encoding.ASCII`, which
         renders every byte above 0x7F as `?` - so the count could only ever have
         been 0, whatever the module did. The packets are 44 bytes, `0xC5` to
         `0xCA`, broadcast about every 2 s, and arrive with NO line terminator,
         appended straight onto the prompt. Both properties had to change: the
         search now runs over BYTES, before any text splitting.

    THE SELF-TEST WAS PART OF THE DEFECT. It fed the analysis the STRING
    `'C5 01 02 03 04 05 06'` - a time code rendered as hex text, a shape no
    module produces - and passed. It now drives the byte path as well.

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

.PARAMETER Only
    Restrict the sitting to one catalogued mnemonic. It must still be one the
    catalog names — this narrows what is asked, never what may be sent.

    THE POINT OF IT IS HYPOTHESIS 2. A time code can only land *inside* a reply
    that is still arriving, so the chance of catching one is the reply's wire time
    over the broadcast interval. A scalar answers in a few milliseconds against a
    ~2 s interval and will essentially never catch one however long you sit there;
    `SYST:STAT?` is ~2176 bytes, about 378 ms at 57600-8-N-1, which is roughly 19%
    of the interval. Asking the long one repeatedly is the whole experiment.

.PARAMETER Repeat
    How many times to walk the command list. Default 1.

    With `-Only SYST:STAT? -Repeat 50`, zero interleaved codes puts the chance of
    a module that interleaves freely producing this sitting at (1 - 0.19)^50, or
    about 0.003% - which is a refutation rather than a quiet afternoon. The note
    reports the arithmetic rather than asserting the conclusion.

.PARAMETER SelfTest
    Checks the reply-anatomy analysis against replies whose shape is known,
    including deliberately malformed ones; the catalog read against the real
    `UccmCommands.cs` and against sources broken on purpose; and every branch of the
    hypothesis 2 verdict. Needs no port.

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
    [string] $Only,
    [int] $Repeat = 1,
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
    #
    # THIS MATCHES A TIME CODE WRITTEN AS TEXT, WHICH IS NOT THE FORM THE HARDWARE SENDS. The
    # Trimble UCCM-P broadcasts a BINARY packet - byte 0xC5, not the characters `C`,`5` - and a
    # byte above 0x7F becomes `?` under Encoding.ASCII, so it can never reach this test. The
    # binary form is found in the byte stream by `Get-UccmBinaryTimeCode` before any text
    # splitting happens. This branch is kept because Heather renders codes as hex text in its
    # own logs, and a transcript pasted from there should still classify.
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

    # A trailing prompt, e.g. `UCCM-P >`. THE README PREDICTED THIS EXACTLY: "A UCCM is believed to
    # have no prompt. If the module turns out to emit one, it will show up as an unexplained extra
    # payload line on every reply." It did, on 10 Sep 2026, on every one of nine replies. Naming it
    # is therefore evidence rather than assumption - and it must be named, because a prompt counted
    # as a payload line silently inflates every value count the sitting reports.
    if ($text -match '^[A-Za-z][A-Za-z0-9\-]*\s*>$') { return 'Prompt' }

    if (-not [string]::IsNullOrWhiteSpace($Sent)) {
        # Canonical form: letters, digits and '?' only, upper-cased. The module may echo with
        # different spacing or case from what was sent, and neither difference makes it not an echo.
        $canon = { param($s) (($s.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) -or $_ -eq '?' }) -join '').ToUpperInvariant() }
        if ((& $canon $text) -eq (& $canon $Sent) -and (& $canon $Sent).Length -gt 0) { return 'Echo' }
    }

    return 'Payload'
}

# The observed packet length, in bytes, of a UCCM-P `C5` time code: 0xC5, 42 payload bytes, 0xCA.
# MEASURED, NOT DOCUMENTED - every packet in the 10 Sep 2026 sitting was exactly this long. It is
# a constant here rather than a literal so that a module which disagrees is one edit to explore.
$script:UccmTimeCodeLength = 44

<#
.SYNOPSIS
    Finds binary `C5` time-code packets in a raw reply.
.DESCRIPTION
    HYPOTHESIS 2 IS ABOUT BINARY, AND EVERY TEXT-BASED TEST IS BLIND TO IT. The module broadcasts
    byte 0xC5, and `Encoding.ASCII.GetString` maps anything above 0x7F to `?`, so a time code is
    already destroyed by the time a line-splitting test could see it. It has to be found here, in
    the bytes, before anything is turned into text.

    FRAMING IS BY LENGTH, NOT BY TERMINATOR, AND THAT IS DELIBERATE. 0xCA also occurs INSIDE the
    packet - the 10 Sep 2026 capture has one at offset 22 of a 44-byte code - so scanning forward
    to the first 0xCA truncates the packet and leaves the tail to be misread as text. So a
    candidate is 0xC5 plus `-PacketLength` bytes whose last byte is 0xCA.

    A 0xC5 THAT DOES NOT FIT THAT SHAPE IS REPORTED RATHER THAN SWALLOWED, as `WellFormed = $false`.
    The length is an observation from one sitting, and a harness that silently ignored everything
    not matching its own guess would confirm that guess by construction - the failure this whole
    script exists to avoid.
#>
function Get-UccmBinaryTimeCode {
    [CmdletBinding()]
    param([byte[]] $Bytes, [int] $PacketLength = $script:UccmTimeCodeLength)

    # RETURNED AS A PLAIN ARRAY, NOT `, $found`. The comma form stops the array unrolling, and a
    # caller writing @(Get-UccmBinaryTimeCode ...) then gets a one-element array holding the array.
    # `.Length` on that reads the OUTER count - 1 - so every packet was cut to a single byte while
    # the packet COUNT stayed right, which is why the totals looked correct and the split did not.
    $found = @()
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return $found }

    $i = 0
    while ($i -lt $Bytes.Length) {
        if ($Bytes[$i] -eq 0xC5) {
            $end = $i + $PacketLength - 1
            if ($end -lt $Bytes.Length -and $Bytes[$end] -eq 0xCA) {
                $found += [pscustomobject]@{ Offset = $i; Length = $PacketLength; WellFormed = $true }
                $i = $end + 1
                continue
            }
            $found += [pscustomobject]@{ Offset = $i; Length = 0; WellFormed = $false }
        }
        $i++
    }
    return $found
}

<#
.SYNOPSIS
    Splits a raw reply into an ordered run of text and binary time-code segments.
.DESCRIPTION
    ORDER IS THE WHOLE POINT. Hypothesis 2 is not "a time code arrived" but "a time code arrived
    IN THE MIDDLE", and only position answers that. Carving the well-formed packets out and
    keeping the text between them preserves the sequence, so the existing mid-reply test works on
    binary codes exactly as it does on textual ones.

    Note the packets arrive with NO line terminator of their own - the 10 Sep 2026 capture has one
    appended directly to the `UCCM-P >` prompt. Splitting the reply into lines first therefore
    cannot ever recover it as its own item, which is the second, independent reason the text path
    could not see these.
#>
function Split-UccmStream {
    [CmdletBinding()]
    param([byte[]] $Bytes)

    $segments = @()
    if ($null -eq $Bytes -or $Bytes.Length -eq 0) { return $segments }

    $codes = @(Get-UccmBinaryTimeCode -Bytes $Bytes) | Where-Object { $_.WellFormed }
    $codes = @($codes)
    $cursor = 0

    foreach ($code in $codes) {
        if ($code.Offset -gt $cursor) {
            $slice = $Bytes[$cursor..($code.Offset - 1)]
            $segments += [pscustomobject]@{ Kind = 'Text'; Text = [Text.Encoding]::ASCII.GetString($slice); Bytes = $slice }
        }
        $slice = $Bytes[$code.Offset..($code.Offset + $code.Length - 1)]
        $segments += [pscustomobject]@{ Kind = 'TimeCode'; Text = $null; Bytes = $slice }
        $cursor = $code.Offset + $code.Length
    }

    if ($cursor -lt $Bytes.Length) {
        $slice = $Bytes[$cursor..($Bytes.Length - 1)]
        $segments += [pscustomobject]@{ Kind = 'Text'; Text = [Text.Encoding]::ASCII.GetString($slice); Bytes = $slice }
    }

    return $segments
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
    param([string] $Response, [string] $Sent, [byte[]] $Bytes)

    $lines = @()
    $kinds = @()
    $binaryCodes = 0
    $unframed = 0

    if ($null -ne $Bytes -and $Bytes.Length -gt 0) {
        # THE BYTE PATH IS THE REAL ONE. Anything that reaches this function as a string has
        # already lost every byte above 0x7F, so a binary time code cannot be recovered from it.
        $all = @(Get-UccmBinaryTimeCode -Bytes $Bytes)
        $binaryCodes = @($all | Where-Object { $_.WellFormed }).Count
        $unframed = @($all | Where-Object { -not $_.WellFormed }).Count

        foreach ($segment in (Split-UccmStream -Bytes $Bytes)) {
            if ($segment.Kind -eq 'TimeCode') {
                $head = ($segment.Bytes | Select-Object -First 8 | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
                $lines += "<binary time code, $($segment.Bytes.Length) bytes: $head ...>"
                $kinds += 'TimeCode'
            }
            else {
                foreach ($line in ($segment.Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })) {
                    $lines += $line
                    $kinds += Get-UccmLineKind -Line $line -Sent $Sent
                }
            }
        }
    }
    else {
        if (-not [string]::IsNullOrEmpty($Response)) {
            $lines = @($Response -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
        }
        foreach ($line in $lines) { $kinds += Get-UccmLineKind -Line $line -Sent $Sent }
    }

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
        Prompted            = ($kinds -contains 'Prompt')
        BinaryTimeCodes     = $binaryCodes
        UnframedC5          = $unframed
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

<#
.SYNOPSIS
    Which line terminators a reply actually used, and whether it ended on one.
.DESCRIPTION
    THE FIRST QUESTION ABOUT AN UNKNOWN MODULE, and the hex dump answers it only if somebody reads
    the dump carefully. A smoke run against a SmartClock showed exactly that: the reply's CRLFs and
    its unterminated trailing prompt were both in the bytes and neither was in the summary.

    Heather's source says nothing about UCCM line endings, and our LineProtocol is line-oriented -
    so a module using bare CR, or never terminating its last line, is a transport problem rather
    than a parsing one, and it should be legible on the first sitting rather than the second.
#>
function Get-UccmLineEndings {
    [CmdletBinding()]
    param([byte[]] $Bytes)

    $crlf = 0; $lf = 0; $cr = 0
    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        if ($Bytes[$i] -eq 13) {
            if ($i + 1 -lt $Bytes.Length -and $Bytes[$i + 1] -eq 10) { $crlf++; $i++ } else { $cr++ }
        } elseif ($Bytes[$i] -eq 10) {
            $lf++
        }
    }

    $terminated = $Bytes.Length -gt 0 -and ($Bytes[-1] -eq 10 -or $Bytes[-1] -eq 13)

    $forms = @()
    if ($crlf -gt 0) { $forms += "CRLF x$crlf" }
    if ($lf -gt 0) { $forms += "bare LF x$lf" }
    if ($cr -gt 0) { $forms += "bare CR x$cr" }

    [pscustomobject]@{
        Crlf            = $crlf
        BareLf          = $lf
        BareCr          = $cr
        EndsTerminated  = $terminated
        Summary         = if ($forms.Count -eq 0) { 'no terminators at all' } else { $forms -join ', ' }
    }
}

<#
.SYNOPSIS
    Turns a mid-reply count into a verdict on hypothesis 2 (#481).
.DESCRIPTION
    A COUNT OF ZERO REFUTES NOTHING ON ITS OWN, and every sitting so far has had to say so. What
    turns it into a refutation is knowing the codes were arriving *while replies were in flight*.
    A code can only land inside a reply that is still arriving, so the exposure is the reply wire
    time over the sitting - and a scalar query, answering in milliseconds against a ~2 s broadcast
    interval, can never test this however long anyone sits there.

    THE THIRD OUTCOME IS THE ONE WORTH HAVING. Zero interleaves with the codes UNACCOUNTED FOR is
    not a quiet module: it is evidence the module holds its broadcasts back while it is answering,
    which is a different fact about the firmware and one nobody would go looking for. Collapsing it
    into "refuted" would throw away the more interesting of the two negative results.

    Pure, so the self-test drives every branch without a port.
#>
function Get-UccmHypothesis2Verdict {
    [CmdletBinding()]
    param(
        [int] $Interleaved,
        [int] $Answered,
        [int] $BinaryCodes,
        [double] $Exposure,
        [double] $ExpectedFrames
    )

    $expectedInterleaves = [Math]::Round($BinaryCodes * $Exposure, 2)

    if ($Interleaved -gt 0) {
        return "CONFIRMED for this module: $Interleaved of $Answered replies carried a code mid-reply."
    }

    if ($BinaryCodes -eq 0) {
        return 'INCONCLUSIVE: no time codes were seen at all, so nothing was exposed to the test.'
    }

    # Fewer than one expected by chance means the sitting simply was not big enough - reporting that
    # as a refutation would be the "confirms it by construction" failure pointing the other way.
    if ($expectedInterleaves -lt 1.0) {
        return "INCONCLUSIVE: $BinaryCodes code(s) seen, but only ~$expectedInterleaves would have " +
            'landed mid-reply by chance. Ask a longer reply, or ask more often.'
    }

    if ($ExpectedFrames -gt 0 -and $BinaryCodes -lt ($ExpectedFrames * 0.5)) {
        return "DEFERRAL SUSPECTED: 0 mid-reply, and only $BinaryCodes code(s) where ~$ExpectedFrames " +
            'were due. The module may be holding its broadcasts back rather than never interleaving.'
    }

    return "REFUTED for this module: 0 mid-reply against ~$expectedInterleaves expected, with " +
        "$BinaryCodes code(s) seen where ~$ExpectedFrames were due."
}


<#
.SYNOPSIS
    The UCCM query catalog, read out of `UccmCommands.cs` rather than restated (#482).
.DESCRIPTION
    THIS SCRIPT USED TO HAND-COPY THE LIST, AND THE COPY DRIFTED. Three of the nine entries
    disagreed with the driver - `SYNC:HOLD:DUR?` for the catalog's `:ROSC:HOLD:DUR?`, a different
    subsystem entirely, and two survey queries missing their leading colon - and those three were
    exactly the three that errored on 11 Sep 2026. The sitting then could not say whether the
    firmware refused the query or whether this script had asked for something the driver never
    asks. A capture that walks a list nobody reconciled cannot report on the driver's catalog,
    however carefully its counts are computed.

    The precedent is `build/Test-NoBlockedCommands.ps1`, which reads its tokens out of
    `BlockedCommands.cs` so that file stays the single place those names occur. Same argument:
    `UccmCommands.All` is the only place the catalog exists.

    PURE, taking source text rather than a path, so the self-test can drive it - including with
    deliberately broken sources - without a file on disk.

    A PARSE THAT FINDS NOTHING IS AN ERROR, NOT AN EMPTY LIST. A silent empty catalog would turn
    a capture run into a no-op that reports nine successes out of nothing, which is the failure
    direction nobody would question.
#>
function Get-UccmCatalogMnemonics {
    [CmdletBinding()]
    param([string] $Source)

    if ([string]::IsNullOrWhiteSpace($Source)) {
        throw 'UccmCommands.cs is empty or unreadable. Fix the read rather than falling back to a literal list.'
    }

    # `public const string Status = "SYST:STAT?";`
    $consts = @{}
    foreach ($m in [regex]::Matches($Source, 'const\s+string\s+(?<name>\w+)\s*=\s*"(?<value>[^"]+)"\s*;')) {
        $consts[$m.Groups['name'].Value] = $m.Groups['value'].Value
    }

    # The `All` collection initialiser, and nothing else: the file also names the three UCCM-P
    # queries in `UccmPOnly`, and a looser match would count them twice.
    $all = [regex]::Match($Source, 'All\s*\{\s*get;\s*\}\s*=\s*\[(?<body>.*?)\]\s*;', 'Singleline')
    if (-not $all.Success) {
        throw 'Could not find the UccmCommands.All initialiser. If the catalog moved or changed shape, fix this parse - do not restate the list here.'
    }

    $mnemonics = @()
    foreach ($q in [regex]::Matches($all.Groups['body'].Value, 'Query\(\s*(?<arg>"[^"]+"|\w+)\s*,')) {
        $arg = $q.Groups['arg'].Value
        if ($arg.StartsWith('"')) {
            $mnemonics += $arg.Trim('"')
        } elseif ($consts.ContainsKey($arg)) {
            $mnemonics += $consts[$arg]
        } else {
            throw "UccmCommands.All names '$arg', which is not a string constant in the same file. Fix this parse rather than guessing the mnemonic."
        }
    }

    if ($mnemonics.Count -eq 0) {
        throw 'Parsed the UccmCommands.All initialiser and found no queries in it. Fix the parse rather than removing the check.'
    }

    # Identity first, catalog order after it. `*IDN?` establishes the unit before anything else is
    # believed about it (#416 step one); the catalog lists it last because it is not a UCCM query.
    @($mnemonics | Where-Object { $_ -eq '*IDN?' }) + @($mnemonics | Where-Object { $_ -ne '*IDN?' })
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

    # --- Line endings, reported rather than left in the hex ----------------------------------
    $crlfOnly = Get-UccmLineEndings -Bytes ([byte[]][Text.Encoding]::ASCII.GetBytes("A`r`nB`r`n"))
    if ($crlfOnly.Crlf -ne 2 -or $crlfOnly.BareLf -ne 0 -or $crlfOnly.BareCr -ne 0) {
        $failures += "CRLF pairs were miscounted: $($crlfOnly.Summary)"
    }
    if (-not $crlfOnly.EndsTerminated) { $failures += 'a reply ending in CRLF was called unterminated' }

    # THE CASE THE SMOKE RUN FOUND. A trailing prompt with no terminator is exactly what a
    # SmartClock leaves, and a module that does the same would otherwise look line-oriented.
    $unterminated = Get-UccmLineEndings -Bytes ([byte[]][Text.Encoding]::ASCII.GetBytes("A`r`nscpi > "))
    if ($unterminated.EndsTerminated) { $failures += 'an unterminated trailing line was not reported as such' }

    # A CR that is part of a CRLF must not also be counted as a bare CR - the defect that would
    # make every CRLF module look like it used both conventions.
    $mixed = Get-UccmLineEndings -Bytes ([byte[]][Text.Encoding]::ASCII.GetBytes("A`rB`r`nC`n"))
    if ($mixed.Crlf -ne 1 -or $mixed.BareCr -ne 1 -or $mixed.BareLf -ne 1) {
        $failures += "mixed terminators were miscounted: $($mixed.Summary)"
    }

    $none = Get-UccmLineEndings -Bytes ([byte[]]@())
    if ($none.Summary -ne 'no terminators at all' -or $none.EndsTerminated) {
        $failures += 'empty bytes did not report as having no terminators'
    }

    # --- THE BINARY FORM, WHICH IS THE ONE THE HARDWARE ACTUALLY SENDS ------------------------
    # Every case above states a time code as the TEXT "C5 01 02 ...". A Trimble UCCM-P sends the
    # BYTE 0xC5, and the cases above are blind to it - which is exactly how a real sitting reported
    # 0 of 9 interleaved while a packet sat in the transcript. These drive the byte path.

    # A packet with 0xCA at offset 22 as well as at the end. The inner one is not decoration: the
    # 10 Sep 2026 capture contains it, and scanning to the FIRST 0xCA would cut this packet in half
    # and leave the tail to be read as text.
    $code = New-Object byte[] $script:UccmTimeCodeLength
    $code[0] = 0xC5
    $code[22] = 0xCA
    $code[$script:UccmTimeCodeLength - 1] = 0xCA

    $binInterleaved = [byte[]]([Text.Encoding]::ASCII.GetBytes("SYNC:TINT?`r`n")) + $code +
        [byte[]]([Text.Encoding]::ASCII.GetBytes("-1.234E-008`r`nCOMMAND COMPLETE`r`n"))

    $g = Get-UccmReplyAnatomy -Bytes $binInterleaved -Sent 'SYNC:TINT?'

    # THE PACKET LENGTH MUST SURVIVE THE ROUND TRIP, not just the packet count. The array-wrapping
    # defect above kept the count right and silently cut every packet to one byte, so a test that
    # only counted packets passed while the split was broken.
    $lengths = @(Get-UccmBinaryTimeCode -Bytes $binInterleaved | Where-Object { $_.WellFormed })
    if ($lengths.Count -ne 1 -or $lengths[0].Length -ne $script:UccmTimeCodeLength) {
        $failures += "the packet length did not survive: got $($lengths[0].Length), expected $script:UccmTimeCodeLength"
    }
    if ($g.BinaryTimeCodes -ne 1) { $failures += "a binary time code was not found: got $($g.BinaryTimeCodes)" }
    if ($g.UnframedC5 -ne 0) { $failures += 'a well-formed packet was reported as unframed' }
    if (-not $g.TimeCodeInterleaved) { $failures += 'a BINARY time code mid-reply was not reported as interleaved' }
    if ($g.PayloadLines.Count -ne 1) { $failures += "the binary packet leaked into the payload: $($g.PayloadLines.Count) line(s)" }
    if ($g.PayloadLines.Count -ge 1 -and $g.PayloadLines[0] -ne '-1.234E-008') { $failures += 'the value was lost to a binary time code' }
    if (-not $g.Terminated) { $failures += 'COMMAND COMPLETE after a binary code was missed' }

    # A trailing packet appended straight onto the prompt, with NO terminator between them. This is
    # the exact shape the 10 Sep 2026 GPS:POS:SURV:PROG? reply came back in.
    $binTrailing = [byte[]]([Text.Encoding]::ASCII.GetBytes("Undefined header`r`nUCCM-P >")) + $code
    $h = Get-UccmReplyAnatomy -Bytes $binTrailing -Sent 'GPS:POS:SURV:PROG?'
    if (-not $h.Errored) { $failures += 'an errored reply carrying a binary code lost its error' }
    if (-not $h.Prompted) { $failures += 'the UCCM-P prompt was not recognised' }
    if (-not $h.TimeCodeAnywhere) { $failures += 'a trailing binary time code was not seen at all' }
    if ($h.TimeCodeInterleaved) { $failures += 'a trailing binary time code was misreported as interleaved' }
    # THE REGRESSION THIS WHOLE CHANGE EXISTS FOR: prompt plus appended binary used to arrive as one
    # "payload line" of mojibake, i.e. binary garbage handed out as a value.
    if ($h.PayloadLines.Count -ne 0) { $failures += "prompt+binary produced $($h.PayloadLines.Count) bogus payload line(s)" }

    # WHY THE BYTE PATH HAD TO EXIST. Fed the same reply as a string, the analysis cannot see the
    # code - Encoding.ASCII turns 0xC5 into '?'. Asserted so nobody "simplifies" back to text.
    $blind = Get-UccmReplyAnatomy -Response ([Text.Encoding]::ASCII.GetString($binTrailing)) -Sent 'GPS:POS:SURV:PROG?'
    if ($blind.TimeCodeAnywhere) { $failures += 'the text path claimed to see a binary time code; the test is not proving what it should' }

    # A 0xC5 that is not a packet is REPORTED, not silently dropped - the length is one sitting's
    # observation, and a harness that ignored everything not matching its own guess would confirm
    # that guess by construction.
    $stray = [byte[]]([Text.Encoding]::ASCII.GetBytes("A`r`n")) + [byte[]]@(0xC5, 0x01, 0x02)
    $k = Get-UccmReplyAnatomy -Bytes $stray -Sent 'X?'
    if ($k.UnframedC5 -ne 1) { $failures += "a stray 0xC5 was not reported as unframed: got $($k.UnframedC5)" }
    if ($k.BinaryTimeCodes -ne 0) { $failures += 'a stray 0xC5 was counted as a packet' }

    # Two codes back to back must both be found, and the scan must not stall.
    $twin = [byte[]]$code + [byte[]]$code
    $t = Get-UccmBinaryTimeCode -Bytes $twin
    if (@($t | Where-Object { $_.WellFormed }).Count -ne 2) { $failures += 'two adjacent time codes were not both found' }

    # Prompts, including the SmartClock's, which the smoke run showed counted as payload.
    foreach ($p in @('UCCM-P >', 'UCCM >', 'scpi >')) {
        if ((Get-UccmLineKind -Line $p -Sent 'X?') -ne 'Prompt') { $failures += "'$p' was not recognised as a prompt" }
    }
    # A value must NOT be mistaken for a prompt.
    if ((Get-UccmLineKind -Line '-1.234E-008' -Sent 'X?') -ne 'Payload') { $failures += 'a value was misread as a prompt' }

    # --- Hypothesis 2 is a decision, not a count (#481) --------------------------------------
    #
    # Every branch, because the whole value of this verdict is that it distinguishes three ways of
    # seeing zero. A rule that collapsed them would read as authoritative and say nothing.
    $confirmed = Get-UccmHypothesis2Verdict -Interleaved 3 -Answered 50 -BinaryCodes 40 -Exposure 0.19 -ExpectedFrames 40
    if ($confirmed -notmatch '^CONFIRMED') { $failures += "a mid-reply code did not confirm: $confirmed" }

    # Nothing broadcast at all: the test was never run, whatever the count says.
    $noCodes = Get-UccmHypothesis2Verdict -Interleaved 0 -Answered 50 -BinaryCodes 0 -Exposure 0.19 -ExpectedFrames 40
    if ($noCodes -notmatch '^INCONCLUSIVE') { $failures += "a sitting with no codes at all was not called inconclusive: $noCodes" }

    # Codes arrived, but the sitting was too small for one to be expected inside a reply. Calling
    # this a refutation is the "confirms it by construction" failure pointing the other way.
    $tooSmall = Get-UccmHypothesis2Verdict -Interleaved 0 -Answered 9 -BinaryCodes 3 -Exposure 0.02 -ExpectedFrames 20
    if ($tooSmall -notmatch '^INCONCLUSIVE') { $failures += "an underpowered sitting was not called inconclusive: $tooSmall" }

    # THE INTERESTING NEGATIVE. Plenty of exposure, plenty of replies, and the codes are MISSING -
    # which is not a module that never interleaves, it is one that stops broadcasting while it
    # answers. Reported as itself rather than folded into a refutation.
    $deferred = Get-UccmHypothesis2Verdict -Interleaved 0 -Answered 50 -BinaryCodes 12 -Exposure 0.5 -ExpectedFrames 40
    if ($deferred -notmatch '^DEFERRAL SUSPECTED') { $failures += "missing codes were not reported as deferral: $deferred" }

    # The real refutation: codes all present and accounted for, none of them mid-reply.
    $refuted = Get-UccmHypothesis2Verdict -Interleaved 0 -Answered 50 -BinaryCodes 40 -Exposure 0.19 -ExpectedFrames 40
    if ($refuted -notmatch '^REFUTED') { $failures += "a well-powered null result was not called a refutation: $refuted" }

    # A confirmation outranks everything: even one code mid-reply settles it, however odd the rest
    # of the arithmetic looks. This is the branch that must never be reachable past.
    $oneIsEnough = Get-UccmHypothesis2Verdict -Interleaved 1 -Answered 1 -BinaryCodes 1 -Exposure 0 -ExpectedFrames 0
    if ($oneIsEnough -notmatch '^CONFIRMED') { $failures += "a single mid-reply code did not confirm: $oneIsEnough" }

    # --- The catalog is READ, not restated (#482) -------------------------------------------
    #
    # The drift this replaced was invisible precisely because both lists looked plausible. So the
    # parse is checked against the REAL file - a synthetic source would only prove the regex
    # matches itself - and then against deliberately broken ones, because a parser that silently
    # returns nothing turns a capture into a no-op reporting successes out of an empty list.
    $catalogFile = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\WinZ3805A.Device\Drivers\Uccm\UccmCommands.cs'))
    if (-not (Test-Path $catalogFile)) {
        $failures += "the UCCM catalog is not where this script expects it: $catalogFile"
    } else {
        $real = Get-UccmCatalogMnemonics -Source (Get-Content -LiteralPath $catalogFile -Raw)

        if ($real.Count -lt 2) { $failures += "the catalog parsed to $($real.Count) quer(y/ies), which cannot be right" }
        if ($real[0] -ne '*IDN?') { $failures += "identity is not asked first: got '$($real[0])'" }
        if (@($real | Where-Object { $_ -eq '*IDN?' }).Count -ne 1) { $failures += 'identity appeared more than once' }

        # The three that drifted, spelled as the DRIVER spells them. Named individually rather
        # than counted, because the whole defect was a list that was the right length and wrong.
        foreach ($expected in ':ROSC:HOLD:DUR?', ':GPS:POS:SURV:STAT?', ':GPS:POS:SURV:PROG?') {
            if ($real -notcontains $expected) {
                $failures += "the catalog no longer names '$expected' - if the driver changed, this assertion should change with it"
            }
        }

        # Nothing may reach the wire that the catalog does not name. This is the assertion that
        # makes the drift impossible rather than merely fixed.
        $catalogText = Get-Content -LiteralPath $catalogFile -Raw
        foreach ($m in $real) {
            if ($catalogText -notmatch [regex]::Escape($m)) {
                $failures += "'$m' would be sent but does not appear in UccmCommands.cs"
            }
        }
    }

    # A source naming a constant it never defines must throw, not guess.
    $danglingSource = @'
public static class UccmCommands {
    public const string Status = "SYST:STAT?";
    public static IReadOnlyList<ScpiCommand> All { get; } =
    [
        Query(Status, "Status", "x", ResponseFormat.MultiLine),
        Query(NeverDefined, "Ghost", "x", ResponseFormat.Text),
    ];
}
'@
    try {
        Get-UccmCatalogMnemonics -Source $danglingSource | Out-Null
        $failures += 'a query naming an undefined constant was accepted instead of throwing'
    } catch {
        if ("$_" -notmatch 'NeverDefined') { $failures += "the undefined-constant error did not name the constant: $_" }
    }

    # An initialiser that parses but holds nothing must throw rather than return an empty list.
    $emptySource = @'
public static class UccmCommands {
    public static IReadOnlyList<ScpiCommand> All { get; } =
    [
    ];
}
'@
    try {
        Get-UccmCatalogMnemonics -Source $emptySource | Out-Null
        $failures += 'an empty catalog was accepted instead of throwing'
    } catch {
        if ("$_" -notmatch 'no queries') { $failures += "the empty-catalog error was about something else: $_" }
    }

    # A file with no All initialiser at all - the shape a rename would produce.
    try {
        Get-UccmCatalogMnemonics -Source 'public static class UccmCommands { }' | Out-Null
        $failures += 'a source with no All initialiser was accepted instead of throwing'
    } catch {
        if ("$_" -notmatch 'All initialiser') { $failures += "the missing-initialiser error was about something else: $_" }
    }

    # `UccmPOnly` names three of the same mnemonics. A looser match would count them twice.
    $withUccmPOnly = @'
public static class UccmCommands {
    public const string Status = "SYST:STAT?";
    public const string HoldoverDuration = ":ROSC:HOLD:DUR?";
    public static IReadOnlyList<ScpiCommand> All { get; } =
    [
        Query(Status, "Status", "x", ResponseFormat.MultiLine),
        Query(HoldoverDuration, "Holdover duration", "x", ResponseFormat.Decimal),
    ];
    public static IReadOnlyList<string> UccmPOnly { get; } =
        [HoldoverDuration];
}
'@
    $twice = Get-UccmCatalogMnemonics -Source $withUccmPOnly
    if ($twice.Count -ne 2) { $failures += "UccmPOnly leaked into the catalog: got $($twice.Count) entries, expected 2" }

    if ($failures.Count -gt 0) {
        Write-Host 'FAIL' -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host 'PASS - the reply anatomy, the echo rule, the time-code distinction, the'
    Write-Host '  line-ending report, the catalog read and hypothesis 2 verdict are checked.'
    Write-Host ''
    Write-Host '  The serial half is not checked here and cannot be. It has however been SMOKE-RUN'
    Write-Host '  against the bench Z3805A on 8 Sep 2026 - not a UCCM, but a real port and real'
    Write-Host '  replies - so the baud walk, the identify step, the per-command read, the hex dump'
    Write-Host '  and the note are known to work. It correctly reported 0 of 9 echoes and 0 of 9'
    Write-Host '  COMMAND COMPLETE for a family that does neither, which is the answer that would'
    Write-Host '  have been embarrassing to get wrong on the day.'
    Write-Host ''
    Write-Host ''
    Write-Host '  The UCCM half is no longer unexercised. A Trimble UCCM-P answered on 10 Sep 2026'
    Write-Host '  at 57600-8-N-1, which refuted the echo hypothesis (0 of 9), confirmed COMMAND'
    Write-Host '  COMPLETE (6 of 9), and showed the time codes to be BINARY packets this script'
    Write-Host '  had been searching for as text. The binary path above is what that sitting bought.'
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

# Every catalogued query, identity first - READ FROM THE DRIVER, never restated here (#482).
$catalogRelative = 'src\WinZ3805A.Device\Drivers\Uccm\UccmCommands.cs'
$catalogPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot (Join-Path '..' $catalogRelative)))
if (-not (Test-Path $catalogPath)) {
    Write-Error "Cannot find the UCCM catalog at '$catalogRelative'. If it moved, update this path - do not paste the list back in."
}
$commands = Get-UccmCatalogMnemonics -Source (Get-Content -LiteralPath $catalogPath -Raw)
Write-Host "Asking $($commands.Count) catalogued quer$(if ($commands.Count -eq 1) { 'y' } else { 'ies' }), read from $catalogRelative."

# -Only narrows what is ASKED; it can never widen what may be SENT, because the list it filters is
# the catalog itself (#482). A mnemonic that is not in the catalog is an error rather than a
# passthrough - the whole point of reading the list from the driver is that nothing else reaches
# the wire.
if ($Only) {
    $wanted = $Only.Trim()
    $matched = @($commands | Where-Object { $_ -eq $wanted })
    if ($matched.Count -eq 0) {
        Write-Error "'$wanted' is not in the UCCM catalog. Catalogued: $($commands -join ', ')"
    }
    $commands = $matched
    Write-Host "Restricted to $wanted."
}

if ($Repeat -lt 1) { Write-Error "-Repeat must be at least 1." }
if ($Repeat -gt 1) { Write-Host "Walking the list $Repeat times." }

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

$sittingStarted = Get-Date

for ($pass = 1; $pass -le $Repeat; $pass++) {
    foreach ($command in $commands) {
        if ($Repeat -gt 1) { Write-Host ("  {0,4}/{1} {2,-22} " -f $pass, $Repeat, $command) -NoNewline }
        else { Write-Host ("  {0,-22} " -f $command) -NoNewline }

        $bytes = Invoke-UccmCommand -Serial $serial -Command $command -WaitMs 5000
        # BYTES, NOT TEXT. Passing the ASCII string here is what made every binary time code invisible.
        $anatomy = Get-UccmReplyAnatomy -Bytes $bytes -Sent $command

        $verdict = if ($bytes.Length -eq 0) { 'SILENT' }
        elseif ($anatomy.Errored) { 'error' }
        else { "$($anatomy.PayloadLines.Count) payload line(s)" }
        if ($anatomy.TimeCodeInterleaved) { $verdict += '  <<< TIME CODE MID-REPLY' }
        elseif ($anatomy.BinaryTimeCodes -gt 0) { $verdict += "  (+$($anatomy.BinaryTimeCodes) trailing)" }
        Write-Host $verdict

        $results += [pscustomobject]@{ Command = $command; Anatomy = $anatomy; Bytes = $bytes }

        # ON A REPEAT SITTING THE FULL DUMP IS KEPT ONLY WHERE THERE IS SOMETHING TO SEE. Fifty
        # status replies hex-dumped in full is a third of a megabyte of near-identical screens, and
        # the evidence is not the fiftieth ordinary reply. A TRAILING code is the ordinary case on
        # this family and is counted rather than dumped; what earns a full dump is a code landing
        # MID-reply - hypothesis 2 itself - or an anomaly: a stray 0xC5, or silence. The first pass
        # is kept whole as a specimen, so the file can be read by someone who has not seen one.
        $worthKeeping = $Repeat -eq 1 -or $pass -eq 1 -or $anatomy.TimeCodeInterleaved -or
            $anatomy.UnframedC5 -gt 0 -or $bytes.Length -eq 0

        if (-not $worthKeeping) {
            [void]$transcript.AppendLine(
                ("==== SENT: {0}  (pass {1}) - {2} byte(s), {3} payload line(s), {4} trailing time code(s)" -f $command, $pass, $bytes.Length, $anatomy.PayloadLines.Count, $anatomy.BinaryTimeCodes))
            [void]$transcript.AppendLine('')
            continue
        }

        [void]$transcript.AppendLine("==== SENT: $command$(if ($Repeat -gt 1) { "  (pass $pass)" })")
        [void]$transcript.AppendLine("---- TERMINATORS: $((Get-UccmLineEndings -Bytes $bytes).Summary)")
        if ($anatomy.TimeCodeInterleaved) {
            [void]$transcript.AppendLine('---- TIME CODE MID-REPLY: this is hypothesis 2, caught.')
        }
        [void]$transcript.AppendLine('---- BYTES')
        [void]$transcript.AppendLine((Format-UccmBytes -Bytes $bytes))
        [void]$transcript.AppendLine('---- LINES')
        for ($i = 0; $i -lt $anatomy.Lines.Count; $i++) {
            [void]$transcript.AppendLine(('  [{0}] {1}' -f $anatomy.Kinds[$i], $anatomy.Lines[$i]))
        }
        [void]$transcript.AppendLine('')
    }
}

$sittingSeconds = [Math]::Round(((Get-Date) - $sittingStarted).TotalSeconds, 1)

$serial.Close()

$capturePath = Join-Path $OutputDirectory "$Label.txt"
[System.IO.File]::WriteAllText($capturePath, $transcript.ToString())

# --- The three hypotheses, as measurements ---------------------------------------------------
$answered = @($results | Where-Object { $_.Bytes.Length -gt 0 })
$echoed = @($answered | Where-Object { $_.Anatomy.EchoFirst })
$interleaved = @($answered | Where-Object { $_.Anatomy.TimeCodeInterleaved })
$terminated = @($answered | Where-Object { $_.Anatomy.Terminated })
$prompted = @($answered | Where-Object { $_.Anatomy.Prompted })

# A plain loop rather than Measure-Object with a calculated property, which is PowerShell 7 only -
# these scripts are run with pwsh, but a gate that silently sums nothing under 5.1 is worse than one
# that will not start.
$binaryCodes = 0
$unframedC5 = 0
foreach ($r in $answered) {
    $binaryCodes += $r.Anatomy.BinaryTimeCodes
    $unframedC5 += $r.Anatomy.UnframedC5
}

$identity = ($results | Where-Object { $_.Command -eq '*IDN?' } | Select-Object -First 1)
$identityText = if ($identity -and $identity.Anatomy.PayloadLines.Count -gt 0) { $identity.Anatomy.PayloadLines[0] } else { '(none)' }

# Line endings across the whole sitting, because "what does this module terminate with" is a
# property of the module rather than of any one reply.
$allBytes = [byte[]]@()
foreach ($r in $answered) { $allBytes += $r.Bytes }
$endings = Get-UccmLineEndings -Bytes $allBytes
$unterminatedCount = @($answered | Where-Object { -not (Get-UccmLineEndings -Bytes $_.Bytes).EndsTerminated }).Count

# --- Hypothesis 2, as a decision rather than a count (#481) -----------------------------------
#
# A COUNT OF ZERO ON ITS OWN REFUTES NOTHING, and that has been written on every sitting so far.
# What turns it into a refutation is knowing the codes were arriving while the replies were in
# flight - so this reports the exposure and the expected yield, and names the case where zero means
# something else entirely: a module that stops broadcasting while it is answering.
#
# THE THIRD OUTCOME IS THE ONE WORTH HAVING. Zero interleaves with the frames unaccounted for is not
# a quiet module, it is evidence of deferral, which is a different fact about the firmware and one
# nobody would go looking for.
$replyBytes = 0
foreach ($r in $answered) { $replyBytes += $r.Bytes.Length }

# Wire time is the exposure: a code can only land inside a reply that is still arriving.
$wireSeconds = if ($found -gt 0) { [Math]::Round($replyBytes / ($found / 10.0), 2) } else { 0 }
$expectedFrames = if ($sittingSeconds -gt 0) { [Math]::Round($sittingSeconds / 2.0, 1) } else { 0 }
$exposure = if ($sittingSeconds -gt 0) { $wireSeconds / $sittingSeconds } else { 0 }

# Of the codes that did arrive, how many should have landed inside a reply if the module simply
# broadcasts on its own clock and lets them fall where they may.
$expectedInterleaves = [Math]::Round($binaryCodes * $exposure, 2)

$hypothesis2 = Get-UccmHypothesis2Verdict `
    -Interleaved $interleaved.Count `
    -Answered $answered.Count `
    -BinaryCodes $binaryCodes `
    -Exposure $exposure `
    -ExpectedFrames $expectedFrames


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
| Line terminators | $($endings.Summary) |
| Replies ending unterminated | $unterminatedCount of $($answered.Count) |
| Replies ending in a prompt | $($prompted.Count) of $($answered.Count) |
| Binary ``C5`` packets seen | $binaryCodes |
| ``0xC5`` bytes not matching the packet shape | $unframedC5 |

## The three hypotheses this sitting was taken to settle

Every one of these was read out of Lady Heather's source rather than a vendor document, and the
driver treats them as hypotheses with citations until a receiver says otherwise.

| Hypothesis | Measured |
|---|---|
| The module echoes the command before answering it | **$($echoed.Count) of $($answered.Count)** replies began with an echo |
| Unsolicited ``C5`` time codes interleave with replies | **$($interleaved.Count) of $($answered.Count)** replies had one mid-reply |
| ``COMMAND COMPLETE`` terminates a reply | **$($terminated.Count) of $($answered.Count)** replies carried it |

The time-code row counts **binary** packets - byte ``0xC5`` through ``0xCA`` - found in the raw
stream. Until 10 Sep 2026 this script looked for the *characters* ``C5`` in text decoded as ASCII,
which turns every byte above ``0x7F`` into ``?``, so the row could only ever have read 0.

### Hypothesis 2, with the exposure it was tested against

A count of zero refutes nothing on its own, so here is what the count was measured against. A code
can only land *inside* a reply that is still arriving, so the exposure is reply wire time over the
sitting; a scalar query answers in milliseconds and cannot test this however long anyone sits there.

| | |
|---|---|
| Sitting length | $sittingSeconds s |
| Reply wire time | $wireSeconds s, $("{0:P0}" -f $exposure) of the sitting |
| Codes seen / due at one per 2 s | $binaryCodes / ~$expectedFrames |
| Mid-reply expected by chance | ~$expectedInterleaves |
| **Verdict** | **$hypothesis2** |

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
Write-Host ("Terminators:         {0}{1}" -f $endings.Summary,
    $(if ($unterminatedCount -gt 0) { " ($unterminatedCount reply/replies end unterminated)" } else { '' }))
Write-Host ("Ends in a prompt:    {0} of {1}" -f $prompted.Count, $answered.Count)
Write-Host ("Echoed first:        {0} of {1}" -f $echoed.Count, $answered.Count)
Write-Host ("Time code mid-reply: {0} of {1}" -f $interleaved.Count, $answered.Count)
Write-Host ("Binary C5 packets:   {0}{1}" -f $binaryCodes,
    $(if ($unframedC5 -gt 0) { " ($unframedC5 stray 0xC5 byte(s) not matching the packet shape)" } else { '' }))
Write-Host ("COMMAND COMPLETE:    {0} of {1}" -f $terminated.Count, $answered.Count)
Write-Host ''
Write-Host ('Reply wire time:     {0} s of {1} s ({2:P0} exposure)' -f $wireSeconds, $sittingSeconds, $exposure)
Write-Host ('Codes seen / due:    {0} / ~{1} at one per 2 s' -f $binaryCodes, $expectedFrames)
Write-Host ('Mid-reply expected:  ~{0}' -f $expectedInterleaves)
Write-Host ('Hypothesis 2:        ' + $hypothesis2)

Write-Host ''
Write-Host "Wrote $capturePath"
Write-Host "Provenance in $notePath - FILL IN 'What was happening' WHILE YOU REMEMBER IT."
