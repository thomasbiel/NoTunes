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

System.Data.SQLite and SQLite are public domain.

## TagLibSharp 2.3.0

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
