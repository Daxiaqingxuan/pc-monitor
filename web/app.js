/* ============================================================
   硬件监测 · dashboard logic  (vanilla, no dependencies)
   ============================================================ */
(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  // theme first: the trend-line colours are read from CSS variables
  const THEME_KEY = "hw-monitor-theme";
  const savedTheme = localStorage.getItem(THEME_KEY);
  document.documentElement.dataset.theme =
    savedTheme || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");

  // Language before anything renders; the server value is adopted in
  // loadSettings() so the page and the desktop menu cannot disagree.
  window.I18N.init();

  const HISTORY_MAX = 900;
  const state = {
    info: null,
    samples: [],
    paused: false,
    connected: false,
    intervalMs: 1000,
    windowMs: 300000,
    peaks: { cpu: 0, gpu: 0 },
    lastCost: 0,
    tempSrc: "none",
    elevated: false,
    engineSig: "",
    procRows: [],
    bulk: false,
    settings: null,
    archive: false,
    lastAux: null,
  };

  /* --------------------------------------------------- sensor state --- */
  /* Declared before anything that paints: buildSeries()/buildPopover() run at
     module scope and ask whether a metric is allowed. These are the same
     switches the desktop widget menu writes into settings.json. */
  const SENSOR_KEYS = ["cpu", "cpuTemp", "gpu", "gpuTemp", "mem", "net", "disk",
    "diskTemp", "fans", "board", "proc"];

  // Which switch each chart line depends on.
  const METRIC_SENSOR = {
    usage: {
      cpu: "cpu", gpu: "gpu", vram: "gpu", ram: "mem", clk: "cpu",
      netrx: "net", nettx: "net", diskr: "disk", diskw: "disk", diskbusy: "disk",
    },
    temp: {
      cpu: "cpuTemp", gpu: "gpuTemp", cpupw: "cpuTemp", gpupw: "gpuTemp",
      ssd: "diskTemp", board: "board",
    },
  };

  function defaultSettings() {
    const o = { lang: "zh" };
    for (const k of SENSOR_KEYS) o[k] = true;
    return o;
  }

  function sensorOn(key) {
    const s = state.settings || defaultSettings();
    return s[key] !== false;
  }

  function metricAllowed(kind, key) {
    const need = (METRIC_SENSOR[kind] || {})[key];
    return !need || sensorOn(need);
  }

  /* ------------------------------------------------------- formatting --- */
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const pct = (v) => (v == null ? "—" : `${Math.round(v)}%`);
  const esc = (s) =>
    String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

  /* Every user-visible string goes through these two. i18n.js owns the
     dictionary; nothing here should hard-code Chinese or English. */
  const t = (k, v) => window.I18N.t(k, v);
  const mlabel = (def) => (def.labelKey ? t(def.labelKey) : def.label || "");

  /* The dashboard repaints every second. Writing the same string back into the
     DOM still costs a style/layout pass, so compare first — most values are
     unchanged between two samples. */
  function setText(node, v) {
    if (!node) return;
    const s = String(v);
    if (node.textContent !== s) node.textContent = s;
  }
  function setDash(node, v) {
    if (!node) return;
    const s = String(v);
    if (node.style.strokeDashoffset !== s) node.style.strokeDashoffset = s;
  }

  function fmtTemp(v) {
    if (v == null || Number.isNaN(v)) return "—";
    return `${Math.round(v)}°C`;
  }
  function fmtBytes(b, digits = 1) {
    if (b == null || Number.isNaN(b)) return "—";
    if (b >= 1073741824) return `${(b / 1073741824).toFixed(digits)} GB`;
    if (b >= 1048576) return `${Math.round(b / 1048576)} MB`;
    if (b >= 1024) return `${Math.round(b / 1024)} KB`;
    return `${Math.round(b)} B`;
  }
  function fmtClock(mhz) {
    if (!mhz) return "—";
    return `${(mhz / 1000).toFixed(2)} GHz`;
  }
  function fmtPower(w) {
    if (w == null || Number.isNaN(w)) return "—";
    return `${Math.round(w)} W`;
  }
  function fmtRate(b) {
    if (b == null || Number.isNaN(b)) return "—";
    if (b < 1024) return `${Math.round(b)} B/s`;
    if (b < 1048576) return `${(b / 1024).toFixed(1)} KB/s`;
    if (b < 1073741824) return `${(b / 1048576).toFixed(1)} MB/s`;
    return `${(b / 1073741824).toFixed(2)} GB/s`;
  }
  /* A 7-day window labelled "04:44" is ambiguous; once the window passes a
     day, prefix the date. Under 24h the clock alone is clearer. */
  function axisLabel(ts, span) {
    const hhmm = fmtClockTime(ts).slice(0, 5);
    if (span < 86400000) return hhmm;
    const d = new Date(ts);
    return `${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")} ${hhmm}`;
  }
  function fmtClockTime(ts) {
    const d = new Date(ts);
    return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}:${String(d.getSeconds()).padStart(2, "0")}`;
  }
  function fmtRpm(v) {
    if (v == null || Number.isNaN(v)) return "—";
    return `${Math.round(v)} RPM`;
  }
  /* getComputedStyle forces a style recalc, and a draw reads a dozen custom
     properties. Cache them until the theme switches. */
  let varCache = null;
  function cssVar(name) {
    if (!varCache) varCache = {};
    if (varCache[name] === undefined) {
      varCache[name] = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }
    return varCache[name];
  }
  function clearVarCache() {
    varCache = null;
  }
  /* Median distance between samples. Used to tell a normal cadence from a real
     outage, so the chart can break the line instead of drawing a fake ramp
     between two points that are minutes apart. */
  function typicalStep(rows) {
    const d = [];
    for (let i = 1; i < rows.length && d.length < 120; i++) {
      const dt = rows[i].t - rows[i - 1].t;
      if (dt > 0) d.push(dt);
    }
    if (!d.length) return 1000;
    d.sort((a, b) => a - b);
    return d[d.length >> 1];
  }
  function withAlpha(hex, a) {
    const h = hex.replace("#", "");
    const n = h.length === 3 ? h.split("").map((c) => c + c).join("") : h;
    const r = parseInt(n.slice(0, 2), 16);
    const g = parseInt(n.slice(2, 4), 16);
    const b = parseInt(n.slice(4, 6), 16);
    return `rgba(${r}, ${g}, ${b}, ${a})`;
  }

  /* -------------------------------------------------------- full scales ---
     Every trend line is scaled against its own "full" value so different
     units can share one plot. The axis then reads "how full is this thing"
     and the tooltip carries the real number. */
  function lastSample() {
    return state.samples.length ? state.samples[state.samples.length - 1] : null;
  }
  function vramFull() {
    const info = state.info || {};
    const s = lastSample();
    return info.vramTotal || (s && s.gpu && s.gpu.vramTotal) || 0;
  }
  function ramFull() {
    const info = state.info || {};
    const s = lastSample();
    return info.ramTotal || (s && s.mem && s.mem.total) || 0;
  }
  function clockFull() {
    const base = (state.info && state.info.baseClock) || 3800;
    return Math.max(4000, Math.round(base * 1.4));
  }

  /* Throughput has no rating to divide by, so the "full" value is whatever the
     busiest sample in the visible window was, rounded up to 1/2/5 x 10^n. The
     rounding matters: without it the scale would be re-derived on every frame
     and the line would appear to breathe at idle. */
  function niceCeil(v) {
    if (!(v > 0)) return 0;
    const mag = Math.pow(10, Math.floor(Math.log10(v)));
    const n = v / mag;
    const step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
    return step * mag;
  }
  function peakFull(series, rows) {
    let peak = 0;
    for (const r of rows) {
      const v = series.get(r);
      if (v != null && isFinite(v) && v > peak) peak = v;
    }
    return niceCeil(peak) || 1;
  }

  const METRICS = {
    usage: {
      cpu: {
        labelKey: "load.cpu", colorVar: "--cpu-1",
        get: (r) => (r.cpu ? r.cpu.u : null),
        full: () => 100,
        format: (v) => `${Math.round(v)}%`,
      },
      gpu: {
        labelKey: "load.gpu", colorVar: "--gpu-2",
        get: (r) => (r.gpu ? r.gpu.u : null),
        full: () => 100,
        format: (v) => `${Math.round(v)}%`,
      },
      vram: {
        labelKey: "load.vram", colorVar: "--c-vram",
        get: (r) => (r.gpu ? r.gpu.vramUsed : null),
        full: vramFull,
        format: (v) => fmtBytes(v),
      },
      ram: {
        labelKey: "load.ram", colorVar: "--c-ram",
        get: (r) => (r.mem ? r.mem.used : null),
        full: ramFull,
        format: (v) => fmtBytes(v),
      },
      clk: {
        labelKey: "load.clk", colorVar: "--c-clk",
        get: (r) => (r.cpu ? r.cpu.clk : null),
        full: clockFull,
        format: (v) => fmtClock(v),
      },
      // Throughput lines have no meaningful fixed ceiling - a quiet desktop
      // does 20 KB/s and a download does 90 MB/s. `peak` tells the chart to
      // scale each of them to the busiest value in the window instead of
      // pinning them to the floor with a 300 MB/s full scale.
      netrx: {
        labelKey: "load.netrx", colorVar: "--c-netrx", peak: true,
        get: (r) => (r.net ? r.net.rx : null),
        full: () => 1,
        format: fmtRate,
      },
      nettx: {
        labelKey: "load.nettx", colorVar: "--c-nettx", peak: true,
        get: (r) => (r.net ? r.net.tx : null),
        full: () => 1,
        format: fmtRate,
      },
      diskr: {
        labelKey: "load.diskr", colorVar: "--c-diskr", peak: true,
        get: (r) => (r.disk ? r.disk.r : null),
        full: () => 1,
        format: fmtRate,
      },
      diskw: {
        labelKey: "load.diskw", colorVar: "--c-diskw", peak: true,
        get: (r) => (r.disk ? r.disk.w : null),
        full: () => 1,
        format: fmtRate,
      },
      diskbusy: {
        labelKey: "load.diskbusy", colorVar: "--c-diskbusy",
        get: (r) => (r.disk ? r.disk.busy : null),
        full: () => 100,
        format: (v) => `${Math.round(v)}%`,
      },
    },
    temp: {
      cpu: {
        labelKey: "temp.cpu", colorVar: "--cpu-1", temp: true,
        get: (r) => (r.cpu ? r.cpu.temp : null),
        full: () => 100,
        format: fmtTemp,
      },
      gpu: {
        labelKey: "temp.gpu", colorVar: "--gpu-2", temp: true,
        get: (r) => (r.gpu ? r.gpu.temp : null),
        full: () => 100,
        format: fmtTemp,
      },
      cpupw: {
        labelKey: "temp.cpupw", colorVar: "--c-cpupw",
        get: (r) => (r.cpu ? r.cpu.power : null),
        full: () => 120,
        format: fmtPower,
      },
      gpupw: {
        labelKey: "temp.gpupw", colorVar: "--c-gpupw",
        get: (r) => (r.gpu ? r.gpu.power : null),
        full: () => 450,
        format: fmtPower,
      },
      ssd: {
        labelKey: "temp.ssd", colorVar: "--c-ssd", temp: true,
        get: (r) => (r.aux ? r.aux.ssd : null),
        full: () => 100,
        format: fmtTemp,
      },
      board: {
        labelKey: "temp.board", colorVar: "--c-board", temp: true,
        get: (r) => (r.aux ? r.aux.board : null),
        full: () => 100,
        format: fmtTemp,
      },
    },
  };

  /* ------------------------------------------------------ line chart ---- */
  class LineChart {
    constructor(canvas, tipEl, options) {
      this.canvas = canvas;
      this.ctx = canvas.getContext("2d");
      this.tip = tipEl;
      this.options = options;
      this.autoY = !!options.autoY;
      this.series = [];
      this.hoverX = null;
      this.geom = null;
      this.windowMs = options.windowMs || 300000;

      const ro = new ResizeObserver(() => this.draw());
      ro.observe(canvas.parentElement);
      canvas.addEventListener("pointermove", (e) => {
        const r = canvas.getBoundingClientRect();
        this.hoverX = e.clientX - r.left;
        // a mouse can fire 100+ moves a second; one repaint per frame is plenty
        if (this.queued) return;
        this.queued = true;
        requestAnimationFrame(() => {
          this.queued = false;
          this.draw();
        });
      });
      canvas.addEventListener("pointerleave", () => {
        this.hoverX = null;
        this.tip.style.opacity = "0";
        this.draw();
      });
    }

    setSeries(series) {
      this.series = series || [];
      this.draw();
    }

    draw() {
      const { canvas, ctx, options } = this;
      const rect = canvas.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      if (canvas.width !== Math.round(rect.width * dpr) || canvas.height !== Math.round(rect.height * dpr)) {
        canvas.width = Math.round(rect.width * dpr);
        canvas.height = Math.round(rect.height * dpr);
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, rect.width, rect.height);

      const padL = 30, padR = 6, padT = 6, padB = 18;
      const plot = {
        x: padL, y: padT,
        w: Math.max(10, rect.width - padL - padR),
        h: Math.max(10, rect.height - padT - padB),
      };
      this.geom = { plot, t0: 0, t1: 0 };

      const lineCol = cssVar("--line");
      const lineSoft = cssVar("--line-soft");
      const muted = cssVar("--muted");

      const rows = state.samples;
      const t1 = rows.length ? rows[rows.length - 1].t : Date.now();
      this.windowMs = state.windowMs;
      const t0 = t1 - this.windowMs;
      this.geom.t0 = t0;
      this.geom.t1 = t1;

      // auto Y axis: only when every visible series is a temperature line,
      // otherwise the mixed units (W vs °C) would make the scale meaningless
      const autoOn = !!this.autoY && this.series.length > 0
        && this.series.every((s) => s.temp);
      let yMin = 0;
      let yMax = options.yMax || 100;
      let ticks = options.ticks || [0, 25, 50, 75, 100];
      if (autoOn) {
        let lo = Infinity, hi = -Infinity;
        for (const r of rows) {
          if (r.t < t0 - 2000) continue;
          for (const s of this.series) {
            const v = s.get(r);
            if (v != null && isFinite(v)) {
              if (v < lo) lo = v;
              if (v > hi) hi = v;
            }
          }
        }
        if (isFinite(lo) && isFinite(hi)) {
          const span = Math.max(hi - lo, 12);
          const pad = Math.max(3, span * 0.15);
          yMin = Math.max(0, Math.floor((lo - pad) / 5) * 5);
          yMax = Math.ceil((hi + pad) / 5) * 5;
          if (yMax - yMin < 20) yMax = yMin + 20;
          const step = (yMax - yMin) / 4;
          ticks = [0, 1, 2, 3, 4].map((i) => Math.round(yMin + i * step));
        }
      }

      // grid ------------------------------------------------------------
      ctx.font = "10px ui-sans-serif, system-ui, sans-serif";
      ctx.textAlign = "right";
      ctx.textBaseline = "middle";
      ctx.setLineDash([2, 4]);
      ctx.lineWidth = 1;
      for (const tick of ticks) {
        const y = plot.y + plot.h - ((tick - yMin) / (yMax - yMin)) * plot.h;
        ctx.strokeStyle = tick === yMin ? lineSoft : lineCol;
        ctx.beginPath();
        ctx.moveTo(plot.x, y);
        ctx.lineTo(plot.x + plot.w, y);
        ctx.stroke();
        if (autoOn || tick % 50 === 0) {
          ctx.fillStyle = muted;
          ctx.fillText(String(tick), plot.x - 6, y);
        }
      }
      ctx.setLineDash([]);

      const xs = (t) => plot.x + ((t - t0) / (t1 - t0)) * plot.w;
      const ys = (v, full) => plot.y + plot.h
        - (autoOn
          ? clamp((v - yMin) / (yMax - yMin), 0, 1)
          : clamp(v / (full || 100), 0, 1)) * plot.h;

      // x labels --------------------------------------------------------
      ctx.textBaseline = "bottom";
      ctx.fillStyle = muted;
      const labelCount = plot.w >= 380 ? 4 : 2;
      for (let i = 0; i < labelCount; i++) {
        const f = i / (labelCount - 1);
        const last = i === labelCount - 1;
        ctx.textAlign = i === 0 ? "left" : last ? "right" : "center";
        ctx.fillText(
          last ? t("misc.now") : axisLabel(t0 + f * (t1 - t0), t1 - t0),
          plot.x + f * plot.w,
          rect.height - 2
        );
      }

      const series = this.series;
      const inWindow = rows.filter((r) => r.t >= t0 - 2000);
      const fulls = series.map((s) => {
        if (s.peak) return peakFull(s, inWindow);
        const f = typeof s.full === "function" ? s.full() : s.full;
        return f && isFinite(f) && f > 0 ? f : 100;
      });

      // series ----------------------------------------------------------
      if (inWindow.length < 2 || !series.length) {
        this.tip.style.opacity = "0";
        return;
      }

      const withArea = series.length <= 2;
      const ends = [];
      // Break the line across outages (panel restart, tab asleep): joining the
      // two ends would paint a straight ramp that never happened.
      const gapMs = Math.max(3000, typicalStep(inWindow) * 3);
      series.forEach((s, si) => {
        const color = cssVar(s.colorVar);
        const full = fulls[si];
        const segs = [];
        let cur = [];
        let lastT = null;
        for (const r of inWindow) {
          const v = s.get(r);
          if (v == null || Number.isNaN(v)) {
            if (cur.length) segs.push(cur);
            cur = [];
            lastT = null;
            continue;
          }
          if (lastT != null && r.t - lastT > gapMs) {
            if (cur.length) segs.push(cur);
            cur = [];
          }
          cur.push([xs(r.t), ys(v, full)]);
          lastT = r.t;
        }
        if (cur.length) segs.push(cur);

        for (const seg of segs) {
          if (!seg.length) continue;
          if (withArea) {
            const grad = ctx.createLinearGradient(0, plot.y, 0, plot.y + plot.h);
            grad.addColorStop(0, withAlpha(color, 0.22));
            grad.addColorStop(1, withAlpha(color, 0.01));
            ctx.beginPath();
            ctx.moveTo(seg[0][0], plot.y + plot.h);
            for (const [x, y] of seg) ctx.lineTo(x, y);
            ctx.lineTo(seg[seg.length - 1][0], plot.y + plot.h);
            ctx.closePath();
            ctx.fillStyle = grad;
            ctx.fill();
          }

          ctx.beginPath();
          seg.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
          ctx.strokeStyle = color;
          ctx.lineWidth = 1.9;
          ctx.lineJoin = "round";
          ctx.lineCap = "round";
          ctx.stroke();
        }
        const lastSeg = segs[segs.length - 1];
        if (lastSeg && lastSeg.length) ends[si] = lastSeg[lastSeg.length - 1];
      });

      // endpoint marker: the head of each line is where "now" is, and a bare
      // line end is easy to lose when several series overlap
      for (let si = 0; si < ends.length; si++) {
        const pt = ends[si];
        if (!pt) continue;
        const color = cssVar(series[si].colorVar);
        ctx.beginPath();
        ctx.arc(pt[0], pt[1], 5.6, 0, Math.PI * 2);
        ctx.fillStyle = withAlpha(color, 0.16);
        ctx.fill();
        ctx.beginPath();
        ctx.arc(pt[0], pt[1], 2.7, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();
      }

      // hover -----------------------------------------------------------
      if (this.hoverX == null || this.hoverX < plot.x || this.hoverX > plot.x + plot.w) {
        this.tip.style.opacity = "0";
        return;
      }
      const tAt = t0 + ((this.hoverX - plot.x) / plot.w) * (t1 - t0);
      // inWindow is time-ordered, so bisect instead of scanning it on every move
      let lo = 0, hi = inWindow.length - 1;
      while (lo < hi) {
        const mid = (lo + hi) >> 1;
        if (inWindow[mid].t < tAt) lo = mid + 1;
        else hi = mid;
      }
      const after = inWindow[lo];
      const before = lo > 0 ? inWindow[lo - 1] : null;
      const best =
        before && Math.abs(before.t - tAt) <= Math.abs(after.t - tAt) ? before : after;
      if (!best) { this.tip.style.opacity = "0"; return; }

      const hx = xs(best.t);
      ctx.beginPath();
      ctx.moveTo(hx, plot.y);
      ctx.lineTo(hx, plot.y + plot.h);
      ctx.strokeStyle = cssVar("--line-strong") || lineCol;
      ctx.lineWidth = 1;
      ctx.setLineDash([3, 3]);
      ctx.stroke();
      ctx.setLineDash([]);

      let tipHtml = `<u>${fmtClockTime(best.t)}</u>`;
      series.forEach((s, si) => {
        const v = s.get(best);
        const color = cssVar(s.colorVar);
        if (v != null) {
          ctx.beginPath();
          ctx.arc(hx, ys(v, fulls[si]), 3.2, 0, Math.PI * 2);
          ctx.fillStyle = cssVar("--panel");
          ctx.fill();
          ctx.lineWidth = 2;
          ctx.strokeStyle = color;
          ctx.stroke();
        }
        tipHtml += `<br><b style="color:${color}">■</b> ${s.label} ${
          v == null ? "—" : s.format(v)
        }`;
      });
      this.tip.innerHTML = tipHtml;
      this.tip.style.opacity = "1";
      const tipW = this.tip.offsetWidth || 120;
      const left = clamp(hx, tipW / 2 + 4, rect.width - tipW / 2 - 4);
      this.tip.style.left = `${left}px`;
      this.tip.style.top = `${plot.y + 4}px`;
    }
  }

  /* ------------------------------------------------------- dom refs ----- */
  const el = {
    hostLine: $("hostLine"),
    cpuName: $("cpuName"), gpuName: $("gpuName"),
    cpuUsage: $("cpuUsage"), gpuUsage: $("gpuUsage"),
    cpuRing: $("cpuRing"), gpuRing: $("gpuRing"),
    cpuTemp: $("cpuTemp"), gpuTemp: $("gpuTemp"),
    cpuPower: $("cpuPower"), gpuPower: $("gpuPower"),
    cpuTempChip: $("cpuTempChip"), gpuTempChip: $("gpuTempChip"),
    cpuClk: $("cpuClk"), vram: $("vram"),
    gpuHot: $("gpuHot"), cpuCores: $("cpuCores"),
    cpuPeak: $("cpuPeak"), gpuPeak: $("gpuPeak"),
    coreGrid: $("coreGrid"), coreMeta: $("coreMeta"),
    engineList: $("engineList"), vramMeta: $("vramMeta"),
    ramPct: $("ramPct"), ramFill: $("ramFill"),
    ramUsed: $("ramUsed"), ramTotal: $("ramTotal"), ramFree: $("ramFree"),
    netRx: $("netRx"), netTx: $("netTx"),
    diskRead: $("diskRead"), diskWrite: $("diskWrite"), diskBusy: $("diskBusy"),
    fanSys: $("fanSys"), fanCpu: $("fanCpu"),
    fanSysWrap: $("fanSysWrap"), fanCpuWrap: $("fanCpuWrap"),
    fanGrid: $("fanGrid"), fanMeta: $("fanMeta"), fanCard: $("fanCard"),
    boardTemp: $("boardTemp"), ssdTemp: $("ssdTemp"),
    alertBtn: $("alertBtn"), alertPop: $("alertPop"),
    procList: $("procList"), procMeta: $("procMeta"),
    procSearch: $("procSearch"), procSort: $("procSort"), procCount: $("procCount"),
    sumDay: $("sumDay"), sumCpuMax: $("sumCpuMax"), sumGpuMax: $("sumGpuMax"),
    sumLoad: $("sumLoad"), sumBusy: $("sumBusy"), sumPower: $("sumPower"), sumRows: $("sumRows"),
    live: $("live"), liveText: $("liveText"),
    tempEmpty: $("tempEmpty"),
    probeState: $("probeState"),
    footInfo: $("footInfo"), footCost: $("footCost"),
    pauseBtn: $("pauseBtn"), pauseLabel: $("pauseLabel"),
    themeBtn: $("themeBtn"), exportBtn: $("exportBtn"),
    bigBtn: $("bigBtn"), bigExit: $("bigExit"),
    usageTip: $("usageTip"), tempTip: $("tempTip"),
    usageLegend: $("usageLegend"), tempLegend: $("tempLegend"),
    usageAdd: $("usageAdd"), tempAdd: $("tempAdd"),
    usageSave: $("usageSave"), tempSave: $("tempSave"),
    tempFit: $("tempFit"),
    usagePop: $("usagePop"), tempPop: $("tempPop"),
    // sensor switches and language
    hero: $("hero"), cpuCard: $("cpuCard"), gpuCard: $("gpuCard"),
    sysCard: $("sysCard"), procCard: $("procCard"), usageCard: $("usageCard"), tempCard: $("tempCard"),
    settingsBtn: $("settingsBtn"), settingsPop: $("settingsPop"), settingsClose: $("settingsClose"),
    langSeg: $("langSeg"), sensorGrid: $("sensorGrid"),
    cpuTempRow: $("cpuTempRow"), gpuTempRow: $("gpuTempRow"), gpuHotRow: $("gpuHotRow"),
    ramBlock: $("ramBlock"), netRow: $("netRow"), diskRow: $("diskRow"), fanRow: $("fanRow"),
    tempRow: $("tempRow"), boardWrap: $("boardWrap"), ssdWrap: $("ssdWrap"),
    lanWarn: $("lanWarn"),
  };

  const usageChart = new LineChart($("usageChart"), el.usageTip, { yMax: 100 });
  const tempChart = new LineChart($("tempChart"), el.tempTip, {
    yMax: 100,
    autoY: (() => {
      try { return localStorage.getItem("hw-temp-fit") === "1"; } catch { return false; }
    })(),
  });

  /* A sample, a resize and a theme switch can all request a repaint in the same
     frame; coalesce them into one. */
  let drawPending = false;
  function drawCharts() {
    if (drawPending) return;
    drawPending = true;
    requestAnimationFrame(() => {
      drawPending = false;
      usageChart.draw();
      tempChart.draw();
    });
  }

  /* ------------------------------------------------- metric selection --- */
  const SERIES_KEY = "hw-monitor-series";
  const seriesCfg = (() => {
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem(SERIES_KEY) || "{}"); } catch (e) { saved = {}; }
    const out = {};
    for (const kind of ["usage", "temp"]) {
      const valid = Object.keys(METRICS[kind]);
      let list = Array.isArray(saved[kind]) ? saved[kind].filter((k) => valid.includes(k)) : [];
      if (!list.length) list = ["cpu", "gpu"];
      out[kind] = list;
    }
    return out;
  })();

  function saveSeries() {
    try { localStorage.setItem(SERIES_KEY, JSON.stringify(seriesCfg)); } catch (e) { /* ignore */ }
  }

  function buildSeries(kind) {
    return seriesCfg[kind].filter((key) => metricAllowed(kind, key)).map((key) => {
      const def = METRICS[kind][key];
      return {
        key,
        label: mlabel(def),
        colorVar: def.colorVar,
        get: def.get,
        full: def.full,
        peak: !!def.peak,
        format: def.format,
      };
    });
  }

  const legendBox = { usage: el.usageLegend, temp: el.tempLegend };

  function renderLegend(kind) {
    const box = legendBox[kind];
    if (!box) return;
    box.innerHTML = "";
    for (const key of seriesCfg[kind]) {
      if (!metricAllowed(kind, key)) continue;
      const def = METRICS[kind][key];
      const b = document.createElement("button");
      b.type = "button";
      b.className = "lg";
      b.title = t("chart.hide", { name: mlabel(def) });
      b.innerHTML = `<i style="background:${cssVar(def.colorVar)}"></i>${esc(mlabel(def))}`;
      b.addEventListener("click", () => toggleMetric(kind, key, false));
      box.appendChild(b);
    }
  }

  function toggleMetric(kind, key, on) {
    const list = seriesCfg[kind];
    const has = list.includes(key);
    if (on && !has) {
      list.push(key);
      // keep the primary pair first so colours stay stable
      list.sort((a, b) => Object.keys(METRICS[kind]).indexOf(a) - Object.keys(METRICS[kind]).indexOf(b));
    } else if (!on && has) {
      if (list.length === 1) { syncPopover(kind); return; } // never leave a chart empty
      list.splice(list.indexOf(key), 1);
    } else {
      return;
    }
    saveSeries();
    applySeries();
  }

  function buildPopover(kind) {
    const pop = kind === "usage" ? el.usagePop : el.tempPop;
    const defs = METRICS[kind];
    pop.innerHTML =
      `<div class="mp-head">${t("chart.pick")}</div>` +
      Object.keys(defs)
        .filter((key) => metricAllowed(kind, key))
        .map(
          (key) => `<label class="mp-row"><input type="checkbox" data-metric="${key}">
            <i style="background:${cssVar(defs[key].colorVar)}"></i>
            <span>${esc(mlabel(defs[key]))}</span></label>`
        )
        .join("") +
      `<p class="mp-note">${esc(t("chart.note"))}</p>`;

    // Assigned, not added: applySettings() rebuilds this popover on every
    // sensor or language change and would otherwise stack listeners.
    pop.onchange = (e) => {
      const input = e.target.closest("input[data-metric]");
      if (!input) return;
      toggleMetric(kind, input.dataset.metric, input.checked);
    };
  }

  function syncPopover(kind) {
    const pop = kind === "usage" ? el.usagePop : el.tempPop;
    pop.querySelectorAll("input[data-metric]").forEach((input) => {
      input.checked = seriesCfg[kind].includes(input.dataset.metric);
    });
  }

  function applySeries() {
    usageChart.setSeries(buildSeries("usage"));
    tempChart.setSeries(buildSeries("temp"));
    renderLegend("usage");
    renderLegend("temp");
    syncPopover("usage");
    syncPopover("temp");
    syncFitBtn(); // power lines disable the adaptive-Y toggle
  }

  buildPopover("usage");
  buildPopover("temp");
  applySeries();

  function wirePicker(kind, btn, pop) {
    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      const open = pop.hidden;
      el.usagePop.hidden = true;
      el.tempPop.hidden = true;
      pop.hidden = !open;
      btn.setAttribute("aria-expanded", String(open));
      if (open) syncPopover(kind);
    });
    btn.addEventListener("pointerdown", (e) => e.stopPropagation());
    pop.addEventListener("pointerdown", (e) => e.stopPropagation());
    pop.addEventListener("click", (e) => e.stopPropagation());
  }
  wirePicker("usage", el.usageAdd, el.usagePop);
  wirePicker("temp", el.tempAdd, el.tempPop);
  document.addEventListener("pointerdown", () => {
    el.usagePop.hidden = true;
    el.tempPop.hidden = true;
    el.usageAdd.setAttribute("aria-expanded", "false");
    el.tempAdd.setAttribute("aria-expanded", "false");
  });

  /* -------------------------------------------------- export as PNG ----- */
  function exportChart(chart, baseName) {
    const src = chart.canvas;
    if (!src.width || !src.height) return;
    const out = document.createElement("canvas");
    out.width = src.width;
    out.height = src.height;
    const c = out.getContext("2d");
    c.fillStyle = cssVar("--panel").trim() || "#ffffff";
    c.fillRect(0, 0, out.width, out.height);
    c.drawImage(src, 0, 0);
    const stamp = new Date();
    const p = (n) => String(n).padStart(2, "0");
    const name = `${baseName}_${stamp.getFullYear()}-${p(stamp.getMonth() + 1)}-${p(stamp.getDate())}_${p(stamp.getHours())}-${p(stamp.getMinutes())}.png`;
    out.toBlob((blob) => {
      if (!blob) return;
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 4000);
    }, "image/png");
  }
  el.usageSave.addEventListener("click", () => exportChart(usageChart, t("chart.usage")));
  el.tempSave.addEventListener("click", () => exportChart(tempChart, t("chart.temp")));

  /* adaptive Y axis toggle (temperature chart) */
  function syncFitBtn() {
    const on = !!tempChart.autoY;
    el.tempFit.classList.toggle("on", on);
    el.tempFit.setAttribute("aria-pressed", String(on));
    // mixed units (power lines) make the scale meaningless - reflect it in the UI
    const mixed = tempChart.series.length > 0 && !tempChart.series.every((s) => s.temp);
    el.tempFit.disabled = mixed;
    el.tempFit.title = mixed
      ? t("misc.mixed")
      : on ? t("chart.fitOn") : t("chart.fit");
  }
  el.tempFit.addEventListener("click", () => {
    tempChart.autoY = !tempChart.autoY;
    try { localStorage.setItem("hw-temp-fit", tempChart.autoY ? "1" : "0"); } catch { /* ignore */ }
    syncFitBtn();
    drawCharts();
  });
  syncFitBtn();

  /* ---------------------------------------------------------- render ---- */
  let coreBars = [];
  let engineRows = new Map();

  function buildCores(n) {
    el.coreGrid.innerHTML = "";
    coreBars = [];
    for (let i = 0; i < n; i++) {
      const d = document.createElement("div");
      d.className = "core";
      d.title = t("misc.core", { n: i });
      const s = document.createElement("span");
      d.appendChild(s);
      el.coreGrid.appendChild(d);
      coreBars.push({ wrap: d, bar: s });
    }
  }

  // "@" marks a dictionary key; anything else is a literal engine name
  // (3D, Copy, Compute 1 …) that reads the same in both languages.
  const ENGINE_LABELS = {
    "3D": "3D",
    "Copy": "Copy",
    "Compute 0": "Compute",
    "Compute 1": "Compute 1",
    "Video Decode": "@engine.video",
    "Video Codec Engine": "@engine.codec",
    "Video JPEG 0": "JPEG",
    "Security 1": "Security",
    "Timer 0": "Timer",
    "Other": "@misc.other",
  };
  function engineLabel(k) {
    const raw = ENGINE_LABELS[k];
    if (!raw) return k;
    return raw.charAt(0) === "@" ? t(raw.slice(1)) : raw;
  }
  const ENGINE_ORDER = ["3D", "Compute 0", "Compute 1", "Copy", "Video Codec Engine", "Video Decode", "Video JPEG 0"];

  function buildEngines(eng) {
    const keys = Object.keys(eng || {});
    const shown = ENGINE_ORDER.filter((k) => keys.includes(k));
    for (const k of keys) if (!shown.includes(k) && eng[k] > 0) shown.push(k);
    const list = shown.slice(0, 4);
    const signature = list.join("|");
    if (signature === state.engineSig) return;
    state.engineSig = signature;
    el.engineList.innerHTML = "";
    engineRows.clear();
    if (!list.length) {
      el.engineList.innerHTML = `<div class="sub-meta">${esc(t("engine.idle"))}</div>`;
      return;
    }
    for (const k of list) {
      const row = document.createElement("div");
      row.className = "eng";
      row.innerHTML = `<span class="eng-name">${esc(engineLabel(k))}</span>
        <span class="eng-track"><i></i></span>
        <span class="eng-val">0%</span>`;
      el.engineList.appendChild(row);
      engineRows.set(k, { fill: row.querySelector("i"), val: row.querySelector(".eng-val") });
    }
  }

  function renderTempChip(chip, temp, nullLabel) {
    chip.classList.remove("warm", "hot");
    if (temp != null) {
      if (temp >= 85) chip.classList.add("hot");
      else if (temp >= 70) chip.classList.add("warm");
    }
    const label = temp == null ? (nullLabel || "—") : `${Math.round(temp)}°C`;
    if (chip.textContent !== label) chip.innerHTML = `<span>${label}</span>`;
  }

  /* Numbers that both live samples and replayed history rows can paint. */
  function paintMeters(s) {
    const cpu = s.cpu || {}, gpu = s.gpu || {}, mem = s.mem || {};

    setText(el.cpuUsage, Math.round(cpu.u || 0));
    setDash(el.cpuRing, 100 - clamp(cpu.u || 0, 0, 100));
    setText(el.cpuTemp, fmtTemp(cpu.temp));
    el.cpuTemp.classList.toggle("hot", cpu.temp != null && cpu.temp >= 85);
    el.cpuTemp.title = cpu.temp == null && !s.elevated && !s.hist ? t("probe.needAdmin") : "";
    setText(el.cpuPower, fmtPower(cpu.power));
    setText(el.cpuClk, fmtClock(cpu.clk));
    if (!s.hist) setText(el.cpuPeak, pct(state.peaks.cpu));
    if (cpu.cores && cpu.cores.length) {
      const cpuCores = (state.info && state.info.cores) || "—";
      setText(el.cpuCores, `${cpuCores} / ${cpu.cores.length}`);
    }
    renderTempChip(el.cpuTempChip, cpu.temp, s.hist || s.elevated ? "—" : t("misc.needAdmin"));

    setText(el.gpuUsage, Math.round(gpu.u || 0));
    setDash(el.gpuRing, 100 - clamp(gpu.u || 0, 0, 100));
    setText(el.gpuTemp, fmtTemp(gpu.temp));
    el.gpuTemp.classList.toggle("hot", gpu.temp != null && gpu.temp >= 85);
    setText(el.gpuPower, fmtPower(gpu.power));
    setText(el.gpuHot, fmtTemp(gpu.hotspot));
    el.gpuHot.classList.toggle("hot", gpu.hotspot != null && gpu.hotspot >= 95);
    if (!s.hist) setText(el.gpuPeak, pct(state.peaks.gpu));
    renderTempChip(el.gpuTempChip, gpu.temp, "—");

    const vramTotal = gpu.vramTotal || (state.info && state.info.vramTotal) || 0;
    if (vramTotal) {
      setText(el.vram, `${fmtBytes(gpu.vramUsed)} / ${Math.round(vramTotal / 1073741824)} GB`);
      setText(el.vramMeta, t("misc.vramPct", { v: (((gpu.vramUsed || 0) / vramTotal) * 100).toFixed(1) }));
    } else {
      setText(el.vram, fmtBytes(gpu.vramUsed));
      setText(el.vramMeta, `${t("load.vram")} —`);
    }

    const ramTotal = mem.total || 0;
    const ramUsed = mem.used || 0;
    const ramPct = ramTotal ? (ramUsed / ramTotal) * 100 : 0;
    const width = `${clamp(ramPct, 0, 100)}%`;
    if (el.ramFill.style.width !== width) el.ramFill.style.width = width;
    setText(el.ramPct, ramTotal ? t("sys.usedPct", { v: ramPct.toFixed(1) }) : "—");
    setText(el.ramUsed, fmtBytes(ramUsed));
    setText(el.ramTotal, fmtBytes(ramTotal, 0));
    setText(el.ramFree, fmtBytes(Math.max(ramTotal - ramUsed, 0)));
  }

  function render(s) {
    if (!s) return;
    // rows replayed from disk only carry the trend fields
    if (s.hist) {
      if (state.bulk) return;
      paintMeters(s);
      drawCharts();
      return;
    }
    state.lastCost = s.cost || 0;
    state.tempSrc = s.tempSrc || "none";
    state.elevated = !!s.elevated;

    // peaks
    state.peaks.cpu = Math.max(state.peaks.cpu, s.cpu.u || 0);
    state.peaks.gpu = Math.max(state.peaks.gpu, s.gpu.u || 0);

    paintMeters(s);

    // per-core load
    const cores = s.cpu.cores || [];
    if (coreBars.length !== cores.length) buildCores(cores.length);
    cores.forEach((v, i) => {
      const b = coreBars[i];
      if (!b) return;
      const h = `${clamp(v, 1, 100)}%`;
      if (b.bar.style.height !== h) b.bar.style.height = h;
      b.wrap.classList.toggle("hot", v >= 85);
      b.wrap.title = `${t("misc.core", { n: i })} · ${Math.round(v)}%`;
    });
    setText(el.coreMeta, t("misc.threadsPeak", { n: cores.length, p: Math.max(...cores, 0) }));

    // GPU engines
    buildEngines(s.gpu.eng);
    for (const [k, node] of engineRows) {
      const v = clamp((s.gpu.eng || {})[k] || 0, 0, 100);
      node.fill.style.width = `${v}%`;
      setText(node.val, `${Math.round(v)}%`);
    }

    // network / disk
    const net = s.net || {};
    const disk = s.disk || {};
    setText(el.netRx, fmtRate(net.rx));
    setText(el.netTx, fmtRate(net.tx));
    setText(el.diskRead, fmtRate(disk.r));
    setText(el.diskWrite, fmtRate(disk.w));
    setText(el.diskBusy, disk.busy == null ? "" : `${t("sys.active")} ${Math.round(disk.busy)}%`);

    // fans / board / ssd
    const aux = s.aux || {};
    state.lastAux = aux;
    setText(el.fanSys, fmtRpm(aux.fanSys));
    el.fanSysWrap.hidden = aux.fanSys == null;
    setText(el.fanCpu, fmtRpm(aux.fanCpu));
    el.fanCpuWrap.hidden = aux.fanCpu == null;
    setText(el.boardTemp, fmtTemp(aux.board));
    setText(el.ssdTemp, fmtTemp(aux.ssd));
    el.boardTemp.classList.toggle("hot", aux.board != null && aux.board >= 60);
    el.ssdTemp.classList.toggle("hot", aux.ssd != null && aux.ssd >= 70);
    renderFans(aux.fans);

    // processes
    state.procRows = s.top || [];
    renderProcs();

    // temp overlay
    const noTemp = s.cpu.temp == null && s.gpu.temp == null;
    el.tempEmpty.hidden = !noTemp;
    updateProbeState();

    setText(el.footCost, t("misc.cost", { cost: s.cost, n: state.samples.length, t: fmtClockTime(s.t) }));
    evaluateAlerts(s);
    drawCharts();
  }

  function updateProbeState() {
    const src = state.tempSrc;
    const map = {
      lhm: "probe.connected",
      "lhm-lite": "probe.basic",
      "lhm-gpu": "probe.gpuOnly",
      "nvidia-smi": "probe.nv",
      none: "probe.none",
    };
    const admin = state.elevated ? t("probe.admin") : t("probe.user");
    el.probeState.textContent = t("probe.line", { src: map[src] ? t(map[src]) : src, admin });
  }

  /* ------------------------------------------------------- process list -- */
  const PROC_KEY = "hw-monitor-procs";
  const procCfg = (() => {
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem(PROC_KEY) || "{}"); } catch (e) { saved = {}; }
    return {
      sort: saved.sort === "mem" ? "mem" : "cpu",
      count: [6, 10, 20].includes(saved.count) ? saved.count : 6,
      q: "",
    };
  })();

  function saveProcCfg() {
    try { localStorage.setItem(PROC_KEY, JSON.stringify({ sort: procCfg.sort, count: procCfg.count })); } catch (e) { /* ignore */ }
  }

  let procNodes = [];
  let procEmpty = false;

  function makeProcRow() {
    const li = document.createElement("li");
    li.className = "proc";
    const name = document.createElement("span");
    name.className = "proc-name";
    const bar = document.createElement("em");
    const label = document.createElement("span");
    name.appendChild(bar);
    name.appendChild(label);
    const cpu = document.createElement("span");
    cpu.className = "proc-cpu";
    const mem = document.createElement("span");
    mem.className = "proc-mem";
    li.appendChild(name);
    li.appendChild(cpu);
    li.appendChild(mem);
    return { li, bar, label, cpu, mem };
  }

  /* ---------------------------------------------------------- fans ------- */
  /* Rebuild the tiles only when the header list changes (a different board, or
     the widget coming back with more sensors); per-second updates just move the
     bar and the number. Seven tiles are cheap, but replacing them every second
     would restart the CSS width transition and the bars would never settle. */
  let fanSig = "";
  function renderFans(fans) {
    const grid = el.fanGrid;
    if (!grid) return;
    const list = Array.isArray(fans) ? fans : [];
    if (!list.length) {
      // A history row carries no aux block. Only show the empty state when we
      // have genuinely never had fan data, otherwise a mid-session reload
      // would blank a card that is about to be filled one tick later.
      if (!grid.querySelector(".fan-item")) {
        fanSig = "";
        grid.innerHTML = `<div class="fan-empty">${esc(t("fan.none"))}</div>`;
      }
      setText(el.fanMeta, "—");
      return;
    }
    const peak = Math.max(...list.map((f) => f.v), 0);
    // A 300 RPM floor keeps a single slow header from rendering as a full bar.
    const scale = Math.max(peak, 300);
    const sig = list.map((f) => f.n).join("|");
    if (sig !== fanSig) {
      fanSig = sig;
      grid.innerHTML = list
        .map(
          (f) => `<div class="fan-item">
            <div class="fan-name" title="${esc(f.n)}">${esc(f.n)}</div>
            <div class="fan-rpm"><span data-v>—</span><em>RPM</em></div>
            <div class="fan-track"><span data-bar></span></div>
          </div>`
        )
        .join("");
    }
    const items = grid.children;
    for (let i = 0; i < list.length && i < items.length; i++) {
      const item = items[i];
      const v = list[i].v;
      setText(item.querySelector("[data-v]"), Math.round(v));
      const bar = item.querySelector("[data-bar]");
      if (bar) bar.style.width = `${Math.min(100, (v / scale) * 100)}%`;
      item.classList.toggle("idle", !(v > 0));
    }
    const spinning = list.filter((f) => f.v > 0).length;
    setText(el.fanMeta, t("fan.meta", { n: spinning, m: list.length, peak: Math.round(peak) }));
  }

  function renderProcs() {
    if (!el.procList) return;
    const rows = state.procRows || [];
    const q = procCfg.q.trim().toLowerCase();
    let list = q ? rows.filter((p) => String(p.n).toLowerCase().includes(q)) : rows.slice();
    list.sort((a, b) => (procCfg.sort === "mem" ? b.m - a.m : b.u - a.u));
    list = list.slice(0, procCfg.count);
    setText(el.procMeta, t("chart.shown", { n: list.length, m: rows.length }));

    if (!list.length) {
      if (!procEmpty) {
        el.procList.innerHTML = "";
        const li = document.createElement("li");
        li.className = "proc-empty";
        li.textContent = q ? t("proc.empty") : t("proc.loading");
        el.procList.appendChild(li);
        procNodes = [];
        procEmpty = true;
      }
      return;
    }
    if (procEmpty || procNodes.length > list.length) {
      el.procList.innerHTML = "";
      procNodes = [];
      procEmpty = false;
    }
    while (procNodes.length < list.length) {
      const node = makeProcRow();
      el.procList.appendChild(node.li);
      procNodes.push(node);
    }

    // the busiest row gets the full bar, so the left edge reads as a ranking
    const scale = Math.max(25, Number(list[0].u) || 0);
    list.forEach((p, i) => {
      const node = procNodes[i];
      setText(node.label, p.n);
      setText(node.cpu, `${Number(p.u || 0).toFixed(1)}%`);
      setText(node.mem, fmtBytes(p.m));
      const load = clamp(Number(p.u) || 0, 0, 100);
      const h = `${Math.max(3, Math.round((Math.min(load, scale) / scale) * 14))}px`;
      if (node.bar.style.height !== h) node.bar.style.height = h;
      node.bar.classList.toggle("hot", load >= 50);
    });
  }

  el.procSearch.addEventListener("input", () => {
    procCfg.q = el.procSearch.value;
    renderProcs();
  });
  el.procSort.addEventListener("click", (e) => {
    const btn = e.target.closest("button");
    if (!btn) return;
    procCfg.sort = btn.dataset.sort;
    saveProcCfg();
    el.procSort.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b === btn));
    renderProcs();
  });
  el.procCount.addEventListener("click", (e) => {
    const btn = e.target.closest("button");
    if (!btn) return;
    procCfg.count = Number(btn.dataset.n);
    saveProcCfg();
    el.procCount.querySelectorAll("button").forEach((b) => b.classList.toggle("on", b === btn));
    renderProcs();
  });

  function syncProcUI() {
    el.procSort.querySelectorAll("button").forEach((b) => {
      b.classList.toggle("on", b.dataset.sort === procCfg.sort);
    });
    el.procCount.querySelectorAll("button").forEach((b) => {
      b.classList.toggle("on", Number(b.dataset.n) === procCfg.count);
    });
  }
  syncProcUI();

  /* ---------------------------------------------------------- summary --- */
  function setStat(node, value, note) {
    if (!node) return;
    node.innerHTML = note ? `${esc(value)}<em>${esc(note)}</em>` : esc(value);
  }

  function atTime(ms) {
    if (!ms) return "";
    const d = new Date(ms);
    return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
  }

  function renderSummary(d) {
    if (!d) return;
    el.sumDay.textContent = d.day ? d.day.slice(5) : "—";
    const cpuT = d.cpuTemp || {}, gpuT = d.gpuTemp || {};
    setStat(el.sumCpuMax, cpuT.max == null ? "—" : fmtTemp(cpuT.max), atTime(cpuT.at));
    setStat(el.sumGpuMax, gpuT.max == null ? "—" : fmtTemp(gpuT.max), atTime(gpuT.at));
    const cl = (d.cpuLoad || {}).max, gl = (d.gpuLoad || {}).max;
    setStat(
      el.sumLoad,
      cl == null && gl == null ? "—" : `CPU ${Math.round(cl || 0)}% · GPU ${Math.round(gl || 0)}%`
    );
    setStat(el.sumBusy, d.busyMin == null ? "—" : `${d.busyMin} ${t("misc.min")}`,
      d.busyRunMin ? t("misc.peakLong", { v: d.busyRunMin }) : "");
    setStat(
      el.sumPower,
      d.avgCpuW == null && d.avgGpuW == null ? "—" : `CPU ${Math.round(d.avgCpuW || 0)}W · GPU ${Math.round(d.avgGpuW || 0)}W`
    );
    const mb = d.bytes ? ` · ${(d.bytes / 1048576).toFixed(1)} MB` : "";
    setStat(el.sumRows, `${d.samples || 0} ${t("misc.rows")}${mb}`);
  }

  async function loadSummary() {
    if (document.hidden) return; // refreshed again when the tab comes back
    try {
      const res = await fetch("/api/summary", { cache: "no-store" });
      if (!res.ok) return;
      renderSummary(await res.json());
    } catch (e) { /* offline */ }
  }

  /* ------------------------------------------------------- info panel --- */
  function applyInfo(info) {
    if (!info) return;
    state.info = info;
    el.hostLine.textContent = `${info.host} · ${info.osName} · ${t("misc.threads", {
      n: info.logical,
    })} / ${Math.round(info.ramTotal / 1073741824)} GB ${t("load.ram")}`;
    el.cpuName.textContent = info.cpuName;
    el.gpuName.textContent = info.gpuName;
    el.footInfo.textContent = `${info.cpuName} · ${info.gpuName}`;
    // LAN mode has no password; say so instead of leaving it implicit.
    el.lanWarn.hidden = !info.lan;
    if (!coreBars.length && info.logical) buildCores(info.logical);
  }

  /* ------------------------------------------------------ connection ---- */
  function setLive(kind, text) {
    el.live.classList.remove("ok", "bad", "paused");
    el.live.classList.add(kind);
    el.liveText.textContent = text;
  }

  function pushSample(s) {
    const last = state.samples[state.samples.length - 1];
    if (last && s.t <= last.t) return; // ignore duplicates after a reconnect
    if (state.archive) {
      // Live ticks keep extending the right edge of a 1h…7d view; the timer
      // below re-reads the archive every couple of minutes so the buckets
      // themselves stay fresh.
      state.samples.push(s);
      if (state.samples.length > ARCHIVE_CAP) state.samples.shift();
      if (!state.paused && !document.hidden) render(s);
      return;
    }
    state.samples.push(s);
    if (state.samples.length > HISTORY_MAX) state.samples.shift();
    // a hidden tab keeps collecting but does not need to repaint
    if (!state.paused && !document.hidden) render(s);
  }

  /* --- polling fallback -------------------------------------------------
     A local proxy, a sleeping machine or a restarted server can all kill the
     SSE stream. EventSource retries on its own, but if it stays broken the
     charts would stay empty forever, so we poll /api/snapshot meanwhile. */
  let pollTimer = null;
  let historyPulled = false;

  /* Replaying 900 rows used to repaint both canvases once per row. Load them
     with painting suppressed, then paint the last one. */
  function absorbHistory(hist) {
    if (!hist.length) return;
    state.bulk = true;
    try {
      for (const s of hist) pushSample(s);
    } finally {
      state.bulk = false;
    }
    render(hist[hist.length - 1]);
  }

  async function tryPullHistory() {
    if (historyPulled || state.samples.length) return;
    try {
      const res = await fetch("/api/history", { cache: "no-store" });
      if (!res.ok) return;
      const d = await res.json();
      if (d.info) applyInfo(d.info);
      const hist = (d.samples || []).slice(-HISTORY_MAX);
      historyPulled = true;
      absorbHistory(hist);
    } catch { /* keep trying next tick */ }
  }

  async function pollOnce() {
    try {
      const res = await fetch("/api/snapshot", { cache: "no-store" });
      if (!res.ok) throw new Error(String(res.status));
      const d = await res.json();
      if (d.info) applyInfo(d.info);
      if (d.sample) {
        pushSample(d.sample);
        state.connected = true;
        if (!state.paused) setLive("ok", t("live.poll"));
      } else if (!state.paused) {
        setLive("bad", t("live.starting"));
      }
    } catch {
      state.connected = false;
      if (!state.paused) setLive("bad", t("live.disconnected"));
    }
  }

  function startPolling() {
    if (pollTimer) return;
    tryPullHistory();
    pollOnce();
    pollTimer = setInterval(pollOnce, Math.max(800, Number(state.intervalMs) || 1000));
  }

  function stopPolling() {
    if (!pollTimer) return;
    clearInterval(pollTimer);
    pollTimer = null;
  }

  function connect() {
    const src = new EventSource("/api/stream");
    src.onopen = () => {
      state.connected = true;
      stopPolling();
      setLive("ok", t("live.ok"));
    };
    src.onmessage = (ev) => {
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.type === "hello") {
        state.intervalMs = Number(msg.interval) || 1000;
        syncIntervalUI();
        if (msg.collector === "offline") setLive("bad", t("live.offline"));
      } else if (msg.type === "info") {
        applyInfo(msg.info);
      } else if (msg.type === "sample") {
        pushSample(msg);
        stopPolling();
        if (state.connected) setLive("ok", t("live.ok"));
      } else if (msg.type === "status") {
        if (msg.collector === "offline") setLive("bad", t("live.restart"));
      }
    };
    src.onerror = () => {
      state.connected = false;
      if (!state.paused) setLive("bad", t("live.retry"));
      startPolling();
      // EventSource retries on its own; recreate if permanently closed
      if (src.readyState === EventSource.CLOSED) setTimeout(connect, 4000);
    };
  }

  async function bootstrap() {
    // Switches and language first: they decide what may be painted at all.
    await loadSettings();
    if (!state.archive) {
      try {
        const res = await fetch("/api/history");
        const data = await res.json();
        applyInfo(data.info);
        // the server keeps the last 15 minutes; load all of it so the 15m window
        // is populated the moment the page opens
        absorbHistory((data.samples || []).slice(-HISTORY_MAX));
      } catch (e) {
        /* stream will fill in */
      }
    }
    connect();
    loadSummary();
  }

  /* ---------------------------------------------------------- alerts ----- */
  const ALERT_KEY = "hw-monitor-alerts";
  let savedAlerts = {};
  try { savedAlerts = JSON.parse(localStorage.getItem(ALERT_KEY) || "{}"); } catch (e) { savedAlerts = {}; }
  const alertCfg = Object.assign(
    { enabled: true, notify: false, cpuTemp: 85, gpuTemp: 90, cpuLoad: 95, gpuLoad: 95 },
    savedAlerts
  );
  const alertInputs = {
    cpuTemp: $("thCpuTemp"), gpuTemp: $("thGpuTemp"),
    cpuLoad: $("thCpuLoad"), gpuLoad: $("thGpuLoad"),
  };
  const alertEnabledBox = $("alertEnabled");
  const alertNotifyBox = $("alertNotify");

  function syncAlertUI() {
    alertEnabledBox.checked = !!alertCfg.enabled;
    alertNotifyBox.checked = !!alertCfg.notify;
    for (const k in alertInputs) alertInputs[k].value = alertCfg[k];
  }
  function saveAlertCfg() {
    localStorage.setItem(ALERT_KEY, JSON.stringify(alertCfg));
  }

  alertEnabledBox.addEventListener("change", () => {
    alertCfg.enabled = alertEnabledBox.checked;
    saveAlertCfg();
    if (!alertCfg.enabled) {
      alertSig = "";
      clearAlertVisuals();
    }
  });
  alertNotifyBox.addEventListener("change", () => {
    alertCfg.notify = alertNotifyBox.checked;
    saveAlertCfg();
    if (alertCfg.notify && "Notification" in window && Notification.permission === "default") {
      Notification.requestPermission();
    }
  });
  for (const k in alertInputs) {
    alertInputs[k].addEventListener("change", () => {
      const v = Number(alertInputs[k].value);
      if (Number.isFinite(v) && v > 0) {
        alertCfg[k] = v;
        saveAlertCfg();
      }
    });
  }
  syncAlertUI();

  function toggleAlertPop(open) {
    el.alertPop.hidden = !open;
    el.alertBtn.setAttribute("aria-expanded", String(open));
  }
  el.alertBtn.addEventListener("click", (e) => {
    e.stopPropagation();
    toggleAlertPop(el.alertPop.hidden);
  });
  $("alertClose").addEventListener("click", () => toggleAlertPop(false));
  document.addEventListener("pointerdown", (e) => {
    if (el.alertPop.hidden) return;
    if (!el.alertPop.contains(e.target) && !el.alertBtn.contains(e.target)) toggleAlertPop(false);
  });

  let BASE_TITLE = document.title;
  const lastNotify = {};
  let titleTimer = null;
  let titleCaption = "";
  let alertSig = "";

  function flashTitle(caption) {
    titleCaption = caption;
    if (titleTimer) return;
    let on = false;
    titleTimer = setInterval(() => {
      on = !on;
      document.title = (on ? "⚠ " : "") + titleCaption;
    }, 1000);
  }

  function clearAlertVisuals() {
    if (titleTimer) {
      clearInterval(titleTimer);
      titleTimer = null;
    }
    document.title = BASE_TITLE;
    document.querySelectorAll(".card.alert, .temp-chip.alert").forEach((n) => n.classList.remove("alert"));
  }

  function notifyHits(hits) {
    if (!alertCfg.notify) return;
    if (!("Notification" in window) || Notification.permission !== "granted") return;
    const now = Date.now();
    for (const h of hits) {
      if (now - (lastNotify[h.key] || 0) < 90000) continue;
      lastNotify[h.key] = now;
      try {
        new Notification(t("alert.titleFlash"), { body: t("misc.alarm", { text: h.text }), tag: h.key });
      } catch (err) { /* notification can fail silently */ }
    }
  }

  function evaluateAlerts(s) {
    const hits = [];
    if (alertCfg.enabled) {
      if (s.cpu.temp != null && s.cpu.temp >= alertCfg.cpuTemp)
        hits.push({ key: "cpuTemp", side: "cpu", text: `CPU ${Math.round(s.cpu.temp)}°C` });
      if (s.gpu.temp != null && s.gpu.temp >= alertCfg.gpuTemp)
        hits.push({ key: "gpuTemp", side: "gpu", text: `GPU ${Math.round(s.gpu.temp)}°C` });
      if ((s.cpu.u || 0) >= alertCfg.cpuLoad)
        hits.push({ key: "cpuLoad", side: "cpu", text: `${t("load.cpu")} ${Math.round(s.cpu.u)}%` });
      if ((s.gpu.u || 0) >= alertCfg.gpuLoad)
        hits.push({ key: "gpuLoad", side: "gpu", text: `${t("load.gpu")} ${Math.round(s.gpu.u)}%` });
    }

    const sig = hits.map((h) => h.key).join("|");
    if (sig !== alertSig) {
      alertSig = sig;
      el.cpuUsage.closest(".card").classList.toggle("alert", hits.some((h) => h.side === "cpu"));
      el.gpuUsage.closest(".card").classList.toggle("alert", hits.some((h) => h.side === "gpu"));
      el.cpuTempChip.classList.toggle("alert", hits.some((h) => h.key === "cpuTemp"));
      el.gpuTempChip.classList.toggle("alert", hits.some((h) => h.key === "gpuTemp"));
      if (hits.length) flashTitle(hits.map((h) => h.text).join(" · "));
      else clearAlertVisuals();
    }
    if (hits.length) notifyHits(hits);
  }

  /* --------------------------------------------------------- sensors ---- */
  const sensorInputs = new Map();
  document.querySelectorAll("[data-sensor]").forEach((input) => {
    sensorInputs.set(input.dataset.sensor, input);
    input.addEventListener("change", () => postSetting(input.dataset.sensor, input.checked));
  });

  function show(node, on) {
    if (node) node.hidden = !on;
  }

  function applySettings(next) {
    if (next) state.settings = Object.assign(defaultSettings(), next);
    sensorInputs.forEach((input, key) => { input.checked = sensorOn(key); });

    const cpu = sensorOn("cpu"), gpu = sensorOn("gpu");
    show(el.cpuCard, cpu);
    show(el.gpuCard, gpu);
    // One column when only one meter is left, so it does not stretch to 50%
    el.hero.classList.toggle("single", cpu !== gpu);
    show(el.cpuTempRow, sensorOn("cpuTemp"));
    show(el.cpuTempChip, sensorOn("cpuTemp"));
    show(el.gpuTempRow, sensorOn("gpuTemp"));
    show(el.gpuHotRow, sensorOn("gpuTemp"));
    show(el.gpuTempChip, sensorOn("gpuTemp"));
    show(el.ramBlock, sensorOn("mem"));
    show(el.netRow, sensorOn("net"));
    show(el.diskRow, sensorOn("disk"));
    show(el.fanRow, sensorOn("fans"));
    show(el.fanCard, sensorOn("fans"));
    show(el.boardWrap, sensorOn("board"));
    show(el.ssdWrap, sensorOn("diskTemp"));
    show(el.tempRow, sensorOn("board") || sensorOn("diskTemp"));
    show(el.procCard, sensorOn("proc"));

    // Rebuild the pickers so they only offer what can actually be measured.
    buildPopover("usage");
    buildPopover("temp");
    applySeries();
    drawCharts();
  }

  async function postSetting(key, value) {
    try {
      const res = await fetch("/api/settings", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ key, value: String(value) }),
      });
      if (!res.ok) throw new Error(String(res.status));
      const d = await res.json();
      if (d.settings) applySettings(d.settings);
    } catch (e) {
      applySettings(state.settings); // put the checkbox back
    }
  }

  async function loadSettings() {
    try {
      const res = await fetch("/api/settings", { cache: "no-store" });
      if (!res.ok) return;
      const d = await res.json();
      applySettings(d);
      if (d.lang && d.lang !== window.I18N.lang) window.I18N.setLang(d.lang);
      syncLangUI();
    } catch (e) { /* keep the defaults: everything on */ }
  }

  function syncLangUI() {
    el.langSeg.querySelectorAll("button").forEach((b) => {
      b.classList.toggle("on", b.dataset.lang === window.I18N.lang);
    });
  }

  el.langSeg.addEventListener("click", (e) => {
    const btn = e.target.closest("button");
    if (!btn) return;
    window.I18N.setLang(btn.dataset.lang); // fires langchange, re-renders below
    syncLangUI();
    postSetting("lang", btn.dataset.lang);
  });

  function toggleSettingsPop(open) {
    el.settingsPop.hidden = !open;
    el.settingsBtn.setAttribute("aria-expanded", String(open));
  }
  el.settingsBtn.addEventListener("click", (e) => {
    e.stopPropagation();
    toggleSettingsPop(el.settingsPop.hidden);
  });
  el.settingsClose.addEventListener("click", () => toggleSettingsPop(false));
  // Deep link: /#sensors opens the switch panel (handy to hand someone the
  // page with the switches already in view).
  if (location.hash === "#sensors") toggleSettingsPop(true);
  document.addEventListener("pointerdown", (e) => {
    if (el.settingsPop.hidden) return;
    if (el.settingsPop.contains(e.target) || el.settingsBtn.contains(e.target)) return;
    toggleSettingsPop(false);
  });

  /* Everything built in JS has to be relabelled by hand after a language
     switch: legends, metric pickers, the probe line, process rows. */
  document.addEventListener("langchange", () => {
    BASE_TITLE = document.title;
    buildPopover("usage");
    buildPopover("temp");
    applySeries();
    syncLangUI();
    procEmpty = false;
    renderProcs();
    if (state.info) applyInfo(state.info);
    updateProbeState();
    if (state.samples.length) render(state.samples[state.samples.length - 1]);
    loadSummary();
  });

  /* ------------------------------------------------------ chart ranges ---- */
  const RANGE_KEY = "hw-monitor-range";
  // Anything at or above an hour is read back from the ndjson archive; the
  // in-memory ring only covers ~75 minutes, so it cannot serve these.
  const ARCHIVE = { 3600000: "1h", 21600000: "6h", 86400000: "24h", 604800000: "7d" };
  const ARCHIVE_CAP = 1200;
  let archiveAt = 0;

  async function loadArchive(ms) {
    const range = ARCHIVE[ms];
    if (!range) return;
    try {
      const res = await fetch(`/api/history?range=${range}`, { cache: "no-store" });
      if (!res.ok) return;
      const d = await res.json();
      if (d.info) applyInfo(d.info);
      const rows = d.samples || [];
      state.archive = true;
      state.samples = rows.slice(-ARCHIVE_CAP);
      historyPulled = true;
      archiveAt = Date.now();
      if (rows.length) render(rows[rows.length - 1]);
      else drawCharts();
    } catch (e) { /* leave the chart as it was */ }
  }

  function setRange(ms, persist) {
    state.windowMs = ms;
    if (persist !== false) localStorage.setItem(RANGE_KEY, String(ms));
    document.querySelectorAll("[data-range] button").forEach((b) => {
      b.classList.toggle("on", Number(b.dataset.ms) === ms);
    });
    if (ARCHIVE[ms]) {
      // Flag it synchronously: bootstrap() must not race this request with a
      // plain /api/history pull and overwrite the archive with the ring.
      state.archive = true;
      loadArchive(ms);
      return;
    }
    if (state.archive) {
      // Back to the live ring: the archive array is a different resolution and
      // cannot be spliced, so drop it and pull the ring again.
      state.archive = false;
      state.samples = [];
      historyPulled = false;
      tryPullHistory();
      return;
    }
    drawCharts();
  }
  document.querySelectorAll("[data-range]").forEach((seg) => {
    seg.addEventListener("click", (e) => {
      const btn = e.target.closest("button");
      if (btn) setRange(Number(btn.dataset.ms));
    });
  });
  setRange(Number(localStorage.getItem(RANGE_KEY)) || 300000, false);

  /* --------------------------------------------------------- controls --- */
  function syncIntervalUI() {
    document.querySelectorAll("#intervalSeg button").forEach((b) => {
      b.classList.toggle("on", Number(b.dataset.ms) === state.intervalMs);
    });
  }

  $("intervalSeg").addEventListener("click", async (e) => {
    const btn = e.target.closest("button");
    if (!btn) return;
    const ms = Number(btn.dataset.ms);
    state.intervalMs = ms;
    syncIntervalUI();
    try {
      await fetch("/api/config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ intervalMs: ms }),
      });
    } catch (err) { /* ignore */ }
  });

  function togglePause() {
    state.paused = !state.paused;
    el.pauseBtn.classList.toggle("on", state.paused);
    el.pauseLabel.textContent = state.paused ? t("btn.resume") : t("btn.pause");
    if (state.paused) setLive("paused", t("live.paused"));
    else {
      setLive("ok", t("live.ok"));
      if (state.samples.length) render(state.samples[state.samples.length - 1]);
    }
  }
  el.pauseBtn.addEventListener("click", togglePause);

  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem(THEME_KEY, theme);
    clearVarCache();
    applySeries();
    drawCharts();
  }
  function toggleTheme() {
    applyTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark");
  }
  el.themeBtn.addEventListener("click", toggleTheme);

  /* --------------------------------------------------------- big screen -- */
  function redrawSoon() {
    // the layout has to settle before the canvas gets its new size
    setTimeout(drawCharts, 140);
  }

  function setBigscreen(on) {
    const body = document.body;
    if (on === body.classList.contains("bigscreen")) return;
    body.classList.toggle("bigscreen", on);
    el.bigBtn.setAttribute("aria-pressed", String(on));
    el.bigBtn.classList.toggle("on", on);
    if (on) {
      const root = document.documentElement;
      if (root.requestFullscreen && !document.fullscreenElement) {
        root.requestFullscreen().catch(() => { /* fall back to a normal window */ });
      }
    } else if (document.fullscreenElement && document.exitFullscreen) {
      document.exitFullscreen().catch(() => { /* already out */ });
    }
    redrawSoon();
  }

  el.bigBtn.addEventListener("click", () => setBigscreen(!document.body.classList.contains("bigscreen")));
  el.bigExit.addEventListener("click", () => setBigscreen(false));
  document.addEventListener("fullscreenchange", () => {
    if (!document.fullscreenElement && document.body.classList.contains("bigscreen")) {
      document.body.classList.remove("bigscreen");
      el.bigBtn.setAttribute("aria-pressed", "false");
      el.bigBtn.classList.remove("on");
      redrawSoon();
    }
  });

  function typing(e) {
    const t = e.target;
    if (!t) return false;
    const tag = (t.tagName || "").toLowerCase();
    return tag === "input" || tag === "textarea" || tag === "select" || tag === "button" || t.isContentEditable;
  }

  document.addEventListener("keydown", (e) => {
    if (typing(e)) return;
    if (e.key === "Escape" && document.body.classList.contains("bigscreen")) {
      setBigscreen(false);
    } else if ((e.key === "f" || e.key === "F") && !e.ctrlKey && !e.metaKey && !e.altKey) {
      setBigscreen(!document.body.classList.contains("bigscreen"));
    } else if ((e.key === "t" || e.key === "T") && !e.ctrlKey && !e.metaKey && !e.altKey) {
      toggleTheme();
    } else if (e.code === "Space") {
      e.preventDefault();
      togglePause();
    }
  });

  el.exportBtn.addEventListener("click", () => {
    const rows = [[
      "time", "cpu_pct", "cpu_temp_c", "cpu_power_w", "cpu_clock_mhz",
      "gpu_pct", "gpu_temp_c", "gpu_hotspot_c", "gpu_power_w",
      "vram_used_gb", "ram_used_gb", "net_rx_bps", "net_tx_bps",
      "disk_read_bps", "disk_write_bps", "fan_rpm", "board_c", "ssd_c",
    ]];
    for (const s of state.samples) {
      if (s.hist) continue;
      const aux = s.aux || {};
      rows.push([
        new Date(s.t).toISOString(),
        s.cpu.u, s.cpu.temp ?? "", s.cpu.power ?? "", s.cpu.clk ?? "",
        s.gpu.u, s.gpu.temp ?? "", s.gpu.hotspot ?? "", s.gpu.power ?? "",
        (s.gpu.vramUsed / 1073741824).toFixed(2),
        (s.mem.used / 1073741824).toFixed(2),
        (s.net || {}).rx ?? "", (s.net || {}).tx ?? "",
        (s.disk || {}).r ?? "", (s.disk || {}).w ?? "",
        aux.fanSys ?? "", aux.board ?? "", aux.ssd ?? "",
      ]);
    }
    const csv = rows.map((r) => r.join(",")).join("\n");
    const blob = new Blob(["\ufeff" + csv], { type: "text/csv;charset=utf-8" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `hw-monitor-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  });

  $("howBtn")?.addEventListener("click", () => {
    const note = document.getElementById("noteCard");
    note.open = true;
    note.scrollIntoView({ behavior: "smooth", block: "center" });
  });

  /* ------------------------------------------------------------ boot ---- */
  window.addEventListener("resize", drawCharts);

  // catch up in one paint after the tab was in the background
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) return;
    drawPending = false; // a frozen tab may have dropped the queued frame
    if (state.samples.length) render(state.samples[state.samples.length - 1]);
    loadSummary();
  });

  // the x axis is anchored to the newest sample, so redrawing on a timer while
  // no samples arrive would paint the exact same picture: no timer needed.

  // today's summary refreshes once a minute; it only changes slowly
  setInterval(loadSummary, 60000);

  // While a 1h…7d window is selected, re-read the archive every couple of
  // minutes: live ticks only extend the right edge, the buckets go stale.
  setInterval(() => {
    if (state.archive && Date.now() - archiveAt > 100000) loadArchive(state.windowMs);
  }, 60000);

  // PWA install support (localhost / HTTPS only; silently skipped elsewhere)
  if ("serviceWorker" in navigator && location.protocol === "http:") {
    navigator.serviceWorker.register("sw.js").catch(() => { /* not installable here */ });
  }

  bootstrap();
})();
