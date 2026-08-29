<#
.SYNOPSIS
    CI gate for A11Y-5: a pointer target that is not a button is still 32 px.

.DESCRIPTION
    A11Y-5 requires pointer targets to be at least 32 x 32 px "at all times", and names
    SkyPlotControl's markers (§9.10.2, #117) as the one recorded exception.

    Test-IconOnlyButtons.ps1 already enforces that floor - but only on Button-like
    controls, and only when they are icon-only. Everything else with a tooltip was
    unchecked, and on 28 Aug 2026 two such things were found in the primary window by a
    user trying to use one of them:

        - the §7.4 rollover badge, a bare TextBlock in a symbol font at FontSize 12.
          A TextBlock is hit-testable only where its glyph is, so the target measured
          about 12 x 15 px - under a fifth of the required area. It could be hit only by
          landing on the glyph exactly, and the tooltip dismissed as soon as the pointer
          slipped off it.
        - TfomPill and FfomPill, SeverityPill instances measuring 73 x 28. A 20 px line
          of text inside XXS padding lands four short, and FfomPill carries a tooltip.

    Neither is a Button, so neither reached the existing gate. A11Y-5 had been recorded
    as passing for weeks.

    WHAT COUNTS AS A POINTER TARGET
    Anything carrying a tooltip, from either of the two places one can be attached:

        - ToolTipService.ToolTip in XAML, as an attribute or a property element.
        - ToolTipService.SetToolTip(SomeName, ...) in a Views/ or Controls/ .cs file,
          resolved back to the x:Name in that folder's XAML.

    The second form is not an afterthought - it is how the badge that prompted this gate
    got its tooltip, so a gate reading only XAML would have missed the defect it exists
    to catch.

    Button-like controls are skipped here because Test-IconOnlyButtons.ps1 owns them;
    two gates failing on one element would make each look like the other's false alarm.

    HOW THE FLOOR IS PROVED
    A static check cannot measure layout, so the floor must be DECLARED, exactly as the
    icon gate requires:

        - MinHeight (or Height) of at least 32 on the element, or
        - a Style in src/WinZ3805A/Themes/ for the element's type that sets it.

    Width is required only of a target with no text in it. A pill sized by its label is
    wide by construction, and demanding a MinWidth of something that always carries
    "TFOM 3" would be arithmetic rather than accessibility. A target with no text has
    nothing but padding to give it width, which is precisely how the badge ended up
    12 px wide.

    XAML is parsed as XML rather than grepped, so attributes and property elements are
    both handled and comments are ignored.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-PointerTargets.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) { throw "No src directory under $Root" }

# Owned by Test-IconOnlyButtons.ps1. Listed by local name, since the XAML may or may not
# prefix them.
$buttonLike = @(
    'Button', 'ToggleButton', 'AppBarButton', 'AppBarToggleButton',
    'HyperlinkButton', 'DropDownButton', 'SplitButton', 'RepeatButton'
)

# Genuine exceptions. A row here is a CLAIM, and the reason is asserted non-empty for the
# same purpose it is in Test-HighContrastLegibility.ps1: an exemption nobody had to justify
# is one nobody will revisit.
$allowed = @{
    # None today. SkyPlotControl's 24 px markers are A11Y-5's one recorded exception
    # (§9.10.2, #117), but they are drawn in code rather than declared in XAML, so they
    # never reach this gate at all.
}

$minimum = 32

# ---------------------------------------------------------------------------------------
# Which types declare a floor in a theme dictionary
# ---------------------------------------------------------------------------------------
$styledFloor = @{}
foreach ($theme in Get-ChildItem (Join-Path $src 'WinZ3805A/Themes') -Filter *.xaml -ErrorAction SilentlyContinue) {
    [xml] $doc = Get-Content $theme.FullName -Raw
    foreach ($style in $doc.GetElementsByTagName('Style')) {
        $target = $style.GetAttribute('TargetType')
        if ([string]::IsNullOrWhiteSpace($target)) { continue }

        $local = ($target -split ':')[-1]
        foreach ($setter in $style.GetElementsByTagName('Setter')) {
            $property = $setter.GetAttribute('Property')
            if ($property -notin @('MinHeight', 'Height')) { continue }

            $value = 0.0
            if ([double]::TryParse($setter.GetAttribute('Value'), [ref] $value) -and $value -ge $minimum) {
                $styledFloor[$local] = $true
            }
        }
    }
}

