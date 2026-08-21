<p align="center">
  <img src="assets/logo.png" alt="Nitrous Logo" width="120">
</p>

<h1 align="center">Nitrous ⚡</h1>

<p align="center">
  <strong>Pure, zero-bloat hardware control for Acer Nitro laptops.</strong>
</p>

---

NitroSense is heavy. Nitrous is invisible.

Nitrous is a lightweight, single-file system tray utility that speaks directly to your Acer Nitro motherboard's Embedded Controller (EC) via WMI. No bulky UI, no background telemetry services fighting Windows, and no unnecessary RAM usage. Just raw, instant hardware control.

### Features
* 🔄 **Built-in Auto-Updater:** Automatically detects and installs new releases directly from GitHub.
* 🔋 **Battery Protection:** Hardware-level 80% charge limit to preserve battery health.
* ⚡ **Dynamic Power:** Instantly swap between Quiet, Balanced, Performance, and Turbo motherboard TDP limits.
* 🧠 **Smart Auto-Switching:** Automatically drops to Quiet on battery and restores your exact previous power/fan state (e.g., Turbo) when plugged back in.
* 🖥️ **Smart Refresh Rate:** Automatically drops your laptop screen to 60Hz on battery and boosts to max Hz on AC power (safely ignores external monitors).
* ❄️ **Independent Fan Override:** Force your fans to Auto, Quiet (25%), Medium (50%), or Max (100%) using mathematically verified 64-bit WMI payloads.
* 🚀 **True Startup:** Bypasses Windows UAC using Task Scheduler to launch silently in the background every time you boot.

### Installation
Because Nitrous is packaged as a standalone executable, there is no installer required.
1. Download **`Nitrous.exe`** from the [Releases](https://github.com/jeremyaliparo/nitrous/releases) page.
2. Place the file anywhere on your PC (e.g., your Documents or Utilities folder).
3. Double-click to run it.

### Usage
Once running, Nitrous lives quietly in your Windows System Tray. Simply **right-click the icon** to access all hardware toggles. Click **Run on Windows Startup** to have Nitrous automatically handle your thermals, refresh rate, and battery limits forever.

### Compatibility
Built and tested for modern Acer Nitro laptops utilizing standard `AcerGamingFunction` WMI classes (2021+). 

**Confirmed Working On:**
* Acer Nitro 16S (AN16S-61)
* Acer Nitro V 15 (ANV15-52) - *Special thanks to [@Baymax0251](https://github.com/Baymax0251) for hardware testing!*

*Disclaimer: This is an unofficial, open-source tool. I am not affiliated with Acer. Use at your own risk.*
