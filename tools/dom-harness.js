/* Minimal DOM stub: runs web/app.js in Node to catch runtime errors that a
   syntax check cannot see (typos in element refs, missing globals, ...).
   Not a browser - only the code paths that run at load time are exercised. */
const fs = require("fs");
const vm = require("vm");

const COLORS = {
  "--cpu-1": "#4f6ef7", "--cpu-2": "#7b5cf5", "--gpu-1": "#f7a03c", "--gpu-2": "#f0653f",
  "--line": "#e6e9ef", "--line-soft": "#eef1f5", "--line-strong": "#cbd5e1",
  "--muted": "#8b95a7", "--panel": "#ffffff", "--track": "#eef1f6",
  "--c-vram": "#0d9488", "--c-ram": "#16a34a", "--c-clk": "#9333ea",
  "--c-cpupw": "#d97706", "--c-gpupw": "#dc2626", "--c-ssd": "#0284c7", "--c-board": "#db2777",
};

let paints = 0;
let rafCalls = 0;

function makeCtx() {
  const noop = () => {};
  return new Proxy({}, {
    get(t, k) {
      if (k === "createLinearGradient") return () => ({ addColorStop: noop });
      if (k === "measureText") return () => ({ width: 10 });
      if (k === "clearRect") return () => { paints++; };
      if (k === "canvas") return {};
      if (typeof k === "string") return t[k] !== undefined ? t[k] : noop;
      return noop;
    },
    set(t, k, v) { t[k] = v; return true; },
  });
}

function makeEl(tag = "div") {
  const el = {
    tagName: tag.toUpperCase(),
    style: {},
    dataset: {},
    hidden: false,
    textContent: "",
    innerHTML: "",
    value: "",
    checked: false,
    offsetWidth: 0,
    children: [],
    classList: {
      _s: new Set(),
      add(...c) { c.forEach((x) => this._s.add(x)); },
      remove(...c) { c.forEach((x) => this._s.delete(x)); },
      toggle(c, on) { if (on === undefined) { this._s.has(c) ? this._s.delete(c) : this._s.add(c); } else if (on) this._s.add(c); else this._s.delete(c); },
      contains(c) { return this._s.has(c); },
    },
    addEventListener() {},
    removeEventListener() {},
    appendChild(c) { this.children.push(c); return c; },
    querySelector() { return makeEl(); },
    querySelectorAll() { return []; },
    closest() { return makeEl(); },
    setAttribute() {},
    getAttribute() { return null; },
    getContext() { return makeCtx(); },
    // a real canvas has a size; without it draw() bails out at the first line
    // and none of the drawing code is exercised
    getBoundingClientRect() {
      return this.tagName === "CANVAS"
        ? { width: 640, height: 196, left: 0, top: 0, right: 640, bottom: 196 }
        : { width: 0, height: 0, left: 0, top: 0, right: 0, bottom: 0 };
    },
    scrollIntoView() {},
    click() {},
    focus() {},
  };
  return el;
}

const cache = new Map();
const document = {
  title: "硬件监测 · CPU / GPU",
  documentElement: Object.assign(makeEl("html"), { dataset: { theme: "light" } }),
  body: makeEl("body"),
  fullscreenElement: null,
  getElementById(id) {
    if (!cache.has(id)) {
      const tag = /Chart$/.test(id) ? "canvas" : "div";
      cache.set(id, Object.assign(makeEl(tag), { id }));
    }
    return cache.get(id);
  },
  querySelector() { return makeEl(); },
  querySelectorAll() { return []; },
  createElement(tag) { return makeEl(tag); },
  addEventListener() {},
};

const store = new Map();
/* HW_SERIES=all preloads the saved metric selection so the io metrics actually
   reach the chart. They are off by default, which would leave the peak-scaling
   code path untested - and that is the newest, least exercised branch. */
