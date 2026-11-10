# tools

Development scripts that do not ship with the app. Nothing here is referenced by
`Nitrous.csproj`, the release workflow or the site.

| File | What it does |
|---|---|
| `capture-site-shots.ps1` | Re-takes the four `docs/assets` screenshots from a build, with the preset, backlight and registry handling that makes them presentable. |
| `check-release-notes.ps1` | Runs the release workflow's note extraction against `CHANGELOG.md` locally and prints the title, line count and marker state. |
| `probe-boot-logo.ps1` | Read-only probe for the firmware's custom boot logo: SMBIOS game record 0x0D and Acer's misc-8 switch. |
| `vibecheck/` | Reflection harness that feeds synthetic audio through `LightingSync+SoundEnvelope` and prints the Vibe step per 20 ms tick. |
| `asinvoker.manifest` | Lets the capture build run without the app's administrator manifest. |

## Screenshots

```powershell
# Elevated once; the UAC prompt is expected. Non-elevated captures draw "--" for
# temperatures and fan RPM.
Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File',
  'D:\Users\Jeremy\Software Engineering\personal\nitrous\tools\capture-site-shots.ps1' -Wait
```

The script builds `app\Nitrous.csproj` with `asinvoker.manifest`, sets a presentable
Global/Neon preset, captures dashboard, GPU, LIGHT and SETUP at 375x660 into `docs\assets`,
and restores the registry and the EC backlight afterwards.

Two things it cannot fix:

- The LIGHT page shows "Fn keys are at N%" when the physical backlight differs from the
  saved preset, so that shot is taken with sound sync on (which hides the hint and also puts
  the SYNC card in view).
- The GPU page shows `Asleep` with `--` clocks when the dGPU is in runtime D3. Do not wake it
  for a screenshot; re-shoot while the machine is already using the dGPU.

## Boot logo probe

```powershell
# Read-only; run it elevated for the WMI half.
pwsh -File tools\probe-boot-logo.ps1
```

Parses SMBIOS type 0xAC directly (game record 0x0D) and asks
`AcerGamingFunction.GetGamingMiscSetting(0x08)`, the BIOS setup's "Customize POST animation"
switch. Neither call writes anything and neither touches the EFI system partition. On the
AN16S-61: record 0x0D = 1 and misc 8 answers `0x100` (valid, switch off).

The WMI half needs elevation; without it the class answers "Access to a CIM resource was not
available to the client" and only the SMBIOS result is printed.

## Release notes

```powershell
pwsh -File tools\check-release-notes.ps1 -Version 0.11.1
```

Mirrors `release.yml`: finds `## <version>`, stops at the next `## `, strips
`<!-- release-name: ... -->` and reports the title. Exit code 1 on a missing or empty
section, like the workflow.

## Vibe harness

```powershell
dotnet run --project tools\vibecheck -c Release
```

Builds `app` first, then reflects into `Nitrous.dll` so it always tests the real code
rather than a copy. The timeline is a 500 Hz pad, a 60 Hz kick at 0.5 s, a noise hat at
1.0 s and silence from 1.6 s; each line is one 20 ms tick with the Vibe step and the band
levels. Pass a DLL path as the first argument to test a different build.
