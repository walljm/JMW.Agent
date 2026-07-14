# Third-party attribution — Recog fingerprint corpus

The XML fingerprint databases in this directory (`http_servers.xml`, `html_title.xml`,
`http_wwwauth.xml`, `favicons.xml`) are the Rapid7 **Recog** project's HTTP databases, redistributed
under its BSD-2-Clause license. They are embedded into the agent binary (`<EmbeddedResource>`), so
this notice satisfies the BSD-2 requirement to reproduce the copyright notice and disclaimer in
binary distributions.

- Source: https://github.com/rapid7/recog (files from the upstream `xml/` directory)
- Upstream license: https://github.com/rapid7/recog/blob/main/COPYING
- The XML files are unmodified from upstream. A small number of Ruby/Onigmo regex quirks that .NET
  rejects (e.g. redundant `\_` escapes) are normalized at load time in `RecogDatabase.TryCompile`,
  not by editing these files — so they can be re-vendored cleanly.

## License (BSD-2-Clause)

```
Copyright (c) 2014-2015, Rapid7
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
