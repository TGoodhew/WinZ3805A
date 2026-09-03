<#
.SYNOPSIS
    CI gate for #388: a page that subscribes to something must let go of it when it is navigated
    away from.

.DESCRIPTION
    THE DEFECT THIS EXISTS TO CATCH IS A LEAK THAT LOOKS LIKE NOTHING. Every page in the Details
    window built a view model in OnNavigatedTo and subscribed to it; every view model subscribed to
    ReceiverStateStore, which is registered for the application's lifetime. That makes a chain -
    store to model to page - anchored at an object that never dies, so a page went on rendering on
    every reading after the user navigated away from it, and a second visit left a second one.

    Measured before the fix, with the window showing Timing: the off-screen Overview page's own
    handler accounted for 216 ms of a 15-second sample after one visit and 585 ms after four.

    THE PAGES HAD Unloaded HANDLERS AND THEY WERE NOT ENOUGH. Unloaded stopped the staleness ticker,
    which was never what kept the page working; the model was. So this checks the hook that
    corresponds to the subscription - OnNavigatedTo pairs with OnNavigatedFrom - rather than any
    hook at all.

    TWO RULES.

    1. A page that subscribes in OnNavigatedTo overrides OnNavigatedFrom, and that override calls
       something that unsubscribes.

    2. No page subscribes to PropertyChanged with a lambda. A lambda cannot be passed to -=, so a
       page that uses one CANNOT undo it however well-intentioned its teardown is. This is the rule
       that would have prevented the whole defect: the seven models and nine pages involved were all
       written with `+= (_, _) => ...`.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.EXAMPLE
    pwsh build/Test-PageTeardown.ps1
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$viewsPath = Join-Path $Root 'src\WinZ3805A\Views'

if (-not (Test-Path $viewsPath)) {
    Write-Error "Cannot find '$viewsPath'. If the pages moved, update this gate - do not delete it."
}

# Pages that are not hosted in the Details window's Frame, so OnNavigatedFrom never fires for them
# and is not the right hook. Each is a claim about where the page lives, not an exemption from
# tidying up - both still detach in Unloaded, which IS their lifecycle.
$notFrameHosted = @{
    'MainPage.xaml.cs' = 'The main window''s own content, set as Window.Content and never navigated.'
}

$failures = @()
$checked = 0

foreach ($file in Get-ChildItem $viewsPath -Filter '*.xaml.cs' | Sort-Object Name) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    # Rule 2 applies to every page, hosted or not: an unsubscribable handler is a defect wherever
    # it is written.
    foreach ($match in [regex]::Matches($text, '(?<what>\w+)\.PropertyChanged\s*\+=\s*\(')) {
        $checked++
        $failures += [pscustomobject]@{
            File = $file.Name
            Rule = 'lambda'
            Why  = ("subscribes to $($match.Groups['what'].Value).PropertyChanged with a lambda, " +
                    'which cannot be removed with -=. Use a named method (#388).')
        }
    }

    if ($notFrameHosted.ContainsKey($file.Name)) { continue }
    if ($text -notmatch 'protected override (async )?void OnNavigatedTo') { continue }

    # Does this page subscribe to anything at all? A page that only reads state has nothing to undo.
    $subscribes = $text -match '\.StatusChanged\s*\+=' -or
                  $text -match '\.PropertyChanged\s*\+=' -or
                  $text -match '_stalenessTicker\.Start\(\)'

    if (-not $subscribes) { continue }

    $checked++

    if ($text -notmatch 'protected override void OnNavigatedFrom') {
        $failures += [pscustomobject]@{
            File = $file.Name
            Rule = 'no teardown'
            Why  = ('subscribes in OnNavigatedTo and never overrides OnNavigatedFrom, so everything ' +
                    'it subscribed to outlives the navigation - and the store outlives the ' +
                    'application, so the page does too (#388).')
        }
        continue
    }

    # And the override must actually undo something rather than merely existing.
    $body = [regex]::Match(
        $text,
        'protected override void OnNavigatedFrom\([^)]*\)\s*\{(?<body>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}')

    if (-not $body.Success -or $body.Groups['body'].Value -notmatch '(Detach\(\)|\-=)') {
        $failures += [pscustomobject]@{
            File = $file.Name
            Rule = 'empty teardown'
            Why  = ('overrides OnNavigatedFrom but neither calls Detach() nor unsubscribes from ' +
                    'anything, which is the shape of a hook somebody added and left empty (#388).')
        }
    }
}

Write-Host "Checked $checked subscription site(s) across $((Get-ChildItem $viewsPath -Filter '*.xaml.cs').Count) page(s)."

if ($notFrameHosted.Count -gt 0) {
    foreach ($pair in $notFrameHosted.GetEnumerator() | Sort-Object Name) {
        Write-Host ("  not Frame-hosted: {0} - {1}" -f $pair.Name, $pair.Value) -ForegroundColor DarkGray
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($failures.Count) page-lifecycle problem(s)." -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host ("  {0,-28} [{1}] {2}" -f $f.File, $f.Rule, $f.Why) -ForegroundColor Red
        if ($env:GITHUB_ACTIONS -eq 'true') {
            Write-Host "::error file=src/WinZ3805A/Views/$($f.File)::$($f.Why)"
        }
    }
    Write-Host ''
    Write-Host 'A page that cannot be let go of goes on rendering on every reading after the user has' -ForegroundColor Yellow
    Write-Host 'moved away from it, once per visit. That is #385, and it took ten hours and 4.9 GB to find.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'PASS: every page that subscribes undoes it on navigation, and no page subscribes with a lambda it cannot remove.' -ForegroundColor Green
exit 0
