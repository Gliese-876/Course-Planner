param(
    [string]$ProcessName = "CoursePlanner",
    [int]$AppPid = 0,
    [ValidateRange(2, 20)][int]$TemporaryTabCount = 7,
    [ValidateRange(1, 10)][int]$CloseIndex = 6,
    [ValidateRange(0, 20)][int]$LeftCloseSteps = 1,
    [ValidateRange(40, 500)][int]$InterClickDelayMilliseconds = 100,
    [switch]$AssertConditionalWidthReflow,
    [switch]$AssertTransitionFocusStability,
    [ValidateRange(50, 1000)][int]$TransitionSampleMilliseconds = 160,
    [ValidateRange(0, 20)][int]$MaximumCloseSteps = 0
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ("CoursePlannerContinuousCloseProbe" -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CoursePlannerContinuousCloseProbe
{
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@
}

$mouseLeftDown = 0x0002
$mouseLeftUp = 0x0004

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

function Get-Root {
    [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
}

function Get-PlanTabs {
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            $all = (Get-Root).FindAll(
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
            $orderedTabs = @($tabs | Sort-Object Position | ForEach-Object Element)
            if ($orderedTabs.Count -gt 0) {
                return $orderedTabs
            }
        }
        catch {
            Start-Sleep -Milliseconds 15
        }
    }
    throw "Unable to read a stable plan-tab automation tree."
}

function Find-ByAutomationId {
    param([Parameter(Mandatory = $true)][string]$AutomationId)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    (Get-Root).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-CloseButton {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $Tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $pattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Send-SingleClick {
    param([int]$X, [int]$Y)

    [CoursePlannerContinuousCloseProbe]::SetCursorPos($X, $Y) | Out-Null
    [CoursePlannerContinuousCloseProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerContinuousCloseProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Click-Element {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $scrollItem = $null
    if ($Element.TryGetCurrentPattern(
        [System.Windows.Automation.ScrollItemPattern]::Pattern,
        [ref]$scrollItem)) {
        ([System.Windows.Automation.ScrollItemPattern]$scrollItem).ScrollIntoView()
        Start-Sleep -Milliseconds 120
    }
    $bounds = $Element.Current.BoundingRectangle
    $hasFiniteBounds =
        -not [double]::IsNaN($bounds.Left) -and
        -not [double]::IsInfinity($bounds.Left) -and
        -not [double]::IsNaN($bounds.Top) -and
        -not [double]::IsInfinity($bounds.Top) -and
        $bounds.Width -gt 0 -and
        $bounds.Height -gt 0
    if (-not $hasFiniteBounds) {
        $selectionItem = $null
        if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$selectionItem)) {
            ([System.Windows.Automation.SelectionItemPattern]$selectionItem).Select()
            Start-Sleep -Milliseconds 120
            return
        }
        throw "The target element is offscreen and exposes neither a finite click target nor SelectionItemPattern."
    }
    $x = [int]($bounds.Left + [Math]::Min(48, $bounds.Width / 3))
    $y = [int]($bounds.Top + $bounds.Height / 2)
    [CoursePlannerContinuousCloseProbe]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 100
    [CoursePlannerContinuousCloseProbe]::mouse_event($mouseLeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [CoursePlannerContinuousCloseProbe]::mouse_event($mouseLeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Wait-ForTabCount {
    param(
        [Parameter(Mandatory = $true)][int]$Expected,
        [int]$TimeoutMilliseconds = 800
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        try {
            if ((Get-PlanTabs).Count -eq $Expected) {
                return $stopwatch.ElapsedMilliseconds
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 8
    }
    throw "Timed out waiting for $Expected plan tabs."
}

function Wait-ForStableTab {
    param(
        [Parameter(Mandatory = $true)][int]$Index,
        [int]$TimeoutMilliseconds = 1600
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $lastLeft = [double]::NaN
    $stableSamples = 0
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        $tabs = Get-PlanTabs
        if ($Index -lt $tabs.Count) {
            $tab = $tabs[$Index]
            $bounds = $tab.Current.BoundingRectangle
            if (!$tab.Current.IsOffscreen -and
                ![double]::IsNaN($bounds.Left) -and
                ![double]::IsInfinity($bounds.Left) -and
                [Math]::Abs($bounds.Left - $lastLeft) -lt 0.5) {
                $stableSamples++
                if ($stableSamples -ge 3) {
                    return $tab
                }
            }
            else {
                $stableSamples = 0
            }
            $lastLeft = $bounds.Left
        }
        Start-Sleep -Milliseconds 40
    }
    throw "Tab index $Index did not settle at a stable on-screen position."
}

function Find-TabByName {
    param([Parameter(Mandatory = $true)][string]$Name)

    Get-PlanTabs |
        Where-Object { $_.Current.Name -eq $Name } |
        Select-Object -First 1
}

function Get-ButtonAtPoint {
    param([int]$X, [int]$Y)

    try {
        $element = [System.Windows.Automation.AutomationElement]::FromPoint(
            [System.Windows.Point]::new($X, $Y))
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        while ($null -ne $element) {
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
                return $element
            }
            $element = $walker.GetParent($element)
        }
    }
    catch {
    }
    return $null
}

function Get-FocusedPlanTabAutomationId {
    try {
        $element = [System.Windows.Automation.AutomationElement]::FocusedElement
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        while ($null -ne $element) {
            if ($element.Current.AutomationId -match '^ShellPlanTab_.+$') {
                return $element.Current.AutomationId
            }
            $element = $walker.GetParent($element)
        }
    }
    catch {
    }
    return ""
}

[CoursePlannerContinuousCloseProbe]::ShowWindow($windowHandle, 9) | Out-Null
[CoursePlannerContinuousCloseProbe]::SetForegroundWindow($windowHandle) | Out-Null
Start-Sleep -Milliseconds 250

$originalTabs = Get-PlanTabs
if ($originalTabs.Count -lt 1) {
    throw "The probe requires an existing plan tab."
}
$originalNames = @($originalTabs | ForEach-Object { $_.Current.Name })
$originalSelectedName = $originalTabs[0].Current.Name
$temporaryNames = New-Object System.Collections.Generic.List[string]
$results = New-Object System.Collections.Generic.List[object]
$failure = $null

try {
    for ($created = 0; $created -lt $TemporaryTabCount; $created++) {
        $beforeCount = (Get-PlanTabs).Count
        Invoke-Element -Element (Find-ByAutomationId -AutomationId "ShellAddPlanTabButton")
        Wait-ForTabCount -Expected ($beforeCount + 1) | Out-Null
        Start-Sleep -Milliseconds 70
        $temporaryNames.Add((Get-PlanTabs)[-1].Current.Name)
    }

    Start-Sleep -Milliseconds 400

    $tabs = Get-PlanTabs
    if ($CloseIndex -ge $tabs.Count - 1) {
        throw "CloseIndex must identify a non-rightmost tab."
    }

    $targetBounds = $tabs[$CloseIndex].Current.BoundingRectangle
    if ($tabs[$CloseIndex].Current.IsOffscreen -or
        $targetBounds.X -lt -10000 -or
        [double]::IsNaN($targetBounds.X) -or
        [double]::IsInfinity($targetBounds.X)) {
        $tabGeometry = for ($geometryIndex = 0; $geometryIndex -lt $tabs.Count; $geometryIndex++) {
            $geometry = $tabs[$geometryIndex].Current
            "$geometryIndex=$($geometry.BoundingRectangle.X):$($geometry.IsOffscreen)"
        }
        throw "The requested middle tab is off-screen; choose a visible CloseIndex ($($tabGeometry -join ', '))."
    }
    Click-Element -Element $tabs[$CloseIndex]
    $target = Wait-ForStableTab -Index $CloseIndex
    $tabs = Get-PlanTabs
    $targetBounds = $target.Current.BoundingRectangle
    $targetCloseButton = Get-CloseButton -Tab $target
    $anchorX = if ($null -ne $targetCloseButton) {
        $closeBounds = $targetCloseButton.Current.BoundingRectangle
        [int]($closeBounds.Left + ($closeBounds.Width / 2))
    }
    else {
        [int]($targetBounds.Right - ($targetBounds.Height * 19 / 36))
    }
    $anchorY = [int]($targetBounds.Top + $targetBounds.Height / 2)
    [CoursePlannerContinuousCloseProbe]::SetCursorPos(
        [int]($targetBounds.Left + 2),
        $anchorY) | Out-Null
    Start-Sleep -Milliseconds 50
    [CoursePlannerContinuousCloseProbe]::SetCursorPos($anchorX, $anchorY) | Out-Null
    [CoursePlannerContinuousCloseProbe]::mouse_event(0x0001, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    $tabs = Get-PlanTabs
    $target = $tabs[$CloseIndex]
    $initialNames = @($tabs | ForEach-Object { $_.Current.Name })
    $closeTrailingInset = $target.Current.BoundingRectangle.Right - $anchorX
    $scale = $target.Current.BoundingRectangle.Height / 36
    $viewportBounds = (Find-ByAutomationId -AutomationId "ShellPlanTabScrollViewer").Current.BoundingRectangle
    $singleTabWidth = [Math]::Min(
        240 * $scale,
        [Math]::Max(92 * $scale, $viewportBounds.Width))
    $anchorDistanceFromLeft = $anchorX - $viewportBounds.Left
    $expectedRemoved = New-Object System.Collections.Generic.List[string]
    for ($rightIndex = $CloseIndex; $rightIndex -lt $initialNames.Count; $rightIndex++) {
        $expectedRemoved.Add($initialNames[$rightIndex])
    }
    $actualLeftCloseSteps = [Math]::Min($LeftCloseSteps, [Math]::Max(0, $CloseIndex - 1))
    for ($leftStep = 1; $leftStep -le $actualLeftCloseSteps; $leftStep++) {
        $expectedRemoved.Add($initialNames[$CloseIndex - $leftStep])
    }
    $closeCount = $expectedRemoved.Count
    if ($MaximumCloseSteps -gt 0) {
        $closeCount = [Math]::Min($closeCount, $MaximumCloseSteps)
    }

    $rightPhaseSeen = $false
    $leftPhaseEntered = $false
    for ($step = 0; $step -lt $closeCount; $step++) {
        $beforeTabs = Get-PlanTabs
        $beforeNames = @($beforeTabs | ForEach-Object { $_.Current.Name })
        $sourceIndex = [Math]::Min($CloseIndex, $beforeTabs.Count - 1)
        $sourceWasRightmost = $sourceIndex -eq ($beforeTabs.Count - 1)
        $rightToLeftHandoff =
            $sourceWasRightmost -and
            $rightPhaseSeen -and
            !$leftPhaseEntered
        $beforeTabWidth = $beforeTabs[-1].Current.BoundingRectangle.Width
        $remainingCount = $beforeTabs.Count - 1
        $singleTabWidthTotal = $remainingCount * $singleTabWidth
        $allMaximumTabsFitWithoutFilling = $singleTabWidthTotal -lt ($viewportBounds.Width - 1)
        $maximumTabsFitBeforeAnchor =
            $singleTabWidthTotal -le ($anchorDistanceFromLeft + 1) -or $remainingCount -eq 1
        $expectWidthReflow =
            $sourceWasRightmost -and
            !$rightToLeftHandoff -and
            $allMaximumTabsFitWithoutFilling -and
            $maximumTabsFitBeforeAnchor
        if ($sourceWasRightmost) {
            $leftPhaseEntered = $true
        }
        else {
            $rightPhaseSeen = $true
        }
        Send-SingleClick -X $anchorX -Y $anchorY
        try {
            $latency = Wait-ForTabCount -Expected ($beforeTabs.Count - 1)
        }
        catch {
            $failure = "Step $($step + 1): the fixed close anchor did not close a tab."
            break
        }

        $afterTabs = Get-PlanTabs
        $afterNames = @($afterTabs | ForEach-Object { $_.Current.Name })
        $removed = @($beforeNames | Where-Object { $afterNames -notcontains $_ })
        $actualRemoved = if ($removed.Count -eq 1) { $removed[0] } else { $removed -join " | " }
        $expected = $expectedRemoved[$step]
        $phase = if ($rightToLeftHandoff) { "Handoff" } elseif ($sourceWasRightmost) { "Left" } else { "Right" }
        $actualTabWidth = $afterTabs[-1].Current.BoundingRectangle.Width
        $expectedTabWidth = if (!$sourceWasRightmost -or $rightToLeftHandoff) {
            $beforeTabWidth
        }
        elseif ($expectWidthReflow) {
            $singleTabWidth
        }
        else {
            $fillWidth = ($anchorDistanceFromLeft + $closeTrailingInset) / $remainingCount
            [Math]::Min($singleTabWidth, [Math]::Max($beforeTabWidth, $fillWidth))
        }
        $replacementIndex = [Math]::Min($sourceIndex, $afterTabs.Count - 1)
        $replacementClose = Get-CloseButton -Tab $afterTabs[$replacementIndex]
        $replacementCloseCenter = if ($null -ne $replacementClose) {
            $bounds = $replacementClose.Current.BoundingRectangle
            $bounds.Left + ($bounds.Width / 2)
        }
        else {
            [double]::NaN
        }
        $anchorDrift = [Math]::Abs($replacementCloseCenter - $anchorX)
        $leftInset = $afterTabs[0].Current.BoundingRectangle.Left - $viewportBounds.Left
        $maxDelayedAnchorDrift = $anchorDrift
        $maxDelayedTabShift = 0.0
        $focusedTabAutomationId = Get-FocusedPlanTabAutomationId
        if ($AssertTransitionFocusStability -and
            $sourceWasRightmost -and
            !$expectWidthReflow) {
            $replacementName = $afterTabs[$replacementIndex].Current.Name
            $initialReplacementLeft = $afterTabs[$replacementIndex].Current.BoundingRectangle.Left
            $sampleTimer = [Diagnostics.Stopwatch]::StartNew()
            while ($sampleTimer.ElapsedMilliseconds -lt $TransitionSampleMilliseconds) {
                Start-Sleep -Milliseconds 8
                $sampleTab = Find-TabByName -Name $replacementName
                if ($null -eq $sampleTab) {
                    continue
                }
                $sampleBounds = $sampleTab.Current.BoundingRectangle
                $sampleClose = Get-CloseButton -Tab $sampleTab
                if ($null -ne $sampleClose) {
                    $sampleCloseBounds = $sampleClose.Current.BoundingRectangle
                    $sampleCloseCenter = $sampleCloseBounds.Left + ($sampleCloseBounds.Width / 2)
                    $maxDelayedAnchorDrift = [Math]::Max(
                        $maxDelayedAnchorDrift,
                        [Math]::Abs($sampleCloseCenter - $anchorX))
                }
                $maxDelayedTabShift = [Math]::Max(
                    $maxDelayedTabShift,
                    [Math]::Abs($sampleBounds.Left - $initialReplacementLeft))
                $sampleFocusedTab = Get-FocusedPlanTabAutomationId
                if (![string]::IsNullOrWhiteSpace($sampleFocusedTab)) {
                    $focusedTabAutomationId = $sampleFocusedTab
                }
            }
        }
        $results.Add([pscustomobject]@{
            Step = $step + 1
            Phase = $phase
            ExpectedRemoved = $expected
            ActualRemoved = $actualRemoved
            Handoff = $rightToLeftHandoff
            ExpectedReflow = $expectWidthReflow
            MaximumWidthTotal = [Math]::Round($singleTabWidthTotal, 1)
            ViewportWidth = [Math]::Round($viewportBounds.Width, 1)
            AnchorDistance = [Math]::Round($anchorDistanceFromLeft, 1)
            TabWidth = [Math]::Round($actualTabWidth, 1)
            AnchorDrift = [Math]::Round($anchorDrift, 1)
            DelayedAnchorDrift = [Math]::Round($maxDelayedAnchorDrift, 1)
            DelayedTabShift = [Math]::Round($maxDelayedTabShift, 1)
            FocusedTab = $focusedTabAutomationId
            LeftInset = [Math]::Round($leftInset, 1)
            CloseLatencyMilliseconds = $latency
            Remaining = $afterNames.Count
        })
        if ($actualRemoved -ne $expected) {
            $failure = "Step $($step + 1): expected $phase-side replacement order '$expected', removed '$actualRemoved'."
            break
        }
        if ($AssertConditionalWidthReflow -and
            [Math]::Abs($actualTabWidth - $expectedTabWidth) -gt 2) {
            $failure = "Step $($step + 1): conditional width reflow expected width $expectedTabWidth, actual $actualTabWidth."
            break
        }
        if ($AssertConditionalWidthReflow -and
            $sourceWasRightmost -and
            !$expectWidthReflow -and
            ([double]::IsNaN($anchorDrift) -or $anchorDrift -gt 2)) {
            $failure = "Step $($step + 1): width reflow moved the current close anchor by $anchorDrift physical pixels."
            break
        }
        if ($AssertConditionalWidthReflow -and
            $expectWidthReflow -and
            [Math]::Abs($leftInset) -gt 2) {
            $failure = "Step $($step + 1): all three conditions were satisfied, but the layout kept a $leftInset physical-pixel lock inset instead of releasing to normal maximum-width placement."
            break
        }
        if ($AssertConditionalWidthReflow -and
            $sourceWasRightmost -and
            !$rightToLeftHandoff -and
            !$expectWidthReflow -and
            $expectedTabWidth -lt ($singleTabWidth - 1) -and
            [Math]::Abs($leftInset) -gt 2) {
            $failure = "Step $($step + 1): left-fill layout left a $leftInset physical-pixel gap instead of filling from the viewport edge."
            break
        }
        if ($AssertTransitionFocusStability -and
            $sourceWasRightmost -and
            !$expectWidthReflow -and
            ($maxDelayedAnchorDrift -gt 2 -or $maxDelayedTabShift -gt 2)) {
            $failure = "Step $($step + 1): the right-to-left handoff replayed layout after settling (close drift $maxDelayedAnchorDrift px, tab shift $maxDelayedTabShift px)."
            break
        }

        Start-Sleep -Milliseconds $InterClickDelayMilliseconds
    }
}
finally {
    for ($index = $temporaryNames.Count - 1; $index -ge 0; $index--) {
        $name = $temporaryNames[$index]
        $tab = Find-TabByName -Name $name
        if ($null -eq $tab) {
            continue
        }

        Click-Element -Element $tab
        Start-Sleep -Milliseconds 120
        $tab = Find-TabByName -Name $name
        if ($null -eq $tab) {
            continue
        }
        $closeButton = Get-CloseButton -Tab $tab
        if ($null -ne $closeButton) {
            $beforeCount = (Get-PlanTabs).Count
            Invoke-Element -Element $closeButton
            Wait-ForTabCount -Expected ($beforeCount - 1) | Out-Null
        }
    }

    $original = Find-TabByName -Name $originalSelectedName
    if ($null -ne $original) {
        Click-Element -Element $original
    }
}

$results |
    Format-Table Step, Phase, Handoff, ExpectedReflow, TabWidth, AnchorDrift,
        DelayedAnchorDrift, DelayedTabShift, LeftInset, CloseLatencyMilliseconds, Remaining -AutoSize
if ($null -ne $failure) {
    throw $failure
}
