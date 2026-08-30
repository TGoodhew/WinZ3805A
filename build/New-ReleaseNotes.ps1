<#
.SYNOPSIS
    Builds the release body from the standing template plus what actually shipped.

.DESCRIPTION
    build\release-notes.md is the half that does not change between releases -
    how to install, what the certificate prompt means, what the application is.
    This adds the half that must never be written down in advance: the
    certificate the package was signed with, and the hash of the file people
    will download.

    BOTH ARE READ OFF THE ARTIFACT, not typed. A thumbprint or a checksum that a
    human copies from the last release is one that eventually describes a
    different file than the one attached, and the failure is silent - it looks
    exactly like a correct release until somebody checks, which is the one thing
    those lines exist for.

    It lives in a script rather than inline in release.yml because the body is
    markdown containing tables and fenced code, and both are made of characters
    that end a YAML block scalar early. The workflow calls this and passes the
    result to `gh release create --notes-file`.

.PARAMETER ZipPath
    The release zip. Its SHA-256 goes in the notes.

.PARAMETER BundlePath
    The signed .msixbundle inside it, read for the signing certificate. Defaults
    to the newest one under src\WinZ3805A\AppPackages.

.PARAMETER OutputPath
    Where to write the assembled markdown. Defaults to a temporary file, whose
    path is returned.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ZipPath,

    [string]$BundlePath,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$template = Join-Path $PSScriptRoot 'release-notes.md'

if (-not (Test-Path $template)) { throw "No release-notes template at $template." }
if (-not (Test-Path $ZipPath)) { throw "No zip at $ZipPath." }

if (-not $BundlePath) {
    $BundlePath = (Get-ChildItem (Join-Path $repo 'src\WinZ3805A\AppPackages') `
            -Recurse -Filter '*.msixbundle' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

if (-not $BundlePath) { throw 'No .msixbundle found, so there is no signature to describe.' }

$signature = Get-AuthenticodeSignature $BundlePath
$signer = $signature.SignerCertificate

if (-not $signer) {
    throw "$BundlePath carries no signature. A release must not describe one it does not have."
}

# Stated rather than assumed, for the reason New-SideloadPackage.ps1 gives: an
# untimestamped package stops installing the day its certificate expires, and
# the notes below promise the opposite.
if (-not $signature.TimeStamperCertificate) {
    throw ("$([IO.Path]::GetFileName($BundlePath)) is not timestamped, and the notes claim it is. " +
           'Rebuild with a reachable timestamp authority.')
}

$hash = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
$zipName = [IO.Path]::GetFileName($ZipPath)

$notes = Get-Content $template -Raw

$verification = @"

## Verifying what you downloaded

| | |
|---|---|
| Signed by | ``$($signer.Subject)`` |
| Certificate thumbprint (SHA-1) | ``$($signer.Thumbprint)`` |
| Certificate expires | $($signer.NotAfter.ToString('d MMMM yyyy')) |
| Zip SHA-256 | ``$hash`` |

Windows shows you the thumbprint in the trust prompt; it should match the row
above. To check the download itself:

``````powershell
Get-FileHash .\$zipName -Algorithm SHA256
``````

The signature is **timestamped**, so this release stays installable after the
certificate expires — a signature countersigned while the certificate was valid
stays valid. Nothing here needs re-downloading on that date.

"@

if (-not $OutputPath) {
    $OutputPath = Join-Path ([IO.Path]::GetTempPath()) 'winz3805a-release-notes.md'
}

Set-Content -Path $OutputPath -Value ($notes + $verification) -Encoding utf8

Write-Host "Release notes for $zipName"
Write-Host "  signed by   $($signer.Subject)"
Write-Host "  thumbprint  $($signer.Thumbprint)"
Write-Host "  expires     $($signer.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  sha-256     $hash"

$OutputPath
