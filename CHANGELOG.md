# Changelog

All notable changes to Nitrous are documented here. The GitHub release for a version is
published from the matching section of this file.

## 0.17.0-test.5 - 2026-11-28

### Appearance

- **The power-profile toast now matches the keyboard flash.** Quiet is white, Balanced orange,
  Performance crimson and Turbo violet. Each mode has a swatch in Theme > Overlay and tray, so the
  toast and the "Mode colour" tray icon can use a custom colour; the keyboard flash itself stays
  whatever the controller is fixed to. RESET returns all four to the firmware colours.
- **Tray icon changes are immediate.** The dashboard signals the tray through a named event when the
  icon style, a mode colour, the accent or the temperature unit changes. The ten-second poll stays
  as a fallback for a tray that started before the event existed.
- The README's Appearance section now covers the presets, card visibility, units, corners, compact
  navigation, opacity and toast/tray choices.

## 0.17.0-test.4 - 2026-11-28

### Appearance

- **Theme presets.** Nitrous, Ice, Ember and Mono set the accent, background and sections together.
  Editing any of the three switches the picker to Custom, so the presets never claim a look they are
  not showing.
- **Dashboard cards on or off.** The temperature graph, Power modes, Display and Fans can each be
  hidden from Theme. A hidden card still works; only its place on the page is removed.
- **Celsius or Fahrenheit.** Every reading follows the choice: the two telemetry cards, the chart
  axes and history ranges, the hover readout, the GPU thermal limit and its hint, and the fan curve
  editor's axis and waypoint readout. Values are still read, stored and written in Celsius.
- **Corners: Rounded, Soft or Square.** Replaces the binary toggle. Soft halves every radius;
  Square removes them. The fan curve editor follows, including its own window.
- **Compact navigation.** Hides the four labels in the bottom bar and shortens it from 54 to 42 px.
- **Window opacity.** 92-100%, so the frame can sit a little lighter over bright content.
- **Overlay and tray choices.** The power-profile toast can sit at the bottom centre (default), top
  centre, bottom left or bottom right; it can last 1-4 seconds and scale 80-120%. The tray icon can
  be the app logo, the active mode's initial in its colour, or the CPU temperature. The tray picks
  changes up within ten seconds; only the temperature style reads the sensor, once per refresh.

## 0.17.0-test.3 - 2026-11-28

### Appearance

- **The accent swatches now follow Square corners while the window is open.** They were created with a
  captured corner radius, so turning the toggle off left them square until the dashboard was reopened;
  they now use resource references like the rest of the surface, including the selection ring.

## 0.17.0-test.2 - 2026-11-28

### Appearance

- **Square corners now cover the rest of the surface.** The cards built on `SetupCardStyle` - including the
  dashboard temperature graph and the Accent Colour section - follow the toggle, as do the accent swatches
  and their selection ring. The whole fan-curve editor follows too: its window, CPU/GPU tabs, Ramp Down
  choices, Enable Curve Override and Snap to Grid boxes, and Save & Apply button. The curve editor is a
  separate window, so it reads the choice when it opens and when it is activated.

## 0.17.0-test.1 - 2026-11-28

### Appearance

- **UI scale (Theme): 90 / 100 / 110 / 125 %, default 100.** The whole dashboard grows together - frame,
  cards, text, plot and controls - through a layout transform, so nothing is left at the old size and text
  stays crisp. The window resizes with it, is clamped to the screen's work area, and 125 % is offered only
  where it fits; ComboBox popups carry the same transform.
- **Square corners (Theme):** a toggle that zeroes the frame, cards, buttons, pills and toggles, including
  the bottom navigation and its hover shapes. The rounded look remains the default.

## 0.16.0 - 2026-11-28

### Dashboard

- **The temperature graph has a scale, a time axis and a hover readout.** The range is chosen from the data
  with a 20 C floor and rounded to fives, so the lines use the plot without exaggerating a small wobble;
  three labelled gridlines sit beside them and `-2m / -1m / now` under them. Hover snaps to the nearest
  timestamped sample and replaces the stats with its age and values, with a crosshair and a dot per visible
  line; the CPU/GPU labels toggle their lines. **2 / 5 / 15 minute windows**, remembered and defaulting to 2,
  slice a fifteen-minute buffer; a gap longer than 12 s breaks the line instead of drawing through it. The
  footer reports min-max and average per series and labels the fan/load peaks.

### GPU

- **The telemetry card is rebuilt around the numbers that move.** Core and memory clocks lead; a two-minute
  power plot draws the draw against the enforced limit (dashed) with a hover readout; load, VRAM and
  temperature become meters against their own limits, with the temperature meter using the driver's current
  thermal target. A real `Asleep · D3` state replaces the `--` rows. The applied limits are named beneath the
  meters (`Thermal target 80 C · Clock cap 1500 MHz`), read from the driver rather than the staged sliders.
  The temperature slider now shows `87 C default` at its right end and `Allowed 75-87 C` in its hint, so the
  selected target is not mistaken for the range.
- **GPU MODE (MUX) on models whose firmware exposes the BIOS switch and where NVIDIA DDS is not the
  control.** Hybrid, Discrete and - where the firmware lists it - Automatic; the mode is stored for the next
  boot, written only by an explicit APPLY after a confirmation, accepted on status 0 or 8, read back and
  reported, with Restart now or Restart later. Nothing is written at startup, on a power-mode change or per
  profile. Advanced Optimus machines get a note pointing at the NVIDIA app; a mask of Hybrid + Discrete is
  treated as BIOS-only without loading `nvcpl.dll`. Verified end-to-end on an AN16S-61: Automatic -> Hybrid ->
  Discrete -> Automatic, every reboot clean, the dGPU driving the display in Discrete.

### Battery

- **The 80% limit is re-asserted on every charger event**, not only at startup and resume. The firmware can
  drop its charge-limit latch on a plug or unplug, and the toggle only shows the saved setting, so the cap
  could be off in hardware while the Setup page said on. Every AC/DC transition, resume and startup now
  writes it again, logs the write, and shows a refusal on the card instead of leaving the toggle looking
  successful. Applying the limit does not force a battery already above 80% down to it; it stops the next
  charge at 80%.
- **Battery health is read from Windows' battery class driver** (`IOCTL_BATTERY_QUERY_INFORMATION`) instead
  of the WMI classes some batteries refuse to instantiate (`BatteryStaticData` answers "Invalid query" on the
  AN16S-61). The WMI classes and the `powercfg` report remain as fallbacks, including for packs that report
  relative capacities.

### Setup

- Sticky Keys carries its icon, matching the other cards in Setup -> Keys.

### Fixes

- Hovering just below the GPU power line no longer makes the readout flicker: the header keeps a fixed size,
  so the plot cannot move under the pointer.

## 0.16.0-test.10 - 2026-11-28

### GPU

- **The MUX gate only consults NVIDIA DDS where the firmware lists Automatic.** A mask of Hybrid +
  Discrete (3) cannot be an Advanced Optimus machine, so the BIOS card is shown without loading
  `nvcpl.dll`; the ANV16-41 probe answers exactly that (mask 3, stored Hybrid).

## 0.16.0-test.9 - 2026-11-28

### Battery

- **Battery health comes from Windows' battery class driver**, not the WMI classes the AN16S-61's battery
  refuses to instantiate (`BatteryStaticData` answers "Invalid query"). `IOCTL_BATTERY_QUERY_INFORMATION`
  returns the design capacity, full-charge capacity and cycle count directly, without administrator rights
  and without spawning `powercfg`. The WMI classes and the `powercfg` report remain as fallbacks, including
  for packs that report capacities in relative units.

## 0.16.0-test.8 - 2026-11-28

### GPU

- **The telemetry card names the applied limits** beneath the meters: `Thermal target 80 C · Clock cap
  1500 MHz`. Both come from the driver (`Current` policy and the locked clock), not from the staged sliders,
  so a chosen-but-unapplied value never looks active.
- **The temperature slider's range is no longer ambiguous.** Its right end reads `87 C default` (with the
  number), and the hint says `Allowed 75-87 C; 87 C is the driver default`. The value above the slider is
  the selected target; the ends are the allowed range, which is why they do not shrink to 75-80.