if (process.env.HW_SERIES === "all") {
  store.set("hw-monitor-series", JSON.stringify({
    usage: ["cpu", "netrx", "nettx", "diskr", "diskw", "diskbusy"],
    temp: ["cpu"],
  }));
}
const localStorage = {
  getItem: (k) => (store.has(k) ? store.get(k) : null),
  setItem: (k, v) => store.set(k, String(v)),
  removeItem: (k) => store.delete(k),
};

const window = {
  devicePixelRatio: 1,
  matchMedia: () => ({ matches: false, addEventListener() {} }),
  addEventListener() {},
  Notification: undefined,
};

class EventSource {
  constructor() { this.readyState = 0; this.onopen = null; this.onmessage = null; this.onerror = null; }
  close() {}
}
EventSource.CLOSED = 2;

class ResizeObserver {
  constructor(cb) { this.cb = cb; }
  observe() {}
  unobserve() {}
  disconnect() {}
}

const INFO = {
  host: "TEST-PC", osName: "Windows 11", logical: 12, cores: 6,
  cpuName: "AMD Ryzen 5 7600", gpuName: "AMD Radeon RX 7900 XTX",
  ramTotal: 32 * 1073741824, vramTotal: 24560 * 1048576, baseClock: 3800,
};

const HIST = {
  type: "sample", hist: true, t: Date.now() - 60000,
  cpu: { u: 12, cores: [], clk: 3600, temp: 48, power: 35 },
  gpu: { u: 4, temp: 45, hotspot: 51, power: 30, vramUsed: 2 * 1073741824, vramTotal: 24560 * 1048576, eng: {} },
  mem: { used: 12 * 1073741824, total: 32 * 1073741824 },
  net: {}, disk: {}, aux: {}, top: [], tempSrc: "hist", elevated: false, cost: 0,
};

const LIVE = {
  type: "sample", t: Date.now(),
  cpu: { u: 42, cores: Array.from({ length: 12 }, (_, i) => 10 + i * 5), clk: 4850, temp: 62, power: 58 },
  gpu: {
    u: 77, temp: 66, hotspot: 78, power: 240, vramUsed: 9 * 1073741824,
    vramTotal: 24560 * 1048576, eng: { "3D": 74, Copy: 2, "Compute 0": 1 },
  },
  mem: { used: 15 * 1073741824, total: 32 * 1073741824 },
  net: { rx: 1200000, tx: 90000 }, disk: { r: 3000000, w: 800000, busy: 12 },
  aux: {
    board: 35.5, ssd: 36, fanCpu: null, fanSys: 673,
    fans: [
      { n: "Fan #1", v: 673 }, { n: "Fan #2", v: 1042 }, { n: "Fan #3", v: 0 },
      { n: "Fan #4", v: 812 }, { n: "Fan #5", v: 0 }, { n: "Fan #6", v: 1197 },
      { n: "Fan #7", v: 0 },
    ],
  },
  top: [
    { n: "chrome", u: 12.5, m: 800 * 1048576 },
    { n: "MsMpEng", u: 3.2, m: 300 * 1048576 },
  ],
  tempSrc: "lhm", elevated: true, cost: 2,
};

/* HW_HISTORY=500 replays a full 15-minute backlog so the cold-start cost of the
   history import can be counted instead of guessed. */
const HISTORY_SAMPLES = (() => {
  const n = Number(process.env.HW_HISTORY || 0);
  if (!n) return [HIST, LIVE];
  const now = Date.now();
  const rows = [];
  for (let i = n; i >= 1; i--) {
    rows.push(Object.assign({}, HIST, {
      t: now - i * 2000,
      cpu: Object.assign({}, HIST.cpu, { u: 10 + (i % 40) }),
    }));
  }
  return rows.concat([LIVE]);
})();

