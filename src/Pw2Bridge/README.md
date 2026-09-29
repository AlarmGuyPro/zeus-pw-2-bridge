# IC-PW2 Bridge — Zeus SDR community feature

Control and monitor an **Icom IC-PW2** 1 kW linear amplifier from inside Zeus,
over the amplifier's CI-V serial link. Adds an IC-PW2 panel with live metering,
amplifier control, automatic band-follow, and a set of safety interlocks.

- **Author:** KQ4WLR
- **Version:** 1.1.1
- **License:** GPL-2.0-or-later
- **Platforms:** Windows x64, macOS x64/arm64, Linux x64/arm64 and Raspberry Pi (arm/arm64)
- **Requires:** Zeus with plugin radio-state support (WDSP 2.10 release or later),
  and a USB-serial connection to the amplifier.

---

## What it does

- **Metering:** Forward Power, SWR, ALC, drain voltage (Vd) and current (Id),
  temperature, humidity, and protection status — shown as Zeus-style gauges and
  bars, with a rolling **peak power** readout.
- **Amplifier control:** OPER / STBY, main power on/off (with confirmation),
  RF input 1/2 selection, antenna 1–6 selection (with the amp's own antenna
  names shown as tooltips), and the internal antenna tuner (enable/disable and
  start-tune).
- **Automatic band-follow:** the amp follows the radio's band automatically as
  you change frequency in Zeus, with a hold-off so the band isn't switched while
  you're transmitting.
- **Safety interlocks:** see the Safety section below.

---

## Connecting the amplifier

The IC-PW2's CI-V is a **serial** interface. Connect a USB-to-serial cable from
your PC to the amplifier's **[REMOTE AUX]** jack (a 3.5 mm mono plug; wire per
the IC-PW2 CI-V Reference Guide — RTS tied to CTS, no flow control). The plugin
talks to that serial port.

In the panel's **Settings**, select the serial port, baud, and CI-V address (default
AA), then Connect. These are remembered.

Port names depend on the OS: `COM10` on Windows, `/dev/cu.usbserial-*` on
macOS, and `/dev/ttyUSB0` or `/dev/ttyACM0` on Linux. Select your adapter in
the existing port picker; a saved Windows COM name must be changed when moving
settings to another OS. On Linux, the account running Zeus needs access to the
serial device (commonly the `dialout` or `uucp` group, depending on the distro).
Install the adapter manufacturer's driver if the device does not appear.
The ZIP includes the required serial libraries; amplifier operation on macOS,
Linux and Raspberry Pi still needs confirmation with real hardware.

### Baud rate — set it explicitly to 19200 for best speed

The IC-PW2 supports 4800, 9600, 19200, and **Auto** for the REMOTE AUX CI-V
baud. Two things to know:

- **Use 19200 for the fastest metering.** On the bench, moving from 9600 to
  19200 cut each meter read from roughly 100 ms+ to about **30 ms** — a ~3x
  speedup that makes Power/SWR noticeably more responsive during transmit. Set
  the amp's REMOTE AUX CI-V baud to **19200** (Set menu → Connectors → CI-V) and
  set the plugin's baud to 19200 to match.
- **Avoid "Auto".** The amp's Auto baud tends to lock to 9600 and will not accept
  a faster rate from the plugin — you'll connect but get no readings (blank
  meters). Always set both the **amp** and the **plugin** to the *same explicit*
  baud. If in doubt, 9600 explicit works everywhere; 19200 explicit is faster.

Changing the baud (or port) in Settings now reconnects automatically so the new
rate takes effect immediately.

> **Why serial and not the amp's Ethernet port?** The IC-PW2's LAN port uses
> Icom's proprietary RS-PW2 protocol with an authenticated, encrypted login that
> is not publicly documented and has not been reverse-engineered by any
> open-source project. Serial CI-V is the open, supported control path, so this
> plugin is serial-only by design.

---

## Understanding the meters — please read

The metering here is genuinely useful, but it has real limitations that come
from *how* the amplifier exposes data. Understanding them will set the right
expectations.

**The meters are polled, not streamed.** The plugin repeatedly asks the amp for
each meter value over the CI-V serial link (9600 baud). It does not receive a
continuous high-rate stream. Each reading is a request/response round trip, and
the plugin reads the meters one at a time. In practice this gives a smooth,
responsive display, but it is fundamentally a **sampled** view, not a
sample-accurate RF measurement.

**Power/SWR can look "slow off the mark" when you key up.** Two things cause
this:
1. The amplifier's RF output genuinely *ramps* over the first fraction of a
   second after you key — so the first readings really are lower than the
   steady-state value. No software can change that; it's the amp.
