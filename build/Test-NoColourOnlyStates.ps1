<#
.SYNOPSIS
    CI gate for A11Y-12 / §9.4.3: no VisualStateGroup distinguishes its states by colour alone.

.DESCRIPTION
    §9.4.3 requires severity to be a triple of colour, shape and text, and A11Y-12 widens that to
    everything: nothing may rely on hue to carry meaning. The failure mode is not a missing token
    or a bad contrast ratio - it is a visual state that changes a brush and nothing else.

    #32 is the case that motivated this. §10.3's footer staleness had three states whose only
    difference was FooterText.Foreground. The age in words was in the text either way; the
    JUDGEMENT the colour was making about that age - fresh, getting old, too old - was carried by
    hue and nothing else.

    Two things make that worse than it sounds. §9.4.3 notes caution and critical converge under
    protanopia and deuteranopia. And under high contrast it is not convergence but identity:
    WzCautionBrush and WzCriticalBrush are both SystemColorWindowTextColor, so two of the three
    states rendered the same pixels.

    So the rule: within a VisualStateGroup, the properties its states set must include at least one
    that is not a brush. A shape, a glyph, a visibility, a thickness - anything a reader can see
    without distinguishing hue.

    This is a structural check and cannot know whether a second channel is a GOOD one. A group that
    sets Opacity alongside a colour passes here and may still be weak. What it makes impossible is
    the specific thing review kept missing: a state that is only a colour.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Properties that carry colour and nothing else. A group whose states set only these is
# distinguishing them by hue.
$colourProperties = @('Foreground', 'Fill', 'Stroke', 'Background', 'BorderBrush')

# Groups allowed to be colour-only, and why. A row is a claim that the group conveys no
# INFORMATION - that it is feedback about the pointer or focus, which a reader is not asked to
# read a state out of. Adding one is a decision, and the reason is asserted non-empty below.
$exempt = [ordered]@{
    'CommonStates'    = 'Pointer feedback - hover and pressed say where the mouse is, not what the app knows.'
    'FocusStates'     = 'Focus feedback. A11Y-2 governs the focus visual and measures it separately.'
    'PointerStates'   = 'Pointer feedback, as CommonStates.'
}

$xamlNs = 'http://schemas.microsoft.com/winfx/2006/xaml'
$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) { Write-Error "No src/ directory under '$Root'." }

foreach ($e in $exempt.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($e.Value)) {
        Write-Error "The exemption '$($e.Key)' has no reason. A row without one is an exemption, not a decision."
    }
}

$targets = @(Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.DirectoryName -notlike '*\bin\*' -and $_.DirectoryName -notlike '*\obj\*' })

$failures = @()
$checked = 0
$allowed = 0

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)

    try { [xml] $doc = Get-Content -LiteralPath $file.FullName -Raw }
    catch { Write-Error "$relative is not well-formed XML: $($_.Exception.Message)" }

    $ns = New-Object System.Xml.XmlNamespaceManager $doc.NameTable
    $ns.AddNamespace('x', $xamlNs)

    foreach ($group in @($doc.SelectNodes('//*[local-name()="VisualStateGroup"]'))) {
        $groupName = $group.GetAttribute('Name', $xamlNs)
        if ([string]::IsNullOrWhiteSpace($groupName)) { $groupName = '(unnamed)' }

        $states = @($group.SelectNodes('.//*[local-name()="VisualState"]'))
        if ($states.Count -lt 2) { continue }

        $properties = @()
        foreach ($setter in @($group.SelectNodes('.//*[local-name()="Setter"]'))) {
            $target = $setter.GetAttribute('Target')
            if ([string]::IsNullOrWhiteSpace($target)) { continue }
            # "FooterText.Foreground" -> "Foreground"
            $properties += ($target -split '\.')[-1]
        }

        # A group that sets nothing at all is not conveying anything by colour either.
        if ($properties.Count -eq 0) { continue }

        $checked++

        $nonColour = @($properties | Where-Object { $colourProperties -notcontains $_ })
        if ($nonColour.Count -gt 0) { continue }

        if ($exempt.Contains($groupName)) { $allowed++; continue }

        $failures += "  $relative : VisualStateGroup '$groupName' sets only $(($properties | Sort-Object -Unique) -join ', ') across $($states.Count) states."
    }
}

if ($checked -eq 0) {
    Write-Error 'No VisualStateGroup with setters was found under src/. This gate cannot pass by finding nothing to check.'
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: a visual state group distinguishes its states by colour alone (A11Y-12, §9.4.3).' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Give the states a second channel a reader can see without distinguishing hue - a shape,' -ForegroundColor Yellow
    Write-Host 'a glyph, a visibility. If the group is pointer or focus feedback rather than information,' -ForegroundColor Yellow
    Write-Host 'add it to the exemption list in this script with the reason.' -ForegroundColor Yellow
    exit 1
}

Write-Host "PASS: every visual state group carries a non-colour channel ($checked groups checked, $allowed exempt)."
