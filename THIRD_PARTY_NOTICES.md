# Third Party Notices

TriffView is licensed under the GNU General Public License, version 3 only. See `LICENSE`.

The components listed below remain under their own licenses. This file is intended to travel with source and binary distributions of TriffView.

## Fonts

### Rajdhani

- Files: `app/src/assets/fonts/Rajdhani-*.ttf`
- Used by: TriffView and EVE Settings GUI
- Copyright: Copyright (c) 2014, Indian Type Foundry (info@indiantypefoundry.com)
- License: SIL Open Font License, Version 1.1
- Source: https://github.com/google/fonts/tree/main/ofl/rajdhani

### SIL Open Font License 1.1

The bundled font software above is licensed under the SIL Open Font License, Version 1.1:

```
SIL OPEN FONT LICENSE
Version 1.1 - 26 February 2007

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The fonts,
including any derivative works, can be bundled, embedded, redistributed
and/or sold with any software provided that any reserved names are not used
by derivative works. The fonts and derivatives, however, cannot be released
under any other type of license. The requirement for fonts to remain under
this license does not apply to any document created using the fonts or
their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may include
source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical writer or
other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining a
copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font Software,
subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in
Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be included
either as stand-alone text files, human-readable headers or in the
appropriate machine-readable metadata fields within text or binary files as
long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any Modified
Version, except to acknowledge the contribution(s) of the Copyright
Holder(s) and the Author(s) or with their explicit written permission.

5) The Font Software, modified or unmodified, in part or in whole, must be
distributed entirely under this license, and must not be distributed under
any other license. The requirement for fonts to remain under this license
does not apply to any document created using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are not
met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM OTHER
DEALINGS IN THE FONT SOFTWARE.
```

## JavaScript Dependencies

The standalone UI uses these direct JavaScript dependencies:

- React 18.3.1 - MIT License
- React DOM 18.3.1 - MIT License
- Vite 5.4.21 - MIT License
- esbuild 0.21.5 - MIT License
- lucide-react 0.468.0 - ISC License

Transitive JavaScript dependencies are recorded in `app/package-lock.json`.

## Microsoft WebView2

TriffView uses the Microsoft WebView2 NuGet package:

- Package: `Microsoft.Web.WebView2`
- Version: `1.0.2792.45`
- Copyright: Copyright (C) Microsoft Corporation. All rights reserved.
- License: BSD-style license included with the NuGet package

The package also includes its own `NOTICE.txt`, including notices for components used by Microsoft WebView2. Preserve the WebView2 package license and notice information when redistributing binary builds that include WebView2 components.

Microsoft WebView2 package license text:

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## .NET and Windows Components

TriffView is built with .NET 8 and uses Windows, WPF, Windows Forms, DWM, and Win32 APIs. Microsoft .NET components are subject to their respective Microsoft and open source license terms.

## EVE Online and CCP Games

TriffView is an unofficial third-party tool for EVE Online. It is not affiliated with, endorsed by, sponsored by, or approved by CCP Games.

EVE Online, EVE, and related names, marks, and imagery are trademarks or intellectual property of CCP hf. TriffView does not claim ownership of those marks.

## Behavioral References

EVE-O Preview, EVE-X Preview, EVE-APM, Nicotine, and related tools were used as behavioral references while designing TriffView features. TriffView does not intentionally include copied source modules from those projects.
