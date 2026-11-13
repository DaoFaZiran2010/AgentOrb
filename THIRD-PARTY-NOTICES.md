# Third-party notices

AgentOrb itself is MIT licensed (see `LICENSE`). It depends on the following third-party
components.

## Anode

- Project: https://github.com/skulitom/Anode
- Version used: v0.11.4 (pinned; downloaded by the installer at install time and verified
  against a recorded SHA-256)
- Licence: MIT

```
MIT License

Copyright (c) 2026 skulitom

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

Anode also offers a paid business licence and support agreement. It is optional and does not
modify the MIT terms above.

## .NET 8 runtime

AgentOrb is published self-contained, so the .NET runtime is redistributed inside its
executables.

- Project: https://github.com/dotnet/runtime
- Licence: MIT, Copyright (c) .NET Foundation and Contributors

## Microsoft Windows components

AgentOrb uses Windows APIs and the Remote Desktop ActiveX control (`mstscax.dll`) that ship
with Windows. No Microsoft binaries are redistributed by this project; they are used from the
operating system on the user's machine.

## Build-time dependencies

None. The solution intentionally has zero NuGet package references; everything builds against
the .NET base class library and the Windows SDK projections included with the .NET 8 SDK.
