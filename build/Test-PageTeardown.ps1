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

<#
.SYNOPSIS
    The text of every place a view undoes what it set up.
.DESCRIPTION
    Detach and OnNavigatedFrom for a page, and the Unloaded and Closed handlers for a view whose
    lifecycle is not the Frame's - MainPage is the window's content and stops its ticker in
    Unloaded, DetailsWindow does its own teardown in Closed. A Stop() or a -= anywhere else in the
    file is not teardown, however tidy it looks: it runs when the page felt like it, not when the
    page went away.
#>
function TeardownRegions {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    $body = '\{(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*\}'
    $regions = @()

    foreach ($pattern in @(
            "private void Detach\([^)]*\)\s*$body",
            "protected override void OnNavigatedFrom\([^)]*\)\s*$body",
            "(?:Unloaded|Closed)\s*\+=[^;{]*$body",
            "(?:Unloaded|Closed)\s*\+=[^;{]*;")) {
        foreach ($match in [regex]::Matches($Text, $pattern)) { $regions += $match.Value }
    }

    return ($regions -join "`n")
}

$failures = @()
$checked = 0

# Every static event declared by a view, found rather than listed, so a new one is covered the day
# it is written. A static event holds its subscribers for the life of the PROCESS: an instance that
# joins one and never leaves is pinned whether or not it renders, costs CPU, or has any other
# subscription at all (#400).
$staticEvents = @()
foreach ($file in Get-ChildItem $viewsPath -Filter '*.xaml.cs') {
    $declaring = $file.Name -replace '\.xaml\.cs$', ''
    foreach ($match in [regex]::Matches(
            (Get-Content -LiteralPath $file.FullName -Raw),
            '(?m)^\s*(?:public|internal|protected)\s+static\s+event\s+[\w<>?, ]+?\s+(?<name>\w+)\s*;')) {
        $staticEvents += [pscustomobject]@{
            Type  = $declaring
            Event = $match.Groups['name'].Value
        }
    }
}

foreach ($file in Get-ChildItem $viewsPath -Filter '*.xaml.cs' | Sort-Object Name) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    # Rule 3 (#400). A started DispatcherTimer is rooted by the dispatcher and its Tick handler
    # captures the view, so a timer that is started and never stopped keeps the view alive on its
    # own. This is what left one TimePage per visit while CPU stayed perfectly flat.
    # THE STOP HAS TO BE IN THE TEARDOWN, not merely somewhere in the file. The first version of
    # this rule asked only that a Stop() existed, and that is not the same claim: DiagnosticsPage
    # stopped _loadingTimer at the end of its loading path and left it running on navigation, so a
    # gate looking anywhere would have passed the very page it was written for. Verified by
    # deleting only the teardown Stop and watching the loose form pass (#400).
    $teardown = TeardownRegions $text

    foreach ($declaration in [regex]::Matches(
            $text, '(?:private|protected|internal)\s+(?:readonly\s+)?DispatcherTimer\s+(?<name>_\w+)')) {
        $timer = $declaration.Groups['name'].Value
        if ($text -notmatch [regex]::Escape("$timer.Start()")) { continue }

        $checked++
        if ($teardown -notmatch [regex]::Escape("$timer.Stop()")) {
            $failures += [pscustomobject]@{
                File = $file.Name
                Rule = 'timer not stopped on teardown'
                Why  = ("starts $timer and does not stop it in Detach, OnNavigatedFrom, Unloaded " +
                        'or Closed. A Stop() elsewhere in the file does not count: a running ' +
                        'DispatcherTimer is rooted by the dispatcher and its Tick captures this ' +
                        'view, so whatever else the timer does, the view cannot be collected while ' +
                        'it runs - one instance per visit, with no rendering to give it away (#400).')
            }
        }
    }

    # Rule 4 (#400). Joining a static event and never leaving it.
    foreach ($declared in $staticEvents) {
        $subscribe = "$($declared.Type).$($declared.Event) +="
        if ($text -notmatch [regex]::Escape($subscribe)) { continue }

        $checked++
        if ($teardown -notmatch [regex]::Escape("$($declared.Type).$($declared.Event) -=")) {
            $failures += [pscustomobject]@{
                File = $file.Name
                Rule = 'static event not left on teardown'
                Why  = ("subscribes to the static $($declared.Type).$($declared.Event) and does " +
                        'not unsubscribe in Detach, OnNavigatedFrom, Unloaded or Closed. A static ' +
                        'event outlives every view, so this pins one instance per subscription for ' +
                        'the life of the process (#400).')
            }
        }
    }

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
