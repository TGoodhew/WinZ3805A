# Removing WinZ3805A completely

*Settings › Apps* removes the app, but it leaves behind the certificate the app
was signed with, the installer's logs, and any copies of your data the installer
saved. This page gives you a script that removes **everything any WinZ3805A
installer has put on your PC, from the first release to the current one**, and
explains each thing it does.

Use it when you want a clean slate: before installing again from scratch, after
an upgrade that went wrong, or because you have stopped using WinZ3805A.

## What it removes, what it asks about, and what it leaves

| | What | Where it lives |
|---|---|---|
| **Removes** | Every copy of WinZ3805A installed for your Windows account, whichever release it came from | Windows' list of installed apps |
| **Removes** | With each copy, everything it stored: your history (`trend.db`), settings, remembered connection and logs | Inside the app's own folder under `%LOCALAPPDATA%\Packages` |
| **Removes** | The certificates WinZ3805A releases were signed with | *Trusted People* (and *Trusted Root*, if one was put there), for the whole PC and for your account |
| **Removes** | The installer's logs and settings | `%LOCALAPPDATA%\WinZ3805A Installer` |
| **Asks first** | Whether to save your history and settings to Documents before removing them. **The answer is yes unless you say no.** | Saved as `Documents\WinZ3805A saved data <version> <date>` |
| **Asks first** | Whether to delete the copies the installer saved when it replaced an older WinZ3805A. **The answer is no unless you say yes.** | `Documents\WinZ3805A earlier copy <version> <date>` |
| **Leaves** | The Windows App Runtime and .NET 10. Other apps use them too; see [Shared components](#shared-components) | |
| **Leaves** | A copy of WinZ3805A from the Microsoft Store, or in another Windows account | |
| **Leaves** | The zip you downloaded, the folder you extracted it to, and any history you exported yourself | Wherever you put them |

It asks for administrator permission **once**, and only to remove certificates
from the PC-wide store. Everything else runs as you.

## 1. Download the script

**[Download Remove-WinZ3805A.ps1](https://raw.githubusercontent.com/TGoodhew/WinZ3805A/main/build/Remove-WinZ3805A.ps1)**

If your browser shows the script as text instead of downloading it, right-click
the link and choose **Save link as…**, and save it to your **Downloads** folder.

Or download it from PowerShell. Open **Windows PowerShell** from the Start menu
and paste:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri https://raw.githubusercontent.com/TGoodhew/WinZ3805A/main/build/Remove-WinZ3805A.ps1 -OutFile "$HOME\Downloads\Remove-WinZ3805A.ps1"
```

You can [read the script on GitHub](../build/Remove-WinZ3805A.ps1) before running it.

## 2. See what it would do (optional)

This lists everything it would remove and keep, and changes nothing. Open
**Windows PowerShell** (an ordinary window, not *Run as administrator*) and paste:

```powershell
cd "$HOME\Downloads"
powershell -ExecutionPolicy Bypass -File .\Remove-WinZ3805A.ps1 -ListOnly
```

`-ExecutionPolicy Bypass` lets this one script run without changing your
PowerShell settings.

## 3. Run it

In the same kind of window:

```powershell
cd "$HOME\Downloads"
powershell -ExecutionPolicy Bypass -File .\Remove-WinZ3805A.ps1
```

## What you will see, step by step

**It looks before it changes anything.** It finds:

- every WinZ3805A for your account, including copies from older releases signed
  under an earlier name, which Windows treats as separate apps;
- each copy's data, and how big it is;
- a WinZ3805A data folder outside any app, which no release since 1.0.1 creates,
  but which is looked for anyway;
- certificates the releases were signed with. It recognises them by their exact
  thumbprints, or by the publisher's name, *The Schnauzer Group LLC*, so it never
  mistakes another company's certificate for ours.
  Some very early test builds used a generic placeholder name that other apps
  can use too. A certificate with that name is removed **only** if a WinZ3805A
  signed with it is installed; otherwise it is listed and left alone;
- the installer's own folder, and any saved copies in Documents;
- the shared components, with which other apps on your account depend on them.

**It shows two lists, *This will remove* and *This will keep*,** and nothing has
changed yet. With `-ListOnly` it stops here.

**If WinZ3805A is running, it asks you to close it** (right-click its icon by the
clock and choose **Exit**), and waits.

**It asks whether to save your history and settings.** Press **Enter** to save
them, which is what you want unless you are sure you will never need that
history again. Type **n** and Enter to skip saving.

**If the installer saved copies in Documents earlier, it asks whether to delete
them too.** Press **Enter** to keep them. Type **y** and Enter to delete them.

**It asks you to press Enter to go ahead.** Close the window instead to stop,
and nothing will have changed.

**It saves your data first.** If saving fails, for example because Documents is
full, it stops before removing anything.

**Windows asks once for administrator permission,** if a certificate is trusted
for the whole PC. If you decline, the app and everything else are still removed,
and the script tells you the certificate is still trusted.

**It removes each copy of WinZ3805A.** Windows deletes each copy's data with it.
Then it removes anything Windows left behind, certificates in your own account's
store, and the installer's folder.

**It checks again** and lists anything still there. That is normally nothing.
Something that is in use can stay until Windows restarts.

**It ends by telling you:**

- where your history was saved, if you saved it;
- to **restart Windows before installing WinZ3805A again**. After an upgrade
  that went wrong, Windows can hold on to the old state until it restarts;
- where the record of the run is.

## Afterwards

- **To install again:** restart Windows first, then run `Install.cmd` from the
  release you want.
- **To bring your history back:** after installing, open **Settings › Import
  history…** in WinZ3805A and choose `trend.db` in the
  `WinZ3805A saved data …` folder in Documents.
- **The record of the run** is on your Desktop as
  `WinZ3805A-removal-<date>-<time>.log`. If something was left behind, please
  attach it to a new issue on
  [GitHub](https://github.com/TGoodhew/WinZ3805A/issues).

## Shared components

The script never removes these, because other apps use them. It lists them so
you know they are there.

**The Windows App Runtime** (`Microsoft.WindowsAppRuntime.2`, with
`WinAppRuntime.Main.2` and `WinAppRuntime.Singleton`) runs many modern Windows
apps. On Windows 10, Photos installs it from the Microsoft Store, and Paint and
Notepad use it on Windows 11. The script's list shows, for each one, whether it
came from the Store and what else on your account uses it.

Leave it installed unless the list says it was **installed outside the Store**
and **nothing else on this account declares it**. If both are true, you can
remove the WinZ3805A installer's copy in Windows PowerShell:

```powershell
Get-AppxPackage Microsoft.WindowsAppRuntime.2 | Where-Object SignatureKind -ne 'Store' | Remove-AppxPackage
```

If you are not sure, leave it: it takes little space, and an app that needs it
will not start without it. Leave `WinAppRuntime.Main.2` and
`WinAppRuntime.Singleton` in place either way, because apps use them without
declaring it.

**.NET 10** runs any app built on it, not only WinZ3805A. If you installed it
for WinZ3805A and nothing else needs it, remove it in **Settings › Apps**. It is
listed as *Microsoft .NET Runtime - 10.0.x (x64)*, and the Desktop Runtime as
*Microsoft Windows Desktop Runtime - 10.0.x (x64)*.

## Other Windows accounts

Each Windows account has its own copy of WinZ3805A and its own data. The script
only removes yours. To remove another account's copy, sign in to that account
and run the script there. Certificates trusted for the whole PC are removed by
the first run, for everyone. A certificate someone added to their own account's
store is removed when that account runs it.
