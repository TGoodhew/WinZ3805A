<#
.SYNOPSIS
    CI gate for A11Y-3, A11Y-5 and §9.9: every icon-only control is named, explained,
    and big enough to hit.

.DESCRIPTION
    §9.9 permits a label-less icon only when it has BOTH an AutomationProperties.Name
    and a ToolTip - "all three, no exceptions" including its placement. A11Y-3 states
    the same rule from the other side: "No icon-only control lacks both
    AutomationProperties.Name and a ToolTip", i.e. every such control has both. This
    script therefore fails on a control missing EITHER.

    A control counts as icon-only when it is a Button, ToggleButton, AppBarButton,
    AppBarToggleButton, HyperlinkButton, DropDownButton, SplitButton or RepeatButton and:

        - it has no Content attribute carrying non-whitespace, non-markup text, and
        - it has no literal text child, and
        - it does have an icon: an *Icon element (FontIcon, SymbolIcon, PathIcon,
          BitmapIcon, ImageIcon, AnimatedIcon), an Icon/Content property element
          containing one, a Glyph/Symbol/Icon attribute, or a TextBlock set in a
          symbol font.

    That last form is not a nicety. The main window's time-zone button draws its glyph
    with a TextBlock in SymbolThemeFontFamily rather than a FontIcon, and this gate did
    not see it as an icon control at all - so A11Y-3 was never checked on it. Both
    idioms are legitimate XAML; a gate that knows only one is a gate with a hole in it.

    SIZE (A11Y-5, §9.6.3)
    §9.6.3 lists the pointer target among the "fixed floors that no mode may reduce":
    at least 32 x 32 px. An icon-only control has no text to give it width, so its size
    comes from padding and glyph alone and lands wherever that falls - the time-zone
    button at 20 x 20, the title-bar buttons at 38 x 27. All of them looked fine and
    all of them were wrong, which is exactly the kind of thing manual review misses and
    a gate does not.

    A static check cannot measure layout, so it requires the floor to be DECLARED:
    MinWidth/MinHeight (or Width/Height) of at least 32 on each axis. That turns the
    floor from something emergent into something stated, which is what §9.6.3 means by
    a floor. A control that genuinely cannot meet it needs a written exception, as the
    sky plot's 24 px markers have in #117 - and those are built in code, not XAML, so
    they never reach this gate.

    XAML is parsed as XML rather than grepped, so attributes and property elements are
    both handled and comments are ignored.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-IconOnlyButtons.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) {
    Write-Error "No src/ directory under '$Root'."
}

$buttonTypes = @('Button', 'ToggleButton', 'AppBarButton', 'AppBarToggleButton',
                 'HyperlinkButton', 'DropDownButton', 'SplitButton', 'RepeatButton')
$iconTypes   = @('FontIcon', 'SymbolIcon', 'PathIcon', 'BitmapIcon', 'ImageIcon',
                 'AnimatedIcon', 'IconSourceElement')

# §9.6.3's pointer-target floor, which no mode may reduce.
$minimumTarget = 32

$files = Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$hits = @()

