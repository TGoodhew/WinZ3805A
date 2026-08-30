---
title: WinZ3805A privacy policy
---

# Privacy policy

**Last updated: 29 August 2026**

## The short version

**WinZ3805A collects no personal data, and transmits nothing anywhere.**

The application has no network code of any kind. It contains no HTTP client, no
socket, no telemetry SDK, and no analytics or crash-reporting service. It talks
to one thing: a serial port on your own computer, connected to your own
instrument.

There is no account to create, nothing to sign in to, and no server behind the
application to send anything to.

## What the application does with data

WinZ3805A reads status and settings from a GPS-disciplined oscillator over
RS-232 and shows them to you. That data — satellite positions, time interval
measurements, the receiver's surveyed position, its diagnostic log — stays on
your computer.

It writes a handful of files to your own user profile so the application
behaves the way you left it, and so it can show you a trend:

| File | What it holds |
|---|---|
| `connection.json` | The serial port you last used, its settings, and whether to reconnect on launch |
| `window.json`, `details-window.json` | Where each window was on screen and how big it was |
| `details-view.json` | Whether the navigation pane was open |
| `satellites-view.json` | Whether the Satellites page shows the plot or the list |
| `advanced.json` | The Advanced Console, undocumented-queries, lock-notification, keep-running and start-minimised switches |
| `appearance.json` | Whether to use the Windows accent colour |
| `trend.db` | The recorded trend: samples of the oscillator's control voltage, the 1 PPS time interval, the synchronisation state and the number of satellites tracked, kept for 56 days |
| `logs\app.log`, and up to four older copies | The application log: the port opened and the settings auto-detect settled on, every connection change, the receiver's synchronisation state, time figure of merit and satellite count as they change, and survey progress. It does not record command traffic and it does not record the receiver's position. |

These are ordinary files in your own profile, under
`%LOCALAPPDATA%\Packages\<package identity>\LocalCache\Local\WinZ3805A\`, which
*Show log folder* on the Diagnostics page opens. Nothing reads them but this
application, and uninstalling removes them.

If you use the CSV export or save the sky plot as an image, the file goes
exactly where you tell it to and nowhere else. **Copy** on the Position page
places the position on the Windows clipboard, where any application can read it
until you copy something else.

## Your receiver's surveyed position

A GPS-disciplined oscillator knows where it is, usually to a few metres, and
WinZ3805A displays that position because operating the instrument requires it.
That is location data in the ordinary sense of the phrase, so it is worth being
explicit:

- It comes from **your instrument**, not from Windows' location service. The
  application does not declare the `location` capability and cannot ask Windows
  where you are.
- It is **displayed and never transmitted**. It appears on screen; it is written
  to disk only if you export it yourself, and put on the clipboard only if you
  press **Copy**. The recorded trend and the application log do not contain it.

## Permissions

The package declares exactly one capability, `runFullTrust`, which is what
permits a desktop application to open a serial port through Win32. It does not
declare the location capability, the microphone or camera capabilities, or any
network capability.

## Diagnostics and crash data

WinZ3805A sends no crash reports or diagnostics. If Windows itself reports an
application crash to Microsoft, that is Windows' own behaviour under your
Windows diagnostic-data settings, and is governed by Microsoft's privacy
statement rather than this one. This application neither adds to it nor receives
anything from it.

## Children

The application is a laboratory instrument tool. It is not directed at children
and collects nothing from anyone.

## Changes

If a future version ever collects or transmits anything, this policy will be
updated before that version ships, the Store listing will disclose it, and the
behaviour will be opt-in rather than on by default.

## Contact

Questions, or a correction to this page:
[open an issue](https://github.com/TGoodhew/WinZ3805A/issues) on the project
repository.
