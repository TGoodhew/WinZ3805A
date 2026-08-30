# Microsoft Store submission

Everything Partner Center asks for that is a decision rather than a file. Kept in
the repository so the listing is reviewed in a pull request like anything else,
and so the wording that carries the §6.3 trademark position cannot be quietly
rewritten in a web form.

**Status: not yet submitted, and deferred.** Store submission was deferred by
decision on 21 Aug 2026 — P0-15 (#15) and OQ-6 (#39) were both closed as
deferred, and are the issues to reopen if the Store is returned to. Partner
Center registration has not happened, so two of the three values in
[Identity](#identity) are still placeholders and the package cannot be
submitted. Everything else here is ready.

---

## Identity

Reserve the app name in Partner Center, then copy these from **Product identity**
into `src/WinZ3805A/Package.appxmanifest` verbatim.

| Partner Center field | Manifest location | In the repository now |
|---|---|---|
| Package/Identity/Name | `Identity/@Name` | `WinZ3805A` |
| Package/Identity/Publisher | `Identity/@Publisher` | `CN=AppPublisher` (placeholder) |
| Package/Properties/PublisherDisplayName | `Properties/PublisherDisplayName` | `The Schanzuer Group LLC` — correct for the sideloaded build; Partner Center issues its own and it must match the account |

Retyping the publisher distinguished name rather than copying it is the usual
way a first submission fails: it must match the certificate the Store signs with
exactly.

`Identity/@Name` is effectively permanent — changing it later is a new app, a new
listing, and no upgrade path for existing users. `Properties/DisplayName` is
deliberately **not** coupled to it (§6.3) and is a one-line change at any time,
which is the hedge if a reviewer objects to the name. See OQ-8 (#41, closed as
deferred with the submission).

## Privacy policy URL

    https://tgoodhew.github.io/WinZ3805A/privacy

Source is [`docs/privacy.md`](privacy.md), published by GitHub Pages from
`/docs` on `main`. **Pages has to be enabled once** in Settings → Pages →
Source: *Deploy from a branch*, `main` / `/docs`. The URL 404s until that is
done, and Store submission checks that it resolves.

The policy states plainly that the application collects and transmits nothing,
which is true and verifiable: there is no HTTP client, no socket, and no
telemetry SDK anywhere in the source.

---

## Listing

### Name

**WinZ3805A**

Model designation only. Per §6.3 this is nominative descriptive use — the
application genuinely is for that device and there is no concise way to say so
otherwise — and it deliberately contains no company mark.

### Short description

> Monitor and control HP and Symmetricom GPS-disciplined oscillators over a
> serial port.

### Description

> WinZ3805A is a modern Windows application for monitoring and controlling
> GPS-disciplined oscillators over RS-232. It works with HP and Symmetricom SCPI
> GPS receivers including the Z3805A, Z3801A, 58503A/B, 59551A and Z3816A, and
> monitors any GPS receiver that speaks NMEA 0183.
>
> It is built for the way these instruments are actually used: left running on a
> bench for weeks at a time. The main window is a single glanceable surface —
> synchronisation state, satellites tracked, 1 PPS time interval, and the figures
> of merit — small enough to leave in the corner of a second monitor and honest
> enough to trust from across the room. A reading is never blanked because it has
> gone stale; it is shown with the age beside it.
>
> **What it shows**
>
> - Synchronisation state, TFOM and FFOM, and the oscillator's EFC
> - 1 PPS time interval, with a 60-sample ring showing the recent trend
> - Satellite sky plot and the tracked-satellite table with signal strengths
> - Surveyed position and survey progress
> - Antenna delay, with a cable-length calculator
> - Holdover state, duration and uncertainty
> - The receiver's own diagnostic log, filterable and exportable as CSV
> - UTC and GPS time, with the GPS week-rollover correction applied
> - Questionable and operation status registers, decoded bit by bit
>
> **What it will not do**
>
> Commands that erase the receiver's survey or its stored configuration are not
> implemented. They are not hidden behind a warning or an advanced mode — they
> are not in the application at all. Everything that does change the instrument
> asks first, in a dialog that names the command, says what it will do, and reads
> back the receiver's own error response if it is refused.
>
> Requires a serial port and a cable to the receiver. USB-to-serial adapters
> work. The application is built for x64 and runs on Windows on ARM under
> emulation; there, check that your adapter's manufacturer ships an ARM64
> driver, as several common chipsets do not.
>
> Not affiliated with, endorsed by, or sponsored by HP, Hewlett-Packard,
> Agilent, Keysight or Symmetricom. Product and model names are used to describe
> the equipment this application works with.

Compatibility is described here in the body, never in the name — §6.3's first
hedge.

### Category

Developer tools, or Utilities & tools. Neither is a comfortable fit; the audience
is metrology and amateur radio rather than either.

### Search terms

GPSDO, GPS disciplined oscillator, frequency standard, 10 MHz reference, 1 PPS,
SCPI, RS-232, NMEA 0183, u-blox, time and frequency, Z3805A, Z3801A, 58503A,
59551A, Thunderbolt alternative, laboratory instrument

### Age rating

All ages. No user-generated content, no network communication, no purchases.

---

## Capability justification

`runFullTrust` is the only declared capability and is restricted, so submission
asks why. The text §6.3 specifies, verbatim:

> Desktop application requiring Win32 serial port access to communicate with
> user-attached RS-232 laboratory instruments.

The `serialcommunication` device capability is deliberately **not** declared: it
governs the UWP `Windows.Devices.SerialCommunication` API, which this application
does not use, and unnecessary capabilities add certification friction.

## Screenshots

Partner Center wants at least one 1366×768 or larger. The obvious four:

1. Main window, locked to GPS, showing the medallion and the headline figures
2. Satellites page with the sky plot and the tracked table
3. A tier C confirmation dialog — it shows the safety model better than prose
4. Diagnostics with the receiver's log loaded

Capture at 100% scaling in Light theme, with a receiver actually connected. A
screenshot of a disconnected application shows nothing worth seeing.

## Before submitting

- [ ] Partner Center registration complete, app name reserved
- [ ] The three identity values copied into `Package.appxmanifest` (#39)
- [ ] GitHub Pages enabled, privacy URL resolves
- [ ] `pwsh build/Invoke-Wack.ps1` clean on **x64**, from an elevated shell —
      the only architecture, per §6.1 as amended
- [ ] Screenshots captured
- [ ] `Identity/@Version` in the manifest set to the release version — it is
      the single version source; `build/New-SideloadPackage.ps1` reads it and
      names the zip from it, and nothing else carries a version
