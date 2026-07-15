param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^v\d+\.\d+\.\d+$")]
    [string]$Tag,
    [ValidateRange(0, 65535)]
    [int]$PackageRevision = 0,
    [string]$Repository = "Gliese-876/Course-Planner"
)

$ErrorActionPreference = "Stop"

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

$repositoryRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryBase ("CoursePlanner.StageRelease." + [Guid]::NewGuid().ToString("N"))
$pfxPath = Join-Path $temporaryRoot "Ephemeral-Staging.pfx"
$cerPath = Join-Path $temporaryRoot "Ephemeral-Staging.cer"
$password = $null

try {
    Push-Location $repositoryRoot
    try {
        $status = @(& git status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect the Git working tree." }
        if ($status.Count -ne 0) {
            throw "Release staging requires a clean Git working tree."
        }

        $head = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve HEAD." }
        $tagCommit = (& git rev-list -n 1 $Tag).Trim()
        if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $head) {
            throw "Tag '$Tag' must point to the current HEAD before staging a release."
        }

        $remoteTagLines = @(& git ls-remote origin "refs/tags/$Tag" "refs/tags/$Tag^{}")
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect the remote release tag." }
        $remoteCommit = $remoteTagLines |
            Where-Object { $_ -match "refs/tags/$([regex]::Escape($Tag))\^\{\}$" } |
            ForEach-Object { ($_ -split "\s+")[0] } |
            Select-Object -First 1
        if (-not $remoteCommit) {
            $remoteCommit = $remoteTagLines |
                Where-Object { $_ -match "refs/tags/$([regex]::Escape($Tag))$" } |
                ForEach-Object { ($_ -split "\s+")[0] } |
                Select-Object -First 1
        }
        if ($remoteCommit -ne $head) {
            throw "Remote tag '$Tag' must point to the current HEAD before staging a release."
        }

        $version = $Tag.Substring(1)
        $packageVersion = "$version.$PackageRevision"

        & "$PSScriptRoot\Test-Ci.ps1"

        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        $passwordBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $password = [Convert]::ToBase64String($passwordBytes)
        $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force

        & "$PSScriptRoot\Create-SideloadCertificate.ps1" `
            -Subject "CN=AppPublisher" `
            -PfxPath $pfxPath `
            -CerPath $cerPath `
            -PfxPassword $securePassword

        $msixRoot = Join-Path $temporaryRoot "msix"
        & "$PSScriptRoot\Publish-Msix.ps1" `
            -PackageVersion $packageVersion `
            -PfxPath $pfxPath `
            -PfxPassword $securePassword `
            -OutputRoot $msixRoot

        $packages = @(Get-ChildItem -LiteralPath $msixRoot -Filter "*.msix" -Recurse -File)
        if ($packages.Count -ne 1) {
            throw "Expected exactly one locally built MSIX; found $($packages.Count)."
        }

        $stagedAssetName = "CoursePlanner-$Tag-x64.staged.msix"
        $stagedPath = Join-Path $temporaryRoot $stagedAssetName
        Copy-Item -LiteralPath $packages[0].FullName -Destination $stagedPath
        $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash.ToLowerInvariant()

        $draftNotesPath = Join-Path $temporaryRoot "DRAFT.md"
        @(
            "This draft contains a locally built staging package for $Tag.",
            "",
            "The signing workflow verifies its SHA-256, replaces the temporary signature with the stable publisher certificate from GitHub Secrets, removes the staging asset, and publishes the final release."
        ) | Set-Content -LiteralPath $draftNotesPath -Encoding utf8NoBOM

        $releaseJson = & gh release view $Tag --repo $Repository --json isDraft 2>$null
        $releaseExists = $LASTEXITCODE -eq 0
        if ($releaseExists) {
            $release = $releaseJson | ConvertFrom-Json
            if (-not $release.isDraft) {
                throw "Release '$Tag' is already published; refusing to replace it."
            }

            Invoke-Native gh @(
                "release", "edit", $Tag,
                "--repo", $Repository,
                "--title", "Course Planner $Tag",
                "--notes-file", $draftNotesPath)
            Invoke-Native gh @(
                "release", "upload", $Tag, $stagedPath,
                "--repo", $Repository,
                "--clobber")
        }
        else {
            Invoke-Native gh @(
                "release", "create", $Tag, $stagedPath,
                "--repo", $Repository,
                "--verify-tag",
                "--draft",
                "--title", "Course Planner $Tag",
                "--notes-file", $draftNotesPath)
        }

        Invoke-Native gh @(
            "workflow", "run", "release.yml",
            "--repo", $Repository,
            "--ref", "main",
            "-f", "release_tag=$Tag",
            "-f", "package_revision=$PackageRevision",
            "-f", "staged_sha256=$stagedHash")

        Write-Host "Locally built package staged for signing: $Tag"
        Write-Host "Staged SHA-256: $stagedHash"
    }
    finally {
        Pop-Location
    }
}
finally {
    $password = $null
    if (Test-Path -LiteralPath $pfxPath) {
        Remove-Item -LiteralPath $pfxPath -Force
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
        $normalizedBase = $temporaryBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $rootItem = Get-Item -LiteralPath $resolvedTemporaryRoot -Force
        if (-not $resolvedTemporaryRoot.StartsWith($normalizedBase, [StringComparison]::OrdinalIgnoreCase) -or
            -not $rootItem.PSIsContainer -or
            $rootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to recursively remove an unsafe release staging path: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
