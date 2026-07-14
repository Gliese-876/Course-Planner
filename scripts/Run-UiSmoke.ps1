param(
    [Parameter(Mandatory = $true)]
    [int]$AppPid,

    [string]$OutputDirectory = "artifacts/ui-smoke"
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()
$mainWindowHwnd = $null

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$weekTwoTitleMarker = "2026-09-14 - 2026-09-20"
$ChineseLanguageText = "$([char]0x7B80)$([char]0x4F53)$([char]0x4E2D)$([char]0x6587)"
$SimplifiedChineseText = "Simplified Chinese"
$EnglishLanguageText = "English"
$ChineseAppTitleText = "$([char]0x9009)$([char]0x8BFE)$([char]0x52A9)$([char]0x624B)"
$SettingsChineseText = "$([char]0x8BBE)$([char]0x7F6E)"
$HourChineseText = "$([char]0x5C0F)$([char]0x65F6)"
$MinuteChineseText = "$([char]0x5206)$([char]0x949F)"
$ThirdPartyLicensesChineseText = "$([char]0x7B2C)$([char]0x4E09)$([char]0x65B9)$([char]0x8BB8)$([char]0x53EF)$([char]0x8BC1)"
$September2026ChineseText = "2026 $([char]0x5E74) 9 $([char]0x6708)"
$HourEnglishText = "Hour"
$MinuteEnglishText = "Minute"
$ThirdPartyLicensesEnglishText = "Third-party licenses"
$September2026EnglishText = "September 2026"
$ClosePlanTabChineseText = "$([char]0x5173)$([char]0x95ED)$([char]0x65B9)$([char]0x6848)$([char]0x6807)$([char]0x7B7E)"
$ClosePlanTabEnglishText = "Close plan tab"
$AddMenuChineseText = "$([char]0x6DFB)$([char]0x52A0)"
$AddMenuEnglishText = "Add"
$CurrentPlanChineseText = "$([char]0x5F53)$([char]0x524D)$([char]0x65B9)$([char]0x6848)"
$CurrentPlanEnglishText = "Current plan"
$NewPlanChineseText = "$([char]0x65B0)$([char]0x65B9)$([char]0x6848)"
$NewPlanEnglishText = "New Plan"
$NewCourseChineseText = "$([char]0x65B0)$([char]0x8BFE)$([char]0x7A0B)"
$NewCourseEnglishText = "New Course"
$RegistrationOrderChineseText = "$([char]0x62A2)$([char]0x8BFE)$([char]0x987A)$([char]0x5E8F)"
$RegistrationOrderEnglishText = "Registration order"
$UndoChineseText = "$([char]0x64A4)$([char]0x9500)"
$UndoEnglishText = "Undo"
$RedoChineseText = "$([char]0x91CD)$([char]0x505A)"
$RedoEnglishText = "Redo"
$KeepConflictChineseText = "$([char]0x4FDD)$([char]0x7559)$([char]0x51B2)$([char]0x7A81)"
$KeepConflictEnglishText = "Keep conflict"
$ManagementListHeaderActionRightInsetDip = 4.0
$CompactTimePickerMinimumWidthDip = 108.0
$CompactTimePickerMinimumValueSlotWidthDip = 36.0

if (-not ("SmokeNative" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

public struct SmokeNativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class SmokeNative
{
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out SmokeNativeRect rect);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

    public const uint KeyUp = 0x0002;
    public const uint MouseWheel = 0x0800;
    public const byte Ctrl = 0x11;
    public const byte Escape = 0x1B;
    public const int Maximize = 3;
    public const int Restore = 9;
}
"@
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-PlanTabElements {
    $deadline = (Get-Date).AddSeconds(5)
    do {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
            $all = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            break
        }
        catch {
            if ((Get-Date) -ge $deadline) {
                throw
            }
            Start-Sleep -Milliseconds 100
        }
    } while ($true)

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

function Get-PlanTabName {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    if ($Tab.Current.AutomationId -notmatch '^ShellPlanTab_(.+)$') {
        throw "Plan tab has an unexpected AutomationId: $($Tab.Current.AutomationId)"
    }

    $closeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "ShellPlanTabClose_$($Matches[1])")
    $closeButton = $Tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCondition)
    if ($null -eq $closeButton) {
        throw "Plan tab '$($Tab.Current.AutomationId)' has no plan-specific close button."
    }

    $accessibleName = $closeButton.Current.Name
    foreach ($prefix in @($script:ClosePlanTabChineseText, $script:ClosePlanTabEnglishText)) {
        $fullPrefix = "$prefix "
        if ($accessibleName.StartsWith($fullPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $accessibleName.Substring($fullPrefix.Length)
        }
    }

    throw "Close button '$accessibleName' does not expose a recognized plan-name prefix."
}

function Ensure-MinimumPlanTabCount {
    param([ValidateRange(1, 16)][int]$Count)

    $deadline = (Get-Date).AddSeconds(8)
    do {
        $tabs = @(Get-PlanTabElements)
        if ($tabs.Count -ge $Count) {
            return
        }

        $before = $tabs.Count
        winapp ui invoke ShellAddPlanTabButton -a $AppPid -w $script:mainWindowHwnd | Out-Null
        do {
            Start-Sleep -Milliseconds 150
            $tabs = @(Get-PlanTabElements)
        } while ($tabs.Count -le $before -and (Get-Date) -lt $deadline)
    } while ((Get-Date) -lt $deadline)

    throw "UI smoke requires at least $Count open plan tabs; found $(@(Get-PlanTabElements).Count)."
}

function Assert-CurrentPlanTab {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Tab)

    if ([string]::IsNullOrWhiteSpace($Tab.Current.ItemStatus)) {
        throw "Plan tab '$($Tab.Current.AutomationId)' was not exposed as the current plan."
    }
}

function Test-UI {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Script
    )

    try {
        $global:LASTEXITCODE = 0
        $output = & $Script 2>&1
        $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        if ($? -and $exitCode -eq 0) {
            $script:pass++
            $script:results += [pscustomobject]@{ name = $Name; status = "PASS"; detail = "" }
        }
        else {
            $script:fail++
            $script:results += [pscustomobject]@{ name = $Name; status = "FAIL"; detail = "$output" }
        }
    }
    catch {
        $script:fail++
        $script:results += [pscustomobject]@{ name = $Name; status = "FAIL"; detail = "$_" }
    }
}

function Invoke-AnyUI {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Selectors
    )

    $messages = @()
    foreach ($selector in $Selectors) {
        $output = winapp ui invoke $selector -a $AppPid -w $script:mainWindowHwnd 2>&1
        if ($LASTEXITCODE -eq 0) {
            return
        }
        $messages += "${selector}: $output"
    }

    throw ($messages -join "`n")
}

function Wait-AnyUI {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Selectors,

        [int]$Timeout = 5000
    )

    $messages = @()
    foreach ($selector in $Selectors) {
        $output = winapp ui wait-for $selector -a $AppPid -w $script:mainWindowHwnd -t $Timeout 2>&1
        if ($LASTEXITCODE -eq 0) {
            return
        }
        $windowOutput = $output

        $output = winapp ui wait-for $selector -a $AppPid -t $Timeout 2>&1
        if ($LASTEXITCODE -eq 0) {
            return
        }
        $messages += "${selector}: $windowOutput`n${selector} (popup): $output"
    }

    throw ($messages -join "`n")
}

function Send-VirtualKey {
    param(
        [Parameter(Mandatory = $true)]
        [byte]$Key
    )

    [SmokeNative]::ShowWindow([IntPtr]$script:mainWindowHwnd, [SmokeNative]::Restore) | Out-Null
    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 100
    [SmokeNative]::keybd_event($Key, 0, 0, [UIntPtr]::Zero)
    [SmokeNative]::keybd_event($Key, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
}

function Send-FocusedVirtualKey {
    param(
        [Parameter(Mandatory = $true)]
        [byte]$Key
    )

    [SmokeNative]::keybd_event($Key, 0, 0, [UIntPtr]::Zero)
    [SmokeNative]::keybd_event($Key, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
}

function Send-FocusedDigitKey {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 9)]
        [int]$Digit
    )

    Send-FocusedVirtualKey ([byte](0x30 + $Digit))
}

function Send-MouseWheelOver {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector,

        [int]$Delta = -120
    )

    $bounds = Get-UiBounds $Selector
    [SmokeNative]::SetCursorPos(
        [int][Math]::Round($bounds.X + ($bounds.Width / 2)),
        [int][Math]::Round($bounds.Y + ($bounds.Height / 2))) | Out-Null
    Start-Sleep -Milliseconds 200
    [SmokeNative]::mouse_event([SmokeNative]::MouseWheel, 0, 0, $Delta, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Assert-SearchText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector
    )

    $result = winapp ui search $Selector -a $AppPid --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "${Selector}: $result"
    }

    $matches = $result | ConvertFrom-Json
    if ($matches.matchCount -le 0) {
        throw "${Selector}: no matching UI text found"
    }
}

function Assert-AnySearchText {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Selectors
    )

    $messages = @()
    foreach ($selector in $Selectors) {
        $result = winapp ui search $selector -a $AppPid --json 2>&1
        if ($LASTEXITCODE -ne 0) {
            $messages += "${selector}: $result"
            continue
        }

        $matches = $result | ConvertFrom-Json
        if ($matches.matchCount -gt 0) {
            return
        }

        $messages += "${selector}: no matching UI text found"
    }

    throw ($messages -join "`n")
}

function Get-MainWindowAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
            $condition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                $AutomationId)
            $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element) {
                return $element
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "UI element '$AutomationId' was not found within $Timeout ms.$detail"
}

function Get-VisibleProcessAutomationElements {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$RetryTimeout = 1000
    )

    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $automationIdCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = (Get-Date).AddMilliseconds($RetryTimeout)
    $lastError = $null
    do {
        try {
            $matches = [System.Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
            $topLevelWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                $processCondition)

            for ($windowIndex = 0; $windowIndex -lt $topLevelWindows.Count; $windowIndex++) {
                $window = $topLevelWindows.Item($windowIndex)
                $candidates = $window.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $automationIdCondition)
                for ($candidateIndex = 0; $candidateIndex -lt $candidates.Count; $candidateIndex++) {
                    $candidate = $candidates.Item($candidateIndex)
                    $bounds = $candidate.Current.BoundingRectangle
                    if ($candidate.Current.ProcessId -eq $AppPid -and
                        -not $candidate.Current.IsOffscreen -and
                        $bounds.Width -gt 0 -and
                        $bounds.Height -gt 0) {
                        $matches.Add($candidate)
                    }
                }
            }

            foreach ($match in $matches) {
                $match
            }
            return
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw [InvalidOperationException]::new(
        "UI Automation FindAll for '$AutomationId' remained unavailable for $RetryTimeout ms.",
        $lastError.Exception)
}

