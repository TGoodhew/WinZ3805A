@echo off
rem ---------------------------------------------------------------------------
rem  WinZ3805A installer.
rem
rem  Double-clicked by someone who has never seen a developer tool, so it does
rem  as little as possible itself: it starts install.ps1 with an execution
rem  policy that will actually let it run, and it stays open afterwards so a
rem  failure is readable rather than a window that vanishes.
rem
rem  Deliberately NOT elevated here. Installing an app is a per-user operation,
rem  and elevating the whole script would install it for whichever administrator
rem  the UAC prompt authenticated - which on a shared machine is not the person
rem  who double-clicked it. install.ps1 elevates only the one step that needs it.
rem ---------------------------------------------------------------------------

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"

echo.
pause
