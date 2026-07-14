param(
    [Parameter(Mandatory = $true)]
    [int]$AppPid,

    [string]$OutputDirectory = "artifacts/theme-smoke"
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()
$mainWindowHwnd = $null

$LightThemeText = "$([char]0x6D45)$([char]0x8272)"
$DarkThemeText = "$([char]0x6DF1)$([char]0x8272)"
$SettingsText = "$([char]0x8BBE)$([char]0x7F6E)"

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-PlanTabAutomationId {
    param([Parameter(Mandatory = $true)][int]$Index)

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:mainWindowHwnd)
    $all = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $tabs = for ($elementIndex = 0; $elementIndex -lt $all.Count; $elementIndex++) {
        $element = $all.Item($elementIndex)
        if ($element.Current.AutomationId -match '^ShellPlanTab_.+$') {
            [pscustomobject]@{
                Position = [int]$element.GetCurrentPropertyValue(
                    [System.Windows.Automation.AutomationElementIdentifiers]::PositionInSetProperty)
                AutomationId = $element.Current.AutomationId
            }
        }
    }
    $ordered = @($tabs | Sort-Object Position)
    if ($Index -lt 0 -or $Index -ge $ordered.Count) {
        throw "Plan tab index $Index is unavailable; found $($ordered.Count) tabs."
    }
    $ordered[$Index].AutomationId
}

function Test-ThemeUI {
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

function Resolve-MainWindow {
    if ($script:mainWindowHwnd) {
        return
    }

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

    if ($windows -and $windows.Count -gt 0) {
        $mainWindow = $windows | Where-Object { $_.title -match "选课助手|Course Planner" } | Select-Object -First 1
        $script:mainWindowHwnd = $mainWindow.hwnd
    }

    if (-not $script:mainWindowHwnd) {
        throw "Could not resolve main window for process $AppPid."
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

function Save-AppScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    winapp ui screenshot RootNavigation -a $AppPid -w $script:mainWindowHwnd -o (Join-Path $OutputDirectory $FileName) | Out-Null
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

function Get-RegionAverageLuminance {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImagePath,

        [Parameter(Mandatory = $true)]
        [object]$Bounds
    )

    $bitmap = [System.Drawing.Bitmap]::new($ImagePath)
    try {
        $left = [Math]::Max(0, [int][Math]::Floor($Bounds.X + 8))
        $top = [Math]::Max(0, [int][Math]::Floor($Bounds.Y + 6))
        $right = [Math]::Min($bitmap.Width - 1, [int][Math]::Ceiling($Bounds.Right - 34))
        $bottom = [Math]::Min($bitmap.Height - 1, [int][Math]::Ceiling($Bounds.Bottom - 6))
        if ($right -le $left -or $bottom -le $top) {
            throw "Invalid sample bounds: left=$left top=$top right=$right bottom=$bottom image=$($bitmap.Width)x$($bitmap.Height)."
        }

        $total = 0.0
        $count = 0
        for ($y = $top; $y -le $bottom; $y += 6) {
            for ($x = $left; $x -le $right; $x += 6) {
                $color = $bitmap.GetPixel($x, $y)
                $total += (0.2126 * $color.R) + (0.7152 * $color.G) + (0.0722 * $color.B)
                $count++
            }
        }

        if ($count -eq 0) {
            throw "No pixels sampled."
        }

        return $total / $count
    }
    finally {
        $bitmap.Dispose()
    }
}

function New-RegionBounds {
    param(
        [Parameter(Mandatory = $true)]
        [double]$X,

        [Parameter(Mandatory = $true)]
        [double]$Y,

        [Parameter(Mandatory = $true)]
        [double]$Width,

        [Parameter(Mandatory = $true)]
        [double]$Height
    )

    [pscustomobject]@{
        X = $X
        Y = $Y
        Width = $Width
        Height = $Height
        Right = $X + $Width
        Bottom = $Y + $Height
    }
}

function Convert-ToRootScreenshotBounds {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Bounds,

        [Parameter(Mandatory = $true)]
        [object]$RootBounds
    )

    New-RegionBounds `
        -X ($Bounds.X - $RootBounds.X) `
        -Y ($Bounds.Y - $RootBounds.Y) `
        -Width $Bounds.Width `
        -Height $Bounds.Height
}

function Get-RegionAverageLuminanceExact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ImagePath,

        [Parameter(Mandatory = $true)]
        [object]$Bounds
    )

    $bitmap = [System.Drawing.Bitmap]::new($ImagePath)
    try {
        $left = [Math]::Max(0, [int][Math]::Floor($Bounds.X))
        $top = [Math]::Max(0, [int][Math]::Floor($Bounds.Y))
        $right = [Math]::Min($bitmap.Width - 1, [int][Math]::Ceiling($Bounds.Right))
        $bottom = [Math]::Min($bitmap.Height - 1, [int][Math]::Ceiling($Bounds.Bottom))
        if ($right -le $left -or $bottom -le $top) {
            throw "Invalid exact sample bounds: left=$left top=$top right=$right bottom=$bottom image=$($bitmap.Width)x$($bitmap.Height)."
        }

        $total = 0.0
        $count = 0
        for ($y = $top; $y -le $bottom; $y += 4) {
            for ($x = $left; $x -le $right; $x += 4) {
                $color = $bitmap.GetPixel($x, $y)
                $total += (0.2126 * $color.R) + (0.7152 * $color.G) + (0.0722 * $color.B)
                $count++
            }
        }

        if ($count -eq 0) {
            throw "No pixels sampled."
        }

        return $total / $count
    }
    finally {
        $bitmap.Dispose()
    }
}