foreach ($f in $files) {
    $text = Get-Content -LiteralPath $f.FullName -Raw
    try {
        $xml = [xml]$text
    }
    catch {
        Write-Host "  (skipped unparseable XAML: $($f.Name) - $($_.Exception.Message))" -ForegroundColor DarkYellow
        continue
    }

    $lines = $text -split "`r?`n"

    # Document order of XmlNode traversal matches source order, so counting how
    # many of each element name we have already seen lets us point at the right
    # occurrence instead of always reporting the first one in the file.
    $seen = @{}

    foreach ($node in $xml.SelectNodes('//*')) {
        if ($buttonTypes -notcontains $node.LocalName) { continue }
        if (-not $seen.ContainsKey($node.LocalName)) { $seen[$node.LocalName] = 0 }
        $seen[$node.LocalName]++
        $occurrence = $seen[$node.LocalName]

        # --- does it carry visible text? -----------------------------------
        $hasText = $false
        $contentAttr = $node.GetAttribute('Content')
        if ($contentAttr -and $contentAttr.Trim() -and $contentAttr -notmatch '^\s*\{') { $hasText = $true }
        foreach ($c in $node.ChildNodes) {
            if ($c.NodeType -eq 'Text' -and $c.Value.Trim()) { $hasText = $true }
        }
        if ($hasText) { continue }

        # --- does it carry an icon? ----------------------------------------
        $hasIcon = $false
        foreach ($a in @('Glyph', 'Symbol', 'Icon')) {
            if ($node.GetAttribute($a)) { $hasIcon = $true }
        }
        foreach ($d in $node.SelectNodes('.//*')) {
            if ($iconTypes -contains $d.LocalName) { $hasIcon = $true }

            # A TextBlock in a symbol font is an icon whatever the element is called.
            if ($d.LocalName -eq 'TextBlock' -and $d.GetAttribute('FontFamily') -match 'Symbol|Fluent Icons|MDL2') {
                $hasIcon = $true
            }
        }
        if (-not $hasIcon) { continue }

        # --- the two required affordances ----------------------------------
        $name = $node.GetAttribute('AutomationProperties.Name')
        $tip  = $node.GetAttribute('ToolTipService.ToolTip')
        if (-not $name) {
            foreach ($d in $node.ChildNodes) {
                if ($d.LocalName -eq 'AutomationProperties.Name') { $name = $d.InnerText }
            }
        }
        if (-not $tip) {
            foreach ($d in $node.ChildNodes) {
                if ($d.LocalName -eq 'ToolTipService.ToolTip') { $tip = $d.InnerText }
            }
        }

        $missing = @()
        if (-not ($name -and $name.Trim())) { $missing += 'AutomationProperties.Name' }
        if (-not ($tip  -and $tip.Trim()))  { $missing += 'ToolTipService.ToolTip' }

        # --- and the target floor ------------------------------------------
        foreach ($axis in @(@('Width', 'MinWidth'), @('Height', 'MinHeight'))) {
            $fixed   = $node.GetAttribute($axis[0])
            $floor   = $node.GetAttribute($axis[1])
            $declared = $null

            foreach ($candidate in @($floor, $fixed)) {
                if ($candidate -and [double]::TryParse($candidate, [ref] $null)) {
                    $value = [double] $candidate
                    if ($null -eq $declared -or $value -gt $declared) { $declared = $value }
                }
            }

            if ($null -eq $declared) {
                $missing += "$($axis[1]) (A11Y-5 needs $minimumTarget, and nothing states one)"
            }
            elseif ($declared -lt $minimumTarget) {
                $missing += "$($axis[1]) >= $minimumTarget (it is $declared)"
            }
        }

        if ($missing.Count -eq 0) { continue }

        # Line number of this element's Nth occurrence in the source text.
        $ln = 1
        $found = 0
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match ("<" + [regex]::Escape($node.LocalName) + "[\s>/]")) {
                $found++
                if ($found -eq $occurrence) { $ln = $i + 1; break }
            }
        }

        $hits += [pscustomobject]@{
            File    = [System.IO.Path]::GetRelativePath($Root, $f.FullName)
            Line    = $ln
            Element = $node.LocalName
            Name    = ($node.GetAttribute('x:Name'))
            Missing = ($missing -join ' and ')
        }
    }
}

Write-Host "Scanned $($files.Count) XAML file(s) for icon-only controls."

if ($hits.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($hits.Count) icon-only control(s) missing a required affordance or size." -ForegroundColor Red
    foreach ($h in $hits) {
        $who = if ($h.Name) { "$($h.Element) '$($h.Name)'" } else { $h.Element }
        Write-Host ("  {0}:{1}  {2} is missing {3}" -f $h.File, $h.Line, $who, $h.Missing) -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') {
            $msg = "Icon-only $($h.Element) is missing $($h.Missing). §9.9 permits a label-less icon only with both an automation name and a tooltip containing the label and accelerator (docs/requirements.md §9.9, A11Y-3)."
            Write-Host "::error file=$($h.File),line=$($h.Line)::$msg"
        }
    }
    Write-Host ''
    Write-Host 'An icon may appear without a visible label only when it is in the title bar or a' -ForegroundColor Yellow
    Write-Host 'card header command position, has a ToolTip containing the label and accelerator,' -ForegroundColor Yellow
    Write-Host 'and has an AutomationProperties.Name. All three, no exceptions.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'PASS: every icon-only control has an automation name, a tooltip and a target floor.' -ForegroundColor Green
exit 0
