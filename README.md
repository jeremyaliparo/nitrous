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

* **🔋 Battery Protection:** Hardware-level 80% charge limit to preserve battery health.
* **⚡ Dynamic Power:** Instantly swap between Quiet, Balanced, and Performance motherboard TDP limits.
* **🔄 Auto-Switching:** Automatically drops to Quiet on battery and boosts to Performance when plugged in.
* **🌪️ Fan Override:** Force your fans to Auto, 50% (Medium), or 100% (Max).
* **🚀 True Startup:** Bypasses Windows UAC using Task Scheduler to launch silently in the background every time you boot.

---

### Installation

Because Nitrous is packaged as a standalone executable, there is no installer required.

1. Download **`Nitrous.exe`** from the [Releases](https://github.com/jeremyaliparo/nitrous/releases) page.
2. Place the file anywhere on your PC (e.g., your Documents or Utilities folder).
3. Double-click to run it.

### Usage

Once running, Nitrous lives quietly in your Windows System Tray.

Simply **right-click the icon** to access all hardware toggles. Click **Run on Windows Startup** to have Nitrous automatically handle your thermals and battery limits forever.

---

### Compatibility

Built and tested specifically for the **Acer Nitro 16S (AN16S-61)**, but utilizes standard `AcerGamingFunction` WMI classes that should work seamlessly across most modern Acer Nitro and Predator laptops (2021+).

*Disclaimer: This is an unofficial, open-source tool. I am not affiliated with Acer. Use at your own risk.*
