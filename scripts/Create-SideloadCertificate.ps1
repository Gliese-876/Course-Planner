param(
    [string]$Subject = "CN=AppPublisher",
    [string]$PfxPath = "$PSScriptRoot\..\CoursePlanner\Packaging\CoursePlanner-Sideload.pfx",
    [string]$CerPath,
    [securestring]$PfxPassword
)

$ErrorActionPreference = "Stop"

if (-not $PfxPassword -and $env:COURSE_PLANNER_PFX_PASSWORD) {
    $PfxPassword = ConvertTo-SecureString -String $env:COURSE_PLANNER_PFX_PASSWORD -Force -AsPlainText
}

if (-not $PfxPassword) {
    throw "Provide -PfxPassword or set COURSE_PLANNER_PFX_PASSWORD."
}

if ([string]::IsNullOrWhiteSpace($CerPath)) {
    $CerPath = [System.IO.Path]::ChangeExtension($PfxPath, ".cer")
}

$pfxDirectory = Split-Path -Parent $PfxPath
$cerDirectory = Split-Path -Parent $CerPath
if (-not [string]::IsNullOrWhiteSpace($pfxDirectory)) {
    New-Item -ItemType Directory -Path $pfxDirectory -Force | Out-Null
}
if (-not [string]::IsNullOrWhiteSpace($cerDirectory)) {
    New-Item -ItemType Directory -Path $cerDirectory -Force | Out-Null
}

$cert = $null
try {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyUsage DigitalSignature `
        -FriendlyName "Course Planner sideload signing" `
        -NotAfter (Get-Date).AddYears(3)

    Export-Certificate -Cert $cert -FilePath $CerPath | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $PfxPassword | Out-Null
}
finally {
    if ($cert) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
    }
}

Write-Host "Created $CerPath"
Write-Host "Created $PfxPath"
