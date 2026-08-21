WinZ3805A
Monitoring and control for HP/Symmetricom SmartClock GPS-disciplined
oscillators - the Z3805A and its siblings - over RS-232.


TO INSTALL

  Double-click Install.cmd.

  Windows will ask for administrator permission once. Read the next section
  before you agree to it.


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
  not already have it. Both come from this folder; nothing is downloaded.


TO REMOVE IT

  Settings > Apps > Installed apps > WinZ3805A > Uninstall.

  That leaves the certificate behind. To remove that too, run certlm.msc,
  open Trusted People > Certificates, and delete the entry.


WHAT IT NEEDS

  Windows 10 version 1809 or later, 64-bit.
  A serial port, or a USB-to-serial adapter, connected to the receiver.

  Windows on ARM works: the 64-bit build runs under emulation.


WHAT IT DOES NOT DO

  It collects nothing and sends nothing anywhere. There is no telemetry, no
  account, and no network connection of any kind. Everything it knows, it
  learned from the serial port.