2. Between the moment you key and the next poll, a beat passes before the
   display catches up. The plugin polls faster during transmit (default 150 ms)
   to track the ramp closely, and running the link at **19200 baud** (see above)
   roughly triples read speed, which tightens this noticeably.

**Peak power is a rolling, sampled peak — not a true peak-envelope.** The small
"PK" number above the Forward Power reading shows the highest power *seen among
the samples* over a rolling window (default 4 seconds, adjustable). Because it's
sampled, it can miss the true instantaneous RF peak that occurs between polls.
It is excellent for understanding how hard you're driving the amp and for
catching your working maximum, but it is **not** a substitute for the
amplifier's own hardware peak meter. Treat it as "the strongest reading the
plugin caught recently," not "the exact RF peak."

**SWR is shown as an instantaneous sampled value** (green up to 2.0, amber
2.0–3.0, red above 3.0). It is not peak-held, deliberately — a peak-hold SWR
reads erratically while tuning. The amplifier performs its own SWR protection
and foldback independently of this display; the plugin does not and should not
be relied on as SWR protection.

**Bottom line:** use these meters for operating awareness and drive-setting, and
rely on the **amplifier's own protection and metering** for anything
safety-critical. The plugin reflects what the amp reports, as fast as the serial
link allows — no more, no less.

---

## Safety interlocks

The plugin never keys your transmitter and never touches PureSignal. Its safety
features act only on the amplifier:

- **Automatic actions only move toward STBY.** Nothing the plugin does on its
  own ever switches the amp to OPER; only your OPER press does.
- **60 m low-power sub-band (5351.5–5366.5 kHz):** to respect the FCC power
  limit there, the plugin forces the amp to STBY on entering that sub-band and
  blocks OPER while you're in it. This works whether or not band-follow is on.
  When you tune out of the sub-band the amp stays in STBY; press OPER when
  you're ready.
- **Overheat auto-STBY:** if the amp's temperature reaches a configurable limit
  (default 120 °F), the plugin forces STBY and *latches* it — OPER stays blocked
  until you manually press STBY, so a hot amp can't silently resume. The
  temperature is checked about once a second during transmit as well. If the
  limit is reached mid-transmission, the latch is set at once and STBY is sent
  as soon as you unkey (the amp won't switch to STBY under RF). The amp's own
  temperature protection still acts during transmit regardless.
- **Protection alarm:** if the amp reports a protection fault, a prominent alarm
  is shown. (The amp stops transmitting on its own; the plugin surfaces it.)
- **No relay switching under RF:** band, RF input, antenna, tuner
  in-line/bypass, OPER, and main power on/off are locked while the radio is
  transmitting. The IC-PW2 itself won't leave OPER while RF is present, so a
  STBY press during transmit is held and sent the moment you unkey (the panel
  shows "STBY WHEN TX DROPS"). With
  "Inhibit band change during TX" on (the default), band-follow also waits
  until TX drops before changing band; if you turn band-follow off during the
  transmission, that deferred change is dropped.
- **Stale-data failsafe:** if the amp stops responding, the panel drops to a
  clear "disconnected" state rather than showing frozen values, and attempts to
  reconnect automatically.
- **Default to STBY on connect:** by default the amp is placed in STBY when the
  plugin gains control, including after an automatic reconnect (configurable).

The amplifier's own protection systems remain your primary safety layer. These
interlocks are an added convenience, not a replacement for the amp's protection.

---

## Installing

**From the catalog (once listed):** in Zeus open **Features → Community**, find
**IC-PW2 Bridge**, and install it.

**From a downloaded or locally built ZIP:** **Features → Community → Install
local feature → ZIP FILE**, choose `com.kq4wlr.zeus.pw2bridge-<version>.zip`,
Install. Hard-refresh (Ctrl+F5) after installing or updating so the new panel
code loads. Remove an older copy first.

Then add the **IC-PW2** panel to your workspace (drag it onto another panel to
make it a tab, or add an empty Multi Panel and drop it in).

The bottom of the Settings panel shows the running version and build time so you
can confirm which build is loaded.

## Known limitations

- **Serial only** (see the note above about the LAN port).
- Hardware serial operation must be verified with your adapter on each OS.
- Metering is sampled/polled as described above.
- A drive-level interlock (auto-STBY if the radio's drive exceeds a set limit) is
  planned but depends on Zeus exposing drive level to plugins; it is not yet
  available.

## Feedback

Issues and suggestions welcome at https://github.com/AlarmGuyPro/zeus-pw-2-bridge/issues.
