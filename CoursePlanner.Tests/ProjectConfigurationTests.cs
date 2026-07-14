namespace CoursePlanner.Tests;

public sealed class ProjectConfigurationTests
{
    [Fact]
    public void ApplicationLayerDoesNotDeclareAChildNamespaceThatShadowsWinUiApplication()
    {
        var applicationProject = File.ReadAllText(ProjectFilePath(
            "CoursePlanner.Application",
            "CoursePlanner.Application.csproj"));
        Assert.Contains("<RootNamespace>CoursePlanner</RootNamespace>", applicationProject, StringComparison.Ordinal);

        var productionProjects = new[]
        {
            "CoursePlanner",
            "CoursePlanner.Application",
            "CoursePlanner.Core",
            "CoursePlanner.Exchange",
            "CoursePlanner.Export",
            "CoursePlanner.Persistence"
        };
        var offenders = productionProjects
            .SelectMany(project => Directory.EnumerateFiles(
                ProjectFilePath(project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path),
                @"(?m)^\s*namespace\s+CoursePlanner\.Application(?:\s*[;{]|\.)"))
            .Select(path => Path.GetRelativePath(RepositoryPaths.Root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AppTargetsOnlyX64ForTheSupportedSingleInstanceLifecyclePath()
    {
        var project = File.ReadAllText(ProjectFilePath("CoursePlanner", "CoursePlanner.csproj"));

        Assert.Contains("<Platforms>x64</Platforms>", project);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", project);
        Assert.Contains("<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>", project);
        Assert.DoesNotContain("ProcessArchitecture", project);
        Assert.Contains(">win-x64</RuntimeIdentifier>", project);
        Assert.DoesNotContain("ARM64", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("win-arm64", project, StringComparison.OrdinalIgnoreCase);

        var publishScript = File.ReadAllText(ProjectFilePath("scripts", "Publish-Msix.ps1"));
        Assert.Contains("[ValidateSet(\"x64\")]", publishScript);
        Assert.DoesNotContain("\"win-x86\"", publishScript);
        Assert.DoesNotContain("ARM64", publishScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("win-arm64", publishScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"-p:AppxBundle=Never\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"-p:PublishReadyToRun=false\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"--self-contained\",", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"true\",", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"-p:WindowsAppSDKSelfContained=true\"", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishReadyToRun=true", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("R2R\\", publishScript, StringComparison.Ordinal);
        Assert.Contains("-p:CoursePlannerPackageManifest=$manifestOverridePath", publishScript);
        Assert.Contains("-p:CoursePlannerTransientLockDirectory=$transientLockDirectory", publishScript);
        Assert.Contains("Read-PackageIdentity", publishScript);
        Assert.Contains("Assert-PackageSignature", publishScript);
        Assert.DoesNotContain("Set-PackageManifestIdentity", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAllBytes($manifestPath", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Create-SideloadCertificate.ps1 failed with exit code",
            publishScript,
            StringComparison.Ordinal);

        var certificateScript = File.ReadAllText(ProjectFilePath("scripts", "Create-SideloadCertificate.ps1"));
        Assert.Contains("finally {", certificateScript, StringComparison.Ordinal);
        Assert.Contains("Cert:\\CurrentUser\\My\\$($cert.Thumbprint)", certificateScript, StringComparison.Ordinal);

        Assert.Contains(
            "<AppxManifest Include=\"$(CoursePlannerPackageManifest)\" />",
            project,
            StringComparison.Ordinal);

        var buildProperties = File.ReadAllText(ProjectFilePath("Directory.Build.props"));
        Assert.Contains("$(CoursePlannerTransientLockDirectory)", buildProperties, StringComparison.Ordinal);
        Assert.Contains("$(MSBuildProjectName).packages.lock.json", buildProperties, StringComparison.Ordinal);

        foreach (var scriptName in new[]
                 {
                     "Test-PlanTabCloseMotion.ps1",
                     "Test-PlanTabContinuousCloseAnchor.ps1",
                     "Test-TitleBarTabDoubleClick.ps1"
                 })
        {
            var uiScript = File.ReadAllText(ProjectFilePath("scripts", scriptName));
            Assert.Contains("[int]$AppPid = 0", uiScript, StringComparison.Ordinal);
            Assert.Contains("Get-Process -Id $Id", uiScript, StringComparison.Ordinal);
            Assert.Contains("Multiple visible '$Name' processes were found", uiScript, StringComparison.Ordinal);
            Assert.Contains("pass -AppPid", uiScript, StringComparison.Ordinal);
        }

        var motionScript = File.ReadAllText(ProjectFilePath("scripts", "Test-PlanTabCloseMotion.ps1"));
        Assert.Contains("System.Drawing.Common", motionScript, StringComparison.Ordinal);
        Assert.Contains("[System.Drawing.Bitmap].Assembly.Location", motionScript, StringComparison.Ordinal);
        Assert.Contains("GetReferencedAssemblies()", motionScript, StringComparison.Ordinal);

        var ciScript = File.ReadAllText(ProjectFilePath("scripts", "Test-Ci.ps1"));
        Assert.Contains("\"-r\", \"win-x64\"", ciScript);
        Assert.Contains("\"-p:Platform=x64\"", ciScript);
        Assert.Contains("\"-p:PublishReadyToRun=false\"", ciScript, StringComparison.Ordinal);
        Assert.Contains("\"--no-restore\"", ciScript);
        Assert.Contains("\"-p:TreatWarningsAsErrors=true\"", ciScript);
        Assert.Contains("\"-p:ArtifactsPath=$isolatedArtifactsPath\"", ciScript);
        Assert.Contains("course-planner-ci-", ciScript, StringComparison.Ordinal);
        Assert.Contains("[string]$PublishOutput = \"\"", ciScript, StringComparison.Ordinal);
        Assert.Contains("PublishOutput must be new or empty", ciScript, StringComparison.Ordinal);
        Assert.Contains("$PathLabel must stay within its trusted base", ciScript, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeDirectoryTree", ciScript, StringComparison.Ordinal);
        Assert.Contains("CI temporary tree", ciScript, StringComparison.Ordinal);
        Assert.Contains("contains a non-directory or reparse point", ciScript, StringComparison.Ordinal);
        Assert.Contains("--results-directory", ciScript, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-timeout", ciScript, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-dump-type", ciScript, StringComparison.Ordinal);
        Assert.Contains("CI test diagnostics preserved at", ciScript, StringComparison.Ordinal);
        Assert.Contains("Cleanup also failed after the primary CI failure", ciScript, StringComparison.Ordinal);
        Assert.Contains("\"-o\", $publishOutputPath", ciScript, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", ciScript, StringComparison.Ordinal);
        Assert.DoesNotContain("ARM64", ciScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("win-arm64", ciScript, StringComparison.OrdinalIgnoreCase);

        var lockFile = File.ReadAllText(ProjectFilePath("CoursePlanner", "packages.lock.json"));
        Assert.Contains("net10.0-windows10.0.26100/win-x64", lockFile);
        Assert.DoesNotContain("win-arm64", lockFile, StringComparison.OrdinalIgnoreCase);
        foreach (var portableProject in new[]
                 {
                     "CoursePlanner.Application",
                     "CoursePlanner.Core",
                     "CoursePlanner.Exchange",
                     "CoursePlanner.Export",
                     "CoursePlanner.Persistence",
                     "CoursePlanner.Tests"
                 })
        {
            var portableLock = File.ReadAllText(ProjectFilePath(portableProject, "packages.lock.json"));
            Assert.DoesNotContain("win-x64", portableLock, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("win-arm64", portableLock, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("AnyCPU", "AnyCPU", "win-x64")]
    [InlineData("x64", "AnyCPU", "win-x64")]
    [InlineData("x64", "x64", "win-arm64")]
    [InlineData("ARM64", "ARM64", "win-arm64")]
    public async Task AppBuildRejectsUnsupportedEntryArchitectures(
        string platform,
        string platformTarget,
        string runtimeIdentifier)
    {
        var result = await RunAppPrepareForBuild(platform, platformTarget, runtimeIdentifier);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Course Planner supports only x64 with the win-x64 runtime identifier.", result.Output);
    }

    [Fact]
    public async Task AppBuildAcceptsTheSupportedX64EntryArchitecture()
    {
        var result = await RunAppPrepareForBuild("x64", "x64", "win-x64");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task AppBuildAcceptsTheDefaultCommandWhenItsEffectiveTargetIsX64()
    {
        var result = await RunAppPrepareForBuild();

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task TransientLockPreparationCopiesEverySolutionLockExactly()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"CoursePlanner.TransientLocks.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var firstProjectDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "First"));
            var secondProjectDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "Second"));
            File.WriteAllText(Path.Combine(firstProjectDirectory.FullName, "First.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(secondProjectDirectory.FullName, "Second.csproj"), "<Project />");
            var firstLock = "{\"version\":1,\"dependencies\":{\"net10.0\":{}}}"u8.ToArray();
            var secondLock = "{\"version\":1,\"dependencies\":{\"net10.0\":{\"Example\":{}}}}"u8.ToArray();
            File.WriteAllBytes(Path.Combine(firstProjectDirectory.FullName, "packages.lock.json"), firstLock);
            File.WriteAllBytes(Path.Combine(secondProjectDirectory.FullName, "packages.lock.json"), secondLock);
            var solutionPath = Path.Combine(fixtureRoot, "Fixture.slnx");
            File.WriteAllText(
                solutionPath,
                "<Solution><Project Path=\"First/First.csproj\" /><Project Path=\"Second/Second.csproj\" /></Solution>");
            var destinationDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "transient-locks"));

            var result = await RunPowerShellScript(
                ProjectFilePath("scripts", "Prepare-TransientPackageLocks.ps1"),
                "-Solution",
                solutionPath,
                "-DestinationDirectory",
                destinationDirectory.FullName);

            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Equal(
                new[] { "First.packages.lock.json", "Second.packages.lock.json" },
                Directory.EnumerateFiles(destinationDirectory.FullName)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal)
                    .ToArray());
            Assert.Equal(firstLock, File.ReadAllBytes(Path.Combine(destinationDirectory.FullName, "First.packages.lock.json")));
            Assert.Equal(secondLock, File.ReadAllBytes(Path.Combine(destinationDirectory.FullName, "Second.packages.lock.json")));
            Assert.Equal(firstLock, File.ReadAllBytes(Path.Combine(firstProjectDirectory.FullName, "packages.lock.json")));
            Assert.Equal(secondLock, File.ReadAllBytes(Path.Combine(secondProjectDirectory.FullName, "packages.lock.json")));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TransientLockPreparationFailsClosedWhenAProjectLockIsMissing()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"CoursePlanner.TransientLocks.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var projectDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "MissingLock"));
            File.WriteAllText(Path.Combine(projectDirectory.FullName, "MissingLock.csproj"), "<Project />");
            var solutionPath = Path.Combine(fixtureRoot, "Fixture.slnx");
            File.WriteAllText(
                solutionPath,
                "<Solution><Project Path=\"MissingLock/MissingLock.csproj\" /></Solution>");
            var destinationDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "transient-locks"));

            var result = await RunPowerShellScript(
                ProjectFilePath("scripts", "Prepare-TransientPackageLocks.ps1"),
                "-Solution",
                solutionPath,
                "-DestinationDirectory",
                destinationDirectory.FullName);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Checked-in NuGet lock file is missing", result.Output, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory.FullName));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TransientLockPreparationFailsClosedInsteadOfOverwritingAMismatchedDestination()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"CoursePlanner.TransientLocks.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var projectDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "Collision"));
            File.WriteAllText(Path.Combine(projectDirectory.FullName, "Collision.csproj"), "<Project />");
            var checkedInLock = "{\"version\":1,\"dependencies\":{}}"u8.ToArray();
            File.WriteAllBytes(Path.Combine(projectDirectory.FullName, "packages.lock.json"), checkedInLock);
            var solutionPath = Path.Combine(fixtureRoot, "Fixture.slnx");
            File.WriteAllText(
                solutionPath,
                "<Solution><Project Path=\"Collision/Collision.csproj\" /></Solution>");
            var destinationDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "transient-locks"));
            var destinationPath = Path.Combine(destinationDirectory.FullName, "Collision.packages.lock.json");
            var mismatchedLock = "mismatched"u8.ToArray();
            File.WriteAllBytes(destinationPath, mismatchedLock);

            var result = await RunPowerShellScript(
                ProjectFilePath("scripts", "Prepare-TransientPackageLocks.ps1"),
                "-Solution",
                solutionPath,
                "-DestinationDirectory",
                destinationDirectory.FullName);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Transient NuGet lock destination is not empty", result.Output, StringComparison.Ordinal);
            Assert.Equal(mismatchedLock, File.ReadAllBytes(destinationPath));
            Assert.Equal(checkedInLock, File.ReadAllBytes(Path.Combine(projectDirectory.FullName, "packages.lock.json")));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void MsixPublishStagesTheSolutionLocksAndRestoresInLockedMode()
    {
        var publishScript = File.ReadAllText(ProjectFilePath("scripts", "Publish-Msix.ps1"));

        Assert.Contains("Prepare-TransientPackageLocks.ps1", publishScript, StringComparison.Ordinal);
        Assert.Contains("-Solution $solutionPath", publishScript, StringComparison.Ordinal);
        Assert.Contains("-DestinationDirectory $transientLockDirectory", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"--locked-mode\"", publishScript, StringComparison.Ordinal);
        Assert.True(
            publishScript.IndexOf("Prepare-TransientPackageLocks.ps1", StringComparison.Ordinal) <
            publishScript.IndexOf("Invoke-Native -FilePath \"dotnet\"", StringComparison.Ordinal),
            "Checked-in locks must be staged before dotnet publish can restore the graph.");
    }

    [Fact]
    public void MsixLockedSolutionRestoreUsesTheSolutionsDefaultPlatformConfiguration()
    {
        var publishScript = File.ReadAllText(ProjectFilePath("scripts", "Publish-Msix.ps1"));
        var restoreStart = publishScript.IndexOf("$restoreArgs = @(", StringComparison.Ordinal);
        var restoreEnd = publishScript.IndexOf(
            "Invoke-Native -FilePath \"dotnet\" -Arguments $restoreArgs",
            restoreStart,
            StringComparison.Ordinal);

        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart, "The locked restore command was not found.");
        var restoreBlock = publishScript[restoreStart..restoreEnd];
        Assert.Contains("$solutionPath", restoreBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("$projectPath", restoreBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("$rid", restoreBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Platform=", restoreBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixPublishUsesTheSameTemporaryArtifactsPathForRestoreAndPublish()
    {
        var publishScript = File.ReadAllText(ProjectFilePath("scripts", "Publish-Msix.ps1"));
        Assert.Contains(
            "$isolatedArtifactsPath = Join-Path $temporaryStateDirectory \"artifacts\"",
            publishScript,
            StringComparison.Ordinal);

        var restoreStart = publishScript.IndexOf("$restoreArgs = @(", StringComparison.Ordinal);
        var restoreEnd = publishScript.IndexOf(
            "Invoke-Native -FilePath \"dotnet\" -Arguments $restoreArgs",
            restoreStart,
            StringComparison.Ordinal);
        var publishStart = publishScript.IndexOf("$publishArgs = @(", StringComparison.Ordinal);
        var publishEnd = publishScript.IndexOf(
            "Invoke-Native -FilePath \"dotnet\" -Arguments $publishArgs",
            publishStart,
            StringComparison.Ordinal);

        Assert.True(restoreStart >= 0 && restoreEnd > restoreStart, "The locked restore command was not found.");
        Assert.True(publishStart >= 0 && publishEnd > publishStart, "The publish command was not found.");

        const string artifactsArgument = "\"-p:ArtifactsPath=$isolatedArtifactsPath\"";
        Assert.Contains(artifactsArgument, publishScript[restoreStart..restoreEnd], StringComparison.Ordinal);
        Assert.Contains(artifactsArgument, publishScript[publishStart..publishEnd], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MsixPublishArtifactsPathMovesEveryMutableBuildPathOutsideTheProjectTree()
    {
        var isolatedArtifactsPath = Path.Combine(
            Path.GetTempPath(),
            $"CoursePlanner.MsixOutputIsolation.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedArtifactsPath);
        try
        {
            var restore = await RunDotNet(
                TimeSpan.FromMinutes(2),
                "restore",
                ProjectFilePath("CoursePlannerWorkspace.slnx"),
                "--locked-mode",
                $"-p:ArtifactsPath={isolatedArtifactsPath}");
            Assert.True(restore.ExitCode == 0, restore.Output);

            var propertyNames = new[]
            {
                "BaseOutputPath",
                "BaseIntermediateOutputPath",
                "OutputPath",
                "IntermediateOutputPath",
                "TargetDir",
                "PublishDir",
                "WinAppLooseLayoutPath",
                "FinalAppxManifestName",
                "AppxLayoutDir"
            };
            var query = await RunDotNet(
                TimeSpan.FromMinutes(1),
                "msbuild",
                ProjectFilePath("CoursePlanner", "CoursePlanner.csproj"),
                "-nologo",
                "-p:Configuration=Release",
                "-p:Platform=x64",
                "-p:RuntimeIdentifier=win-x64",
                $"-p:ArtifactsPath={isolatedArtifactsPath}",
                $"-getProperty:{string.Join(',', propertyNames)}");
            Assert.True(query.ExitCode == 0, query.Output);

            using var document = System.Text.Json.JsonDocument.Parse(query.Output);
            var properties = document.RootElement.GetProperty("Properties");
            foreach (var propertyName in propertyNames)
            {
                var value = properties.GetProperty(propertyName).GetString();
                Assert.False(string.IsNullOrWhiteSpace(value), $"MSBuild property '{propertyName}' was empty.");
                Assert.True(
                    IsPathWithin(value!, isolatedArtifactsPath),
                    $"MSBuild property '{propertyName}' escaped the isolated artifacts root: '{value}'.");
            }

            var projectDirectory = ProjectFilePath("CoursePlanner");
            Assert.False(
                IsPathWithin(properties.GetProperty("TargetDir").GetString()!, projectDirectory),
                "The package build TargetDir must not overlap the project tree or its registered AppX layout.");
        }
        finally
        {
            Directory.Delete(isolatedArtifactsPath, recursive: true);
        }
    }

    [Fact]
    public async Task MsixPublishResolvesRelativeNonexistentOutputPathsBeforeUsingThem()
    {
        var publishScriptPath = ProjectFilePath("scripts", "Publish-Msix.ps1");
        var publishScript = File.ReadAllText(publishScriptPath);
        const string outputRootNormalization =
            "$OutputRoot = Resolve-UnresolvedFileSystemPath -Path $OutputRoot -ParameterName \"OutputRoot\"";
        const string pfxPathNormalization =
            "$PfxPath = Resolve-UnresolvedFileSystemPath -Path $PfxPath -ParameterName \"PfxPath\"";
        var outputRootNormalizationIndex = publishScript.IndexOf(
            outputRootNormalization,
            StringComparison.Ordinal);
        var pfxPathNormalizationIndex = publishScript.IndexOf(
            pfxPathNormalization,
            StringComparison.Ordinal);
        var packageOutputCreation = publishScript.IndexOf(
            "$packageOutput = Join-Path $OutputRoot",
            StringComparison.Ordinal);

        Assert.Contains("GetUnresolvedProviderPathFromPSPath", publishScript, StringComparison.Ordinal);
        Assert.Contains("$provider.Name -ne \"FileSystem\"", publishScript, StringComparison.Ordinal);
        Assert.True(
            outputRootNormalizationIndex >= 0,
            "Publish-Msix.ps1 does not normalize OutputRoot before using it.");
        Assert.True(
            pfxPathNormalizationIndex >= 0,
            "Publish-Msix.ps1 does not normalize PfxPath before using it.");
        Assert.True(
            packageOutputCreation >= 0,
            "The package output construction was not found in Publish-Msix.ps1.");
        Assert.True(
            outputRootNormalizationIndex < packageOutputCreation,
            "OutputRoot must be normalized before packageOutput is constructed.");
        Assert.True(
            pfxPathNormalizationIndex < packageOutputCreation,
            "PfxPath must be normalized before any packaging work starts.");

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"CoursePlanner.MsixPathResolution.Tests.{Guid.NewGuid():N}");
        var callerDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "caller")).FullName;
        var probeScriptPath = Path.Combine(fixtureRoot, "Probe-PublishPathResolution.ps1");
        File.WriteAllText(
            probeScriptPath,
            """
            param(
                [Parameter(Mandatory = $true)][string]$PublishScript,
                [Parameter(Mandatory = $true)][string]$WorkingDirectory,
                [Parameter(Mandatory = $true)][string]$RelativeOutputRoot,
                [Parameter(Mandatory = $true)][string]$RelativePfxPath
            )

            Set-Location -LiteralPath $WorkingDirectory
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $PublishScript,
                [ref]$tokens,
                [ref]$errors)
            if ($errors.Count -ne 0) {
                throw "Publish script did not parse: $($errors -join '; ')"
            }

            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq "Resolve-UnresolvedFileSystemPath"
            }, $true)
            if (-not $function) {
                throw "Resolve-UnresolvedFileSystemPath was not found."
            }

            $body = $function.Body.GetScriptBlock()
            [pscustomobject]@{
                OutputRoot = & $body -Path $RelativeOutputRoot -ParameterName "OutputRoot"
                PfxPath = & $body -Path $RelativePfxPath -ParameterName "PfxPath"
            } | ConvertTo-Json -Compress
            """);

        try
        {
            const string relativeOutputRoot = @".\artifacts\audit-msix";
            const string relativePfxPath = @".\certificates\audit-signing.pfx";
            var probe = await RunPowerShellScript(
                probeScriptPath,
                "-PublishScript",
                publishScriptPath,
                "-WorkingDirectory",
                callerDirectory,
                "-RelativeOutputRoot",
                relativeOutputRoot,
                "-RelativePfxPath",
                relativePfxPath);
            Assert.True(probe.ExitCode == 0, probe.Output);

            using var document = System.Text.Json.JsonDocument.Parse(probe.Output);
            var outputRoot = document.RootElement.GetProperty("OutputRoot").GetString();
            var pfxPath = document.RootElement.GetProperty("PfxPath").GetString();
            var expectedOutputRoot = Path.Combine(callerDirectory, "artifacts", "audit-msix");
            var expectedPfxPath = Path.Combine(callerDirectory, "certificates", "audit-signing.pfx");
            Assert.Equal(Path.GetFullPath(expectedOutputRoot), outputRoot, ignoreCase: true);
            Assert.Equal(Path.GetFullPath(expectedPfxPath), pfxPath, ignoreCase: true);
            Assert.False(Directory.Exists(expectedOutputRoot));
            Assert.False(File.Exists(expectedPfxPath));
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void ReleaseWorkflowKeepsPrivateSigningMaterialInRepositorySecrets()
    {
        var workflow = File.ReadAllText(ProjectFilePath(".github", "workflows", "release.yml"));

        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("microsoft/setup-WinAppCli@v0.1", workflow, StringComparison.Ordinal);
        Assert.Contains("secrets.COURSE_PLANNER_PFX_BASE64", workflow, StringComparison.Ordinal);
        Assert.Contains("secrets.COURSE_PLANNER_PFX_PASSWORD", workflow, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_STAGED_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", workflow, StringComparison.Ordinal);
        Assert.Contains("X509ContentType]::Cert", workflow, StringComparison.Ordinal);
        Assert.Contains("Remove signing material", workflow, StringComparison.Ordinal);
        Assert.Contains("Install-CoursePlanner.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Install-CoursePlanner.cmd", workflow, StringComparison.Ordinal);
        Assert.Contains("release delete-asset", workflow, StringComparison.Ordinal);
        Assert.Contains("--draft=false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/setup-dotnet", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-Ci.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Publish-Msix.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Create-SideloadCertificate.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", workflow, StringComparison.Ordinal);

        var stagingScript = File.ReadAllText(ProjectFilePath("scripts", "Stage-Release.ps1"));
        Assert.Contains("Test-Ci.ps1", stagingScript, StringComparison.Ordinal);
        Assert.Contains("Publish-Msix.ps1", stagingScript, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator", stagingScript, StringComparison.Ordinal);
        Assert.Contains("\"workflow\", \"run\", \"release.yml\"", stagingScript, StringComparison.Ordinal);
        Assert.Contains("\"--draft\"", stagingScript, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $pfxPath -Force", stagingScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseInstallerVerifiesSignerBeforeTrustingThePublisher()
    {
        var installer = File.ReadAllText(ProjectFilePath("scripts", "Install-CoursePlanner.ps1"));
        var signerCheck = installer.IndexOf(
            "$signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint",
            StringComparison.Ordinal);
        var trustDecision = installer.IndexOf(
            "$alreadyTrusted = Test-CertificateAlreadyTrusted",
            signerCheck,
            StringComparison.Ordinal);
        var install = installer.IndexOf("Add-AppxPackage", StringComparison.Ordinal);

        Assert.True(signerCheck >= 0, "The installer does not compare the MSIX signer to the bundled certificate.");
        Assert.True(trustDecision > signerCheck, "The publisher must not be trusted before the signer is verified.");
        Assert.True(install > trustDecision, "The package must not be installed before publisher trust is established.");
        Assert.Contains("StoreLocation]::LocalMachine", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreLocation]::CurrentUser", installer, StringComparison.Ordinal);
        Assert.Contains("-Verb RunAs", installer, StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", installer, StringComparison.Ordinal);
        Assert.Contains("[switch]$VerifyOnly", installer, StringComparison.Ordinal);
        Assert.Contains("[switch]$InstallCertificateOnly", installer, StringComparison.Ordinal);
    }

    private static async Task<MsBuildResult> RunAppPrepareForBuild(
        string platform,
        string platformTarget,
        string runtimeIdentifier) =>
        await RunAppPrepareForBuild(
        [
            $"-p:Platform={platform}",
            $"-p:PlatformTarget={platformTarget}",
            $"-p:RuntimeIdentifier={runtimeIdentifier}"
        ]);

    private static async Task<MsBuildResult> RunAppPrepareForBuild(params string[] architectureProperties)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        DisableMsBuildNodeReuse(startInfo);
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(ProjectFilePath("CoursePlanner", "CoursePlanner.csproj"));
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-t:PrepareForBuild");
        foreach (var property in architectureProperties)
            startInfo.ArgumentList.Add(property);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet msbuild.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new MsBuildResult(
            process.ExitCode,
            (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private static async Task<MsBuildResult> RunPowerShellScript(string scriptPath, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new MsBuildResult(
            process.ExitCode,
            (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private static async Task<MsBuildResult> RunDotNet(TimeSpan timeout, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        DisableMsBuildNodeReuse(startInfo);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new MsBuildResult(
            process.ExitCode,
            (await standardOutput) + Environment.NewLine + (await standardError));
    }

    private static void DisableMsBuildNodeReuse(System.Diagnostics.ProcessStartInfo startInfo)
    {
        // Reusable MSBuild nodes inherit redirected stdout/stderr handles from
        // the short-lived dotnet CLI process. If they outlive that process,
        // ReadToEndAsync never observes EOF and the test host hangs forever.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
    }

    private static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var parent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MsBuildResult(int ExitCode, string Output);

    private static string ProjectFilePath(params string[] parts) =>
        RepositoryPaths.FromRoot(parts);
}
