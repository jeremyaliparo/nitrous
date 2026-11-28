<div align="center">

<img src="assets/nitrous-hero.gif" alt="The Nitrous logo and wordmark inside a faint gauge bezel, with the background glow pulsing slowly" width="800">

### Pure hardware control for Acer Nitro laptops.
*No OEM service stack. No telemetry daemon. Just the firmware.*

<a href="https://github.com/jeremyaliparo/nitrous/releases/latest"><img src="https://img.shields.io/github/v/release/jeremyaliparo/nitrous?style=flat-square&logo=github&label=Download%20Latest&color=e60012&include_prereleases" alt="Latest Release"></a>
<a href="https://github.com/jeremyaliparo/nitrous/releases/latest"><img src="https://img.shields.io/github/downloads/jeremyaliparo/nitrous/total?style=flat-square&logo=github&label=Downloads&color=e60012" alt="Downloads"></a>
<a href="https://github.com/jeremyaliparo/nitrous/stargazers"><img src="https://img.shields.io/github/stars/jeremyaliparo/nitrous?style=flat-square&logo=github&label=Star%20Nitrous&color=ffb703" alt="GitHub Stars"></a>
<a href="https://discord.gg/PGWzb3mHbG"><img src="https://img.shields.io/badge/Discord-Join%20Chat-5865F2?style=flat-square&logo=discord&logoColor=white" alt="Discord"></a>
<a href="https://jeremyaliparo.github.io/nitrous/"><img src="https://img.shields.io/badge/Website-nitrous-e60012?style=flat-square&logo=data%3Aimage%2Fsvg%2Bxml%3Bbase64%2CPHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIyNCIgaGVpZ2h0PSIyNCIgdmlld0JveD0iMCAwIDI0IDI0IiBmaWxsPSJub25lIiBzdHJva2U9IiNmZmZmZmYiIHN0cm9rZS13aWR0aD0iMiIgc3Ryb2tlLWxpbmVjYXA9InJvdW5kIiBzdHJva2UtbGluZWpvaW49InJvdW5kIj48Y2lyY2xlIGN4PSIxMiIgY3k9IjEyIiByPSIxMCIvPjxsaW5lIHgxPSIyIiB5MT0iMTIiIHgyPSIyMiIgeTI9IjEyIi8%2BPHBhdGggZD0iTTEyIDJhMTUuMyAxNS4zIDAgMCAxIDQgMTAgMTUuMyAxNS4zIDAgMCAxLTQgMTAgMTUuMyAxNS4zIDAgMCAxLTQtMTAgMTUuMyAxNS4zIDAgMCAxIDQtMTB6Ii8%2BPC9zdmc%2B" alt="Website"></a>

