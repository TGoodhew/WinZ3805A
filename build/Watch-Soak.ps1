<#
.SYNOPSIS
    Measures a long soak of the running application: whether memory grows, and if it does, on which
    heap and in which type.

.DESCRIPTION
    THE DEFECT THIS EXISTS TO CATCH TAKES HOURS TO APPEAR AND MINUTES TO MISREAD. #385 held 2.2 GB
    and pegged a core inside two hours, which anybody would notice. #399 grew 19 MB an hour with CPU
    perfectly flat, no errors logged and the window still responsive - invisible in an afternoon,
    3.2 GB across the week G1 expects the app to survive on a second monitor. Nothing in CI can see
    either: both need a receiver, a night, and a person to look.

    THREE INSTRUMENTS, NARROWING. Each answers a question the one before it cannot.

    1. The process sampler - working set, private bytes, handles, GDI and user objects, threads,
       CPU - answers IS ANYTHING GROWING AT ALL. It is the only instrument here that sees a native
       or GDI leak, the only one cheap enough to run for days, and the one that makes runs
       comparable across issues, because #385 was measured this way.

    2. dotnet-counters answers WHICH HEAP. #399 was 147.6 MB of large object heap against 4.4 MB of
       gen2; that one line would have aimed the whole investigation on the first morning instead of
       the second. Ten minutes of counters is worth nine hours of guessing at a working-set curve.

    3. dotnet-gcdump answers WHICH TYPE, and is what actually cracked #399: 69.5 MB of
       ManagedObjectWrapperHolder[] - the CsWinRT interop table - against 101,861 live objects in
       the whole heap. That ratio is the finding. A table with 8.4 million slots and a heap with a
       hundred thousand objects is not a retention bug, it is a rate bug, and no amount of
       working-set sampling gets you there.

    SIX TRAPS, ALL OF THEM MET IN ANGER.

    - THE VERDICT IS PRIVATE BYTES, NOT WORKING SET, AND THIS ONE COST A THREE-HOUR RUN. Working
      set is what the operating system currently keeps resident, so ANYTHING can move it without
      the application allocating or freeing a byte. On 4 Sep 2026 one run saw it fall 38 MB in a
      single sample when another session started bench work on the same machine, and rise 120 MB
      when a dump faulted every page back in - private bytes moved by less than one MB through
      both. Read as working set that run said -16.35 MB/hour; read as private bytes it said -0.07.
      Minimising the window does the same thing, which is why the procedure says not to.
      This script now leads with private and warns when the two disagree.

    - ATTACHING COSTS THE TARGET MEMORY. A diagnostic session allocates its buffers inside the
      process being measured - dotnet-counters says so itself, about --maxTimeSeries - and one
      gcdump taken mid-soak on 4 Sep 2026 moved the working set 8 MB in the sample that followed
      it. An instrument that changes the reading by more than the trend it is looking for has to be
      used at the ends and not throughout, which is the whole reason this script attaches twice
      rather than continuously.

    - dotnet-counters ps DOES NOT ENUMERATE A PACKAGED APP. The first #399 session concluded from
      that the diagnostics IPC was out of reach and spent the night on working set alone. It is not
      out of reach: \\.\pipe\dotnet-diagnostic-<pid> exists and -p <pid> attaches. This script
      checks for the pipe and says so rather than inferring anything from an empty process list.

    - A gcdump FORCES A BLOCKING GEN2 COLLECTION. It perturbs the very thing being measured, so a
      dump belongs at each END of a soak and never on a repeating sample: dumping every few minutes
      keeps collecting the heap you are trying to watch grow. That is why -SkipDumps exists, for
      when another measurement is already in flight.

    - IN THE COUNTERS CSV THE VALUE IS THE LAST COLUMN, NOT THE FOURTH. The fourth is the counter
      type. Reading the fourth produces a confident table of the words "Metric" and "Rate".

    - GROWTH MEASURED FROM THE FIRST SAMPLE IS NOT GROWTH. Launch, JIT, and any navigation done to
      exercise the app all land in the first minutes and dwarf the trend. The headline number here
      is measured after -SettleMinutes, and the settling samples are kept and printed rather than
      discarded, because how long a build takes to settle is itself worth seeing.

    WHAT THIS SCRIPT DOES NOT DO. It does not decide whether a build passes. A soak is compared
    against another soak - same resting page, same windows open, same receiver state - and that
    comparison is a person's job. Section 14 of docs/manual-qa.md is the procedure.

