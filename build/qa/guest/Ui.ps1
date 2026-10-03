# UI Automation helpers for guest scripts (#633). Prepended to a scenario's guest script, never run
# alone. Windows PowerShell 5.1 ships the managed UI Automation client, so nothing is installed:
# WinUI 3 exposes its controls to it like any other framework.
#
# Controls are found under the app's own top-level window, by AutomationId where the control has a
# stable one and by name otherwise. A ContentDialog's buttons are its template's PrimaryButton and
# CloseButton, which is what tells the dialog's Connect from the footer's Connect beside it.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$script:Ae = [System.Windows.Automation.AutomationElement]

function Get-AppWindow {
    param([int]$Seconds = 30)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        foreach ($process in @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)) {
            # The process's own main-window handle first: one call, and no walk of every window on
            # the desktop. The main window is the one named exactly as the package's display name;
            # Details and Help carry a prefix since #637, so a handle naming one of them is passed by.
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                try {
                    $window = $script:Ae::FromHandle($process.MainWindowHandle)
                    if ($window -and $window.Current.Name -notmatch ' - ') { return $window }
                }
                catch { }
            }
            $condition = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ProcessIdProperty, $process.Id)
            try {
                foreach ($window in $script:Ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
                    if ($window.Current.Name -notmatch ' - ') { return $window }
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $null
}

# What was there when a window was not found: every WinZ3805A process and its windows' titles.
function Get-WindowReport {
    @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | ForEach-Object {
        "process $($_.Id) started $($_.StartTime.ToString('HH:mm:ss')) main window '$($_.MainWindowTitle)'"
    }) -join '; '
}

function Find-Control {
    param($Root, [string]$AutomationId, [string]$Name, [int]$Seconds = 10)
    $property = if ($AutomationId) { $script:Ae::AutomationIdProperty } else { $script:Ae::NameProperty }
    $value = if ($AutomationId) { $AutomationId } else { $Name }
    $condition = New-Object System.Windows.Automation.PropertyCondition($property, $value)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $hit = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    $null
}

function Invoke-Control {
    param($Element)
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# What a selection control has selected now, by name.
function Get-Selection {
    param($Element)
    @($Element.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() |
        ForEach-Object { $_.Current.Name }) -join ','
}

# Chooses an item of a ComboBox by its text, and returns whether the box then shows it. The list is
# opened first, because its items exist to UI Automation only while it is: they live in a popup,
# found from the desktop rather than under the box. More than one element can carry the text - the
# item's own TextBlock, and an item of a list that has just closed - so only an on-screen list item
# is tried, and the box's own selection is the judge, not a call that returned without throwing.
function Select-ComboItem {
    param($Box, [string]$Name, [int]$Attempts = 6)
    $expand = $Box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($script:Ae::NameProperty, $Name)))
    for ($i = 0; $i -lt $Attempts -and (Get-Selection $Box) -ne $Name; $i++) {
        try { $expand.Expand() } catch { }
        Start-Sleep -Milliseconds 700
        foreach ($item in $script:Ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($item.Current.IsOffscreen) { continue }
            try { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    try { if ($expand.Current.ExpandCollapseState -ne 'Collapsed') { $expand.Collapse() } } catch { }
    (Get-Selection $Box) -eq $Name
}

# A navigation item of a window's NavigationView, by its text: a list item, so that a button of the
# same name elsewhere - the Details title bar has a Settings button too - is never the one chosen.
function Select-NavigationItem {
    param($Window, [string]$Name)
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($script:Ae::NameProperty, $Name)))
    $item = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $item) { return $false }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $true
}

# The app's window whose title starts with this, such as 'Receiver Details'.
function Get-AppWindowNamed {
    param([string]$Prefix, [int]$Seconds = 15)
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        foreach ($process in @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)) {
            $condition = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ProcessIdProperty, $process.Id)
            foreach ($window in $script:Ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)) {
                if ($window.Current.Name -like "$Prefix*") { return $window }
            }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $null
}

# Every button under the window, for a failure's detail: what was there instead.
function Get-ButtonList {
    param($Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition($script:Ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    @($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        ForEach-Object { "$($_.Current.Name)[$($_.Current.AutomationId)]" }) -join ', '
}

# A key, sent where the keyboard focus is; focus is put on the element first.
function Send-KeyTo {
    param($Element, [string]$Keys)
    try { $Element.SetFocus() } catch { }
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

$script:AppLog = Join-Path $env:LOCALAPPDATA "Packages\$((Get-AppxPackage -Name WinZ3805A | Select-Object -First 1).PackageFamilyName)\LocalCache\Local\WinZ3805A\logs\app.log"

function Get-AppLogLines { @(if (Test-Path $script:AppLog) { Get-Content $script:AppLog }) }

# A log line's own time, from its first 23 characters ("2026-10-02 18:01:57.947").
function Get-LineTime {
    param([string]$Line)
    [datetime]::ParseExact($Line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
}
