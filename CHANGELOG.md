# Changelog

All notable changes to Nitrous are documented here. The GitHub release for a version is
published from the matching section of this file.

## 0.8.5 - 2026-10-27

### GPU page

- `RESET` and `APPLY` now share one compact action row inside the `CLOCK OFFSETS` card instead
  of a separate full-width button section, so the page uses less vertical space.
- Clock offsets are positive-only. Core is `0..300 MHz`, memory is `0..1000 MHz`, and the
  driver range check still rejects values the VBIOS does not advertise.
- Positive offsets display with an explicit `+` prefix in the value box and status messages.
  Saved negative offsets from older versions are clamped to `0` on first run.
- A sleeping dGPU is now detected from NVAPI/NVML failures. Nitrous pauses automatic clock
  writes instead of probing the GPU every few seconds, and only allows one wake probe per
  minute. Explicit `APPLY` still retries once.
- `Deep GPU Telemetry` turns itself off after three failed NVML polls and the GPU page explains
  why, so a sleeping GPU is not held awake by a two-second telemetry loop.

### Hotkeys apply immediately

- Editing a hotkey on the `KB` page signals the tray process directly, so the new binding is
  registered right away instead of requiring a Nitrous restart. The existing two-second
  registry poll remains as a fallback, and registration now runs on the tray's message thread.
- The page no longer says a restart is required.

### Dashboard launch

- Pressing the dashboard hotkey repeatedly no longer opens multiple dashboard windows. The tray
  now remembers a dashboard that is still starting, ignores duplicate launch requests for two
  seconds, and reuses the launched process. A dashboard single-instance mutex is also a hard
  backstop for any other launch path.

### Dashboard position recovery

- The dashboard validates its saved position against the current monitor layout. If the title
  bar has no reachable spot (for example, the external monitor it was on was removed), it
  reopens centered on the primary display.
- A `DisplaySettingsChanged` handler recovers a dashboard that is already open when a monitor
  is unplugged.
- The tray menu has a new **Reset Dashboard Position** item. It clears the saved coordinates
  and, if the dashboard is open, moves it back to the primary work area.

## 0.8.4 - 2026-10-25

### Lower idle memory

- NVML is now initialized on demand instead of at tray startup. The tray no longer maps the
  ~23 MB NVIDIA driver image just to sit in the notification area; it loads only when the fan
  curve engine needs the NVML temperature fallback or the dashboard requests deep telemetry.
- Shutdown and re-initialization paths only touch NVML when this process actually loaded it,
  so a normal tray exit cannot pull the driver in by accident.
- The dashboard's software-rendering setup moved out of tray startup. Starting the tray no
  longer creates a WPF composition device for a process that only hosts a WinForms tray icon.
- Net effect: the idle tray process is roughly 20 MB smaller in private commit and working
  set. Dashboard launch is unchanged or slightly faster, because NVML initialization now runs
  off the UI thread alongside the existing telemetry polling.
- Per-profile GPU offsets, the 0.8.3 clock-ownership switch, the 30-second watchdog, and the
  diagnostics bundle behave exactly as before.

### Redesigned power-profile overlay

- The hotkey toast is a new card: Nitrous logo tile, `POWER PROFILE` caption, the profile name,
  a per-profile accent bar, and a status dot. Quiet is green, Balanced blue, Performance amber,
  Turbo red.
- The card is rendered once per profile and resolution into a layered-window surface, then
  animated by changing only its opacity and position. There is no per-frame text rendering and
  no WPF dependency, so the overlay does not hitch the tray or the foreground app.
- The overlay scales with the monitor's DPI and clamps to roughly 30% of the work area width on
  small panels. It appears on the monitor that owns the active window, falling back to the
  monitor under the cursor.
- A second profile change while the card is visible swaps the content in place instead of
  flashing the toast out and back in.
- The toast is shown as soon as the hotkey saves the new profile, before the WMI/NVAPI writes
  finish, so a sleeping dGPU no longer delays it. Surfaces are pre-warmed after boot, so the
  first hotkey does not pay the first-render cost.
- The app declares per-monitor V2 DPI awareness, and the overlay respects the reduced-motion
  setting by skipping the fade when UI animations are disabled.

## 0.8.3 - 2026-10-24

### GPU clock ownership is now opt-in

Nitrous no longer writes GPU clocks unless you explicitly allow it.

- Added a **Nitrous manages GPU clocks** switch in the `CLOCK OFFSETS` card on the GPU page.
- The switch defaults to **off**, including for existing installs. Updating to 0.8.3
  immediately stops every automatic GPU write. The state is not inferred from existing
  per-profile offsets, so a machine that was tuned by another tool is not taken over by an
  update.