.PARAMETER ProcessId
    The process to watch. Defaults to the single running WinZ3805A.

.PARAMETER OutputDirectory
    Where the samples, counters and dumps are written. Created if missing.

.PARAMETER Label
    A word for this run, used in the file names, so a before and an after do not overwrite.

.PARAMETER DurationMinutes
    How long to sample. The default hour is enough to separate a #399-sized leak from noise; #385
    would have been unmistakable in ten minutes.

.PARAMETER IntervalSeconds
    Seconds between process samples.

.PARAMETER SettleMinutes
    Minutes at the start excluded from the headline growth figure.

.PARAMETER SkipDumps
    Take no gcdumps. Use when another measurement of the same process is already running, since a
    dump forces a collection.

.PARAMETER SelfTest
    Check the arithmetic against synthetic samples and exit. Needs no application, no receiver and
    no serial port - which is the half of this script that CAN be checked on every push.

.EXAMPLE
    pwsh build/Watch-Soak.ps1 -Label before -DurationMinutes 60

.EXAMPLE
    pwsh build/Watch-Soak.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run')]
    [int] $ProcessId,

    [Parameter(ParameterSetName = 'Run')]
    [string] $OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'WinZ3805A-soak'),

    [Parameter(ParameterSetName = 'Run')]
    [string] $Label = 'soak',

    [Parameter(ParameterSetName = 'Run')]
    [int] $DurationMinutes = 60,

    [Parameter(ParameterSetName = 'Run')]
    [int] $IntervalSeconds = 60,

    [Parameter(ParameterSetName = 'Run')]
    [int] $SettleMinutes = 5,

    [Parameter(ParameterSetName = 'Run')]
    [switch] $SkipDumps,

    [Parameter(ParameterSetName = 'SelfTest', Mandatory)]
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------
# The arithmetic, kept free of the process and the disk so -SelfTest can reach all of it.
# ---------------------------------------------------------------------------------------------

<#
.SYNOPSIS
    Reduces process samples to the few numbers a soak is read by.
.DESCRIPTION
    Samples are objects with UptimeMinutes, WorkingSetMB, PrivateMB, Handles, GdiObjects and
    CpuSeconds. Growth is measured from the first sample at or after SettleMinutes, for the reason
    in this file's header: the first minutes are launch, not trend.
