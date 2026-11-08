# Changelog

All notable changes to Nitrous are documented here. The GitHub release for a version is
published from the matching section of this file.

## 0.11.0-test.12 - 2026-11-08

### Lighting

- With **Screen sync** on, only brightness is adjustable: Global Colour greys to `Auto`, and the Area
  Static/Dynamic selector, effect, speed, zone swatches and single-colour chip are disabled. A hint
  says the screen owns the colours.
- The **Cover logo** panel follows the Follow target. `Keyboard + lid logo` disables its controls and
  TEST LID LOGO with a hint; `Keyboard` leaves the logo editable. Sound sync keeps the preset colours
  as its base and leaves the colour editors usable.

## 0.11.0-test.11 - 2026-11-08

### Lighting

- Screen sync gains a **Screen saturation** slider (0–200%): 0 is grey, 100 leaves the captured
  colours unchanged, 200 boosts them. It transforms the RGB values before they are written; the
  keyboard LEDs have no hardware saturation control.

## 0.11.0-test.10 - 2026-11-08

### Lighting

- An old **Yes** from the ambiguous red lid-logo test is invalidated once, so the green test runs
  again. Until a logo is explicitly confirmed, the Follow selector stays on **Keyboard** and the lid
  option is disabled.
- Keep the mode flash on if the power-mode key's colour is wanted; that key is firmware-driven and
  is not a separately addressable cover logo.

## 0.11.0-test.9 - 2026-11-08

### Lighting

- The lid-logo check now writes **green**, not red: the power-mode palette already includes red
  (Performance), so a red test on a Performance machine looked unchanged. The prompt asks for green
  or another non-mode colour, and says to choose No when the logo only follows the power mode.
- The unavailable panel explains that same case, so a mode-only lid logo is hidden instead of
  offering controls that do nothing.

## 0.11.0-test.8 - 2026-11-08

### Lighting

- Sync now uses the **active preset's static colours** as its base: Global's one colour, or Area's
  four zone colours. Editing a colour while sync is on takes effect on the next refresh instead of
  being ignored, and switching Global/Area switches the base.
- Brightness follows the active preset's slider (Area or Global), not always Global.
- The Global colour chip stays editable while sync is on, even for an animated preset. The shared
  card now says the preset's colours are the base and that turning sync off is only needed to edit
  the effects.

## 0.11.0-test.7 - 2026-11-08

### Lighting

- **Beat** joins Peak and Spectrum in the Sound mode list. It watches the bass band for onsets,
  requires a 140 ms gap between hits, and fades a short flash, so it lands on kicks instead of
  following every transient. The sensitivity slider sets its threshold.

## 0.11.0-test.6 - 2026-11-08

### Lighting

- Sync is now one shared card under the Global/Area tabs, so it works from **Area → Keyboard**
  as well. A **Follow** selector chooses `Keyboard` or `Keyboard + lid logo`.
- Sound sync has **Peak** and **Spectrum** modes. Spectrum runs a 2048-point FFT on WASAPI loopback
  and drives bass / low-mid / high-mid / treble into the four zones; Peak keeps the single
  brightness pulse.
- Switching Global/Area no longer suspends sync. The saved preset returns when it is turned off.

## 0.11.0-test.5 - 2026-11-08

### Lighting

- **Sound sync now reacts to beats, not overall loudness.** The raw peak is compared with a slow
  baseline and the transient drives the brightness, with a fast release. The previous square-root
  gain pushed most music to full, which is why it looked static.
- Silence turns the keyboard off completely. A **Sound sensitivity** slider (1-10) appears while
  Sound sync is on.

## 0.11.0-test.4 - 2026-11-08

### Lighting

- **Sound sync** on the LIGHT page under Global: the keyboard and cover logo pulse with the default
  audio output (system audio, not the microphone). It is a software effect like screen sync, runs in
  the tray, and the two sources are mutually exclusive.
- Both syncs now share one tray worker, restore path and power-mode pause. Turning either on raises a
  zero brightness to 100 so the effect is visible.
