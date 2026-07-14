[CmdletBinding()]
param(
    [string]$MsixPath,
    [string]$CertificatePath,
    [switch]$AcceptPublisherCertificate,
    [switch]$VerifyOnly,
    [switch]$InstallCertificateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-InstallerFile {
    param(
        [string]$Path,
        [Parameter(Mandatory = $true)][string]$Filter,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $resolved = (Resolve-Path -LiteralPath $Path).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "$Description path is not a file: '$resolved'."
        }

        return $resolved
    }

    $matches = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter $Filter -File)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description matching '$Filter' beside this script; found $($matches.Count)."
    }

    $matches[0].FullName
}

function Test-CertificateAlreadyTrusted {
    param([Parameter(Mandatory = $true)][string]$Thumbprint)

    $existing = Get-ChildItem -LiteralPath "Cert:\LocalMachine\TrustedPeople" |
        Where-Object Thumbprint -eq $Thumbprint |
        Select-Object -First 1
    $null -ne $existing
}

function Test-IsAdministrator {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
        $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally {
        $identity.Dispose()
    }
}

function Add-PublisherCertificateToLocalMachine {
    param(
        [Parameter(Mandatory = $true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if (-not (Test-IsAdministrator)) {
        throw "Installing the publisher certificate for this device requires administrator approval."
    }

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        "TrustedPeople",
        [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($Certificate)
    }
    finally {
        $store.Close()
    }
}

function Invoke-ElevatedCertificateInstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$PublisherCertificatePath
    )

    $windowsPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    $arguments = @(
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"{0}"' -f $InstallerPath),
        "-MsixPath", ('"{0}"' -f $PackagePath),
        "-CertificatePath", ('"{0}"' -f $PublisherCertificatePath),
        "-InstallCertificateOnly"
    )

    try {
        $process = Start-Process `
            -FilePath $windowsPowerShell `
            -ArgumentList $arguments `
            -Verb RunAs `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
    }
    catch {
        throw "Administrator approval was cancelled; the publisher certificate was not installed."
    }

    if ($process.ExitCode -ne 0) {
        throw "The elevated publisher-certificate step failed with exit code $($process.ExitCode)."
    }
}

$resolvedMsixPath = Resolve-InstallerFile -Path $MsixPath -Filter "*.msix" -Description "MSIX package"
$resolvedCertificatePath = Resolve-InstallerFile `
    -Path $CertificatePath `
    -Filter "*.cer" `
    -Description "publisher certificate"

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $resolvedCertificatePath)

try {
    if ($VerifyOnly -and $InstallCertificateOnly) {
        throw "VerifyOnly and InstallCertificateOnly cannot be used together."
    }

    if ($certificate.HasPrivateKey) {
        throw "The release certificate must not contain a private key."
    }

    $now = Get-Date
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) {
        throw "The publisher certificate is not currently valid. Valid range: $($certificate.NotBefore) to $($certificate.NotAfter)."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $resolvedMsixPath
    if (-not $signature.SignerCertificate -or $signature.SignatureType -ne "Authenticode") {
        throw "The MSIX package does not contain an Authenticode signature."
    }

    if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The MSIX signer does not match the bundled publisher certificate. Installation stopped."
    }

    $signatureStatus = $signature.Status.ToString()
    if ($signatureStatus -notin @("Valid", "NotTrusted", "UnknownError")) {
        throw "The MSIX signature failed validation with status '$signatureStatus': $($signature.StatusMessage)"
    }

    Write-Host "Course Planner package signature verified." -ForegroundColor Green
    Write-Host "Publisher:  $($certificate.Subject)"
    Write-Host "Thumbprint: $($certificate.Thumbprint)"
    Write-Host "Valid until: $($certificate.NotAfter.ToString('yyyy-MM-dd'))"

    if ($VerifyOnly) {
        Write-Host "Verification completed; no trust or installation state was changed." -ForegroundColor Green
        return
    }

    $alreadyTrusted = Test-CertificateAlreadyTrusted -Thumbprint $certificate.Thumbprint
    if ($InstallCertificateOnly) {
        if (-not $alreadyTrusted) {
            Add-PublisherCertificateToLocalMachine -Certificate $certificate
        }

        if (-not (Test-CertificateAlreadyTrusted -Thumbprint $certificate.Thumbprint)) {
            throw "The publisher certificate was not found in Local Computer -> Trusted People after installation."
        }

        Write-Host "Publisher certificate trusted for this device." -ForegroundColor Green
        return
    }

    if (-not $alreadyTrusted) {
        if (-not $AcceptPublisherCertificate) {
            $answer = Read-Host "Trust this publisher for all users on this device (requires UAC), then install Course Planner for the current Windows user? [y/N]"
            if ($answer -notmatch "^(?i:y|yes)$") {
                throw "Installation cancelled."
            }
        }

        if (Test-IsAdministrator) {
            Add-PublisherCertificateToLocalMachine -Certificate $certificate
        }
        else {
            Invoke-ElevatedCertificateInstall `
                -InstallerPath $PSCommandPath `
                -PackagePath $resolvedMsixPath `
                -PublisherCertificatePath $resolvedCertificatePath
        }

        if (-not (Test-CertificateAlreadyTrusted -Thumbprint $certificate.Thumbprint)) {
            throw "The publisher certificate is not trusted by the local computer. Installation stopped."
        }
    }

    Add-AppxPackage -Path $resolvedMsixPath -ErrorAction Stop
    Write-Host "Course Planner was installed successfully. Open it from the Start menu." -ForegroundColor Green
}
catch {
    throw
}
finally {
    $certificate.Dispose()
}
