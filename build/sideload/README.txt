WinZ3805A
Monitoring and control for HP/Symmetricom SmartClock GPS-disciplined
oscillators - the Z3805A and its siblings - over RS-232, and monitoring
for any GPS receiver that speaks NMEA 0183 or any Symmetricom or Trimble
UCCM telecom module.


TO INSTALL

  Double-click Install.cmd.

  WinZ3805A needs Microsoft's free .NET 10 Runtime. If this folder has a
  Runtime\dotnet-runtime-...exe file (the "offline" download), the installer
  installs .NET from it when your machine does not have it, which needs
  administrator permission once. Otherwise it opens Microsoft's download page
  for you.


ABOUT THE SIGNATURE

  WinZ3805A is signed by The Schnauzer Group LLC through Microsoft's Azure
  Artifact Signing, so Windows can check it the same way it checks any other
  signed application. You do not have to trust a certificate by hand, and
  the installer adds nothing to any certificate store. Its log records who
  signed the package and who vouched for that.

  If this PC has never been connected to the internet, Windows may not yet
  have the Microsoft root certificate the signature chains to. The installer
  checks before it changes anything and says so; connecting the PC once
  fixes it.

  The application installs for your account only. Administrator permission
  is asked for only to install .NET, or to remove a certificate an earlier
  release added (see below).


WHAT GETS INSTALLED

  WinZ3805A itself, and the Windows App Runtime it needs if your machine does
  not already have it. Both come from this folder.

  .NET 10, if your machine does not have it: from this folder in the offline
  download, or from Microsoft's page in the other one. Either way it is
  Microsoft's own installer, installed for the whole machine, and Windows keeps
  it up to date - provided "Receive updates for other Microsoft products" is
  on (Settings > Windows Update > Advanced options). The installer tells you
  whether it is.
  The licences of the components the application uses are listed in
  THIRD-PARTY-NOTICES.md, in this folder and inside the application.


IF SOMETHING GOES WRONG

  The installer keeps a record of everything it did: what it found, what
  it removed or kept and why, and whether WinZ3805A opened when it was
  started at the end. It is in

    %LOCALAPPDATA%\WinZ3805A Installer\logs

  one file per run, named by date and time, and the installer prints the
  path as it finishes. Please include it when reporting a problem.

  If WinZ3805A stops starting, run the newest Install.cmd again: it checks
  and puts right the pieces it installed, and keeps your data. If the app
  itself is damaged, it installs it again, saving your data to Documents
  first and moving it back afterwards. Windows' own Settings > Apps >
  WinZ3805A > Advanced options > Repair is also safe.
  Do NOT use Reset there unless you mean it: it deletes your history and
  settings.


IF AN EARLIER WINZ3805A IS INSTALLED

  Releases up to 1.5.x were signed under earlier publisher identities -
  up to 1.3.0 under one, 1.3.1 to 1.5.x under another - so Windows cannot
  upgrade them in place. The installer handles it: it asks
  you to close the earlier copy if it is running, and lists what it will
  remove before changing anything. After installing, it saves the earlier
  copy's history, settings and logs to a "WinZ3805A earlier copy ..." folder
  in Documents, moves them into the new copy, and removes the earlier copy.
  Windows 10 cannot install the two side by side, so there it saves the data
  and removes the earlier copy first, then installs and moves the data in.
  The saved folder stays in Documents either way.
  The certificates earlier releases asked you to trust are removed from
  Trusted People, which needs administrator permission once.

  If the new version was already installed and has history of its own, it
  is not overwritten. Import the earlier history from Settings > Import
  history..., choosing trend.db in the saved folder.


INSTALLING FROM A SCRIPT

  Install.cmd -Unattended installs without stopping for a key: it skips the
  "Press Enter" prompts, stops at once if WinZ3805A is running, and does not
  open the .NET download page. The administrator prompt still appears when
  something needs it. Its exit code says how it went: 0 installed and
  started, 1 failed, 2 installed but did not start, 3 installed but .NET 10 is
  missing. The log is written as usual.


TO REMOVE IT

  Windows 11: Settings > Apps > Installed apps > WinZ3805A > Uninstall.
  Windows 10: Settings > Apps > Apps & features > WinZ3805A > Uninstall.

  Uninstalling deletes what the application has stored: your remembered
  connection, your settings, the recorded trend history, and the application
  log. To keep the history - to reinstall later, say - use Export history...
  on the application's Settings page first, and Import history... after
  installing again.

  Uninstalling leaves the installer's logs behind. To
  remove everything this installer has put on the machine, follow
  https://github.com/TGoodhew/WinZ3805A/blob/main/docs/remove-winz3805a.md
  Its script saves your history to Documents before removing anything.

  .NET stays installed too, because other applications may use it. If
  nothing else needs it, remove "Microsoft .NET Runtime - 10..." from
  Settings > Apps.


WHAT IT NEEDS

  Windows 10 version 22H2 or later, or Windows 11, on a 64-bit Intel or AMD processor.
  A serial port, or a USB-to-serial adapter, connected to the receiver.


WHAT IT DOES NOT DO

  It collects nothing and sends nothing anywhere. There is no telemetry, no
  account, and no network connection of any kind. Everything it knows, it
  learned from the serial port.