- Settings are unified as `KbSyncEnabled`/`KbSyncSource`; the screen-only prototype keys migrate on
  first launch.

## 0.11.0-test.3 - 2026-11-08

### Lighting

- **Screen sync is remembered across the Global/Area tabs.** Opening Area suspends it and applies the
  Area preset; returning to Global resumes the same switch state instead of turning it off.
- The sync worker **pauses for 1.5 seconds during a power-mode flash**, so the controller's
  acknowledgement is not overwritten by the next frame.

## 0.11.0-test.2 - 2026-11-08

### Lighting

- **Prototype screen sync.** A new **Screen sync** switch on the LIGHT page under Global makes the
  four keyboard zones follow the screen's colours, with the cover logo following the average. It is
  a software effect: the tray writes the zones continuously, so it keeps running when the dashboard
  is closed, and turning it off restores the saved Global or Area preset. Protected content and true
  fullscreen can block the capture, and it is not a firmware effect.

## 0.11.0-test.1 - 2026-11-08

### Keyboard

- **Backlight brightness** on models without a lighting controller: a five-step row in
  **Setup → KEYBOARD** (`Off`, `25`, `50`, `75`, `100`). It uses the EC HID interface where the
  model has one and `APGeAction` where it does not, snaps to the firmware's levels, and preserves
  the auto-off timeout. Toggling auto-off no longer resets the brightness.
- **EC probe version 12** records the keyboard type and both brightness paths, plus which one the
  model uses, so coverage can be mapped from a diagnostic report without writing anything.

### Dashboard

- The **Whisper Mode** label now follows the request without waiting for a mode change, so it no
  longer disappears while Quiet is still asking for it. Releasing it in another mode still hides
  it.

## 0.10.0 - 2026-11-08

### Lighting

- The `KB` page is now `LIGHT`, and it drives the lid's cover logo as well as the keyboard. Both use
  the keyboard's own HID controller: no Acer service, driver or pipe is involved.
- **Global** and **Area** are two presets. Opening one applies it: Global writes both devices, Area
  re-applies the keyboard and the logo from their own settings. They keep separate settings, and the
  preset in use is remembered and applied on the next launch.
- **Global speed has five steps, each a matched pair** captured from the OEM software: the keyboard
  takes 1/3/5/7/9 while the cover logo takes 1/2/3/4/5. Sending the same number to both made the two
  drift apart above 5, because the logo has five steps and the keyboard nine.
- Colour applies to **Static** only. The animated effects run their own palette, so Global greys its
  colour to `Auto` and the Dynamic panel does not offer one.
- **Static** keeps the four per-zone colours; **Dynamic** sets one effect across all four with its
  own speed (1-9). Switching between them applies the selected one immediately, and Dynamic no
  longer lists Static, which is not an animation.
- The Area tab is a single card: the Keyboard/Cover logo selector sits at the top under a `DEVICE`
  label and a hairline, then the mode selector, then the settings. Brightness comes before Speed in
  every view, and the segmented buttons carry icons.
- A **single-colour keyboard** gets one Colour chip instead of four zone swatches that cannot work.
- The cover logo supports `Static`, `Breathing` and `Neon`; Breathing and Neon cycle their own
  palette (the colour reads `Auto`) and the logo has five speed steps. Selecting an effect it does
  not implement leaves it on its previous mode, which is what the OEM software does.
- **The lid logo is confirmed once.** On the first visit to Lighting, Nitrous writes a test colour to
  the lid and asks whether the "N" lit up. The answer is remembered, nothing further is written if it
  did not, and the check can be repeated with **TEST LID LOGO** or **CHECK AGAIN**.
- **Power mode change flash**, on by default, reproduces the OEM acknowledgement: the keyboard blinks
  in the mode's colour, and the firmware brings the power/mode key and the lid with it. The
  controller looks the colour up in a fixed palette, so the values Acer sends are used - white,
  orange, red and violet; anything else falls back to orange.