- While the switch is off, Nitrous is read-only for GPU clocks. Startup, power-profile
  cards, the power-cycle hotkey, AC/DC switching, resume, the 30-second watchdog, **APPLY**,
  and **RESET** all skip the write. Telemetry, current clocks, temperatures, and the live
  offset readout keep working.
- While the switch is on, Nitrous owns the P0 core and memory offsets:
  - saved per-profile offsets apply at startup, profile switches, hotkeys, AC/DC changes,
    resume, and every watchdog tick;
  - a profile saved as `0/0` means stock clocks for that profile and does write `0/0`;
  - **APPLY** saves and verifies the current values, and **RESET** returns the active profile
    to stock.
- The switch is enforced inside `NvidiaGpuManager`, not only in the UI, so no call site can
  bypass it. The tray reads the shared setting on every watchdog tick, so toggling it in the
  dashboard takes effect immediately without restarting Nitrous.
- The sliders, offset boxes, **APPLY**, and **RESET** are disabled while the switch is off,
  and the card states that the displayed offsets come from the driver rather than Nitrous.

### Warnings and safe cleanup

- Enabling the switch now explains exactly what Nitrous will write, that `0/0` returns the
  GPU to stock, and that Nitrous cannot preserve another tool's voltage/frequency curve.
- If `MSIAfterburner.exe` is running, the warning is stronger: two tools writing GPU clocks
  will fight and an mV curve may reset. It asks you to set Afterburner's Core and Memory
  sliders to `0` or to close Afterburner first. If Afterburner is registered to start with
  Windows, the warning says so too.
- Turning the switch off never writes by itself. If Nitrous recorded the offsets it last
  applied, it offers to clear them once:
  - **Yes** writes `0/0` only when the GPU still reports exactly the offsets Nitrous applied;
  - **No** leaves the current offsets alone;
  - **Cancel** keeps GPU clock management on.
- Nitrous records those offsets (`GpuLastAppliedCore` / `GpuLastAppliedMemory`) after a
  verified write, so the cleanup offer cannot clobber a value another tool has since set.

### Diagnostics

- Every GPU clock write now logs its trigger (`startup`, `startup-apply`, `profile-click`,
  `hotkey`, `acdc`, `watchdog`, `apply`, `reset`, `enable`, `disable-clear`) together with the
  previous and new offsets.
- `gpu.json` and `summary.txt` now report `gpuClockManagement`, `lastAppliedOffsets`,
  `voltageOverridesDetected`, `baseVoltageCount`, `generalVoltageCount`, and the P0 base
  voltage entries with their editable flag and mV values.
- A clock write that sees an external voltage override logs a warning, so the next
  diagnostics bundle shows whether Nitrous wrote while an Afterburner curve was active.

### Behavior changes to be aware of

- An update no longer applies your saved GPU offsets until you turn the switch on. If you
  rely on Nitrous for per-profile OC, enable it once on the GPU page.
- With the switch on, switching to a `0/0` profile now returns the GPU to stock clocks.
  Previously an automatic path could leave the previous profile's offset in place.
- Startup applies a stock profile only when management is on, and a mid-boot disable stops
  the retry loop instead of writing once the driver appears.
- Fan curves, CPU power limits, processor boost, charge limit, refresh switching, Acer
  service control, hotkeys, OSD, and the updater are unchanged.

### Notes

- Nitrous still writes only P0 Graphics and Memory clock deltas through a sparse
  `SetPstates20` request and never writes voltage. It cannot preserve an external mV curve,
  so Nitrous and MSI Afterburner should not both apply GPU clocks.
- Offsets remain per power profile. `0/0` is a real value when management is on, not a
  "leave whatever is there" placeholder.

## 0.8.2 - 2026-10-24

- Battery charge and discharge telemetry now refresh after resume and AC/DC changes instead
  of showing a stale rate.
- The Acer background services page clearly reports when no restore baseline exists, instead
  of implying that a restore is available.

## 0.8.1 - 2026-10-24

- Added processor boost mode selection with separate AC and battery modes, original-value
  backups, and restore.
- Added Acer background service controls with an exact start-type and running-state backup
  and a matching restore.

## 0.8.0 - 2026-10-23

- Added a redacted diagnostics export from **Settings → Diagnostics**.
- Restored the power-profile OSD and hardened hardware, ACPI, and GPU state handling.
- Built-in GPU profile defaults are stock `0/0`, and legacy automatic offsets are migrated
  to `0/0` on first run.