#>
function Measure-SoakSeries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Samples,
        [int] $SettleMinutes = 5
    )

    $settled = @($Samples | Where-Object { $_.UptimeMinutes -ge $SettleMinutes })
    if ($settled.Count -lt 2) {
        return [pscustomobject]@{
            Settled             = $false
            Samples             = $Samples.Count
            SpanHours           = 0.0
            WorkingSetMbPerHour = 0.0
            PrivateMbPerHour    = 0.0
            CpuSecondsPerMinute = 0.0
            HandleDelta         = 0
            GdiDelta            = 0
            FirstWorkingSetMB   = 0.0
            LastWorkingSetMB    = 0.0
            Perturbed           = $false
        }
    }

    $first = $settled[0]
    $last = $settled[-1]

    # Parenthesised deliberately: PowerShell's comma binds tighter than binary minus, so an
    # unbracketed subtraction inside an argument list silently becomes an array.
    $spanMinutes = ($last.UptimeMinutes - $first.UptimeMinutes)
    $spanHours = $spanMinutes / 60.0

    $perHour = {
        param($a, $b)
        if ($spanHours -le 0) { return 0.0 }
        return [math]::Round((($b - $a) / $spanHours), 2)
    }

    [pscustomobject]@{
        Settled             = $true
        Samples             = $settled.Count
        SpanHours           = [math]::Round($spanHours, 3)
        WorkingSetMbPerHour = (& $perHour $first.WorkingSetMB $last.WorkingSetMB)
        PrivateMbPerHour    = (& $perHour $first.PrivateMB $last.PrivateMB)
        CpuSecondsPerMinute = $(if ($spanMinutes -gt 0) {
                [math]::Round((($last.CpuSeconds - $first.CpuSeconds) / $spanMinutes), 2)
            }
            else { 0.0 })
        HandleDelta         = ($last.Handles - $first.Handles)
        GdiDelta            = ($last.GdiObjects - $first.GdiObjects)
        FirstWorkingSetMB   = $first.WorkingSetMB
        LastWorkingSetMB    = $last.WorkingSetMB

        # True when the two series tell different stories, which means the difference was not made
        # by this application. Five MB/hour is well above the couple of MB the two normally drift
        # apart by and well below the tens of MB a trim or a dump moves.
        Perturbed           = ([math]::Abs(
            (& $perHour $first.WorkingSetMB $last.WorkingSetMB) -
            (& $perHour $first.PrivateMB $last.PrivateMB)) -gt 5)
    }
}

<#
.SYNOPSIS
    Reads the last recorded value of one counter out of a dotnet-counters CSV.
.DESCRIPTION
    The value is the LAST column. The fourth is the counter type, and reading it yields the word
    "Metric" for every counter in the file.
#>
function Read-CounterValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $Lines,
        [Parameter(Mandatory)] [string] $NameLike
    )

    $value = $null
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split(',')
        if ($parts.Count -lt 5) { continue }

        $name = $parts[2]
        if ($name -notlike $NameLike) { continue }

        $candidate = 0.0
        if ([double]::TryParse($parts[-1], [ref] $candidate)) { $value = $candidate }
    }

    return $value
}

<#
.SYNOPSIS
    Totals the bytes a gcdump report attributes to one type.
.DESCRIPTION
    A report lists a type once per size bucket, so the bytes for a type are the sum of its lines
    rather than the largest of them.
#>
function Read-DumpTypeBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $ReportLines,
        [Parameter(Mandatory)] [string] $TypeName
    )

    [long] $total = 0
    [long] $count = 0
    foreach ($line in $ReportLines) {
        if ($line -notmatch '^\s*([\d,]+)\s+([\d,]+)\s+(\S.*?)\s*$') { continue }

        $type = $Matches[3]
        if ($type -notlike "$TypeName*") { continue }

        $total += [long] ($Matches[1] -replace ',', '')
        $count += [long] ($Matches[2] -replace ',', '')
    }

    [pscustomobject]@{ Bytes = $total; Count = $count }
}

<#
.SYNOPSIS
    Reads the "N GC Heap bytes" and "N GC Heap objects" summary from a gcdump report.
.DESCRIPTION
    The ratio between them is the finding in #399: a hundred thousand objects under a table sized
    for eight million is a rate, not a retention.
#>
function Read-DumpTotals {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [AllowEmptyString()] [string[]] $ReportLines)

    $bytes = 0L
    $objects = 0L
    foreach ($line in $ReportLines) {
        if ($line -match '^\s*([\d,]+)\s+GC Heap bytes') { $bytes = [long] ($Matches[1] -replace ',', '') }
        elseif ($line -match '^\s*([\d,]+)\s+GC Heap objects') { $objects = [long] ($Matches[1] -replace ',', '') }
    }

    [pscustomobject]@{ Bytes = $bytes; Objects = $objects }
}

# ---------------------------------------------------------------------------------------------
# Self-test
# ---------------------------------------------------------------------------------------------

