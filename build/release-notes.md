## Installing

Download **one** of the two zips below, unblock it, extract it, and double-click
**`Install.cmd`**:

- **`WinZ3805A-<version>-x64.zip`**: for a machine with an internet connection.
  If .NET 10 is not installed, the installer opens Microsoft's download page for it.
- **`WinZ3805A-<version>-x64-offline.zip`**: for a machine without one. It adds
  Microsoft's own .NET 10 Runtime installer, run when .NET 10 is missing.

> Unblock first: Windows marks anything downloaded from the internet, and the mark
> survives extraction. Right-click the **zip** → *Properties* → tick *Unblock* →
> *OK*, **before** extracting. If you skip it, Windows 11 asks whether to run
> `Install.cmd`, saying the publisher could not be verified; choose *Run* and the
> install carries on.

Both carry the signed package and the x64 Windows App Runtime.
The application carries **no .NET runtime of its own**. .NET is installed by
Microsoft's installer and kept patched by Microsoft Update, so a .NET security fix
reaches you from Microsoft without waiting for this project. Windows applies them
only when *Settings › Windows Update › Advanced options › Receive updates for
other Microsoft products* is on, and the installer tells you whether it is.

### Upgrading from 1.5.x or earlier

From **2.0.0** the package is signed through **Azure Artifact Signing** (#793), and the publisher
is the company's full verified name, `CN=The Schnauzer Group LLC, O=The Schnauzer Group LLC,
L=Renton, S=Washington, C=US`. Releases 1.3.1 to 1.5.x were self-signed as `CN=The Schnauzer
Group LLC` alone, and releases up to 1.3.0 under a misspelling of the company's name. The
publisher is part of a package's identity, so Windows treats this release as a **different
application** that cannot upgrade the old one. **`Install.cmd` handles it** (#590), for every
release from 1.0.1 on:

1. It asks you to exit the old copy if it is running (right-click its notification-area icon →
   **Exit**).
2. Before changing anything, it lists the old copy and the old certificates it will remove.
3. After installing this release, it saves the old copy's history, settings and logs to a
   *WinZ3805A earlier copy …* folder in **Documents**. It moves them into the new copy, then
   removes the old copy. The old certificates come out of *Trusted People* in one administrator
   prompt — nothing new goes in.

**On Windows 10 the order is different** (#617). Windows 10 cannot install the two side by side,
so the installer saves the old copy's data to Documents and removes the old copy *before*
installing this release, then moves the data in. If saving or removing fails, it stops before
installing anything.

If this release was already installed and has history of its own, that history is not
overwritten. Add the old history from **Settings → Import history…**, choosing `trend.db` in the
saved folder.

Later releases under the same publisher upgrade in place.

### If the install or the first start goes wrong

`Install.cmd` records every run in `%LOCALAPPDATA%\WinZ3805A Installer\logs`, one file per run.
The record covers what it found, what it removed or kept and why, and the state of the machine
before and after. The installer starts WinZ3805A once at the end, and the record says whether it
stayed open; if it didn't, it includes Windows' error entries for it. The installer prints the
path as it finishes. Please attach the file to any report.

**If WinZ3805A installs but does not open, restart Windows and start it from the Start menu.**
An upgrade with an earlier installer on Windows 10 could leave Windows unable to start it until a
restart (#614). If it still does not open,
[the start diagnosis](https://github.com/TGoodhew/WinZ3805A/blob/main/docs/diagnose-start.md)
records why, for a report.

If WinZ3805A stops starting later, **run the newest `Install.cmd` again**: it checks and puts right
what it installed, and keeps your data. From **1.3.4**, if the app itself is damaged, it installs it
again, saving your history and settings to Documents first and moving them back afterwards (#600).
Windows' *Settings › Apps › WinZ3805A › Advanced options ›
**Repair*** is also safe. **Reset** in the same place deletes your history and settings.

### About the signature

The package is signed by **The Schnauzer Group LLC** through Microsoft's **Azure Artifact
Signing**, whose certificate chains to a Microsoft root that Windows already trusts. There is
**no certificate to trust by hand** and no prompt for one: `Install.cmd` checks the signature
before changing anything, and asks for administrator permission only to install .NET or to remove
an earlier release's certificate.

A PC that has never been online may not yet have that Microsoft root. The installer says so, in
words, before changing anything; connecting the PC once fixes it.

### Requirements

- **Windows 10 version 22H2 (build 19045) or later, or Windows 11, x64.** That is the floor the
  package declares and the oldest Windows the release QA pass covers; the installer stops, and
  says why, on anything older. From **1.3.6** this was raised from Windows 10 1809.
- **.NET 10 Runtime**, free from Microsoft. The offline zip installs it if it is
  missing, and the online zip opens its [download page](https://dotnet.microsoft.com/download/dotnet/10.0).
- A serial port, or a USB-to-serial adapter, wired to the receiver
  (9600-8-N-1 for a Z3805A)

### Uninstalling

*Settings › Apps* removes the application and everything it stored. To keep the history, use
**Export history…** on its Settings page first.

Uninstalling leaves the installer's logs behind. To remove everything any
release has put on the machine, follow
[Removing WinZ3805A completely](https://github.com/TGoodhew/WinZ3805A/blob/main/docs/remove-winz3805a.md).
Its script saves your history to Documents first, and never removes the shared Windows App
Runtime or .NET.

## What this is

A WinUI 3 monitor and control application for HP/Symmetricom SmartClock
GPS-disciplined oscillators — the Z3805A and its siblings (Z3801A, 58503A/B,
59551A, Z3816A) — over RS-232, plus monitoring for any GPS receiver that speaks
NMEA 0183 and read-only monitoring of Symmetricom and Trimble UCCM telecom
modules.

Destructive receiver commands are **unreachable rather than warned about**: the
command catalog is an allowlist, and the excluded commands are not entries with
a flag — they do not exist as data.

New to it? [The user's guide](https://github.com/TGoodhew/WinZ3805A/blob/main/docs/how-to-use.md)
is also in the app under **Help** (`F1`).
