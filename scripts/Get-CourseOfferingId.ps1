param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Path
)

$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$coreProject = Join-Path $repositoryRoot "CoursePlanner.Core\CoursePlanner.Core.csproj"
$coreAssembly = Join-Path $repositoryRoot "CoursePlanner.Core\bin\Debug\net10.0\CoursePlanner.Core.dll"

if (-not (Test-Path -LiteralPath $coreAssembly)) {
    & dotnet build $coreProject --configuration Debug --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Could not build CoursePlanner.Core."
    }
}

$null = [System.Reflection.Assembly]::LoadFrom($coreAssembly)
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$json = Get-Content -Raw -LiteralPath $resolvedPath
[CoursePlanner.Core.JsonInputGuard]::Validate($json)

$document = [System.Text.Json.JsonDocument]::Parse($json)
try {
    $courses = $document.RootElement.GetProperty("courses")
    if ($courses.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
        throw "The root 'courses' property must be an array."
    }

    foreach ($courseElement in $courses.EnumerateArray()) {
        $course = [System.Text.Json.JsonSerializer]::Deserialize(
            $courseElement.GetRawText(),
            [CoursePlanner.Core.CourseOffering],
            [CoursePlanner.Core.JsonDefaults]::Options)

        if ($null -eq $course) {
            throw "A course object could not be parsed."
        }

        $expected = [CoursePlanner.Core.CourseIdentityService]::GenerateOfferingId($course)
        [pscustomobject]@{
            CourseName = $course.CourseName
            CurrentOfferingId = $course.OfferingId
            ExpectedOfferingId = $expected
            Matches = [string]::Equals(
                $course.OfferingId,
                $expected,
                [System.StringComparison]::Ordinal)
        }
    }
}
finally {
    $document.Dispose()
}