if ($SelfTest) {
    $failures = New-Object System.Collections.Generic.List[string]
    function Assert-Equal($expected, $actual, $what) {
        if ($expected -ne $actual) { $script:failures.Add("$what : expected $expected, got $actual") }
    }

    # A series that gains 10 MB an hour once settled, after a 60 MB launch spike that would read
    # as 600 MB/hour if the settling samples were counted.
    $samples = @()
    foreach ($m in 0..65) {
        $ws = if ($m -lt 5) { 200.0 + (12.0 * $m) } else { 260.0 + (10.0 * (($m - 5) / 60.0)) }
        $samples += [pscustomobject]@{
            UptimeMinutes = [double] $m
            WorkingSetMB  = [math]::Round($ws, 3)
            PrivateMB     = [math]::Round(($ws - 70.0), 3)
            Handles       = 1500 + $m
            GdiObjects    = 81
            CpuSeconds    = 10.0 * $m
        }
    }

    $m = Measure-SoakSeries -Samples $samples -SettleMinutes 5
    Assert-Equal $true $m.Settled 'settled'
    Assert-Equal 10.0 $m.WorkingSetMbPerHour 'working set MB/hour excludes the launch spike'
    Assert-Equal 10.0 $m.CpuSecondsPerMinute 'CPU seconds per minute'
    Assert-Equal 60 $m.HandleDelta 'handle delta over the settled span'
    Assert-Equal 0 $m.GdiDelta 'GDI delta'

    # Too short to say anything.
    $short = Measure-SoakSeries -Samples @($samples[0]) -SettleMinutes 5
    Assert-Equal $false $short.Settled 'a single sample is not a trend'

    # The counters trap: the value is the last column, not the fourth.
    $csv = @(
        'Timestamp,Provider,Counter Name,Counter Type,Mean/Increment',
        '09/04/2026 05:42:18,System.Runtime,dotnet.gc.last_collection.heap.size (By)[gc.heap.generation=loh],Metric,147593880',
        '09/04/2026 05:43:18,System.Runtime,dotnet.gc.last_collection.heap.size (By)[gc.heap.generation=gen2],Metric,4360112'
    )
    Assert-Equal 147593880 (Read-CounterValue -Lines $csv -NameLike '*generation=loh*') 'LOH read from the last column'
    Assert-Equal 4360112 (Read-CounterValue -Lines $csv -NameLike '*generation=gen2*') 'gen2 read from the last column'
    Assert-Equal $null (Read-CounterValue -Lines $csv -NameLike '*nothing-like-this*') 'an absent counter is null'

    # A type spread over several size buckets totals rather than maximises.
    $report = @(
        '    112,123,723  GC Heap bytes',
        '        101,861  GC Heap objects',
        '',
        '   Object Bytes     Count  Type',
        '     67,108,888         1  ManagedObjectWrapperHolder[] (Bytes > 10M)  [System.Private.CoreLib.dll]',
        '      2,097,176        17  ManagedObjectWrapperHolder[] (Bytes > 1M)  [System.Private.CoreLib.dll]',
        '        262,168         4  ManagedObjectWrapperHolder[] (Bytes > 100K)  [System.Private.CoreLib.dll]',
        '        252,328         2  WinZ3805A.Controls.TrendSample[] (Bytes > 100K)  [WinZ3805A.dll]'
    )
    $wrappers = Read-DumpTypeBytes -ReportLines $report -TypeName 'ManagedObjectWrapperHolder'
    Assert-Equal 69468232 $wrappers.Bytes 'wrapper bytes are summed across buckets'
    Assert-Equal 22 $wrappers.Count 'wrapper counts are summed across buckets'

    $trend = Read-DumpTypeBytes -ReportLines $report -TypeName 'WinZ3805A.Controls.TrendSample'
    Assert-Equal 252328 $trend.Bytes 'an unrelated type is not swept up'

    $totals = Read-DumpTotals -ReportLines $report
    Assert-Equal 112123723 $totals.Bytes 'heap bytes'
    Assert-Equal 101861 $totals.Objects 'heap objects'

    # The trap that cost a run: working set moves, private does not, and only one of them is about
    # this application. The verdict must follow private and the disagreement must be announced.
    $trimmed = @()
    foreach ($m in 0..65) {
        # Private is flat throughout. Working set is trimmed 40 MB at the half-way mark.
        $ws = if ($m -lt 33) { 300.0 } else { 260.0 }
        $trimmed += [pscustomobject]@{
            UptimeMinutes = [double] $m
            WorkingSetMB  = $ws
            PrivateMB     = 200.0
            Handles       = 1500
            GdiObjects    = 81
            CpuSeconds    = 10.0 * $m
        }
    }

    $t = Measure-SoakSeries -Samples $trimmed -SettleMinutes 5
    Assert-Equal 0.0 $t.PrivateMbPerHour 'a trimmed working set does not move the private verdict'
    Assert-Equal $true $t.Perturbed 'the two series disagreeing is reported as perturbation'
    if ($t.WorkingSetMbPerHour -ge 0) {
        $script:failures.Add('the synthetic trim should have driven the working-set figure negative')
    }

    # And a genuine leak, where both rise together, must NOT be flagged as perturbation.
    $leaking = @()
    foreach ($m in 0..65) {
        $leaking += [pscustomobject]@{
            UptimeMinutes = [double] $m
            WorkingSetMB  = 300.0 + (0.2 * $m)
            PrivateMB     = 200.0 + (0.2 * $m)
            Handles       = 1500
            GdiObjects    = 81
            CpuSeconds    = 10.0 * $m
        }
    }

    $g = Measure-SoakSeries -Samples $leaking -SettleMinutes 5
    Assert-Equal 12.0 $g.PrivateMbPerHour 'a real leak shows in private bytes'
    Assert-Equal $false $g.Perturbed 'both series rising together is a leak, not perturbation'

    if ($failures.Count -gt 0) {
        Write-Host "Watch-Soak self-test FAILED" -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }

    Write-Host "Watch-Soak self-test passed." -ForegroundColor Green
    Write-Host "  The arithmetic is checked. Everything that needs a receiver is not, and cannot be here."
    exit 0
}

