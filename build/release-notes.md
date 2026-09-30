## Installing

Download **`WinZ3805A-<version>-x64.zip`** below, unblock it, extract it, and
double-click **`Install.cmd`**.

> Unblocking matters: Windows marks anything downloaded from the internet, and
> the mark survives extraction. Right-click the **zip** → *Properties* → tick
> *Unblock* → *OK*, **before** extracting. Skipping it makes the installer fail
> in ways that do not mention the mark.

The zip carries everything the install needs — the signed package with its own
.NET runtime, its certificate, and the x64 Windows App Runtime — so a bench
machine with no internet connection and no Visual Studio can install and run it.
Nothing needs downloading first, .NET included.

### Upgrading from 1.3.0 or earlier

From **1.3.1** the package is signed as **The Schnauzer Group LLC**. Earlier releases were signed
under a misspelling of the company's name. The publisher is part of a package's identity, so
Windows treats this release as a **different application**: it installs **alongside** the old
one rather than over it, and it starts with no history. To move across:

1. In the old copy, **Settings → Export history…**, and save the file.
2. Exit the old copy: right-click its notification-area icon → **Exit**. Two copies would compete
   for the same serial port.
3. Install this release, then **Settings → Import history…** the file.
4. Uninstall the old copy in *Settings › Apps*. Both are called WinZ3805A. The old one is the copy
   whose publisher is **not** The Schnauzer Group LLC. `build/Uninstall-Sideload.ps1` removes both
   copies and both certificates.

This happens once. Later releases upgrade in place again.

### About the certificate prompt

The package is signed with a **self-signed certificate**, so `Install.cmd` asks
once for administrator permission to add it to the *Trusted People* store. That
is the only thing it asks for, and the README inside the zip explains what it
does and does not grant before asking.

What that trust means, plainly: a certificate in *Trusted People* can vouch for
packages **you choose to install**. It does not let anything install itself, and
it is not a root authority. `build/Uninstall-Sideload.ps1` removes the
certificate along with the app, which is what puts a machine back to clean.

There is no code-signing authority behind this certificate — that is the cost of
not paying one — so the thumbprint below is what you check it against.

### Requirements

- **Windows 10 version 1809 (build 17763) or later, x64.** That is the floor the package
  declares and will install against. Windows 11 is the sensible choice — mainstream servicing
  for 1809 has ended, and only the LTSC Extended channel is still serviced — but the
  application does not require it.
- A serial port, or a USB-to-serial adapter, wired to the receiver
  (9600-8-N-1 for a Z3805A)

### Uninstalling

*Settings › Apps* removes the application. To remove the certificate too, run
[`build/Uninstall-Sideload.ps1`](https://github.com/TGoodhew/WinZ3805A/blob/main/build/Uninstall-Sideload.ps1)
from a clone, or delete the entry by hand from `certlm.msc` → *Trusted People* →
*Certificates*.

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
