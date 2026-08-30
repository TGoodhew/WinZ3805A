<#
.SYNOPSIS
    Puts the signing certificate where .github\workflows\release.yml can find it.

.DESCRIPTION
    The release workflow signs the MSIX with the same self-signed certificate a
    local build uses, which means the runner needs the PFX and its password. A
    repository secret is the only place either belongs.

    WHAT THIS KEY IS AND IS NOT. It is not a certificate-authority key: nothing
    on a stranger's machine trusts it until they choose to. But a person who has
    installed a release HAS chosen to - the certificate sits in their Trusted
    People store - and anyone holding this PFX can sign a package that installs
    under exactly that trust, silently, as an upgrade. So it is a secret in the
    way that matters, and it is why build\devcert.pfx is gitignored and this
    script prints commands rather than writing the key anywhere.

    Run with -Apply to set the secrets with the gh CLI; without it, the commands
    are printed for you to run or to paste into a password manager first.

.PARAMETER CertificatePath
    The PFX to publish. Defaults to build\devcert.pfx.

.PARAMETER CertificatePassword
    Its password. Defaults to the one 'winapp cert generate' uses.

.PARAMETER Apply
    Actually set the secrets, rather than printing what would set them.

.NOTES
    ROTATING. Delete build\devcert.pfx, re-run New-SideloadPackage.ps1 (which
    regenerates it from the manifest so the subject cannot drift), then run this
    again. Understand what rotation costs before doing it: a release signed with
    a NEW certificate is not an upgrade to one signed with the old, because the
    package family name is derived from the publisher and the certificate must
    match it. Everyone re-installs and re-trusts. Rotate for a compromised key,
    not for tidiness.
#>

[CmdletBinding()]
param(
    [string]$CertificatePath,

    [string]$CertificatePassword = 'password',

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (-not $CertificatePath) {
    $CertificatePath = Join-Path $PSScriptRoot 'devcert.pfx'
}

if (-not (Test-Path $CertificatePath)) {
    throw ("No certificate at $CertificatePath. Run build\New-SideloadPackage.ps1 once - it " +
           'generates one from the manifest so its subject cannot drift from the declared publisher.')
}

# Read back before publishing it, because a PFX whose subject does not match the
# manifest signs nothing installable, and finding that out on a tag is finding it
# out in the worst place.
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
    -ArgumentList $CertificatePath, $CertificatePassword

[xml]$manifestXml = Get-Content (Join-Path $repo 'src\WinZ3805A\Package.appxmanifest')
$publisher = $manifestXml.Package.Identity.Publisher

if ($certificate.Subject -ne $publisher) {
    throw ("The certificate is for '$($certificate.Subject)' but the manifest declares '$publisher'. " +
           "Delete $CertificatePath and re-run New-SideloadPackage.ps1 to regenerate it.")
}

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($CertificatePath))

Write-Host ''
Write-Host "  Certificate  $($certificate.Subject)"
Write-Host "  Thumbprint   $($certificate.Thumbprint)"
Write-Host "  Expires      $($certificate.NotAfter.ToString('d MMMM yyyy'))"
Write-Host "  PFX          $CertificatePath  ($([math]::Round((Get-Item $CertificatePath).Length / 1KB)) KB)"
Write-Host ''

if ($certificate.NotAfter -lt (Get-Date).AddDays(60)) {
    Write-Warning ("This certificate expires in $([int]($certificate.NotAfter - (Get-Date)).TotalDays) days. " +
                   'Releases already published stay installable because their signatures are timestamped, ' +
                   'but a NEW release signed after that date will not install. Read the rotation note in ' +
                   'this script before replacing it - it is not a free operation.')
}

if ($Apply) {
    Write-Host 'Setting repository secrets...'

    $base64 | gh secret set SIGNING_PFX_BASE64
    if ($LASTEXITCODE -ne 0) { throw 'Could not set SIGNING_PFX_BASE64.' }

    $CertificatePassword | gh secret set SIGNING_PFX_PASSWORD
    if ($LASTEXITCODE -ne 0) { throw 'Could not set SIGNING_PFX_PASSWORD.' }

    Write-Host ''
    Write-Host '  Both secrets set. Check them with: gh secret list'
    Write-Host ''
}
else {
    $temp = Join-Path ([IO.Path]::GetTempPath()) 'winz3805a-pfx.b64'
    Set-Content -Path $temp -Value $base64 -Encoding ascii

    Write-Host 'Run these, or re-run this script with -Apply:'
    Write-Host ''
    Write-Host "  gh secret set SIGNING_PFX_BASE64 < `"$temp`""
    Write-Host "  gh secret set SIGNING_PFX_PASSWORD --body '<the PFX password>'"
    Write-Host ''
    Write-Host "  Delete $temp afterwards - it is the private key in text form."
    Write-Host ''
}
