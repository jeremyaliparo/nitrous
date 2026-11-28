# Nitrous

## Keep the notes current

Findings belong in `D:\Users\Jeremy\Software Engineering\personal\nitrosense-investigations\TGP-NOTES.md`, not in the
conversation. When a session produces something worth keeping — a protocol detail, a measurement,
a firmware quirk, a rejected hypothesis, or a UI constraint found by testing — append it before
the session ends. Do not wait to be asked: a reboot loses the chat, and re-deriving a result costs
another round trip with a tester.

Write down what was measured, on which machine, and how. Cite the file and line when a finding
explains a specific check.

## Branching

Every release line has its own branch, cut from `master` and merged back after the release is
tagged. `master` always reflects the latest stable release; work for the next version never lands
on it directly.

```
0.18.0    the next release line
master    0.17.0, the last stable
```

Test builds and the final release are tagged from the release branch.

## Build and publish

Test builds go to `bin\<release>\Nitrous-<version>.exe` — the folder is the release line, the file name is
the full version including any test suffix, so a tester build is never mistaken for the release:

```
bin\0.14.0\Nitrous-0.14.0-test.36.exe
bin\0.14.0\Nitrous-0.14.0.exe
```

Publish with `publish.ps1`; it reads `<Version>` from `app/Nitrous.csproj`, publishes single-file,
names the file, refuses to overwrite an existing one, and prints the SHA-256:

```powershell
pwsh -NoProfile -File publish.ps1
```

Do not pass `-o bin\publishN` by hand any more, and do not override the version on the command line.

Never tag or publish a release unless asked. The version comes from the csproj; do not override it
with `-p:Version` on the command line or the binary will disagree with the tree.

**Publish once, after the commit.** The informational version embeds HEAD (`0.11.0-test.N+<sha>`),
so a build made before committing names the previous commit and does not match the tree it came
from. Order for every test build: bump the version and changelog, `dotnet build`, commit, then
publish into the next free `publishN`. Do not keep a pre-commit copy; the build already proves it
compiles. If a folder cannot be overwritten because a tester is running it, publish to the next
number and say which one they are on.

## Tools

`tools/` holds the development scripts worth keeping across sessions: the site screenshot
capture (run it elevated), the release-note dry run, the boot-logo probe and the Vibe
reflection harness. The folder is git-ignored on purpose — it is local tooling, not part of the
product — so it stays on this machine and never appears in the repository or the release.

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