function Assert-LightPlanTabsRefreshedBeforeHover {
    $fileName = Join-Path $OutputDirectory "planner-light-pre-hover-tab-refresh.png"
    winapp ui screenshot RootNavigation -a $AppPid -w $script:mainWindowHwnd --capture-screen -o $fileName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not capture light planner tab refresh screenshot."
    }

    $root = Get-UiBounds RootNavigation
    $tab = Convert-ToRootScreenshotBounds -Bounds (Get-UiBounds (Get-PlanTabAutomationId 0)) -RootBounds $root
    $luminance = Get-RegionAverageLuminance $fileName $tab
    if ($luminance -lt 150) {
        throw "Light plan tab average luminance $([Math]::Round($luminance, 1)) is too dark before hover; dynamic tab brushes were not refreshed."
    }
}

function Assert-DarkShellChromeHasNoLightLeak {
    $fileName = Join-Path $OutputDirectory "planner-dark-pre-hover-shell-chrome.png"
    winapp ui screenshot RootNavigation -a $AppPid -w $script:mainWindowHwnd --capture-screen -o $fileName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not capture dark shell chrome screenshot."
    }

    $root = Get-UiBounds RootNavigation
    $tabs = Get-UiBounds ShellPlanTabs
    $tabRailBounds = New-RegionBounds -X ($tabs.Right - $root.X - 72) -Y ($tabs.Y - $root.Y + 8) -Width 48 -Height ($tabs.Height - 16)
    $captionBounds = New-RegionBounds -X ($root.Width - 120) -Y 8 -Width 84 -Height 24

    $tabRailLuminance = Get-RegionAverageLuminanceExact $fileName $tabRailBounds
    if ($tabRailLuminance -gt 95) {
        throw "Dark tab rail average luminance $([Math]::Round($tabRailLuminance, 1)) is too light before hover; tab rail surface leaked a light brush."
    }

    $captionLuminance = Get-RegionAverageLuminanceExact $fileName $captionBounds
    if ($captionLuminance -gt 120) {
        throw "Dark title bar caption area average luminance $([Math]::Round($captionLuminance, 1)) is too light before hover; native caption buttons leaked the system light brush."
    }
}

function Save-InactivePlanTabHoverScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    $inactiveTabId = Get-PlanTabAutomationId 1
    winapp ui hover $inactiveTabId -a $AppPid -w $script:mainWindowHwnd --dwell-time 500 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not hover inactive plan tab $inactiveTabId."
    }

    Save-AppScreenshot $FileName
}

