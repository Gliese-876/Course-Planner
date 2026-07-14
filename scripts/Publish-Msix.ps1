param(
    [string]$Project = "$PSScriptRoot\..\CoursePlanner\CoursePlanner.csproj",
    [string]$Solution = "$PSScriptRoot\..\CoursePlannerWorkspace.slnx",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [ValidatePattern("^\d+\.\d+\.\d+\.\d+$")]
    [string]$PackageVersion = "1.0.0.0",
    [string]$Publisher = "CN=AppPublisher",
    [string]$PfxPath = "$PSScriptRoot\..\CoursePlanner\Packaging\CoursePlanner-Sideload.pfx",
    [securestring]$PfxPassword,
    [string]$OutputRoot = "$PSScriptRoot\..\artifacts\msix"
)

$ErrorActionPreference = "Stop"

function Convert-SecureStringToPlainText {
    param([Parameter(Mandatory = $true)][securestring]$Value)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Resolve-RuntimeIdentifier {
    param([Parameter(Mandatory = $true)][string]$Platform)

    switch ($Platform) {
        "x64" { "win-x64" }
        default { throw "Unsupported platform: $Platform" }
    }
}

function Resolve-UnresolvedFileSystemPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParameterName
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$ParameterName must be a non-empty file-system path."
    }

    $provider = $null
    $drive = $null
    try {
        $providerPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            $Path,
            [ref]$provider,
            [ref]$drive)
    }
    catch {
        throw "$ParameterName path '$Path' could not be resolved: $($_.Exception.Message)"
    }

    if (-not $provider -or $provider.Name -ne "FileSystem") {
        $providerName = if ($provider) { $provider.Name } else { "unknown" }
        throw "$ParameterName must use the FileSystem provider; '$Path' resolved through '$providerName'."
    }

    $providerPath
}

function New-PackageManifestOverride {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Publisher
    )

    [xml]$manifest = Get-Content -LiteralPath $SourcePath
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Save($DestinationPath)
}