## 0.16.0-test.7 - 2026-11-28

### Battery

- **The 80% limit is re-asserted on every charger event**, not only at startup and resume. The firmware can
  drop its charge-limit latch on a plug or unplug, and the toggle only shows the saved setting, so the cap
  could be off in hardware while the setup page still said on. Startup and resume already wrote it; AC/DC
  transitions now do too, with a log line for every write.
- **A refused write is visible.** The EC has no charge-limit readback, so the write is the only signal;
  the card now says when the firmware refused it instead of leaving the toggle looking successful.
- Applying the limit does not force a battery that is already above 80% down to it; it stops the next charge
  at 80%. On this machine the write was verified at 99% (`uReturn=0`), so the remaining 99% is the battery's
  existing charge, not a failed limit.

## 0.16.0-test.6 - 2026-11-27

### GPU

- The **GPU MODE (MUX)** card carries its switch icon, and the Advanced Optimus note a display icon.

## 0.16.0-test.5 - 2026-11-27

### GPU

- **The telemetry card is rebuilt around the numbers that move.** Core and memory clocks lead; a two-minute
  power plot draws the draw against the enforced limit (dashed) with a hover readout, and `2m / 5m / 15m`
  windows remembered across sessions; load, VRAM and temperature become meters against their own limits.
  The temperature meter uses the driver's current thermal target, and the power plot only records while the
  page is open, so a closed page is a gap rather than a zero.
- **A real asleep state**: `Asleep · D3` with one explanation instead of a card full of `--` values. The
  dGPU is still never woken to draw it.
- **GPU MODE (MUX) on models whose firmware exposes the BIOS switch and where NVIDIA DDS is not the
  control.** Hybrid, Discrete and - where the firmware lists it - Automatic; the mode is stored for the next
  boot, applied only by an explicit APPLY after a confirmation, read back and reported, with Restart now or
  Restart later. Nothing is written at startup, on a power-mode change or per profile.
- The MUX card is hidden when the firmware does not list both Hybrid and Discrete or reports `0xFF`, and on
  **Advanced Optimus** machines: the NVIDIA app switches the display live there, and a BIOS mode would only
  apply after a restart. Those machines get a note pointing at the NVIDIA app instead. If the DDS probe
  cannot run, the BIOS card is the fallback, because it is then the only switch.

## 0.16.0-test.2 - 2026-11-27

### Dashboard

- **The temperature graph now has a scale, a time axis and a hover readout.** The range is picked from the
  data with a 20 C floor and rounded to fives, so the lines use the plot without turning a 2 C wobble into a
  mountain range; three labelled gridlines sit beside them and `-2m / -1m / now` under them.
- **Hover snaps to the nearest sample** and replaces the stats with `-0:42 · CPU 52 C · GPU 47 C`, with a
  crosshair and a dot on each visible line. The footer now reports each series' min-max and average, and the
  fan/load peaks are labelled instead of being three unlabelled pairs.
- **2 / 5 / 15 minute windows**, remembered across sessions and defaulting to 2. The buffer keeps fifteen
  minutes of timestamped samples, and a gap longer than 12 s - the dashboard was hidden - breaks the line
  instead of drawing a straight line across it.
- Click the **CPU** or **GPU** label to hide that line; the axis refits to the one left. The chart rebuilds
  once per 2 s poll and the hover only moves the overlay when the sample under the pointer changes, so there
  is no timer, animation or per-frame work.

## 0.16.0-test.1 - 2026-11-27

### Setup

- Sticky Keys now carries an icon in **Setup -> Keys**, like the other cards on the page.

## 0.15.0 - 2026-11-27

### CPU power limits

