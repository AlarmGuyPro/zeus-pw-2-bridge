// SPDX-License-Identifier: GPL-2.0-or-later
//
// IC-PW2 Bridge — Zeus workspace panel (redesigned).
//
// Plain ESM (no build step): React elements via React.createElement, so this
// file is valid as written. Zeus provides `react` as an external.
//
// Layout matches the Zeus station look: analog arc gauges for Forward Power and
// SWR, segmented bars for ALC / Vd / Id, and four clearly separated control
// groups — main Power (confirm), STBY/OPER, RF Input 1/2, and Antenna 1-6.
// Input and antenna reflect what the amp actually reports (1A 00 / 1A 06), and
// antenna writes target the live input, which is what the hardware requires.
//
// Styling uses only the documented Zeus CSS token subset, scoped under the
// feature root class. No raw colors.

import React from "react";

const { useState, useEffect, useRef, useCallback } = React;
const h = React.createElement;

const ROOT = "com-kq4wlr-pw2bridge";
// Secondary class names carry the feature prefix (CONTRIBUTING §4).
const pcls = (names) => String(names || "").split(/\s+/).filter(Boolean)
  .map((n) => ROOT + "__" + n).join(" ");
const PANEL_ID = "pw2bridge.main";
const AMP_BANDS = ["160m", "80m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];

// ── styles ──────────────────────────────────────────────────────────────────
const CSS = `
.${ROOT}{font-family:var(--font-sans);color:var(--fg-1);background:var(--bg-1);
  border:1px solid var(--panel-border);border-radius:var(--r-sm);padding:6px;
  display:flex;flex-direction:column;gap:5px;box-sizing:border-box;
  width:100%;height:100%;overflow:auto;font-size:10px}
.${ROOT} *{box-sizing:border-box}
.${ROOT} .${ROOT}__row{display:flex;align-items:center;gap:5px;flex-wrap:wrap}
.${ROOT} .${ROOT}__between{justify-content:space-between}
.${ROOT} .${ROOT}__title{font-size:11px;font-weight:700;color:var(--fg-0);letter-spacing:.02em}
.${ROOT} .${ROOT}__sub{font-size:8px;color:var(--fg-3)}
.${ROOT} .${ROOT}__pill{font-size:8px;font-weight:700;padding:2px 5px;border-radius:var(--r-lg);
  border:1px solid var(--line);color:var(--fg-2);background:var(--bg-2);white-space:nowrap}
.${ROOT} .${ROOT}__pill.${ROOT}__ok{color:var(--ok);border-color:var(--ok)}
.${ROOT} .${ROOT}__pill.${ROOT}__warn{color:var(--amber);border-color:var(--amber)}
.${ROOT} .${ROOT}__pill.${ROOT}__tx{color:var(--tx);border-color:var(--tx)}
.${ROOT} .${ROOT}__pill.${ROOT}__acc{color:var(--accent-bright);border-color:var(--accent)}
.${ROOT} .${ROOT}__pill.${ROOT}__dim{opacity:.45}

.${ROOT} .${ROOT}__gauges{display:grid;grid-template-columns:1fr 1fr;gap:5px}
.${ROOT} .${ROOT}__gauge{background:var(--bg-2);border:1px solid var(--line);border-radius:var(--r-sm);
  padding:4px 5px 2px;display:flex;flex-direction:column;gap:1px}
.${ROOT} .${ROOT}__gauge .${ROOT}__grow{display:flex;align-items:center;gap:4px}
.${ROOT} .${ROOT}__gauge .${ROOT}__gdot{width:4px;height:4px;border-radius:50%;background:var(--accent);flex:none}
.${ROOT} .${ROOT}__gauge .${ROOT}__glabel{font-size:7.5px;color:var(--fg-3);letter-spacing:.06em;text-transform:uppercase}
.${ROOT} .${ROOT}__gauge .${ROOT}__gunits{font-size:7px;color:var(--fg-3);margin-left:auto;letter-spacing:.04em}

.${ROOT} .${ROOT}__bars{display:flex;flex-direction:column;gap:3px}
.${ROOT} .${ROOT}__bar{display:grid;grid-template-columns:24px 1fr 50px;align-items:center;gap:5px}
.${ROOT} .${ROOT}__bar .${ROOT}__bk{font-size:8px;color:var(--fg-3)}
.${ROOT} .${ROOT}__bar .${ROOT}__bv{font-family:var(--font-mono);font-size:9px;color:var(--fg-1);text-align:right}
.${ROOT} .${ROOT}__seg{display:flex;gap:1px;height:7px}
.${ROOT} .${ROOT}__seg i{flex:1;background:var(--bg-inset);border-radius:1px}
.${ROOT} .${ROOT}__seg i.${ROOT}__on{background:var(--accent)}
.${ROOT} .${ROOT}__seg i.${ROOT}__hot{background:var(--amber)}
.${ROOT} .${ROOT}__seg i.${ROOT}__max{background:var(--tx)}

.${ROOT} .${ROOT}__group{border-top:1px solid var(--line);padding-top:4px;display:flex;flex-direction:column;gap:4px}
.${ROOT} .${ROOT}__glab{font-size:7.5px;color:var(--fg-3);letter-spacing:.06em}
.${ROOT} .${ROOT}__btns{display:flex;gap:3px;flex-wrap:wrap}
.${ROOT} button.${ROOT}__b{font-family:var(--font-sans);font-size:9px;font-weight:600;
  padding:3px 9px;border-radius:var(--r-lg);border:1px solid var(--line-strong);
  background:var(--bg-3);color:var(--fg-1);cursor:pointer;transition:background var(--dur-fast) var(--ease-out)}
.${ROOT} button.${ROOT}__b:hover{background:var(--bg-2)}
.${ROOT} button.${ROOT}__b:focus-visible{outline:2px solid var(--accent);outline-offset:1px}
.${ROOT} button.${ROOT}__b:disabled{opacity:.5;cursor:not-allowed}
.${ROOT} button.${ROOT}__b.${ROOT}__sel{border-color:var(--accent);color:var(--accent-bright);background:var(--bg-1)}
.${ROOT} button.${ROOT}__b.${ROOT}__ok{border-color:var(--ok);color:var(--ok)}
.${ROOT} button.${ROOT}__b.${ROOT}__danger{border-color:var(--tx);color:var(--tx)}
.${ROOT} button.${ROOT}__b.${ROOT}__ant{min-width:24px;padding:3px 0;text-align:center}

.${ROOT} .${ROOT}__fld{display:flex;align-items:center;gap:5px;font-size:9px;color:var(--fg-2)}
.${ROOT} .${ROOT}__fld label{min-width:70px;color:var(--fg-3);font-size:8px}
.${ROOT} .${ROOT}__in,.${ROOT} select.${ROOT}__in{font-family:var(--font-mono);font-size:9px;
  background:var(--bg-inset);color:var(--fg-0);border:1px solid var(--line);
  border-radius:var(--r-xs);padding:2px 4px;min-width:0}
.${ROOT} .${ROOT}__in:focus,.${ROOT} select.${ROOT}__in:focus{outline:2px solid var(--accent);outline-offset:1px}
.${ROOT} .${ROOT}__toggle{display:flex;align-items:center;gap:4px;font-size:9px;color:var(--fg-2)}
.${ROOT} .${ROOT}__muted{font-size:8px;color:var(--fg-3)}
.${ROOT} .${ROOT}__log{background:var(--bg-inset);border:1px solid var(--line);border-radius:var(--r-xs);
  font-family:var(--font-mono);font-size:8px;line-height:1.5;max-height:110px;overflow:auto;padding:5px}
.${ROOT} .${ROOT}__log .${ROOT}__ln{display:flex;gap:5px;white-space:nowrap}
.${ROOT} .${ROOT}__log .${ROOT}__ts{color:var(--fg-3)}
.${ROOT} .${ROOT}__log .${ROOT}__civ{color:var(--accent-bright)}
.${ROOT} .${ROOT}__log .${ROOT}__warn{color:var(--amber)}
.${ROOT} .${ROOT}__log .${ROOT}__info{color:var(--fg-2)}
.${ROOT} .${ROOT}__log .${ROOT}__bridge{color:var(--fg-1)}

.${ROOT} .${ROOT}__modal{position:absolute;inset:0;background:var(--bg-inset);
  display:flex;align-items:center;justify-content:center;border-radius:var(--r-sm);z-index:5}
.${ROOT} .${ROOT}__card{background:var(--bg-1);border:1px solid var(--line-strong);border-radius:var(--r-sm);
  padding:10px;display:flex;flex-direction:column;gap:7px;max-width:220px}
.${ROOT} .${ROOT}__card .${ROOT}__ct{font-size:10px;color:var(--fg-0);font-weight:600}
@media (prefers-reduced-motion: reduce){.${ROOT} button.${ROOT}__b{transition:none}}
`;

function ensureStyle() {
  if (typeof document === "undefined") return;
  if (document.getElementById("pw2-style")) return;
  const s = document.createElement("style");
  s.id = "pw2-style";
  s.textContent = CSS;
  document.head.appendChild(s);
}

async function api(callBackend, method, path, body) {
  const res = await callBackend(method, path, body);
  const txt = await res.text();
  const data = txt ? JSON.parse(txt) : null;
  if (!res.ok) throw new Error((data && data.message) || `${method} ${path} → ${res.status}`);
  return data;
}

function fmt(v, d) {
  if (v === null || v === undefined) return "—";
  return typeof v === "number" && d !== undefined ? v.toFixed(d) : String(v);
}

// ── analog half-circle gauge (SVG), styled to match the Zeus station gauges ──
// 180° sweep, faded track, numbered ticks OUTSIDE the arc, thin needle from a
// bottom-center hub resting at the left (min), red danger cap, value readout
// across the bottom, and a header row (dot+label left, units right).
//
//  label   : gauge name (e.g. "FORWARD POWER")
//  value   : current value (or null)
//  min,max : scale ends (Forward Power 0..1000, SWR 1..5)
//  unitHdr : right-side units text in the header ("WATTS", "RATIO · :1")
//  digits  : decimals in the big readout
//  ticks   : array of scale values to label around the arc
//  warnFrac: 0..1 fraction where the red danger zone begins (null = none)
function Gauge(label, value, min, max, unitHdr, digits, ticks, warnFrac, colorMode, dim, peak) {
  const W = 200, H = 150, cx = 100, cy = 92, r = 66;
  const band = 5.06;                       // another 25% narrower (was 6.75)
  const a0 = 150, a1 = 390;                // 240° sweep, ends below the hub
  const span = max - min;
  const clamp = (v) => Math.max(min, Math.min(max, v));
  const known = value != null && !dim;
  const frac = known ? (clamp(value) - min) / span : 0;
  const rad = (deg) => deg * Math.PI / 180;
  const toXY = (deg, rr) => [cx + rr * Math.cos(rad(deg)), cy + rr * Math.sin(rad(deg))];
  const fToDeg = (f) => a0 + (a1 - a0) * f;

  const arc = (fromF, toF, rr) => {
    const [x0, y0] = toXY(fToDeg(fromF), rr);
    const [x1, y1] = toXY(fToDeg(toF), rr);
    // large-arc flag must reflect the ANGULAR span in degrees (>180), not the
    // fraction. Total sweep is (a1 - a0) degrees.
    const large = (a1 - a0) * (toF - fromF) > 180 ? 1 : 0;
    return `M ${x0} ${y0} A ${rr} ${rr} 0 ${large} 1 ${x1} ${y1}`;
  };

  const uid = (label + max).replace(/[^a-z0-9]/gi, "");

  // Fill color. "power" = bright amber. "swr" = green ≤2, yellow 2–3, red ≥3.
  let fillColor = "var(--accent)";
  if (colorMode === "power") fillColor = "var(--amber)";
  else if (colorMode === "swr") {
    fillColor = value == null ? "var(--ok)"
      : value >= 3 ? "var(--tx)"
      : value >= 2 ? "var(--amber)"
      : "var(--ok)";
  }

  const majorFracs = (ticks || []).map((tv) => (tv - min) / span);
  const isMajor = (f) => majorFracs.some((mf) => Math.abs(mf - f) < 0.001) || f < 0.001 || f > 0.999;
  const tickEls = [];
  const N = 40;
  for (let i = 0; i <= N; i++) {
    const f = i / N, major = isMajor(f), d = fToDeg(f);
    const [ix, iy] = toXY(d, r - band / 2);
    const [ox, oy] = toXY(d, r - band / 2 - (major ? 6 : 3));
    tickEls.push(h("line", { key: "t" + i, x1: ix, y1: iy, x2: ox, y2: oy,
      stroke: major ? "var(--fg-1)" : "var(--fg-3)", strokeWidth: major ? 1.1 : 0.6, opacity: major ? 0.85 : 0.45 }));
  }
  const labelEls = (ticks || []).filter((tv) => {
    const f = (tv - min) / span; return f > 0.001 && f < 0.999;
  }).map((tv, i) => {
    const f = (tv - min) / span;
    const [lx, ly] = toXY(fToDeg(f), r - band / 2 - 13);
    return h("text", { key: "l" + i, x: lx, y: ly, fill: "var(--fg-2)", fontSize: 9,
      textAnchor: "middle", dominantBaseline: "middle", fontFamily: "var(--font-mono)", opacity: 0.8 }, formatTick(tv));
  });

  const ndeg = fToDeg(frac);
  const tip = toXY(ndeg, r - band + 1);
  const tail = toXY(ndeg + 180, 9);
  const baseL = toXY(ndeg + 90, 2.4);
  const baseR = toXY(ndeg - 90, 2.4);
  const [gx, gy] = toXY(a0, r);
  const capIn = toXY(a1, r - band / 2 - 0.5);
  const capOut = toXY(a1, r + band / 2 + 0.5);

  return h("div", { className: pcls("gauge") },
    h("div", { className: pcls("grow") },
      h("span", { className: pcls("gdot") }),
      h("span", { className: pcls("glabel") }, label),
      h("span", { className: pcls("gunits") }, unitHdr)),
    h("svg", { viewBox: `0 0 ${W} ${H}`, width: "100%", role: "img", "aria-label": `${label} ${fmt(value, digits)}` },
      h("defs", null,
        h("radialGradient", { id: "face" + uid, cx: "50%", cy: "42%", r: "70%" },
          h("stop", { offset: "0%", stopColor: "var(--bg-2)" }),
          h("stop", { offset: "60%", stopColor: "var(--bg-1)" }),
          h("stop", { offset: "100%", stopColor: "var(--bg-inset)" })),
        // faint blue glow rising from the bottom of the box
        h("linearGradient", { id: "blue" + uid, x1: "0", y1: "1", x2: "0", y2: "0" },
          h("stop", { offset: "0%", stopColor: "var(--accent)", stopOpacity: dim ? "0.06" : "0.22" }),
          h("stop", { offset: "100%", stopColor: "var(--accent)", stopOpacity: "0" }))),
      // face wash
      h("rect", { x: 0, y: 0, width: W, height: H, rx: 6, fill: `url(#face${uid})` }),
      // faint blue bottom glow — kept narrow so it doesn't distort the track edge
      h("path", { d: arc(0, 1, r), fill: "none", stroke: `url(#blue${uid})`, strokeWidth: band + 12,
        strokeLinecap: "round", opacity: 0.4 }),
      // unlit track and lit fill share the exact same arc(0,1,r) / radius / width
      h("path", { d: arc(0, 1, r), fill: "none", stroke: "var(--bg-3)", strokeWidth: band, strokeLinecap: "round", opacity: dim ? 0.5 : 0.85 }),
      known && frac > 0.002 ? h("path", { d: arc(0, frac, r), fill: "none",
        stroke: fillColor, strokeWidth: band, strokeLinecap: "round", opacity: 0.95 }) : null,
      ...tickEls, ...labelEls,
      // green rest node (min end) + clean radial red cap (max end)
      h("circle", { cx: gx, cy: gy, r: 3, fill: "var(--ok)", opacity: dim ? 0.4 : 1 }),
      h("line", { x1: capIn[0], y1: capIn[1], x2: capOut[0], y2: capOut[1], stroke: "var(--tx)", strokeWidth: 2.5, opacity: dim ? 0.4 : 1 }),
      // needle
      h("polygon", { points: `${tip[0]},${tip[1]} ${baseL[0]},${baseL[1]} ${tail[0]},${tail[1]} ${baseR[0]},${baseR[1]}`,
        fill: "var(--fg-0)", opacity: dim ? 0.35 : 1 }),
      // hub
      h("circle", { cx, cy, r: 6, fill: "var(--bg-2)", stroke: "var(--line-strong)", strokeWidth: 1 }),
      h("circle", { cx: cx - 1.5, cy: cy - 1.5, r: 1.6, fill: "var(--fg-1)", opacity: dim ? 0.3 : 0.7 }),
      // peak readout (smaller) just above the current value, when provided
      (known && peak != null && peak !== undefined)
        ? h("text", { x: cx, y: H - 26, fill: "var(--amber)", fontSize: 10, textAnchor: "middle",
            fontFamily: "var(--font-mono)", fontWeight: 600, opacity: 0.9 }, "PK " + fmt(peak, digits)) : null,
      // value at box bottom — "—" when unknown
      h("text", { x: cx, y: H - 8, fill: "var(--fg-0)", fontSize: 20, textAnchor: "middle",
        fontFamily: "var(--font-mono)", fontWeight: 700, opacity: known ? 1 : 0.4 }, known ? fmt(value, digits) : "—"),
      known ? h("text", { x: cx + 32, y: H - 8, fill: "var(--fg-3)", fontSize: 8, textAnchor: "start",
        fontFamily: "var(--font-mono)" }, unitShort(unitHdr)) : null));
}

function unitShort(u) {
  if (!u) return "";
  if (u.indexOf("WATT") >= 0) return "W";
  if (u.indexOf("RATIO") >= 0) return ":1";
  return "";
}

function formatTick(v) {
  if (v >= 1000) return (v / 1000) + "k";
  return String(v);
}

// ── segmented bar ───────────────────────────────────────────────────────────
function Bar(label, value, max, unit, digits, warnFrac) {
  const N = 20;
  const frac = value == null ? 0 : Math.max(0, Math.min(1, value / max));
  const lit = Math.round(frac * N);
  const wi = warnFrac != null ? Math.round(warnFrac * N) : N + 1;
  const segs = [];
  for (let i = 0; i < N; i++) {
    let cls = "";
    if (i < lit) cls = i >= N - 1 ? "max" : (i >= wi ? "hot" : "on");
    segs.push(h("i", { key: i, className: pcls(cls) }));
  }
  return h("div", { className: pcls("bar") },
    h("span", { className: pcls("bk") }, label),
    h("div", { className: pcls("seg") }, segs),
    h("span", { className: pcls("bv") }, fmt(value, digits) + (unit ? " " + unit : "")));
}

// ── panel ───────────────────────────────────────────────────────────────────
function makePanel(callBackend) {
  return function Pw2Panel() {
    const [status, setStatus] = useState(null);
    const [cfg, setCfg] = useState(null);
    const [ver, setVer] = useState(null);
    const [ports, setPorts] = useState([]);
    const [log, setLog] = useState([]);
    const [err, setErr] = useState(null);
    const [busy, setBusy] = useState(false);
    const [showSettings, setShowSettings] = useState(false);
    const [showLog, setShowLog] = useState(false);
    const [confirmPower, setConfirmPower] = useState(null);
    const [tuneDialog, setTuneDialog] = useState(false);
    const timer = useRef(null);

    const refresh = useCallback(async () => {
      try { setStatus(await api(callBackend, "GET", "/status")); setErr(null); }
      catch (e) { setErr(e.message); }
    }, []);

    useEffect(() => {
      ensureStyle();
      let alive = true;
      (async () => {
        try {
          const [c, p] = await Promise.all([
            api(callBackend, "GET", "/config"),
            api(callBackend, "GET", "/ports"),
          ]);
          if (!alive) return;
          setCfg(c); setPorts(p || []);
        } catch (e) { if (alive) setErr(e.message); }
        try { const v = await api(callBackend, "GET", "/version"); if (alive) setVer(v); } catch (e) { /* ignore */ }
        refresh();
      })();
      timer.current = setInterval(refresh, 1500);
      return () => { alive = false; if (timer.current) clearInterval(timer.current); };
    }, [refresh]);

    useEffect(() => {
      if (!showLog) return;
      let alive = true;
      const t = setInterval(async () => {
        try { const l = await api(callBackend, "GET", "/log"); if (alive) setLog(l || []); } catch (e) { /* ignore */ }
      }, 1000);
      return () => { alive = false; clearInterval(t); };
    }, [showLog]);

    const act = useCallback(async (method, path, body) => {
      setBusy(true);
      try { await api(callBackend, method, path, body); await refresh(); setErr(null); }
      catch (e) { setErr(e.message); }
      finally { setBusy(false); }
    }, [refresh]);

    const saveCfg = useCallback(async (patch) => {
      setBusy(true);
      try { setCfg(await api(callBackend, "POST", "/config", patch)); setErr(null); }
      catch (e) { setErr(e.message); }
      finally { setBusy(false); }
    }, []);

    if (!status && !err) {
      return h("div", { className: ROOT }, h("div", { className: pcls("muted") }, "Loading IC-PW2 Bridge…"));
    }

    const connected = status && status.connected;
    const m = (status && status.meters) || {};
    const useF = !cfg || cfg.tempUnit !== "C";
    const tempVal = useF ? m.tempF : m.tempC;
    const ampState = (status && status.ampState) || "OFF";
    const tx = status && status.tx;
    const activeInput = status && typeof status.activeInput === "number" ? status.activeInput : 0;
    const activeAnt = status && typeof status.activeAntenna === "number" ? status.activeAntenna : 0;
    const protBad = m.protection && m.protection !== "None";
    const dim = !connected;
    const poweredOn = status && status.poweredOn;

    const antName = status && status.activeAntennaName ? status.activeAntennaName : "";
    const antLabel = antName && antName.length ? antName : (connected ? "ANT " + activeAnt : "—");
    const overheat = status && status.overheatLatched;
    const header = h("div", { className: pcls("row between") },
      h("div", null,
        h("div", { className: pcls("title") }, "IC-PW2"),
        h("div", { className: pcls("sub") },
          (connected ? (status.port || "connected") : "disconnected") + (connected ? "  ·  INPUT " + (activeInput + 1) + "  ·  " + antLabel : ""))),
      h("div", { className: pcls("row") },
        (!dim && tx) ? h("span", { className: pcls("pill tx") }, "TX") : null,
        h("span", { className: pcls("pill " + (dim ? "dim" : (poweredOn ? "ok" : "dim"))) }, dim ? "POWER —" : (poweredOn ? "POWER ON" : "POWER OFF")),
        h("span", { className: pcls("pill " + (dim ? "dim" : (ampState === "OPER" ? "ok" : ampState === "STBY" ? "acc" : ""))) }, dim ? "—" : ampState),
        h("span", { className: pcls("pill " + (connected ? "ok" : "warn")) }, connected ? "ONLINE" : "OFFLINE")));

    // Prominent alarm banners for overheat / protection faults.
    const alarms = [];
    if (!dim && overheat) alarms.push(h("span", { key: "oh", className: pcls("pill tx") }, "⚠ OVERHEAT — STBY LATCHED (press STBY to clear)"));
    if (!dim && protBad) alarms.push(h("span", { key: "pr", className: pcls("pill tx") }, "⚠ PROTECT: " + m.protection));
    const alarmRow = alarms.length ? h("div", { className: pcls("row") }, alarms) : null;

    const on60mLow = status && status.detectedBand === "60m-new";
    const bandRow = h("div", { className: pcls("row") },
      h("span", { className: pcls("glab") }, "BAND"),
      h("span", { className: pcls("pill acc") }, (status && status.detectedBandDisplay) || "—"),
      status && status.bandFollow ? h("span", { className: pcls("muted") }, "auto-follow on") : h("span", { className: pcls("muted") }, "auto-follow off"),
      on60mLow ? h("span", { className: pcls("pill warn") }, "60m — STBY forced") : null,
      status && status.pendingBand ? h("span", { className: pcls("pill warn") }, "pending " + status.pendingBand) : null);

    const gauges = h("div", { className: pcls("gauges") },
      Gauge("FORWARD POWER", dim ? null : m.po, 0, 1000, "WATTS", 0, [0, 200, 400, 600, 800, 1000], null, "power", dim, dim ? null : m.peakPo),
      Gauge("SWR", dim ? null : m.swr, 1, 5, "RATIO · :1", 2, [1, 2, 3, 4, 5], 0.625, "swr", dim));  // red from SWR 3.5 (=(3.5-1)/4)

    const bars = h("div", { className: pcls("bars") },
      Bar("ALC", dim ? null : m.alc, 100, "%", 0, 0.85),
      Bar("Vd", dim ? null : m.vd, 60, "V", 1, null),
      Bar("Id", dim ? null : m.id, 50, "A", 1, 0.85));

    const env = h("div", { className: pcls("row") },
      h("span", { className: pcls("pill") }, "TEMP " + (dim ? "—" : fmt(tempVal, 1) + (useF ? " °F" : " °C"))),
      h("span", { className: pcls("pill") }, "HUMIDITY " + (dim ? "—" : fmt(m.humidity) + " %")),
      h("span", { className: pcls("pill " + (dim ? "dim" : (protBad ? "warn" : "ok"))) }, "PROTECT " + (dim ? "—" : (m.protection || "—"))));

    const powerGroup = h("div", { className: pcls("group") },
      h("div", { className: pcls("glab") }, "MAIN POWER"),
      h("div", { className: pcls("btns") },
        h("button", { className: pcls("b ok"), disabled: busy || !connected, onClick: () => setConfirmPower(true) }, "Power ON"),
        h("button", { className: pcls("b danger"), disabled: busy || !connected, onClick: () => setConfirmPower(false) }, "Power OFF")));

    const tunerState = status && typeof status.tunerState === "number" ? status.tunerState : 0;
    const tunerOn = tunerState >= 1;
    const tuning = tunerState === 2;
    const ampGroup = h("div", { className: pcls("group") },
      h("div", { className: pcls("glab") }, "AMPLIFIER"),
      h("div", { className: pcls("btns") },
        h("button", { className: pcls("b " + (ampState === "OPER" ? "sel" : "")), disabled: busy || !connected || on60mLow, onClick: () => act("POST", "/oper") }, "OPER"),
        h("button", { className: pcls("b " + (ampState === "STBY" ? "sel" : "")), disabled: busy || !connected, onClick: () => act("POST", "/stby") }, "STBY"),
        h("button", { className: pcls("b " + (tunerOn ? "sel" : "")), disabled: busy || !connected, onClick: () => act("POST", "/tuner", { enabled: !tunerOn }) }, tunerOn ? "TUNER ON" : "TUNER OFF"),
        h("button", { className: pcls("b " + (tuning ? "acc" : "")), disabled: busy || !connected, onClick: () => setTuneDialog(true) }, tuning ? "TUNING…" : "TUNE"),
        protBad ? h("button", { className: pcls("b warn"), disabled: busy, onClick: () => act("POST", "/clear-protection") }, "Clear Protection") : null));

    const inputGroup = h("div", { className: pcls("group") },
      h("div", { className: pcls("glab") }, "RF INPUT"),
      h("div", { className: pcls("btns") },
        h("button", { className: pcls("b " + (activeInput === 0 ? "sel" : "")), disabled: busy || !connected, onClick: () => act("POST", "/input", { input: 0 }) }, "INPUT 1"),
        h("button", { className: pcls("b " + (activeInput === 1 ? "sel" : "")), disabled: busy || !connected, onClick: () => act("POST", "/input", { input: 1 }) }, "INPUT 2")));

    const antNames = (status && status.antennaNames) || [];
    const antButtons = [];
    for (let a = 1; a <= 6; a++) {
      const nm = antNames[a - 1];
      const tip = nm && nm.length ? "ANT " + a + " — " + nm : "ANT " + a;
      antButtons.push(h("button", {
        key: a, className: pcls("b ant " + (activeAnt === a ? "sel" : "")),
        title: tip,
        disabled: busy || !connected, onClick: () => act("POST", "/antenna", { antenna: a }),
      }, String(a)));
    }
    const antGroup = h("div", { className: pcls("group") },
      h("div", { className: pcls("glab") }, "ANTENNA  (INPUT " + (activeInput + 1) + ")"),
      h("div", { className: pcls("btns") }, antButtons));

    const tools = h("div", { className: pcls("row") },
      connected
        ? h("button", { className: pcls("b"), disabled: busy, onClick: () => act("POST", "/disconnect") }, "Disconnect")
        : h("button", { className: pcls("b"), disabled: busy, onClick: () => act("POST", "/connect") }, "Connect"),
      h("button", { className: pcls("b"), onClick: () => setShowSettings(v => !v) }, showSettings ? "Hide Settings" : "Settings"),
      h("button", { className: pcls("b"), onClick: () => setShowLog(v => !v) }, showLog ? "Hide Log" : "Log"));

    const errBanner = err ? h("div", { className: pcls("row") },
      h("span", { className: pcls("pill warn") }, "ERROR"), h("span", { className: pcls("muted") }, err)) : null;

    const settings = showSettings && cfg ? h("div", { className: pcls("group") },
      h("div", { className: pcls("fld") }, h("label", null, "COM Port"),
        h("select", { className: pcls("in"), value: cfg.port, onChange: e => saveCfg({ port: e.target.value }) },
          (ports.length ? ports : [cfg.port]).map(p => h("option", { key: p, value: p }, p)))),
      h("div", { className: pcls("fld") }, h("label", null, "Baud"),
        h("select", { className: pcls("in"), value: String(cfg.baud), onChange: e => saveCfg({ baud: parseInt(e.target.value, 10) }) },
          ["4800", "9600", "19200"].map(b => h("option", { key: b, value: b }, b)))),
      h("div", { className: pcls("fld") }, h("label", null, "CI-V Addr"),
        h("input", { className: pcls("in"), value: cfg.pw2AddrHex, size: 4, maxLength: 2,
          onChange: e => saveCfg({ pw2AddrHex: e.target.value.toUpperCase() }) }),
        h("span", { className: pcls("muted") }, "hex, default AA")),
      h("div", { className: pcls("fld") }, h("label", null, "Temp Unit"),
        h("select", { className: pcls("in"), value: cfg.tempUnit, onChange: e => saveCfg({ tempUnit: e.target.value }) },
          [h("option", { key: "F", value: "F" }, "°F"), h("option", { key: "C", value: "C" }, "°C")])),
      h("div", { className: pcls("fld") }, h("label", null, "Overheat STBY"),
        h("input", { className: pcls("in"), type: "number", step: 1, value: cfg.maxTemp, size: 5,
          onChange: e => saveCfg({ maxTemp: parseFloat(e.target.value) || 120 }) }),
        h("span", { className: pcls("muted") }, "°" + cfg.tempUnit + " — auto-STBY at/above")),
      h("div", { className: pcls("fld") }, h("label", null, "Default STBY"),
        h("select", { className: pcls("in"), value: cfg.defaultStby || "connect",
          onChange: e => saveCfg({ defaultStby: e.target.value }) },
          [h("option", { key: "connect", value: "connect" }, "On connect / Zeus start"),
           h("option", { key: "always", value: "always" }, "Also on amp power-on"),
           h("option", { key: "off", value: "off" }, "Off")])),
      h("div", { className: pcls("fld") }, h("label", null, "Poll idle"),
        h("input", { className: pcls("in"), type: "number", min: 250, step: 50, value: cfg.pollMsIdle, size: 6,
          onChange: e => saveCfg({ pollMsIdle: parseInt(e.target.value, 10) || 1500 }) }),
        h("span", { className: pcls("muted") }, "ms (min 250)")),
      h("div", { className: pcls("fld") }, h("label", null, "Poll TX"),
        h("input", { className: pcls("in"), type: "number", min: 100, step: 50, value: cfg.pollMsTx, size: 6,
          onChange: e => saveCfg({ pollMsTx: parseInt(e.target.value, 10) || 300 }) }),
        h("span", { className: pcls("muted") }, "ms (min 100)")),
      h("div", { className: pcls("fld") }, h("label", null, "Peak window"),
        h("input", { className: pcls("in"), type: "number", min: 1000, step: 500, value: cfg.peakWindowMs, size: 6,
          onChange: e => saveCfg({ peakWindowMs: parseInt(e.target.value, 10) || 4000 }) }),
        h("span", { className: pcls("muted") }, "ms — peak-Po hold")),
      h("label", { className: pcls("toggle") },
        h("input", { type: "checkbox", checked: !!cfg.bandFollow, onChange: e => saveCfg({ bandFollow: e.target.checked }) }),
        "Automatic band-follow"),
      h("label", { className: pcls("toggle") },
        h("input", { type: "checkbox", checked: !!cfg.txInhibit, onChange: e => saveCfg({ txInhibit: e.target.checked }) }),
        "Inhibit band change during TX"),
      h("label", { className: pcls("toggle") },
        h("input", { type: "checkbox", checked: !!cfg.autoConnect, onChange: e => saveCfg({ autoConnect: e.target.checked }) }),
        "Auto-connect on start"),
      h("div", { className: pcls("fld") }, h("label", null, "Manual band"),
        h("select", { className: pcls("in"), value: "", onChange: e => { if (e.target.value) act("POST", "/band", { band: e.target.value }); } },
          [h("option", { key: "", value: "" }, "set…")].concat(AMP_BANDS.map(b => h("option", { key: b, value: b }, b))))),
      h("div", { className: pcls("muted"), style: { paddingTop: "4px" } },
        ver ? ("IC-PW2 Bridge v" + (ver.manifestVersion || "?") + " · built " + (ver.built || "?")) : "version …")) : null;

    const logView = showLog ? h("div", { className: pcls("group") },
      h("div", { className: pcls("log") },
        (log.length ? log : [{ ts: "", kind: "info", message: "no events yet" }]).map((e, i) =>
          h("div", { className: pcls("ln"), key: i },
            h("span", { className: pcls("ts") }, e.ts),
            h("span", { className: pcls(e.kind || "info") }, "[" + (e.kind || "info") + "]"),
            h("span", null, e.message))))) : null;

    const modal = confirmPower !== null ? h("div", { className: pcls("modal") },
      h("div", { className: pcls("card") },
        h("div", { className: pcls("ct") }, confirmPower ? "Turn the amplifier ON?" : "Turn the amplifier OFF?"),
        h("div", { className: pcls("muted") }, confirmPower
          ? "This powers up the IC-PW2 over CI-V."
          : "This powers down the IC-PW2. It will stop amplifying until powered back on."),
        h("div", { className: pcls("btns") },
          h("button", { className: pcls("b " + (confirmPower ? "ok" : "danger")),
            onClick: () => { const on = confirmPower; setConfirmPower(null); act("POST", "/power", { on }); } },
            confirmPower ? "Yes, power ON" : "Yes, power OFF"),
          h("button", { className: pcls("b"), onClick: () => setConfirmPower(null) }, "Cancel")))) : null;

    const tuneModal = tuneDialog ? h("div", { className: pcls("modal") },
      h("div", { className: pcls("card") },
        h("div", { className: pcls("ct") }, "Start antenna tuner"),
        h("div", { className: pcls("muted") },
          "This puts the IC-PW2's internal tuner into TUNE mode. The tuner needs a carrier to match against — it does not key your radio."),
        h("div", { className: pcls("muted") },
          "Steps: 1) press Start below to arm the amp tuner, then 2) press the Tune button in Zeus (or key a low-power carrier) to run the tuning cycle."),
        h("div", { className: pcls("btns") },
          h("button", { className: pcls("b acc"),
            onClick: () => { setTuneDialog(false); act("POST", "/tune"); } }, "Start amp tuner"),
          h("button", { className: pcls("b"), onClick: () => setTuneDialog(false) }, "Cancel")))) : null;

    return h("div", { className: ROOT, style: { position: "relative" } },
      header, alarmRow, bandRow, gauges, bars, env,
      powerGroup, ampGroup, inputGroup, antGroup, tools, errBanner, settings, logView, modal, tuneModal);
  };
}

export default function activate(api) {
  const { registerPanel, callBackend } = api;
  ensureStyle();
  registerPanel({ id: PANEL_ID, component: makePanel(callBackend) });
}
