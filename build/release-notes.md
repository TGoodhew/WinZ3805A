## Installing

Download **`WinZ3805A-<version>-x64.zip`** below, unblock it, extract it, and
double-click **`Install.cmd`**.

> Unblocking matters: Windows marks anything downloaded from the internet, and
> the mark survives extraction. Right-click the **zip** → *Properties* → tick
> *Unblock* → *OK*, **before** extracting. Skipping it makes the installer fail
> in ways that do not mention the mark.

The zip carries everything the install needs — the signed package, its
certificate, and the x64 Windows App Runtime — so a bench machine with no
internet connection and no Visual Studio can install from it.

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

- Windows 11 24H2 or later, **x64**
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
59551A, Z3816A) — over RS-232, plus a generic NMEA 0183 talker.

Destructive receiver commands are **unreachable rather than warned about**: the
command catalog is an allowlist, and the excluded commands are not entries with
a flag — they do not exist as data.

New to it? [The user's guide](https://github.com/TGoodhew/WinZ3805A/blob/main/docs/how-to-use.md)
is also in the app under **Help** (`F1`).