- The keyboard and the cover logo animate on independent controllers, so their pulses are not
  phase-locked. The Global card says so rather than promising a synchronisation the hardware does
  not have.
- Keyboard backlight auto-off moved to Setup, next to the hotkeys.
- A model with no reachable keyboard-lighting controller gets **no LIGHT tab** instead of a page of
  disabled controls. Its Fn keys may still change the backlight, but no user-mode interface exposes
  that, so there is nothing a control could do.

### Appearance

- Nitrous takes its accent colour from seven fixed accents on the Setup page, plus `Auto` (follow
  Windows) and a **custom picker for any colour**. The choice is remembered; a custom colour is
  stored as its hex.
- Custom colours added with **Add to Custom Colors** are remembered across sessions and shared by
  every colour picker: accent, Global, keyboard zones and cover logo.
- The accent is contrast-checked before use, and every colour in the interface comes from one
  palette rather than ~270 literals. Status colours stay fixed, so a temperature crossing a
  threshold looks the same whatever accent you pick.
- The GPU page's **APPLY** button follows a live accent change instead of keeping the one it started
  with.

### Hotkeys

- `Ctrl + Home` opens the dashboard out of the box, seeded only on a machine that has never stored a
  dashboard binding, so a binding you cleared stays cleared.
- Cycling the power profile stays unbound by default: changing a machine's power mode from a key
  press you forgot you set is a worse surprise than having no binding.
- The binding boxes live on the Setup page and are taller, so a shortcut reads as a key rather than
  as a text field that happens to contain one.

### Quiet mode

- Quiet now also tells NVIDIA's driver that a client asked for WhisperMode, which is what the NVIDIA
  App reads before it offers its WhisperMode controls. It does not change the power cap or the fans.
- The POWER row says `Whisper Mode: On` while it is requested, and the label disappears as soon as
  another mode releases it.

### Setup

- A new `KEYBOARD` section holds the global hotkeys, Sticky Keys and the keyboard backlight auto-off.

### Updates

- A test build is told plainly what it is when a release exists: "You're running a test version of
  Nitrous (…). The release version … is available on GitHub." Stable installs keep the prompt they
  have always had, and a release outranks a test build of the same version.

### Diagnostics

- EC probe version 11 lists Acer's own HID interfaces (`1025`, `0CF2`) and the keyboard controller
  vendors Acer ships (`04F2`, `05AF`, `0D62`). That separates "this model has no lighting
  controller" from "this model's keyboard is a USB device Nitrous does not speak".

### Not in this release

- **PL1/PL2 are not adjustable.** They need MSR `0x610`, which is ring0, and shipping a kernel driver
  for one setting would change what Nitrous is. The EC table some models expose is a capability
  record, not the applied limits. ThrottleStop remains the right tool for that.

## 0.9.0 - 2026-11-01

Nitrous 0.9.0 is the first release for external testing. It is not a stable release: several
controls depend on firmware that varies between models, and a few are still out of reach.

### Power modes

- `QUIET / BALANCED / PERFORMANCE / TURBO` now actually change the laptop's operating mode, and
  the enforced GPU power limit follows it. On the AN16S-61 that is 80 / 85 / 100 / 115 W.
- Nitrous used to write the Acer WMI misc register for this. The firmware stores that value and
  echoes it back but does not apply it, so the buttons looked like they worked and changed
  nothing. On models with an embedded-controller HID interface the mode is set there instead, and
  the mode is read back after every change so a refused one is never shown as active.
- Models without that interface keep the WMI path.
- That path is not a fallback in name only. On an ANV15-41, which has no embedded-controller
  interface, `Performance` moves the enforced limit to 65 W ΓÇö confirmed with `nvidia-smi`, not
  assumed from the register.
- Turbo is offered whenever the channel Nitrous actually uses lists it, not only when the WMI
  register advertises it, so models that used to hide the button now show it. A failed read is
  retried instead of being remembered for the session.

### Fans

