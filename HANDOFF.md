# IC-PW2 Bridge — Handoff & Reference

Zeus community feature (plugin) that controls and monitors an **Icom IC-PW2**
1 kW linear amplifier over CI-V serial, with automatic band-follow driven by
Zeus's radio state.

- **Plugin ID:** `com.kq4wlr.zeus.pw2bridge`
- **Author:** KQ4WLR
- **Current version:** 1.0.0 (first catalog submission)
- **SDK:** ABI 1, minVersion 1.5.0
- **Platform:** Windows (win-x64) first; Linux/Pi possible later (see Porting)
- **License intent:** GPL-2.0-or-later

This document is the durable record. If you return after a gap, publish it, or
hand it to someone else, read this first. It captures not just *what* the code
does but *why* — especially the CI-V quirks that were discovered on real
hardware and would be easy to "fix" back into bugs.

---

## 1. Origin

Ported from a Python/Tkinter desktop app (`bridge.py`, ~7000 lines) that bridged
Thetis/Flex to an IC-PW2 over three links (TCI WebSocket, CI-V serial, CAT TCP).
The Zeus plugin keeps only the **CI-V amp control**; it gets radio frequency
from Zeus directly instead of TCI, and it dropped the CAT-TCP "drive cap"
feature (the SDK has no set-drive, and it isn't needed).

The heavy, risky work — the CI-V protocol — was ported to C# and verified
command-by-command against the real amp using the console harness (see §6).

---

## 2. Architecture

```
src/Pw2Bridge/
  Pw2CivProtocol.cs   Pure CI-V byte logic: framing, echo-safe reply parsing,
                      BCD decoders, meter calibration curves. No I/O, no Zeus.
                      Unit-testable in isolation.
  BandPlan.cs         Frequency -> band-name mapping incl. the 60m FCC split.
  Pw2CivSerial.cs     SerialPort wrapper. All the typed CI-V commands
                      (read meters / set band / OPER-STBY / input / antenna /
                      tuner / power). Depends on Pw2CivProtocol + BandPlan.
  Pw2BridgePlugin.cs  The Zeus entrypoint (IZeusPlugin + IBackendPlugin).
                      Owns the serial link, subscribes to radio state for
                      band-follow, runs the meter poll loop, enforces the
                      safeties, and exposes HTTP endpoints under
                      /api/plugins/com.kq4wlr.zeus.pw2bridge/.
  ui/pw2bridge.es.js  The React panel (plain ESM, no build step — uses
                      React.createElement, not JSX). Talks to the backend via
                      callBackend. Renders gauges/controls; Zeus supplies CSS
                      tokens.
                      Every secondary class goes through pcls(), which
                      emits `com-kq4wlr-pw2bridge__<name>` (CONTRIBUTING §4
                      prefix rule). Don't name a local `pcls`; the gauge
                      code already uses `cx`/`cy` for SVG coordinates.
  plugin.json         Manifest (capabilities, entrypoint, UI panel slot).
  Zeus.Community.Pw2Bridge.csproj   Plugin project. Bundles System.IO.Ports.
  build-package.ps1   Catalog template packager, copied UNMODIFIED from
                      zeus-community-features/templates/hello-world.
  README.md           Operator guide (packaged into the ZIP).

tools/Pw2CivHarness/  Standalone console app. Shares the three protocol/serial
                      source files. Two modes: `selftest` (no hardware,
                      asserts against the CI-V guide) and `serial` (interactive
                      session with the real amp). This is how the protocol was
                      proven and how to debug the wire.

sdk/                  Vendored copy of the Zeus SDK contracts (required so the
                      feature builds standalone). Do not edit.
Directory.Build.props Copied from the SDK; sets TreatWarningsAsErrors=true, so
                      the build is strict — warnings fail it.
```

**Data flow:** Zeus radio state -> `OnFrequencyChanged` -> band-follow ->
`Pw2CivSerial.SetBand` -> amp. Meter poll loop -> `Pw2CivSerial.Read*` ->
`_telemetry` -> `GET /status` -> React panel. UI buttons -> `POST` endpoints ->
`Pw2CivSerial` writes -> amp.

**Threading:** the poll loop reads serial *outside* a lock, then commits values
under `_stateGate` briefly. Serial round-trips can take up to ~500 ms; never
hold the state lock across a serial call or `/status` stalls.

---

## 3. CI-V protocol notes (hardware-verified — read before touching)

All reconciled against the *IC-PW2 CI-V Reference Guide* (A7736-5EX-2, Aug 2024)
**and** confirmed on the real amp. The gotchas below cost real debugging time;
don't undo them.

- **Frame:** `FE FE <pw2> E0 <cmd> [sub] [data] FD` out; reply has dest/src
  swapped. ACK = `FE FE E0 <pw2> FB FD`, NAK = `FA`. Default PW2 addr `0xAA`,
  9600 8N1. The USB-serial cable ties RTS->CTS (no flow control) — the code
  asserts DTR/RTS and uses `Handshake.None`.

- **Echo:** the amp echoes our outgoing packet before replying. `FindReply`
  rejects the echo by requiring dest=controller (E0) and src=pw2.

- **Meters (cmd `15`):** Po=`15 11`, SWR=`15 12`, ALC=`15 13`, Vd=`15 15`,
  Id=`15 16`. Calibration curves are piecewise per the guide's cal points.
  **ALC scaling fix:** the original Python read ALC but never scaled it; the
  guide says full scale is raw 120 (not 255), so `RawToAlcPercent` = raw/120.

- **Temperature (`1A 0E`):** 3 bytes, BCD, with a sign byte. Bench example
  `03 29 00` = +32.9 C. Verified.

- **Band set (`1A 03 [rfInput][bandCode]`):** band codes 00-09 = 1.8-50 MHz.
  **60m has no band code** — deliberately absent from `BandPlan.BandCode`.

- **RF input (`1A 00`):** read returns 00=INPUT1 / 01=INPUT2. Verified it
  tracks the physical selection. This is the *reliable* "which input" source
  (NOT `1A 06`).

- **Antenna (`1A 06 [rfInput][ant]`):** ant 00-05 = ANT1-6.
  **CRITICAL:** an antenna write is NAKed unless it targets the input the amp
  is *currently operating on*. The plugin reads the live input via `1A 00`
  before every antenna (and band) write and targets it. Early code hardcoded
  input 0 and every `setant` NAKed — that was the bug, not the amp.

- **Antenna names (`1A 05 00 <n>`, n hex 59/62/65/68/71/74):** reply is
  `... 1A 05 00 <n> <ASCII name...> FD`.
  **CRITICAL:** the names read as empty for a long time. Root cause was NOT the
  sub-command (those hex values are correct; `0x3B` etc. NAK). It was
  `SendRaw`'s read loop stopping at the *echo's* FD before the longer name
  reply fully arrived. Fix: once any FD beyond the echoed packet is seen, pause
  ~25 ms and drain all remaining bytes. This also made every multi-read
  steadier. Verified: `0x59`="(Name 1)", `0x68`="Dummy Load".

- **Tuner (`1C 01`):** 00=off, 01=on(in-line), 02=start tune cycle. Starting a
  tune only arms the amp's tuner; the operator still supplies the carrier from
  the radio. The plugin never keys TX.

- **Protection (`1A 0C`):** 00=None,01=TEMP,02=ALC,03=POWER,04=BAND,
  05=POWER SUPPLY. Read-only; the amp handles the TX-stop itself.

- **Main power (`18 01`/`18 00`):** power-on sends a wake-up preamble then the
  command, with retries (the amp CPU can miss the first framed byte when off).

- **Read-only commands (never send as writes):** the guide's `*2` list —
  `03, 15, 19 00, 1A 02, 1A 0B, 1A 0C, 1A 0E, 1A 0F, 1A 11, 1C 00, 1C 03`.
  We only ever read these.

---

## 4. Radio state / band-follow

- Requires the `ReadRadioState` capability. Zeus provides
  `IPluginContext.Radio` (an `IRadioStateReader`) with `FrequencyHz`, `Band`,
  `Mox` + change events.
- **History:** on early engine builds `context.Radio` was null despite the
  capability being granted — the shipping "Zeus Link" runtime never registered
  `IRadioStateReader`. Reported to maintainers; fixed engine-side (PR #1992,
  shipped in the WDSP 2.10 release: "Plugins can read your current radio state
  through Zeus Link"). No plugin change was needed — the deferred-attach code
  (`AttachRadio` re-checked each poll) picked it up automatically.
- **Do NOT** try to read frequency any other way. CONTRIBUTING forbids calling
  Zeus's own endpoints or reaching around the SDK. TCI would also be a
  reach-around. If `Radio` is ever null again, it's a host issue → open an
  issue, don't work around it.
- **Band-follow logic** (`OnFrequencyChanged`): freq -> band -> if amp-supported
  and changed, forward it; **TX-inhibit** defers the change while `Mox` is true
  and applies the pending band on TX-drop.

---

## 5. Safety logic (deliberate — don't weaken without thought)

- **Never key TX / never touch PureSignal.** Hard rule from CONTRIBUTING.
- **60m low-power sub-band (5351.5-5366.5 kHz, `60m-new`):** actively forces
  STBY on entry, blocks OPER while there, re-asserts STBY if the amp drifts to
  OPER (e.g. front-panel press), and restores the prior state on exit. Normal
  60m channels are left alone.
- **Overheat auto-STBY:** configurable threshold (default **120 F**). At/above
  it, force STBY and **latch** — OPER stays blocked until the operator manually
  presses STBY (does NOT auto-restore on cool-down, by request). Prominent
  alarm banner.
- **Protection alarm:** when `1A 0C` reports a fault, show a prominent banner.
  The amp stops TX itself; the plugin doesn't duplicate that.
- **High SWR:** left to the amp's own foldback (display only — arc goes
  green<=2 / yellow 2-3 / red>=3).
- **Stale-data failsafe:** if the amp goes silent for ~5 s (dead poll cycles),
  drop the link -> failsafe UI (grey, "—"); if auto-connect is on, retry every
  ~3 s and resume when reads succeed.
- **Send band on connect:** after a ~1.5 s settle delay (serial/amp needs to
  settle), forward the current band — on fresh connect and on Zeus open.

**Open item for international publish:** US operators won't hit a band change
mid-transmission the way some other regions might. If publishing widely, review
the TX-inhibit / pending-band path for that edge case.

---

## 6. Build, test, package

**Toolchain:** .NET 10 SDK. (Publish/catalog validation also needs PowerShell 7
and Node.js.) I use `node --check ui/pw2bridge.es.js` to validate the UI JS.

**Harness (prove the protocol / debug the wire — no Zeus needed):**
```
cd tools/Pw2CivHarness   # or: dotnet run --project tools/Pw2CivHarness -- selftest
dotnet run -- selftest              # assertions vs the CI-V guide, no hardware
dotnet run -- ports                 # list COM ports
dotnet run -- serial COM10 9600 AA  # interactive session with the amp
```
Harness commands: `meters`, `po/swr/alc/vd/id`, `temp/hum/prot/tuner`, `amp`,
`ant`, `setant`, `antname`, `antnames`, `input`, `setinput`, `band <name>`,
`oper`/`stby`, `pwron`/`pwroff`, `clearprot`, and `raw <hex...>` for probing.

**Build + package the plugin (Windows, PowerShell 7):**
```
pwsh src/Pw2Bridge/build-package.ps1 -ManagedDependency System.IO.Ports.dll
```
Output: `artifacts/com.kq4wlr.zeus.pw2bridge/com.kq4wlr.zeus.pw2bridge-<version>.zip`
plus a `.sha256` file. The ZIP root holds `plugin.json`,
`Zeus.Community.Pw2Bridge.dll`, `Zeus.Community.Pw2Bridge.deps.json`,
`System.IO.Ports.dll`, `ui/pw2bridge.es.js`, `README.md`, `LICENSE`, and
`THIRD_PARTY_NOTICES.md`. The csproj builds for `win-x64` with
`AppendRuntimeIdentifierToOutputPath=false` so the *Windows* System.IO.Ports
implementation (not the platform-neutral stub that throws
PlatformNotSupportedException) lands flat in `bin/Release/net10.0`, where the
unmodified template script expects it. Do not edit `build-package.ps1`;
CONTRIBUTING forbids bypassing its checks.

The old `package.bat` (dotnet publish + Compress-Archive) is retired: it
omitted LICENSE/notices and used a non-catalog ZIP name.

**Install locally in Zeus:** Features -> Community -> Install local feature ->
ZIP FILE -> pick the ZIP from `artifacts/` -> Install ZIP. Hard-refresh (Ctrl+F5)
after reinstalling so the new UI loads. Remove the old copy first.

**Tabs/docking:** the panel uses the standard panel system (no manifest change
needed). In current Zeus, dragging a panel *onto* another merges them into a
tabbed "Multi Panel"; dragging loosely on top just overlaps. To force a tab,
add an empty Multi Panel from the + picker and drop the IC-PW2 into it.

---

## 7. Publishing to the community catalog (when ready)

From CONTRIBUTING.md (current SDK). The catalog PR changes only `registry.json`;
the ZIP is stored as an immutable Zeus-SDR release asset created by a maintainer.

1. **Own public source repo.** Keep the full source, project files, lockfiles,
   package script, `LICENSE`, notices, and operator docs public. Tag the exact
   source for the release so reviewers can reproduce it.
2. **Bump the version** in `plugin.json` (and use the same everywhere).
3. **Clean-checkout build + package** on Windows. Produce
   `com.kq4wlr.zeus.pw2bridge-<version>.zip`.
4. **Compute the ZIP's SHA-256** (64 lowercase hex).
5. **Registry entry** (in your PR) uses this shape:
   ```json
   {
     "id": "com.kq4wlr.zeus.pw2bridge",
     "channel": "community",
     "name": "IC-PW2 Bridge",
     "description": "Control and monitoring for the Icom IC-PW2 amplifier.",
     "author": "KQ4WLR",
     "license": "GPL-2.0-or-later",
     "homepage": "https://github.com/AlarmGuyPro/zeus-pw-2-bridge",
     "categories": ["amplifiers"],
     "verified": false,
     "versions": [{
       "version": "<version>",
       "sdkAbi": 1,
       "sdkMinVersion": "1.5.0",
       "platforms": ["win-x64"],
       "downloadUrl": "https://github.com/Zeus-SDR/zeus-community-features/releases/download/community-com.kq4wlr.zeus.pw2bridge-v<version>/com.kq4wlr.zeus.pw2bridge-<version>.zip",
       "sha256": "<64-hex>"
     }]
   }
   ```
   Use `platforms: ["win-x64"]` (not `"any"`) because the ZIP bundles the
   native `System.IO.Ports` for Windows. Put your own intake URL in the PR
   template; the Zeus-SDR downloadUrl 404s until custody is completed.
6. **Run the required local checks** (need PowerShell 7 + Node.js), from the
   catalog repo root — schema-validate `registry.json` and the manifest, run
   the SDK-boundary and package validators (`tools/validate-*.ps1`,
   `validate-package.ps1` against your ZIP). See CONTRIBUTING §7 for the exact
   commands.
7. **Open the PR** against `zeus-community-features` `main`.

Validation gate: schema match, download fetched + SHA-256 checked, embedded
manifest verified, ABI compatibility, archive-safety. Nothing lands unverified.

---

## 8. Known-good state / loose ends

- Everything in §3 is hardware-verified on the author's amp.
- Band-follow works (post engine fix).
- ANT6 reads blank on the author's amp because it's genuinely unnamed — the
  UI falls back to "ANT n" for blank names. Correct behavior.
- Overheat default 120 F is a starting guess; tune after real operating.
- Not yet done: Linux/Pi packaging; publishing; any changes from the author's
  multi-day live test.

## 9. Porting to Linux / Raspberry Pi (future)

Code is portable; it's a packaging exercise. Add the Linux `System.IO.Ports`
native runtimes to the package, expose a serial-device name setting
(`/dev/ttyUSB0` vs `COM10`), set `platforms` accordingly, and rebuild/package
per platform per CONTRIBUTING §2.7.
