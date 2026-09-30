# Third-party notices

IC-PW2 Bridge (`com.kq4wlr.zeus.pw2bridge`) is licensed GPL-2.0-or-later (see
`LICENSE`). The distributed package bundles the following third-party
component, which remains under its own license.

## System.IO.Ports

- Packages: `System.IO.Ports` 9.0.0 and its `runtime.*.System.IO.Ports`
  dependencies (NuGet): `System.IO.Ports.dll` and the managed/native assets
  under `runtimes/`
- Source: https://github.com/dotnet/runtime
- License: MIT

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

## Zeus SDK contracts (build-time only, not bundled)

`sdk/Openhpsdr.Zeus.Plugins.Contracts/` is a verbatim, unmodified copy of the
public SDK snapshot (ABI 1 / SDK 1.5.0) from
https://github.com/Zeus-SDR/zeus-community-features, GPL-2.0-or-later. It is
referenced at build time only; `Zeus.Plugins.Contracts.dll` is not included in
the package. `build-package.ps1` was adapted from that repository's
`templates/hello-world/` for portable serial packaging on 2026-09-29 by KB2UKA.