function Get-ProcessAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    $lastMatchCount = 0
    do {
        try {
            $matches = @(Get-VisibleProcessAutomationElements $AutomationId)
            $lastMatchCount = $matches.Count
            if ($matches.Count -eq 1) {
                return $matches[0]
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "Expected one visible process-owned UI element '$AutomationId' within $Timeout ms; found $lastMatchCount.$detail"
}

function Focus-AutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $automationId = $Element.Current.AutomationId
    $Element.SetFocus()
    $deadline = (Get-Date).AddSeconds(2)
    do {
        Start-Sleep -Milliseconds 50
        if ($Element.Current.HasKeyboardFocus) {
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "UI element '$automationId' did not receive keyboard focus."
}

function Wait-AutomationNumericValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [double]$ExpectedValue,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastObserved = $null
    $lastError = $null
    do {
        try {
            $element = Get-MainWindowAutomationElement $AutomationId -Timeout 250
            $rangePattern = $null
            if (-not $element.TryGetCurrentPattern(
                    [System.Windows.Automation.RangeValuePattern]::Pattern,
                    [ref]$rangePattern)) {
                throw "UI element '$AutomationId' does not expose RangeValuePattern."
            }

            $lastObserved = [double]$rangePattern.Current.Value
            if ([Math]::Abs($lastObserved - $ExpectedValue) -lt 0.001) {
                return
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    $observedText = if ($null -eq $lastObserved) { "unavailable" } else { "$lastObserved" }
    throw "UI element '$AutomationId' did not expose numeric value $ExpectedValue within $Timeout ms. Observed: $observedText.$detail"
}

function Set-AutomationNumericValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [double]$Value
    )

    $element = Get-MainWindowAutomationElement $AutomationId
    try {
        $rangePattern = [System.Windows.Automation.RangeValuePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)
    }
    catch {
        throw "UI element '$AutomationId' does not expose RangeValuePattern: $_"
    }
    if ($rangePattern.Current.IsReadOnly) {
        throw "UI element '$AutomationId' is read-only."
    }

    $rangePattern.SetValue($Value)
}

function Get-NumberBoxEditorAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            $numberBox = Get-MainWindowAutomationElement $AutomationId -Timeout 250
            $editCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)
            $editor = $numberBox.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                $editCondition)
            if ($null -ne $editor) {
                return $editor
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "NumberBox '$AutomationId' did not expose an editable text control within $Timeout ms.$detail"
}

function Set-NumberBoxTextAndCommit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    [SmokeNative]::ShowWindow([IntPtr]$script:mainWindowHwnd, [SmokeNative]::Restore) | Out-Null
    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 100

    $editor = Get-NumberBoxEditorAutomationElement $AutomationId
    Focus-AutomationElement $editor
    try {
        $valuePattern = [System.Windows.Automation.ValuePattern]$editor.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
    }
    catch {
        throw "NumberBox '$AutomationId' editor does not expose ValuePattern: $_"
    }
    if ($valuePattern.Current.IsReadOnly) {
        throw "NumberBox '$AutomationId' editor is read-only."
    }

    $valuePattern.SetValue($Text)
    Send-FocusedVirtualKey ([byte]0x0D)
}

function Wait-NumberBoxVisibleText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedText,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastObserved = $null
    $lastError = $null
    do {
        try {
            $editor = Get-NumberBoxEditorAutomationElement $AutomationId -Timeout 250
            $valuePattern = [System.Windows.Automation.ValuePattern]$editor.GetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern)
            $lastObserved = $valuePattern.Current.Value
            if ([string]::Equals($lastObserved, $ExpectedText, [StringComparison]::Ordinal)) {
                return
            }
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    $observedText = if ($null -eq $lastObserved) { "unavailable" } else { "'$lastObserved'" }
    throw "NumberBox '$AutomationId' did not show '$ExpectedText' within $Timeout ms. Observed: $observedText.$detail"
}

function Invoke-AutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    if (-not $Element.Current.IsEnabled) {
        throw "UI element '$($Element.Current.AutomationId)' is disabled."
    }

    try {
        $invokePattern = [System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
    }
    catch {
        throw "UI element '$($Element.Current.AutomationId)' does not expose InvokePattern: $_"
    }

    $invokePattern.Invoke()
}

function Invoke-MainWindowAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    Invoke-AutomationElement (Get-MainWindowAutomationElement $AutomationId)
}

function Resize-AppWindow {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Width,

        [Parameter(Mandatory = $true)]
        [int]$Height
    )

    [SmokeNative]::ShowWindow([IntPtr]$script:mainWindowHwnd, [SmokeNative]::Restore) | Out-Null
    [SmokeNative]::MoveWindow([IntPtr]$script:mainWindowHwnd, 80, 80, $Width, $Height, $true) | Out-Null
    Start-Sleep -Milliseconds 700
}

function Invoke-WhileCtrlHeld {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector
    )

    $element = Get-MainWindowAutomationElement $Selector
    [SmokeNative]::ShowWindow([IntPtr]$script:mainWindowHwnd, [SmokeNative]::Restore) | Out-Null
    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, 0, [UIntPtr]::Zero)
    try {
        $deadline = (Get-Date).AddSeconds(3)
        do {
            Start-Sleep -Milliseconds 50
            try {
                $enabled = $element.Current.IsEnabled
            }
            catch {
                $element = Get-MainWindowAutomationElement $Selector
                $enabled = $element.Current.IsEnabled
            }
        } while (-not $enabled -and (Get-Date) -lt $deadline)

        if (-not $enabled) {
            throw "UI element '$Selector' did not become enabled while Ctrl was held."
        }

        Invoke-AutomationElement $element
        Start-Sleep -Milliseconds 150
    }
    finally {
        [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    }
}

function Assert-DisabledWhileCtrlHeld {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 100
    [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, 0, [UIntPtr]::Zero)
    try {
        Start-Sleep -Milliseconds 150
        $element = Get-MainWindowAutomationElement $AutomationId
        if ($element.Current.IsEnabled) {
            throw "UI element '$AutomationId' was enabled with only one comparison plan selected."
        }
    }
    finally {
        [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    }
}

function Assert-AppWindowMinimumSize {
    param(
        [Parameter(Mandatory = $true)]
        [int]$MinimumWidthDip,

        [Parameter(Mandatory = $true)]
        [int]$MinimumHeightDip
    )

    $hwnd = [IntPtr]$script:mainWindowHwnd
    try {
        [SmokeNative]::ShowWindow($hwnd, [SmokeNative]::Restore) | Out-Null
        $targetWidth = [Math]::Max(240, $MinimumWidthDip - 160)
        $targetHeight = [Math]::Max(240, $MinimumHeightDip - 160)
        [SmokeNative]::MoveWindow($hwnd, 80, 80, $targetWidth, $targetHeight, $true) | Out-Null
        Start-Sleep -Milliseconds 700

        $rect = [SmokeNativeRect]::new()
        if (-not [SmokeNative]::GetWindowRect($hwnd, [ref]$rect)) {
            throw "GetWindowRect failed."
        }

        $actualWidth = $rect.Right - $rect.Left
        $actualHeight = $rect.Bottom - $rect.Top

        if ($actualWidth -lt ($MinimumWidthDip - 2) -or $actualHeight -lt ($MinimumHeightDip - 2)) {
            throw "Window minimum size was not enforced. Actual ${actualWidth}x${actualHeight}, expected at least ${MinimumWidthDip}x${MinimumHeightDip}."
        }
    }
    finally {
        Resize-AppWindow 1600 900
    }
}

function Clear-TransientUi {
    [SmokeNative]::SetCursorPos(1, 1) | Out-Null
    Start-Sleep -Milliseconds 250
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, 0, [UIntPtr]::Zero)
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}

function Save-AppScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    Clear-TransientUi
    winapp ui screenshot RootNavigation -a $AppPid -w $script:mainWindowHwnd -o (Join-Path $OutputDirectory $FileName)
}

function Save-ScreenRectangleScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [double]$X,

        [Parameter(Mandatory = $true)]
        [double]$Y,

        [Parameter(Mandatory = $true)]
        [double]$Width,

        [Parameter(Mandatory = $true)]
        [double]$Height,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $left = [int][Math]::Floor($X)
    $top = [int][Math]::Floor($Y)
    $right = [int][Math]::Ceiling($X + $Width)
    $bottom = [int][Math]::Ceiling($Y + $Height)
    $pixelWidth = $right - $left
    $pixelHeight = $bottom - $top
    if ($pixelWidth -le 0 -or $pixelHeight -le 0) {
        throw "Cannot capture a non-positive screen rectangle ${pixelWidth}x${pixelHeight}."
    }

    $bitmap = [System.Drawing.Bitmap]::new($pixelWidth, $pixelHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $left,
            $top,
            0,
            0,
            [System.Drawing.Size]::new($pixelWidth, $pixelHeight),
            [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save(
            (Join-Path $OutputDirectory $FileName),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Save-WindowScreenScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $rect = [SmokeNativeRect]::new()
    if (-not [SmokeNative]::GetWindowRect($WindowHandle, [ref]$rect)) {
        throw "GetWindowRect failed for '$WindowHandle'."
    }

    Save-ScreenRectangleScreenshot `
        -X $rect.Left `
        -Y $rect.Top `
        -Width ($rect.Right - $rect.Left) `
        -Height ($rect.Bottom - $rect.Top) `
        -FileName $FileName
}

function Save-AutomationElementScreenScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $bounds = $Element.Current.BoundingRectangle
    Save-ScreenRectangleScreenshot `
        -X $bounds.X `
        -Y $bounds.Y `
        -Width $bounds.Width `
        -Height $bounds.Height `
        -FileName $FileName
}

function Maximize-AppWindow {
    [SmokeNative]::ShowWindow(
        [IntPtr]$script:mainWindowHwnd,
        [SmokeNative]::Maximize) | Out-Null
    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 700
}

function Test-AutomationElementVisible {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    try {
        $bounds = $Element.Current.BoundingRectangle
        return -not $Element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0
    }
    catch {
        return $false
    }
}

function Get-ResponsiveToolbarCommandNames {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    switch ($AutomationId) {
        "NewCourseButton" { return @($script:NewCourseChineseText, $script:NewCourseEnglishText) }
        "NewPlanButton" { return @($script:NewPlanChineseText, $script:NewPlanEnglishText) }
        "RegistrationOrderButton" { return @($script:RegistrationOrderChineseText, $script:RegistrationOrderEnglishText) }
        "UndoButton" { return @($script:UndoChineseText, $script:UndoEnglishText) }
        "RedoButton" { return @($script:RedoChineseText, $script:RedoEnglishText) }
        default { throw "No localized overflow names are registered for '$AutomationId'." }
    }
}

function Get-OptionalMainWindowAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 100
    )

    try {
        return Get-MainWindowAutomationElement $AutomationId -Timeout $Timeout
    }
    catch {
        return $null
    }
}

