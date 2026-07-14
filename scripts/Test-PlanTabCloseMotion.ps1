param(
    [string]$ProcessName = "CoursePlanner",
    [int]$AppPid = 0,
    [int]$SampleDurationMilliseconds = 450,
    [int]$SecondCloseDelayMilliseconds = 160,
    [ValidateRange(3, 30)][int]$TemporaryTabCount = 3,
    [ValidateRange(2, 10)][int]$CloseClickCount = 2,
    [switch]$AssertRapidInputStability
)

$ErrorActionPreference = "Stop"

if ($CloseClickCount -gt $TemporaryTabCount) {
    throw "CloseClickCount cannot exceed TemporaryTabCount."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing.Common

if (-not ("CoursePlannerTabMotionProbe" -as [type])) {
    $drawingReferences = @(
        [System.Drawing.Bitmap].Assembly.Location
        [System.Drawing.Color].Assembly.Location
        [System.Drawing.Bitmap].Assembly.GetReferencedAssemblies() |
            Where-Object Name -like "System.Private.Windows.*" |
            ForEach-Object { [System.Reflection.Assembly]::Load($_).Location }
    ) | Select-Object -Unique
    Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CoursePlannerTabMotionProbe
{
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static ulong CaptureHash(int x, int y, int width, int height)
    {
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            var rectangle = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var length = Math.Abs(data.Stride) * height;
                var pixels = new byte[length];
                Marshal.Copy(data.Scan0, pixels, 0, length);
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    hash ^= pixels[index];
                    hash *= prime;
                    hash ^= pixels[index + 1];
                    hash *= prime;
                    hash ^= pixels[index + 2];
                    hash *= prime;
                }
                return hash;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
'@
}

$mouseLeftDown = 0x0002
$mouseLeftUp = 0x0004
$mouseMiddleDown = 0x0020
$mouseMiddleUp = 0x0040

function Resolve-UiTargetProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [int]$Id = 0
    )

    if ($Id -gt 0) {
        $candidate = Get-Process -Id $Id -ErrorAction Stop
        if ($candidate.ProcessName -ne $Name) {
            throw "Process $Id is '$($candidate.ProcessName)', not '$Name'."
        }
        if ($candidate.MainWindowHandle -eq [IntPtr]::Zero) {
            throw "Process $Id does not have a visible top-level window."
        }
        return $candidate
    }

    $candidates = @(
        Get-Process -Name $Name -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                if ($_.MainWindowHandle -ne [IntPtr]::Zero) { $_ }
            }
            catch {
                # The process exited while candidates were being enumerated.
            }
        }
    )
    if ($candidates.Count -eq 0) {
        throw "$Name does not have a visible top-level window."
    }
    if ($candidates.Count -gt 1) {
        throw "Multiple visible '$Name' processes were found ($($candidates.Id -join ', ')); pass -AppPid."
    }
    $candidates[0]
}

$process = Resolve-UiTargetProcess -Name $ProcessName -Id $AppPid
$windowHandle = $process.MainWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) {
    throw "$ProcessName does not have a top-level window."
}

