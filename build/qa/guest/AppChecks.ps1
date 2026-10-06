# AppChecks.ps1 - runs inside a QA VM, on its signed-in desktop, with WinZ3805A installed and
# running (#633). Each check is one a person makes against the running application: manual-qa.md
# §11 (help in the installed package), §25 (a second launch brings a covered window forward) and
# §18 (the tray icon survives an Explorer restart). It prints one JSON line of results; the host
# reads it.
#
# Input is real: keystrokes go in through keybd_event and SendKeys, which is what a person's keyboard
# produces, because §25's whole subject is the foreground right Windows grants a launch the user
# made, and a launch started from a background script does not get it.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class QaWindows
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    public static uint ForegroundProcessId()
    {
        uint id;
        GetWindowThreadProcessId(GetForegroundWindow(), out id);
        return id;
    }

    // The titles of a process's visible top-level windows.
    public static string[] Titles(uint processId)
    {
        var titles = new List<string>();
        EnumWindows((window, parameter) =>
        {
            uint id;
            GetWindowThreadProcessId(window, out id);
            if (id == processId && IsWindowVisible(window))
            {
                var text = new StringBuilder(256);
                GetWindowText(window, text, 256);
                if (text.Length > 0) titles.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
'@

$results = New-Object System.Collections.Generic.List[object]
function Result([string]$Section, [string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results.Add([ordered]@{ section = $Section; name = $Name; ok = $Ok; detail = $Detail })
}
# Waits until the condition is truthy, up to the given seconds, and returns it: each check below waits
# on what it is about to read rather than on a clock the app can outrun (#633, 5 Oct 2026).
function Wait-For([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do { $v = & $Condition; if ($v) { return $v }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
    $null
}

function Get-ForegroundName {
    $id = [QaWindows]::ForegroundProcessId()
    try { (Get-Process -Id $id -ErrorAction Stop).ProcessName } catch { "process $id" }
}

$package = Get-AppxPackage -Name WinZ3805A | Select-Object -First 1
$appLog = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache\Local\WinZ3805A\logs\app.log"

if (-not (Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)) {
    Start-Process "shell:AppsFolder\$($package.PackageFamilyName)!App"
    $null = Wait-For { Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } } 45
    Start-Sleep -Seconds 2
}
$app = Get-Process -Name WinZ3805A | Select-Object -First 1

# --- §11: the guide and every image it names are in the installed package ------------------
$help = Join-Path $package.InstallLocation 'Help'
$guide = Join-Path $help 'how-to-use.md'
$images = @()
if (Test-Path $guide) {
    $images = @([regex]::Matches((Get-Content $guide -Raw), '!\[[^\]]*\]\((?<path>[^)\s]+)\)') | ForEach-Object { $_.Groups['path'].Value })
}
$missing = @($images | Where-Object { -not (Test-Path (Join-Path $help $_)) })
Result '11' 'the guide and every image it names are in the package' ((Test-Path $guide) -and $images.Count -gt 0 -and $missing.Count -eq 0) "$($images.Count) image(s), $($missing.Count) missing"

# --- §25: a second launch brings a covered window to the front ------------------------------
$notepad = Start-Process notepad -PassThru
Start-Sleep -Seconds 3
$before = Get-ForegroundName
[QaWindows]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)   # Windows key down
[QaWindows]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)   # and up: the Start menu opens
Start-Sleep -Seconds 2
[System.Windows.Forms.SendKeys]::SendWait('WinZ3805A')
# Enter only once the search is ready. On QA-Win10 the search host was still starting after the
# revert when a fixed four seconds ran out, so Enter went to "search the web" and Edge opened a Bing
# search for WinZ3805A instead (5 Oct 2026, twice).
#
# The results themselves cannot be read: they are drawn in a WebView2 pane that shows UI Automation
# no children from here, and Start's window is not among the desktop's children at all (measured on
# both VMs, 6 Oct 2026). The match that looked for "WinZ3805A ... App" there never matched once; its
# 20-second timeout was what made Enter wait. So the step waits on what can be seen: the focused
# search box holding the name, which means the search host is up, and then the search window's pixels
# holding still, which means the results have finished drawing. Up to 20 s for both; Enter goes in
# either way, as before, and the detail says what was seen and when.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
function Test-SearchHolds([string]$Text) {
    try {
        $focus = [System.Windows.Automation.AutomationElement]::FocusedElement
        $value = $focus.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        $owner = (Get-Process -Id $focus.Current.ProcessId).ProcessName
        ($owner -in 'SearchHost', 'SearchApp', 'SearchUI') -and $value -eq $Text
    }
    catch { $false }
}
function Get-ForegroundHash {
    try {
        $window = [System.Windows.Automation.AutomationElement]::FromHandle([QaWindows]::GetForegroundWindow())
        $r = $window.Current.BoundingRectangle
        if ($r.IsEmpty -or $r.Width -lt 50) { return '' }
        $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $g.Dispose()
        $stream = New-Object System.IO.MemoryStream; $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
        [BitConverter]::ToString([System.Security.Cryptography.SHA1]::Create().ComputeHash($stream.ToArray()))
    }
    catch { '' }
}
$searchStarted = Get-Date
$searchHeldName = [bool](Wait-For { Test-SearchHolds 'WinZ3805A' } 20)
$heldAfter = ((Get-Date) - $searchStarted).TotalSeconds
$previous = ''; $still = 0; $settled = $false
while (((Get-Date) - $searchStarted).TotalSeconds -lt 20) {
    Start-Sleep -Milliseconds 500
    $hash = Get-ForegroundHash
    if ($hash -and $hash -eq $previous) { $still++ } else { $still = 0 }
    $previous = $hash
    # Two unchanged frames, a second apart in all, after at least two seconds of looking.
    if ($still -ge 2 -and ((Get-Date) - $searchStarted).TotalSeconds -ge 2) { $settled = $true; break }
}
$settledAfter = ((Get-Date) - $searchStarted).TotalSeconds
$searchReady = "the search box held the name: $searchHeldName ($([int]$heldAfter) s); the results settled: $settled ($([int]$settledAfter) s)"
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
# The second copy hands over and exits; the first comes to the front.
$null = Wait-For { (Get-ForegroundName) -eq 'WinZ3805A' -and @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue).Count -eq 1 } 20
$after = Get-ForegroundName
$copies = @(Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue).Count
Result '25' 'a covered window comes to the front on a second launch' ($after -eq 'WinZ3805A') "foreground before: $before; after: $after; before Enter, $searchReady"
Result '25' 'one copy still running' ($copies -eq 1) "$copies running"
Result '25' 'the app logged taking the foreground' ((Get-Content $appLog -Raw) -match 'Brought to the front') ''
$notepad | Stop-Process -Force -ErrorAction SilentlyContinue

# --- §11: Ctrl+D and F1 open their windows, each with its own caption ------------------------
# Sent to whatever is in front, which is the app if §25 passed - so a §25 failure fails this too,
# and the detail says what was in front. F1 goes to the Details window, which also answers it.
# The captions are what the taskbar, Alt+Tab and Narrator name the windows by. Both reached Windows
# as the bare display name until #637, so the first version of this check counted windows instead.
$displayName = (Get-AppxPackageManifest $package).Package.Properties.DisplayName
$front = Get-ForegroundName
[System.Windows.Forms.SendKeys]::SendWait('^d')
$null = Wait-For { @([QaWindows]::Titles([uint32]$app.Id)) -contains "Receiver Details - $displayName" } 15
$titles = @([QaWindows]::Titles([uint32]$app.Id))
Result '11' 'Ctrl+D opens Details, captioned as Details' ($titles -contains "Receiver Details - $displayName") "Ctrl+D went to $front; app windows: $($titles -join ' / ')"

[System.Windows.Forms.SendKeys]::SendWait('{F1}')
$null = Wait-For { @([QaWindows]::Titles([uint32]$app.Id)) -contains "Help - $displayName" } 15
$titles = @([QaWindows]::Titles([uint32]$app.Id))
Result '11' 'F1 opens the guide, captioned as Help' ($titles -contains "Help - $displayName") "app windows: $($titles -join ' / ')"

# --- §18: the tray icon survives an Explorer restart ----------------------------------------
$lines = @(Get-Content $appLog).Count
Get-Process -Name explorer -ErrorAction SilentlyContinue | Stop-Process -Force
# Windows restarts Explorer itself; if it has not within 15 s, start it, then wait for the app's line.
if (-not (Wait-For { Get-Process -Name explorer -ErrorAction SilentlyContinue } 15)) { Start-Process explorer }
$null = Wait-For { ((@(Get-Content $appLog) | Select-Object -Skip $lines) -join "`n") -match 'Explorer restarted; adding the tray icon again' } 30
$new = (@(Get-Content $appLog) | Select-Object -Skip $lines) -join "`n"
Result '18' 'the tray icon is added again after an Explorer restart' ($new -match 'Explorer restarted; adding the tray icon again') ''
Result '18' 'the app survived the restart' ([bool](Get-Process -Name WinZ3805A -ErrorAction SilentlyContinue)) ''

$results | ConvertTo-Json -Depth 3 -Compress