function Read-PackageIdentity {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $manifestEntry = $archive.GetEntry("AppxManifest.xml")
        if (-not $manifestEntry) {
            throw "Package '$PackagePath' does not contain AppxManifest.xml."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $identity = $manifest.Package.Identity
        if (-not $identity.Name -or -not $identity.Publisher -or -not $identity.Version -or -not $identity.ProcessorArchitecture) {
            throw "Package '$PackagePath' has an incomplete identity."
        }

        [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [string]$identity.Version
            ProcessorArchitecture = [string]$identity.ProcessorArchitecture
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackageSignature {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$ExpectedCertificate
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if (-not $signature.SignerCertificate -or $signature.SignatureType -ne "Authenticode") {
        throw "Package '$PackagePath' does not have an Authenticode signature."
    }
    if ($signature.SignerCertificate.Thumbprint -ne $ExpectedCertificate.Thumbprint) {
        throw "Package signer '$($signature.SignerCertificate.Thumbprint)' does not match PFX '$($ExpectedCertificate.Thumbprint)'."
    }

    $signatureStatus = $signature.Status.ToString()
    if ($signatureStatus -eq "Valid") {
        return
    }
    if ($signatureStatus -notin @("UnknownError", "NotTrusted")) {
        throw "Package signature validation failed with status '$signatureStatus': $($signature.StatusMessage)"
    }

    # A newly generated sideload certificate is intentionally not installed in
    # a trust store yet. Validate its chain in memory so packaging stays
    # non-interactive and never changes the user's certificate trust settings.
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $null = $chain.ChainPolicy.CustomTrustStore.Add($ExpectedCertificate)
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.DisableCertificateDownloads = $true
        $null = $chain.ChainPolicy.ApplicationPolicy.Add(
            [System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3"))

        if (-not $chain.Build($signature.SignerCertificate)) {
            $statuses = ($chain.ChainStatus | ForEach-Object Status) -join ", "
            throw "Package signer failed the isolated code-signing chain validation: $statuses"
        }
    }
    finally {
        $chain.Dispose()
    }
}

if (-not $PfxPassword -and $env:COURSE_PLANNER_PFX_PASSWORD) {
    $PfxPassword = ConvertTo-SecureString -String $env:COURSE_PLANNER_PFX_PASSWORD -Force -AsPlainText
}

if (-not $PfxPassword) {
    throw "Provide -PfxPassword or set COURSE_PLANNER_PFX_PASSWORD."
}

$OutputRoot = Resolve-UnresolvedFileSystemPath -Path $OutputRoot -ParameterName "OutputRoot"
$PfxPath = Resolve-UnresolvedFileSystemPath -Path $PfxPath -ParameterName "PfxPath"
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$solutionPath = (Resolve-Path -LiteralPath $Solution).Path
$projectDir = Split-Path -Parent $projectPath
$manifestPath = Join-Path $projectDir "Package.appxmanifest"
[xml]$sourceManifest = Get-Content -LiteralPath $manifestPath
$packageName = [string]$sourceManifest.Package.Identity.Name
if ([string]::IsNullOrWhiteSpace($packageName)) {
    throw "Package manifest identity must include Name."
}
$rid = Resolve-RuntimeIdentifier -Platform $Platform
$stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
$nonce = [Guid]::NewGuid().ToString("N").Substring(0, 8)
$packageOutput = Join-Path $OutputRoot "$PackageVersion-$Platform-$stamp-$nonce"
New-Item -ItemType Directory -Path $packageOutput | Out-Null
$temporaryStateDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "CoursePlanner.Publish.$([Guid]::NewGuid().ToString('N'))"
$isolatedArtifactsPath = Join-Path $temporaryStateDirectory "artifacts"
$transientLockDirectory = Join-Path $temporaryStateDirectory "locks"
New-Item -ItemType Directory -Path $transientLockDirectory -Force | Out-Null
$manifestOverridePath = Join-Path $temporaryStateDirectory "Package.appxmanifest"

$plainPassword = $null
$cert = $null
try {
    New-PackageManifestOverride `
        -SourcePath $manifestPath `
        -DestinationPath $manifestOverridePath `
        -Version $PackageVersion `
        -Publisher $Publisher

    & "$PSScriptRoot\Prepare-TransientPackageLocks.ps1" `
        -Solution $solutionPath `
        -DestinationDirectory $transientLockDirectory | Out-Null

    $restoreArgs = @(
        "restore",
        $solutionPath,
        "--locked-mode",
        "-p:ArtifactsPath=$isolatedArtifactsPath",
        "-p:CoursePlannerTransientLockDirectory=$transientLockDirectory"
    )
    Invoke-Native -FilePath "dotnet" -Arguments $restoreArgs

    if (-not (Test-Path -LiteralPath $PfxPath)) {
        & "$PSScriptRoot\Create-SideloadCertificate.ps1" -Subject $Publisher -PfxPath $PfxPath -PfxPassword $PfxPassword
    }

    $plainPassword = Convert-SecureStringToPlainText -Value $PfxPassword
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $PfxPath,
        $plainPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    if ($cert.Subject -ne $Publisher) {
        throw "PFX subject '$($cert.Subject)' does not match manifest publisher '$Publisher'."
    }
    if (-not $cert.HasPrivateKey) {
        throw "PFX does not contain a private key."
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $vsInstallRoot = if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    }
    else {
        $null
    }

    $mspdbcmf = $null
    if ($vsInstallRoot) {
        $mspdbcmf = Get-ChildItem -LiteralPath (Join-Path $vsInstallRoot "VC\Tools\MSVC") -Filter mspdbcmf.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object FullName -like "*\bin\Hostx64\x64\mspdbcmf.exe" |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName

        if (-not $mspdbcmf) {
            $mspdbcmf = Get-ChildItem -LiteralPath (Join-Path $vsInstallRoot "MSBuild\Microsoft\VisualStudio") -Filter MsPdbCmf.exe -Recurse -ErrorAction SilentlyContinue |
                Where-Object FullName -like "*\AppxPackage\x64\MsPdbCmf.exe" |
                Sort-Object FullName -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }

    $publishArgs = @(
        "publish",
        $projectPath,
        "-c",
        "Release",
        "-r",
        $rid,
        "--self-contained",
        "true",
        "--no-restore",
        "-p:Platform=$Platform",
        "-p:RuntimeIdentifier=$rid",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:ArtifactsPath=$isolatedArtifactsPath",
        "-p:CoursePlannerPackageManifest=$manifestOverridePath",
        "-p:CoursePlannerTransientLockDirectory=$transientLockDirectory",
        "-p:GenerateAppxPackageOnBuild=true",
        "-p:AppxBundle=Never",
        "-p:PublishReadyToRun=false",
        "-p:AppxPackageDir=$packageOutput\",
        "-p:AppxPackageSigningEnabled=false",
        "-p:AppxPackageVersion=$PackageVersion"
    )

    if ($mspdbcmf) {
        $publishArgs += "-p:MsPdbCmfExeFullpath=$mspdbcmf"
    }

    Invoke-Native -FilePath "dotnet" -Arguments $publishArgs

    $matchingPackages = @(
        Get-ChildItem -LiteralPath $packageOutput -Filter "*.msix" -Recurse -File | ForEach-Object {
            $identity = Read-PackageIdentity -PackagePath $_.FullName
            if (
                $identity.Name -eq $packageName -and
                $identity.Publisher -eq $Publisher -and
                $identity.Version -eq $PackageVersion -and
                $identity.ProcessorArchitecture -eq $Platform
            ) {
                $_
            }
        }
    )

    if ($matchingPackages.Count -ne 1) {
        throw "Expected exactly one $Platform MSIX with identity '$packageName', publisher '$Publisher', and version '$PackageVersion' in '$packageOutput'; found $($matchingPackages.Count)."
    }
    $package = $matchingPackages[0]

    Invoke-Native -FilePath "winapp" -Arguments @("sign", $package.FullName, $PfxPath, "--password", $plainPassword)
    Assert-PackageSignature -PackagePath $package.FullName -ExpectedCertificate $cert
    Write-Host "MSIX package: $($package.FullName)"
}
finally {
    if ($cert) {
        $cert.Dispose()
    }
    $plainPassword = $null
    if (Test-Path -LiteralPath $temporaryStateDirectory) {
        Remove-Item -LiteralPath $temporaryStateDirectory -Recurse -Force
    }
}