- Fan curves rise instantly but no longer fall instantly. When the target drops, the fans hold for
  3 seconds and then step down at a chosen rate, so a short burst of load no longer makes them rev
  up and drop again.
- **Ramp down** in the curve editor: `Off`, `Gentle` (5 %/s), `Balanced` (10 %/s, default) and
  `Fast` (20 %/s). `Off` keeps the previous behaviour.
- The ramp never applies at or above 90 ┬░C: the fans go straight to the curve's speed.
- The curve editor now uses the dashboard's colours instead of the old blue theme.

### Keyboard lighting

- The `KB` page can set the keyboard's four lighting zones, an effect, and brightness. Static
  colours are per zone; an effect applies to all four.
- An `ALL` tile beside the four zones sets every zone to one colour in a single pick, which is
  what most people want. The tile previews the four zones as a gradient, so a mixed setup is
  visible without opening each one.
- This talks to the keyboard's own HID controller (an ENEK interface). The Acer WMI lighting
  methods store values the firmware never applies, so they were not usable for this.
- On models without that controller the card appears disabled, so an absent feature is not
  mistaken for a broken one.
- The four zones are also withheld when SMBIOS reports a single-colour keyboard, so a model that
  can only light one colour is not offered four swatches that cannot do anything.
- **Auto-off** turns the keyboard backlight off after a chosen time (`Never`, 30 seconds, 1 minute,
  5 minutes). The controller takes brightness and auto-off in one write, so Nitrous reads the
  current level first and keeps it rather than guessing.
- The auto-off is now read back after it is written. A controller that answers "done" but has
  nowhere to store the value is disabled with an explanation, instead of accepting the change and
  doing nothing.
- Opening the dashboard re-applied the lighting, which switched the backlight back on without
  re-arming the auto-off, so the keyboard stayed lit until the timeout was picked again. Any
  lighting change now re-arms the saved auto-off, debounced so dragging the brightness slider
  costs one write rather than one per tick.
- Where there is no embedded-controller interface, the same 30-second auto-off is a firmware timeout
  reachable over Acer's `APGeAction`. Nitrous sets that too and reads it back, so the feature now
  works on models that only expose it that way. That path is on/off at 30 seconds rather than a list,
  and the backlight returns on the next key press after the timer has already switched it off ΓÇö
  the firmware's behaviour, not a Nitrous choice.

### Accessibility

- **Sticky Keys** on the `KB` page: the Windows setting that lets you press one key at a time for
  shortcuts. It is a per-user preference rather than firmware, so it works on every model. The flags
  are read from Windows before changing them, so the confirmation and sound preferences are kept.

### Battery and USB

- **USB Charging While Off** on the Setup page: keeps charging USB devices while the laptop is
  off, stopping at 10 %, 20 % or 30 % battery. Nitrous reads the firmware's current state when the
  page opens, and the controls stay disabled on models that do not implement it.
- The state is validated before it is trusted. A firmware that answers something other than on or
  off is treated as not implementing the feature, rather than that value being read as "off" and
  offering a toggle that cannot work.

### Other Nitro models

- Nothing is keyed to a specific model. At startup Nitrous reads what the laptop reports ΓÇö its
  Acer SMBIOS tables, which Acer WMI methods exist, whether the embedded controller has a HID
  interface and which modes it lists ΓÇö and picks the mode channel from that. An unlisted Nitro
  that follows the same protocol works without a new build.
- The only per-model data is a short list of firmware quirks that cannot be probed; a model that
  is not on it gets the plain protocol.

### GPU page

- The GPU page reports the power limit the laptop is enforcing (`draw / enforced / max W`) and
  never writes it directly.

### Dashboard

- Disabled toggles now look disabled. The toggle style had no disabled state, so a control the
  model does not support looked exactly like one that worked. The same style is used by CPU Power,
  Battery Limit, USB Charging and GPU clock ownership.
- A disabled control's tooltip now appears. WPF hides tooltips on disabled elements by default, so
  the explanation of why a control was unavailable could never be read.
