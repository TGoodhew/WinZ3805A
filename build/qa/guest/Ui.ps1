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

# What UI Automation does not say: whether Windows keeps a window above others, which window is on
# top at a point, and input that Windows treats as a person's. SetCursorPos alone moves the pointer
# without a pointer event, so no tooltip opens (manual-qa.md section 22); mouse_event does.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QaWin32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static bool IsTopmost(IntPtr hWnd) { return (GetWindowLong(hWnd, -20) & 0x8) != 0; }
    public static RECT Rect(IntPtr hWnd) { RECT r; GetWindowRect(hWnd, out r); return r; }
    public static IntPtr TopAt(int x, int y) { POINT p; p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2); }

    // Moves the pointer there in small steps, so the window under it sees it arrive.
    public static void MoveTo(int x, int y)
    {
        POINT now; GetCursorPos(out now);
        for (int i = 1; i <= 10; i++)
        {
            SetCursorPos(now.X + (x - now.X) * i / 10, now.Y + (y - now.Y) * i / 10);
            mouse_event(0x0001, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(15);
        }
    }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    public static void RightClick() { mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); }
    public static void LeftUp() { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
}
'@

# The notification-area menu (#568). UI Automation cannot see a Win32 popup menu here, which is why
# this was a person's check until 3 Oct 2026; Win32 can. The menu is opened the way a right-click on
# the icon opens it: the icon's callback message, posted to the app's hidden tray window, which then
# runs its own handler. What that leaves out is only the shell routing a click to the icon. Items
# are read from the menu itself, ticks included, and chosen with a real click, because keyboard
# focus on a popup menu is not reliable from here - a key chose an item once and missed the next.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class QaTray
{
    delegate bool EnumProc(IntPtr w, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr w, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr w, uint m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern uint GetMenuState(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr menu, uint item, StringBuilder s, int n, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetMenuItemRect(IntPtr w, IntPtr menu, uint item, out RECT r);

    // The app's tray window: its class is WinZ3805A.TrayIcon.<guid>, one per icon (TrayIconWindow).
    public static IntPtr Window(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((w, p) => {
            uint owner; GetWindowThreadProcessId(w, out owner);
            var s = new StringBuilder(256); GetClassName(w, s, 256);
            if (owner == pid && s.ToString().StartsWith("WinZ3805A.TrayIcon.")) { found = w; return false; }
            return true; }, IntPtr.Zero);
        return found;
    }

    // As a right-click on the icon: WM_USER + 1 with WM_RBUTTONUP, TrayIcon's callback message.
    public static void Open(IntPtr trayWindow) { PostMessage(trayWindow, 0x0401, IntPtr.Zero, (IntPtr)0x0205); }

    static IntPtr Menu()
    {
        IntPtr popup = FindWindow("#32768", null);
        return popup == IntPtr.Zero ? IntPtr.Zero : SendMessage(popup, 0x01E1, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool IsOpen() { return FindWindow("#32768", null) != IntPtr.Zero; }

    // Each item of the open menu, its accelerator marks removed: "Keep above other windows [ticked]".
    public static string[] Items()
    {
        IntPtr menu = Menu();
        var list = new List<string>();
        if (menu == IntPtr.Zero) return list.ToArray();
        for (uint i = 0; i < GetMenuItemCount(menu); i++)
        {
            uint state = GetMenuState(menu, i, 0x400);
            var s = new StringBuilder(128); GetMenuString(menu, i, s, 128, 0x400);
            list.Add(((state & 0x800) != 0 ? "---" : s.ToString().Replace("&", "")) + ((state & 0x8) != 0 ? " [ticked]" : ""));
        }
        return list.ToArray();
    }

    // The centre of an item of the open menu, by position; empty when there is no menu.
    public static int[] Centre(uint index)
    {
        IntPtr menu = Menu();
        RECT r;
        if (menu == IntPtr.Zero || !GetMenuItemRect(IntPtr.Zero, menu, index, out r)) return new int[0];
        return new int[] { (r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2 };
    }
}
'@

# Opens the tray menu, returns its items, and clicks the one named, if any; then waits for it to act.
function Use-TrayMenu {
    param([string]$Choose)
    $process = Get-Process -Name WinZ3805A | Select-Object -First 1
    $tray = [QaTray]::Window([uint32]$process.Id)
    if ($tray -eq [IntPtr]::Zero) { return [pscustomobject]@{ Items = @(); Chosen = $false } }
    [QaTray]::Open($tray)
    Start-Sleep -Seconds 2
    $items = @([QaTray]::Items())
    $chosen = $false
    $index = [Array]::FindIndex([string[]]$items, [Predicate[string]]{ param($i) $i -like "$Choose*" })
    if ($Choose -and $index -ge 0) {
        $c = [QaTray]::Centre([uint32]$index)
        if ($c.Count -eq 2) { [QaWin32]::MoveTo($c[0], $c[1]); [QaWin32]::LeftDown(); [QaWin32]::LeftUp(); $chosen = $true }
        Start-Sleep -Seconds 2
    }
    elseif ([QaTray]::IsOpen()) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
    [pscustomobject]@{ Items = $items; Chosen = $chosen }
}

# An element's on-screen rectangle as left, top, width, height in physical pixels.
function Get-Bounds {
    param($Element)
    $r = $Element.Current.BoundingRectangle
    [pscustomobject]@{ Left = [int]$r.Left; Top = [int]$r.Top; Width = [int]$r.Width; Height = [int]$r.Height; Right = [int]$r.Right; Bottom = [int]$r.Bottom }
}

# A window handle as UI Automation knows it.
function Get-Handle { param($Element) [IntPtr]$Element.Current.NativeWindowHandle }

# The toggle state of a control that has one: On, Off or Indeterminate.
function Get-ToggleState {
    param($Element)
    "$($Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)"
}


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
