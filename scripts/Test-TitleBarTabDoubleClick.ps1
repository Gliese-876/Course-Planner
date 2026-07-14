param(
    [string]$ProcessName = "CoursePlanner",
    [int]$AppPid = 0,
    [string]$TabAutomationId = "",
    [int]$Attempts = 3
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("CoursePlannerTitleBarProbe" -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CoursePlannerTitleBarProbe
{
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    public static int HitTest(IntPtr windowHandle, int screenX, int screenY)
    {
        var packed = (screenY << 16) | (screenX & 0xffff);
        return SendMessage(windowHandle, 0x0084, IntPtr.Zero, new IntPtr(packed)).ToInt32();
    }
}
'@
}

function Find-ByAutomationId {
    param(
        [Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory = $true)][string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Send-SingleClick {
    param([int]$X, [int]$Y)

    [CoursePlannerTitleBarProbe]::SetCursorPos($X, $Y) | Out-Null
    [CoursePlannerTitleBarProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerTitleBarProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Send-DoubleClick {
    param([int]$X, [int]$Y)

    [CoursePlannerTitleBarProbe]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 100
    Send-SingleClick -X $X -Y $Y
    Start-Sleep -Milliseconds 60
    Send-SingleClick -X $X -Y $Y
    Start-Sleep -Milliseconds 60
}

function Get-PlanTabs {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
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
        [Parameter(Mandatory = $true)][scriptblock]$Condition,
        [int]$TimeoutMilliseconds = 3000
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        $count = (Get-PlanTabs).Count
        if (& $Condition $count) {
            return $count
        }
        Start-Sleep -Milliseconds 20
    }
    throw "Timed out waiting for the expected plan-tab count."
}

function Get-CloseButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $Tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Remove-TemporaryTabs {
    param([int]$BaselineCount)

    while ((Get-PlanTabs).Count -gt $BaselineCount) {
        $tabs = Get-PlanTabs
        $tab = $tabs[-1]
        $closeButton = Get-CloseButton -Tab $tab
        if ($null -eq $closeButton) {
            $bounds = $tab.Current.BoundingRectangle
            [CoursePlannerTitleBarProbe]::SetCursorPos(
                [int]($bounds.Left + $bounds.Width / 2),
                [int]($bounds.Top + $bounds.Height / 2)) | Out-Null
            [CoursePlannerTitleBarProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
            [CoursePlannerTitleBarProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 120
            $tab = (Get-PlanTabs)[-1]
            $closeButton = Get-CloseButton -Tab $tab
        }
        if ($null -eq $closeButton) {
            throw "Unable to expose a close button while cleaning temporary tabs."
        }

        $before = (Get-PlanTabs).Count
        Invoke-Element -Element $closeButton
        Wait-ForTabCount -Condition { param($count) $count -eq ($before - 1) } | Out-Null
    }
}

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

$mouseLeftDown = 0x0002
$mouseLeftUp = 0x0004
$maximizeWindow = 3
$restoreWindow = 9
$hitCaption = 2
$results = @()

try {
    foreach ($initialState in @(
        [pscustomobject]@{ Name = "Restored"; ShowCommand = $restoreWindow; Maximized = $false },
        [pscustomobject]@{ Name = "Maximized"; ShowCommand = $maximizeWindow; Maximized = $true })) {
        for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
            [CoursePlannerTitleBarProbe]::ShowWindow($windowHandle, $initialState.ShowCommand) | Out-Null
            [CoursePlannerTitleBarProbe]::SetForegroundWindow($windowHandle) | Out-Null
            Start-Sleep -Milliseconds 350

            $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
            $tab = if ([string]::IsNullOrWhiteSpace($TabAutomationId)) {
                (Get-PlanTabs | Select-Object -First 1)
            }
            else {
                Find-ByAutomationId -Root $root -AutomationId $TabAutomationId
            }
            if ($null -eq $tab) {
                throw "Unable to find a plan tab through UI Automation."
            }
            $addButton = Find-ByAutomationId -Root $root -AutomationId "ShellAddPlanTabButton"
            $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $closeButton = $tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            if ($null -eq $addButton -or $null -eq $closeButton) {
                throw "Unable to find the add-plan or close-tab button."
            }

            $bounds = $tab.Current.BoundingRectangle
            $targetX = [int]($bounds.Left + [Math]::Min(36, [Math]::Max(12, $bounds.Width / 4)))
            $targetY = [int]($bounds.Top + $bounds.Height / 2)
            $closeBounds = $closeButton.Current.BoundingRectangle
            $closeX = [int]($closeBounds.Left + $closeBounds.Width / 2)
            $closeY = [int]($closeBounds.Top + $closeBounds.Height / 2)
            $addBounds = $addButton.Current.BoundingRectangle
            $addX = [int]($addBounds.Left + $addBounds.Width / 2)
            $addY = [int]($addBounds.Top + $addBounds.Height / 2)
            Send-DoubleClick -X $targetX -Y $targetY
            Start-Sleep -Milliseconds 350
            $results += [pscustomobject]@{
                TargetKind = "InteractiveTab"
                InitialState = $initialState.Name
                Attempt = $attempt
                ExpectedMaximized = $initialState.Maximized
                ActualMaximized = [CoursePlannerTitleBarProbe]::IsZoomed($windowHandle)
                Target = "$targetX,$targetY"
            }

            [CoursePlannerTitleBarProbe]::ShowWindow($windowHandle, $initialState.ShowCommand) | Out-Null
            Start-Sleep -Milliseconds 250
            $baselineCount = (Get-PlanTabs).Count
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
            $addButton = Find-ByAutomationId -Root $root -AutomationId "ShellAddPlanTabButton"
            $addBounds = $addButton.Current.BoundingRectangle
            $addX = [int]($addBounds.Left + $addBounds.Width / 2)
            $addY = [int]($addBounds.Top + $addBounds.Height / 2)
            Send-DoubleClick -X $addX -Y $addY
            $addedCount = Wait-ForTabCount -Condition { param($count) $count -gt $baselineCount }
            $results += [pscustomobject]@{
                TargetKind = "AddButton"
                InitialState = $initialState.Name
                Attempt = $attempt
                ExpectedMaximized = $initialState.Maximized
                ActualMaximized = [CoursePlannerTitleBarProbe]::IsZoomed($windowHandle)
                Target = "$addX,$addY (+$($addedCount - $baselineCount))"
            }
            Remove-TemporaryTabs -BaselineCount $baselineCount

            [CoursePlannerTitleBarProbe]::ShowWindow($windowHandle, $initialState.ShowCommand) | Out-Null
            Start-Sleep -Milliseconds 250
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
            $addButton = Find-ByAutomationId -Root $root -AutomationId "ShellAddPlanTabButton"
            for ($created = 0; $created -lt 3; $created++) {
                $before = (Get-PlanTabs).Count
                Invoke-Element -Element $addButton
                Wait-ForTabCount -Condition { param($count) $count -eq ($before + 1) } | Out-Null
            }
            $beforeCloseCount = (Get-PlanTabs).Count
            $closeTab = (Get-PlanTabs)[-1]
            $closeButton = Get-CloseButton -Tab $closeTab
            if ($null -eq $closeButton) {
                throw "The selected temporary tab did not expose its close button."
            }
            $closeBounds = $closeButton.Current.BoundingRectangle
            $closeX = [int]($closeBounds.Left + $closeBounds.Width / 2)
            $closeY = [int]($closeBounds.Top + $closeBounds.Height / 2)
            [CoursePlannerTitleBarProbe]::SetCursorPos($closeX, $closeY) | Out-Null
            Start-Sleep -Milliseconds 100
            Send-SingleClick -X $closeX -Y $closeY
            $afterFirstCloseCount = Wait-ForTabCount -Condition { param($count) $count -eq ($beforeCloseCount - 1) }
            Send-SingleClick -X $closeX -Y $closeY
            Start-Sleep -Milliseconds 350
            $afterCloseCount = (Get-PlanTabs).Count
            $results += [pscustomobject]@{
                TargetKind = "CloseButton"
                InitialState = $initialState.Name
                Attempt = $attempt
                ExpectedMaximized = $initialState.Maximized
                ActualMaximized = [CoursePlannerTitleBarProbe]::IsZoomed($windowHandle)
                Target = "$closeX,$closeY (first: -$($beforeCloseCount - $afterFirstCloseCount), final: $afterCloseCount)"
            }
            Remove-TemporaryTabs -BaselineCount $baselineCount

            [CoursePlannerTitleBarProbe]::ShowWindow($windowHandle, $initialState.ShowCommand) | Out-Null
            Start-Sleep -Milliseconds 350
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
            $addButton = Find-ByAutomationId -Root $root -AutomationId "ShellAddPlanTabButton"
            $rootBounds = $root.Current.BoundingRectangle
            $addBounds = $addButton.Current.BoundingRectangle
            $captionLimit = [int]($rootBounds.Right - 150)
            $blankX = [int]($addBounds.Right + 24)
            $blankY = [int]($addBounds.Top + $addBounds.Height / 2)
            if ($blankX -ge $captionLimit) {
                throw "The current window does not expose enough blank title-bar space for the probe."
            }
            if ([CoursePlannerTitleBarProbe]::HitTest($windowHandle, $blankX, $blankY) -ne $hitCaption) {
                throw "The blank title-bar background is not a native caption region."
            }

            Send-DoubleClick -X $blankX -Y $blankY
            Start-Sleep -Milliseconds 350
            $results += [pscustomobject]@{
                TargetKind = "BlankCaption"
                InitialState = $initialState.Name
                Attempt = $attempt
                ExpectedMaximized = -not $initialState.Maximized
                ActualMaximized = [CoursePlannerTitleBarProbe]::IsZoomed($windowHandle)
                Target = "$blankX,$blankY"
            }
        }
    }
}
finally {
    [CoursePlannerTitleBarProbe]::ShowWindow($windowHandle, $restoreWindow) | Out-Null
}

$results | Format-Table -AutoSize
if ($results | Where-Object { $_.ActualMaximized -ne $_.ExpectedMaximized }) {
    throw "Title-bar double-click routing did not match the interactive/control and blank-caption rules."
}
