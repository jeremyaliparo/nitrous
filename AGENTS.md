# Nitrous

## Keep the notes current

Findings belong in `C:\Users\Jeremy\Documents\Nitrous-TGP-Investigation\TGP-NOTES.md`, not in the
conversation. When a session produces something worth keeping — a protocol detail, a measurement,
a firmware quirk, a rejected hypothesis, or a UI constraint found by testing — append it before
the session ends. Do not wait to be asked: a reboot loses the chat, and re-deriving a result costs
another round trip with a tester.

Write down what was measured, on which machine, and how. Cite the file and line when a finding
explains a specific check.

## Build and publish

Test builds go to `bin\publishN` and are reported with their SHA-256:

```powershell
dotnet publish app/Nitrous.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false -o bin\publishN
```

Never tag or publish a release unless asked. The version comes from the csproj; do not override it
with `-p:Version` on the command line or the binary will disagree with the tree.

## Test builds

Anything handed to testers is never a plain version number. Suffix it:

```
0.10.0-test.1     first tester build of 0.10.0
0.10.0-test.2     the next one, after fixes
0.10.0            the real release, once the testers are happy
```

- Use `-`, never `_`. `0.10.0_test` is not valid SemVer, and the release workflow rejects the tag.
- The suffix is read from `AssemblyInformationalVersion`, which is the only place it survives —
  `AssemblyVersion` is numeric only, so a suffixed build would otherwise display as a plain `0.10.0`.
  It has to be visible in the dashboard header, the tray, the log start line and the diagnostics
  bundle.
- `app/Nitrous.csproj` `<Version>`, the `CHANGELOG.md` heading and the git tag must all carry the
  same string, or the release workflow cannot find the notes and fails on a pushed tag.
- Test builds publish as GitHub prereleases, so `releases/latest`, the updater's default check and
  the website keep pointing at the last stable build.
- Promoting to a release drops the suffix and reuses the same changelog section.

## Editing XAML

The `edit` tool rewrites whole XAML files. For `app/Ui/NitrousDashboard.xaml`, splice by line index
in a PowerShell script and re-read the region afterwards to confirm the indentation survived. The
file mixes tabs and spaces, so match on line numbers rather than on whitespace.

## Ground rules

- No speculative hardware writes. Every opcode and value must come from observed Acer behaviour or
  from static evidence in Acer's own software.
- Never wake a sleeping dGPU.
- GPU offsets are positive-only: core `0..300`, memory `0..1000`.
- When a capability cannot be proven, disable or hide the control and say why, rather than letting
  it look like it worked.
