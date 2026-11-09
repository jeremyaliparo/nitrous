/* ==========================================================================
   Nitrous simulator
   Draws a four-zone keyboard, runs the lighting effects, and simulates the
   SYNC card. Nothing here touches real hardware or system audio: the beat is
   synthesised in the page with the Web Audio API.
   ========================================================================== */

(() => {
  "use strict";

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  const TAU = Math.PI * 2;

  /* ------------------------------- Utilities ----------------------------- */

  const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
  const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
  const lerp = (a, b, t) => a + (b - a) * t;

  function hexToRgb(hex) {
    const v = parseInt(hex.replace("#", ""), 16);
    return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
  }

  function rgbToHex([r, g, b]) {
    return (
      "#" +
      [r, g, b]
        .map((c) => Math.round(clamp(c, 0, 255)).toString(16).padStart(2, "0"))
        .join("")
    );
  }

  function rgbToHsl([r, g, b]) {
    r /= 255;
    g /= 255;
    b /= 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    if (max === min) return [0, 0, l];
    const d = max - min;
    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
    else if (max === g) h = ((b - r) / d + 2) / 6;
    else h = ((r - g) / d + 4) / 6;
    return [h, s, l];
  }

  function hslToRgb([h, s, l]) {
    if (s === 0) {
      const v = Math.round(l * 255);
      return [v, v, v];
    }
    const hue = (p, q, t) => {
      if (t < 0) t += 1;
      if (t > 1) t -= 1;
      if (t < 1 / 6) return p + (q - p) * 6 * t;
      if (t < 1 / 2) return q;
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
      return p;
    };
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    return [
      Math.round(hue(p, q, h + 1 / 3) * 255),
      Math.round(hue(p, q, h) * 255),
      Math.round(hue(p, q, h - 1 / 3) * 255),
    ];
  }

  function shiftHue(rgb, deg) {
    const [h, s, l] = rgbToHsl(rgb);
    return hslToRgb([(((h + deg / 360) % 1) + 1) % 1, s, l]);
  }

  function mixRgb(a, b, t) {
    return [
      Math.round(lerp(a[0], b[0], t)),
      Math.round(lerp(a[1], b[1], t)),
      Math.round(lerp(a[2], b[2], t)),
    ];
  }

  function saturateRgb(rgb, amount) {
    const [h, s, l] = rgbToHsl(rgb);
    return hslToRgb([h, clamp(s * (amount / 100), 0, 1), l]);
  }

  const rgbStr = ([r, g, b]) => `${r} ${g} ${b}`;

  /* -------------------------------- State -------------------------------- */

  const PALETTE = [
    "#b388ff",
    "#7c3aed",
    "#4f8dff",
    "#22d3ee",
    "#86efac",
    "#ff8a1e",
    "#ef2b3f",
    "#ff5c8a",
  ];

  const SCENES = [
    { name: "Neon sky", palette: ["#8b5cf6", "#b545e8", "#4f8dff", "#22d3ee"] },
    { name: "Sunset drive", palette: ["#ff8a1e", "#ef2b3f", "#b545e8", "#4f8dff"] },
    { name: "Four bars", palette: ["#b388ff", "#4f8dff", "#22d3ee", "#ff5c8a"] },
  ];

  const MODES = [
    { name: "Quiet", color: "#4f8dff", heat: 0.6, power: 45, floor: 550, curveStart: 54 },
    { name: "Balanced", color: "#86efac", heat: 0.78, power: 60, floor: 700, curveStart: 48 },
    { name: "Performance", color: "#ff8a1e", heat: 0.92, power: 85, floor: 900, curveStart: 44 },
    { name: "Turbo", color: "#ef2b3f", heat: 1, power: 100, floor: 1100, curveStart: 41 },
  ];

  const WORKLOADS = {
    idle: { cpu: 0.04, gpu: 0.02 },
    office: { cpu: 0.28, gpu: 0.08 },
    gaming: { cpu: 0.55, gpu: 0.85 },
    stress: { cpu: 1, gpu: 0.95 },
  };

  const state = {
    lighting: "global",
    device: "keyboard",
    areaStyle: "Static",
    global: { effect: "Static", color: "#b388ff", brightness: 100, speed: 3 },
    logo: { effect: "Static", color: "#b388ff", brightness: 100, speed: 3 },
    zones: ["#b388ff", "#4f8dff", "#22d3ee", "#ff5c8a"],
    areaEffect: "Breathing",
    areaSpeed: 4,
    areaBrightness: 100,
    screenSync: false,
    saturation: 100,
    pulse: false,
    floor: 0,
    soundSync: false,
    soundMode: "Spectrum",
    sensitivity: 4,
    follow: "both",
    scene: 0,
    mode: 2,
    workload: "office",
    fanMode: "auto",
    refresh: 144,
    cpuFan: 40,
    gpuFan: 40,
  };

  const thermal = {
    cpu: 48,
    gpu: 44,
    cpuRpm: 1200,
    gpuRpm: 1100,
    clock: 0,
    shown: {},
  };

  /* ------------------------------- Keyboard ------------------------------ */

  const ROWS = [
    [
      ["Esc", 1], ["F1", 1], ["F2", 1], ["F3", 1], ["F4", 1], ["F5", 1], ["F6", 1],
      ["F7", 1], ["F8", 1], ["F9", 1], ["F10", 1], ["F11", 1], ["F12", 1], ["PrtSc", 1],
    ],
    [
      ["`", 1], ["1", 1], ["2", 1], ["3", 1], ["4", 1], ["5", 1], ["6", 1], ["7", 1],
      ["8", 1], ["9", 1], ["0", 1], ["-", 1], ["=", 1], ["Bksp", 2],
    ],
    [
      ["Tab", 1.5], ["Q", 1], ["W", 1], ["E", 1], ["R", 1], ["T", 1], ["Y", 1], ["U", 1],
      ["I", 1], ["O", 1], ["P", 1], ["[", 1], ["]", 1], ["\\", 1.5],
    ],
    [
      ["Caps", 1.75], ["A", 1], ["S", 1], ["D", 1], ["F", 1], ["G", 1], ["H", 1], ["J", 1],
      ["K", 1], ["L", 1], [";", 1], ["'", 1], ["Enter", 2.25],
    ],
    [
      ["Shift", 2.25], ["Z", 1], ["X", 1], ["C", 1], ["V", 1], ["B", 1], ["N", 1], ["M", 1],
      [",", 1], [".", 1], ["/", 1], ["Shift", 2.75],
    ],
    [
      ["Ctrl", 1.25], ["Fn", 1], ["Win", 1], ["Alt", 1], ["Space", 6.25], ["Alt", 1],
      ["Fn", 1], ["Ctrl", 1], ["←", 1], ["↑", 1], ["↓", 1], ["→", 1],
    ],
  ];

  let keys = [];
  let flash = { until: 0, rgb: [255, 138, 30] };

  function buildKeyboard() {
    const kb = $("#keyboard");
    if (!kb) return;
    kb.innerHTML = "";
    keys = [];

    ROWS.forEach((row, rowIndex) => {
      const rowEl = document.createElement("div");
      rowEl.className = "kb-row";
      const total = row.reduce((sum, [, w]) => sum + w, 0);
      let cursor = 0;

      row.forEach(([label, w]) => {
        const el = document.createElement("div");
        el.className = "key";
        el.style.setProperty("--w", w);
        el.dataset.label = label;
        el.setAttribute("aria-hidden", "true");

        if (label.length <= 3 || label === "Space") el.textContent = label;

        const centre = (cursor + w / 2) / total;
        cursor += w;

        const zone = Math.min(3, Math.floor(centre * 4));
        el.style.setProperty("--rgb", "60 60 78");
        el.style.setProperty("--a", "0.12");

        rowEl.appendChild(el);
        keys.push({
          el,
          zone,
          x: centre,
          y: ROWS.length === 1 ? 0.5 : rowIndex / (ROWS.length - 1),
          lastRgb: "",
          lastA: "",
        });
      });

      kb.appendChild(rowEl);
    });
  }

  /* --------------------------------- Audio ------------------------------- */

  const Audio = {
    ctx: null,
    master: null,
    analyser: null,
    data: null,
    noise: null,
    playing: false,
    timer: null,
    next: 0,
    step: 0,

    ensure() {
      if (this.ctx) return true;
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (!Ctx) return false;

      this.ctx = new Ctx();
      this.master = this.ctx.createGain();
      this.master.gain.value = 0.32;

      this.analyser = this.ctx.createAnalyser();
      this.analyser.fftSize = 2048;
      this.analyser.smoothingTimeConstant = 0.62;

      this.data = new Uint8Array(this.analyser.frequencyBinCount);

      this.master.connect(this.analyser);
      this.analyser.connect(this.ctx.destination);

      const length = Math.floor(this.ctx.sampleRate * 0.3);
      this.noise = this.ctx.createBuffer(1, length, this.ctx.sampleRate);
      const channel = this.noise.getChannelData(0);
      for (let i = 0; i < length; i += 1) channel[i] = Math.random() * 2 - 1;

      return true;
    },

    async start() {
      if (!this.ensure()) return false;
      try {
        await this.ctx.resume();
      } catch (error) {
        return false;
      }

      if (this.playing) return true;

      this.playing = true;
      this.step = 0;
      this.next = this.ctx.currentTime + 0.06;
      this.timer = window.setInterval(() => this.schedule(), 25);
      return true;
    },

    stop() {
      this.playing = false;
      if (this.timer) {
        window.clearInterval(this.timer);
        this.timer = null;
      }
    },

    schedule() {
      if (!this.playing || !this.ctx) return;
      const horizon = this.ctx.currentTime + 0.12;
      while (this.next < horizon) {
        this.playStep(this.step, this.next);
        this.next += 0.125;
        this.step += 1;
      }
    },

    playStep(step, time) {
      if (step % 4 === 0) this.kick(time);
      if (step % 2 === 0) this.hat(time);
      if (step % 8 === 0) {
        const notes = [55, 55, 65.41, 49];
        this.bass(time, notes[(step / 8) % notes.length]);
      }
      if (step % 16 === 12) this.hat(time, 0.16);
    },

    kick(time) {
      const osc = this.ctx.createOscillator();
      const gain = this.ctx.createGain();
      osc.type = "sine";
      osc.frequency.setValueAtTime(150, time);
      osc.frequency.exponentialRampToValueAtTime(45, time + 0.14);
      gain.gain.setValueAtTime(0.9, time);
      gain.gain.exponentialRampToValueAtTime(0.001, time + 0.3);
      osc.connect(gain).connect(this.master);
      osc.start(time);
      osc.stop(time + 0.32);
    },

    hat(time, level = 0.24) {
      const source = this.ctx.createBufferSource();
      source.buffer = this.noise;
      const high = this.ctx.createBiquadFilter();
      high.type = "highpass";
      high.frequency.value = 7200;
      const gain = this.ctx.createGain();
      gain.gain.setValueAtTime(level, time);
      gain.gain.exponentialRampToValueAtTime(0.001, time + 0.07);
      source.connect(high).connect(gain).connect(this.master);
      source.start(time);
      source.stop(time + 0.09);
    },

    bass(time, freq) {
      const osc = this.ctx.createOscillator();
      const filter = this.ctx.createBiquadFilter();
      const gain = this.ctx.createGain();
      osc.type = "sawtooth";
      osc.frequency.value = freq;
      filter.type = "lowpass";
      filter.frequency.value = 420;
      gain.gain.setValueAtTime(0.0001, time);
      gain.gain.exponentialRampToValueAtTime(0.22, time + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, time + 0.42);
      osc.connect(filter).connect(gain).connect(this.master);
      osc.start(time);
      osc.stop(time + 0.44);
    },

    read() {
      if (!this.playing || !this.analyser) return [0, 0, 0, 0];
      this.analyser.getByteFrequencyData(this.data);

      const ranges = [
        [1, 5],
        [6, 18],
        [19, 50],
        [51, 130],
      ];

      return ranges.map(([from, to]) => {
        let sum = 0;
        for (let i = from; i <= to; i += 1) sum += this.data[i];
        return clamp01(sum / (to - from + 1) / 190);
      });
    },
  };

  /* -------------------------------- Thermal ------------------------------ */

  function fanTarget(temp, mode, pct) {
    if (state.fanMode === "max") return 5200;
    if (state.fanMode === "custom") return Math.max(500, 5200 * (pct / 100));

    const ratio = clamp01((temp - mode.curveStart) / 50);
    return Math.max(mode.floor, 5200 * ratio);
  }

  function approach(current, target, dt, upTau, downTau) {
    const tau = target > current ? upTau : downTau;
    return current + (target - current) * (1 - Math.exp(-dt / tau));
  }

  function setMetric(id, text) {
    if (thermal.shown[id] === text) return;
    const element = document.getElementById(id);
    if (element) element.textContent = text;
    thermal.shown[id] = text;
  }

  function updateDash(load, mode) {
    const rpm = (value) =>
      Math.round(value).toLocaleString("en-US").replace(/,/g, " ");

    setMetric("dashCpuTemp", `${Math.round(thermal.cpu)} °C`);
    setMetric("dashCpuRpm", `${rpm(thermal.cpuRpm)} RPM`);
    setMetric("dashGpuTemp", `${Math.round(thermal.gpu)} °C`);
    setMetric("dashGpuRpm", `${rpm(thermal.gpuRpm)} RPM`);

    const watts = Math.round(mode.power * (0.3 + 0.7 * load.cpu));
    setMetric("dashPower", `${watts} W`);
  }

  function updateThermal(dt) {
    const load = WORKLOADS[state.workload] || WORKLOADS.office;
    const mode = MODES[state.mode];

    const cpuTarget = fanTarget(thermal.cpu, mode, state.cpuFan);
    const gpuTarget = fanTarget(thermal.gpu, mode, state.gpuFan);

    // Fans spin up quickly and step down slowly, like the real ramp shaping.
    thermal.cpuRpm = approach(thermal.cpuRpm, cpuTarget, dt, 0.6, 2.6);
    thermal.gpuRpm = approach(thermal.gpuRpm, gpuTarget, dt, 0.6, 2.6);

    const airflowCpu = 0.65 + (thermal.cpuRpm / 5200) * 1.15;
    const airflowGpu = 0.65 + (thermal.gpuRpm / 5200) * 1.15;

    const cpuEq = 24 + (130 * Math.pow(load.cpu, 1.3) * mode.heat) / airflowCpu;
    const gpuEq = 24 + (100 * Math.pow(load.gpu, 1.3) * mode.heat) / airflowGpu;

    thermal.cpu = clamp(approach(thermal.cpu, cpuEq, dt, 5, 7), 32, 95);
    thermal.gpu = clamp(approach(thermal.gpu, gpuEq, dt, 5, 7), 32, 95);

    thermal.clock += dt;
    if (thermal.clock >= 0.25) {
      thermal.clock = 0;
      updateDash(load, mode);
    }
  }

  /* ------------------------------- Painting ------------------------------ */

  let beatEnv = 0;
  let peakEnv = 0;
  let spectrumEnv = [0, 0, 0, 0];
  let lastBeat = 0;
  let statusCache = "";
  let barsCache = "";

  const EFFECTS = new Set([
    "Static", "Breathing", "Neon", "Wave", "Ripple", "Zoom", "Snake", "Disco",
  ]);

  function effectAt(effect, x, y, t, speed, rgb) {
    switch (effect) {
      case "Breathing": {
        const a = 0.16 + 0.84 * (0.5 + 0.5 * Math.sin(t * (0.55 + speed * 0.3)));
        return { rgb, a };
      }
      case "Neon": {
        const hue = (t * (14 + speed * 7)) % 360;
        const a = 0.55 + 0.45 * (0.5 + 0.5 * Math.sin(t * 2.4 + x * 2));
        return { rgb: shiftHue(rgb, hue + x * 60), a };
      }
      case "Wave": {
        const phase = (x * 2.1 - t * (0.32 + speed * 0.11)) * TAU;
        return { rgb, a: 0.1 + 0.9 * (0.5 + 0.5 * Math.sin(phase)) };
      }
      case "Ripple": {
        const distance = Math.hypot(x - 0.5, y - 0.5) * 2;
        const phase = (distance * 2.6 - t * (0.4 + speed * 0.1)) * TAU;
        return { rgb, a: 0.08 + 0.92 * (0.5 + 0.5 * Math.sin(phase)) };
      }
      case "Zoom": {
        const distance = 1 - Math.hypot(x - 0.5, y - 0.5) * 2;
        const phase = (distance * 2.6 - t * (0.4 + speed * 0.1)) * TAU;
        return { rgb, a: 0.08 + 0.92 * (0.5 + 0.5 * Math.sin(phase)) };
      }
      case "Snake": {
        const pos = ((t * (0.11 + speed * 0.045)) % 1.5) - 0.25;
        const distance = Math.abs(x - pos);
        const a = distance < 0.07 ? 1 : distance < 0.15 ? 0.4 : 0.05;
        return { rgb, a };
      }
      case "Disco": {
        const seed = Math.floor(t * (1.6 + speed * 0.7)) * 7.31 + Math.floor(x * 17) * 3.7;
        return {
          rgb: hslToRgb([(seed * 0.137) % 1, 0.85, 0.62]),
          a: 0.35 + 0.65 * Math.abs(Math.sin(seed * 1.7)),
        };
      }
      default:
        return { rgb, a: 1 };
    }
  }

  function screenPalette() {
    return SCENES[state.scene].palette.map((hex) =>
      saturateRgb(hexToRgb(hex), state.saturation)
    );
  }

  function currentConfig() {
    const syncCap =
      state.lighting === "area" ? state.areaBrightness : state.global.brightness;

    if (state.screenSync) {
      return {
        effect: "Static",
        colours: screenPalette(),
        cap: syncCap,
        speed: 3,
      };
    }
    if (state.soundSync) {
      if (state.lighting === "area") {
        return {
          effect: state.areaStyle === "Static" ? "Static" : state.areaEffect,
          colours: state.zones.map(hexToRgb),
          cap: syncCap,
          speed: state.areaSpeed,
        };
      }

      const rgb = hexToRgb(state.global.color);
      return {
        effect: EFFECTS.has(state.global.effect) ? state.global.effect : "Static",
        colours: [rgb, rgb, rgb, rgb],
        cap: syncCap,
        speed: state.global.speed,
      };
    }
    if (state.lighting === "global") {
      const rgb = hexToRgb(state.global.color);
      return {
        effect: EFFECTS.has(state.global.effect) ? state.global.effect : "Static",
        colours: [rgb, rgb, rgb, rgb],
        cap: state.global.brightness,
        speed: state.global.speed,
      };
    }
    return {
      effect: state.areaStyle === "Static" ? "Static" : state.areaEffect,
      colours: state.zones.map(hexToRgb),
      cap: state.areaBrightness,
      speed: state.areaSpeed,
    };
  }

  function audioAlpha(zone) {
    const sens = state.sensitivity / 4;

    if (state.soundSync) {
      if (state.soundMode === "Spectrum") {
        return clamp01(spectrumEnv[zone] * sens * 1.7);
      }
      if (state.soundMode === "Beat") {
        return clamp01(beatEnv * sens * 1.15);
      }
      return clamp01(peakEnv * sens * 1.8);
    }

    if (state.screenSync && state.pulse) {
      const floor = state.floor / 100;
      const value = clamp01(beatEnv * 1.1 * sens + peakEnv * 0.3);
      return floor + value * (1 - floor);
    }

    return null;
  }

  function audioBrightness() {
    if (!state.soundSync) return 1;
    return Math.max(audioAlpha(0), audioAlpha(1), audioAlpha(2), audioAlpha(3));
  }

  function paint(t, dt) {
    const now = performance.now();
    const bands = Audio.read();

    const bass = bands[0];
    if (bass > 0.32 && now - lastBeat > 140) {
      lastBeat = now;
      beatEnv = 1;
    }
    beatEnv = Math.max(0, beatEnv - dt * 1.9);

    const presence = bands.reduce((a, b) => a + b, 0) / bands.length;
    peakEnv += (presence - peakEnv) * Math.min(1, dt * 16);
    for (let i = 0; i < 4; i += 1) {
      spectrumEnv[i] += (bands[i] - spectrumEnv[i]) * Math.min(1, dt * 18);
    }

    const cfg = currentConfig();
    const flashLeft = Math.max(0, (flash.until - now) / 1100);
    const cap = cfg.cap / 100;
    const syncOn = state.screenSync || state.soundSync;

    let totalAlpha = 0;

    for (const key of keys) {
      let colour = cfg.colours[key.zone];
      let alpha;

      const audio = audioAlpha(key.zone);
      const animated = cfg.effect !== "Static";

      if (audio !== null && !(state.soundSync && animated)) {
        alpha = audio;
      } else {
        // With an effect during sound sync the firmware keeps the animation running and the
        // audio scales its brightness, so the movement survives.
        const eased = effectAt(cfg.effect, key.x, key.y, t, cfg.speed, colour);
        colour = eased.rgb;
        alpha = eased.a;
        if (state.soundSync && animated) alpha *= audioBrightness();
      }

      alpha *= cap;

      if (flashLeft > 0) {
        const f = flashLeft * flashLeft;
        colour = mixRgb(colour, flash.rgb, f * 0.85);
        alpha = Math.max(alpha, flashLeft * cap);
      }

      const rgb = rgbStr(colour);
      const a = alpha.toFixed(2);

      if (key.lastRgb !== rgb) {
        key.el.style.setProperty("--rgb", rgb);
        key.lastRgb = rgb;
      }
      if (key.lastA !== a) {
        key.el.style.setProperty("--a", a);
        key.lastA = a;
      }

      totalAlpha += alpha;
    }

    const average = keys.length ? totalAlpha / keys.length : 0;

    paintLogo(t, cfg, average, syncOn);

    const glow = $(".deck-glow");
    if (glow) {
      const base = cfg.colours[0];
      glow.style.setProperty("--deck", `rgb(${base[0]} ${base[1]} ${base[2]} / 0.45)`);
      glow.style.opacity = String(clamp01(average * 0.9));
    }

    const status = statusText(cfg);
    const statusEl = $("#lightStatus");
    if (statusEl && status !== statusCache) {
      statusEl.textContent = status;
      statusCache = status;
    }
  }

  function paintLogo(t, cfg, average, syncOn) {
    const preview = $("#lidPreview");
    if (!preview) return;

    let rgb;
    let alpha;

    if (state.soundSync && state.follow === "both") {
      // Sound sync writes the lid once as a Static frame: it cannot follow the beat, because the
      // cover logo stutters whenever its brightness is rewritten per frame.
      const avg = cfg.colours.reduce(
        (acc, c) => [acc[0] + c[0], acc[1] + c[1], acc[2] + c[2]],
        [0, 0, 0]
      );
      rgb = avg.map((c) => Math.round(c / cfg.colours.length));
      alpha = cfg.cap / 100;
    } else if (syncOn && state.follow === "both") {
      const avg = cfg.colours.reduce(
        (acc, c) => [acc[0] + c[0], acc[1] + c[1], acc[2] + c[2]],
        [0, 0, 0]
      );
      rgb = avg.map((c) => Math.round(c / cfg.colours.length));
      alpha = average;
    } else {
      const eased = effectAt(
        state.logo.effect,
        0.5,
        0.5,
        t,
        state.logo.speed,
        hexToRgb(state.logo.color)
      );
      rgb = eased.rgb;
      alpha = eased.a * (state.logo.brightness / 100);
    }

    const value = rgbStr(rgb);
    if (preview.dataset.rgb !== value) {
      preview.style.setProperty("--rgb", value);
      preview.dataset.rgb = value;
    }
    preview.style.setProperty("--a", alpha.toFixed(2));
  }

  function statusText(cfg) {
    if (state.screenSync) return "Screen sync";
    if (state.soundSync) return "Sound sync";
    return cfg.effect;
  }

  function updateScreenBars() {
    const bars = $$("#screenBars i");
    const palette = screenPalette();
    const signature = palette.map(rgbToHex).join("");
    if (signature === barsCache) return;
    barsCache = signature;

    bars.forEach((bar, index) => {
      const hex = rgbToHex(palette[index]);
      bar.style.background = hex;
      bar.style.color = hex;
    });
  }

  function setScene(index) {
    state.scene = clamp(index, 0, SCENES.length - 1);
    const screen = $("#laptopScreen");
    if (screen) screen.dataset.scene = String(state.scene);

    const name = $("#screenSceneName");
    if (name) name.textContent = SCENES[state.scene].name;

    $$("#screenScenes button").forEach((button, i) => {
      button.classList.toggle("active", i === state.scene);
      button.setAttribute("aria-pressed", String(i === state.scene));
    });

    barsCache = "";
    updateScreenBars();
  }

  /* -------------------------------- UI sync ------------------------------ */

  function fillRange(input) {
    const min = Number(input.min) || 0;
    const max = Number(input.max || 100);
    const value = Number(input.value);
    const percent = max === min ? 0 : ((value - min) / (max - min)) * 100;
    input.style.setProperty("--fill", `${percent.toFixed(1)}%`);
  }

  function syncControls() {
    $("#globalEffect").value = state.global.effect;
    $("#globalColor").value = state.global.color;
    $("#globalBrightness").value = state.global.brightness;
    $("#globalBrightnessValue").textContent = `${state.global.brightness}%`;
    $("#globalSpeed").value = state.global.speed;
    $("#globalSpeedValue").textContent = String(state.global.speed);

    $("#logoEffect").value = state.logo.effect;
    $("#logoColor").value = state.logo.color;
    $("#logoBrightness").value = state.logo.brightness;
    $("#logoBrightnessValue").textContent = `${state.logo.brightness}%`;
    $("#logoSpeed").value = state.logo.speed;
    $("#logoSpeedValue").textContent = String(state.logo.speed);

    $("#areaEffect").value = state.areaEffect;
    $("#areaSpeed").value = state.areaSpeed;
    $("#areaSpeedValue").textContent = String(state.areaSpeed);
    $("#areaBrightness").value = state.areaBrightness;
    $("#areaBrightnessValue").textContent = `${state.areaBrightness}%`;
    $("#areaBrightnessDynamic").value = state.areaBrightness;
    $("#areaBrightnessDynamicValue").textContent = `${state.areaBrightness}%`;

    $("#screenSync").checked = state.screenSync;
    $("#soundSync").checked = state.soundSync;
    $("#pulse").checked = state.pulse;
    $("#floor").value = state.floor;
    $("#floorValue").textContent = `${state.floor}%`;
    $("#saturation").value = state.saturation;
    $("#saturationValue").textContent = `${state.saturation}%`;
    $("#soundMode").value = state.soundMode;
    $("#sensitivity").value = state.sensitivity;
    $("#sensitivityValue").textContent = String(state.sensitivity);
    $("#follow").value = state.follow;

    $("#screenOptions").classList.toggle("is-open", state.screenSync);
    $("#soundOptions").classList.toggle("is-open", state.soundSync);

    $("#globalPanel").hidden = state.lighting !== "global";
    $("#areaPanel").hidden = state.lighting !== "area";

    $$("#lightingTabs button").forEach((button) => {
      button.classList.toggle("active", button.dataset.lighting === state.lighting);
    });

    $("#keyboardArea").hidden = state.device !== "keyboard";
    $("#logoArea").hidden = state.device !== "logo";

    $$("#areaDevice button").forEach((button) => {
      button.classList.toggle("active", button.dataset.device === state.device);
    });

    const forcedStatic = state.screenSync;
    const effectiveStyle = forcedStatic ? "Static" : state.areaStyle;

    $$("#areaMode button").forEach((button) => {
      button.classList.toggle("active", button.dataset.style === effectiveStyle);
      button.disabled = forcedStatic && button.dataset.style === "Dynamic";
    });

    $("#staticArea").hidden = effectiveStyle !== "Static";
    $("#dynamicArea").hidden = effectiveStyle !== "Dynamic";

    $("#globalEffect").disabled = forcedStatic;
    $("#areaEffect").disabled = forcedStatic;

    const floorCtl = $("#floorCtl");
    if (floorCtl) floorCtl.hidden = !(state.screenSync && state.pulse);

    const beatScreen = $("#beatBtnScreen");
    if (beatScreen) beatScreen.hidden = !(state.screenSync && state.pulse);

    const coloursLocked = state.screenSync;
    $$("#zones input[type=color]").forEach((input) => {
      input.disabled = coloursLocked;
    });
    $("#globalColor").disabled = coloursLocked;
    $("#globalSwatches").classList.toggle("is-locked", coloursLocked);

    const logoLocked =
      (state.screenSync || state.soundSync) && state.follow === "both";
    $("#logoArea").classList.toggle("is-locked", logoLocked);
    $$("#logoArea select, #logoArea input").forEach((input) => {
      input.disabled = logoLocked;
    });
    const logoLock = $("#logoLock");
    if (logoLock) logoLock.hidden = !logoLocked;

    $("#modeKeyText").textContent = MODES[state.mode].name;
    $("#appModeText").textContent = MODES[state.mode].name;
    const dashMode = $("#dashMode");
    if (dashMode) dashMode.textContent = MODES[state.mode].name;

    $$("#dashModes button").forEach((button) => {
      button.classList.toggle("active", Number(button.dataset.mode) === state.mode);
    });

    $$("#workload button").forEach((button) => {
      button.classList.toggle("active", button.dataset.load === state.workload);
    });

    $$("#fanMode button").forEach((button) => {
      button.classList.toggle("active", button.dataset.fan === state.fanMode);
    });

    $("#refreshRate").value = String(state.refresh);
    $("#customFans").hidden = state.fanMode !== "custom";
    $("#cpuFan").value = state.cpuFan;
    $("#cpuFanValue").textContent = `${state.cpuFan}%`;
    $("#gpuFan").value = state.gpuFan;
    $("#gpuFanValue").textContent = `${state.gpuFan}%`;

    const watts = Math.round(
      MODES[state.mode].power * (0.3 + 0.7 * (WORKLOADS[state.workload] || WORKLOADS.office).cpu)
    );
    $("#dashPower").textContent = `${watts} W`;

    $$(".mode-dot").forEach((dot) => {
      dot.style.background = MODES[state.mode].color;
      dot.style.color = MODES[state.mode].color;
    });

    $$("#zones .zone").forEach((zone, index) => {
      zone.style.setProperty("--zone", state.zones[index]);
      zone.querySelector("input").value = state.zones[index];
    });

    ["#globalBrightness", "#globalSpeed", "#logoBrightness", "#logoSpeed",
      "#areaBrightness", "#areaBrightnessDynamic", "#areaSpeed", "#floor",
      "#saturation", "#sensitivity", "#cpuFan", "#gpuFan"].forEach((id) => fillRange($(id)));

    updateScreenBars();
    syncSwatches();
  }

  function syncSwatches() {
    $$("#globalSwatches .swatch").forEach((swatch) => {
      swatch.classList.toggle("active", swatch.dataset.colour === state.global.color);
    });
    $$("#logowatches .swatch").forEach((swatch) => {
      swatch.classList.toggle("active", swatch.dataset.colour === state.logo.color);
    });
  }

  function buildSwatches(container, onPick) {
    if (!container) return;
    container.innerHTML = "";
    PALETTE.forEach((colour) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "swatch";
      button.dataset.colour = colour;
      button.style.background = colour;
      button.title = colour;
      button.setAttribute("aria-label", `Use ${colour}`);
      button.addEventListener("click", () => onPick(colour));
      container.appendChild(button);
    });
  }

  function buildZones() {
    const zones = $("#zones");
    if (!zones) return;
    zones.innerHTML = "";

    state.zones.forEach((colour, index) => {
      const label = document.createElement("label");
      label.className = "zone";
      label.style.setProperty("--zone", colour);
      label.dataset.zone = String(index);

      const input = document.createElement("input");
      input.type = "color";
      input.value = colour;
      input.setAttribute("aria-label", `Zone ${index + 1} colour`);

      const dot = document.createElement("span");
      dot.className = "zone-dot";

      const small = document.createElement("small");
      small.textContent = `Z${index + 1}`;

      label.append(input, dot, small);
      zones.appendChild(label);

      input.addEventListener("input", () => {
        state.zones[index] = input.value;
        label.style.setProperty("--zone", input.value);
        if (state.device === "keyboard" && state.lighting === "area") {
          label.classList.add("active");
        }
      });

      label.addEventListener("click", () => {
        $$("#zones .zone").forEach((other) => other.classList.remove("active"));
        label.classList.add("active");
      });
    });
  }

  function setSync(screen, sound) {
    state.screenSync = screen;
    state.soundSync = sound;

    if (state.screenSync) {
      state.soundSync = false;
    }

    syncControls();
  }

  function setMode(index, withFlash = true) {
    state.mode = ((index % MODES.length) + MODES.length) % MODES.length;

    if (withFlash) {
      flash = { until: performance.now() + 1100, rgb: hexToRgb(MODES[state.mode].color) };
    }

    syncControls();
  }

  function updateBeatButtons() {
    const label = Audio.playing ? "■ Stop demo beat" : "▶ Play demo beat";
    ["#beatBtn", "#beatBtnScreen"].forEach((id) => {
      const button = $(id);
      if (!button) return;
      button.textContent = label;
      button.classList.toggle("playing", Audio.playing);
    });
  }

  async function toggleBeat() {
    if (Audio.playing) {
      Audio.stop();
      updateBeatButtons();
      return;
    }

    const started = await Audio.start();
    if (started) {
      if (!state.soundSync) setSync(false, true);
      updateBeatButtons();
    }
  }

  function applyPreset(name) {
    if (name === "screen") {
      setSync(true, false);
      state.pulse = false;
      state.floor = 0;
      state.saturation = 100;
    } else if (name === "sound") {
      setSync(false, true);
      state.soundMode = "Spectrum";
      Audio.start().then((started) => {
        if (started) updateBeatButtons();
      });
    } else if (name === "hybrid") {
      setSync(true, false);
      state.pulse = true;
      state.floor = 0;
      state.saturation = 140;
      Audio.start().then((started) => {
        if (started) updateBeatButtons();
      });
    } else if (name === "reset") {
      state.lighting = "global";
      state.device = "keyboard";
      state.areaStyle = "Static";
      state.global = { effect: "Static", color: "#b388ff", brightness: 100, speed: 3 };
      state.logo = { effect: "Static", color: "#b388ff", brightness: 100, speed: 3 };
      state.zones = ["#b388ff", "#4f8dff", "#22d3ee", "#ff5c8a"];
      state.areaEffect = "Breathing";
      state.areaSpeed = 4;
      state.areaBrightness = 100;
      state.screenSync = false;
      state.soundSync = false;
      state.pulse = false;
      state.floor = 0;
      state.saturation = 100;
      state.soundMode = "Spectrum";
      state.sensitivity = 4;
      state.follow = "both";
      state.scene = 0;
      state.mode = 2;
      state.workload = "office";
      state.fanMode = "auto";
      state.refresh = 144;
      state.cpuFan = 40;
      state.gpuFan = 40;
      thermal.cpu = 48;
      thermal.gpu = 44;
      thermal.cpuRpm = 1200;
      thermal.gpuRpm = 1100;
      thermal.clock = 0;
      Audio.stop();
      updateBeatButtons();
      buildZones();
      setScene(0);
    }

    syncControls();
    syncSwatches();
  }

  /* --------------------------------- Wire -------------------------------- */

  function init() {
    buildKeyboard();
    buildZones();

    buildSwatches($("#globalSwatches"), (colour) => {
      state.global.color = colour;
      syncControls();
    });

    buildSwatches($("#logowatches"), (colour) => {
      state.logo.color = colour;
      syncControls();
    });

    setScene(state.scene);

    /* Tabs */
    $$(".app-tabs button").forEach((button) => {
      button.addEventListener("click", () => {
        $$(".app-tabs button").forEach((other) => {
          other.classList.remove("active");
          other.setAttribute("aria-selected", "false");
        });
        button.classList.add("active");
        button.setAttribute("aria-selected", "true");

        const page = button.dataset.tab;
        $$(".app-body > section").forEach((section) => {
          section.hidden = section.dataset.page !== page;
        });
      });
    });

    /* Lighting preset */
    $$("#lightingTabs button").forEach((button) => {
      button.addEventListener("click", () => {
        state.lighting = button.dataset.lighting;
        syncControls();
      });
    });

    /* Area device */
    $$("#areaDevice button").forEach((button) => {
      button.addEventListener("click", () => {
        state.device = button.dataset.device;
        syncControls();
      });
    });

    /* Area style */
    $$("#areaMode button").forEach((button) => {
      button.addEventListener("click", () => {
        if (button.disabled) return;
        state.areaStyle = button.dataset.style;
        syncControls();
      });
    });

    /* Global controls */
    $("#globalEffect").addEventListener("change", (event) => {
      state.global.effect = event.target.value;
      syncControls();
    });

    $("#globalColor").addEventListener("input", (event) => {
      state.global.color = event.target.value;
      syncSwatches();
    });

    $("#globalBrightness").addEventListener("input", (event) => {
      state.global.brightness = Number(event.target.value);
      $("#globalBrightnessValue").textContent = `${state.global.brightness}%`;
      fillRange(event.target);
    });

    $("#globalSpeed").addEventListener("input", (event) => {
      state.global.speed = Number(event.target.value);
      $("#globalSpeedValue").textContent = String(state.global.speed);
      fillRange(event.target);
    });

    /* Area controls */
    $("#areaEffect").addEventListener("change", (event) => {
      state.areaEffect = event.target.value;
    });

    $("#areaSpeed").addEventListener("input", (event) => {
      state.areaSpeed = Number(event.target.value);
      $("#areaSpeedValue").textContent = String(state.areaSpeed);
      fillRange(event.target);
    });

    ["#areaBrightness", "#areaBrightnessDynamic"].forEach((id) => {
      const input = $(id);
      if (!input) return;
      input.addEventListener("input", (event) => {
        state.areaBrightness = Number(event.target.value);
        syncControls();
      });
    });

    /* Logo controls */
    $("#logoEffect").addEventListener("change", (event) => {
      state.logo.effect = event.target.value;
    });

    $("#logoColor").addEventListener("input", (event) => {
      state.logo.color = event.target.value;
      syncSwatches();
    });

    $("#logoBrightness").addEventListener("input", (event) => {
      state.logo.brightness = Number(event.target.value);
      $("#logoBrightnessValue").textContent = `${state.logo.brightness}%`;
      fillRange(event.target);
    });

    $("#logoSpeed").addEventListener("input", (event) => {
      state.logo.speed = Number(event.target.value);
      $("#logoSpeedValue").textContent = String(state.logo.speed);
      fillRange(event.target);
    });

    /* Dashboard */
    $$("#dashModes button").forEach((button) => {
      button.addEventListener("click", () => setMode(Number(button.dataset.mode)));
    });

    $$("#workload button").forEach((button) => {
      button.addEventListener("click", () => {
        state.workload = button.dataset.load;
        syncControls();
      });
    });

    $$("#fanMode button").forEach((button) => {
      button.addEventListener("click", () => {
        state.fanMode = button.dataset.fan;
        syncControls();
      });
    });

    $("#refreshRate").addEventListener("change", (event) => {
      state.refresh = Number(event.target.value);
    });

    ["#cpuFan", "#gpuFan"].forEach((id) => {
      const input = $(id);
      if (!input) return;
      input.addEventListener("input", (event) => {
        const value = Number(event.target.value);
        if (id === "#cpuFan") state.cpuFan = value;
        else state.gpuFan = value;

        $(`${id}Value`).textContent = `${value}%`;
        fillRange(event.target);
      });
    });

    /* Sync */
    $("#screenSync").addEventListener("change", (event) => {
      setSync(event.target.checked, false);
    });

    $("#soundSync").addEventListener("change", (event) => {
      setSync(false, event.target.checked);
    });

    $("#pulse").addEventListener("change", (event) => {
      state.pulse = event.target.checked;
      syncControls();
    });

    $("#floor").addEventListener("input", (event) => {
      state.floor = Number(event.target.value);
      $("#floorValue").textContent = `${state.floor}%`;
      fillRange(event.target);
    });

    $("#saturation").addEventListener("input", (event) => {
      state.saturation = Number(event.target.value);
      $("#saturationValue").textContent = `${state.saturation}%`;
      fillRange(event.target);
      barsCache = "";
      updateScreenBars();
    });

    $("#soundMode").addEventListener("change", (event) => {
      state.soundMode = event.target.value;
    });

    $("#sensitivity").addEventListener("input", (event) => {
      state.sensitivity = Number(event.target.value);
      $("#sensitivityValue").textContent = String(state.sensitivity);
      fillRange(event.target);
    });

    $("#follow").addEventListener("change", (event) => {
      state.follow = event.target.value;
      syncControls();
    });

    /* Demo beat */
    ["#beatBtn", "#beatBtnScreen"].forEach((id) => {
      const button = $(id);
      if (button) button.addEventListener("click", toggleBeat);
    });

    /* Presets */
    $$(".sim-quick button").forEach((button) => {
      button.addEventListener("click", () => {
        applyPreset(button.dataset.preset);
        $("#appBody")?.scrollTo?.({ top: 0 });
        if (window.matchMedia("(max-width: 1080px)").matches) {
          $(".sim-stage")?.scrollIntoView({ behavior: "smooth", block: "start" });
        }
      });
    });

    /* Scenes */
    $$("#screenScenes button").forEach((button) => {
      button.addEventListener("click", () => setScene(Number(button.dataset.scene)));
    });

    /* Mode */
    $("#modeKey").addEventListener("click", () => setMode(state.mode + 1));
    $("#appMode").addEventListener("click", () => setMode(state.mode + 1));

    /* Keep the simulation honest: pause the audio when the tab is hidden. */
    document.addEventListener("visibilitychange", () => {
      if (document.hidden && Audio.ctx && Audio.playing) {
        Audio.stop();
        updateBeatButtons();
      }
    });

    setMode(state.mode, false);
    syncControls();
    updateBeatButtons();
    setScene(state.scene);
    lastFrame = performance.now();
    requestAnimationFrame(frame);
  }

  /* ------------------------------- Animation ----------------------------- */

  let lastFrame = performance.now();

  function frame(now) {
    const dt = Math.min(0.05, Math.max(0, (now - lastFrame) / 1000));
    lastFrame = now;
    updateThermal(dt);
    paint(now / 1000, dt);
    requestAnimationFrame(frame);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init, { once: true });
  } else {
    init();
  }
})();
