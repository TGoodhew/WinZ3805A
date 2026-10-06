# Judging the QA pass's photographs (#633), and the report that says whether the pass passed.
#
# Seven scenarios end with photographs "for the agent to judge", and until 5 Oct 2026 a pass reported
# PASS whether anyone had looked or not - v1.3.5 was tagged from a pass whose photographs nobody
# judged. So every photograph a scenario keeps is now compared with the same photograph from the last
# accepted pass (the baseline), and the pass is not PASS until each one that differs has a recorded
# verdict.
#
# Why the threshold cannot do the judging: two passes of the same build differ in 2-5 % of their
# pixels wherever a reading or the clock is on screen, while a real regression - #697's separator
# changing colour - is a fraction of a percent. A pixel count cannot tell them apart; a person or an
# agent looking at where the difference is can. Under 0.5 % is treated as unchanged (162 of 244
# photographs in two passes of v1.3.5), and everything else is put in front of the judge as baseline,
# now, and the difference marked in red, full size, because small shapes judged from scaled images
# misled once already.
#
# Files in a run folder:
#   results.json    the scenarios' checks (written by Invoke-QaPass.ps1)
#   run.json        what was run: candidate, machines, when
#   judging.json    each photograph's comparison with the baseline
#   verdicts.json   the judge's verdict on each photograph that needed one
#   judging\        the triptychs to judge from
#   report.md       the report, regenerated whenever a verdict is recorded

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class QaJudge {
    static int[] Pixels(Bitmap b) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var a = new int[b.Width * b.Height];
        for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, a, y * b.Width, b.Width);
        b.UnlockBits(d); return a;
    }
    static bool Differs(int u, int v) {
        return Math.Abs(((u >> 16) & 255) - ((v >> 16) & 255)) + Math.Abs(((u >> 8) & 255) - ((v >> 8) & 255)) + Math.Abs((u & 255) - (v & 255)) > 48;
    }
    // The share of pixels that differ, as a percentage; -1 when the sizes differ. With triptych set (PowerShell passes $null as ""),
    // writes baseline | now | now with the differing pixels in red, side by side.
    public static double Compare(string baseline, string now, string triptych) {
        using (var a = new Bitmap(baseline)) using (var b = new Bitmap(now)) {
            bool same = a.Width == b.Width && a.Height == b.Height;
            double share = -1; int[] pa = null, pb = null;
            if (same) {
                pa = Pixels(a); pb = Pixels(b); int n = 0;
                for (int i = 0; i < pa.Length; i++) if (Differs(pa[i], pb[i])) n++;
                share = 100.0 * n / pa.Length;
            }
            if (!string.IsNullOrEmpty(triptych)) {
                int w = a.Width + b.Width + (same ? b.Width : 0) + 16, h = Math.Max(a.Height, b.Height);
                using (var t = new Bitmap(w, h)) using (var g = Graphics.FromImage(t)) {
                    g.Clear(Color.Magenta);
                    g.DrawImage(a, 0, 0, a.Width, a.Height);
                    g.DrawImage(b, a.Width + 8, 0, b.Width, b.Height);
                    if (same) {
                        using (var m = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb)) {
                            var d = m.LockBits(new Rectangle(0, 0, m.Width, m.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            var o = new int[pb.Length];
                            for (int i = 0; i < pb.Length; i++) {
                                int v = pb[i]; int grey = (((v >> 16) & 255) + ((v >> 8) & 255) + (v & 255)) / 6 + 128;
                                o[i] = Differs(pa[i], v) ? unchecked((int)0xFFFF0000) : unchecked((int)0xFF000000) | grey << 16 | grey << 8 | grey;
                            }
                            for (int y = 0; y < m.Height; y++) Marshal.Copy(o, y * m.Width, d.Scan0 + y * d.Stride, m.Width);
                            m.UnlockBits(d);
                            g.DrawImage(m, a.Width + b.Width + 16, 0, m.Width, m.Height);
                        }
                    }
                    t.Save(triptych, ImageFormat.Png);
                }
            }
            return share;
        }
    }
}
"@

$script:Unchanged = 0.5

function Get-QaBaselineRoot { Join-Path $env:LOCALAPPDATA 'WinZ3805A QA\baselines' }

