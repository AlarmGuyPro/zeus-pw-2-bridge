// SPDX-License-Identifier: GPL-2.0-or-later
//
// IC-PW2 Bridge — Zeus community feature backend.
//
// Wraps the hardware-proven Pw2CivSerial layer in the Zeus plugin contract:
//   • auto band-follow driven by IRadioStateReader.FrequencyChanged, with
//     TX-inhibit (defer band changes while Mox is engaged), matching the
//     behaviour of the original Python bridge;
//   • a background meter-poll loop with a user-selectable cadence (idle vs.
//     TX rate), persisted via IPluginSettings;
//   • an in-memory ring buffer of recent events, surfaced at GET log for the
//     UI — the Zeus-native replacement for the bridge's file logger;
//   • HTTP endpoints under /api/plugins/<id>/ for the React panel.
//
// This backend never keys the transmitter. Band/OPER/STBY are the only writes,
// and OPER/STBY toggle the amp circuit only (front-panel AMP key equivalent).

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.Pw2Bridge;

public sealed class Pw2BridgePlugin : IZeusPlugin, IBackendPlugin
{
    // Human-readable version; keep in sync with plugin.json "version".
    private const string ManifestVersion = "1.0.0";

    private IPluginContext? _ctx;
    private ILogger? _log;
    private Pw2CivSerial? _amp;

    // Live config (persisted). Defaults mirror the original bridge.
    private Settings _cfg = new();

    // State shared with the poll loop and endpoints.
    private readonly object _stateGate = new();
    private Telemetry _telemetry = new();
    private string? _detectedBand;     // last band Zeus told us about
    private string? _pendingBand;      // band deferred because TX was active
    private string _lastForwardedBand = "";
    private volatile bool _tx;

    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private readonly RingLog _events = new(capacity: 200);

    // ── lifecycle ────────────────────────────────────────────────────────────

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _log = context.Logger;
        _amp = new Pw2CivSerial(context.Logger);

        _cfg = await LoadSettingsAsync(context.Settings, ct);
        _amp.SetPw2Addr(Convert.ToInt32(_cfg.Pw2AddrHex, 16));

        // Log exactly what the host granted, to distinguish "not declared" from
        // "declared but host didn't expose it" when diagnosing band-follow.
        Note("info", $"Granted capabilities: {context.GrantedCapabilities}. Radio={(context.Radio is null ? "null" : "present")}, RadioController={(context.RadioController is null ? "null" : "present")}.");

        // Subscribe to Zeus radio state for band-follow + TX inhibit.
        if (context.Radio is { } radio)
        {
            AttachRadio(radio);
        }
        else
        {
            Note("warn", "Radio state unavailable at init (context.Radio is null). Will keep checking — band-follow starts once the host exposes radio state.");
        }

        if (_cfg.AutoConnect)
            TryConnect();

