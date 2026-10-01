# When WinZ3805A installs but does not start

If `Install.cmd` finished and said **"WinZ3805A did not stay open"**, or the app
closes straight away with no window and no error, this page is for you. The
installer can see *that* the app stopped, but not *why*. A short script can.

The script starts WinZ3805A once, watches it for up to 20 seconds, and writes
what happened to a text file on your Desktop. **It changes nothing on your
machine.** It does not need administrator permission.

## 1. Download the script

**[Download Diagnose-Start.ps1](https://raw.githubusercontent.com/TGoodhew/WinZ3805A/main/build/Diagnose-Start.ps1)**

Your browser may show the script as text instead of downloading it. If it does,
right-click the link above and choose **Save link as…**, and save it to your
**Downloads** folder as `Diagnose-Start.ps1`.

Or download it from PowerShell instead. Open **Windows PowerShell** from the
Start menu and paste:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/TGoodhew/WinZ3805A/main/build/Diagnose-Start.ps1 -OutFile "$HOME\Downloads\Diagnose-Start.ps1"
```

You can [read the script on GitHub](../build/Diagnose-Start.ps1) before you run it.

## 2. Run it

1. Open **Windows PowerShell** from the Start menu. An ordinary window is
   right. Do not choose *Run as administrator*.
2. Paste these two lines and press **Enter**:

   ```powershell
   cd "$HOME\Downloads"
   powershell -ExecutionPolicy Bypass -File .\Diagnose-Start.ps1
   ```

   `-ExecutionPolicy Bypass` lets this one script run without changing your
   PowerShell settings.
3. Wait about 20 seconds. If WinZ3805A opens this time, close it. That is
   useful to know as well.

When it finishes, it prints **"Written to …"** with the file's location.

## 3. Send the result

The file is **`WinZ3805A-start-diagnosis.txt` on your Desktop**. Please attach it
to [issue #614](https://github.com/TGoodhew/WinZ3805A/issues/614), or send it to
whoever asked you to run this. If you also have the newest install log from
`%LOCALAPPDATA%\WinZ3805A Installer\logs`, send that too.

## What the file contains

- Your Windows version.
- The WinZ3805A and Windows App Runtime packages installed for your account,
  and which runtime WinZ3805A uses.
- How WinZ3805A ended: the **exit code** of its process. This is the one fact
  the installer cannot record.
- Anything Windows logged about installing or starting apps during those
  20 seconds.

It contains none of your files or settings. Some Windows log entries include
your account's security ID (a string beginning `S-1-5-21-`), which identifies
your account on that one PC. You can delete those lines before sending if you
prefer.
