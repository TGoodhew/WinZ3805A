# Third-party notices

WinZ3805A is distributed under the MIT licence (see `LICENSE`). It redistributes
the components below, each under its own terms.

**Only one component is redistributed as a file inside the package**: the
Cascadia Mono font. Everything else is a NuGet dependency that ships as an
assembly, and the Windows App SDK is a *framework-dependent* reference resolved
by the Store at install time (§6.3) rather than something carried in the MSIX.
The distinction matters for this file: the SIL Open Font License requires its
text to travel with the font, which is why it is reproduced in full below and
also shipped beside the font as `Assets/Fonts/CascadiaMono-OFL.txt`.

---

## Components

| Component | Licence | Redistributed as |
|---|---|---|
| Cascadia Mono (font) | SIL Open Font License 1.1 | `Assets/Fonts/CascadiaMono.ttf` inside the package |
| Windows App SDK / WinUI 3 | MIT | Framework package, resolved at install |
| CommunityToolkit.Mvvm | MIT | Assembly in the package |
| CommunityToolkit.WinUI.Controls.SettingsControls | MIT | Assembly in the package |
| Markdig | BSD 2-Clause | Assembly in the package |
| Microsoft.Data.Sqlite (with SQLite) | MIT (SQLite itself is public domain) | Assembly in the package |
| Microsoft.Extensions.DependencyInjection | MIT | Assembly in the package |
| Microsoft.Extensions.Hosting | MIT | Assembly in the package |
| Microsoft.Extensions.Logging, .Abstractions | MIT | Assembly in the package |
| System.IO.Ports | MIT | Assembly in the package |
| Microsoft.Windows.SDK.BuildTools, .WinApp | Microsoft Windows SDK licence | Build-time only; not redistributed |

`Microsoft.Windows.SDK.BuildTools` and its `.WinApp` companion are build-time
tooling. They produce no assembly in the shipped package and are listed only so
the dependency set here matches the one in the project files.

## Trademarks

HP, Hewlett-Packard, Agilent, Keysight and Symmetricom are marks of their
respective owners. This application is not affiliated with, endorsed by, or
sponsored by any of them. Model designations such as Z3805A appear here and in
the Store listing to describe the equipment the application works with, which is
nominative descriptive use — see §6.3, which sets out that position and the two
hedges that follow from it.

---

## SIL Open Font License 1.1 — Cascadia Mono

Reproduced in full, as clause 2 of the licence requires. §9.5.1 embeds this font
rather than assuming it is present: it is inbox on Windows 11 but ships with
Windows Terminal on Windows 10, so it cannot be relied on at §6.1's 1809 floor.

```
Copyright (c) 2019 - Present, Microsoft Corporation,
with Reserved Font Name Cascadia Code.

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