const sandbox = {
  document,
  window,
  localStorage,
  EventSource,
  ResizeObserver,
  Notification: undefined,
  navigator: { serviceWorker: undefined }, // registration is skipped in the harness
  location: { protocol: "file:" },
  getComputedStyle: () => ({ getPropertyValue: (n) => COLORS[n] || "#888888" }),
  fetch: (url) => {
    if (String(url).includes("/api/history")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ info: INFO, samples: HISTORY_SAMPLES }) });
    }
    if (String(url).includes("/api/summary")) {
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({
          day: "2026-11-02", samples: 120, gap: 5, bytes: 20000,
          cpuTemp: { max: 71, at: Date.now() }, gpuTemp: { max: 68, at: Date.now() },
          cpuLoad: { max: 88 }, gpuLoad: { max: 99 }, busyMin: 12.5, busyRunMin: 3,
          avgCpuW: 52.3, avgGpuW: 210.4,
        }),
      });
    }
    return Promise.resolve({ ok: false, json: () => Promise.resolve({}) });
  },
  requestAnimationFrame: (fn) => { rafCalls++; if (fn) fn(); return 0; },
  setInterval: () => 0,
  clearInterval: () => {},
  setTimeout: (fn) => { try { fn(); } catch (e) { throw e; } return 0; },
  console,
  Date,
  Math,
  JSON,
  Object,
  Array,
  Number,
  String,
  isFinite,
  isNaN,
  parseInt,
  parseFloat,
  Blob: class {},
  URL: { createObjectURL: () => "blob:", revokeObjectURL() {} },
};
sandbox.globalThis = sandbox;
sandbox.window = Object.assign(window, sandbox);

const code = fs.readFileSync("web/app.js", "utf8");
const i18nCode = fs.readFileSync("web/i18n.js", "utf8");
let failed = false;
process.on("unhandledRejection", (err) => {
  failed = true;
  console.error("UNHANDLED REJECTION:", err && err.stack ? err.stack : err);
});

try {
  vm.createContext(sandbox);
  // i18n.js defines window.I18N, which app.js uses for every label - load it
  // first or the boot fails before a single pixel is painted.
  vm.runInContext(i18nCode, sandbox, { filename: "i18n.js" });
  vm.runInContext(code, sandbox, { filename: "app.js" });
  // let the bootstrap promises settle, then inspect what got painted
  setTimeout(() => {
    const cpu = document.getElementById("cpuUsage").textContent;
    const gpu = document.getElementById("gpuUsage").textContent;
    const mem = document.getElementById("ramUsed").textContent;
    const fan = document.getElementById("fanSys").textContent;
    const ssd = document.getElementById("ssdTemp").textContent;
    const procs = document.getElementById("procMeta").textContent;
    const sum = document.getElementById("sumCpuMax").innerHTML;
    const fanMeta = document.getElementById("fanMeta").textContent;
    console.log(`cpuUsage=${cpu} gpuUsage=${gpu} ram=${mem} fan=${fan} ssd=${ssd}`);
    console.log(`procMeta=${procs} sumCpuMax=${sum}`);
    console.log(`fanMeta=${fanMeta}`);
    console.log(`history=${HISTORY_SAMPLES.length - 1} rows, canvas paints=${paints}, frames=${rafCalls}`);
    console.log(`foot=${document.getElementById("footCost").textContent}`);
    if (String(cpu) !== "42") { failed = true; console.error("render() did not update the DOM"); }
    // 7 headers, 3 of them at 0 RPM -> "4/7 路在转 · 最高 1197 RPM"
    if (!/^4\/7 路在转 · 最高 1197 RPM$/.test(fanMeta)) {
      failed = true;
      console.error("fan tile summary not rendered as expected: " + fanMeta);
    }
    console.log(failed ? "FAILED" : "app.js 初始化 + 渲染一条实时样本：无异常");
    process.exit(failed ? 1 : 0);
  }, 20);
} catch (err) {
  console.error("RUNTIME ERROR:", err && err.stack ? err.stack : err);
  process.exit(1);
}