- The Dashboard scrolls. The custom fan section was cut off at the bottom on shorter windows.
- The `KB` page scrolls too, for the same reason, and hotkey text is centred in its box instead of
  sitting against the left edge.
- The GPU temperature no longer shows `--` while the GPU is awake: the embedded controller reports
  0 on some models, so the driver's reading is used instead when it is available.

### Display

- The refresh rate is a list of the rates the display actually advertises, instead of Auto / 60 /
  Max. `Auto` uses the rate you chose on AC and 60 Hz on battery.
- The current rate is shown next to the `DISPLAY` label, so the choice and what the screen is
  actually running are both visible.
- **LCD Overdrive** on the Dashboard, beside the refresh controls. Overdrive is a property of the
  panel rather than the model, so the control only appears where the firmware reports the panel
  supports it; where it does not, the whole row is collapsed instead of leaving a gap.
- Refresh and overdrive are two rows in one card, each with its label on the left and its control on
  the right, and the selected rate is centred in the list rather than pinned to the left edge.
- A panel without overdrive can answer the same `0` as a panel that has it switched off, so the
  change is read back after it is written. A panel that ignores the write is hidden with a log
  entry instead of looking like it worked.

### Diagnostics

- New **EC PROBE** button writes a read-only report of this machine's Acer firmware: the WMI
  classes and method IDs it exposes, the EC HID interface and the modes it lists, the SMBIOS
  gaming version, the sensor addresses Nitrous uses, and the power-mode and supported-profile
  registers. It never calls a `Set*` method. Reports are saved to
  `%LOCALAPPDATA%\Nitrous\ec-probe`, so a report from another Nitro model can be compared against
  one that is known to work.
- The probe also reports the keyboard backlight: the SMBIOS colour count, whether the EC HID and
  ENEK interfaces are present, and a read-only sweep of `GetGamingKBBacklight`. That is what shows
  whether a model without the EC interface can still set the auto-off over WMI.
- **Export Diagnostics** now includes the newest EC probe report. It is written to a separate
  folder, so it used to be missing from the bundle even when someone had just run it.
- The probe covers more of the machine: the fan behaviour, speed and table, the battery interface,
  every Acer-vendor HID interface by usage page, and the keyboard auto-off state. Both
  `APGeAction` and `AcerGenericMethod` are swept, because they are separate function spaces and
  reading the wrong one answers `0xE1` for everything.
- Every call records why it failed rather than reporting a silent null, and answers that are byte
  arrays are reported as hex instead of being dropped.

## 0.8.5 - 2026-10-27

### GPU page

- `RESET` and `APPLY` share one compact action row inside `CLOCK OFFSETS`.
- Offsets are positive-only: core `0..300 MHz`, memory `0..1000 MHz`, displayed with a `+`
  prefix. Negative values saved by older versions are clamped to `0` on first run.
- A sleeping dGPU is detected from its Windows power state instead of by probing it, so Nitrous
  no longer wakes the GPU just to find out that it is asleep.
- The clock watchdog skips its check when the active profile is `0/0` and Nitrous owns no
  offset. On battery this removes the wake that used to happen every two minutes.
- `Deep GPU Telemetry` polls only while the GPU page is open, and an asleep GPU shows `Asleep`
  instead of stale readings. It still turns itself off automatically if the GPU is powered but
  NVML stops responding.

### Hotkeys

- Editing a hotkey on the `KB` page applies immediately. The "restart required" note is gone.

### Dashboard

- Pressing the dashboard hotkey repeatedly now reuses one window instead of opening several.
- A saved off-screen position is recovered onto the primary display, including when a monitor
  is unplugged. New **Reset Dashboard Position** tray item.

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

- Added a redacted diagnostics export from **Settings ΓåÆ Diagnostics**.
- Restored the power-profile OSD and hardened hardware, ACPI, and GPU state handling.
- Built-in GPU profile defaults are stock `0/0`, and legacy automatic offsets are migrated
  to `0/0` on first run.
