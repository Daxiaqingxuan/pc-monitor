/* ============================================================
   Bilingual dictionary + DOM binding
   ------------------------------------------------------------
   Loaded before app.js. Static text lives in index.html with
   data-i18n="key" (textContent), data-i18n-ph (placeholder),
   data-i18n-title (title) and data-i18n-aria (aria-label).
   Dynamic text goes through I18N.t(key, vars) in app.js.

   One language setting is shared with the desktop widget through
   /api/settings, so switching here also switches the tray menu.
   ============================================================ */
window.I18N = (function () {
  "use strict";

  const KEY = "hw-monitor-lang";

  const DICT = {
    zh: {
      "app.title": "硬件监测 · CPU / GPU",
      "app.brand": "硬件监测",
      "app.connecting": "正在连接传感器…",

      "btn.export": "导出",
      "btn.exportTitle": "导出本次记录为 CSV",
      "btn.pause": "暂停",
      "btn.resume": "继续",
      "btn.pauseTitle": "暂停刷新（空格）",
      "btn.alertTitle": "告警设置",
      "btn.bigTitle": "大屏模式（F）",
      "btn.bigAria": "大屏模式",
      "btn.themeTitle": "切换主题",
      "btn.themeAria": "切换明暗主题",
      "btn.settings": "检测项",
      "btn.settingsTitle": "检测项与语言",

      "lang.zh": "中文",
      "lang.en": "English",
      "lang.label": "语言 / Language",

      "alert.title": "告警",
      "alert.close": "收起",
      "alert.enable": "启用告警",
      "alert.notify": "桌面通知",
      "alert.cpuTemp": "CPU 温度",
      "alert.gpuTemp": "GPU 温度",
      "alert.cpuLoad": "CPU 占用",
      "alert.gpuLoad": "GPU 占用",
      "alert.hint": "超阈值时对应卡片描边变红、标签页标题闪烁；桌面通知需浏览器授权。",
      "alert.titleFlash": "硬件监测告警",

      "stat.temp": "温度",
      "stat.power": "功耗",
      "stat.clk": "当前频率",
      "stat.cores": "核心 / 线程",
      "stat.peak": "峰值占用",
      "stat.hot": "热点",
      "stat.vram": "显存占用",

      "sub.cores": "核心负载",
      "sub.engines": "引擎负载",

      "chart.usage": "占用率趋势",
      "chart.temp": "温度趋势",
      "chart.window": "时间窗口",
      "chart.pick": "选择指标",
      "chart.png": "导出 PNG",
      "chart.fit": "自适应纵轴：按数据范围缩放",
      "chart.fitOn": "自适应纵轴：已开启",
      "chart.note": "多条线各自按满量程缩放（占用/温度 100%，显存与内存按总量，功耗按额定，网络与磁盘按窗口内峰值），悬停可看真实数值。",
      "chart.shown": "显示 {n} / {m} 个",
      "chart.hide": "隐藏 {name}",
      "chart.empty": "没有可用的检测项",

      "empty.tempTitle": "没有读到温度传感器",
      "empty.tempBody": "未找到 LibreHardwareMonitor 的传感器库，或没有取得读取权限。",
      "empty.tempBtn": "查看当前探测状态",

      "fan.title": "风扇转速",
      "fan.meta": "{n}/{m} 路在转 · 最高 {peak} RPM",
      "fan.none": "未读到风扇转速。这一项需要管理员权限，且主板 SuperIO 已被 LibreHardwareMonitor 识别。",

      "sys.title": "内存 · 网络 · 磁盘",
      "sys.used": "已用",
      "sys.total": "总量",
      "sys.free": "可用",
      "sys.net": "网络",
      "sys.disk": "磁盘",
      "sys.read": "读",
      "sys.write": "写",
      "sys.fan": "风扇",
      "sys.max": "最高",
      "sys.temp": "温度",
      "sys.board": "主板",
      "sys.usedPct": "{v}% 已用",
      "sys.active": "活动",

      "proc.title": "进程占用",
      "proc.filter": "过滤进程…",
      "proc.sort": "排序",
      "proc.count": "条数",
      "proc.empty": "没有匹配的进程",
      "proc.loading": "采集器正在准备进程列表…",

      "sum.title": "今日概况",
      "sum.cpuMax": "最高 CPU 温度",
      "sum.gpuMax": "最高 GPU 温度",
      "sum.load": "峰值占用",
      "sum.busy": "高负载时长",
      "sum.power": "平均功耗",
      "sum.rows": "已记录",

      "note.title": "数据来源与已知限制",
      "note.usage": "<b>占用率</b>：CPU 每核负载走 <code>NtQuerySystemInformation</code>（与任务管理器同源，&lt;1ms），GPU 各引擎与显存走 Windows 的 WDDM 性能计数器，都不需要管理员权限。",
      "note.temps": "<b>温度 / 风扇</b>：Windows 不向普通程序暴露这些寄存器，这里直接加载 <b>LibreHardwareMonitor</b> 的传感器库读取。分两种情况：",
      "note.temps1": "<b>GPU 温度 / 热点</b>：普通权限即可读取；",
      "note.temps2": "<b>CPU 温度、主板与风扇（SuperIO）、SSD 温度</b>：需要内核驱动权限，因此启动时会请求一次管理员授权。",
      "note.none": "没有权限或没有库文件时，对应位置显示「—」，不会用估算值填充。",
      "note.hist": "<b>历史</b>：每 5 秒把一行精简记录追加到 <code>data\\history-YYYY-MM-DD.ndjson</code>，重启后自动回填趋势图，只保留最近 14 天。",
      "note.keys": "<b>快捷键</b>：<code>F</code> 大屏 · <code>空格</code> 暂停 / 继续 · <code>Esc</code> 退出大屏。",
      "note.state": "探测状态：—",

      "settings.title": "检测项",
      "settings.lang": "语言",
      "settings.hint": "关闭的检测项会停止采样，不只是隐藏。设置保存在 settings.json，桌面悬浮窗与网页共用。",
      "settings.saving": "保存中…",

      "sen.cpu": "CPU 占用",
      "sen.cpuTemp": "CPU 温度 / 功耗",
      "sen.gpu": "GPU 占用",
      "sen.gpuTemp": "GPU 温度 / 热点",
      "sen.mem": "内存",
      "sen.net": "网络速率",
      "sen.disk": "磁盘读写",
      "sen.diskTemp": "SSD 温度",
      "sen.fans": "风扇转速",
      "sen.board": "主板温度",
      "sen.proc": "进程榜单",

      "live.ok": "实时",
      "live.poll": "实时 · 轮询",
      "live.starting": "采集器启动中",
      "live.disconnected": "服务未连接",
      "live.offline": "采集器离线",
      "live.paused": "已暂停",
      "live.retry": "连接中断 · 重试中",
      "live.restart": "采集器重启中",

      "load.cpu": "CPU",
      "load.gpu": "GPU",
      "load.vram": "显存",
      "load.ram": "内存",
      "load.clk": "频率",
      "load.netrx": "网络 ↓",
      "load.nettx": "网络 ↑",
      "load.diskr": "磁盘读",
      "load.diskw": "磁盘写",
      "load.diskbusy": "磁盘活动",
      "temp.cpu": "CPU",
      "temp.gpu": "GPU",
      "temp.cpupw": "CPU 功耗",
      "temp.gpupw": "GPU 功耗",
      "temp.ssd": "SSD",
      "temp.board": "主板",

      "engine.video": "视频解码",
      "engine.codec": "视频编解码",
      "engine.idle": "当前没有活动的 GPU 引擎",

      "probe.connected": "LibreHardwareMonitor 传感器库 · 已连接（CPU / GPU / 主板 / 存储）",
      "probe.basic": "LibreHardwareMonitor · 基础模式（主板与存储不可用）",
      "probe.gpuOnly": "LibreHardwareMonitor · 仅 GPU（缺管理员权限）",
      "probe.nv": "nvidia-smi · 仅 GPU 温度",
      "probe.none": "未找到温度源（温度不可用）",
      "probe.line": "探测状态：{src} · {admin}",
      "probe.admin": "管理员权限",
      "probe.user": "普通权限",
      "probe.needAdmin": "读取 CPU 温度需要管理员权限",
      "misc.needAdmin": "需管理员",

      "misc.none": "(无归属)",
      "misc.min": "分钟",
      "misc.rows": "条",
      "misc.peakLong": "最长 {v} 分钟",
      "misc.threads": "{n} 线程",
      "misc.threadsPeak": "{n} 线程 · 最高 {p}%",
      "misc.core": "核心 {n}",
      "misc.cores": "核心",
      "misc.vramPct": "显存 {v}%",
      "misc.now": "现在",
      "misc.cost": "采样耗时 {cost} ms · {n} 个样本 · 更新于 {t}",
      "misc.alarm": "{text}，超过设定阈值",
      "misc.other": "其他",
      "misc.mixed": "勾选功耗等混合单位指标时不可用",
      "misc.lanWarn": "局域网模式：本页面没有密码，同网段任何人都能打开。",
      "big.exit": "退出大屏",
    },

    en: {
      "app.title": "Hardware Monitor · CPU / GPU",
      "app.brand": "Hardware Monitor",
      "app.connecting": "Connecting to sensors…",

      "btn.export": "Export",
      "btn.exportTitle": "Export the current series as CSV",
      "btn.pause": "Pause",
      "btn.resume": "Resume",
      "btn.pauseTitle": "Pause refresh (Space)",
      "btn.alertTitle": "Alert settings",
      "btn.bigTitle": "Big screen (F)",
      "btn.bigAria": "Big screen",
      "btn.themeTitle": "Toggle theme",
      "btn.themeAria": "Toggle light or dark theme",
      "btn.settings": "Sensors",
      "btn.settingsTitle": "Sensors and language",

      "lang.zh": "中文",
      "lang.en": "English",
      "lang.label": "Language / 语言",

      "alert.title": "Alerts",
      "alert.close": "Close",
      "alert.enable": "Enable alerts",
      "alert.notify": "Desktop notification",
      "alert.cpuTemp": "CPU temp",
      "alert.gpuTemp": "GPU temp",
      "alert.cpuLoad": "CPU load",
      "alert.gpuLoad": "GPU load",
      "alert.hint": "On a breach the card outline turns red and the tab title flashes. Desktop notifications need browser permission.",
      "alert.titleFlash": "Hardware Monitor alert",

      "stat.temp": "Temp",
      "stat.power": "Power",
      "stat.clk": "Clock",
      "stat.cores": "Cores / threads",
      "stat.peak": "Peak load",
      "stat.hot": "Hotspot",
      "stat.vram": "VRAM",

      "sub.cores": "Core load",
      "sub.engines": "Engine load",

      "chart.usage": "Utilization",
      "chart.temp": "Temperature",
      "chart.window": "Time window",
      "chart.pick": "Pick metrics",
      "chart.png": "Export PNG",
      "chart.fit": "Auto Y axis: fit the data range",
      "chart.fitOn": "Auto Y axis: on",
      "chart.note": "Each line scales to its own full range (load and temperature 100%, VRAM and RAM by total, power by rating, network and disk by the peak in the window). Hover for the real number.",
      "chart.shown": "Showing {n} / {m}",
      "chart.hide": "Hide {name}",
      "chart.empty": "No sensor is enabled",

      "empty.tempTitle": "No temperature sensor",
      "empty.tempBody": "The LibreHardwareMonitor sensor library was not found, or read permission was denied.",
      "empty.tempBtn": "Check probe status",

      "fan.title": "Fans",
      "fan.meta": "{n}/{m} spinning · max {peak} RPM",
      "fan.none": "No fan reading. This needs administrator rights and a motherboard SuperIO that LibreHardwareMonitor can identify.",

      "sys.title": "Memory · Network · Disk",
      "sys.used": "Used",
      "sys.total": "Total",
      "sys.free": "Free",
      "sys.net": "Network",
      "sys.disk": "Disk",
      "sys.read": "Read",
      "sys.write": "Write",
      "sys.fan": "Fans",
      "sys.max": "Max",
      "sys.temp": "Temp",
      "sys.board": "Board",
      "sys.usedPct": "{v}% used",
      "sys.active": "Busy",

      "proc.title": "Processes",
      "proc.filter": "Filter processes…",
      "proc.sort": "Sort",
      "proc.count": "Rows",
      "proc.empty": "No matching process",
      "proc.loading": "Collector is preparing the process list…",

      "sum.title": "Today",
      "sum.cpuMax": "Max CPU temp",
      "sum.gpuMax": "Max GPU temp",
      "sum.load": "Peak load",
      "sum.busy": "Time at high load",
      "sum.power": "Average power",
      "sum.rows": "Recorded",

      "note.title": "Data sources and known limits",
      "note.usage": "<b>Utilization</b>: per-core CPU load comes from <code>NtQuerySystemInformation</code> (the same source Task Manager uses, under 1 ms); GPU engines and VRAM come from the Windows WDDM performance counters. Neither needs administrator rights.",
      "note.temps": "<b>Temperature / fans</b>: Windows does not expose these registers to normal programs, so the <b>LibreHardwareMonitor</b> sensor library is loaded and read directly. Two cases:",
      "note.temps1": "<b>GPU temperature / hotspot</b>: readable without elevation;",
      "note.temps2": "<b>CPU temperature, motherboard and fans (SuperIO), SSD temperature</b>: these need kernel driver access, so the app asks for administrator rights once at launch.",
      "note.none": "Without permission or without the library, the field shows a dash. It is never filled with an estimate.",
      "note.hist": "<b>History</b>: one compact row every 5 seconds is appended to <code>data\\history-YYYY-MM-DD.ndjson</code>, replayed into the chart after a restart, and kept for 14 days.",
      "note.keys": "<b>Keys</b>: <code>F</code> big screen · <code>Space</code> pause / resume · <code>Esc</code> leave big screen.",
      "note.state": "Probe: —",

      "settings.title": "Sensors",
      "settings.lang": "Language",
      "settings.hint": "A disabled sensor stops sampling — it is not just hidden. Saved to settings.json and shared with the desktop widget.",
      "settings.saving": "Saving…",

      "sen.cpu": "CPU load",
      "sen.cpuTemp": "CPU temp / power",
      "sen.gpu": "GPU load",
      "sen.gpuTemp": "GPU temp / hotspot",
      "sen.mem": "Memory",
      "sen.net": "Network",
      "sen.disk": "Disk I/O",
      "sen.diskTemp": "SSD temp",
      "sen.fans": "Fans",
      "sen.board": "Board temp",
      "sen.proc": "Process list",

      "live.ok": "Live",
      "live.poll": "Live · polling",
      "live.starting": "Collector starting",
      "live.disconnected": "Disconnected",
      "live.offline": "Collector offline",
      "live.paused": "Paused",
      "live.retry": "Reconnecting…",
      "live.restart": "Collector restarting",

      "load.cpu": "CPU",
      "load.gpu": "GPU",
      "load.vram": "VRAM",
      "load.ram": "RAM",
      "load.clk": "Clock",
      "load.netrx": "Net ↓",
      "load.nettx": "Net ↑",
      "load.diskr": "Disk read",
      "load.diskw": "Disk write",
      "load.diskbusy": "Disk busy",
      "temp.cpu": "CPU",
      "temp.gpu": "GPU",
      "temp.cpupw": "CPU power",
      "temp.gpupw": "GPU power",
      "temp.ssd": "SSD",
      "temp.board": "Board",

      "engine.video": "Video Decode",
      "engine.codec": "Video Codec",
      "engine.idle": "No active GPU engine",

      "probe.connected": "LibreHardwareMonitor · connected (CPU / GPU / board / storage)",
      "probe.basic": "LibreHardwareMonitor · basic mode (board and storage unavailable)",
      "probe.gpuOnly": "LibreHardwareMonitor · GPU only (no administrator rights)",
      "probe.nv": "nvidia-smi · GPU temperature only",
      "probe.none": "No temperature source (unavailable)",
      "probe.line": "Probe: {src} · {admin}",
      "probe.admin": "administrator",
      "probe.user": "standard user",
      "probe.needAdmin": "CPU temperature needs administrator rights",
      "misc.needAdmin": "Needs admin",

      "misc.none": "(unattributed)",
      "misc.min": "min",
      "misc.rows": "rows",
      "misc.peakLong": "longest {v} min",
      "misc.threads": "{n} threads",
      "misc.threadsPeak": "{n} threads · peak {p}%",
      "misc.core": "Core {n}",
      "misc.cores": "Cores",
      "misc.vramPct": "VRAM {v}%",
      "misc.now": "now",
      "misc.cost": "sample cost {cost} ms · {n} samples · updated {t}",
      "misc.alarm": "{text}, threshold exceeded",
      "misc.other": "Other",
      "misc.mixed": "Not available when mixed-unit lines such as power are selected",
      "misc.lanWarn": "LAN mode: this page has no password. Anyone on your network can open it.",
      "big.exit": "Exit big screen",
    },
  };

  let lang = "zh";

  function t(key, vars) {
    const table = DICT[lang] || DICT.zh;
    let s = table[key];
    if (s === undefined) s = DICT.zh[key];
    if (s === undefined) return key;
    if (vars) {
      for (const k in vars) {
        if (Object.prototype.hasOwnProperty.call(vars, k)) {
          s = s.split("{" + k + "}").join(String(vars[k]));
        }
      }
    }
    return s;
  }

  function apply(root) {
    const scope = root || document;
    scope.querySelectorAll("[data-i18n]").forEach((node) => {
      const key = node.getAttribute("data-i18n");
      const val = t(key);
      // Some entries carry inline markup (<b>, <code>); only use HTML when
      // the dictionary actually contains a tag, so plain text stays escaped.
      if (/[<]/.test(val)) node.innerHTML = val;
      else node.textContent = val;
    });
    scope.querySelectorAll("[data-i18n-ph]").forEach((node) => {
      node.placeholder = t(node.getAttribute("data-i18n-ph"));
    });
    scope.querySelectorAll("[data-i18n-title]").forEach((node) => {
      node.title = t(node.getAttribute("data-i18n-title"));
    });
    scope.querySelectorAll("[data-i18n-aria]").forEach((node) => {
      node.setAttribute("aria-label", t(node.getAttribute("data-i18n-aria")));
    });
    document.documentElement.lang = lang === "zh" ? "zh-CN" : "en";
  }

  function setLang(next, silent) {
    lang = next === "en" ? "en" : "zh";
    try { localStorage.setItem(KEY, lang); } catch (e) { /* private mode */ }
    apply(document);
    if (!silent) document.dispatchEvent(new CustomEvent("langchange", { detail: { lang } }));
  }

  function init(serverLang) {
    let saved = null;
    try { saved = localStorage.getItem(KEY); } catch (e) { /* private mode */ }
    // The server value wins on first load: the desktop menu and the page must
    // agree, and the desktop menu cannot read localStorage.
    lang = (saved || serverLang) === "en" ? "en" : "zh";
    try { localStorage.setItem(KEY, lang); } catch (e) { /* private mode */ }
    apply(document);
  }

  return {
    t,
    apply,
    setLang,
    init,
    get lang() { return lang; },
    keys: (l) => Object.keys(DICT[l || lang] || DICT.zh),
  };
})();