# The photographs a run kept, as paths relative to the run folder. pairs\ is guide-pages' guide image
# beside its photograph, made from the photograph; judging\ is this module's own output.
function Get-QaPhotographs {
    param([string]$RunDir)
    Get-ChildItem $RunDir -Recurse -Filter *.png -File |
        Where-Object { $_.FullName -notmatch '\\(pairs|judging|unpacked)\\' } |
        ForEach-Object { $_.FullName.Substring($RunDir.TrimEnd('\').Length + 1) } | Sort-Object
}

# The severity pills a photograph showed, from the <photo>.pills.json written beside it in the guest
# (#728), as 'label = severity' strings; $null when it has no record.
function Read-QaPills {
    param([string]$Photograph)
    $path = "$Photograph.pills.json"
    if (-not (Test-Path $path)) { return $null }
    $raw = Get-Content $path -Raw
    if (-not "$raw".Trim()) { return $null }
    # Never the bare pipeline: an empty array would come back as nothing, which is "no record".
    $pills = @((ConvertFrom-Json $raw) | ForEach-Object { $_ })
    , @($pills | ForEach-Object { "$($_.name) = $($_.status)" })
}

# How two pill records differ, as text for the report; empty when they say the same. Compared as
# sets of label and severity: a pill that moves is the pixels' business, a pill that changes what it
# says or how bad it is is this one's.
function Compare-QaPills {
    param([string[]]$Baseline, [string[]]$Now)
    $gone = @($Baseline | Where-Object { $Now -cnotcontains $_ })
    $came = @($Now | Where-Object { $Baseline -cnotcontains $_ })
    @(@($gone | ForEach-Object { "was $_" }) + @($came | ForEach-Object { "now $_" })) -join '; '
}

# Each photograph against the baseline: unchanged, changed, resized or new. The triptych of each that
# is not unchanged is written to judging\, named after the photograph.
#
# A photograph whose severity pills differ from the baseline's is 'changed' whatever its pixel share
# (#728): #724 turned the FFOM pill from a green circle to a red hexagon and the TFOM pill from a red
# hexagon to a grey ring, and the photograph scored 0.33 %, under the 0.5 % that counts as unchanged.
# A baseline taken before pills were recorded has no record to compare, and that is said, not
# flagged, so the check starts working with the first baseline promoted after it.
function Compare-QaPhotographs {
    param([string]$RunDir, [string]$BaselineRoot = (Get-QaBaselineRoot))
    $judging = Join-Path $RunDir 'judging'
    New-Item -ItemType Directory -Force $judging | Out-Null
    $rows = foreach ($rel in (Get-QaPhotographs $RunDir)) {
        $now = Join-Path $RunDir $rel
        $baseline = Join-Path $BaselineRoot $rel
        $triptych = Join-Path $judging (($rel -replace '[\\/]', '__'))
        if (-not (Test-Path $baseline)) {
            [pscustomobject]@{ image = $rel; status = 'new'; percent = $null; triptych = $null; pills = $null }
            continue
        }
        $share = [QaJudge]::Compare($baseline, $now, $null)
        $pillsNow = Read-QaPills $now
        $pillsThen = Read-QaPills $baseline
        $pills = if ($null -eq $pillsNow) { $null }
            elseif ($null -eq $pillsThen) { 'no pill record in the baseline to compare' }
            else { Compare-QaPills $pillsThen $pillsNow }
        $pillsChanged = $null -ne $pillsNow -and $null -ne $pillsThen -and $pills
        $status = if ($share -lt 0) { 'resized' } elseif ($share -lt $script:Unchanged -and -not $pillsChanged) { 'unchanged' } else { 'changed' }
        if ($status -ne 'unchanged') { [void][QaJudge]::Compare($baseline, $now, $triptych) }
        [pscustomobject]@{ image = $rel; status = $status; percent = $(if ($share -ge 0) { [Math]::Round($share, 2) } else { $null }); triptych = $(if ($status -ne 'unchanged') { $triptych.Substring($RunDir.TrimEnd('\').Length + 1) } else { $null }); pills = $(if ($pills) { $pills } else { $null }) }
    }
    $rows = @($rows)
    ConvertTo-Json -InputObject $rows -Depth 3 | Set-Content (Join-Path $RunDir 'judging.json') -Encoding UTF8
    $rows
}

function Read-QaJson {
    param([string]$Path)
    # Unrolled: Windows PowerShell hands a JSON array back as one object, which @() then counts as one.
    if (Test-Path $Path) { (Get-Content $Path -Raw | ConvertFrom-Json) | ForEach-Object { $_ } }
}

# A verdict on each photograph matching the pattern (a wildcard on its relative path).
function Set-QaVerdict {
    param([string]$RunDir, [string]$Image, [ValidateSet('pass', 'fail')][string]$Verdict, [string]$Note = '')
    $judged = @(Read-QaJson (Join-Path $RunDir 'judging.json'))
    $matched = @($judged | Where-Object { $_.status -ne 'unchanged' -and $_.image -like $Image })
    if (-not $matched.Count) { throw "no photograph needing a verdict matches '$Image'" }
    $path = Join-Path $RunDir 'verdicts.json'
    $verdicts = @{}
    $existing = Read-QaJson $path
    if ($existing) { foreach ($p in $existing.PSObject.Properties) { $verdicts[[string]$p.Name] = $p.Value } }
    foreach ($m in $matched) { $verdicts[[string]$m.image] = [ordered]@{ verdict = $Verdict; note = $Note; at = (Get-Date -Format 'yyyy-MM-dd HH:mm') } }
    $verdicts | ConvertTo-Json -Depth 3 | Set-Content $path -Encoding UTF8
    $matched.Count
}

function Get-QaScenarioVerdict {
    param($Result)
    if ($Result.Error -like 'skipped:*') { 'skipped' }
    elseif ($Result.Error) { 'ERROR' }
    elseif (@($Result.Checks | Where-Object { -not $_.Ok }).Count) { 'FAIL' }
    else { 'PASS' }
}

# report.md from what the run folder holds, and the pass's overall verdict: FAIL when a check failed,
# an error stopped a scenario or a photograph was judged a failure; AWAITING JUDGEMENT when only
# verdicts are missing; PASS otherwise.
function Write-QaReport {
    param([string]$RunDir)
    $results = @(Read-QaJson (Join-Path $RunDir 'results.json'))
    $run = Read-QaJson (Join-Path $RunDir 'run.json')
    $judged = @(Read-QaJson (Join-Path $RunDir 'judging.json'))
    $verdictsObject = Read-QaJson (Join-Path $RunDir 'verdicts.json')
    $verdicts = @{}
    if ($verdictsObject) { foreach ($p in $verdictsObject.PSObject.Properties) { $verdicts[[string]$p.Name] = $p.Value } }

    $needing = @($judged | Where-Object { $_.status -ne 'unchanged' })
    $awaiting = @($needing | Where-Object { -not $verdicts.ContainsKey([string]$_.image) })
    $rejected = @($needing | Where-Object { $verdicts.ContainsKey([string]$_.image) -and $verdicts[[string]$_.image].verdict -eq 'fail' })
    $failed = @($results | Where-Object { (Get-QaScenarioVerdict $_) -in 'FAIL', 'ERROR' })
    $overall = if ($failed.Count -or $rejected.Count) { 'FAIL' } elseif ($awaiting.Count) { 'AWAITING JUDGEMENT' } elseif ($results.Count) { 'PASS' } else { 'NOTHING RAN' }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("## Automated QA: $($run.candidate)")
    $lines.Add('')
    $lines.Add("Run $($run.started) by ``build/qa/Invoke-QaPass.ps1`` on $(@($run.machines) -join ' and '). Evidence: ``$RunDir``.")
    $lines.Add('')
    $lines.Add("**Verdict: $overall** - $($results.Count) scenario run(s), $($failed.Count) failed; $($judged.Count) photograph(s), $($needing.Count) differing from the baseline, $($awaiting.Count) awaiting a verdict, $($rejected.Count) judged a failure.")
    $lines.Add('')
    $lines.Add('| Scenario | Machine | Result | Detail |')
    $lines.Add('|---|---|---|---|')
    foreach ($r in $results) {
        $verdict = Get-QaScenarioVerdict $r
        $detail = if ($r.Error) { $r.Error } else { (($r.Checks | Where-Object { -not $_.Ok } | ForEach-Object { "$($_.Name)$(if ($_.Detail) { " ($($_.Detail))" })" }) -join '; ') }
        $lines.Add("| $($r.Scenario) | $($r.Machine) | **$verdict** | $("$detail" -replace '\|', '/') |")
    }
    $lines.Add('')
    $lines.Add('### Photographs')
    $lines.Add('')
    if (-not $judged.Count) { $lines.Add('None were kept.') }
    else {
        $lines.Add("Each compared with the last accepted pass's (``$(Get-QaBaselineRoot)``). Under $($script:Unchanged) % of pixels differing counts as unchanged; the rest are judged from ``judging\``, baseline | now | differences in red.")
        $lines.Add('')
        $lines.Add('| Photograph | Against the baseline | Verdict |')
        $lines.Add('|---|---|---|')
        foreach ($j in $needing) {
            $v = if ($verdicts.ContainsKey([string]$j.image)) { "$($verdicts[[string]$j.image].verdict.ToUpper())$(if ($verdicts[[string]$j.image].note) { " - $($verdicts[[string]$j.image].note)" })" } else { '**awaiting**' }
            $against = switch ($j.status) { 'new' { 'no baseline' } 'resized' { 'a different size' } default { "$($j.percent) % differs" } }
            if ($j.pills -and $j.pills -notlike 'no pill record*') { $against = "**pills changed: $($j.pills)**; $against" }
            $lines.Add("| ``$($j.image)`` | $against | $("$v" -replace '\|', '/') |")
        }
        $lines.Add('')
        $lines.Add("$(@($judged | Where-Object { $_.status -eq 'unchanged' }).Count) unchanged.")
    }
    $lines.Add('')
    foreach ($r in $results) {
        $lines.Add("<details><summary>$($r.Scenario) on $($r.Machine): $(Get-QaScenarioVerdict $r)</summary>")
        $lines.Add('')
        foreach ($c in $r.Checks) { $lines.Add("- $(if ($c.Ok) { 'PASS' } else { '**FAIL**' }) $($c.Name)$(if ($c.Detail) { " - $($c.Detail)" })") }
        if ($r.Error) { $lines.Add("- $($r.Error)") }
        $lines.Add('')
        $lines.Add('</details>')
    }
    $lines | Set-Content -Path (Join-Path $RunDir 'report.md') -Encoding UTF8
    [pscustomobject]@{ Verdict = $overall; Failed = $failed.Count; Awaiting = $awaiting.Count; Rejected = $rejected.Count; Report = (Join-Path $RunDir 'report.md') }
}

# A run whose verdict is PASS becomes the baseline its photographs are next compared with.
function Publish-QaBaseline {
    param([string]$RunDir, [string]$BaselineRoot = (Get-QaBaselineRoot))
    $summary = Write-QaReport $RunDir
    if ($summary.Verdict -ne 'PASS') { throw "only a passed run becomes the baseline; this one is $($summary.Verdict)" }
    $count = 0
    foreach ($rel in (Get-QaPhotographs $RunDir)) {
        $target = Join-Path $BaselineRoot $rel
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item (Join-Path $RunDir $rel) $target -Force
        # Its pill record with it (#728), or the next run would have nothing to compare pills with;
        # and an older record removed when this photograph has none, so it cannot go stale.
        if (Test-Path (Join-Path $RunDir "$rel.pills.json")) { Copy-Item (Join-Path $RunDir "$rel.pills.json") "$target.pills.json" -Force }
        else { Remove-Item "$target.pills.json" -Force -ErrorAction SilentlyContinue }
        $count++
    }
    Set-Content (Join-Path $BaselineRoot 'source.txt') "Baseline from $RunDir, published $(Get-Date -Format 'yyyy-MM-dd HH:mm'). $count photograph(s)." -Encoding UTF8
    $count
}

Export-ModuleMember -Function Get-QaBaselineRoot, Get-QaPhotographs, Read-QaPills, Compare-QaPills, Compare-QaPhotographs, Set-QaVerdict, Get-QaScenarioVerdict, Write-QaReport, Publish-QaBaseline
