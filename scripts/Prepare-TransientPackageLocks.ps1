param(
    [Parameter(Mandatory = $true)]
    [string]$Solution,
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory
)

$ErrorActionPreference = "Stop"

$solutionPath = (Resolve-Path -LiteralPath $Solution).Path
$solutionDirectory = Split-Path -Parent $solutionPath
$destinationPath = (Resolve-Path -LiteralPath $DestinationDirectory).Path
[xml]$solutionXml = Get-Content -LiteralPath $solutionPath -Raw
$projectNodes = @($solutionXml.SelectNodes("//*[local-name()='Project']"))
if ($projectNodes.Count -eq 0) {
    throw "Solution '$solutionPath' does not contain any projects."
}

foreach ($projectNode in $projectNodes) {
    $relativeProjectPath = [string]$projectNode.Path
    $projectPath = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($solutionDirectory, $relativeProjectPath))
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Solution project '$projectPath' does not exist."
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
    $sourceLockPath = Join-Path (Split-Path -Parent $projectPath) "packages.lock.json"
    if (-not (Test-Path -LiteralPath $sourceLockPath -PathType Leaf)) {
        throw "Checked-in NuGet lock file is missing for project '$projectPath': '$sourceLockPath'."
    }
    $destinationLockPath = Join-Path $destinationPath "$projectName.packages.lock.json"
    if (Test-Path -LiteralPath $destinationLockPath) {
        throw "Transient NuGet lock destination is not empty: '$destinationLockPath'."
    }
    Copy-Item -LiteralPath $sourceLockPath -Destination $destinationLockPath
    $sourceHash = (Get-FileHash -LiteralPath $sourceLockPath -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash -LiteralPath $destinationLockPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "Transient NuGet lock does not exactly match its checked-in source: '$destinationLockPath'."
    }
    Write-Output $destinationLockPath
}
