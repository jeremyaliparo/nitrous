# Changelog

All notable changes to Nitrous are documented here. The GitHub release for a version is
published from the matching section of this file.

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