function Get-Root {
    [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
}

function Find-ByAutomationId {
    param([Parameter(Mandatory = $true)][string]$AutomationId)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    (Get-Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-PlanTabs {
    $root = Get-Root
    $all = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $tabs = for ($index = 0; $index -lt $all.Count; $index++) {
        $element = $all.Item($index)
        if ($element.Current.AutomationId -match '^ShellPlanTab_.+$') {
            [pscustomobject]@{
                Position = [int]$element.GetCurrentPropertyValue(
                    [System.Windows.Automation.AutomationElementIdentifiers]::PositionInSetProperty)
                Element = $element
            }
        }
    }
    @($tabs | Sort-Object Position | ForEach-Object Element)
}

function Invoke-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $pattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-ForTabCount {
    param(
        [Parameter(Mandatory = $true)][int]$Expected,
        [int]$TimeoutMilliseconds = 3000
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        try {
            if ((Get-PlanTabs).Count -eq $Expected) {
                return
            }
        }
        catch {
            # WinUI can invalidate an automation peer during incremental
            # tab-tree updates; retry against the next stable tree.
        }
        Start-Sleep -Milliseconds 20
    }
    throw "Timed out waiting for $Expected plan tabs."
}

function Click-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $scrollItemPattern = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.ScrollItemPattern]::Pattern,
        [ref]$scrollItemPattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$scrollItemPattern).ScrollIntoView()
        Start-Sleep -Milliseconds 100
    }
    $bounds = $Element.Current.BoundingRectangle
    if ([double]::IsNaN($bounds.X) -or [double]::IsInfinity($bounds.X) -or
        [double]::IsNaN($bounds.Y) -or [double]::IsInfinity($bounds.Y)) {
        throw "The UI Automation element is not currently on screen."
    }
    $x = [int]($bounds.Left + [Math]::Min($bounds.Width / 2, 48))
    $y = [int]($bounds.Top + $bounds.Height / 2)
    [CoursePlannerTabMotionProbe]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 60
    [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function MiddleClick-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $bounds = $Element.Current.BoundingRectangle
    if ([double]::IsNaN($bounds.X) -or [double]::IsInfinity($bounds.X) -or
        [double]::IsNaN($bounds.Y) -or [double]::IsInfinity($bounds.Y)) {
        throw "The UI Automation element is not currently on screen."
    }
    $x = [int]($bounds.Left + [Math]::Min($bounds.Width / 2, 48))
    $y = [int]($bounds.Top + $bounds.Height / 2)
    [CoursePlannerTabMotionProbe]::SetCursorPos($x, $y) | Out-Null
    [CoursePlannerTabMotionProbe]::mouse_event($mouseMiddleDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerTabMotionProbe]::mouse_event($mouseMiddleUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Find-CloseButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $Tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-TabText {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $Tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-TabByName {
    param([Parameter(Mandatory = $true)][string]$Name)

    (Get-PlanTabs) | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
}

[CoursePlannerTabMotionProbe]::SetForegroundWindow($windowHandle) | Out-Null
Start-Sleep -Milliseconds 250

$originalTabs = Get-PlanTabs
if ($originalTabs.Count -lt 1) {
    throw "The motion probe requires at least one existing plan tab."
}
$originalTabNames = @($originalTabs | ForEach-Object { $_.Current.Name })
$originalSelectedTab = $null
foreach ($tab in $originalTabs) {
    if ($null -ne (Find-CloseButton -Tab $tab)) {
        $originalSelectedTab = $tab
        break
    }
}

$temporaryTabs = @()
$samples = @()
$failure = $null

try {
    for ($created = 0; $created -lt $TemporaryTabCount; $created++) {
        $beforeCount = (Get-PlanTabs).Count
        Invoke-Element -Element (Find-ByAutomationId -AutomationId "ShellAddPlanTabButton")
        Wait-ForTabCount -Expected ($beforeCount + 1)
        Start-Sleep -Milliseconds 120
        $createdTab = (Get-PlanTabs)[-1]
        $temporaryTabs += ,[pscustomobject]@{
            Element = $createdTab
            Name = $createdTab.Current.Name
        }
    }

    Start-Sleep -Milliseconds 350
    $targetIndex = $originalTabs.Count
    $tabs = Get-PlanTabs
    $target = $tabs[$targetIndex]
    Click-Element -Element $target
    Start-Sleep -Milliseconds 350
    $tabs = Get-PlanTabs
    $target = $tabs[$targetIndex]
    $secondTarget = $tabs[$targetIndex + 1]
    $trackedNeighborTab = $tabs[$targetIndex + 2]
    $targetName = $target.Current.Name
    $neighborName = $trackedNeighborTab.Current.Name
    $targetBounds = $target.Current.BoundingRectangle
    $neighborBounds = $trackedNeighborTab.Current.BoundingRectangle
    $captureLeft = [int][Math]::Max(0, $targetBounds.Left - 8)
    $captureTop = [int][Math]::Max(0, $targetBounds.Top - 2)
    $captureRight = [int][Math]::Ceiling($neighborBounds.Right + 40)
    $captureBottom = [int][Math]::Ceiling($targetBounds.Bottom + 2)
    $captureWidth = [Math]::Max(1, $captureRight - $captureLeft)
    $captureHeight = [Math]::Max(1, $captureBottom - $captureTop)
    $firstCloseButton = Find-CloseButton -Tab $target
    if ($null -eq $firstCloseButton) {
        throw "The selected probe tab did not expose its close button."
    }
    $firstCloseBounds = $firstCloseButton.Current.BoundingRectangle
    $fixedCloseX = [int]($firstCloseBounds.Left + $firstCloseBounds.Width / 2)
    $fixedCloseY = [int]($firstCloseBounds.Top + $firstCloseBounds.Height / 2)
    [CoursePlannerTabMotionProbe]::SetCursorPos($fixedCloseX, $fixedCloseY) | Out-Null
    [CoursePlannerTabMotionProbe]::mouse_event(0x0001, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
    $closeClicksSent = 1
    while ($stopwatch.ElapsedMilliseconds -lt $SampleDurationMilliseconds) {
        if ($closeClicksSent -lt $CloseClickCount -and
            $stopwatch.ElapsedMilliseconds -ge ($SecondCloseDelayMilliseconds * $closeClicksSent)) {
            [CoursePlannerTabMotionProbe]::SetCursorPos($fixedCloseX, $fixedCloseY) | Out-Null
            [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
            [CoursePlannerTabMotionProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
            $closeClicksSent++
        }
        try {
            $samples += [pscustomobject]@{
                Milliseconds = $stopwatch.ElapsedMilliseconds
                Hash = [CoursePlannerTabMotionProbe]::CaptureHash(
                    $captureLeft,
                    $captureTop,
                    $captureWidth,
                    $captureHeight)
            }
        }
        catch {
            # A display topology change can invalidate a single screen capture.
        }
        Start-Sleep -Milliseconds 1
    }

    $visualFrames = @()
    $lastHash = $null
    foreach ($sample in $samples) {
        if ($null -eq $lastHash -or $sample.Hash -ne $lastHash) {
            $visualFrames += $sample
            $lastHash = $sample.Hash
        }
    }

    $firstMovement = $visualFrames | Where-Object { $_.Milliseconds -gt 0 } | Select-Object -First 1
    $motionFrames = @($visualFrames | Where-Object { $_.Milliseconds -gt 0 })
    Start-Sleep -Milliseconds 100
    $expectedTabCount = $originalTabs.Count + $TemporaryTabCount - $CloseClickCount
    $actualTabCount = (Get-PlanTabs).Count

    [pscustomobject]@{
        Target = $targetName
        Neighbor = $neighborName
        SecondCloseDelayMilliseconds = $SecondCloseDelayMilliseconds
        CloseClickCount = $CloseClickCount
        FirstMovementMilliseconds = $firstMovement.Milliseconds
        CapturedFrames = $samples.Count
        DistinctRenderedFrames = $motionFrames.Count
        ExpectedTabCount = $expectedTabCount
        ActualTabCount = $actualTabCount
    } | Format-List
    $visualFrames | Select-Object Milliseconds | Format-Table -AutoSize

    if ($closeClicksSent -ne $CloseClickCount -or $actualTabCount -ne $expectedTabCount) {
        $failure = "The continuous-close probe did not close all $CloseClickCount target tabs."
    }
    elseif ($null -eq $firstMovement -or $firstMovement.Milliseconds -gt 100) {
        $failure = "Rendered pixels did not begin changing within 70 ms of the first close."
    }
    elseif (!$AssertRapidInputStability -and $motionFrames.Count -lt 6) {
        $failure = "The tab replacement has fewer than six distinct rendered frames."
    }
}
finally {
    for ($cleanupIndex = $temporaryTabs.Count - 1; $cleanupIndex -ge 0; $cleanupIndex--) {
        $temporaryRecord = $temporaryTabs[$cleanupIndex]
        $temporaryTab = $temporaryRecord.Element
        try {
            $bounds = $temporaryTab.Current.BoundingRectangle
            if ([double]::IsNaN($bounds.X) -or [double]::IsInfinity($bounds.X)) {
                throw "Stale UI Automation element."
            }
        }
        catch {
            if ($originalTabNames -contains $temporaryRecord.Name) {
                continue
            }
            $temporaryTab = Find-TabByName -Name $temporaryRecord.Name
            if ($null -eq $temporaryTab) {
                continue
            }
        }

        $beforeCount = (Get-PlanTabs).Count
        Click-Element -Element $temporaryTab
        Start-Sleep -Milliseconds 180
        try {
            $closeButton = Find-CloseButton -Tab $temporaryTab
        }
        catch {
            $closeButton = $null
        }
        if ($null -ne $closeButton) {
            Invoke-Element -Element $closeButton
            Wait-ForTabCount -Expected ($beforeCount - 1)
            Start-Sleep -Milliseconds 100
        }
    }

    if ($null -ne $originalSelectedTab) {
        try {
            Click-Element -Element $originalSelectedTab
        }
        catch {
            # The probe reports product failures above; cleanup must not hide
            # the primary result if the original tab was externally removed.
        }
    }
}

if ($null -ne $failure) {
    throw $failure
}
