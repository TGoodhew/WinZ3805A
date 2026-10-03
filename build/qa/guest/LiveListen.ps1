# Records every UI Automation live-region event raised by WinZ3805A, for A11Y-9 (#633): the time,
# the element's text and its live setting. The managed client Windows PowerShell ships predates live
# regions and knows neither the event nor the property, so this declares the native COM interface
# itself, as far as the methods it calls. Runs for $Seconds, writing one line per event to $Out.
param([int]$Seconds = 180, [string]$Out = 'C:\qa\live-events.txt')
Add-Type -TypeDefinition @'
using System;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElement
{
    void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache();
    void FindAllBuildCache(); void BuildUpdatedCache();
    [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
}

[ComImport, Guid("146c3c17-f12e-4e22-8c27-f894b9b79c69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationEventHandler
{
    void HandleAutomationEvent(IUIAutomationElement sender, int eventId);
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    void CompareElements(); void CompareRuntimeIds();
    IUIAutomationElement GetRootElement();
    void ElementFromHandle(); void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
    void ElementFromHandleBuildCache(); void ElementFromPointBuildCache(); void GetFocusedElementBuildCache();
    void CreateTreeWalker(); void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker();
    void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition(); void CreateCacheRequest();
    void CreateTrueCondition(); void CreateFalseCondition(); void CreatePropertyCondition(); void CreatePropertyConditionEx();
    void CreateAndCondition(); void CreateAndConditionFromArray(); void CreateAndConditionFromNativeArray();
    void CreateOrCondition(); void CreateOrConditionFromArray(); void CreateOrConditionFromNativeArray(); void CreateNotCondition();
    void AddAutomationEventHandler(int eventId, IUIAutomationElement element, int scope, IntPtr cacheRequest, IUIAutomationEventHandler handler);
}

[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
public class CUIAutomation { }

[ComVisible(true)]
public class LiveListener : IUIAutomationEventHandler
{
    public readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
    readonly int _pid;
    public LiveListener(int pid) { _pid = pid; }

    public void HandleAutomationEvent(IUIAutomationElement sender, int eventId)
    {
        try
        {
            object pid = sender.GetCurrentPropertyValue(30002);
            if (pid is int && (int)pid != _pid) return;
            object name = sender.GetCurrentPropertyValue(30005);
            object live = sender.GetCurrentPropertyValue(30135);
            object id = sender.GetCurrentPropertyValue(30011);
            string setting = (live is int) ? ((int)live == 2 ? "Assertive" : (int)live == 1 ? "Polite" : "Off") : "?";
            Events.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + setting + "\t" + id + "\t" + name);
        }
        catch (Exception e) { Events.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "\terror\t\t" + e.Message); }
    }

    // Registered from a thread of its own in the multithreaded apartment, as UI Automation asks of
    // a client that handles events, and kept alive for as long as it listens.
    public void Listen(int milliseconds)
    {
        var t = new Thread(() => {
            var automation = (IUIAutomation)new CUIAutomation();
            automation.AddAutomationEventHandler(20024, automation.GetRootElement(), 7, IntPtr.Zero, this);
            Thread.Sleep(milliseconds);
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.IsBackground = true;
        t.Start();
    }
}
'@
# Its own console hidden, so it cannot cover the app while the scenario drives it (see Ui.ps1).
Add-Type -Name ConsoleWindow -Namespace QaLive -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
[void][QaLive.ConsoleWindow]::ShowWindow([QaLive.ConsoleWindow]::GetConsoleWindow(), 0)
$app = Get-Process -Name WinZ3805A | Select-Object -First 1
$listener = New-Object LiveListener($app.Id)
$listener.Listen($Seconds * 1000)
"listening to process $($app.Id) for $Seconds s" | Set-Content $Out
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $line = $null
    while ($listener.Events.TryDequeue([ref]$line)) { Add-Content -LiteralPath $Out -Value $line }
    Start-Sleep -Milliseconds 500
}
$line = $null
while ($listener.Events.TryDequeue([ref]$line)) { Add-Content -LiteralPath $Out -Value $line }
'done' | Add-Content -LiteralPath $Out