# ---------------------------------------------------------------------------------------
# Names that receive a tooltip from code
# ---------------------------------------------------------------------------------------
$namedInCode = @{}
$codeFiles = Get-ChildItem $src -Recurse -Filter *.cs |
    Where-Object { $_.FullName -match '\\(Views|Controls)\\' -and $_.FullName -notmatch '\\(bin|obj)\\' }

foreach ($file in $codeFiles) {
    $text = Get-Content $file.FullName -Raw
    foreach ($m in [regex]::Matches($text, 'ToolTipService\.SetToolTip\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)')) {
        $name = $m.Groups['name'].Value
        if ($name -in @('this', 'sender', 'item', 'marker', 'element', 'control')) { continue }
        $namedInCode[$name] = $true
    }
}

# ---------------------------------------------------------------------------------------
# Walk the XAML
# ---------------------------------------------------------------------------------------
function Get-Dimension {
    param([System.Xml.XmlElement] $Element, [string[]] $Names)

    foreach ($n in $Names) {
        $raw = $Element.GetAttribute($n)
        $value = 0.0
        if (-not [string]::IsNullOrWhiteSpace($raw) -and [double]::TryParse($raw, [ref] $value)) {
            return $value
        }
    }

    return 0.0
}

function Test-HasText {
    param([System.Xml.XmlElement] $Element)

    foreach ($n in @('Text', 'Content', 'Label')) {
        $raw = $Element.GetAttribute($n)
        if (-not [string]::IsNullOrWhiteSpace($raw) -and $raw -notmatch '^\s*\{') { return $true }
    }

    # A child TextBlock carrying literal text counts: it is what gives the target width.
    foreach ($child in $Element.GetElementsByTagName('TextBlock')) {
        $raw = $child.GetAttribute('Text')
        if (-not [string]::IsNullOrWhiteSpace($raw) -and $raw -notmatch '^\s*\{') { return $true }
    }

    return $false
}

$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

$xamlFiles = Get-ChildItem $src -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

foreach ($file in $xamlFiles) {
    [xml] $doc = Get-Content $file.FullName -Raw
    $relative = $file.FullName.Substring($Root.Length).TrimStart('\')

    foreach ($element in $doc.SelectNodes('//*')) {
        if ($element -isnot [System.Xml.XmlElement]) { continue }

        $local = $element.LocalName
        if ($local -in $buttonLike) { continue }

        $name = $element.GetAttribute('Name', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $element.GetAttribute('Name') }

        # Is this a pointer target?
        $hasXamlTip = -not [string]::IsNullOrWhiteSpace($element.GetAttribute('ToolTipService.ToolTip'))
        $hasCodeTip = -not [string]::IsNullOrWhiteSpace($name) -and $namedInCode.ContainsKey($name)
        if (-not ($hasXamlTip -or $hasCodeTip)) { continue }

        $checked++

        $label = if ([string]::IsNullOrWhiteSpace($name)) { "<$local>" } else { $name }
        if ($allowed.ContainsKey($label)) {
            if ([string]::IsNullOrWhiteSpace($allowed[$label])) {
                $failures.Add("${relative}: '$label' is allow-listed with no reason given.")
            }
            continue
        }

        $height = Get-Dimension -Element $element -Names @('MinHeight', 'Height')
        $width = Get-Dimension -Element $element -Names @('MinWidth', 'Width')
        $styled = $styledFloor.ContainsKey($local)

        if ($height -lt $minimum -and -not $styled) {
            $failures.Add(
                "${relative}: '$label' ($local) carries a tooltip, so it is a pointer target, " +
                "but declares no MinHeight of $minimum (A11Y-5).")
            continue
        }

        if (-not (Test-HasText -Element $element) -and $width -lt $minimum -and -not $styled) {
            $failures.Add(
                "${relative}: '$label' ($local) is a pointer target with no text to give it " +
                "width, and declares no MinWidth of $minimum (A11Y-5).")
        }
    }
}

Write-Host "Pointer targets checked (non-button, tooltip-bearing): $checked"

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "A11Y-5 requires pointer targets of at least $minimum x $minimum px at all times." -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  $f" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Wrap the target in a Border with Background="Transparent" and MinWidth/MinHeight of 32.'
    Write-Host 'Transparent, not unset: an unset Background is null and is not hit-testable at all,'
    Write-Host 'so padding alone enlarges the box on screen and changes nothing for the pointer.'
    exit 1
}

if ($checked -eq 0) {
    Write-Host 'No non-button pointer targets found - check the detection rather than trusting this.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'PASS - every non-button pointer target declares A11Y-5 floor.' -ForegroundColor Green
exit 0