# ---------------------------------------------------------------------------------------------
# The run
# ---------------------------------------------------------------------------------------------

if (-not $ProcessId) {
    $found = @(Get-Process -Name 'WinZ3805A' -ErrorAction SilentlyContinue)
    if ($found.Count -eq 0) { throw 'WinZ3805A is not running. Start it, or pass -ProcessId.' }
    if ($found.Count -gt 1) { throw "Several WinZ3805A processes are running ($($found.Id -join ', ')). Pass -ProcessId." }
    $ProcessId = $found[0].Id
}

$target = Get-Process -Id $ProcessId
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$samplePath = Join-Path $OutputDirectory "$Label-samples.csv"
$counterPath = Join-Path $OutputDirectory "$Label-counters.csv"
$firstDump = Join-Path $OutputDirectory "$Label-start.gcdump"
$lastDump = Join-Path $OutputDirectory "$Label-end.gcdump"

Write-Host "Soaking pid $ProcessId for $DurationMinutes minutes, into $OutputDirectory" -ForegroundColor Cyan

# The pipe, not the process list: dotnet-counters ps cannot see a packaged app, and its silence
# is not evidence that the diagnostics IPC is unreachable.
$pipe = "dotnet-diagnostic-$ProcessId"
$hasPipe = @([System.IO.Directory]::GetFiles('\\.\pipe\')) -match [regex]::Escape($pipe)
if ($hasPipe) {
    Write-Host "  diagnostics pipe present - counters and dumps available" -ForegroundColor Green
}
else {
    Write-Warning "No $pipe. Managed detail is unavailable; the process sampler will still run."
}

$tool = { param($name) $null -ne (Get-Command $name -ErrorAction SilentlyContinue) }

if ($hasPipe -and -not $SkipDumps -and (& $tool 'dotnet-gcdump')) {
    Write-Host "  opening gcdump (forces a gen2 collection)" -ForegroundColor DarkGray
    & dotnet-gcdump collect -p $ProcessId -o $firstDump 2>&1 | Out-Null
}

$counters = $null
if ($hasPipe -and (& $tool 'dotnet-counters')) {
    # --duration rather than killing it at the end: the CSV is flushed when the tool stops itself,
    # and a force-kill can take the file with it.
    # Cast: [math]::Floor returns a double, and "d2" formats only integral types.
    $span = '00:{0:d2}:{1:d2}:00' -f [int] [math]::Floor($DurationMinutes / 60), [int] ($DurationMinutes % 60)
    $counters = Start-Process dotnet-counters -PassThru -WindowStyle Hidden -ArgumentList @(
        'collect', '-p', $ProcessId, '--counters', 'System.Runtime',
        '--refresh-interval', [string] [math]::Max(5, $IntervalSeconds),
        '--duration', $span,
        '--format', 'csv', '-o', $counterPath)
}

$gui = Add-Type -PassThru -Name 'SoakGui' -Namespace 'WinZ3805A' -MemberDefinition @'
[DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
'@

'Timestamp,UptimeMinutes,WorkingSetMB,PrivateMB,Handles,Threads,GdiObjects,UserObjects,CpuSeconds' |
    Out-File -FilePath $samplePath -Encoding utf8

$samples = New-Object System.Collections.Generic.List[object]
$started = Get-Date
$deadline = $started.AddMinutes($DurationMinutes)

while ((Get-Date) -lt $deadline) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) {
        Write-Warning "pid $ProcessId is gone after $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) minutes."
        break
    }

    $sample = [pscustomobject]@{
        UptimeMinutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 2)
        WorkingSetMB  = [math]::Round(($p.WorkingSet64 / 1MB), 2)
        PrivateMB     = [math]::Round(($p.PrivateMemorySize64 / 1MB), 2)
        Handles       = $p.HandleCount
        Threads       = $p.Threads.Count
        GdiObjects    = [int] $gui::GetGuiResources($p.Handle, 0)
        UserObjects   = [int] $gui::GetGuiResources($p.Handle, 1)
        CpuSeconds    = [math]::Round($p.TotalProcessorTime.TotalSeconds, 1)
    }
    $samples.Add($sample)

    '{0:yyyy-MM-ddTHH:mm:ss},{1},{2},{3},{4},{5},{6},{7},{8}' -f (Get-Date), $sample.UptimeMinutes,
        $sample.WorkingSetMB, $sample.PrivateMB, $sample.Handles, $sample.Threads,
        $sample.GdiObjects, $sample.UserObjects, $sample.CpuSeconds | Add-Content -Path $samplePath

    Start-Sleep -Seconds $IntervalSeconds
}