- **PL1 and PL2 are two 1 W sliders with a shared APPLY**, on Intel Nitro models that already have
  [PawnIO](https://pawnio.eu) installed. The old pickers stepped in 5 W, so 29 W or 28 W could not be
  asked for. The sustained slider stops at the mode's own request and the burst slider starts where
  sustained sits, so the cap can only ask for less than the firmware does and PL1 can never be set above
  PL2. RESET stands both on the mode's request again.
- **The cap is held per power profile and per power source.** A cap set in Balanced applies to Balanced
  only; a profile with no cap is left alone, and clearing is the explicit RESET rather than a side effect
  of where the machine happens to be. The ceiling is the highest request the firmware has been seen to
  make for that profile - not the number it happens to be asking at the instant - so a dip (DTT, or the
  charger sharing power with a charging battery) can no longer shrink the cap or hide it.

### GPU tuning

- **A thermal target and a core clock cap, held per profile**, beside the offsets on one four-slider GPU
  TUNING card with a single APPLY and RESET. A profile with no value restores the driver's own thermal
  target and clears the clock lock; every mode change, start-up and resume re-applies the active
  profile's values. Both limits are lower-only and read back after the write.
- The limit cards read the driver **off the UI thread**, so opening the GPU page no longer pauses, and a
  sleeping dGPU is still never woken to draw a card.

### Lighting and keys

- **The LIGHT page now exists on the WMI-only Nitros** - the AN515/AN517 family, four RGB zones and no
  ENEK HID interface - through Acer's own `SetGamingKBBacklight` and `SetGamingRgbKb` setters: zone
  colours, brightness and the five effects that controller actually implements. Snake and Disco are no
  longer offered where the firmware accepts their codes and leaves the keys dark. Where a BIOS only
  accepts zone colours, the effect and speed controls say so instead of accepting changes that go
  nowhere.
- **The power-mode key works on the models whose firmware leaves switching to software** (Acer's
  `APGeEvent`), and **the Nitro key is read from Acer's HID collection** as well as from the keystroke,
  so both act where a keyboard hook never sees them.
- Spectrum sound sync reads the live sample buffer again instead of one nothing filled, and two seconds
  of silence is logged once, so "sound sync does nothing" can be told apart from a hardware limit.

### Setup and dashboard

- **SETUP is four pinned categories** - Power, System, Keys and Theme - instead of six sections and five
  screens of scrolling. The header and tabs never scroll away, the last category is remembered, and deep
  links open the right one.
- **A coffee toggle keeps the machine and its screen awake** from the title bar, held by the tray through
  a Windows execution-state request, so it survives closing the dashboard and costs no CPU while idle.
- **Scrollbars** is now a plain show/hide of the page scrollbar, and **the DISPLAY card names the panel**
  it is driving - adapter, rate and signal depth, read from Windows' own advanced-colour state.
- A status line with nothing to say no longer leaves a chin at the bottom of a card.

### Fixes

- A cap no longer follows the firmware's request down, and a mode change no longer wipes one.
- The lighting sync, the effect limit, the mode and Nitro keys and the GPU limits above.

## 0.15.0-test.28 - 2026-11-27

### CPU

- **The CPU power limits are two 1 W sliders with one Apply.** The dropdowns stepped in 5 W, so 29 W or 28 W
  could not be asked for. Sustained and burst now move in single watts, and both are written by the same
  APPLY, so dragging to a value cannot send a dozen writes to MSR 0x610 on the way there.
- The sustained slider's right end is the mode's own request (the learned ceiling), and the burst slider's
  left end follows sustained: the cap can only ask for less than the firmware does, and PL1 cannot be set
  above PL2. A cap already above a request that has since dipped keeps its value on the dial instead of
  being pulled down to the dip.
- RESET stands both sliders on the mode's own request and applies that, which is what "follow this mode"
  means; the profile is then saved as uncapped.

## 0.15.0-test.27 - 2026-11-27

### Setup

- **SETUP is now four categories, with the tabs pinned above the scroller** - Power, System, Keys and Theme.
  The page had grown to six sections and five screens of scrolling; one category at a time now fits without
  hunting, and the header and tabs never scroll away. The last category is remembered between openings.
- Power holds CPU Power, CPU Power Limits, Battery Health and USB Charging While Off; System holds Deep GPU
  Telemetry, Run on Startup, Boot Logo, Acer Services and Diagnostics; Keys holds Global Hotkeys, Sticky Keys,
  Backlight Brightness and the sound sync; Theme holds accent, background, sections, font and scrollbars.
- Deep links still land correctly: the hotkey capture opens Keys, the CPU limits preview opens Power.
- **An empty status line no longer leaves a chin** at the bottom of a card. A status line with no text used to
  keep its top margin, so the card's bottom padding looked larger than its top - visible on Diagnostics
  (8 px) and Backlight Auto-off (4 px), and the same on Boot Logo, Backlight Brightness and the brightness
  sync. They now collapse, margin and all, until they have something to say.

## 0.15.0-test.26 - 2026-11-27

### GPU

- **The GPU card is four sliders with one Apply.** Core offset, memory offset, temperature limit and core
  clock limit now sit together and are committed by the same APPLY; RESET returns all four to neutral - stock
  offsets, the driver's own temperature and no clock cap. Before this the two limits applied as soon as they
  were picked, so the button directly beneath them looked like it governed only the offsets.
- The temperature slider runs from the policy's minimum to its own default, so it can only ask for less heat
  than the driver does; the clock slider's far right is "no cap", which is exactly what clearing means.
- Apply and Reset report every part in one line ("Applied +50 / +100 MHz offsets, 85 C, 1500 MHz cap to
  Balanced."), and the readout beside the performance state re-reads what the driver holds afterwards.

## 0.15.0-test.25 - 2026-11-27

### GPU

- **The GPU page opens without the pause.** Its limit cards read the thermal policy, the clock cap and the
  GPU's maximum clock, and those NVAPI/NVML calls ran on the UI thread during the page switch - which is
  where the delay came from. They now run on a worker thread and the cards paint when the answer arrives, so
  switching to the page no longer waits for the driver.
- Turning **Manage GPU tuning** on now applies both saved values - thermal target and clock cap - before
  reading them back, so the cards show the result of the switch instead of the state it replaced.

## 0.15.0-test.24 - 2026-11-27

### GPU

- **The thermal limit is now genuinely per power profile.** Setting 85 C in Balanced left the driver holding
  85 in Performance: both re-asserts skipped a profile with no saved value, and the driver keeps one global
  thermal target, so nothing put it back. A profile with no value now restores the driver's default (87 C),
  and a profile with a value gets that value on every mode change.
- **The core clock limit follows the profile too.** A cap set in one profile stayed in the next for the same
  reason; a profile without a cap now clears the driver's lock, so the GPU boosts freely there.
- **Every mode change applies them**, including the hotkey and the mode key - the tray's cycle re-applied the
  GPU offsets but not these two. The GPU page also refreshes its limit cards when a mode change arrives while
  it is open, instead of leaving the previous mode's reading on screen.

## 0.15.0-test.23 - 2026-11-27

### Appearance

- **Scrollbars is now a plain show/hide switch for the four pages' scrollbar.** On - the default - leaves the
  existing thin scrollbar exactly as it always was; off hides it, and the wheel, the keyboard and touch
  panning still scroll the page. The two earlier builds widened and restyled the bar, which was not what was
  asked; that work is gone.

## 0.15.0-test.22 - 2026-11-27

### Appearance

- **Show scrollbars now shows the whole scrollbar**: a 15 px bar with a visible track, up and down arrows and
  a thumb, present on all four pages all the time instead of appearing only when a page overflows. Off keeps
  the thin bar. Arrows and the area around the thumb move by a line and a page as a normal scrollbar does.

## 0.15.0-test.21 - 2026-11-27

### Keep awake

- **A coffee toggle in the title bar, left of Pin to Top, keeps the machine and its screen awake.** It is a
  Windows thread execution-state flag, not a timer, thread or service: one call when it is switched and
  nothing in between, so an idle Nitrous with it on costs no CPU at all. The tray holds the request on its
  own thread, so it survives closing the dashboard; the dashboard applies it too for an immediate response
  to the click. Off by default.
- The value is a plain setting, so it stays as it was left across restarts and appears in diagnostics.

### Appearance

- **Show scrollbars** (Settings → Appearance): the four pages can use a wide, clearly visible scrollbar
  instead of the thin, quiet one. It is the same control either way - only the width and the thumb are
  swapped, and the choice is remembered.

## 0.15.0-test.20 - 2026-11-26

### Display

- **The DISPLAY card now names the panel it is controlling**: the adapter Windows is using, the rate it is
  running at and the signal depth - `AMD Radeon 860M @ 144 Hz · 10-bit`. The depth is Windows' own
  advanced-colour state for that display's path, so it is the current signal (8, 10 or 12 bits per channel),
  not what the panel could theoretically do; anything the driver does not report is left out rather than
  guessed.
- The line follows the display: it is rebuilt when the refresh rate changes and when the window is activated,
  so plugging in, unplugging or switching a monitor updates it instead of leaving yesterday's adapter.

## 0.15.0-test.19 - 2026-11-25

### Lighting

- **Spectrum sound sync works.** Its analyser read a sample buffer that nothing ever filled, so every frame
  measured as silence and the keyboard switched the lights off - which is exactly what "it's like turning the
  light off" was. Peak, Beat and Vibe were unaffected because they read the live buffer the capture fills.
  The analyser now takes that buffer as its input.

## 0.15.0-test.18 - 2026-11-25

### Lighting

- **Snake and Disco are no longer offered on the keyboards whose controller has five effects.** On the WMI
  four-zone keyboards the firmware accepts effect codes 6 and 7 and leaves the keys dark, so both controls
  existed and could not work. The pickers, the effect hotkey cycle and the sync restore now use the five the
  controller implements - Breathing, Neon, Wave, Ripple and Zoom - and an effect saved as Snake or Disco is
  not written at all instead of switching the keys off.
- **Sound sync now says when it is hearing silence.** Two seconds of zero levels - nothing playing, or the
  default output device not reaching the meter - is logged once, and the first frame with audio is logged
  too. Without that, "sound sync does nothing" could not be told apart from a hardware limit in a bug report.

## 0.15.0-test.17 - 2026-11-25

### CPU

- **The cap's ceiling is now the highest request the firmware has made for that mode, not the one it is
  making right now.** On an ANV15-51 the same Balanced mode asks about 20 W when it is idle and 36-45 W under
  demand, minutes apart - and Nitrous clamped to whatever it read at that instant, so a 25 W cap and then a
  30 W cap were both written as 20 W the moment the request dipped. The ceiling is remembered per mode and
  per power source (battery and AC have different tables) and kept across restarts, so a dip can no longer
  decide how high a cap may be.
- The card still shows the live request beside what the register holds - `asks 20 W ... register 30 W` - so
  a dip stays visible instead of silent, while the picker offers up to the learned ceiling.
- The single remembered ceiling an earlier build kept is deleted on start; it was the lower of two readings
  and could pin a cap below the mode's real maximum.

## 0.15.0-test.16 - 2026-11-25

### CPU

- **A cap no longer follows the firmware's own request down.** The firmware lowers its sustained request on
  its own - Intel DTT adapting, or the charger sharing power with a charging battery - and the periodic
  re-assert wrote that dip over the saved cap. On an ANV15-51 a 25 W cap in Balanced became 20 W about three
  minutes after it was set, with no mode change and no other software involved: the firmware's request had
  dropped to 20 W, and the next refresh clamped the register to it. Re-assert now writes only a raise, a mode
  change, or a register the firmware has taken back to its own numbers; a dip under an applied cap is logged
  once and left in place.
- **The remembered ceiling no longer latches a dip.** It adopts a live reading unless that reading is exactly
  what Nitrous last wrote, so one dip can no longer pin the ceiling - and the cap - below the mode's real
  request for the rest of the session.
- The cap control keeps showing the value the register holds while the request has dipped, so a card reading
  20 W over a 25 W cap can no longer be mistaken for the cap being reset.

## 0.15.0-test.15 - 2026-11-24

### Diagnostics

- **The EC probe answers what the keyboard and key work depends on** (probe v14): how each BIOS declares
  the lighting setters' inputs - a byte array for the effect record, or a number, which limits the model to
  zone colours; the HID interface on Acer's gaming page `0x88`, which is how the Nitro key arrives where it
  is not a keystroke; and the firmware's `APGeEvent` class, watched for two seconds so pressing the mode key
  during the probe records it.

## 0.15.0-test.14 - 2026-11-24

### Keys

- **The power-mode key now works on the models whose firmware leaves switching to software.** Those units
  raise a firmware event (`APGeEvent`, kind 7, value 4 or 5) and change nothing themselves, which is why the
  key did nothing once Acer's software was gone. Nitrous reads the event and walks the same cycle as its own
  power-mode hotkey, with the same on-screen display. On a model where the firmware runs the key itself no
  event is raised, so the watcher stays idle there. If Acer's own app is also running it applies the key too,
  so the mode can step twice; Nitrous does not try to fight it.
- **The Nitro key is read from Acer's HID collection as well as from the keystroke.** Some models send it as
  a vendor HID report (usage page `0x88`, bytes `81 FF`) rather than scan code `0x75`, where a keyboard hook
  can never see it; both paths open the dashboard now.

## 0.15.0-test.13 - 2026-11-24

### Lighting

- **The LIGHT page now appears on the older Nitros whose keyboard lighting is driven through Acer's WMI
  class** - the AN515/AN517 family, four RGB zones and no ENEK HID interface. Until now those were hidden
  even though SMBIOS reports an RGB keyboard, because the only controller Nitrous had was the HID one. The
  AN515-55 is the first report of that gap.
- Zone colours, the seven effects and brightness go through `SetGamingKBBacklight` and `SetGamingRgbKb`,
  the registers Acer's own software drives on those units, in its NitroSense payload layout.
- The lid logo and the mode-key blink stay absent there - they live on the HID controller this family does
  not have - and the panel says so instead of offering a button that cannot work.
- Where a BIOS declares the effect record's input as a number rather than an array, only zone colours can
  be written: the effect, speed and brightness controls are disabled and say why, rather than accepting
  changes that go nowhere.
- Sound and screen sync use the same path. This interface has one brightness for the whole keyboard, so a
  per-zone level is written as a dimmed zone colour, which looks the same.
- The EC probe reports whether this interface answers, and reads the record without an input - the way this
  family's firmware answers it.

## 0.15.0-test.12 - 2026-11-22

### CPU

- **A mode change no longer wipes a cap.** The previous build cleared the register when entering a profile
  with no cap saved, which looked like the right meaning of "disabled for this mode" - but modes change on
  their own (charger switch, tray, hotkeys), so Angel's cap vanished minutes after he set it. Two `cleared`
  events in his log sit exactly on mode changes he did not make. An uncapped profile now means "Nitrous
  writes nothing here": clearing is the explicit action (**RESET**), not a side effect of where the machine
  happens to be.
- The card, the save and the re-assert now agree on which profile they are working on. The cap path asked
  the EC for the current mode while the card showed the saved one; where those disagree, a value could be
  stored against the wrong profile.

## 0.15.0-test.11 - 2026-11-21

### CPU

- **The power cap is held per power profile.** A cap set in Balanced now applies to Balanced only; switching
  to a profile with no cap restores that mode's own limits, which is what "disable the limit for this mode"
  has to mean. Before this, one saved pair followed the user into every mode, clamped to whatever that mode
  asked for.
- Why it mattered: Angel's ANV15-51 asked **24, 29, 37, 41, 45 and 55 W in Balanced on a single day** - the
  firmware's own number moves, so a global cap looked like it "sometimes applied and sometimes did not" when
  the clamp was simply following a moving ceiling. There are no refusals in his log; every write stuck.
- A cap saved by the old single-value model is moved onto the active profile on first run, so an upgrade does
  not silently drop a limit someone set.

## 0.15.0-test.10 - 2026-11-21

### GPU

- **The GPU TUNING card now says how each control commits.** The two sliders sit under
  `CLOCK OFFSETS · APPLY`, the two dropdowns under `LIMITS · APPLIED IMMEDIATELY`. They behave differently
  for a reason - a dragged slider is not a decision, while a chosen value is - but the card never said so,
  which made one half look broken or the other half look unfinished.
- A gap above the rule that separates the tuning controls from the RESET / APPLY row.

## 0.15.0-test.9 - 2026-11-21

GPU tuning is a feature, not a patch, so this line is **0.15.0**. The GPU work before it - the thermal
target, the probes, the GPU TUNING card - shipped to testers as `0.14.1-test.1` through `.8`; those files
are still out there and are the same code.

### GPU

- **Core Clock Limit.** A cap on the GPU's boost clock, per power profile, in the GPU TUNING card under the
  thermal target: `No cap`, then the GPU's own maximum down to 400 MHz in 100 MHz steps. It is written with
  NVML's locked clocks - the call `nvidia-smi -lgc 0,N` makes - and read back through NVAPI's clock-boost
  lock, so the row shows what the driver holds rather than what was asked for. `No cap` resets it, and a
  refusal is reported instead of leaving the control looking as though it worked.
- The approach follows **G-Helper**, which ships this control: the same NVML write, the same NVAPI read-back
  (the driver carries the active cap in the lock entry's voltage field), and the same 400 MHz floor.

## 0.14.1-test.8 - 2026-11-21

### Diagnostics

- `--probe-gpu-clock` reads the driver's clock-boost surface read-only: the reported ranges, the current
  lock state per domain, and the size of the boost table.
- What it found on an RTX 5060 Laptop: the lock state reads (seven domains, all unlocked), the boost table
  reads (80 points, offsets at 0), and the **ranges decode to nonsense** - `-1000..1000 MHz`,
  `max temperature 32256 C` - so the wrapper's struct does not match this driver and those numbers are not
  trustworthy. Separately, NVML's locked clocks **do** work here: pinning 1500 MHz took effect
  immediately and reset cleanly, which makes a clock cap possible through NVML rather than through
  `NvAPI_GPU_SetClockBoostLock` (which takes a voltage, not a frequency).

## 0.14.1-test.7 - 2026-11-21

### GPU

- **The thermal target is held per power profile.** A value is stored for Quiet, Balanced, Performance and
  Turbo and written when that profile becomes active - the driver's policy is one global target, so the
  per-profile part is ours - and it is restored on a mode change, at start-up and after a resume.
- **One gate for GPU writes: "Manage GPU tuning"** replaces the clock-only switch and covers the thermal
  limit too, so a machine cannot end up with offsets allowed and thermal writes not, or the reverse.
- The clock card became the **GPU TUNING** card: core and memory sliders, then the thermal row, under a
  single heading and one switch. The separate thermal card is gone.

## 0.14.1-test.6 - 2026-11-21

### GPU

- The thermal target also reads beside the performance state in the telemetry card - `87 C (default)` or
  `85 C (modified)` - derived from the driver's policy rather than from the saved setting, so a value
  changed in another tool is reported as modified too.

## 0.14.1-test.5 - 2026-11-21

### GPU

- **A GPU thermal target.** One control on the GPU page sets the temperature the GPU throttles at,
  between the driver's own minimum and default - 75-87 °C on the RTX 5060 - through NVAPI's thermal
  policy. It is a **single global setting, not per profile**: the target read 87 °C in Quiet, Balanced
  and Performance, and a written value ignored mode switches. Lower-only, read back after the write,
  reverted if it does not stick, and re-applied after a resume.
- The card appears only once the GPU page has been opened, because reading the policy is what would wake
  a sleeping GPU.

### Diagnostics

- **The GPU power-limit probe is gone.** NVML refuses that write with `NOT_SUPPORTED` and NVAPI exposes no
  client power policy on the hardware tested, so the feature cannot be built and the probe was dead
  weight. `--probe-gpu-thermal [--temperature C] [--hold N] [--keep|--restore]` replaces it and covers
  only the thermal policy.

## 0.14.1-test.4 - 2026-11-21

### Diagnostics

- `--probe-gpu-power --policies` reads NVAPI's client power and thermal policies - the interface NVIDIA's
  tuning partners use, and a different surface from NVML. `--temperature <C>` performs one reversible,
  lower-only write of the GPU throttle target.
- What that settled on an RTX 5060 Laptop: NVML refuses the power-limit write with `NOT_SUPPORTED`
  (code 4), and NVAPI exposes **no** client power policy on this GPU (zero entries), so GPU power cannot
  be set through either API here. The thermal policy **is** present: range 75-87 °C, one global target of
  87 °C in every power mode, and a write to 85 °C was accepted, held, and reverted cleanly. NVAPI setters
  need administrator - without it they return `NVAPI_INVALID_USER_PRIVILEGE`, which is not the same
  answer as "unsupported".

## 0.14.1-test.3 - 2026-11-21

### Diagnostics

- `--probe-gpu-power --set <W>` performs a single reversible write: it lowers the limit, samples whether it
  holds, and always puts the mode's own number back. It refuses to raise above the current value. NVML
  return codes are now named, because the answer that matters is usually the code: **4 means the driver
  does not support writing this limit at all**.

## 0.14.1-test.2 - 2026-11-21

### Diagnostics

- The GPU power probe reports the mode the EC *actually* has, not the saved setting, and gained
  `--wake` for machines whose dGPU sleeps within seconds - a laptop GPU drops back to D3 faster than a
  sampling run can start, and the probe will not wake it unless asked.

## 0.14.1-test.1 - 2026-11-21

### Diagnostics

- **A read-only GPU power-limit probe.** `--probe-gpu-power [--seconds N]` samples the driver's own
  fields - range, default, current and enforced limit, draw and temperature - every two seconds, records
  the Acer mode and power source with each sample, and summarises the run by mode. It refuses to run
  while Windows reports the dGPU powered down, because initialising NVML is what wakes it, and it never
  writes anything.
- First finding, on an RTX 5060 Laptop: `nvmlDeviceGetPowerManagementLimit` is not implemented by this
  driver, so the enforced limit is the number to read (100 W, range 5-115 W, default 70 W).

## 0.14.0 - 2026-11-20

### CPU power limits

- **PL1 and PL2 are visible, and cappable, on Intel Nitro models that already have
  [PawnIO](https://pawnio.eu) installed.** Windows exposes no API for the package power limit, so the
  card reads the mode's own request from MCHBAR `0x59A0` and the register the CPU holds at MSR `0x610`.
  Lowering is the only direction offered: each list is built from the mode's numbers, the write is read
  back, and a value that does not stick is reverted. The lock bit is never written.
- Your cap follows the mode down and returns when you switch back, survives a sleep or a charger change,
  and **RESET** restores the mode's own limits. AMD machines do not see the card at all.

### Lighting

- **Brightness steps are learned from the keyboard instead of assumed.** Some firmwares keep three, not
  five, which made the dropdown offer levels that did nothing and the beat effect write refused values.
- **"Pulse with sound" warns where the backlight is driven over ACPI**, a path measured to add audio
  latency; keyboards with a HID lighting interface keep the original beat rate. Fewer writes, and a
  back-off after three refusals.

### GPU and setup

- Open the NVIDIA App from Nitrous - a card on the GPU page and a hotkey - including machines that only
  have the Store build of the Control Panel.
- GPU temperature falls back to NVML where the EC reports `0`.
- Battery Health leads the battery card, and the Turbo profile no longer flashes on models without it.

### Fixes

- **Nitrous no longer fails to start on a machine with an NVIDIA GPU but no NVIDIA App or Control Panel
  entry.** That combination killed the process before a window appeared, and it had been latent since
  0.13.0.
- A brightness control says so when the keyboard never reports its level back, rather than appearing to
  work.
- The dashboard no longer shows the firmware's limits in place of your cap just after waking.

## 0.14.0-test.36 - 2026-11-20

### CPU

- **The card catches up after a resume.** His log shows the dashboard reading the register at wake and
  the tray re-applying his 50 W cap two seconds later, so the card showed the firmware's value with a
  blank status line until the window was reopened. The resume refresh now runs twice: once straight
  after wake for the rest of the window, once after the tray's re-apply has landed.
- The status line now describes the register rather than replaying the last click - `Holding 50 W
  sustained / 115 W burst.` when a cap is in place, `Following this mode's own limits.` when not - so it
  is right whoever applied the cap. A refusal is still reported, and now clears when the mode changes.

## 0.14.0-test.35 - 2026-11-20

### Fixes

- **Nitrous no longer fails to start on a machine that has an NVIDIA GPU but no NVIDIA app entry.**
  The vendor-tool search ended in `FirstOrDefault` over a list of tuples, which yields `(null, null)`,
  and the next line read `match.Id.Length`. That throws, and it ran inside the tray constructor, so the
  process died before a window appeared - the ANV15-51 tester hit it after uninstalling the Control Panel.
- The vendor search also can no longer take the app down at all: every discovery step logs and returns
  nothing instead of letting an unexpected exception escape into start-up code.

## 0.14.0-test.34 - 2026-11-20

### Lighting

- **The brightness control says when the keyboard does not report its level back.** Senpai confirmed his
  keyboard was lit while the firmware answered `0`, and that a write from Nitrous changes nothing - it
  only responds to Fn+F11, which is EC firmware. Where the step probe finds nothing it can verify, the
  card now says the changes may not take effect, and the reported `0` is no longer trusted as "Off".

## 0.14.0-test.33 - 2026-11-20

### Lighting

- A backlight write that the firmware accepts but reports back at a different level now logs both
  numbers instead of a bare failure, so a refused call and a firmware that never reports its level can
  be told apart. Senpai's ANV15-52 records brightness `0` with the timeout correct, which is the
  second case or an unlit keyboard - the log line will say which on the next bundle.

## 0.14.0-test.32 - 2026-11-20

### Setup

- **The Turbo profile no longer flashes on models that do not have it.** The window drew the button and
  the hardware check then hid it a moment later, which Senpai caught on an ANV15-52. The last known answer
  is remembered, so the first frame is already right; the check still runs each launch and corrects it if
  the hardware has changed.

## 0.14.0-test.31 - 2026-11-20

### CPU

- **A RESET button on the power-limit card** returns both limits to the mode's own values in one click,
  the way RESTORE works on the card above. It selects the top value rather than stopping the management,
  so the register keeps following the mode afterwards - the stale number that made Off look broken.
- With both limits at the mode's own values the status line reads "Following this mode's own limits."
  instead of reporting a cap at the ceiling.

## 0.14.0-test.30 - 2026-11-20

### CPU

- Choosing a value now costs fewer driver round-trips: a forced refresh after a write no longer re-runs
  the mode guard, which was four more reads behind every selection. Senpai hears this IOCTL as an audio
  pop, so the shortest path through it is the one worth taking.

## 0.14.0-test.29 - 2026-11-20

### CPU

- **"Off" is gone; the top value means "follow the mode".** Senpai reported that the limits never went
  back to off "in any mode", and the cause was the definition: Off meant Nitrous stopped writing, and a
  mode change does not rewrite that register, so the previous mode's numbers stayed on screen. A reset
  that looks like it failed. Choosing the mode's own value now keeps the register at whatever the current
  mode asks, on every mode change and after a resume.
- The sustained row reads "Held across mode changes." instead of "Steady power over time."

## 0.14.0-test.28 - 2026-11-20

### CPU

- The log now tells a clamp from a choice: an automatic hold reads `held at 45 W sustained / 115 W burst to
  stay inside this mode`, where a value the user picked reads `capped to ...`. Both used to read the same,
  which made a mode change look like someone touching the control.

## 0.14.0-test.27 - 2026-11-20

### CPU

- **The ceiling is selectable again.** Dropping 55 W / 115 W from the lists was the wrong cure for
  "choosing the maximum snaps back to Off" - those numbers are real choices, and they came straight
  back missing. The snap-back came from saving "no cap" for a value that equalled the ceiling; the
  choice is now saved as made, and the dropdowns read it back.
- A clamp caused by a mode change no longer overwrites that choice. "50 W in Performance" survives a
  detour through Quiet and is restored on the way back, instead of being flattened to Quiet's number
  and forgotten.
- When a mode's ceiling is below the saved value, the box shows the clamped number actually in the
  register rather than claiming Off.

## 0.14.0-test.26 - 2026-11-20

### CPU

- **The top of the list is no longer offered, because it was Off.** Senpai found that picking the
  maximum snapped straight back to Off: the top entry was the mode's own number, choosing it saved no
  cap, and the next refresh drew Off in its place. The list now stops strictly below the ceiling.
- **The register is pulled back inside the mode's request when a mode change leaves it above it.**
  Switching Performance to Balanced left the sustained limit at Performance's value, because a mode
  change does not rewrite the register. A Quiet switch could therefore fail to lower the limits Quiet
  advertises. It only ever lowers, never raises.

## 0.14.0-test.25 - 2026-11-20

### CPU

- The power-limit card has an icon like its neighbours, and each row now says what the field is for -
  "Steady power over time." and "Short bursts of extra power." - instead of carrying the PawnIO
  explanation.
- Results and refusals moved to a status line under the rows, the same dim 9 pt line the other cards
  use, so a message is not competing with the row's own description.

## 0.14.0-test.24 - 2026-11-20

### CPU

- **Both fields are cappable now: sustained (PL1) and burst (PL2), each lower-only.** The previous build
  offered only the sustained limit, which is not what the feature is - PL2 is what decides how far a
  short burst goes. Each list is bounded by the mode's own value for that field, the sustained list
  stops at the applied burst value and the burst list starts at the applied sustained value, so a pair
  where sustained exceeds burst cannot be chosen at all.
- Off on a field returns that field to the mode's own number; Off on both is a full reset.

## 0.14.0-test.23 - 2026-11-20

### CPU

- **The power-limit card is shown on Intel Nitro models without PawnIO**, disabled and saying what it
  needs, so the feature can be found before the driver is installed. On AMD models it is absent: these
  limits do not exist there, and an inert control would be noise on every AMD Nitro.
- The card follows the setup page's conventions - card padding, a rule above the control row, the same
  title and body sizes - and every "no numbers yet" state shares one layout so they cannot drift apart.

### Fixes

- A power cap write that finished after the dashboard had been closed crashed the process.
- The not-installed state could re-enable its own dropdown through its own change event.

## 0.14.0-test.22 - 2026-11-20

### CPU

- The card now says what Off does. It returns the mode's own limit, not whatever value another tool had
  left in the register, which pr4u confirmed on his machine and flagged as a wording gap: the semantics
  were correct and unstated.

## 0.14.0-test.21 - 2026-11-20

### CPU

- Every refusal on the power cap now leaves a line in the log, and clearing it logs what the register
  held before the write. The previous round came down to a log that showed a cap line and nothing else:
  a write that never happened was invisible, so the cause had to be inferred rather than read.

## 0.14.0-test.20 - 2026-11-20

### CPU

- **Off now restores the mode's own limit.** pr4u's first run showed it plainly: choosing Off left the
  register untouched. The reset replayed a value captured before the first write, and a fresh dashboard
  process had captured nothing - the cap had been applied by an earlier process - so there was nothing
  to replay and the reset did nothing.
- Off now writes the mode's own request back, which is what "no cap" means and cannot exceed it.
- The result of a change is no longer wiped before it can be read: the card used to replace it with the
  standing explanation on the next refresh.

## 0.14.0-test.19 - 2026-11-20

### CPU

- The cap list is built from the firmware's own request, not from the live MCHBAR copy. Once a cap is
  written that copy can read higher than the mode actually asks for, so the last reading taken while
  the firmware still owned it is used, refreshed whenever a mode is applied or the machine resumes.
  Without this the list could have offered a value above what the mode asks for, which the cap is not
  supposed to allow.

## 0.14.0-test.18 - 2026-11-20

### CPU

- **Sustained power limit control, on Intel Nitro models that already have PawnIO installed.** The CPU
  card now offers a limit at or below the one the current power mode asks for. Choosing a value writes
  PL1 in MSR `0x610`, reads it back, and puts the previous value back if the write does not stick.
  Lower-only by construction: the list is built from the mode's own request, so nothing here can ask
  the package for more power than the firmware already did, and the register's lock bit is never
  written.
- The cap is re-applied after sleep and after a charger change, because the firmware restores its own
  value and clears the clamp bits across a resume. Switching to a mode with a lower limit brings the
  cap down with it.
- Choosing **Off** restores the value captured before the first write rather than re-reading MCHBAR:
  writing `0x610` was measured moving the MCHBAR copy, so a fresh read there would restore a number
  that is no longer the mode's.

## 0.14.0-test.17 - 2026-11-20

### CPU

- **The power-limit card shows both copies, labelled and unranked.** `test.15` called MCHBAR "the
  firmware's copy" and treated the register as an override, but pr4u measured the two moving together:
  writing `0x610` changed what `0x59A0` read, and it stayed changed across a resume. Neither copy
  stands alone, so the card now reads `asks` (MCHBAR `0x59A0`, what the mode requests) and `register`
  (MSR `0x610`, what the CPU holds), and collapses to one pair only when they agree.
- Correction to `test.16`: on pr4u's ANV15-52 all four modes read bit-identical on AC and on battery,
  so the power source is context for a laptop that switches mode on an adapter change, not a separate
  limit table. The label stays; the claim that the table differs goes.

## 0.14.0-test.16 - 2026-11-20

### CPU

- The power-limit card names the power source and re-reads when it changes. These laptops use a
  different limit table on battery, and the firmware also adjusts PL1 while it runs - pr4u saw Quiet
  move between 26 and 31 W minutes apart, and his Turbo reading came back 35 W against the 55 W his
  own sweep had recorded. Without the source on the line, a number that drifted looked like a bug
  instead of context.

## 0.14.0-test.15 - 2026-11-20

### CPU

- **The power-limit card reads the firmware's copy of the limits, not the register.** MSR `0x610`
  turns out to be seeded once at boot and then left alone: on pr4u's i9-13900H a full
  Quiet/Balanced/Performance sweep left it bit-identical, so the card would have shown the same two
  numbers under every mode label. The limits now come from the firmware's own copy at MCHBAR `0x59A0`,
  which a mode switch does rewrite, via PawnIO's read-only IntelMCHBAR module (LGPL-2.1, embedded
  beside IntelMSR).
- When `0x610` disagrees with the firmware - something outside Nitrous has set the limits itself - the
  card says so rather than hiding it, because that register is the copy the CPU enforces.

## 0.14.0-test.14 - 2026-11-18

### CPU

- The PL1/PL2 card follows the power mode. Switching Quiet, Balanced or Performance re-reads the
  register once the EC has applied the mode, so the card shows the limits actually in force rather
  than whichever mode was selected at start-up, and the log line names the mode.

## 0.14.0-test.13 - 2026-11-18

### CPU

- **The CPU's package power limits are shown, when PawnIO is installed.** PL1 and PL2 come from
  MSR `0x610`, which Windows exposes no API for, so the app reads it through the PawnIO driver's
  device interface with PawnIO's own IntelMSR module (embedded unmodified, LGPL-2.1 - see
  `THIRD-PARTY-NOTICES.md`). The card appears on SETUP only when the driver is already installed and
  the CPU is Intel, because the module does not load on AMD. Nothing is installed, started or written.
- The limits are **reported, not changed**. No part of Nitrous writes MSR `0x610`: there is no
  verified source for a safe upper bound on a given model, and raising a package limit past the
  firmware's own is exactly how a laptop is made to exceed its power budget. A write control needs a
  clamp proven on real hardware first.

## 0.14.0-test.12 - 2026-11-18

### Lighting

- **The brightness steps are learned from the keyboard, not assumed.** Five were hardcoded
  (0/25/50/75/100), but henry's ANV15-41 keeps only off, 50 and 100: the dropdown offered steps that
  did nothing, a saved `25` could never be restored, and the beat effect wrote levels the firmware
  refused, retrying each one. On a WMI backlight the app now writes each candidate once, keeps the ones
  that read back, restores the level it started on and saves the list; the dropdown, the restore and the
  beat effect all use it. One visible sweep on the first run, then nothing.
- The beat effect snaps to that list instead of the 25 % grid, so a three-step keyboard pulses
  0/50/100 rather than fighting the firmware.

## 0.14.0-test.11 - 2026-11-17

### Setup

- **Battery card reordered.** Battery Health now leads the card, with the battery icon beside it, and
  the 80% Battery Limit switch sits below with its description; USB Charging While Off follows. Layout
  only - nothing about what the switches do.

## 0.14.0-test.10 - 2026-11-17

### Lighting

- **A HID five-step keyboard keeps its original beat rate.** test.6 lowered the step floor to 150 ms
  for every five-step backlight and test.7 took the ACPI path down to 500 ms. Only the ACPI path has
  evidence against it - the EC HID path was slowed without cause - so it is back at the original
  70 ms. The ACPI path stays at 500 ms, and RGB keyboards were never affected.

## 0.14.0-test.9 - 2026-11-17

### Lighting

- **"Pulse with sound" warns when the backlight is driven through ACPI.** Ace Knight reinstalled Windows
  and measured it: LatencyMon flagged audio latency in **10 of 10** runs with the effect on, and in 1-2
  of 10 with it off. On those keyboards the toggle now carries a line saying so, because the firmware
  traffic is the trigger and the option should not be a trap. EC HID keyboards, where a step is a HID
  feature report, keep it without a warning. The README has a troubleshooting entry.

## 0.14.0-test.8 - 2026-11-16

### Lighting

- **Diagnostic modes for the audio-latency report.** Two test values on `KbSyncDiagnostic` split the
  effect in half: `writes` drives the backlight from a synthetic pattern with no audio client in the
  process, and `capture` runs the audio client but never touches the backlight. Which half reproduces
  the latency decides whether this is the ACPI calls or the loopback capture - the ordinary feature is
  unchanged when the value is absent or unrecognised.

## 0.14.0-test.7 - 2026-11-15

### Lighting

- **The beat effect steps half as often when the backlight is driven through ACPI.** The audio latency
  survived the previous cut, and running the read-only EC probe produces the same crackle in music -
  so on that platform the ACPI calls themselves starve the audio driver, not the value they carry.
  Five-step keyboards now step every 500 ms; EC HID keyboards keep 150 ms, where a write is a feature
  report rather than an ACPI method. The log records which floor was chosen.

## 0.14.0-test.6 - 2026-11-15

### Lighting

- **Sound sync no longer floods the firmware with backlight writes.** On a five-step backlight the
  beat effect runs a 50 Hz loop and set the level through `APGeAction`, and each write cost four ACPI
  round-trips: read the OEM hotkey byte from the registry, read the timeout, write the level, read it
  back. The hotkey and timeout are cached now, the write is verified every eighth step instead of
  every step, and the step floor went from 70 ms to 150 ms - about 42 calls per second down to
  roughly 7. Five firmware levels cannot show more than that anyway.
- **A model that refuses the write stops being asked.** After three consecutive refusals the effect
  turns itself off for the session and says so, instead of retrying at full rate forever.

Reported as audio latency (LatencyMon) while Nitrous was running, on an ANV15-51.

## 0.14.0-test.5 - 2026-11-14

### Dashboard

- The `GPU temp: the EC reports 0` line now only appears when the EC is demonstrably answering, with
  a CPU temperature in the same read. An unelevated or failed read returns zero for every sensor, and
  that is not the quirk the line is meant to report - it misled one diagnosis already.

## 0.14.0-test.4 - 2026-11-14

### Dashboard

- **GPU temperature shows on the DASH again when the EC reports 0.** pr4u's ANV15-52 shows a live GPU
  usage next to `-- C`: his EC answers 0 for the dGPU temperature, and the NVML fallback only ran when
  NVML had *not* been polled that tick. With Deep GPU Telemetry on, the usage number itself is an NVML
  reading, so the fallback was skipped and the zero stood. The dashboard now uses the driver's core
  temperature whenever the EC has none, and only asks NVML for a second reading if it wasn't polled
  at all. The history chart gains the same value.
- The log records it once per session: `GPU temp: the EC reports 0; using the driver's 42 C.`

## 0.14.0-test.3 - 2026-11-14

### GPU

- **The NVIDIA hotkey registers on every machine now.** Its registration had been placed inside the
  lighting branch, which only runs on four-zone keyboards, so a single-colour keyboard (lori's
  AN517-51) never bound it - which is why the button worked while the hotkey did nothing: the
  dashboard finds the Control Panel on its own.
- **A failed detection is no longer remembered for the session.** The tray starts at logon, when the
  Start apps list can still be empty; caching that miss kept the hotkey and the tray entry away until
  the next restart. Only a hit is cached now, and a press retries the search.
- Every hotkey logs `Hotkey: <name> bound to <binding>.` when it registers, so a binding that does
  nothing is visible in the log instead of silent.

## 0.14.0-test.2 - 2026-11-14

### GPU

- **The NVIDIA Control Panel is found on machines that only have the Store build.** lori's laptop has
  no NVIDIA App, and its Control Panel is the packaged version: no exe under Program Files and no
  Start Menu shortcut, so the GPU card never appeared. Detection now also searches the vendor folder
  for `nvcplui.exe` by name and, as a last resort, asks the Start apps list - the only place a
  packaged app appears - then launches it through `shell:AppsFolder`. The log records what was found
  and which route found it.

## 0.14.0-test.1 - 2026-11-14

### GPU

- **Open the NVIDIA App from Nitrous.** A card at the bottom of the GPU page opens the NVIDIA App,
  or the older NVIDIA Control Panel on machines that still have it, with a rebindable hotkey
  (**Ctrl + Alt + N** by default). The card, the tray entry and the hotkey only exist when the
  software is actually found: the App's install path first, then the Start Menu shortcuts, which also
  cover the packaged Control Panel. Nitrous launches it and stays out of the way - driver settings
  remain the vendor's business.

## 0.13.1 - 2026-11-13

### Update

- **An update no longer leaves Nitrous stuck in Task Manager.** The updater replaces the running
  executable by renaming it, and a single-file app reads its assemblies from that file, so anything
  the old process had not loaded yet becomes unloadable. With GPU clock management off (the default)
  the first such load was the GPU manager during shutdown: teardown threw, the final `Exit` was
  skipped, and the process stayed alive holding the single-instance lock. The replacement then waited
  a minute and gave up, leaving a tray-less `Nitrous.exe` and a relaunch that refused to start until
  it was killed by hand.
- Shutdown now exits whatever happens, every disposal is guarded, and a restart that cannot spawn its
  replacement stays open instead of exiting into nothing.
- A replacement that has waited 30 s for the instance it is replacing now ends that instance and
  takes over. **Updating from 0.13.0 therefore pauses for about 30 seconds after the download**, with
  no tray icon, while that happens; it is the handover, not a freeze. From 0.13.1 onwards the update
  is immediate again.

## 0.13.0 - 2026-11-13

### Appearance

- **Background presets.** SETUP > APPEARANCE gains a Background list: **Midnight** (the shipped dark
  grey), **Black**, **Graphite**, **Slate** and **Navy**. Each preset rebuilds the card, hover,
  border, grid and toggle shades around the window colour, so text contrast holds and nothing loses
  its edge. Presets rather than a colour picker because those surfaces are built to match the window
  colour, and an arbitrary pick could leave the labels unreadable.
- **Sections.** A second list sets the card surface on its own: **Default** keeps the card that
  belongs to the chosen background, or pick Black, Graphite or Slate. The shades that sit on the card
  are lifted from it, so a section colour never leaves its controls without an edge. Background and
  Sections are stored separately and both are restored at startup.
- **Font.** A third list sets the UI font, applied to the dashboard and the fan-curve window alike.
  It is built from the fonts this machine actually has: Windows default, Segoe UI, Bahnschrift,
  Calibri, Tahoma, Trebuchet MS and Inter, plus Cascadia Code, Cascadia Mono, Consolas, JetBrains
  Mono, Iosevka NF and Fira Code as code faces when they are installed. A family that is not
  installed is not offered, and a saved choice that has since been removed falls back to the Windows
  font and says so in the log.
- The header subtitle now wraps instead of being cut at the card edge, and the fan card's caption row
  has its own column, so a wide or monospaced font no longer clips either.

### Cooling

- **CoolBoost, where the firmware has it.** A switch on the DASH fan card raises Acer's own fan
  speeds. It appears only when APGeAction function 7's marker says that function is CoolBoost (the
  AN517-51 answers 1, the 16S and the ANV15 line answer 0). On firmware that uses that function for
  the operating mode the control stays hidden, because writing it there would change the mode.
- The switch hides in Max, which already runs the fans at full speed, and the state is read back
  from the firmware after every write and every profile change, so it cannot show a state the
  firmware refused.

### Docs

- The README claimed compatibility with 2021+ models; a 2019 **AN517-51** runs it, added to the
  confirmed list. The compatibility text now says the protocol decides, not the year.


## 0.12.0 - 2026-11-13

### Dashboard

- **CPU and GPU usage** now sit beside the fan speed on the two cards: CPU from the idle/total time
  between polls, GPU from the same NVML reading the GPU page uses. GPU usage reads `--` while the
  dGPU is in D3, because Nitrous will not wake it to take a measurement.
- **A two-minute history** under the cards: 60 samples at one per 2 s poll, 20-100 C, right-aligned
  so the newest sample is at the edge. A sleeping dGPU leaves a gap rather than a drop to zero.
- The history also reports the **peak** in its window - highest CPU and GPU temperature, highest fan
  speed and highest usage - reset as the window rolls.

### Setup

- **Custom boot logo.** SETUP > STARTUP shows the picture Acer's firmware reads at power-on and
  replaces it with your own. Any picture is accepted and re-encoded to a baseline JPEG (or a GIF if
  the encoder produces anything else), scaled to at most 40 % of the panel so the firmware's decoder
  takes it. REMOVE deletes the picture and brings Acer's logo back. The card only appears where the
  firmware advertises the feature: SMBIOS record 0x0D or the BIOS setup's switch answering 0/1.
- The write is a file on the EFI system partition, not a BIOS change: temp file, flush, rename,
  read-back hash check and a free-space guard, and the firmware switch is remembered so REMOVE puts
  it back. Wiping or reinstalling the OS removes the picture with the partition.
- **Battery health**: design capacity, what the pack holds now and the cycle count, in the same card
  as the 80 % charge limit. Read once when the dashboard opens, from the battery's WMI classes, with
  `powercfg /batteryreport /xml` as the fallback for drivers whose `BatteryStaticData` class fails to
  instantiate (the AN16S-61 is one).
- The page reads as sections now: one spacing rule for the labels, one gap between cards, and the
  battery rows are a single block. `EXPORT DIAGNOSTICS` became `EXPORT ZIP`, which is what it does
  and no longer clips its own label.

### Lighting

- **The saved lighting is re-applied after a power-mode change**, two seconds later so Acer's
  acknowledgement has finished. The firmware lights the keyboard on a mode change even when the
  saved state is off, and on models with a controller the flash left its colour behind; until now
  nothing put the preset back until the next manual edit. The same restore runs at startup.
- **The Fn-brightness hint no longer returns after a slider is moved.** The sliders write the
  lighting controller while the Fn keys write a separate EC register, so moving a slider left the
  register untouched and the hint reappeared on the next open. A slider move now applies its level
  to that register too - snapped to its five steps and debounced, because each write opens the EC
  HID - and both sides are compared on those same five steps.

### Diagnostics

- **EC probe version 13** asks the firmware about CoolBoost the way Acer's own software does:
  APGeAction function 7's marker (1 = CoolBoost, 6 = the operating mode) and, when it is CoolBoost,
  sub-function 2's state. Both answers and a verdict go into the JSON and the text report.
- The boot-logo preview no longer throws when the EFI partition cannot be read, which happens on a
  dashboard opened without elevation; it shows no preview and logs the reason instead.


## 0.11.0 - 2026-11-09
<!-- release-name: Total Lightning Control -->

### Lighting

- **Screen sync.** The four zones follow the colours on screen, with a **Screen saturation** slider
  (0-200 %), a brightness cap and a **Follow** target (`Keyboard` or `Keyboard + lid logo`). It is a
  software effect: the tray keeps writing while the dashboard is closed, and turning it off restores
  the saved preset. Protected content and true fullscreen can block the capture; it is not a
  firmware effect.
- **Pulse with sound** on top of screen sync: the screen supplies the colours while the system audio
  scales their brightness, with a **Silence floor** (0-10 %) so hits can fall fully dark or keep a
  dim glow.
- **Sound sync** is its own source, taken from WASAPI loopback (system audio, never the microphone).
  **Peak** follows loudness, **Spectrum** splits bass / low-mid / high-mid / treble across the
  zones, **Beat** lands on kick onsets, and **Vibe** is a three-layer engine ported from the
  MIT-licensed KB-Breathe. A shared **Sensitivity** slider sets the threshold, and 16-, 24- and
  32-bit mix formats are accepted.
- **Vibe is the default on five-step keyboards.** A kick holds step 4 for about 110 ms and then
  drops straight to the base level, so the backlight goes 4 -> 0 instead of walking 4-3-2-1. Hi-hats
  give 3, sustained vocals 1-2, and silence gives 0. Levels map 1:1 onto the firmware's 25 % steps.
- **The active preset is the sync base.** Global contributes its colour and effect, Area its zone
  colours and Dynamic effect, and brightness follows that preset's slider. Switching Global/Area no
  longer suspends sync; the source is remembered and resumed.
- With sound sync on, the **firmware keeps animating the selected effect** while the audio drives
  its brightness, and both effect and speed stay editable. The **lid is written once as Static**,
  because the cover logo stutters when its brightness or speed is rewritten per frame.
- Sync **pauses for 1.5 seconds during a power-mode flash** so the controller's acknowledgement is
  not overwritten, and it keeps working from Area / Keyboard as well as Global.
- The **lid-logo check writes green** and asks again once for older answers: red is already in the
  power-mode palette, so it proved nothing on a Performance machine. A lid that only follows the
  power-mode key is explained and its controls are hidden.
- **Fn brightness is respected.** The LIGHT page reads the EC level once on open and, if it differs
  from the saved preset, says so under the slider instead of overwriting it. The saved preset is
  applied once when the tray starts, not every time the dashboard opens.

### Keyboard

- **Backlight brightness on models without a lighting controller**, for example the single-colour
  ANV15-52: a five-step row (`Off`, `25`, `50`, `75`, `100`) in Setup > KEYBOARD, using the EC HID
  interface where the model has one and `APGeAction` where it does not. Values snap to the
  firmware's levels, and toggling auto-off no longer resets the brightness.
- **Sound sync on that same five-step path**: Setup > KEYBOARD > **Pulse with sound** drives the
  backlight from the system audio with **Vibe**, **Beat** or **Peak**, and restores the chosen level
  when it stops. The pulse reaches 0 between hits rather than stopping at 25 %.

### Hotkeys

- Four new global bindings: **Cycle Fan Mode** (Auto -> Max -> Custom), **Screen Sync**,
  **Sound Sync** and **Next Lighting Effect**. All are unbound by default, confirm with an OSD, and
  the two lighting ones are hidden where the keyboard has no RGB zones.
- The **Global Hotkeys** card is grouped by where each action applies (WINDOW, DASHBOARD, LIGHT
  PAGE), with a separator and a one-line description for every binding.

### Dashboard

- The **Whisper Mode** label follows the request immediately, so it no longer vanishes while Quiet
  is still asking for it.

### Website

- An **interactive simulator** renders the dashboard, the four zones, the lid logo and the sync
  behaviour in the browser, linked from the header and the hero.
- The README and site describe the screen and audio sync as **TOTAL LIGHTNING CONTROL**.

### Diagnostics

- **EC probe version 12** records the keyboard type and both brightness paths, plus which one the
  model uses, so coverage can be mapped from a report without writing anything to the hardware.

### Build

- The executable carries **Jeremy Aliparo** as company and author, a copyright line and a one-line
  description.

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

