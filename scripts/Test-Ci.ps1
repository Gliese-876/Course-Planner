param(
    [string]$Solution = "$PSScriptRoot\..\CoursePlannerWorkspace.slnx",
    [string]$AppProject = "$PSScriptRoot\..\CoursePlanner\CoursePlanner.csproj",
    [string]$Manifest = "$PSScriptRoot\..\CoursePlanner\Package.appxmanifest",
    [string]$PublishOutput = ""
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

function Test-PowerShellSyntax {
    param([Parameter(Mandatory = $true)][string]$Path)

    $errors = $null
    [System.Management.Automation.PSParser]::Tokenize((Get-Content -Raw -LiteralPath $Path), [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        throw "PowerShell syntax errors in ${Path}: $($errors | Out-String)"
    }
}

function Test-Manifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    [xml]$manifestXml = Get-Content -LiteralPath $Path
    $identity = $manifestXml.Package.Identity
    if (-not $identity.Name -or -not $identity.Publisher -or -not $identity.Version) {
        throw "Package identity must include Name, Publisher, and Version."
    }
    if ($identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Package identity version must be four-part numeric."
    }
}

function Assert-OrdinaryDirectoryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$PathLabel
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullBase = [IO.Path]::GetFullPath($BasePath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals($fullPath, $fullBase, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($fullBase + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$PathLabel must stay within its trusted base '$fullBase': $fullPath"
    }

    $cursor = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (-not $item.PSIsContainer -or
                $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                throw "$PathLabel path components must be ordinary directories: $($item.FullName)"
            }
        }

        if ([string]::Equals($cursor, $fullBase, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if (-not $parent) {
            throw "Could not reach $PathLabel trusted base '$fullBase' from '$fullPath'."
        }
        $cursor = $parent.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
}

function Remove-SafeDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$PathLabel
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-OrdinaryDirectoryPath -Path $Path -BasePath $BasePath -PathLabel $PathLabel
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $fullBase = [IO.Path]::GetFullPath($BasePath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([string]::Equals($fullPath, $fullBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$PathLabel must be a child of its cleanup base, not the base itself: $fullPath"
    }
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals($fullPath, $resolvedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$PathLabel resolves to an unexpected location: $resolvedPath"
    }

    $item = Get-Item -LiteralPath $resolvedPath -Force
    $reparseEntry = Get-ChildItem -LiteralPath $resolvedPath -Force -Recurse |
        Where-Object { $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) } |
        Select-Object -First 1
    if (-not $item.PSIsContainer -or
        $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        $reparseEntry) {
        $unsafePath = if ($reparseEntry) { $reparseEntry.FullName } else { $resolvedPath }
        throw "$PathLabel contains a non-directory or reparse point: $unsafePath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    if (Test-Path -LiteralPath $resolvedPath) {
        throw "$PathLabel survived cleanup: $resolvedPath"
    }
}

$solutionPath = (Resolve-Path -LiteralPath $Solution).Path
$appProjectPath = (Resolve-Path -LiteralPath $AppProject).Path
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$repositoryRoot = (Resolve-Path -LiteralPath "$PSScriptRoot\..").Path
$repositoryArtifactsRoot = Join-Path $repositoryRoot "artifacts"
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryStateDirectory = Join-Path $temporaryRoot ("course-planner-ci-" + [Guid]::NewGuid().ToString("N"))
$isolatedArtifactsPath = Join-Path $temporaryStateDirectory "artifacts"
$runId = [Guid]::NewGuid().ToString("N")
$testResultsParent = Join-Path $repositoryArtifactsRoot "ci-test-results"
$testResultsPath = Join-Path $testResultsParent $runId
$testSucceeded = $false
$primaryFailure = $null
$publishOutputIsTemporary = [string]::IsNullOrWhiteSpace($PublishOutput)
if (-not $publishOutputIsTemporary -and -not (Test-Path -LiteralPath $repositoryArtifactsRoot)) {
    $repositoryRootItem = Get-Item -LiteralPath $repositoryRoot -Force
    if (-not $repositoryRootItem.PSIsContainer -or
        $repositoryRootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Repository root must be an ordinary directory: $repositoryRoot"
    }
    New-Item -ItemType Directory -Path $repositoryArtifactsRoot | Out-Null
}
$publishOutputBase = if ($publishOutputIsTemporary) {
    $temporaryStateDirectory
}
else {
    $repositoryArtifactsRoot
}
$publishOutputPath = if ($publishOutputIsTemporary) {
    Join-Path $temporaryStateDirectory "publish"
}
else {
    [IO.Path]::GetFullPath($PublishOutput)
}

try {
    Assert-OrdinaryDirectoryPath -Path $temporaryStateDirectory -BasePath $temporaryRoot -PathLabel "CI temporary tree"
    New-Item -ItemType Directory -Path $isolatedArtifactsPath -Force | Out-Null
    Assert-OrdinaryDirectoryPath -Path $temporaryStateDirectory -BasePath $temporaryRoot -PathLabel "CI temporary tree"

    Assert-OrdinaryDirectoryPath -Path $publishOutputPath -BasePath $publishOutputBase -PathLabel "PublishOutput"
    if (Test-Path -LiteralPath $publishOutputPath) {
        $publishOutputItem = Get-Item -LiteralPath $publishOutputPath -Force
        if (-not $publishOutputItem.PSIsContainer -or
            $publishOutputItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "PublishOutput must be an ordinary directory: $publishOutputPath"
        }
        if (@(Get-ChildItem -LiteralPath $publishOutputPath -Force).Count -ne 0) {
            throw "PublishOutput must be new or empty so stale files cannot survive validation: $publishOutputPath"
        }
    }
    else {
        New-Item -ItemType Directory -Path $publishOutputPath | Out-Null
    }
    Assert-OrdinaryDirectoryPath -Path $publishOutputPath -BasePath $publishOutputBase -PathLabel "PublishOutput"

    Assert-OrdinaryDirectoryPath -Path $testResultsPath -BasePath $repositoryRoot -PathLabel "CI test-results directory"
    if (Test-Path -LiteralPath $testResultsPath) {
        throw "CI test-results directory already exists despite a unique run id: $testResultsPath"
    }
    New-Item -ItemType Directory -Path $testResultsPath -Force | Out-Null
    Assert-OrdinaryDirectoryPath -Path $testResultsPath -BasePath $repositoryRoot -PathLabel "CI test-results directory"

    Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1" | ForEach-Object {
        Test-PowerShellSyntax -Path $_.FullName
    }

    Test-Manifest -Path $manifestPath
    Invoke-Native dotnet @(
        "restore",
        $solutionPath,
        "--locked-mode",
        "-p:ArtifactsPath=$isolatedArtifactsPath"
    )
    Invoke-Native dotnet @(
        "build",
        $solutionPath,
        "-c", "Debug",
        "--no-restore",
        "-p:ArtifactsPath=$isolatedArtifactsPath",
        "-p:TreatWarningsAsErrors=true"
    )
    Invoke-Native dotnet @(
        "test",
        $solutionPath,
        "-c", "Debug",
        "--no-build",
        "--results-directory", $testResultsPath,
        "--blame-hang",
        "--blame-hang-timeout", "2m",
        "--blame-hang-dump-type", "none",
        "--diag", (Join-Path $testResultsPath "vstest.log"),
        "-p:ArtifactsPath=$isolatedArtifactsPath"
    )
    $testSucceeded = $true
    Invoke-Native dotnet @(
        "publish",
        $appProjectPath,
        "-c", "Release",
        "-r", "win-x64",
        "--no-self-contained",
        "--no-restore",
        "-o", $publishOutputPath,
        "-p:ArtifactsPath=$isolatedArtifactsPath",
        "-p:Platform=x64",
        "-p:PublishReadyToRun=false",
        "-p:TreatWarningsAsErrors=true",
        "-p:GenerateAppxPackageOnBuild=false",
        "-p:AppxPackageSigningEnabled=false"
    )
}
catch {
    $primaryFailure = $_
    throw
}
finally {
    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    try {
        $testResultsAreEmpty = -not (Test-Path -LiteralPath $testResultsPath) -or
            @(Get-ChildItem -LiteralPath $testResultsPath -Force).Count -eq 0
        if ($testSucceeded -or $testResultsAreEmpty) {
            Remove-SafeDirectoryTree `
                -Path $testResultsPath `
                -BasePath $repositoryRoot `
                -PathLabel "CI test-results directory"
        }
        else {
            Write-Warning "CI test diagnostics preserved at '$testResultsPath'."
        }
    }
    catch {
        $cleanupErrors.Add($_.Exception)
    }

    try {
        Remove-SafeDirectoryTree `
            -Path $temporaryStateDirectory `
            -BasePath $temporaryRoot `
            -PathLabel "CI temporary tree"
    }
    catch {
        $cleanupErrors.Add($_.Exception)
    }

    if ($cleanupErrors.Count -gt 0) {
        if ($null -ne $primaryFailure) {
            foreach ($cleanupError in $cleanupErrors) {
                Write-Warning "Cleanup also failed after the primary CI failure: $($cleanupError.Message)"
            }
        }
        else {
            throw [AggregateException]::new("CI cleanup failed.", $cleanupErrors)
        }
    }
}