function Prepare-HistoryToolbarOverflow {
    Maximize-AppWindow
    $more = Get-OptionalMainWindowAutomationElement ToolbarMoreButton
    if ($null -ne $more -and (Test-AutomationElementVisible $more)) {
        if (-not $more.Current.IsEnabled) {
            throw "ToolbarMoreButton is visible but disabled while commands are overflowed."
        }
        return
    }

    $hwnd = [IntPtr]$script:mainWindowHwnd
    $maximizedRect = [SmokeNativeRect]::new()
    if (-not [SmokeNative]::GetWindowRect($hwnd, [ref]$maximizedRect)) {
        throw "GetWindowRect failed while preparing toolbar overflow."
    }

    [SmokeNative]::ShowWindow($hwnd, [SmokeNative]::Restore) | Out-Null
    $height = [Math]::Max(720, [Math]::Min(900, $maximizedRect.Bottom - $maximizedRect.Top))
    $maximumWidth = $maximizedRect.Right - $maximizedRect.Left
    for ($targetWidth = $maximumWidth - 16; $targetWidth -ge 752; $targetWidth -= 16) {
        [SmokeNative]::MoveWindow($hwnd, 80, 80, $targetWidth, $height, $true) | Out-Null
        Start-Sleep -Milliseconds 120
        $more = Get-OptionalMainWindowAutomationElement ToolbarMoreButton -Timeout 100
        if ($null -eq $more -or -not (Test-AutomationElementVisible $more)) {
            continue
        }

        Start-Sleep -Milliseconds 250
        $more = Get-OptionalMainWindowAutomationElement ToolbarMoreButton -Timeout 250
        if ($null -eq $more) {
            continue
        }
        if (-not $more.Current.IsEnabled) {
            throw "ToolbarMoreButton became visible at width $targetWidth but is disabled."
        }
        return
    }

    # Some UIA providers omit a collapsed More button and continue to expose every command
    # directly even at the minimum width. Direct history commands are a valid presentation.
    return
}

function Assert-HistoryToolbarPresentation {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$UndoEnabled,

        [Parameter(Mandatory = $true)]
        [bool]$RedoEnabled,

        [Parameter(Mandatory = $true)]
        [string]$ScreenshotFileName
    )

    $undo = Get-OptionalMainWindowAutomationElement UndoButton
    $redo = Get-OptionalMainWindowAutomationElement RedoButton
    $more = Get-OptionalMainWindowAutomationElement ToolbarMoreButton
    $undoVisible = $null -ne $undo -and (Test-AutomationElementVisible $undo)
    $redoVisible = $null -ne $redo -and (Test-AutomationElementVisible $redo)
    $moreVisible = $null -ne $more -and (Test-AutomationElementVisible $more)

    if ($moreVisible -and -not $more.Current.IsEnabled) {
        throw "ToolbarMoreButton is visible but disabled while overflow commands exist."
    }
    if ((-not $undoVisible -or -not $redoVisible) -and -not $moreVisible) {
        throw "A history command is collapsed but ToolbarMoreButton is not visible."
    }

    $menuOpened = $false
    try {
        $actualUndo = if ($undoVisible) { [bool]$undo.Current.IsEnabled } else { $null }
        $actualRedo = if ($redoVisible) { [bool]$redo.Current.IsEnabled } else { $null }
        if (-not $undoVisible -or -not $redoVisible) {
            Invoke-AutomationElement $more
            $menuOpened = $true

            if (-not $undoVisible) {
                $undoItem = Get-VisibleProcessMenuItemByNames `
                    @(Get-ResponsiveToolbarCommandNames UndoButton)
                $actualUndo = [bool]$undoItem.Current.IsEnabled
            }
            if (-not $redoVisible) {
                $redoItem = Get-VisibleProcessMenuItemByNames `
                    @(Get-ResponsiveToolbarCommandNames RedoButton)
                $actualRedo = [bool]$redoItem.Current.IsEnabled
            }
        }

        if ($actualUndo -ne $UndoEnabled -or $actualRedo -ne $RedoEnabled) {
            throw "History presentation exposed Undo=$actualUndo, Redo=$actualRedo; expected Undo=$UndoEnabled, Redo=$RedoEnabled."
        }

        Save-WindowScreenScreenshot `
            -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
            -FileName $ScreenshotFileName
    }
    finally {
        if ($menuOpened) {
            Send-FocusedVirtualKey ([SmokeNative]::Escape)
            Start-Sleep -Milliseconds 200
        }
    }
}

function Wait-HistoryCommandState {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$UndoEnabled,

        [Parameter(Mandatory = $true)]
        [bool]$RedoEnabled,

        [Parameter(Mandatory = $true)]
        [string]$ScreenshotFileName,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            Assert-HistoryToolbarPresentation `
                -UndoEnabled $UndoEnabled `
                -RedoEnabled $RedoEnabled `
                -ScreenshotFileName $ScreenshotFileName
            return
        }
        catch {
            $lastError = $_
        }

        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "History state did not become Undo=$UndoEnabled, Redo=$RedoEnabled within $Timeout ms.$detail"
}

function Invoke-ResponsiveToolbarCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $command = Get-OptionalMainWindowAutomationElement $AutomationId
    if ($null -ne $command -and (Test-AutomationElementVisible $command)) {
        Invoke-AutomationElement $command
        return
    }

    $more = Get-OptionalMainWindowAutomationElement ToolbarMoreButton
    if ($null -eq $more -or -not (Test-AutomationElementVisible $more)) {
        throw "Toolbar command '$AutomationId' is collapsed without a visible ToolbarMoreButton."
    }
    if (-not $more.Current.IsEnabled) {
        throw "ToolbarMoreButton is disabled while '$AutomationId' is overflowed."
    }

    Invoke-AutomationElement $more
    $menuItem = Get-VisibleProcessMenuItemByNames `
        @(Get-ResponsiveToolbarCommandNames $AutomationId)
    Invoke-AutomationElement $menuItem
}

function Send-ControlChord {
    param(
        [Parameter(Mandatory = $true)]
        [byte]$Key
    )

    [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
    Start-Sleep -Milliseconds 100
    [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, 0, [UIntPtr]::Zero)
    try {
        [SmokeNative]::keybd_event($Key, 0, 0, [UIntPtr]::Zero)
        [SmokeNative]::keybd_event($Key, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    }
    finally {
        [SmokeNative]::keybd_event([SmokeNative]::Ctrl, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 80
}

function Assert-FocusWithinAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 2000
    )

    $target = Get-MainWindowAutomationElement $AutomationId
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastFocused = "none"
    do {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $current = $focused
        while ($null -ne $current) {
            try {
                $lastFocused = "$($focused.Current.AutomationId) ('$($focused.Current.Name)')"
                if ([System.Windows.Automation.Automation]::Compare($current, $target)) {
                    return
                }
                $current = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($current)
            }
            catch {
                break
            }
        }

        Start-Sleep -Milliseconds 25
    } while ((Get-Date) -lt $deadline)

    throw "Keyboard focus did not enter '$AutomationId' within $Timeout ms. Last focused element: $lastFocused."
}

function Test-IsTooltipAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $current = $Element
    for ($depth = 0; $depth -lt 8 -and $null -ne $current; $depth++) {
        try {
            if ($current.Current.ControlType -eq [System.Windows.Automation.ControlType]::ToolTip -or
                $current.Current.ClassName -match 'ToolTip') {
                return $true
            }
            $current = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($current)
        }
        catch {
            return $false
        }
    }

    return $false
}

function Get-VisibleCtrlFShortcutTooltips {
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $processCondition)
    $shortcutPattern = '(?i)\b(?:ctrl|control)\s*\+\s*f\b'

    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements.Item($index)
        try {
            $bounds = $element.Current.BoundingRectangle
            if ($element.Current.ProcessId -ne $AppPid -or
                $element.Current.IsOffscreen -or
                $bounds.Width -le 0 -or
                $bounds.Height -le 0 -or
                -not (Test-IsTooltipAutomationElement $element)) {
                continue
            }

            $name = $element.Current.Name
            $helpText = $element.Current.HelpText
            if ($name -match $shortcutPattern -or $helpText -match $shortcutPattern) {
                [pscustomobject]@{
                    AutomationId = $element.Current.AutomationId
                    Name = $name
                    HelpText = $helpText
                }
            }
        }
        catch {
            # Tooltip popups are transient; elements that disappear mid-enumeration are irrelevant.
        }
    }
}

function Assert-NoCtrlFShortcutTooltips {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $elements = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $hoverControlTypes = @(
        [System.Windows.Automation.ControlType]::Button,
        [System.Windows.Automation.ControlType]::Edit,
        [System.Windows.Automation.ControlType]::ComboBox,
        [System.Windows.Automation.ControlType]::Hyperlink,
        [System.Windows.Automation.ControlType]::ListItem,
        [System.Windows.Automation.ControlType]::TabItem,
        [System.Windows.Automation.ControlType]::TreeItem)
    $centers = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    try {
        for ($index = 0; $index -lt $elements.Count; $index++) {
            $element = $elements.Item($index)
            try {
                $bounds = $element.Current.BoundingRectangle
                if ($element.Current.ProcessId -ne $AppPid -or
                    $element.Current.IsOffscreen -or
                    $bounds.Width -le 0 -or
                    $bounds.Height -le 0 -or
                    $hoverControlTypes -notcontains $element.Current.ControlType) {
                    continue
                }

                $x = [int][Math]::Round($bounds.X + ($bounds.Width / 2))
                $y = [int][Math]::Round($bounds.Y + ($bounds.Height / 2))
                if (-not $centers.Add("$x,$y")) {
                    continue
                }

                [SmokeNative]::SetCursorPos($x, $y) | Out-Null
                Start-Sleep -Milliseconds 750
                $matches = @(Get-VisibleCtrlFShortcutTooltips)
                if ($matches.Count -gt 0) {
                    Save-WindowScreenScreenshot `
                        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
                        -FileName "requested-ctrl-f-tooltip-failure.png"
                    $details = $matches | ForEach-Object {
                        "id='$($_.AutomationId)', name='$($_.Name)', help='$($_.HelpText)'"
                    }
                    throw "Hovering '$($element.Current.AutomationId)' exposed a Ctrl+F shortcut tooltip: $($details -join '; ')"
                }
            }
            catch {
                if ("$_" -match 'exposed a Ctrl\+F shortcut tooltip') {
                    throw
                }
                # Dynamic planner elements may be replaced while the batch walks the UIA tree.
            }
        }
    }
    finally {
        [SmokeNative]::SetCursorPos(1, 1) | Out-Null
        Start-Sleep -Milliseconds 300
    }
}

