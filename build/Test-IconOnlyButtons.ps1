<#
.SYNOPSIS
    CI gate for A11Y-3 / §9.9: no icon-only control lacks an automation name or a tooltip.

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
          containing one, or a Glyph/Symbol/Icon attribute.

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
    Write-Host "FAIL: $($hits.Count) icon-only control(s) missing a required affordance." -ForegroundColor Red
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

Write-Host 'PASS: every icon-only control has an automation name and a tooltip.' -ForegroundColor Green
exit 0
