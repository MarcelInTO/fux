# Third-Party Notices

fux incorporates third-party material. This file reproduces the required copyright
and license notices. fux's own license is in [LICENSE](LICENSE).

---

## 1. Microsoft XML Notepad

fux is a derivative work of **Microsoft XML Notepad**. Source from that project is
incorporated directly into this repository: the document engine under `src/Model/`
(`XmlCache`, `DomLoader`, `Checker`, `SchemaCache`, `XmlCsvReader`, `UndoManager` and
related types), the build properties it imports at `src/Version/Version.props`, and the
XML/XSD fixtures under `sandbox/testdata/`. Some of that source has been modified; much
of it is unmodified. Individual files retain their original copyright headers where
present.

Earlier revisions of this repository also carried upstream's WinForms application,
installer projects, `XmlStats`, `WindowsInput` and documentation. fux never built any of
it, and it has since been removed from the working tree; it remains in git history and on
the `upstream-master` branch, which is a pristine mirror of microsoft/XmlNotepad.

- Project: https://github.com/microsoft/XmlNotepad
- License: MIT

```
The MIT License (MIT)

Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Microsoft does not endorse, sponsor, or support fux. "XML Notepad" and "Microsoft"
are used here only to identify the origin of the incorporated code.

The upstream project's own `SECURITY.md` and `CODE_OF_CONDUCT.md` described Microsoft's
processes and do not apply to fux, so they are not carried at the root of this
repository. They remain retrievable from git history:
`git show upstream/master:SECURITY.md`.

---

## 2. Terminal.Gui

Redistributed in binary form inside fux's self-contained build.

- Project: https://github.com/gui-cs/Terminal.Gui/
- Authors: Miguel de Icaza, Charlie Kindel (@tig), @BDisp, and contributors
- License: MIT

---

## 3. Newtonsoft.Json

Redistributed in binary form inside fux's self-contained build.

- Project: https://www.newtonsoft.com/json
- License: MIT

```
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## 4. Microsoft.Xml.SgmlReader

Used for HTML import; redistributed in binary form inside fux's self-contained build.
This is the only fux dependency under Apache-2.0 rather than MIT.

- Project: https://github.com/lovettchris/SgmlReader
- Authors: Chris Lovett, Steve Bjorg
- License: Apache License 2.0 — full text in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)

```
Copyright (c) 2002-2022, Microsoft Corporation
Copyright (c) 2007-2013, MindTouch

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

fux has not modified SgmlReader; it is consumed as a published NuGet package.

---

## Transitive dependencies

The sections above cover fux's incorporated source and its direct package references.
A self-contained build additionally bundles the .NET runtime and the transitive
closure of those packages (Markdig, TextMateSharp, Onigwrap, ColorHelper, Wcwidth,
the `Microsoft.Extensions.*` and `System.IO.Abstractions` families, and others). Each
carries its own license, declared in its package metadata. To enumerate the current
closure for auditing:

```sh
dotnet list src/Fux/Fux.csproj package --include-transitive
```
