<#
.SYNOPSIS
    CI gate for #403: no DispatcherQueueHandler is cached in a field and handed to TryEnqueue.

.DESCRIPTION
    DispatcherQueue.TryEnqueue mints a COM callable wrapper for the handler on EVERY call. It does
    not reuse one by delegate identity. The runtime then records each wrapper in a
    List<ManagedObjectWrapperHolder> hung off the wrapped object by a dependent handle, and that
    list is only ever appended to - RegisterManagedObjectWrapperForDiagnostics never removes.

    So the lifetime of the delegate is the lifetime of the list. Hand TryEnqueue a fresh delegate
    and its list dies with it at the next gen0. Hand it the same instance every time and the list
    has an owner that never dies, and it grows for the life of the process.

    That is #403. Ten views and two services each cached one handler in a readonly field, MainPage
    renders about 2.6 times a second, and its list reached 8,192 slots - 65,560 bytes, one array -
    in 38 minutes. Every other holder array in the same dump was 56 or 88 bytes.

    THE POINT WORTH NOT REDISCOVERING IS THAT THE FIELD WAS DELIBERATE. It was introduced as the
    fix for #399, on the guess that the wrapper was cached by delegate identity, so that one
    delegate would mean one wrapper. It is not, so caching the delegate never reduced the minting -
    it did nothing at all except give the record an immortal owner. The comment on the field said
    "a field rather than a lambda so the hop allocates nothing", which is true, irrelevant, and was
    copied into twelve places. Two of them stated the inverted belief outright. This is precisely a
    rule review cannot hold, because the wrong version looks like the careful version.

    Two checks, both narrow and both about the measured defect:

      1. No field or property of type DispatcherQueueHandler. There is no legitimate use for one
         in this tree, and the field IS the leak.
      2. TryEnqueue is never handed a bare field. Catches the same thing where the field's type
         was inferred or spelled differently.

    A method group, a lambda and `new DispatcherQueueHandler(...)` all allocate a fresh delegate
    per call, so any of them is safe. The last is preferred and is what the tree uses: a delegate
    creation expression is SPECIFIED to produce a new instance, where the other two are merely
    uncached by the compiler we happen to build with today.
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$src = Join-Path $Root 'src'
if (-not (Test-Path $src)) { Write-Error "No src/ directory under '$Root'." }

$targets = @(Get-ChildItem -Path $src -Recurse -Filter '*.cs' -File |
    Where-Object { $_.DirectoryName -notlike '*\bin\*' -and $_.DirectoryName -notlike '*\obj\*' })

# A field or property declaration whose type is DispatcherQueueHandler, qualified or not. Requires
# an accessibility keyword so a local variable inside a method is not matched.
$fieldPattern =
    '^\s*(?:private|internal|protected|public)\b[^;{}()]*\bDispatcherQueueHandler\s+\w+\s*(?:;|=>|=[^=])'

# TryEnqueue handed a single bare identifier that is a field - `_render`, `this._render`.
$boundPattern = '\bTryEnqueue\(\s*(?:this\.)?(_\w+)\s*\)'

$failures = @()
$enqueueSites = 0

foreach ($file in $targets) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName)
    $lineNumber = 0

    foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
        $lineNumber++

        # Comments describe the rule in several of these files; they are not code.
        $code = $line -replace '//.*$', ''
        if ($code.TrimStart().StartsWith('///') -or $code.TrimStart().StartsWith('*')) { continue }

        if ($code -match '\bTryEnqueue\(') { $enqueueSites++ }

        if ($code -match $fieldPattern) {
            $failures += "  ${relative}:${lineNumber} : a DispatcherQueueHandler stored in a field - $($code.Trim())"
        }

        if ($code -match $boundPattern) {
            $failures += "  ${relative}:${lineNumber} : TryEnqueue handed the cached field '$($Matches[1])' - $($code.Trim())"
        }
    }
}

if ($enqueueSites -eq 0) {
    Write-Error 'No TryEnqueue call was found under src/. This gate cannot pass by finding nothing to check.'
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: a dispatcher handler is cached and reused, which leaks for the life of the process (#403).' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ''
    Write-Host 'TryEnqueue mints a COM callable wrapper per call and records it in a list owned by the' -ForegroundColor Yellow
    Write-Host 'delegate. A reused delegate owns a list that never dies. Build a fresh handler at the' -ForegroundColor Yellow
    Write-Host 'call site instead - `TryEnqueue(new DispatcherQueueHandler(Render))` - which costs one' -ForegroundColor Yellow
    Write-Host 'gen0 allocation and lets the record die with it.' -ForegroundColor Yellow
    exit 1
}

Write-Host "PASS: no cached dispatcher handler ($enqueueSites TryEnqueue call sites checked)."
