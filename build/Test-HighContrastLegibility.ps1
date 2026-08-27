<#
.SYNOPSIS
    CI gate for A11Y-8 / §9.2: nothing drawn ON a surface in the HighContrast theme is
    defined AS that surface.

.DESCRIPTION
    Test-ThemeDictionaryParity.ps1 already checks that every token exists in every theme
    with the same type. #218 is the case that showed key parity is not the same thing as
    legibility: WzSequential1Brush and WzSequential2Brush existed in HighContrast, with
    the right type, defined as SystemColorWindowColor — the surface they are painted on.

    A tracked satellite on the sky plot is filled with a step of that ramp chosen by
    signal strength. Steps 1 and 2 span C/N 26-34, and §11.1 calls 35 and above good, so
    every satellite that was not already good was drawn in the page background and read
    as an untracked hollow ring. The legend swatch, hard-wired to step 5, went on showing
    a filled dot. It compiled, it passed every gate, and it had never been looked at.

    No existing gate could catch it. Test-ContrastFloor.ps1 cannot resolve HighContrast at
    all — those tokens are the user's own SystemColor* choices, which is exactly why this
    check is structural rather than photometric: it does not need to know what colour the
    user's window is to know that a foreground must not BE it.

    So: in a HighContrast dictionary, a brush defined as SystemColorWindowColor must be a
    surface, and must be named here with a reason. Anything else is a foreground painted
    in the background colour.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-HighContrastLegibility.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Surfaces, and why each is allowed to be the window colour. A row here is a claim that the
# token names something drawn UNDER other content rather than on top of it — so adding one is
# a decision, not a formality, and the reason is asserted non-empty below.
$surfaces = [ordered]@{
    'WzPageBackgroundFallbackBrush' = 'The page itself, behind everything, when the backdrop cannot draw.'
    'WzLayerFillBrush'              = 'A layer fill. §9.2 collapses fills to the window colour under high contrast; strokes separate.'
    'WzCardFillBrush'               = 'A card fill, collapsed by the same §9.2 rule. Its border carries the edge.'
    'WzOverlayFillBrush'            = 'The flyout and dialog surface, which content is drawn on.'
}

$backgroundColour = 'SystemColorWindowColor'
$xamlNs = 'http://schemas.microsoft.com/winfx/2006/xaml'

$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) {
    Write-Error "No src/ directory under '$Root'."
}

foreach ($entry in $surfaces.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value)) {
        Write-Error "The surface allowlist entry '$($entry.Key)' has no reason. A row without one is an exemption, not a decision."
    }
}

# @() so a one-file tree still yields a collection under Set-StrictMode.
$targets = @(Get-ChildItem -Path $src -Recurse -Filter '*.xaml' -File |
    Where-Object { $_.DirectoryName -notlike '*\bin\*' -and $_.DirectoryName -notlike '*\obj\*' })

$failures = @()
$checked = 0
$allowed = 0

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)

    try {
        [xml] $doc = Get-Content -LiteralPath $file.FullName -Raw
    }
    catch {
        Write-Error "$relative is not well-formed XML: $($_.Exception.Message)"
    }

    $ns = New-Object System.Xml.XmlNamespaceManager $doc.NameTable
    $ns.AddNamespace('x', $xamlNs)

    # Parsing as XML rather than grepping means XML comments are excluded for free, which
    # matters here: the comment added with #218 names the offending colour on purpose.
    $dictionaries = @($doc.SelectNodes('//*[local-name()="ResourceDictionary"][@x:Key="HighContrast"]', $ns))

    foreach ($dictionary in $dictionaries) {
        $checked++

        foreach ($brush in @($dictionary.SelectNodes('.//*[local-name()="SolidColorBrush"]'))) {
            $key = $brush.GetAttribute('Key', $xamlNs)
            if ([string]::IsNullOrWhiteSpace($key)) { continue }

            $colour = $brush.GetAttribute('Color')
            if ($colour -notmatch [regex]::Escape($backgroundColour)) { continue }

            if ($surfaces.Contains($key)) { $allowed++; continue }

            $failures += "  $relative : '$key' is defined as $backgroundColour, the surface it is drawn on."
        }
    }
}

if ($checked -eq 0) {
    Write-Error "No HighContrast dictionary found under src/. This gate cannot pass by finding nothing to check."
}

if ($failures.Count -gt 0) {
    Write-Host "FAIL: a HighContrast token is defined as the surface it is painted on." -ForegroundColor Red
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
    Write-Host "Either give the token a visible colour, or — if it really names a surface — add it to" -ForegroundColor Yellow
    Write-Host "the allowlist in this script with the reason it is drawn under other content." -ForegroundColor Yellow
    exit 1
}

Write-Host "PASS: no HighContrast foreground token is defined as $backgroundColour ($checked dictionaries checked, $allowed surfaces allowed)."