function Get-VisibleMainWindowAutomationElement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        try {
            $element = Get-MainWindowAutomationElement $AutomationId -Timeout 100
            $bounds = $element.Current.BoundingRectangle
            if (-not $element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) {
                return $element
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 25
    } while ((Get-Date) -lt $deadline)

    throw "Visible UI element '$AutomationId' was not found within $Timeout ms."
}

function Test-MainWindowAutomationElementVisible {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    try {
        $element = Get-MainWindowAutomationElement $AutomationId -Timeout 50
        $bounds = $element.Current.BoundingRectangle
        return -not $element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0
    }
    catch {
        return $false
    }
}

function Test-MainWindowAutomationElementInTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    try {
        Get-MainWindowAutomationElement $AutomationId -Timeout 50 | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Wait-MainWindowAutomationElementGone {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        if (-not (Test-MainWindowAutomationElementInTree $AutomationId)) {
            return
        }
        Start-Sleep -Milliseconds 20
    } while ((Get-Date) -lt $deadline)

    throw "UI element '$AutomationId' remained in the UI Automation tree after $Timeout ms."
}

function Wait-StopwatchElapsed {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Stopwatch]$Stopwatch,

        [Parameter(Mandatory = $true)]
        [int]$Milliseconds
    )

    while ($Stopwatch.ElapsedMilliseconds -lt $Milliseconds) {
        $remaining = $Milliseconds - $Stopwatch.ElapsedMilliseconds
        Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(25, $remaining)))
    }
}

function Assert-StatusOpenActionHidden {
    try {
        $element = Get-MainWindowAutomationElement StatusOpenButton -Timeout 100
        $bounds = $element.Current.BoundingRectangle
        if (-not $element.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0) {
            throw "Ordinary StatusBar unexpectedly exposed StatusOpenButton."
        }
    }
    catch {
        if ("$_" -match 'unexpectedly exposed') {
            throw
        }
    }
}

function Get-StatusBarCloseButton {
    $status = Get-VisibleMainWindowAutomationElement StatusBar
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $status.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $buttonCondition)
    $candidates = @()
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        try {
            $bounds = $button.Current.BoundingRectangle
            if ($button.Current.AutomationId -ne "StatusOpenButton" -and
                $button.Current.IsEnabled -and
                -not $button.Current.IsOffscreen -and
                $bounds.Width -gt 0 -and
                $bounds.Height -gt 0) {
                $candidates += [pscustomobject]@{
                    Right = $bounds.Right
                    Element = $button
                }
            }
        }
        catch {
        }
    }

    $close = $candidates | Sort-Object Right -Descending | Select-Object -First 1
    if ($null -eq $close) {
        throw "Visible StatusBar did not expose its native close button."
    }
    return $close.Element
}

function Set-MainWindowAutomationTextValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [AllowEmptyString()]
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $element = Get-MainWindowAutomationElement $AutomationId
    $valuePattern = $null
    if (-not $element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$valuePattern)) {
        $editCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)
        $element = $element.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $editCondition)
        if ($null -eq $element -or
            -not $element.TryGetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern,
                [ref]$valuePattern)) {
            throw "UI element '$AutomationId' does not expose ValuePattern."
        }
    }

    $value = [System.Windows.Automation.ValuePattern]$valuePattern
    if ($value.Current.IsReadOnly) {
        throw "UI element '$AutomationId' is read-only."
    }
    $value.SetValue($Text)
    Start-Sleep -Milliseconds 200
}

function Get-MainWindowAutomationElementByNameFragment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [string]$NameFragment,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastMatchCount = 0
    $lastError = $null
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    do {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
            $candidates = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            $matches = [System.Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
            for ($index = 0; $index -lt $candidates.Count; $index++) {
                $candidate = $candidates.Item($index)
                $name = $candidate.Current.Name
                if (-not [string]::IsNullOrEmpty($name) -and
                    $name.IndexOf(
                        $NameFragment,
                        [StringComparison]::Ordinal) -ge 0) {
                    $matches.Add($candidate)
                }
            }

            $lastMatchCount = $matches.Count
            if ($matches.Count -eq 1) {
                return $matches[0]
            }
        }
        catch {
            $lastError = $_
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "Expected one '$AutomationId' containing '$NameFragment'; found $lastMatchCount.$detail"
}

function Scroll-AutomationElementIntoView {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $scrollItemPattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern,
            [ref]$scrollItemPattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$scrollItemPattern).ScrollIntoView()
        Start-Sleep -Milliseconds 250
    }
}

function Get-VisibleProcessAutomationElementByNames {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names,

        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.ControlType]$ControlType,

        [int]$Timeout = 5000
    )

    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $controlTypeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $lastError = $null
    do {
        try {
            $topLevelWindows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                $processCondition)
            for ($windowIndex = 0; $windowIndex -lt $topLevelWindows.Count; $windowIndex++) {
                $window = $topLevelWindows.Item($windowIndex)
                $elements = $window.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $controlTypeCondition)
                for ($index = 0; $index -lt $elements.Count; $index++) {
                    $element = $elements.Item($index)
                    $bounds = $element.Current.BoundingRectangle
                    if ($element.Current.ProcessId -eq $AppPid -and
                        $Names -contains $element.Current.Name -and
                        -not $element.Current.IsOffscreen -and
                        $bounds.Width -gt 0 -and
                        $bounds.Height -gt 0) {
                        return $element
                    }
                }
            }
        }
        catch {
            $lastError = $_
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) { "" } else { " Last UI Automation error: $lastError" }
    throw "Could not find a visible process-owned $ControlType named '$($Names -join "' or '")'.$detail"
}

function Get-VisibleProcessMenuItemByNames {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names,

        [int]$Timeout = 5000
    )

    Get-VisibleProcessAutomationElementByNames `
        -Names $Names `
        -ControlType ([System.Windows.Automation.ControlType]::MenuItem) `
        -Timeout $Timeout
}

function Get-VisibleProcessButtonByNames {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names,

        [int]$Timeout = 250
    )

    try {
        $button = Get-VisibleProcessAutomationElementByNames `
            -Names $Names `
            -ControlType ([System.Windows.Automation.ControlType]::Button) `
            -Timeout $Timeout
        if ($button.Current.IsEnabled) {
            return $button
        }
        return $null
    }
    catch {
        return $null
    }
}

