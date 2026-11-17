# Protocol reference and test fixtures

The fixed-request Grappa calculation, protocol layouts and the synthetic
Grappa/MP3 test fixtures are based on [rustunes](https://github.com/fbiba/rustunes).
The adoption preferences, first-library session and artwork protocol are also
based on that reference, at commit
`70345d15f4727179c43d4e5a17d633825ccbd4dd`. The C# implementation adapts these
protocol layouts; it uses .NET cryptographic primitives and includes no Apple binaries.

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

## Microsoft .NET packages, version 10.0.15

The following direct and transitive runtime packages share the same upstream
source revision and accompanying notices:

- Microsoft.Extensions.DependencyInjection/10.0.15
- Microsoft.Extensions.DependencyInjection.Abstractions/10.0.15
- System.Configuration.ConfigurationManager/10.0.15
- System.Diagnostics.EventLog/10.0.15
- System.Security.Cryptography.ProtectedData/10.0.15

Copyright (c) .NET Foundation and Contributors. All rights reserved.
The package metadata also states: © Microsoft Corporation. All rights reserved.

The complete MIT license is included in
[Microsoft-LICENSE.txt](Licenses/Microsoft-LICENSE.txt). It is copied unchanged
from `src/runtime/LICENSE.TXT` at the source revision identified below.
Their identical, complete upstream
third-party notices are retained once in
[Microsoft-THIRD-PARTY-NOTICES.txt](Licenses/Microsoft-THIRD-PARTY-NOTICES.txt),
copied unchanged from `Microsoft.Extensions.DependencyInjection` 10.0.15.

Corresponding source:
[dotnet/dotnet, commit 8e0f8ed2312954727f8919d7d1c2f78b483b4ee7](https://github.com/dotnet/dotnet/tree/8e0f8ed2312954727f8919d7d1c2f78b483b4ee7/src/runtime).

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

## StbImageSharp/2.30.16 and StbImageWriteSharp/1.16.7

NoTunes uses these unmodified managed image libraries to validate embedded
covers and convert non-JPEG covers to JPEG without changing the MP3 file.
The packages identify StbImageSharpTeam and StbImageWriteSharpTeam as their
authors. Their upstream license declarations and credits are retained in
[StbImageSharp-README.md](Licenses/StbImageSharp-README.md) and
[StbImageWriteSharp-README.md](Licenses/StbImageWriteSharp-README.md).

StbImageSharp offers the MIT license or public-domain dedication; the
StbImageWriteSharp port declares public domain. The complete, unmodified
MIT/Unlicense alternatives of their bundled stb headers, including
Copyright (c) 2017 Sean Barrett, are retained in
[StbImageSharp-LICENSE.txt](Licenses/StbImageSharp-LICENSE.txt) and
[StbImageWriteSharp-LICENSE.txt](Licenses/StbImageWriteSharp-LICENSE.txt).
These files accompany build and publish output.

Corresponding sources:

- [StbImageSharp, commit 125af70cb557033f2c46aec8e82eaaf72ac49817](https://github.com/StbSharp/StbImageSharp/tree/125af70cb557033f2c46aec8e82eaaf72ac49817).
- [StbImageWriteSharp 1.16.7, commit 276e0a0e389f8bcc79aa6fb4446ff825d0fdb2b4](https://github.com/StbSharp/StbImageWriteSharp/tree/276e0a0e389f8bcc79aa6fb4446ff825d0fdb2b4).

# Test dependencies

The following packages are used unmodified by the test project. They are not
application runtime dependencies. Their license texts and notices are retained
under `Licenses/`, which is copied to build and publish output.

## NUnit/5.0.0

Copyright (c) 2009-2019 Charlie Poole, 2014-2025 Rob Prouse, 2026 Terje
Sandstrom and Contributors. MIT License.

The complete license and original notices are copied unchanged from the
NuGet package into [NUnit-LICENSE.txt](Licenses/NUnit-LICENSE.txt),
[NUnit-NOTICES.md](Licenses/NUnit-NOTICES.md) and
[NUnit-THIRD-PARTY-NOTICES.md](Licenses/NUnit-THIRD-PARTY-NOTICES.md).

Corresponding source:
[nunit, version 5.0.0](https://github.com/nunit/nunit/tree/v5.0.0).

## NUnit3TestAdapter/6.3.1

Copyright (c) 2011-2021 Charlie Poole, 2014-2026 Terje Sandstrom, as stated
in the package metadata. The upstream MIT license carries Copyright (c)
2011-2020 Charlie Poole, 2014-2026 Terje Sandstrom and is retained unchanged in
[NUnit3TestAdapter-LICENSE.txt](Licenses/NUnit3TestAdapter-LICENSE.txt).

Corresponding source:
[nunit3-vs-adapter, version 6.3.1](https://github.com/nunit/nunit3-vs-adapter/tree/V6.3.1).

## Microsoft test SDK and test platform, version 18.12.0

- Microsoft.NET.Test.Sdk/18.12.0
- Microsoft.CodeCoverage/18.12.0
- Microsoft.TestPlatform.TestHost/18.12.0
- Microsoft.TestPlatform.ObjectModel/18.12.0

Copyright (c) Microsoft Corporation. All rights reserved. These packages use
the MIT license, retained in
[Microsoft-TestPlatform-LICENSE.txt](Licenses/Microsoft-TestPlatform-LICENSE.txt)
from the upstream VSTest repository. Original package notices are copied
unchanged from `Microsoft.TestPlatform.TestHost` and `Microsoft.CodeCoverage`
18.12.0 into
[Microsoft-TestPlatform-THIRD-PARTY-NOTICES.txt](Licenses/Microsoft-TestPlatform-THIRD-PARTY-NOTICES.txt)
and [Microsoft-CodeCoverage-THIRD-PARTY-NOTICES.txt](Licenses/Microsoft-CodeCoverage-THIRD-PARTY-NOTICES.txt).

Corresponding source: [microsoft/vstest](https://github.com/microsoft/vstest),
version 18.12.0. The package metadata identifies source commit
`8c03f23cd7df6ab28c3f1e3d112dea978eae8b0c` on branch `18.12`.

## Transitive adapter dependencies

The adapter also resolves the following unmodified, MIT-licensed Microsoft
packages for the test project. Their package metadata states:
© Microsoft Corporation. All rights reserved. The complete Microsoft MIT
license is retained in
[Microsoft-TestPlatform-LICENSE.txt](Licenses/Microsoft-TestPlatform-LICENSE.txt).

- Microsoft.TestPlatform.AdapterUtilities/18.10.1, from
  [microsoft/vstest](https://github.com/microsoft/vstest), source commit
  `87dfd4b2d2bacd91ad69e009ade6f4715b3b46ec`.
- Microsoft.Testing.Extensions.Telemetry/2.4.1,
  Microsoft.Testing.Extensions.TrxReport.Abstractions/2.4.1,
  Microsoft.Testing.Extensions.VSTestBridge/2.4.1,
  Microsoft.Testing.Platform/2.4.1 and
  Microsoft.Testing.Platform.MSBuild/2.4.1, from
  [microsoft/testfx](https://github.com/microsoft/testfx), source commit
  `9a14d19cb8ef1ec6a2aae81d4223a09190f96593`.
- Microsoft.ApplicationInsights/2.23.0, from
  [ApplicationInsights-dotnet](https://github.com/microsoft/ApplicationInsights-dotnet),
  source commit `2faa7e8b157a431daa2e71785d68abd5fa817b53`.
- Microsoft.Extensions.DependencyModel/8.0.2, from
  [dotnet/runtime](https://github.com/dotnet/runtime), source commit
  `81cabf2857a01351e5ab578947c7403a5b128ad1`. Its original .NET Foundation MIT
  license is retained in [Microsoft-LICENSE.txt](Licenses/Microsoft-LICENSE.txt),
  and its complete package notices are copied unchanged into
  [Microsoft-DependencyModel-THIRD-PARTY-NOTICES.txt](Licenses/Microsoft-DependencyModel-THIRD-PARTY-NOTICES.txt).