        StartPollLoop();
        Note("info", $"Initialized. Auto band-follow {(_cfg.BandFollow ? "ON" : "OFF")}.");
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (_ctx?.Radio is { } radio)
        {
            radio.FrequencyChanged -= OnFrequencyChanged;
            radio.MoxChanged -= OnMoxChanged;
        }
        await StopPollLoopAsync();
        _amp?.Dispose();
        _amp = null;
        Note("info", "Stopped.");
    }

    // ── radio-state handlers (band-follow) ───────────────────────────────────

    private long _lastLoggedHz;

    private bool _radioAttached;

    private void AttachRadio(IRadioStateReader radio)
    {
        if (_radioAttached) return;
        radio.FrequencyChanged += OnFrequencyChanged;
        radio.MoxChanged += OnMoxChanged;
        _tx = radio.Mox;
        _radioAttached = true;
        Note("info", $"Radio interface attached. Initial freq {radio.FrequencyHz} Hz, band {BandPlan.Display(BandPlan.FromHz(radio.FrequencyHz)) ?? "?"}, MOX {radio.Mox}.");
        OnFrequencyChanged(radio.FrequencyHz); // prime with current band
    }

    private bool _forced60mStby;               // holding the amp in STBY for 60M-NEW
    private string _stateBefore60m = "STBY";   // amp state to restore when leaving
    private bool _overheatLatched;             // holding STBY due to overheat (manual clear)
    private readonly string[] _antNames = new string[6]; // cached ANT 1..6 names

    private void OnFrequencyChanged(long hz)
    {
        var band = BandPlan.FromHz(hz);
        lock (_stateGate) _detectedBand = band;

        // Log frequency events (throttled to real changes) so band-follow is visible.
        if (Math.Abs(hz - _lastLoggedHz) > 500)
        {
            _lastLoggedHz = hz;
            Note("info", $"Radio freq → {hz} Hz ({BandPlan.Display(band) ?? "out-of-band"}).");
        }

        if (!_cfg.BandFollow || band is null) return;

        // 60m low-power sub-band (5351.5–5366.5 kHz): FCC power limits mean the
        // amp must not amplify here. Actively force STBY on entry and hold it,
        // then restore the prior state on exit — not just "skip forwarding."
        if (BandPlan.Is60mLowPower(band))
        {
            if (!_forced60mStby)
            {
                var st = _amp is { IsConnected: true } ? _amp.ReadAmpCircuit() : null;
                _stateBefore60m = st == 1 ? "OPER" : "STBY";
                _forced60mStby = true;
            }
            Force60mStby();
            return;
        }

        // Leaving 60M-NEW → restore the amp to its pre-60m state.
        if (_forced60mStby)
        {
            _forced60mStby = false;
            if (_stateBefore60m == "OPER" && _amp is { IsConnected: true })
            {
                var (ok, msg) = _amp.SetAmpCircuit(true);
                if (ok) lock (_stateGate) _telemetry.AmpState = "OPER";
                Note(ok ? "civ" : "warn", $"Left 60M-NEW — restored OPER: {(ok ? "OK" : "FAIL")} {msg}");
            }
            else
            {
                Note("bridge", "Left 60M-NEW — amp remains STBY.");
            }
        }

        if (!BandPlan.IsAmpSupported(band))
        {
            // Normal 60m channels / out-of-amp-range: no CI-V band code, and no
            // low-power restriction — leave the amp as-is.
            return;
        }
        if (band == _lastForwardedBand) return;

        if (_tx && _cfg.TxInhibit)
        {
            lock (_stateGate) _pendingBand = band;
            Note("bridge", $"Deferring band → {band} (TX active).");
            return;
        }
        ForwardBand(band);
    }

    private void Force60mStby()
    {
        if (_amp is not { IsConnected: true }) return;
        if (_amp.ReadAmpCircuit() == 1) // currently OPER — drop it
        {
            var (ok, msg) = _amp.SetAmpCircuit(false);
            Note(ok ? "civ" : "warn", $"60M-NEW: forcing STBY (FCC power limit): {(ok ? "OK" : "FAIL")} {msg}");
        }
        lock (_stateGate) _telemetry.AmpState = "STBY";
        _lastForwardedBand = "";
    }

    private void OnMoxChanged(bool tx)
    {
        bool rising = tx && !_tx;
        _tx = tx;

        // On TX drop, apply any band change we deferred.
        if (!tx)
        {
            string? pending;
            lock (_stateGate) { pending = _pendingBand; _pendingBand = null; }
            if (pending is not null)
            {
                Note("bridge", $"TX dropped — applying deferred band {pending}.");
                ForwardBand(pending);
            }
        }

        // On TX rising edge, poke a fast poll so SWR/Po update quickly.
        if (rising) _pollWake.Set();
    }

    private void ForwardBand(string band)
    {
        if (_amp is not { IsConnected: true })
        {
            Note("bridge", $"Band {band} not forwarded — amp not connected.");
            return;
        }
        int live = _amp.ReadRfInput() ?? _cfg.RfInput;
        var (ok, msg) = _amp.SetBand(band, live);
        _lastForwardedBand = ok ? band : _lastForwardedBand;
        Note(ok ? "civ" : "warn", $"Band → {BandPlan.Display(band)}: {(ok ? "OK" : "FAIL")} {msg}");
    }

    // ── meter poll loop (selectable cadence) ─────────────────────────────────

    private readonly ManualResetEventSlim _pollWake = new(false);

    private void StartPollLoop()
    {
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_pollCts.Token));
    }

    private async Task StopPollLoopAsync()
    {
        try { _pollCts?.Cancel(); } catch { }
        _pollWake.Set();
        if (_pollTask is not null) { try { await _pollTask; } catch { } }
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private int _pollTick;
    private int _powerMiss;
    private int _staleMiss;
    private int _recoverTick;
    private int _prevAmpState = -1;      // last seen amp circuit (0/1), -1 = unknown
    private bool _prevReachable;         // was the amp reachable last cycle
    // Rolling peak-Po tracker: timestamped samples, pruned to the window.
    private readonly Queue<(DateTime t, int w)> _poSamples = new();
    private readonly object _peakGate = new();

    private void RecordPoSample(int watts)
    {
        var now = DateTime.UtcNow;
        lock (_peakGate)
        {
            _poSamples.Enqueue((now, watts));
            var cutoff = now.AddMilliseconds(-_cfg.PeakWindowMs);
            while (_poSamples.Count > 0 && _poSamples.Peek().t < cutoff)
                _poSamples.Dequeue();
        }
    }

    private int ComputePeakPo()
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-_cfg.PeakWindowMs);
        int peak = 0;
        lock (_peakGate)
        {
            foreach (var (t, w) in _poSamples)
                if (t >= cutoff && w > peak) peak = w;
        }
        return peak;
    }


    private void PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // If the host didn't expose radio state at init, keep watching for it
            // (it can appear once the operator connects a radio in Zeus).
            if (!_radioAttached && _ctx?.Radio is { } lateRadio)
                AttachRadio(lateRadio);

            int interval = _tx ? _cfg.PollMsTx : _cfg.PollMsIdle;
            var amp = _amp;

            if (amp is { IsConnected: true })
            {
                try
                {
                    // Read OUTSIDE the state lock (each CI-V round-trip can take up
                    // During TX we want Po/SWR/ALC as fast as the serial link
                    // allows — nothing else. Each extra CI-V round-trip (temp,
                    // humidity, antenna, tuner, ...) at 9600 baud steals tens of ms
                    // from the meters that actually matter mid-transmission. So on
                    // TX, read only the three power meters and skip the rest; the
                    // slow/medium reads resume the moment TX drops.
                    bool txNow = _tx;
                    bool slow = !txNow && _pollTick % 4 == 0;
                    bool med  = !txNow && _pollTick % 2 == 0;

                    int? po = amp.ReadPoWatts();
                    double? swr = amp.ReadSwr();
                    int? alc = amp.ReadAlcPercent();
                    double? vd = med ? amp.ReadVolts() : null;
                    double? id = med ? amp.ReadAmps() : null;
                    int? ampSt = slow ? amp.ReadAmpCircuit() : null;
                    string? prot = slow ? amp.ReadProtection() : null;
                    int? hum = slow ? amp.ReadHumidity() : null;
                    double? tempC = slow ? amp.ReadTemperatureC() : null;
                    int? rfIn = slow ? amp.ReadRfInput() : null;
                    int? ant = null;
                    string? antName = null;
                    if (slow && rfIn.HasValue)
                    {
                        ant = amp.ReadAntennaForInput(rfIn.Value);
                        if (ant.HasValue) antName = amp.ReadAntennaName(ant.Value);
                    }
                    int? tuner = slow ? amp.ReadTunerState() : null;

                    // Stale-data watchdog: a cycle where nothing came back at all
                    // means the link is dead even though the port is still "open".
                    bool gotAnything = po.HasValue || swr.HasValue || alc.HasValue
                        || vd.HasValue || id.HasValue || ampSt.HasValue || tempC.HasValue || rfIn.HasValue;
                    if (gotAnything) _staleMiss = 0; else _staleMiss++;

                    // On a STBY→OPER transition, refresh the antenna names (cheap,
                    // event-driven; names can change if the operator re-labels).
                    if (ampSt == 1 && _prevAmpState == 0)
                        ReadAllAntennaNames();
                    if (ampSt.HasValue) _prevAmpState = ampSt.Value;

                    // "always" mode: if the amp just became reachable (front-panel
                    // power-on), force STBY. ("connect" mode handles the link-gain
                    // case in ApplyDefaultStbyOnConnect; "off" does nothing.)
                    bool reachableNow = ampSt.HasValue || tempC.HasValue || rfIn.HasValue;
                    if (_cfg.DefaultStby == "always" && reachableNow && !_prevReachable
                        && !_forced60mStby && !_overheatLatched && ampSt == 1)
                    {
                        var (ok, _) = amp.SetAmpCircuit(false);
                        Note(ok ? "civ" : "warn", "Default-to-STBY (always): amp powered on → STBY.");
                    }
                    _prevReachable = reachableNow;

                    // Enforce 60M-NEW STBY if the amp drifted to OPER (front panel).
                    if (_forced60mStby && ampSt == 1)
                    {
                        var (ok, _) = amp.SetAmpCircuit(false);
                        Note(ok ? "civ" : "warn", "60M-NEW: re-asserting STBY (amp was OPER).");
                    }

                    // Overheat protection: if temperature reaches the configured max,
                    // force STBY and latch it. Stays in STBY until the operator
                    // manually re-enables (does NOT auto-restore on cool-down).
                    if (tempC.HasValue)
                    {
                        double tempForCompare = _cfg.TempUnit == "C" ? tempC.Value : Pw2Civ.CToF(tempC.Value);
                        if (tempForCompare >= _cfg.MaxTemp)
                        {
                            if (!_overheatLatched)
                            {
                                _overheatLatched = true;
                                if (ampSt == 1) amp.SetAmpCircuit(false);
                                Note("warn", $"OVERHEAT {tempForCompare:F1}°{_cfg.TempUnit} ≥ {_cfg.MaxTemp}°{_cfg.TempUnit} — forced STBY, latched.");
                            }
                            else if (ampSt == 1)
                            {
                                amp.SetAmpCircuit(false); // hold STBY while latched
                            }
                        }
                    }

                    lock (_stateGate)
                    {
                        var t = _telemetry;
                        t.Connected = true;
                        if (po.HasValue) { t.Po = po.Value; RecordPoSample(po.Value); }
                        t.PeakPo = ComputePeakPo();
                        if (swr.HasValue) t.Swr = swr.Value;
                        if (alc.HasValue) t.AlcPercent = alc.Value;
                        if (vd.HasValue) t.Vd = vd.Value;
                        if (id.HasValue) t.Id = id.Value;
                        if (ampSt.HasValue) t.AmpState = AmpStateName(ampSt);
                        if (prot != null) t.Protection = prot;
                        if (hum.HasValue) t.Humidity = hum.Value;
                        if (tempC.HasValue) { t.TempC = tempC; t.TempF = Pw2Civ.CToF(tempC.Value); }
                        if (rfIn.HasValue) t.ActiveInput = rfIn.Value;
                        if (ant.HasValue) t.ActiveAntenna = ant.Value;
                        if (antName != null) t.ActiveAntennaName = antName;
                        if (tuner.HasValue) t.TunerState = tuner.Value;
                        // If we're holding 60M-NEW and the amp reads OPER (e.g. the
                        // front-panel OPER was pressed), mark it for correction below.
                        if (_forced60mStby && ampSt == 1) t.AmpState = "STBY";
                        // On a slow cycle, whether the amp answered a status query
                        // at all is our proxy for "powered on" — a powered-off amp
                        // ignores CI-V. Debounce so one dropped read doesn't flip
                        // the pill: require several consecutive misses to go OFF.
                        if (slow)
                        {
                            bool answered = ampSt.HasValue || tempC.HasValue || rfIn.HasValue;
                            if (answered) { t.PoweredOn = true; _powerMiss = 0; }
                            else if (++_powerMiss >= 3) t.PoweredOn = false;
                        }
                        t.DetectedBand = _detectedBand;
                        t.PendingBand = _pendingBand;
                    }

                    // If the link has been silent for a while (dead cycles ≈ 5s at
                    // idle cadence), treat it as lost: drop the connection so the UI
                    // shows the failsafe state, and let the recovery path below
                    // attempt to reconnect.
                    int staleLimit = Math.Max(3, (int)(5000.0 / Math.Max(250, _cfg.PollMsIdle)));
                    if (_staleMiss >= staleLimit)
                    {
                        Note("warn", $"No response for ~{staleLimit} cycles — link lost, entering recovery.");
                        amp.Disconnect();
                        _staleMiss = 0;
                        lock (_stateGate) _telemetry.Connected = false;
                    }
                }
                catch (Exception ex)
                {
                    Note("warn", $"poll error: {ex.Message}");
                }
            }
            else
            {
                lock (_stateGate) _telemetry.Connected = false;

                // Auto-recovery: if the operator wants auto-connect, periodically
                // try to re-establish the link (every ~3s) after a drop.
                if (_cfg.AutoConnect)
                {
                    _recoverTick++;
                    if (_recoverTick >= Math.Max(1, (int)(3000.0 / Math.Max(250, _cfg.PollMsIdle))))
                    {
                        _recoverTick = 0;
                        var (ok, _) = _amp is not null ? _amp.Connect(_cfg.Port, _cfg.Baud, _cfg.StopBits) : (false, "");
                        if (ok)
                        {
                            Note("info", "Recovery: reconnected.");
                            _lastForwardedBand = "";
                            string? band; lock (_stateGate) band = _detectedBand;
                            if (_cfg.BandFollow && band is not null && BandPlan.IsAmpSupported(band))
                                ForwardBand(band);
                        }
                    }
                }
            }

            _pollTick++;

            // Wait for the interval, but wake early on a TX rising edge or
            // shutdown. Reset-then-wait; the MOX handler's Set() during a read is
            // still pending here so the first TX read fires with no added latency.
            _pollWake.Reset();
            _pollWake.Wait(Math.Max(50, interval), token);
        }
    }

    private static string AmpStateName(int? s) => s switch
    {
        0 => "STBY", 1 => "OPER", _ => "OFF"
    };

    // ── connection ───────────────────────────────────────────────────────────

    private (bool ok, string message) TryConnect()
    {
        if (_amp is null) return (false, "not initialized");
        var (ok, msg) = _amp.Connect(_cfg.Port, _cfg.Baud, _cfg.StopBits);
        Note(ok ? "info" : "warn", $"Connect {_cfg.Port} @ {_cfg.Baud}: {(ok ? "OK" : msg)}");
        if (ok)
        {
            _lastForwardedBand = "";
            // After the serial/amp settles (~1.5s): read all antenna names,
            // apply the connect-time STBY default, then forward the band.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                if (_amp is not { IsConnected: true }) return;

                ReadAllAntennaNames();
                ApplyDefaultStbyOnConnect();

                string? band; lock (_stateGate) band = _detectedBand;
                if (_cfg.BandFollow && band is not null && BandPlan.IsAmpSupported(band))
                {
                    Note("bridge", $"Settle complete — forwarding band {BandPlan.Display(band)}.");
                    ForwardBand(band);
                }
            });
            _pollWake.Set();
        }
        return (ok, msg);
    }

    /// <summary>Read all six antenna names into the cache (for button tooltips).</summary>
    private void ReadAllAntennaNames()
    {
        if (_amp is not { IsConnected: true }) return;
        for (int a = 1; a <= 6; a++)
        {
            var name = _amp.ReadAntennaName(a);
            if (name != null) _antNames[a - 1] = name;
        }
        Note("info", "Antenna names refreshed.");
    }

    /// <summary>
    /// Apply the connect-time "default to STBY" safety per the configured mode.
    /// "connect" and "always" both force STBY when the plugin gains the link;
    /// "off" leaves the amp as-is. (The "always" extra — reacting to a later
    /// front-panel power-on — is handled in the poll loop.)
    /// </summary>
    private void ApplyDefaultStbyOnConnect()
    {
        if (_amp is not { IsConnected: true }) return;
        if (_cfg.DefaultStby == "off") return;
        if (_forced60mStby || _overheatLatched) return; // those already hold STBY
        if (_amp.ReadAmpCircuit() == 1)
        {
            var (ok, msg) = _amp.SetAmpCircuit(false);
            if (ok) lock (_stateGate) _telemetry.AmpState = "STBY";
            Note(ok ? "civ" : "warn", $"Default-to-STBY on connect: {(ok ? "OK" : "FAIL")} {msg}");
        }
    }

    // ── HTTP endpoints ───────────────────────────────────────────────────────

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Live telemetry snapshot for the panel.
        endpoints.MapGet("status", () =>
        {
            object payload;
            lock (_stateGate)
            {
                var t = _telemetry;
                payload = new
                {
                    connected = t.Connected,
                    port = _cfg.Port,
                    ampState = t.AmpState,
                    detectedBand = t.DetectedBand,
                    detectedBandDisplay = BandPlan.Display(t.DetectedBand),
                    pendingBand = t.PendingBand,
                    lastForwardedBand = _lastForwardedBand,
                    tx = _tx,
                    bandFollow = _cfg.BandFollow,
                    activeInput = t.ActiveInput,
                    activeAntenna = t.ActiveAntenna,
                    activeAntennaName = t.ActiveAntennaName,
                    antennaNames = _antNames,
                    tunerState = t.TunerState,
                    overheatLatched = _overheatLatched,
                    poweredOn = t.PoweredOn,
                    meters = new
                    {
                        po = t.Po,
                        peakPo = t.PeakPo,
                        swr = t.Swr,
                        alc = t.AlcPercent,
                        vd = t.Vd,
                        id = t.Id,
                        tempC = t.TempC,
                        tempF = t.TempF,
                        humidity = t.Humidity,
                        protection = t.Protection,
                    },
                };
            }
            return Results.Ok(payload);
        });

        // Current config (for the settings UI).
        endpoints.MapGet("config", () => Results.Ok(_cfg));

        // Version + build timestamp, so the operator can confirm which build is
        // loaded. Build time = the plugin assembly's last-write time (set at
        // compile), so it can't be forgotten the way a hand-edited string can.
        endpoints.MapGet("version", () =>
        {
            string ver = "?", built = "?";
            try
            {
                var asm = typeof(Pw2BridgePlugin).Assembly;
                ver = asm.GetName().Version?.ToString() ?? "?";
                var path = asm.Location;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    built = System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
            }
            catch { /* leave defaults */ }
            return Results.Ok(new { manifestVersion = ManifestVersion, assemblyVersion = ver, built });
        });

        // Update config. Any subset of fields may be sent.
        endpoints.MapPost("config", async (ConfigUpdate body) =>
        {
            bool reconnect = false;
            if (body.Port is not null && body.Port != _cfg.Port) { _cfg.Port = body.Port; reconnect = true; }
            if (body.Baud is int baud && baud != _cfg.Baud) { _cfg.Baud = baud; reconnect = true; }
            if (body.StopBits is int sb && sb != _cfg.StopBits) { _cfg.StopBits = sb; reconnect = true; }
            if (body.Pw2AddrHex is not null) { _cfg.Pw2AddrHex = body.Pw2AddrHex; _amp?.SetPw2Addr(Convert.ToInt32(body.Pw2AddrHex, 16)); }
            if (body.RfInput is int rf) _cfg.RfInput = rf;
            if (body.BandFollow is bool bf) _cfg.BandFollow = bf;
            if (body.TxInhibit is bool ti) _cfg.TxInhibit = ti;
            if (body.AutoConnect is bool ac) _cfg.AutoConnect = ac;
            if (body.TempUnit is not null) _cfg.TempUnit = body.TempUnit;
            if (body.MaxTemp is double mt) _cfg.MaxTemp = mt;
            if (body.DefaultStby is not null) _cfg.DefaultStby = body.DefaultStby;
            if (body.PollMsIdle is int pi) _cfg.PollMsIdle = Math.Max(250, pi);
            if (body.PollMsTx is int pt) _cfg.PollMsTx = Math.Max(100, pt);
            if (body.PeakWindowMs is int pw) _cfg.PeakWindowMs = Math.Max(1000, pw);

            if (_ctx is not null) await SaveSettingsAsync(_ctx.Settings, _cfg, CancellationToken.None);

            // A port/baud/stopbits change only takes effect on a fresh open, so
            // reconnect if we were connected — otherwise the setting is saved but
            // the wire keeps running at the old parameters.
            if (reconnect && _amp is { IsConnected: true })
            {
                Note("info", $"Connection settings changed — reconnecting at {_cfg.Baud} baud.");
                _amp.Disconnect();
                TryConnect();
            }

            _pollWake.Set();
            Note("info", "Config updated.");
            return Results.Ok(_cfg);
        });

        endpoints.MapPost("connect", () =>
        {
            var (ok, msg) = TryConnect();
            return ok ? Results.Ok(new { ok, message = msg }) : Results.BadRequest(new { ok, message = msg });
        });

        endpoints.MapPost("disconnect", () =>
        {
            _amp?.Disconnect();
            Note("info", "Disconnected by user.");
            return Results.Ok(new { ok = true });
        });

        // List COM ports so the UI can offer a picker.
        endpoints.MapGet("ports", () => Results.Ok(System.IO.Ports.SerialPort.GetPortNames()));

        // Amp circuit control. These never key TX.
        endpoints.MapPost("oper", () => AmpWrite(true));
        endpoints.MapPost("stby", () => AmpWrite(false));

        // Internal tuner: enable/disable (in-line vs bypass) and start a tune
        // cycle. Neither keys the transmitter — the operator supplies the tuning
        // carrier from the radio.
        endpoints.MapPost("tuner", (TunerRequest body) =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            var (ok, msg) = _amp.SetTuner(body.Enabled);
            if (ok) lock (_stateGate) _telemetry.TunerState = body.Enabled ? 1 : 0;
            Note(ok ? "civ" : "warn", $"Tuner {(body.Enabled ? "ON" : "OFF")}: {(ok ? "OK" : "FAIL")} {msg}");
            return Results.Ok(new { ok, message = msg });
        });

        endpoints.MapPost("tune", () =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            var (ok, msg) = _amp.StartTune();
            if (ok) lock (_stateGate) _telemetry.TunerState = 2;
            Note(ok ? "civ" : "warn", $"Tune cycle: {(ok ? "OK" : "FAIL")} {msg}");
            _pollWake.Set();
            return Results.Ok(new { ok, message = msg });
        });

        // Main power on/off (18 01 / 18 00). Highest-consequence write.
        endpoints.MapPost("power", (PowerRequest body) =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            var (ok, msg) = _amp.SetMainPower(body.On);
            if (ok) lock (_stateGate) _telemetry.PoweredOn = body.On;
            Note(ok ? "civ" : "warn", $"Main power {(body.On ? "ON" : "OFF")}: {(ok ? "OK" : "FAIL")} {msg}");
            return Results.Ok(new { ok, message = msg });
        });

        // RF input select (1A 00). Proven live-readable on the bench.
        endpoints.MapPost("input", (InputRequest body) =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            var (ok, msg) = _amp.SetRfInput(body.Input);
            if (ok)
            {
                // Reflect the change immediately (input is on the slow poll, so
                // otherwise the button would lag several seconds). Also read back
                // the antenna for the newly-selected input so it updates too.
                int? newAnt = _amp.ReadAntennaForInput(body.Input);
                lock (_stateGate)
                {
                    _telemetry.ActiveInput = body.Input;
                    if (newAnt.HasValue) _telemetry.ActiveAntenna = newAnt.Value;
                }
            }
            Note(ok ? "civ" : "warn", $"RF input → INPUT{body.Input + 1}: {(ok ? "OK" : "FAIL")} {msg}");
            _pollWake.Set();
            return Results.Ok(new { ok, message = msg });
        });

        // Antenna select (1A 06). MUST target the input the amp is actually on,
        // or the amp NAKs — verified on the bench.
        endpoints.MapPost("antenna", (AntennaRequest body) =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            int live = _amp.ReadRfInput() ?? _cfg.RfInput;
            var (ok, msg) = _amp.SetAntenna(body.Antenna, live);
            if (ok) lock (_stateGate) _telemetry.ActiveAntenna = body.Antenna; // reflect immediately
            Note(ok ? "civ" : "warn", $"ANT → {body.Antenna} on INPUT{live + 1}: {(ok ? "OK" : "FAIL")} {msg}");
            _pollWake.Set();
            return Results.Ok(new { ok, message = msg });
        });

        // Manual band set (in addition to auto-follow), scoped to the live input.
        endpoints.MapPost("band", (BandRequest body) =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            int live = _amp.ReadRfInput() ?? _cfg.RfInput;
            var (ok, msg) = _amp.SetBand(body.Band, live);
            if (ok) _lastForwardedBand = body.Band;
            Note(ok ? "civ" : "warn", $"Manual band → {body.Band}: {(ok ? "OK" : "FAIL")} {msg}");
            return Results.Ok(new { ok, message = msg });
        });

        endpoints.MapPost("clear-protection", () =>
        {
            if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
            bool ok = _amp.DeactivateProtection();
            Note(ok ? "civ" : "warn", $"Deactivate protection: {(ok ? "OK" : "FAIL")}");
            return Results.Ok(new { ok });
        });

        // Recent event log for the panel's debug view.
        endpoints.MapGet("log", () => Results.Ok(_events.Snapshot()));
    }

    private IResult AmpWrite(bool oper)
    {
        if (_amp is not { IsConnected: true }) return Results.BadRequest(new { ok = false, message = "not connected" });
        // Enforce the 60m low-power rule: block OPER while parked in 60M-NEW.
        if (oper && _forced60mStby)
        {
            Note("warn", "OPER blocked — amp must stay in STBY on the 60m low-power sub-band.");
            lock (_stateGate) _telemetry.AmpState = "STBY";
            return Results.Ok(new { ok = false, message = "Blocked: 60m low-power sub-band requires STBY." });
        }
        // Overheat latch: block OPER until temperature has recovered and the
        // operator manually re-enables. A manual STBY clears the latch so they
        // can retry once things have cooled.
        if (oper && _overheatLatched)
        {
            Note("warn", "OPER blocked — overheat latch active. Let the amp cool, then press STBY to clear.");
            lock (_stateGate) _telemetry.AmpState = "STBY";
            return Results.Ok(new { ok = false, message = "Blocked: overheat latch active (press STBY to clear)." });
        }
        if (!oper && _overheatLatched)
        {
            _overheatLatched = false; // manual STBY clears the latch
            Note("info", "Overheat latch cleared by operator.");
        }
        var (ok, msg) = _amp.SetAmpCircuit(oper);
        if (ok)
        {
            // Reflect the new state immediately instead of waiting for the next
            // slow poll (up to a few seconds away).
            lock (_stateGate) _telemetry.AmpState = oper ? "OPER" : "STBY";
        }
        Note(ok ? "civ" : "warn", $"{(oper ? "OPER" : "STBY")}: {(ok ? "OK" : "FAIL")} {msg}");
        return Results.Ok(new { ok, message = msg });
    }

    // ── settings persistence ─────────────────────────────────────────────────

    private static async Task<Settings> LoadSettingsAsync(IPluginSettings store, CancellationToken ct)
    {
        try
        {
            var s = await store.GetAsync<Settings>("config", ct);
            return s ?? new Settings();
        }
        catch { return new Settings(); }
    }

    private static Task SaveSettingsAsync(IPluginSettings store, Settings cfg, CancellationToken ct)
    {
        try { return store.SetAsync("config", cfg, ct); } catch { return Task.CompletedTask; }
    }

    // ── logging ──────────────────────────────────────────────────────────────

    private void Note(string kind, string message)
    {
        _events.Add(kind, message);
        switch (kind)
        {
            case "warn": _log?.LogWarning("{Msg}", message); break;
            default: _log?.LogInformation("[{Kind}] {Msg}", kind, message); break;
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    public sealed class Settings
    {
        public string Port { get; set; } = "COM10";
        public int Baud { get; set; } = 9600;
        public int StopBits { get; set; } = 1;
        public string Pw2AddrHex { get; set; } = "AA";
        public int RfInput { get; set; } = 0;          // 0 = INPUT1, 1 = INPUT2
        public bool BandFollow { get; set; } = true;
        public bool TxInhibit { get; set; } = true;
        public bool AutoConnect { get; set; } = true;
        public string TempUnit { get; set; } = "F";     // "F" or "C"
        public double MaxTemp { get; set; } = 120;       // overheat auto-STBY threshold, in TempUnit
        public string DefaultStby { get; set; } = "connect"; // "connect" | "always" | "off"
        public int PollMsIdle { get; set; } = 1500;
        public int PollMsTx { get; set; } = 150;
        public int PeakWindowMs { get; set; } = 4000;    // rolling peak-Po window
    }

    private sealed record ConfigUpdate(
        string? Port, int? Baud, int? StopBits, string? Pw2AddrHex, int? RfInput,
        bool? BandFollow, bool? TxInhibit, bool? AutoConnect, string? TempUnit,
        double? MaxTemp, string? DefaultStby, int? PollMsIdle, int? PollMsTx, int? PeakWindowMs);

    private sealed record BandRequest(string Band);
    private sealed record PowerRequest(bool On);
    private sealed record InputRequest(int Input);
    private sealed record AntennaRequest(int Antenna);
    private sealed record TunerRequest(bool Enabled);

    private sealed class Telemetry
    {
        public bool Connected { get; set; }
        public int? Po { get; set; }
        public int PeakPo { get; set; }
        public double? Swr { get; set; }
        public int? AlcPercent { get; set; }
        public double? Vd { get; set; }
        public double? Id { get; set; }
        public double? TempC { get; set; }
        public double? TempF { get; set; }
        public int? Humidity { get; set; }
        public string? Protection { get; set; }
        public string AmpState { get; set; } = "OFF";
        public bool PoweredOn { get; set; }
        public int ActiveInput { get; set; } = 0;      // 0=INPUT1, 1=INPUT2
        public int ActiveAntenna { get; set; } = 1;    // 1..6
        public string ActiveAntennaName { get; set; } = "";
        public int TunerState { get; set; } = 0;        // 0=off, 1=on, 2=tuning
        public string? DetectedBand { get; set; }
        public string? PendingBand { get; set; }
    }
}

/// <summary>One entry in the debug ring buffer.</summary>
public sealed record LogEntry(string Ts, string Kind, string Message);

/// <summary>Tiny thread-safe ring buffer of recent events for the debug view.</summary>
internal sealed class RingLog
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<LogEntry> _q = new();

    public RingLog(int capacity) => _capacity = capacity;

    public void Add(string kind, string message)
    {
        _q.Enqueue(new LogEntry(DateTime.Now.ToString("HH:mm:ss.fff"), kind, message));
        while (_q.Count > _capacity && _q.TryDequeue(out _)) { }
    }

    public LogEntry[] Snapshot() => _q.ToArray();
}