function Select-Theme {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ThemeText
    )

    winapp ui invoke ThemeBox -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Start-Sleep -Milliseconds 300

    $inspect = winapp ui inspect ThemeBox -a $AppPid -w $script:mainWindowHwnd --depth 5 2>&1
    $pattern = '^\s*(\S+)\s+ListItem\s+"' + [regex]::Escape($ThemeText) + '"'
    $selector = $null
    foreach ($line in $inspect) {
        if ($line -match $pattern) {
            $selector = $Matches[1]
            break
        }
    }

    if (-not $selector) {
        throw "Could not find theme item '$ThemeText'. Inspect output: $inspect"
    }

    winapp ui invoke $selector -a $AppPid -w $script:mainWindowHwnd | Out-Null
    Start-Sleep -Milliseconds 800

    $value = winapp ui get-value ThemeBox -a $AppPid -w $script:mainWindowHwnd 2>&1
    if ($LASTEXITCODE -ne 0 -or "$value" -notlike "*$ThemeText*") {
        throw "ThemeBox value '$value' did not match '$ThemeText'."
    }
}

function Wait-PlannerDynamicSurfaces {
    winapp ui wait-for ShellPlanTabs -a $AppPid -w $script:mainWindowHwnd -t 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ShellPlanTabs was not visible." }

    winapp ui wait-for LibraryTree -a $AppPid -w $script:mainWindowHwnd -t 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "LibraryTree was not visible." }

    winapp ui wait-for TimetableScrollViewer -a $AppPid -w $script:mainWindowHwnd -t 5000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "TimetableScrollViewer was not visible." }
}

Test-ThemeUI "Resolve main window" { Resolve-MainWindow }
Test-ThemeUI "Navigate to Settings" { Invoke-AnyUI @("SettingsItem", "Settings", $SettingsText) }
Start-Sleep -Milliseconds 700
Test-ThemeUI "Theme selector visible" { winapp ui wait-for ThemeBox -a $AppPid -w $script:mainWindowHwnd -t 5000 }
Test-ThemeUI "Select light theme without restart" { Select-Theme $LightThemeText }
Test-ThemeUI "Light settings screenshot" { Save-AppScreenshot "settings-light.png" }

Test-ThemeUI "Navigate to Planner after light theme" { winapp ui invoke PlannerItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Milliseconds 700
Test-ThemeUI "Light planner dynamic surfaces visible" { Wait-PlannerDynamicSurfaces }
Test-ThemeUI "Light plan tabs refresh before hover" { Assert-LightPlanTabsRefreshedBeforeHover }
Test-ThemeUI "Light planner screenshot" { Save-AppScreenshot "planner-light.png" }
Test-ThemeUI "Light inactive plan tab hover screenshot" { Save-InactivePlanTabHoverScreenshot "planner-light-inactive-tab-hover.png" }

Test-ThemeUI "Navigate to Settings for dark theme" { Invoke-AnyUI @("SettingsItem", "Settings", $SettingsText) }
Start-Sleep -Milliseconds 700
Test-ThemeUI "Select dark theme without restart" { Select-Theme $DarkThemeText }
Test-ThemeUI "Dark settings screenshot" { Save-AppScreenshot "settings-dark.png" }

Test-ThemeUI "Navigate to Planner after dark theme" { winapp ui invoke PlannerItem -a $AppPid -w $script:mainWindowHwnd }
Start-Sleep -Milliseconds 700
Test-ThemeUI "Dark planner dynamic surfaces visible" { Wait-PlannerDynamicSurfaces }
Test-ThemeUI "Dark shell chrome has no light leak before hover" { Assert-DarkShellChromeHasNoLightLeak }
Test-ThemeUI "Dark planner screenshot" { Save-AppScreenshot "planner-dark.png" }
Test-ThemeUI "Dark inactive plan tab hover screenshot" { Save-InactivePlanTabHoverScreenshot "planner-dark-inactive-tab-hover.png" }

$summary = [pscustomobject]@{
    passed = $pass
    failed = $fail
    results = $results
}
$summary | ConvertTo-Json -Depth 4 | Out-File (Join-Path $OutputDirectory "theme-smoke-results.json") -Encoding utf8

Write-Host "Passed: $pass | Failed: $fail"
if ($fail -gt 0) {
    $results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
        Write-Host "FAIL: $($_.name) - $($_.detail)"
    }
    exit 1
}