[**Features**](#key-features) • [**Quick Start**](#quick-start) • [**Compatibility**](#compatibility) • [**Safety & Design**](#safety--non-destructive-design) • [**Troubleshooting**](#troubleshooting) • [**Build**](#building-from-source)

<br>

<p align="center">
  <a href="assets/dashboard.png"><img src="assets/dashboard.png" alt="Dashboard" width="23%" /></a>&nbsp;
  <a href="assets/gpu.png"><img src="assets/gpu.png" alt="GPU" width="23%" /></a>&nbsp;
  <a href="assets/keyboard.png"><img src="assets/keyboard.png" alt="Lighting" width="23%" /></a>&nbsp;
  <a href="assets/settings.png"><img src="assets/settings.png" alt="Setup" width="23%" /></a>
</p>

</div>

---

## Why Nitrous?

OEM control centers install heavy background service stacks, telemetry daemons, and wake your discrete GPU just to read a temperature sensor. **Nitrous** replaces all of it with a single portable executable:

- **Zero Idle dGPU Wake:** GPU telemetry is only polled while the GPU tab is actively open, and sleeping (`D3`) GPUs are checked via free Windows PnP state queries so battery life is never tanked.
- **Reversible Changes:** CPU power limits, processor boost mode, and disabled Acer services are backed up before they are touched and restored with one click.
- **Portable & Lightweight:** Ships as a single framework-dependent `.exe` with silent tray boot, configurable global hotkeys, and SHA-256 verified updates.

## Key Features

### Power and Thermals

**Modes that stick**<br>
Quiet, Balanced, Performance, and Turbo are applied directly through the EC, so the firmware's own CPU and GPU limits follow the mode.

**CPU power limits (`PL1` / `PL2`)**<br>
On Intel Nitro models with [PawnIO](https://pawnio.eu) already installed, sustained and burst limits are two 1 W sliders, held per power profile and per power source—AMD models never see the card. The sustained slider stops at the mode's own request and burst starts where sustained sits, so a cap can only lower what the firmware asks and `PL1` can never exceed `PL2`. The ceiling is the highest request the firmware has been seen to make for that profile, so a dip cannot shrink a cap. **APPLY** writes both, read back after the write and reverted if it does not stick; **RESET** follows the mode again.

**Fan control**<br>
Per-profile curves alongside Auto, Max, and manual 1–100% control. Instant spin-up paired with a gradual ramp-down stops brief CPU bursts from causing revving. CoolBoost appears only where the firmware supports it.

**Battery health**<br>
Charge limit, USB charging while the lid is off, and a health figure read directly from the battery itself.

**AC / DC memory**<br>
Optional separate power and fan modes for charger and battery, applied automatically when the plug state changes.

**App profiles**<br>
Send the power mode to Performance or Turbo while a chosen game or app is running, and optionally switch the fans to Auto, Max, or Custom, then hand the saved settings back when it closes. Rules match by executable, the first match wins, and a manual change during a game is respected rather than fought.

---

### GPU

**Clock offsets**<br>
Core and memory offsets per profile, paired with live clock, utilisation, power, and thermal readouts.

**Opt-in ownership**<br>
Clock writes are **off by default**, so external tools such as Afterburner are never overwritten by accident. When enabled, a background watchdog repairs driver clock drift.

**Vendor tools**<br>
Opens the NVIDIA App-or the Control Panel where that is what is installed-from the GPU page or with <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>N</kbd>.

**Thermal target and clock cap**<br>
The temperature the GPU throttles at, lowered inside the driver's own range (75-87 °C on an RTX 5060), and a core clock cap—held per power profile beside the clock offsets, with one **APPLY**. Both are read back after the write and re-applied on mode changes, at start-up and after resume; a profile with no value restores the driver's default target and clears the clock lock.

**GPU MUX (BIOS)**<br>
On models whose firmware exposes the switch and where NVIDIA Advanced Optimus is not the control, the built-in display can be set to **Hybrid** or **Discrete** (and **Automatic** where the firmware lists it). The mode is stored for the next boot, applied only after a confirmation, and never re-applied in the background. Advanced Optimus models switch the display live in the NVIDIA app instead, so Nitrous leaves the BIOS setting alone there.

**No idle wake**<br>
Polls the GPU only while its tab is open, checking a sleeping dGPU through Windows' cached PnP state rather than touching it.

---

### Display

**Refresh rate**<br>
Switch to any rate the panel advertises. `Auto` uses your chosen rate on AC and drops to 60 Hz on battery.

**LCD overdrive**<br>
Panel response overdrive wherever the display reports support for it.

---

### Lighting

**Global and Area presets**<br>
One effect across the keyboard and lid, or four per-zone colours with brightness and speed, through the keyboard's own HID controller or—on WMI-only Nitros—Acer's lighting setters, with the effects that controller implements.

**Screen and sound sync**<br>
Follow the screen's colours, run the active effect on audio, or combine screen colours with an audio beat—with four-band Spectrum, bass Beat, the five-step Vibe engine, saturation control, and a silence floor.

**Cover logo**<br>
Dedicated lid logo control with Static, Breathing, and Neon effects.

**Mode flash**<br>
Acknowledges a power-profile change in the mode's colour, matching the OEM software.

**Fit to hardware**<br>
Single-colour keyboards get one Colour chip instead of four dead zones, and models with no reachable controller omit the `LIGHT` tab entirely. WMI-only Nitros get the page through Acer's setters; where the BIOS only accepts zone colours, the effect and speed controls say so.

---

### Appearance

**Theme presets**<br>
Dark: Nitrous, Ice, Ember, and Mono. Light: Daylight, Frost, Sand, and Mono. A preset sets the accent, background, and sections together, and editing any one switches the picker to `Custom`.

**Accent and surfaces**<br>
Seven curated accents, `Auto` to follow Windows, or any custom colour—contrast-checked so a pick can never make a label unreadable. Light and dark each remember their own accent, background, and section choices; five dark window presets and four light ones rebuild the hover, border, and toggle shades around the colour.

**Light, dark, or system**<br>
Choose a mode or follow the Windows app theme. The two palettes are independent, so a light daylight look and a dark midnight look can each keep their own accent and surfaces.

**Fonts and scale**<br>
A font list built strictly from families installed on your PC, plus a whole-dashboard UI scale (90–125 %) that grows the window and every card, not just the text.

**Shape, light, and height**<br>
Rounded, Soft, or Square corners across the dashboard and the fan-curve editor, compact navigation that drops the bottom-bar labels, 92–100 % window opacity, and a bottom grip that changes only the window height and remembers it.

**Dashboard cards**<br>
Show or hide the temperature graph, power modes, display controls, and fans individually. Hidden cards keep working; only their place on the page goes.

**Celsius or Fahrenheit**<br>
Every reading follows the unit—chart axes and history ranges, thermal limits, the hover readout, and the curve editor—while the hardware is still read and written in Celsius.

**Toast and tray**<br>
The power-profile toast can sit at either screen edge, last 1–4 seconds, and scale 80–120 %. It comes in four designs—**Card** with the logo tile, **Compact**, **Minimal**, or **Nitro** with the angular N mark—and its colour follows the firmware's keyboard flash by default (Quiet white, Balanced orange, Performance crimson, Turbo violet), with each mode recolourable. The tray icon can show the logo, the active mode, or live CPU temperature.

**Keep awake**<br>
A coffee toggle in the title bar holds a Windows execution-state request so the machine and screen stay awake—no timer and no service, and the tray keeps it after the dashboard is closed.

---

### System and Diagnostics

**Service disabler**<br>
One-click disable—and exact restore—of the stock Acer background services.

**Diagnostics**<br>
A redacted export bundle and a read-only EC probe so an unsupported Nitro model can be compared against one that works.

## Quick Start

### 1. Check Requirements

| Component | Requirement |
| :--- | :--- |
| **OS** | Windows 10 or Windows 11 (x64) |
| **Runtime** | [**.NET 10 Desktop Runtime (x64)**](https://dotnet.microsoft.com/download/dotnet/10.0) *(Required — the app will not start without it)* |
| **Privileges** | Administrator (requested automatically via app manifest) |
| **Hardware** | Acer Nitro laptop with the gaming WMI interface (probed at startup; 2019 models confirmed) |

### 2. Download & Verify

Nitrous is completely portable and requires no installer.

1. Download `Nitrous.exe` and `SHA256SUMS` from the [**Releases**](https://github.com/jeremyaliparo/nitrous/releases) page.
2. *(Recommended)* Verify the executable checksum in PowerShell against `SHA256SUMS` or `Nitrous.exe.sha256`:
   ```powershell
   Get-FileHash .\Nitrous.exe -Algorithm SHA256
   ```
3. Move `Nitrous.exe` to a permanent folder (e.g., `C:\Tools\Nitrous`) and launch it.

> [!TIP]
> Nitrous runs silently in your system tray. Click the tray icon or press your dashboard hotkey (**Ctrl + Home** by default, rebindable on the Setup page) to summon the dashboard. Configure AC/battery automation rules under **Setup**.

---

## Compatibility

Designed for Acer Nitro laptops that expose the gaming WMI classes and, on some models, an embedded-controller HID interface. The year does not decide it: a 2019 AN517-51 runs Nitrous the same way a 2025 16S does, and a feature the firmware does not answer simply stays hidden.

**Confirmed Models:**
- **Acer Nitro 5** (`AN517-51`, 2019)
- **Acer Nitro 16S** (`AN16S-61`)
- **Acer Nitro V 15** (`ANV15-41`<!--, `ANV15-52` — *Thanks [@Baymax0251](https://github.com/Baymax0251) for testing!* -->)

> [!NOTE]
> **Unlisted models work out of the box** if they follow the same Acer protocol—nothing is hardcoded to a model string. Nitrous inspects your SMBIOS tables, available WMI methods, and EC HID interfaces at startup, enabling every control your firmware supports and disabling or hiding what it doesn't. If a feature is greyed out on your machine, run **Setup → Diagnostics → EC PROBE** and open an issue.

---

## Safety & Non-Destructive Design

> [!CAUTION]
> Nitrous writes directly to your Embedded Controller (EC) firmware and GPU driver. It is an unofficial project not affiliated with Acer—use it at your own risk.

Nitrous is engineered so that every system change is clamped, isolated, or backed up before a single byte is written:

- **Hard Fan Floor (1%):** Manual speeds and custom curves are strictly clamped to a minimum of **1%** in both the UI and the WMI layer so stale registry values can never zero-out your fans.
- **CPU Limit Backups:** Original AC/DC min/max processor states are backed up in `HKCU\Software\Nitrous` (`CpuPowerBackup_<scheme>_{AC|DC}_{Min|Max}`) and restored on exit or recovery from an abrupt crash. Do not delete these keys while CPU limits are active.
- **Processor Boost Backups:** Backed up under `CpuBoostBackup_<scheme>_{AC|DC}`. Since boost mode is a persistent preference, it stays active across reboots—use **Setup → Processor Boost Mode → RESTORE** to revert to Windows defaults.
- **Acer Service Snapshots:** Disabling OEM services is strictly opt-in. Before stopping anything, Nitrous snapshots the exact start types, delayed-auto flags, and running states to `%LOCALAPPDATA%\Nitrous\acer-services-backup.json`. Use **Setup → Acer Background Services → RESTORE** to revert. *(Note: Services already disabled prior to using Nitrous have no baseline and will not be touched).*
- **Opt-In GPU Clock Ownership:** **Nitrous manages GPU clocks** is **OFF by default** (including across updates).
  - *When OFF:* Nitrous is strictly read-only. Startup, profile switches, hotkeys, AC/DC transitions, resume, the 30-second watchdog, APPLY, and RESET perform zero GPU writes. Turning the switch off never writes on its own, though it will offer to clear offsets previously applied by Nitrous.
  - *When ON:* Saved per-profile P0 core/memory deltas (`0/0` = stock) are applied, verified, and maintained by the watchdog. Because Nitrous writes only P0 clock deltas and never voltage, it cannot preserve an external MSI Afterburner voltage/frequency curve—**run only one clock manager at a time**.
- **Honest Battery Telemetry:** The wattage indicator next to the power pill shows the live Windows battery charge/discharge rate, never a fabricated adapter wattage rating.

---

## Troubleshooting

<details>
<summary><b>GPU offsets keep resetting to 0</b></summary>

- Ensure the background tray process is running (the clock watchdog lives in the tray process).
- Verify **Nitrous manages GPU clocks** is turned **ON** on the GPU page.
- Check `%LOCALAPPDATA%\Nitrous\logs\nitrous.log` for `GPU OC`, `NVAPI`, or `NVML` warnings. Every write logs its trigger (`startup`, `profile-click`, `hotkey`, `acdc`, `watchdog`, `apply`, `reset`) along with old and new offsets.
- **Note on sleeping GPUs:** A dGPU in `D3` sleep cannot be read or written. Nitrous checks the Windows PnP power state without waking the card and skips polling until the GPU wakes up, preventing false `0` overwrites.
- If another utility resets the P0 state, the Nitrous watchdog repairs it within 30 seconds.
</details>

<details>
<summary><b>MSI Afterburner sliders or mV curve reset when Nitrous starts</b></summary>

- Nitrous and Afterburner must not manage clocks simultaneously. Either turn **Nitrous manages GPU clocks** **OFF** in Nitrous, or zero out Afterburner's Core/Memory sliders and let Nitrous handle per-profile offsets.
- Nitrous writes a sparse P0 packet with `0` base-voltage entries, which replaces the driver's active P0 override and drops custom voltage/frequency curves.
- If an external voltage curve is present, `gpu.json` and the log flag `voltageOverridesDetected` to aid bug reporting.
</details>

<details>
<summary><b>Fans are not following my custom curve</b></summary>

- Custom curves only execute when the fan mode is set to **Custom/Medium** and curve mode is toggled on.
- Confirm Nitrous is running with Administrator privileges and your laptop exposes `AcerGamingFunction`.
- Check the log for WMI write failures; if a write fails, the engine automatically retries on the next 2-second tick.
</details>

<details>
<summary><b>Acer hotkeys or lighting stopped working</b></summary>

- Open **Setup → Acer Background Services** and click **RESTORE** to re-enable and restart the OEM services.
- If the status shows *already disabled; no Nitrous backup to restore*, the services were disabled outside of Nitrous—re-enable them via Windows Services (`services.msc`).
- If a restore is only partially successful, `%LOCALAPPDATA%\Nitrous\acer-services-backup.json` is retained so you can retry.
</details>

<details>
<summary><b>CPU min/max values look wrong or Boost Mode names differ</b></summary>

- **CPU Min/Max:** Nitrous restores the exact baseline captured before it modified your power scheme (not a hardcoded `5%/100%`). To reset manually, toggle the CPU power feature off, wait for the log confirmation, and exit Nitrous.
- **Boost Modes:** Available modes are queried directly from the active Windows power plan and system firmware, falling back to standard documented modes if Windows exposes none. Click **RESTORE** to return to your original AC/DC boost settings.
</details>

<details>
<summary><b>A control is greyed out / disabled</b></summary>

Nitrous disables controls when your firmware does not respond to them rather than failing silently. Hover over any disabled control to see the exact reason:
- **Keyboard Zones:** Requires a 4-zone RGB keyboard; single-colour models get one Colour chip instead.
- **LIGHT page:** A model with no reachable keyboard-lighting controller gets no tab at all. Its Fn keys may still change the backlight, but no user-mode interface exposes that.
- **Cover Logo:** Confirmed once with a test colour on the first visit. If the lid's "N" did not light up, the effect controls are replaced by an explanation and nothing further is written to that device.
- **Keyboard Auto-Off:** Requires the embedded-controller interface and now lives in **Setup → Keys**. On models without it Nitrous cannot reach the setting, even where the OEM software still offers it.
- **USB Charging While Off:** Requires an Acer WMI class that is only partially implemented on certain BIOS versions.
- **CoolBoost:** Offered only where the firmware itself says APGeAction function 7 is CoolBoost. On firmware that uses that function for the power mode the switch stays hidden, because writing it would change the mode.

**LCD Overdrive** is the exception: it is a panel property, so where the display reports no support the control is hidden entirely rather than greyed out, and the refresh list fills the row.
</details>

<details>
<summary><b>Audio crackles, or LatencyMon reports DPC/ISR spikes, while sound sync is on</b></summary>

- **On a five-step backlight** (a keyboard with no lighting controller) the beat effect changes the backlight through the firmware's ACPI interface. On some Acer laptops that firmware traffic starves the audio driver: measured on an ANV15-51, LatencyMon flagged audio latency in **10 of 10** runs with the effect on, and in 1-2 of 10 with it off.
- **Pulse with sound** is off by default. The LIGHT page marks this on those keyboards; leave it off if you hear crackle. Nothing else in Nitrous uses that path continuously.
- Keyboards with a lighting controller are unaffected: those steps are HID feature reports, not ACPI calls.
</details>

<details>
<summary><b>Auto-update says "manual download required"</b></summary>

- The release did not publish a GitHub asset digest or checksum file. Visit the Releases page and download the update manually after checking the hash.
</details>

### Reporting Bugs & Diagnostics

If you encounter a bug or unsupported hardware control, attach a diagnostic bundle to your GitHub issue:

1. Open **Setup → Diagnostics** and click **Export Diagnostics**.
2. Nitrous saves a redacted ZIP to `%LOCALAPPDATA%\Nitrous\diagnostics` and highlights it in File Explorer.
3. Attach `Nitrous-Diagnostics-*.zip` to your issue.

> [!NOTE]
> **Privacy First:** Nothing is uploaded automatically. Usernames, machine names, user-profile paths, SIDs, emails, and IP/MAC addresses are scrubbed before the ZIP is created, and BIOS, motherboard and memory serial numbers plus system UUIDs are never collected at all. You can inspect `summary.txt` and `errors.txt` inside the archive before sharing.

### File & Registry Locations

| Path | Description |
| :--- | :--- |
| `%LOCALAPPDATA%\Nitrous\logs\nitrous.log` | Application logs (rotated at 1 MB, up to 3 archives; tagged by process role and PID). |
| `%LOCALAPPDATA%\Nitrous\diagnostics` | Exported diagnostic ZIP bundles. |
| `%LOCALAPPDATA%\Nitrous\acer-services-backup.json` | Snapshot of original Acer service start types and running states. |
| `HKCU\Software\Nitrous` | User preferences plus CPU power and boost baseline backups. |

---

## Building From Source

Requires the **.NET SDK 10.0.400** or newer on Windows (pinned via `global.json` with `rollForward: latestFeature`).

```powershell
# Restore and build Release
dotnet restore app/Nitrous.csproj
dotnet build app/Nitrous.csproj -c Release

# Publish a framework-dependent single-file executable
dotnet publish app/Nitrous.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o dist
```

Visual Studio users can also publish directly using `app/Properties/PublishProfiles/win-x64.pubxml`.

<details>
<summary><b>Release Architecture & Auto-Updater Verification</b></summary>

> [!WARNING]
> `Nitrous.exe` is **not code-signed**. Always download from official GitHub Releases and verify the SHA-256 hash.

The release workflow supports both `MAJOR.MINOR.PATCH` and `vMAJOR.MINOR.PATCH` tags (numeric tags are used starting with `0.8.1` for backward compatibility with `0.7.2`'s `Version.TryParse` updater) and publishes `Nitrous.exe`, `SHA256SUMS`, and `Nitrous.exe.sha256`.

Before replacing the executable during an auto-update, Nitrous enforces a strict verification pipeline:
1. Queries the latest GitHub release for a `sha256:<hex>` asset digest.
2. Falls back to downloading and parsing `Nitrous.exe.sha256` or `SHA256SUMS` from the release assets.
3. Refuses the automatic update and prompts for manual download if neither checksum source is present.
4. Validates that the downloaded binary is a valid PE image, matches a plausible file size, and passes the SHA-256 check before renaming the active binary to `Nitrous.exe.old` and spawning the updated process with `--updated` (rolling back automatically if replacement fails).
</details>

---

## Licence

Nitrous is MIT licensed. Third-party components — the LGPL-3.0 NVIDIA API wrapper it ships, its
.NET dependencies and the site's fonts — are listed with their licence texts in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

<div align="center">
<sub>Unofficial open-source utility. Not affiliated with or endorsed by Acer Inc. Use at your own risk.</sub>
</div>