if ($counters) {
    # It should have stopped itself on --duration; give it a moment to write the file, and only
    # then insist.
    $null = $counters.WaitForExit(20000)
    if (-not $counters.HasExited) {
        Write-Warning 'dotnet-counters did not stop on its own; the counters CSV may be short.'
        Stop-Process -Id $counters.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
}

$alive = $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
if ($alive -and $hasPipe -and -not $SkipDumps -and (& $tool 'dotnet-gcdump')) {
    Write-Host "  closing gcdump" -ForegroundColor DarkGray
    & dotnet-gcdump collect -p $ProcessId -o $lastDump 2>&1 | Out-Null
}

# ---------------------------------------------------------------------------------------------
# What it saw
# ---------------------------------------------------------------------------------------------

$summary = Measure-SoakSeries -Samples $samples.ToArray() -SettleMinutes $SettleMinutes

Write-Host ''
Write-Host "Soak: $Label" -ForegroundColor Cyan
if (-not $summary.Settled) {
    Write-Warning "Too few samples after $SettleMinutes minutes to say anything."
}
else {
    Write-Host ("  span                 {0} h over {1} settled samples" -f $summary.SpanHours, $summary.Samples)
    Write-Host ("  PRIVATE (the verdict){0,8} MB/hour" -f $summary.PrivateMbPerHour) -ForegroundColor Cyan
    Write-Host ("  working set          {0} -> {1} MB   ({2} MB/hour)" -f
        $summary.FirstWorkingSetMB, $summary.LastWorkingSetMB, $summary.WorkingSetMbPerHour)
    Write-Host ("  CPU                  {0} s/min" -f $summary.CpuSecondsPerMinute)
    Write-Host ("  handles / GDI        {0:+#;-#;0} / {1:+#;-#;0}" -f $summary.HandleDelta, $summary.GdiDelta)

    # The two series disagreeing is the signature of something OUTSIDE the application moving the
    # working set, and it is loud when it happens - tens of megabytes in a single sample.
    if ($summary.Perturbed) {
        Write-Host ''
        Write-Warning ("Working set and private bytes disagree by {0} MB/hour. Something outside " -f
            [math]::Round([math]::Abs($summary.WorkingSetMbPerHour - $summary.PrivateMbPerHour), 1))
        Write-Host '  the application moved the working set: another process taking memory, this' -ForegroundColor Yellow
        Write-Host '  window being minimised, or your own dump faulting every page into residency.' -ForegroundColor Yellow
        Write-Host '  Read the private figure. The working-set one is not about this application.' -ForegroundColor Yellow
    }
}

if (Test-Path $counterPath) {
    $counterLines = @(Get-Content $counterPath)
    $heaps = [ordered]@{}
    foreach ($gen in 'gen0', 'gen1', 'gen2', 'loh', 'poh') {
        $heaps[$gen] = Read-CounterValue -Lines $counterLines -NameLike "*heap.size*generation=$gen*"
    }

    Write-Host '  managed heap, last collection:'
    foreach ($gen in $heaps.Keys) {
        if ($null -ne $heaps[$gen]) {
            Write-Host ("    {0,-5} {1,10:N1} MB" -f $gen, ($heaps[$gen] / 1MB))
        }
    }
    Write-Host "  ($counterPath)"
}

if ((Test-Path $firstDump) -and (Test-Path $lastDump) -and (& $tool 'dotnet-gcdump')) {
    $a = @(& dotnet-gcdump report $firstDump 2>&1 | ForEach-Object { [string] $_ })
    $b = @(& dotnet-gcdump report $lastDump 2>&1 | ForEach-Object { [string] $_ })

    $ta = Read-DumpTotals -ReportLines $a
    $tb = Read-DumpTotals -ReportLines $b
    Write-Host '  live heap, start -> end:'
    Write-Host ("    bytes    {0,12:N0} -> {1,12:N0}" -f $ta.Bytes, $tb.Bytes)
    Write-Host ("    objects  {0,12:N0} -> {1,12:N0}" -f $ta.Objects, $tb.Objects)

    # Named because it is the one #399 turned on, and because a table that grows while the object
    # count does not is the signature worth recognising again.
    $wa = Read-DumpTypeBytes -ReportLines $a -TypeName 'ManagedObjectWrapperHolder'
    $wb = Read-DumpTypeBytes -ReportLines $b -TypeName 'ManagedObjectWrapperHolder'
    Write-Host ("    ManagedObjectWrapperHolder[]  {0,12:N0} -> {1,12:N0} bytes" -f $wa.Bytes, $wb.Bytes)
}

Write-Host ''
Write-Host 'A soak is read against another soak, not against a threshold. Section 14 of' -ForegroundColor DarkGray
Write-Host 'docs/manual-qa.md says what to hold equal between the two.' -ForegroundColor DarkGray
