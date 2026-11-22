<#
.SYNOPSIS
    Builds the release body from the standing template plus what actually shipped.

.DESCRIPTION
    build\release-notes.md is the half that does not change between releases -
    how to install, what the signature means, what the application is.
    This adds the half that must never be written down in advance: who signed
    the package, through whom, and when, and the hash of the file people will
    download.

    BOTH ARE READ OFF THE ARTIFACT, not typed. A signer or a checksum that a
    human copies from the last release is one that eventually describes a
    different file than the one attached, and the failure is silent - it looks
    exactly like a correct release until somebody checks, which is the one thing
    those lines exist for.

    It lives in a script rather than inline in release.yml because the body is
    markdown containing tables and fenced code, and both are made of characters
    that end a YAML block scalar early. The workflow calls this and passes the
    result to `gh release create --notes-file`.

.PARAMETER ZipPath
    The release zips - the online one and the offline one (#588). Each one's
    SHA-256 goes in the notes, and so does the version of the .NET installer
    the offline zip carries, read out of the zip rather than assumed.

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
    [string[]]$ZipPath,

    [string]$BundlePath,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$template = Join-Path $PSScriptRoot 'release-notes.md'

if (-not (Test-Path $template)) { throw "No release-notes template at $template." }
foreach ($zip in $ZipPath) { if (-not (Test-Path $zip)) { throw "No zip at $zip." } }

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

Add-Type -AssemblyName System.IO.Compression.FileSystem

# One row per zip, online first. The offline zip names the .NET installer it carries, so that is
# read out of the archive itself: the notes then cannot claim a patch the zip does not have.
$rows = foreach ($zip in ($ZipPath | Sort-Object { $_ -like '*-offline.zip' })) {
    $zipName = [IO.Path]::GetFileName($zip)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    "| ``$zipName`` SHA-256 | ``$hash`` |"

    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip).Path)
    try {
        $installer = $archive.Entries | Where-Object { $_.FullName -like 'Runtime/dotnet-runtime-*-win-x64.exe' } |
            Select-Object -First 1
    }
    finally {
        $archive.Dispose()
    }

    if ($installer -and $installer.Name -match '^dotnet-runtime-(.+)-win-x64\.exe$') {
        "| .NET Runtime in ``$zipName`` | $($Matches[1]), Microsoft's installer, signed by Microsoft |"
    }
}

$notes = Get-Content $template -Raw

$verification = @"

## Verifying what you downloaded

| | |
|---|---|
| Signed by | ``$($signer.Subject)`` |
| Certificate issued by | ``$($signer.Issuer)`` |
| Certificate thumbprint (SHA-1) | ``$($signer.Thumbprint)`` |
| Timestamped by | ``$(($signature.TimeStamperCertificate.Subject -split ',')[0] -replace '^CN=', '')`` |
$($rows -join [Environment]::NewLine)

To check the download itself, with the name of the zip you downloaded:

``````powershell
Get-FileHash .\WinZ3805A-<version>-x64.zip -Algorithm SHA256
``````

The signature is made through **Azure Artifact Signing**, whose certificates last
three days and are renewed daily, so the thumbprint differs from release to
release while the *Signed by* name stays the same. The signature is
**timestamped**, which is what keeps this release installable long after that
certificate has expired: a signature countersigned while the certificate was
valid stays valid.

"@

if (-not $OutputPath) {
    $OutputPath = Join-Path ([IO.Path]::GetTempPath()) 'winz3805a-release-notes.md'
}

Set-Content -Path $OutputPath -Value ($notes + $verification) -Encoding utf8

Write-Host "Release notes for $(($ZipPath | ForEach-Object { [IO.Path]::GetFileName($_) }) -join ', ')"
Write-Host "  signed by   $($signer.Subject)"
Write-Host "  thumbprint  $($signer.Thumbprint)"
Write-Host "  expires     $($signer.NotAfter.ToString('yyyy-MM-dd'))"
$rows | ForEach-Object { Write-Host "  $_" }

$OutputPath
