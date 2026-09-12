<#
.SYNOPSIS
    Fails when a custom control pushes its own automation name instead of computing
    it in an automation peer.

.DESCRIPTION
    `AutomationProperties.SetName` takes the control as its first argument, so every
    call hands that control across to WinRT and mints a COM callable wrapper. The
    runtime records each wrapper in a `List<ManagedObjectWrapperHolder>` hung off the
    wrapped object by a dependent handle, and that list is ONLY EVER APPENDED TO. A
    long-lived control whose name is pushed on every reading therefore owns a list
    that grows for the life of the process.

    #403 found this mechanism and fixed it for ReadoutTile, which computes its name in
    an AutomationPeer instead - the boundary is crossed once, when the framework
    creates the peer. The gate #403 left behind, Test-NoCachedDispatcherHandlers,
    checks a DIFFERENT route to the same growth: a delegate cached in a field and
    handed to DispatcherQueue.TryEnqueue. It has nothing to say about this one.

    #487 is what that gap cost. The holder arrays kept growing - 65,536 slots in one
    array after 49.5 hours, five of them, roughly 328,000 retained wrappers - with the
    dispatcher gate green and the issue title saying so. SeverityPill was pushing on
    every Text change, and fifteen of those exist with four on the main window, which
    §9.1 expects to be left running for weeks.

    The fix is always the same shape: expose the phrase as a property, override
    OnCreateAutomationPeer, and answer GetNameCore from it.

.NOTES
    THE ALLOWLIST IS NOT AN EXEMPTION, it is a claim that pushing is correct here, and
    each row states why. Two cases are genuinely different:

      - A name pushed on a KEYSTROKE rather than on a reading is bounded by a person's
        fingers, not by uptime.
      - Pushing raises an automation property-changed event and a pulled name does not.
        Where that event is the point - the user just moved a cursor and has to hear
        where it landed - converting would trade a leak that cannot happen for an
        accessibility regression that would.

    Those are judgements, so they are written down rather than inferred, and a new row
    needs the same argument made out loud.
#>

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $PSScriptRoot '..\src')
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path $Root).Path

# file = why pushing is right here. A row without a reason is not a row.
$allowed = @{
    'SkyPlotControl.cs'      = 'Fires on a keystroke, not a reading, and the property-changed event is what tells the user where the cursor landed.'
    'ConnectionStatusPill.cs' = 'Fires when the connection state changes, not on a reading, and the same call keeps the tooltip a pointer user needs.'
}

$files = Get-ChildItem -Path $root -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    Where-Object { $_.FullName -match '\\Controls\\|\\Views\\' }

$failures = @()

foreach ($file in $files) {
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*(//|///|\*)') { continue }

        # Only `this` - pushing a name onto a child element built per render is a
        # different thing entirely: that element is short-lived and its holder dies
        # with it, which is the whole reason this leak needs a LONG-LIVED object.
        if ($line -match 'AutomationProperties\.SetName\(\s*this\s*,') {
            if ($allowed.ContainsKey($file.Name)) { continue }

            $failures += [pscustomobject]@{
                File = $file.FullName.Replace("$repo\", '')
                Line = $i + 1
                Text = $line.Trim()
            }
        }
    }
}

# An allowlist row for a file that no longer pushes is a stale claim, and stale claims
# are how an allowlist becomes a hole.
$stale = @()
foreach ($name in $allowed.Keys) {
    $found = $files | Where-Object { $_.Name -eq $name } | ForEach-Object {
        (Get-Content $_.FullName) -match 'AutomationProperties\.SetName\(\s*this\s*,'
    }
    if (-not $found) { $stale += $name }
}

foreach ($name in $allowed.Keys) {
    if ([string]::IsNullOrWhiteSpace($allowed[$name])) {
        $failures += [pscustomobject]@{ File = $name; Line = 0; Text = 'allowlisted with no reason given' }
    }
}

Write-Host ("Scanned {0} control and view file(s); {1} allowlisted with a reason." -f $files.Count, $allowed.Count)

if ($stale.Count -gt 0) {
    Write-Host ''
    Write-Host "FAIL: $($stale.Count) allowlist row(s) name a file that no longer pushes a name."
    $stale | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'Remove the row. A row that protects nothing is a hole waiting for the next push.'
    exit 1
}

if ($failures.Count -eq 0) {
    Write-Host 'PASS: no control pushes its own automation name outside the allowlist.'
    exit 0
}

Write-Host ''
Write-Host "FAIL: $($failures.Count) pushed automation name(s)."
foreach ($f in $failures) {
    Write-Host ("  {0}:{1}  {2}" -f $f.File, $f.Line, $f.Text)
}

Write-Host ''
Write-Host 'AutomationProperties.SetName takes the control, so every call marshals it into WinRT and'
Write-Host 'appends to a per-object list the runtime never shrinks. On a control that lives as long as'
Write-Host 'the window and is updated on every reading, that list grows without bound (#403, #487).'
Write-Host ''
Write-Host 'Compute the name instead: expose the phrase as a property, override OnCreateAutomationPeer,'
Write-Host 'and answer GetNameCore from it. ReadoutTile and SeverityPill are the worked examples.'
Write-Host ''
Write-Host 'If pushing is genuinely right - a keystroke rather than a reading, or an announcement where'
Write-Host 'the property-changed event is the point - add a row to $allowed with the reason.'
exit 1
