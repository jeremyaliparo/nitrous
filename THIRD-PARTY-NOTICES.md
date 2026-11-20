# Third-party notices

Nitrous itself is MIT licensed — see [LICENSE](LICENSE). The components below are included in, or
served alongside, the published build and this site, under their own terms.

## NvAPIWrapper.Net 0.8.1.101 — LGPL-3.0

- Copyright © Soroush Falahati (falahati.net).
- Licensed under the **GNU Lesser General Public License, version 3**, together with the GNU GPL
  version 3 it incorporates: [licences/LGPL-3.0.txt](licences/LGPL-3.0.txt),
  [licences/GPL-3.0.txt](licences/GPL-3.0.txt).
- Used **unmodified**. Nitrous calls it to read and write NVIDIA P0 clock offsets; no other part of
  the app depends on it, and the call sites are confined to `app/Managers/NvidiaGpuManager.cs`.
- Corresponding source for this exact package version: <https://github.com/falahati/NvAPIWrapper>
  (NuGet `NvAPIWrapper.Net` 0.8.1.101).
- The published build is a single file with the assembly embedded. Anyone who wants to run Nitrous
  against a different build of the library can build from source (see the README) and reference
  their own; nothing else needs to change.

## System.Management 10.0.10 — MIT

Copyright © .NET Foundation and Contributors. Full text:
[licences/MIT-dotnet.txt](licences/MIT-dotnet.txt).

## PawnIO modules — IntelMSR, IntelMCHBAR — LGPL-2.1-or-later

- `assets/pawnio/IntelMSR.bin` is the **IntelMSR** module from
  [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) 0.2.12, by namazso.
- `assets/pawnio/IntelMCHBAR.bin` is the **IntelMCHBAR** module from the same release, by Gen Li.
- Both are shipped **unmodified** and embedded in the binary as resources.
- Licensed under the **GNU Lesser General Public License, version 2.1 or later**:
  [licences/LGPL-2.1.txt](licences/LGPL-2.1.txt), with the modules' own copy in
  `assets/pawnio/COPYING`.
- Source for these exact module versions:
  <https://github.com/namazso/PawnIO.Modules/blob/master/IntelMSR.p> and
  <https://github.com/namazso/PawnIO.Modules/blob/master/IntelMCHBAR.p>. To substitute them, replace
  the embedded resources with modules you built; PawnIO loads whatever bytecode it is handed.
- The modules are only loaded when the **PawnIO** driver (<https://pawnio.eu>) is already installed,
  and only on Intel CPUs. Nitrous never installs, ships or starts that driver. PawnIO's licence
  exception permits independent programs to talk to the driver through its device interface, which
  is the only interface used here — no PawnIO code is linked.
- Used for one thing: displaying the CPU package power limits, read from MSR `0x610` and the
  firmware's MCHBAR copy at `0x59A0`. Nitrous does not write either of them.

## Fonts served by the site

Both are shipped as Latin subsets in `docs/assets/fonts`, unmodified apart from the subset.

- **Inter** — Copyright © 2016 The Inter Project Authors. SIL Open Font License 1.1:
  [licences/OFL-Inter.txt](licences/OFL-Inter.txt) (also next to the font file).
- **JetBrains Mono** — Copyright 2020 The JetBrains Mono Project Authors. SIL Open Font License 1.1:
  [licences/OFL-JetBrainsMono.txt](licences/OFL-JetBrainsMono.txt) (also next to the font file).

Neither font declares a Reserved Font Name, so the subsets keep their original family names.

The application itself bundles **no font**: the SETUP font list offers whichever families Windows
reports, and nothing is installed or shipped.

## Trademarks

Acer, Nitro, NitroSense and Predator are trademarks of Acer Inc. Nitrous is an unofficial project,
not affiliated with or endorsed by Acer. The marks are used only to describe what the software
works with.