function Wait-CourseAddStatus {
    param([int]$Timeout = 5000)

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        if (Test-MainWindowAutomationElementVisible StatusBar) {
            return
        }

        $keepConflict = Get-VisibleProcessButtonByNames `
            @($script:KeepConflictChineseText, $script:KeepConflictEnglishText)
        if ($null -ne $keepConflict) {
            Invoke-AutomationElement $keepConflict
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "Adding the requested course produced neither a StatusBar nor a resolvable conflict dialog."
}

function Expand-ProcessMenuItemByNames {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Names
    )

    $element = Get-VisibleProcessMenuItemByNames $Names
    $expandPattern = $null
    if ($element.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$expandPattern)) {
        ([System.Windows.Automation.ExpandCollapsePattern]$expandPattern).Expand()
    }
    else {
        Invoke-AutomationElement $element
    }
    Start-Sleep -Milliseconds 250
}

function Wait-MainWindowAutomationElementEnabled {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$Timeout = 5000
    )

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    do {
        try {
            $element = Get-MainWindowAutomationElement $AutomationId -Timeout 100
            if ($element.Current.IsEnabled) {
                return $element
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    throw "UI element '$AutomationId' did not become enabled within $Timeout ms."
}

function ConvertTo-WindowHandleInt64 {
    param([Parameter(Mandatory = $true)]$Value)

    $text = "$Value"
    if ($text.StartsWith("0x", [StringComparison]::OrdinalIgnoreCase)) {
        return [Convert]::ToInt64($text.Substring(2), 16)
    }
    return [Convert]::ToInt64($text, 10)
}

function Get-OwnedWinUiWindowHandle {
    param([int]$Timeout = 5000)

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $mainHandle = ConvertTo-WindowHandleInt64 $script:mainWindowHwnd
    do {
        $output = winapp ui list-windows -a $AppPid --json 2>&1
        if ($LASTEXITCODE -eq 0) {
            $windows = @($output | ConvertFrom-Json)
            foreach ($window in $windows) {
                try {
                    $handle = ConvertTo-WindowHandleInt64 $window.hwnd
                    $owner = ConvertTo-WindowHandleInt64 $window.ownerHwnd
                    if ($window.className -eq "WinUIDesktopWin32WindowClass" -and
                        $handle -ne $mainHandle -and
                        $owner -eq $mainHandle) {
                        return [IntPtr]$handle
                    }
                }
                catch {
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)

    throw "No owned WinUI tool window appeared within $Timeout ms."
}

function Assert-DisabledTransparentButtonBackground {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Element,

        [Parameter(Mandatory = $true)]
        [string]$ScreenshotFileName
    )

    if ($Element.Current.IsEnabled) {
        throw "UI element '$($Element.Current.AutomationId)' must be disabled at the default window size."
    }
    $bounds = $Element.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "Disabled UI element '$($Element.Current.AutomationId)' is not visible."
    }

    Save-AutomationElementScreenScreenshot $Element $ScreenshotFileName

    $padding = 8
    $width = [int][Math]::Ceiling($bounds.Width) + (2 * $padding)
    $height = [int][Math]::Ceiling($bounds.Height) + (2 * $padding)
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            ([int][Math]::Floor($bounds.X) - $padding),
            ([int][Math]::Floor($bounds.Y) - $padding),
            0,
            0,
            [System.Drawing.Size]::new($width, $height),
            [System.Drawing.CopyPixelOperation]::SourceCopy)

        $buttonHeight = [int][Math]::Ceiling($bounds.Height)
        if ($buttonHeight -lt 16) {
            throw "Disabled button height $buttonHeight is too small for a reliable transparency sample."
        }

        # Compare several pixels immediately inside and outside the left edge at
        # the same Y coordinate. This avoids both the adjacent pin button and the
        # native top-border/Mica gradient above this title-bar button.
        $insideX = $padding + 3
        $outsideX = $padding - 3
        $sampleFractions = @(0.20, 0.30, 0.40, 0.50, 0.60, 0.70, 0.80)
        $channelDeltas = [System.Collections.Generic.List[int]]::new()
        $sampleDetails = [System.Collections.Generic.List[string]]::new()
        foreach ($sampleFraction in $sampleFractions) {
            $sampleYOffset = [int][Math]::Round($buttonHeight * $sampleFraction)
            $sampleY = $padding + $sampleYOffset
            $inside = $bitmap.GetPixel($insideX, $sampleY)
            $outside = $bitmap.GetPixel($outsideX, $sampleY)
            $maximumChannelDelta = [Math]::Max(
                [Math]::Abs([int]$inside.R - [int]$outside.R),
                [Math]::Max(
                    [Math]::Abs([int]$inside.G - [int]$outside.G),
                    [Math]::Abs([int]$inside.B - [int]$outside.B)))
            $channelDeltas.Add($maximumChannelDelta)
            $sampleDetails.Add(
                "yOffset=$sampleYOffset inside=$($inside.R),$($inside.G),$($inside.B) " +
                "outside=$($outside.R),$($outside.G),$($outside.B) delta=$maximumChannelDelta")
        }

        $sortedDeltas = @($channelDeltas | Sort-Object)
        $medianChannelDelta = $sortedDeltas[[int][Math]::Floor($sortedDeltas.Count / 2.0)]
        $matchingSampleCount = @($channelDeltas | Where-Object { $_ -le 8 }).Count
        $requiredMatchingSampleCount = [int][Math]::Ceiling($channelDeltas.Count * 0.70)
        if ($medianChannelDelta -gt 8 -or
            $matchingSampleCount -lt $requiredMatchingSampleCount) {
            throw (
                "Disabled button background is visibly different from the adjacent title bar " +
                "(median channel delta $medianChannelDelta; matching samples " +
                "$matchingSampleCount/$($channelDeltas.Count), required $requiredMatchingSampleCount). " +
                "Samples: $($sampleDetails -join '; ')")
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Wait-UiGone {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector,

        [int]$Timeout = 5000
    )

    $output = winapp ui wait-for $Selector -a $AppPid -w $script:mainWindowHwnd -t $Timeout --gone 2>&1
    if ($LASTEXITCODE -eq 0 -or "$output" -match "not found") {
        return
    }

    throw "$output"
}

function Select-ComboItem {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComboSelector,

        [Parameter(Mandatory = $true)]
        [string[]]$ItemTexts
    )

    winapp ui invoke $ComboSelector -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Start-Sleep -Milliseconds 300

    $inspect = winapp ui inspect $ComboSelector -a $AppPid -w $script:mainWindowHwnd --depth 5 2>&1
    foreach ($itemText in $ItemTexts) {
        $pattern = '^\s*(\S+)\s+ListItem\s+"' + [regex]::Escape($itemText) + '"'
        foreach ($line in $inspect) {
            if ($line -match $pattern) {
                winapp ui invoke $Matches[1] -a $AppPid -w $script:mainWindowHwnd | Out-Null
                Start-Sleep -Milliseconds 900
                return
            }
        }
    }

    throw "Could not find any combo item '$($ItemTexts -join "', '")'. Inspect output: $inspect"
}

function Assert-ComboValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComboSelector,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedText
    )

    $value = winapp ui get-value $ComboSelector -a $AppPid -w $script:mainWindowHwnd 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$value") -or "$value" -notlike "*$ExpectedText*") {
        throw "$ComboSelector value '$value' did not include '$ExpectedText'."
    }
}

function Get-UiBounds {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector
    )

    $result = winapp ui inspect $Selector -a $AppPid -w $script:mainWindowHwnd --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "${Selector}: $result"
    }

    $json = $result | ConvertFrom-Json
    $element = $json.windows |
        ForEach-Object { $_.elements } |
        Where-Object { $null -ne $_ } |
        Select-Object -First 1
    if ($null -eq $element) {
        throw "${Selector}: no inspectable UI element found"
    }

    [pscustomobject]@{
        X = [double]$element.x
        Y = [double]$element.y
        Width = [double]$element.width
        Height = [double]$element.height
        Right = [double]$element.x + [double]$element.width
        Bottom = [double]$element.y + [double]$element.height
    }
}

function Assert-UiElementOnScreen {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) {
        throw "UI element '$AutomationId' was not found."
    }

    $bounds = $element.Current.BoundingRectangle
    if ($element.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "UI element '$AutomationId' is not reachable on screen (offscreen=$($element.Current.IsOffscreen), bounds=$bounds)."
    }
}

function Assert-CompactTimePickerDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedValue
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $parentCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $parent = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $parentCondition)
    if ($null -eq $parent -or $parent.Current.IsOffscreen) {
        throw "Visible compact time picker '$AutomationId' was not found."
    }

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $button = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if ($null -eq $button -or $button.Current.IsOffscreen) {
        throw "Visible time button for '$AutomationId' was not found."
    }
    if (-not $button.Current.Name.EndsWith(" $ExpectedValue", [System.StringComparison]::Ordinal)) {
        throw "Time button '$AutomationId' has incomplete accessible name '$($button.Current.Name)'; expected suffix '$ExpectedValue'."
    }

    $dpi = [SmokeNative]::GetDpiForWindow([IntPtr]$script:mainWindowHwnd)
    if ($dpi -eq 0) {
        throw "Could not resolve window DPI for '$AutomationId'."
    }
    $scale = $dpi / 96.0
    $buttonWidthDip = $button.Current.BoundingRectangle.Width / $scale
    if ($buttonWidthDip -lt $script:CompactTimePickerMinimumWidthDip - 0.5) {
        throw "Time button '$AutomationId' is only $([Math]::Round($buttonWidthDip, 2)) DIP wide at $dpi DPI; expected at least $($script:CompactTimePickerMinimumWidthDip) DIP."
    }

    $textTypeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $textNameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $ExpectedValue)
    $valueCondition = New-Object System.Windows.Automation.AndCondition($textTypeCondition, $textNameCondition)
    $valueText = $button.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $valueCondition)
    if ($null -eq $valueText -or $valueText.Current.IsOffscreen) {
        throw "Full visible value text '$ExpectedValue' was not exposed for '$AutomationId'."
    }
    $valueWidthDip = $valueText.Current.BoundingRectangle.Width / $scale
    if ($valueWidthDip -lt $script:CompactTimePickerMinimumValueSlotWidthDip - 0.5) {
        throw "Time value slot '$AutomationId' is only $([Math]::Round($valueWidthDip, 2)) DIP wide; '$ExpectedValue' would be visually truncated."
    }
}

function Select-ListItemByDescendantText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Text)
    $textElement = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $textElement) {
        throw "List item text '$Text' was not found."
    }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $listItem = $textElement
    while ($null -ne $listItem -and
           $listItem.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
        $listItem = $walker.GetParent($listItem)
    }
    if ($null -eq $listItem) {
        throw "Text '$Text' does not have a ListItem ancestor."
    }

    $scrollItemPattern = $null
    if ($listItem.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern,
            [ref]$scrollItemPattern)) {
        $scrollItemPattern.ScrollIntoView()
    }

    $deadline = (Get-Date).AddSeconds(3)
    while ($listItem.Current.IsOffscreen -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($listItem.Current.IsOffscreen) {
        throw "List item '$Text' could not be scrolled into view."
    }

    $listItem.SetFocus()
    $selectionPattern = $null
    if (-not $listItem.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$selectionPattern)) {
        throw "List item '$Text' does not expose SelectionItemPattern."
    }
    $selectionPattern.Select()
    Start-Sleep -Milliseconds 250
    if (-not $selectionPattern.Current.IsSelected) {
        throw "List item '$Text' was not selected."
    }
}

function Invoke-FirstDescendantButton {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ParentAutomationId
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $parentCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $ParentAutomationId)
    $parent = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $parentCondition)
    if ($null -eq $parent -or -not $parent.Current.IsEnabled) {
        throw "Enabled UI container '$ParentAutomationId' was not found."
    }

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $button = $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if ($null -eq $button -or -not $button.Current.IsEnabled) {
        throw "Enabled descendant button for '$ParentAutomationId' was not found."
    }

    $invokePattern = $null
    if (-not $button.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$invokePattern)) {
        throw "Descendant button for '$ParentAutomationId' does not expose InvokePattern."
    }
    $invokePattern.Invoke()
    Start-Sleep -Milliseconds 250
}

function Open-CompactTimePickerAutomationSurface {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ParentAutomationId
    )

    [SmokeNative]::ShowWindow([IntPtr]$script:mainWindowHwnd, [SmokeNative]::Restore) | Out-Null
    $deadline = (Get-Date).AddSeconds(2)
    do {
        [SmokeNative]::SetForegroundWindow([IntPtr]$script:mainWindowHwnd) | Out-Null
        Start-Sleep -Milliseconds 50
        if ([SmokeNative]::GetForegroundWindow() -eq [IntPtr]$script:mainWindowHwnd) {
            break
        }
    } while ((Get-Date) -lt $deadline)
    if ([SmokeNative]::GetForegroundWindow() -ne [IntPtr]$script:mainWindowHwnd) {
        throw "The main window could not be foregrounded before opening '$ParentAutomationId'."
    }

    Invoke-FirstDescendantButton $ParentAutomationId
    foreach ($automationId in @("TimePickerHourWheel", "TimePickerMinuteWheel", "TimePickerApplyButton")) {
        Get-ProcessAutomationElement $automationId | Out-Null
    }
}

function Assert-CompactTimePickerAutomationSurface {
    foreach ($automationId in @("TimePickerHourWheel", "TimePickerMinuteWheel", "TimePickerApplyButton")) {
        Get-ProcessAutomationElement $automationId -Timeout 1000 | Out-Null
    }
}

function Assert-FocusedTimePickerPartValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^\d{2}$')]
        [string]$ExpectedValue
    )

    $element = Get-ProcessAutomationElement $AutomationId
    if (-not $element.Current.HasKeyboardFocus) {
        throw "Time picker part '$AutomationId' does not have keyboard focus."
    }
    if (-not $element.Current.Name.EndsWith(" $ExpectedValue", [StringComparison]::Ordinal)) {
        throw "Time picker part '$AutomationId' exposed '$($element.Current.Name)' instead of value '$ExpectedValue'."
    }
}

function Wait-CompactTimePickerAutomationSurfaceGone {
    param([int]$Timeout = 3000)

    $deadline = (Get-Date).AddMilliseconds($Timeout)
    $remaining = @()
    $lastError = $null
    do {
        try {
            $remaining = @(
                foreach ($automationId in @("TimePickerHourWheel", "TimePickerMinuteWheel", "TimePickerApplyButton")) {
                    if (@(Get-VisibleProcessAutomationElements $automationId).Count -gt 0) {
                        $automationId
                    }
                })
            $lastError = $null
            if ($remaining.Count -eq 0) {
                return
            }
        }
        catch {
            $lastError = $_
            $remaining = @("enumeration-in-progress")
        }
        Start-Sleep -Milliseconds 50
    } while ((Get-Date) -lt $deadline)

    $detail = if ($null -eq $lastError) {
        ""
    }
    else {
        " Last UI Automation error: $lastError"
    }
    throw "Time picker surface remained visible after $Timeout ms: $($remaining -join ', ').$detail"
}

function Set-UiVerticalScrollPercent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [ValidateRange(0, 100)]
        [double]$Percent
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $matches = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $visibleMatches = @(
        for ($index = 0; $index -lt $matches.Count; $index++) {
            $candidate = $matches.Item($index)
            if (-not $candidate.Current.IsOffscreen) {
                $candidate
            }
        })
    if ($visibleMatches.Count -eq 0) {
        throw "Scroll container '$AutomationId' was not found."
    }
    if ($visibleMatches.Count -gt 1) {
        throw "Scroll container '$AutomationId' is ambiguous; found $($visibleMatches.Count) on-screen matches."
    }
    $element = $visibleMatches[0]

    $pattern = $null
    if (-not $element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern,
            [ref]$pattern)) {
        throw "Scroll container '$AutomationId' does not expose ScrollPattern."
    }
    if (-not $pattern.Current.VerticallyScrollable) {
        throw "Scroll container '$AutomationId' is not vertically scrollable."
    }

    $deadline = (Get-Date).AddSeconds(5)
    do {
        $pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $Percent)
        Start-Sleep -Milliseconds 250
        $actual = $pattern.Current.VerticalScrollPercent
        if ([Math]::Abs($actual - $Percent) -le 1) {
            return
        }
    } while ((Get-Date) -lt $deadline)

    throw "Scroll container '$AutomationId' reached $actual% instead of $Percent%."
}

function Get-UiSearchBounds {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Selector,

        [Parameter(Mandatory = $true)]
        [string]$Type
    )

    $result = winapp ui search $Selector -a $AppPid -w $script:mainWindowHwnd --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "${Selector}: $result"
    }

    $json = $result | ConvertFrom-Json
    $element = $json.matches |
        Where-Object { $_.type -eq $Type } |
        Select-Object -First 1
    if ($null -eq $element) {
        throw "${Selector}: no $Type search match found"
    }

    [pscustomobject]@{
        X = [double]$element.x
        Y = [double]$element.y
        Width = [double]$element.width
        Height = [double]$element.height
        Right = [double]$element.x + [double]$element.width
        Bottom = [double]$element.y + [double]$element.height
    }
}

function Assert-SemesterAddButtonVisibleEdgeAligned {
    $fileName = Join-Path $OutputDirectory "semester-add-button-visible-edge.png"
    Clear-TransientUi
    winapp ui screenshot RootNavigation -a $AppPid -w $script:mainWindowHwnd --capture-screen -o $fileName | Out-Null

    $addButton = Get-UiBounds AddSemesterButton
    $semesterList = Get-UiBounds SemesterList
    $content = Get-UiBounds ContentScrollViewer

    $dpiScale = $addButton.Height / 34.0
    $expectedActionRight = $semesterList.Right - ($script:ManagementListHeaderActionRightInsetDip * $dpiScale)
    $delta = [Math]::Abs([int][Math]::Round($addButton.Right) - [int][Math]::Round($expectedActionRight))
    if ($delta -gt 2) {
        throw "Add button right edge $([int][Math]::Round($addButton.Right)) is not aligned with semester list content track right edge $([int][Math]::Round($expectedActionRight)). Delta: $delta px."
    }

    $interPaneGap = [int][Math]::Round($content.X - $semesterList.Right)
    if ($interPaneGap -lt 18 -or $interPaneGap -gt 44) {
        throw "Semester list/content gap $interPaneGap px is outside the intended comfortable spacing range."
    }
}

Test-UI "App window exists" {
    $windows = @()
    $deadline = (Get-Date).AddSeconds(5)
    do {
        $windows = winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json
        $mainWindow = $windows | Where-Object { $_.className -eq "WinUIDesktopWin32WindowClass" -and $_.ownerHwnd -eq 0 } | Select-Object -First 1
        if ($mainWindow) {
            $script:mainWindowHwnd = $mainWindow.hwnd
            return
        }

        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    if (-not ($windows | Where-Object { $_.title -match "$ChineseAppTitleText|Course Planner" })) {
        throw "Course Planner window was not found."
    }
}
if (-not $script:mainWindowHwnd) {
    throw "Course Planner main window handle was not resolved; aborting UI smoke to avoid cascading false failures."
}

Test-UI "App window enforces minimum size" { Assert-AppWindowMinimumSize 752 360 }

Test-UI "Navigate to Planner" { winapp ui invoke PlannerItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Shell plan tabs visible" { winapp ui wait-for ShellPlanTabs -a $AppPid -w $script:mainWindowHwnd -t 5000 }

# Requested UI behavior batch (default): history commands may be direct or overflowed, but More must stay usable.
Test-UI "Requested history buttons expose initial, new-plan, undo, and redo states" {
    Prepare-HistoryToolbarOverflow
    $initialPlanCount = @(Get-PlanTabElements).Count

    Wait-HistoryCommandState `
        -UndoEnabled $false `
        -RedoEnabled $false `
        -ScreenshotFileName "requested-history-initial.png"

    Invoke-ResponsiveToolbarCommand NewPlanButton
    Wait-HistoryCommandState `
        -UndoEnabled $true `
        -RedoEnabled $false `
        -ScreenshotFileName "requested-history-new-plan.png"

    Invoke-ResponsiveToolbarCommand UndoButton
    Wait-HistoryCommandState `
        -UndoEnabled $false `
        -RedoEnabled $true `
        -ScreenshotFileName "requested-history-undone.png"

    Invoke-ResponsiveToolbarCommand RedoButton
    Wait-HistoryCommandState `
        -UndoEnabled $true `
        -RedoEnabled $false `
        -ScreenshotFileName "requested-history-redone.png"

    # Restore the persisted plan set so repeated default smoke runs do not accumulate plans.
    Invoke-ResponsiveToolbarCommand UndoButton
    $cleanupDeadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 50
        $restoredPlanCount = @(Get-PlanTabElements).Count
    } while ($restoredPlanCount -ne $initialPlanCount -and (Get-Date) -lt $cleanupDeadline)
    if ($restoredPlanCount -ne $initialPlanCount) {
        throw "History probe cleanup left $restoredPlanCount plans; expected $initialPlanCount."
    }
    Maximize-AppWindow
}

Test-UI "At least two plan tabs are available" { Ensure-MinimumPlanTabCount 2 }
$planTabElements = @(Get-PlanTabElements)
if ($planTabElements.Count -lt 2) {
    throw "UI smoke requires at least two open plan tabs; found $($planTabElements.Count)."
}
$firstPlanTab = $planTabElements[0]
$secondPlanTab = $planTabElements[1]
$firstPlanTabId = $firstPlanTab.Current.AutomationId
$secondPlanTabId = $secondPlanTab.Current.AutomationId
$firstPlanName = Get-PlanTabName $firstPlanTab
$secondPlanName = Get-PlanTabName $secondPlanTab
Test-UI "Plan tabs expose stable UIA Invoke and plan-specific close buttons" {
    foreach ($tab in @($firstPlanTab, $secondPlanTab)) {
        if ($tab.Current.AutomationId -notmatch '^ShellPlanTab_(.+)$') {
            throw "Plan tab AutomationId does not contain a stable plan id: $($tab.Current.AutomationId)"
        }
        $planId = $Matches[1]
        $invokePattern = $null
        if (-not $tab.TryGetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern,
                [ref]$invokePattern)) {
            throw "Plan tab '$($tab.Current.AutomationId)' does not expose InvokePattern."
        }
        $closeCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            "ShellPlanTabClose_$planId")
        if ($null -eq $tab.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCondition)) {
            throw "Plan tab '$($tab.Current.AutomationId)' has no plan-specific close button."
        }
    }
}
Test-UI "Plan tab UIA Invoke selects the plan" {
    winapp ui invoke $secondPlanTabId -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Start-Sleep -Milliseconds 250
    Assert-CurrentPlanTab $secondPlanTab
}
Test-UI "Focused plan tab Enter selects the plan" {
    winapp ui focus $firstPlanTabId -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Send-VirtualKey 0x0D
    Assert-CurrentPlanTab $firstPlanTab
}
Test-UI "Focused plan tab Space selects the plan" {
    winapp ui focus $secondPlanTabId -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Send-VirtualKey 0x20
    Assert-CurrentPlanTab $secondPlanTab
}
Test-UI "Reset planner to week mode" { winapp ui invoke WeekViewButton -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Milliseconds 500
Test-UI "Normalize planner tab selection" {
    winapp ui click $secondPlanTabId -a $AppPid -w $script:mainWindowHwnd
    Start-Sleep -Milliseconds 250
    winapp ui click $firstPlanTabId -a $AppPid -w $script:mainWindowHwnd
}
Test-UI "Course library search visible" { winapp ui wait-for CourseSearchBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Course library hierarchy visible" { winapp ui wait-for LibraryTree -a $AppPid -w $script:mainWindowHwnd -t 5000 }

# Requested UI behavior batch (default): Ctrl+F remains functional without advertising itself as a hover tooltip.
Test-UI "Requested Ctrl+F focuses the course search field" {
    $focusSource = @(Get-PlanTabElements)[0]
    Focus-AutomationElement $focusSource
    Send-ControlChord ([byte]0x46)
    Assert-FocusWithinAutomationElement CourseSearchBox
}
Test-UI "Requested hover scan exposes no Ctrl+F shortcut tooltip" {
    Assert-NoCtrlFShortcutTooltips
}

$script:requestedCourseName = "UI Smoke Course $AppPid-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
# Requested UI behavior batch (default): an ordinary status has no Open action, lives for three seconds,
# and remains in the UIA tree during the exit-animation interval before it is collapsed.
Test-UI "Requested ordinary StatusBar auto-closes after three seconds with entrance and exit motion" {
    Invoke-ResponsiveToolbarCommand NewCourseButton
    Get-VisibleMainWindowAutomationElement CourseNameBox -Timeout 5000 | Out-Null
    Set-MainWindowAutomationTextValue CourseNameBox $script:requestedCourseName
    winapp ui scroll-into-view SaveCourseEditButton -a $AppPid -w $script:mainWindowHwnd | Out-Null

    $statusLifetime = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-MainWindowAutomationElement SaveCourseEditButton
    Get-VisibleMainWindowAutomationElement StatusBar -Timeout 5000 | Out-Null
    Assert-StatusOpenActionHidden
    Save-WindowScreenScreenshot `
        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
        -FileName "requested-status-entering.png"

    Wait-StopwatchElapsed $statusLifetime 260
    if (-not (Test-MainWindowAutomationElementVisible StatusBar)) {
        throw "StatusBar disappeared during its entrance/settled interval."
    }
    Save-WindowScreenScreenshot `
        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
        -FileName "requested-status-settled.png"

    Wait-StopwatchElapsed $statusLifetime 2600
    if (-not (Test-MainWindowAutomationElementVisible StatusBar)) {
        throw "StatusBar closed before 2.60 seconds from its trigger."
    }
    Wait-StopwatchElapsed $statusLifetime 2750
    if (-not (Test-MainWindowAutomationElementVisible StatusBar)) {
        throw "StatusBar closed before its three-second lifetime elapsed."
    }
    Save-WindowScreenScreenshot `
        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
        -FileName "requested-status-before-auto-exit.png"

    Wait-StopwatchElapsed $statusLifetime 3000
    if (Test-MainWindowAutomationElementInTree StatusBar) {
        Save-WindowScreenScreenshot `
            -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
            -FileName "requested-status-exiting.png"
    }

    Wait-StopwatchElapsed $statusLifetime 3350
    if (Test-MainWindowAutomationElementInTree StatusBar) {
        $remaining = [Math]::Max(1, 4000 - $statusLifetime.ElapsedMilliseconds)
        Wait-MainWindowAutomationElementGone StatusBar -Timeout $remaining
    }
    $elapsed = $statusLifetime.ElapsedMilliseconds
    if ((Test-MainWindowAutomationElementInTree StatusBar) -or $elapsed -gt 4000) {
        throw "StatusBar remained in the UIA tree beyond the four-second auto-close deadline: $elapsed ms."
    }
}

Test-UI "Requested rapid consecutive statuses keep only the latest notification lifetime" {
    $firstEditRow = Get-MainWindowAutomationElementByNameFragment `
        -AutomationId LibraryCourseRow `
        -NameFragment $script:requestedCourseName `
        -Timeout 5000
    Scroll-AutomationElementIntoView $firstEditRow
    Invoke-AutomationElement $firstEditRow
    Get-VisibleMainWindowAutomationElement SaveCourseEditButton -Timeout 5000 | Out-Null
    $firstNotification = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-MainWindowAutomationElement SaveCourseEditButton
    Get-VisibleMainWindowAutomationElement StatusBar -Timeout 5000 | Out-Null
    Start-Sleep -Milliseconds 250

    $secondEditRow = Get-MainWindowAutomationElementByNameFragment `
        -AutomationId LibraryCourseRow `
        -NameFragment $script:requestedCourseName `
        -Timeout 5000
    Scroll-AutomationElementIntoView $secondEditRow
    Invoke-AutomationElement $secondEditRow
    Get-VisibleMainWindowAutomationElement SaveCourseEditButton -Timeout 5000 | Out-Null
    $latestNotification = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-MainWindowAutomationElement SaveCourseEditButton
    Get-VisibleMainWindowAutomationElement StatusBar -Timeout 5000 | Out-Null
    Assert-StatusOpenActionHidden

    Wait-StopwatchElapsed $firstNotification 3350
    if (-not (Test-MainWindowAutomationElementInTree StatusBar)) {
        throw "The stale notification generation closed the replacement StatusBar."
    }
    Save-WindowScreenScreenshot `
        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
        -FileName "requested-status-latest-generation.png"

    Wait-StopwatchElapsed $latestNotification 3350
    if (Test-MainWindowAutomationElementInTree StatusBar) {
        $remaining = [Math]::Max(1, 4000 - $latestNotification.ElapsedMilliseconds)
        Wait-MainWindowAutomationElementGone StatusBar -Timeout $remaining
    }
    if ((Test-MainWindowAutomationElementInTree StatusBar) -or
        $latestNotification.ElapsedMilliseconds -gt 4000) {
        throw "The latest StatusBar remained in the UIA tree beyond its four-second deadline."
    }
}

# The saved course provides deterministic state for the registration-order reset-button assertion.
Test-UI "Requested registration reset is disabled with a transparent background" {
    $courseRow = Get-MainWindowAutomationElementByNameFragment `
        -AutomationId LibraryCourseRow `
        -NameFragment $script:requestedCourseName `
        -Timeout 5000
    Scroll-AutomationElementIntoView $courseRow
    Focus-AutomationElement $courseRow
    Send-FocusedVirtualKey ([byte]0x5D)
    Expand-ProcessMenuItemByNames @($script:AddMenuChineseText, $script:AddMenuEnglishText)
    $currentPlanItem = Get-VisibleProcessMenuItemByNames `
        @($script:CurrentPlanChineseText, $script:CurrentPlanEnglishText)
    Invoke-AutomationElement $currentPlanItem

    Wait-CourseAddStatus -Timeout 5000
    Assert-StatusOpenActionHidden
    Invoke-AutomationElement (Get-StatusBarCloseButton)
    Start-Sleep -Milliseconds 40
    if (-not (Test-MainWindowAutomationElementInTree StatusBar)) {
        throw "Manual StatusBar close collapsed immediately instead of playing its exit animation."
    }
    Save-WindowScreenScreenshot `
        -WindowHandle ([IntPtr]$script:mainWindowHwnd) `
        -FileName "requested-status-manual-exiting.png"
    Wait-MainWindowAutomationElementGone StatusBar -Timeout 1500

    Wait-MainWindowAutomationElementEnabled RegistrationOrderButton -Timeout 5000 | Out-Null
    Invoke-ResponsiveToolbarCommand RegistrationOrderButton
    $registrationWindowHwnd = Get-OwnedWinUiWindowHandle -Timeout 5000
    $resetButton = Get-ProcessAutomationElement RegistrationOrderResetSizeButton -Timeout 5000
    $resetBounds = $resetButton.Current.BoundingRectangle
    [SmokeNative]::SetCursorPos(
        [int][Math]::Round($resetBounds.X + ($resetBounds.Width / 2)),
        [int][Math]::Round($resetBounds.Y + ($resetBounds.Height / 2))) | Out-Null
    Start-Sleep -Milliseconds 300
    Save-WindowScreenScreenshot `
        -WindowHandle $registrationWindowHwnd `
        -FileName "registration-reset-disabled-transparent.png"
    Assert-DisabledTransparentButtonBackground `
        -Element $resetButton `
        -ScreenshotFileName "registration-reset-disabled-transparent-crop.png"

    Invoke-AutomationElement (Get-ProcessAutomationElement RegistrationOrderCloseButton -Timeout 5000)
    Start-Sleep -Milliseconds 700
    if (@(Get-VisibleProcessAutomationElements RegistrationOrderResetSizeButton).Count -ne 0) {
        throw "Registration-order tool window remained visible after its close animation."
    }
}

Test-UI "Planner screenshot" { Save-AppScreenshot "planner.png" }

Test-UI "Ctrl select one non-active plan tab" { Invoke-WhileCtrlHeld $secondPlanTabId }
Test-UI "Single Ctrl-selected tab keeps comparison disabled" { Assert-DisabledWhileCtrlHeld CompareButton }
Test-UI "Ctrl deselect one non-active plan tab" { Invoke-WhileCtrlHeld $secondPlanTabId }
Test-UI "Ctrl select comparison base plan tab" { Invoke-WhileCtrlHeld $firstPlanTabId }
Test-UI "Ctrl select comparison current plan tab" { Invoke-WhileCtrlHeld $secondPlanTabId }
Test-UI "Ctrl-selected tabs open ordered comparison" { Invoke-WhileCtrlHeld CompareButton }
Start-Sleep -Milliseconds 500
Test-UI "Ordered comparison title preserves base-to-current direction" { winapp ui wait-for WeekTitleText -a $AppPid -w $script:mainWindowHwnd --value "$firstPlanName → $secondPlanName" --contains -t 5000 }

Test-UI "Planner semester overview mode" { winapp ui invoke SemesterViewButton -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Milliseconds 500
Test-UI "Semester overview hides course library" { Wait-UiGone CourseSearchBox }
Test-UI "Semester overview hides detail pane" { Wait-UiGone CourseNameBox }
Test-UI "Semester overview hides week selector" { Wait-UiGone WeekNumberBox }
Test-UI "Semester overview week card visible" { winapp ui wait-for SemesterWeekCard2 -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Semester overview screenshot" { Save-AppScreenshot "planner-semester-overview.png" }

Test-UI "Semester overview week card opens week" {
    $invokeOutput = winapp ui invoke SemesterWeekCard2 -a $AppPid -w $script:mainWindowHwnd 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "SemesterWeekCard2: $invokeOutput"
    }

    Wait-AutomationNumericValue WeekNumberBox 2
}
Start-Sleep -Milliseconds 500
Test-UI "Week selector visible in week mode" { winapp ui wait-for WeekNumberBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Week title matches clicked semester week date range" { winapp ui wait-for WeekTitleText -a $AppPid -w $script:mainWindowHwnd --value $script:weekTwoTitleMarker --contains -t 5000 }
Test-UI "Week selector matches clicked semester week" { Wait-AutomationNumericValue WeekNumberBox 2 }
Test-UI "Course library restored in week mode" { winapp ui wait-for CourseSearchBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Week selector normalizes a fractional UIA value to an integral week" {
    Set-AutomationNumericValue WeekNumberBox 2.9
    Wait-NumberBoxVisibleText WeekNumberBox "2"
    Wait-AutomationNumericValue WeekNumberBox 2
}
Test-UI "Fractional week normalization preserves the selected week title" { winapp ui wait-for WeekTitleText -a $AppPid -w $script:mainWindowHwnd --value $script:weekTwoTitleMarker --contains -t 5000 }
Test-UI "Week selector restores the current week after empty input" {
    Set-NumberBoxTextAndCommit WeekNumberBox ""
    Wait-NumberBoxVisibleText WeekNumberBox "2"
    Wait-AutomationNumericValue WeekNumberBox 2
}
Test-UI "Empty week normalization preserves the selected week title" { winapp ui wait-for WeekTitleText -a $AppPid -w $script:mainWindowHwnd --value $script:weekTwoTitleMarker --contains -t 5000 }

Test-UI "Normalize comparison selection after semester overview" {
    Invoke-MainWindowAutomationElement $firstPlanTabId
    Start-Sleep -Milliseconds 250
}
Test-UI "Ctrl select comparison base plan after semester overview" { Invoke-WhileCtrlHeld $firstPlanTabId }
Test-UI "Ctrl select comparison current plan after semester overview" { Invoke-WhileCtrlHeld $secondPlanTabId }
Test-UI "Planner comparison mode" {
    Invoke-WhileCtrlHeld CompareButton
    Wait-AnyUI @("InlineSwapCompareButton")
}
Start-Sleep -Milliseconds 500
Test-UI "Comparison keeps unified week selector" { winapp ui wait-for WeekNumberBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Comparison hides course library" { Wait-UiGone CourseSearchBox }
Test-UI "Comparison hides detail pane" { Wait-UiGone CourseNameBox }
Test-UI "Comparison inline swap visible" { winapp ui wait-for InlineSwapCompareButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Comparison inline swap invokes" { winapp ui invoke InlineSwapCompareButton -a $AppPid -w $script:mainWindowHwnd }
Test-UI "Comparison inline swap reverses direction" { winapp ui wait-for WeekTitleText -a $AppPid -w $script:mainWindowHwnd --value "$secondPlanName → $firstPlanName" --contains -t 5000 }
Test-UI "Comparison screenshot" { Save-AppScreenshot "planner-comparison.png" }
Test-UI "Narrow planner resize" { Resize-AppWindow 620 720 }
Test-UI "Narrow planner timetable remains visible" { winapp ui wait-for TimetableScrollViewer -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Narrow planner screenshot" { Save-AppScreenshot "planner-narrow.png" }
Test-UI "Restore planner window size" { Resize-AppWindow 1600 900 }

Test-UI "Planner week mode after comparison" { winapp ui invoke WeekViewButton -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Milliseconds 500
Test-UI "Planner timetable visible after comparison" { winapp ui wait-for TimetableScrollViewer -a $AppPid -w $script:mainWindowHwnd -t 5000 }

Test-UI "Navigate to Semesters" { winapp ui invoke SemestersItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Plan tabs hidden on Semesters" { Wait-UiGone ShellPlanTabs }
Test-UI "App title visible on Semesters" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Semester editor visible" { winapp ui wait-for SemesterNameBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Semester add button aligns with selected row edge" { Assert-SemesterAddButtonVisibleEdgeAligned }
Test-UI "Period start picker visible" { winapp ui wait-for PeriodStartPicker -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Period end picker visible" { winapp ui wait-for PeriodEndPicker -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Save period button visible" { winapp ui wait-for SavePeriodButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Add period button visible" { winapp ui wait-for AddPeriodButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Period list visible" { winapp ui wait-for PeriodList -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Localized calendar picker opens calendar flyout" {
    Invoke-AnyUI @("StartDatePickerButton", "StartDatePicker")
    Wait-AnyUI @("CalendarJumpButton", $September2026ChineseText, $September2026EnglishText)
    Wait-AnyUI @("CalendarDate20260831")
    Wait-AnyUI @("CalendarDate20260907")
    Wait-AnyUI @("CalendarDate20260930")
    Wait-AnyUI @("CalendarDate20261001")
    winapp ui invoke CalendarJumpButton -a $AppPid | Out-Null
    Start-Sleep -Milliseconds 250
    Wait-AnyUI @("CalendarMonth9")
    winapp ui invoke CalendarJumpButton -a $AppPid | Out-Null
    Start-Sleep -Milliseconds 250
    Wait-AnyUI @("CalendarYear2026")
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, 0, [UIntPtr]::Zero)
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
}
Test-UI "Period list first row remains reachable and selectable" {
    Select-ListItemByDescendantText "08:00-08:45"
    Assert-UiElementOnScreen PeriodList
}
Test-UI "Period picker values remain fully readable at the current DPI" {
    Assert-CompactTimePickerDisplay PeriodStartPicker "08:00"
    Assert-CompactTimePickerDisplay PeriodEndPicker "08:45"
}
Test-UI "Compact period time picker supports keyboard entry and circular stepping" {
    Select-ListItemByDescendantText "08:00-08:45"
    Open-CompactTimePickerAutomationSurface PeriodStartPicker
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerMinuteWheel")
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "00"
    Send-FocusedDigitKey 3
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "03"
    Send-FocusedDigitKey 7
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "37"
    Assert-CompactTimePickerAutomationSurface
    Invoke-AutomationElement (Get-ProcessAutomationElement "TimePickerApplyButton")
    Wait-CompactTimePickerAutomationSurfaceGone
    Assert-CompactTimePickerDisplay PeriodStartPicker "08:37"

    Open-CompactTimePickerAutomationSurface PeriodStartPicker
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerHourWheel")
    Send-FocusedDigitKey 2
    Assert-FocusedTimePickerPartValue "TimePickerHourWheel" "02"
    Send-FocusedDigitKey 3
    Assert-FocusedTimePickerPartValue "TimePickerHourWheel" "23"
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerMinuteWheel")
    Send-FocusedDigitKey 5
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "05"
    Send-FocusedDigitKey 9
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "59"
    Send-FocusedVirtualKey 0x28
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "00"
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerHourWheel")
    Send-FocusedVirtualKey 0x28
    Assert-FocusedTimePickerPartValue "TimePickerHourWheel" "00"
    Assert-CompactTimePickerAutomationSurface
    Invoke-AutomationElement (Get-ProcessAutomationElement "TimePickerApplyButton")
    Wait-CompactTimePickerAutomationSurfaceGone
    Assert-CompactTimePickerDisplay PeriodStartPicker "00:00"

    Open-CompactTimePickerAutomationSurface PeriodStartPicker
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerHourWheel")
    Send-FocusedDigitKey 0
    Assert-FocusedTimePickerPartValue "TimePickerHourWheel" "00"
    Send-FocusedDigitKey 8
    Assert-FocusedTimePickerPartValue "TimePickerHourWheel" "08"
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerMinuteWheel")
    Send-FocusedDigitKey 0
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "00"
    Send-FocusedDigitKey 0
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "00"
    Assert-CompactTimePickerAutomationSurface
    Invoke-AutomationElement (Get-ProcessAutomationElement "TimePickerApplyButton")
    Wait-CompactTimePickerAutomationSurfaceGone
    Assert-CompactTimePickerDisplay PeriodStartPicker "08:00"
}
Test-UI "Compact period time picker Escape discards an uncommitted draft" {
    Open-CompactTimePickerAutomationSurface PeriodStartPicker
    Focus-AutomationElement (Get-ProcessAutomationElement "TimePickerMinuteWheel")
    Send-FocusedDigitKey 4
    Assert-FocusedTimePickerPartValue "TimePickerMinuteWheel" "04"
    Send-FocusedVirtualKey ([SmokeNative]::Escape)
    Wait-CompactTimePickerAutomationSurfaceGone
    Assert-CompactTimePickerDisplay PeriodStartPicker "08:00"
}
Test-UI "Semesters screenshot" { Save-AppScreenshot "semesters.png" }

Test-UI "Navigate to Course Library" { winapp ui invoke LibraryItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Plan tabs hidden on Course Library" { Wait-UiGone ShellPlanTabs }
Test-UI "App title visible on Course Library" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Course library command bar visible" { winapp ui wait-for LibraryManagerCommandBar -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Open independent library editor" { winapp ui invoke LibraryManagerNewCourseButton -a $AppPid -w $script:mainWindowHwnd }
Test-UI "Library editor save visible" { winapp ui wait-for LibraryEditorSaveButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Course library manager screenshot" { Save-AppScreenshot "course-library.png" }

Test-UI "Navigate to Plans" { winapp ui invoke PlansItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Plan tabs hidden on Plans" { Wait-UiGone ShellPlanTabs }
Test-UI "App title visible on Plans" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Plan manager command bar visible" { winapp ui wait-for PlanManagerCommandBar -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Plan manager screenshot" { Save-AppScreenshot "plans.png" }

Test-UI "Navigate to Labels" { winapp ui invoke LabelsItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Plan tabs hidden on Labels" { Wait-UiGone ShellPlanTabs }
Test-UI "App title visible on Labels" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Label list visible" { winapp ui wait-for LabelList -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Label editor visible" { winapp ui wait-for LabelNameBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Label reorder visible" { winapp ui wait-for MoveLabelUpButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Labels screenshot" { Save-AppScreenshot "labels.png" }

Test-UI "Navigate to Settings" { Invoke-AnyUI @("SettingsItem", "Settings", $SettingsChineseText) }
Start-Sleep -Seconds 1
Test-UI "Plan tabs hidden on Settings" { Wait-UiGone ShellPlanTabs }
Test-UI "App title visible on Settings" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Settings general content visible" { winapp ui wait-for LanguageBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Language selector visible" { winapp ui wait-for LanguageBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Theme selector visible" { winapp ui wait-for ThemeBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Closed combo boxes ignore mouse wheel selection" {
    winapp ui focus ThemeBox -a $AppPid -w $script:mainWindowHwnd | Out-Null
    $before = winapp ui get-value ThemeBox -a $AppPid -w $script:mainWindowHwnd 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$before")) {
        throw "ThemeBox before wheel value unavailable: $before"
    }

    Send-MouseWheelOver ThemeBox -Delta -120

    $after = winapp ui get-value ThemeBox -a $AppPid -w $script:mainWindowHwnd 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$after")) {
        throw "ThemeBox after wheel value unavailable: $after"
    }
    if ("$after" -ne "$before") {
        throw "Closed ThemeBox changed from '$before' to '$after' after mouse wheel."
    }
}
Test-UI "Licenses entry visible" { winapp ui wait-for ViewLicensesButton -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "Licenses dialog lists third party notices" {
    Resize-AppWindow 1600 900
    winapp ui invoke ViewLicensesButton -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Start-Sleep -Milliseconds 700
    Assert-AnySearchText @($ThirdPartyLicensesChineseText, $ThirdPartyLicensesEnglishText)
    Assert-SearchText "SkiaSharp"
    Assert-SearchText "https://github.com/Pal3love/dream-han-cjk"
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, 0, [UIntPtr]::Zero)
    [SmokeNative]::keybd_event([SmokeNative]::Escape, 0, [SmokeNative]::KeyUp, [UIntPtr]::Zero)
}
Test-UI "Switch language to English keeps text" { Select-ComboItem LanguageBox @($EnglishLanguageText) }
Test-UI "English language selector keeps value" { Assert-ComboValue LanguageBox $EnglishLanguageText }
Test-UI "English app title visible after language switch" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd --value "Course Planner" --contains -t 5000 }
Test-UI "Switch language back to Chinese keeps text" { Select-ComboItem LanguageBox @($SimplifiedChineseText, $ChineseLanguageText) }
Test-UI "Chinese language selector keeps value" { Assert-ComboValue LanguageBox $ChineseLanguageText }
Test-UI "Chinese app title visible after language switch" { winapp ui wait-for ShellAppTitleText -a $AppPid -w $script:mainWindowHwnd --value $ChineseAppTitleText --contains -t 5000 }
Test-UI "Settings screenshot" { Save-AppScreenshot "settings.png" }

Test-UI "Navigate back to Planner" { winapp ui invoke PlannerItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Seconds 1
Test-UI "Planner restored" { winapp ui wait-for ShellPlanTabs -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-UI "App brand hidden on Planner" { Wait-UiGone ShellAppBrand }

$summary = [pscustomobject]@{
    passed = $pass
    failed = $fail
    results = $results
}
$summary | ConvertTo-Json -Depth 4 | Out-File (Join-Path $OutputDirectory "ui-smoke-results.json") -Encoding utf8

Write-Host "Passed: $pass | Failed: $fail"
if ($fail -gt 0) {
    $results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
        Write-Host "FAIL: $($_.name) - $($_.detail)"
    }
    exit 1
}
