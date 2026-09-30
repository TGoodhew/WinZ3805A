WinZ3805A
Monitoring and control for HP/Symmetricom SmartClock GPS-disciplined
oscillators - the Z3805A and its siblings - over RS-232, and monitoring
for any GPS receiver that speaks NMEA 0183 or any Symmetricom or Trimble
UCCM telecom module.


TO INSTALL

  Double-click Install.cmd.

  Windows will ask for administrator permission once. Read the next section
  before you agree to it.

  WinZ3805A needs Microsoft's free .NET 10 Runtime. If this folder has a
  Runtime\dotnet-runtime-...exe file (the "offline" download), the installer
  installs .NET from it when your machine does not have it, in that same
  permission prompt. Otherwise it opens Microsoft's download page for you.


ABOUT THAT PERMISSION PROMPT

  This application is not distributed through the Microsoft Store, so Windows
  has no reason to trust it until you say it can. The installer asks for
  administrator rights once, to add this application's signature to a
  certificate store called "Trusted People".

  That store is narrower than it sounds, and the distinction matters:

    - A certificate in "Trusted People" can vouch for applications you choose
      to install by hand. That is all it can do.

    - It CANNOT vouch for a website, and it CANNOT make code you did not
      choose to run look like it came from a company you trust. Those would
      require the "Trusted Root" store, which this installer does not touch.

  If you would rather check before agreeing, the certificate is the .cer file
  in this folder - double-click it to see who issued it and when it expires.

  Nothing else in this installation needs administrator rights. The
  application installs for your account only.


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
  and puts right the pieces it installed, and keeps your data. Windows'
  own Settings > Apps > WinZ3805A > Advanced options > Repair is also safe.
  Do NOT use Reset there unless you mean it: it deletes your history and
  settings.


IF AN EARLIER WINZ3805A IS INSTALLED

  Releases up to 1.3.0 were signed under an earlier publisher identity, so
  Windows cannot upgrade them in place. The installer handles it: it asks
  you to close the earlier copy if it is running, and lists what it will
  remove before changing anything. After installing, it saves the earlier
  copy's history, settings and logs to a "WinZ3805A earlier copy ..." folder
  in Documents, moves them into the new copy, and removes the earlier copy.
  The certificate earlier releases were signed with is removed from Trusted
  People in the same permission prompt.

  If the new version was already installed and has history of its own, it
  is not overwritten. Import the earlier history from Settings > Import
  history..., choosing trend.db in the saved folder.


TO REMOVE IT

  Settings > Apps > Installed apps > WinZ3805A > Uninstall.

  Uninstalling deletes what the application has stored: your remembered
  connection, your settings, the recorded trend history, and the application
  log. If you want to keep those - to reinstall a newer version, say -
  uninstall from PowerShell instead:

    Get-AppxPackage WinZ3805A | Remove-AppxPackage -PreserveApplicationData

  Either way the certificate stays behind. To remove that too, run
  certlm.msc, open Trusted People > Certificates, and delete the entry.

  .NET stays installed too, because other applications may use it. If
  nothing else needs it, remove "Microsoft .NET Runtime - 10..." from
  Settings > Apps.


WHAT IT NEEDS

  Windows 10 version 1809 or later, on a 64-bit Intel or AMD processor.
  A serial port, or a USB-to-serial adapter, connected to the receiver.


WHAT IT DOES NOT DO

  It collects nothing and sends nothing anywhere. There is no telemetry, no
  account, and no network connection of any kind. Everything it knows, it
  learned from the serial port.
