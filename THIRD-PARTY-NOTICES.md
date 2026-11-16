# Protocol reference and test fixtures

The fixed-request Grappa calculation, protocol layouts and the synthetic
Grappa/MP3 test fixtures are based on [rustunes](https://github.com/fbiba/rustunes).
The implementation uses .NET cryptographic primitives; no Apple binaries are included.

MIT License

Copyright (c) 2026 Feim Biba

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

# Package licenses

The runtime packages below are used unmodified. Their license texts and
notices are included under `Licenses/` and accompany build and publish output.
The package versions and upstream source references identify the distributed
dependencies, including transitive dependencies.

## BouncyCastle.Cryptography/2.7.0

NoTunes uses BouncyCastle for managed TLS. Copyright (c) 2000-2026 The Legion
of the Bouncy Castle Inc. Its complete MIT license and original copyright
notice are included in [BouncyCastle-LICENSE.md](Licenses/BouncyCastle-LICENSE.md),
copied unchanged from the NuGet package's `LICENSE.md`.

Corresponding source:
[bc-csharp, commit 4007498b13582d90ee1eda5d9920c324428b98b3](https://github.com/bcgit/bc-csharp/tree/4007498b13582d90ee1eda5d9920c324428b98b3).

## CommandLineParser/2.9.1

NoTunes uses CommandLineParser for command-line parsing. Copyright (c) 2005 -
2015 Giacomo Stelluti Scala & Contributors. Its complete MIT license and
original copyright notice are included in
[CommandLineParser-LICENSE.md](Licenses/CommandLineParser-LICENSE.md), copied
unchanged from the NuGet package's `License.md`.

Corresponding source:
[commandlineparser, version 2.9.1](https://github.com/commandlineparser/commandline).

## Microsoft .NET packages, version 10.0.13

The following direct and transitive runtime packages share the same upstream
source revision and accompanying notices:

- Microsoft.Extensions.DependencyInjection/10.0.13
- Microsoft.Extensions.DependencyInjection.Abstractions/10.0.13
- System.Configuration.ConfigurationManager/10.0.13
- System.Diagnostics.EventLog/10.0.13
- System.Security.Cryptography.ProtectedData/10.0.13

Copyright (c) .NET Foundation and Contributors. All rights reserved.
The package metadata also states: © Microsoft Corporation. All rights reserved.

The complete MIT license is included in
[Microsoft-LICENSE.txt](Licenses/Microsoft-LICENSE.txt). It is copied unchanged
from `Microsoft.NETCore.App.Runtime.win-x64` 10.0.13, which identifies the same
source revision as the packages above. Their identical, complete upstream
third-party notices are retained once in
[Microsoft-THIRD-PARTY-NOTICES.txt](Licenses/Microsoft-THIRD-PARTY-NOTICES.txt),
copied unchanged from `Microsoft.Extensions.DependencyInjection` 10.0.13.

Corresponding source:
[dotnet/dotnet, commit 4099f83977f4d29604a9aeb971fb0a7bc1d5f699](https://github.com/dotnet/dotnet/tree/4099f83977f4d29604a9aeb971fb0a7bc1d5f699/src/runtime).

## System.Data.SQLite/2.0.4 and SQLite/3.53.4

The managed System.Data.SQLite provider and the native SQLite library are
public domain. The SQLite package metadata credits Copyright 2014-2026
SourceGear, LLC for the package. Its original `LICENSE.txt` is included
unchanged in [SQLite-LICENSE.txt](Licenses/SQLite-LICENSE.txt).

Corresponding upstream sources are
[System.Data.SQLite, version 2.0.4](https://system.data.sqlite.org/) and
[SQLite, version 3.53.4](https://sqlite.org/src/).

## TagLibSharp/2.3.0

NoTunes uses **TagLibSharp**, an LGPL library, to read MP3 tags and audio
properties. Its license is **LGPL-2.1-only**.

Copyright (c) 2006-2007 Brian Nickel.
Copyright (c) 2009-2020 Other contributors.

The package lists Brian Nickel, Gabriel Burt, Stephen Shaw and other
contributors as its authors. See the accompanying
[copyright and author notice](Licenses/TagLibSharp-NOTICE.txt) and the
[complete LGPL 2.1 license text](Licenses/LGPL-2.1.txt).

The unmodified library is used as a separate, replaceable `TaglibSharp.dll`.
Corresponding source for this version is available in the
[TaglibSharp-2.3.0.0 release](https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0).
These notices and license texts accompany the build and publish output.
