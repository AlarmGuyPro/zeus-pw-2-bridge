// SPDX-License-Identifier: GPL-2.0-or-later
//
// Serial transport + typed CI-V command set for the IC-PW2.
//
// Port of the CIVSerial class from the Python bridge. Talks to the amp over a
// COM port (USB-serial -> the amp's 3.5mm [REMOTE AUX] jack, guide p.2), using
// the framing/scaling in Pw2Civ. Windows-first: DTR/RTS are asserted because
// the guide's wiring ties RTS->CTS (no hardware flow control).
//
// Threading: every method that touches the port takes _gate, so a poll and a
// band-forward can't interleave on the wire. Read methods return null on
// timeout/NAK; write methods return (ok, message).

using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.Pw2Bridge;

public sealed class Pw2CivSerial : IDisposable
{
    // One send/receive round trip budget. A CI-V reply on a 9600 link arrives
    // in well under 100ms when it comes at all; 150ms is ample headroom. Keeping
    // this tight is what makes Po/SWR responsive during TX — a longer budget
    // means every slow/empty meter read stalls the whole loop for that long.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(150);

    private readonly object _gate = new();
    private readonly ILogger _log;
    private SerialPort? _port;

    public byte Pw2Addr { get; private set; } = Pw2Civ.DefaultPw2Addr;

    public Pw2CivSerial(ILogger log) => _log = log;

    public bool IsConnected
    {
        get { lock (_gate) return _port is { IsOpen: true }; }
    }

    public void SetPw2Addr(int addr)
    {
        if (Pw2Civ.IsSafeAddr(addr))
            lock (_gate) Pw2Addr = (byte)addr;
    }

    public (bool ok, string message) Connect(string portName, int baud = 9600, int stopBits = 1)
    {
        lock (_gate)
        {
            try
            {
                Close_NoLock();
                var p = new SerialPort(portName, baud, Parity.None, 8,
                    stopBits == 2 ? System.IO.Ports.StopBits.Two : System.IO.Ports.StopBits.One)
                {
                    ReadTimeout = 20,   // fine granularity; we loop to our own deadline
                    WriteTimeout = 500,
                    // Guide p.2: RTS tied to CTS, comms without flow control.
                    Handshake = Handshake.None,
                    DtrEnable = true,
                    RtsEnable = true,
                };
                p.Open();
                _port = p;
                _log.LogInformation("CI-V connected {Port} @ {Baud} 8N{Stop}", portName, baud, stopBits);
                return (true, "OK");
            }
            catch (Exception ex)
            {
                _port = null;
                _log.LogWarning("CI-V connect failed on {Port}: {Err}", portName, ex.Message);
                return (false, ex.Message);
            }
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            Close_NoLock();
            _log.LogInformation("CI-V disconnected");
        }
    }

    private void Close_NoLock()
    {
        try { if (_port is { IsOpen: true }) _port.Close(); } catch { /* ignore */ }
        _port?.Dispose();
        _port = null;
    }

    /// <summary>
    /// Send a raw CI-V packet and, if requested, collect the reply. Returns the
    /// received bytes (may include our echo plus the reply) or an empty array.
    /// Mirrors CIVSerial.send_raw().
    /// </summary>
    public byte[] SendRaw(byte[] pkt, bool expectReply = true)
    {
        lock (_gate)
        {
            if (_port is not { IsOpen: true }) return Array.Empty<byte>();
            try
            {
                try { _port.DiscardInBuffer(); } catch { /* ignore */ }
                _port.Write(pkt, 0, pkt.Length);

                if (!expectReply) return Array.Empty<byte>();

                var buf = new List<byte>(64);
                var readChunk = new byte[64];
                var deadline = DateTime.UtcNow + ReadTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    int got;
                    try { got = _port.Read(readChunk, 0, readChunk.Length); }
                    catch (TimeoutException) { got = 0; }
                    if (got > 0)
                    {
                        for (int k = 0; k < got; k++) buf.Add(readChunk[k]);
                        // Return as soon as a COMPLETE reply frame is present:
                        // FE FE E0 <pw2> ... FD occurring after our echoed packet.
                        // Checking for a whole valid frame lets us stop instantly
                        // instead of doing a blind trailing read that must time out
                        // (~50ms) on every single call — that timeout tax was the
                        // bulk of each read's ~108ms cost.
                        if (HasCompleteReplyFrame(buf, pkt.Length))
                            break;
                    }
                }
                return buf.ToArray();
            }
            catch (Exception ex)
            {
                _log.LogWarning("CI-V I/O error: {Err}", ex.Message);
                Close_NoLock();
                return Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// True once buf contains a complete CI-V reply frame after the echoed
    /// command: a preamble FE FE, dest = controller (E0), src = pw2, then any
    /// bytes, ending with FD. Both data replies and ACK/NAK frames match. This
    /// lets SendRaw stop the instant a full reply has arrived rather than waiting
    /// for a read timeout.
    /// </summary>
    private bool HasCompleteReplyFrame(List<byte> buf, int echoLen)
    {
        int start = Math.Max(0, echoLen - 1); // reply begins after our echoed packet
        for (int i = start; i + 4 < buf.Count; i++)
        {
            if (buf[i] != Pw2Civ.Preamble || buf[i + 1] != Pw2Civ.Preamble) continue;
            if (buf[i + 2] != Pw2Civ.CtrlAddr || buf[i + 3] != Pw2Addr) continue;
            // find the terminating FD for this frame
            for (int j = i + 4; j < buf.Count; j++)
                if (buf[j] == Pw2Civ.EndOfMessage) return true;
            return false; // frame started but not yet terminated
        }
        return false;
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>Set frequency band via 1A 03 [rfInput, bandCode] (guide p.8).</summary>
    public (bool ok, string message) SetBand(string band, int rfInput = 0)
    {
        if (!Pw2Civ.BandCode.TryGetValue(band, out var code))
            return (false, "unsupported");
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x03, new[] { (byte)(rfInput & 1), code });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>OPER/STBY: amp circuit on/off via 1A 09 01 / 1A 09 00 (guide p.6).</summary>
    public (bool ok, string message) SetAmpCircuit(bool oper)
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x09, new[] { (byte)(oper ? 0x01 : 0x00) });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>Main power on/off via 18 01 / 18 00 (guide p.4). Optional feature.</summary>
    public (bool ok, string message) SetMainPower(bool on)
    {
        // Power-off is a single awake packet. Power-on may need the wake-up
        // preamble because the amp CPU can miss the first framed command.
        if (!on)
            return AckResult(SendRaw(Pw2Civ.Build(Pw2Addr, 0x18, 0x00)));

        try
        {
            lock (_gate)
            {
                if (_port is { IsOpen: true })
                {
                    var wake = new byte[12];
                    Array.Fill(wake, Pw2Civ.Preamble);
                    _port.Write(wake, 0, wake.Length);
                }
            }
            Thread.Sleep(150);
        }
        catch { /* ignore */ }

        var pkt = Pw2Civ.Build(Pw2Addr, 0x18, 0x01);
        string last = "no response";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var resp = SendRaw(pkt);
            if (resp.Length > 0)
            {
                if (Pw2Civ.IsAck(resp, Pw2Addr)) return (true, $"ACK (attempt {attempt + 1})");
                if (Pw2Civ.IsNak(resp, Pw2Addr)) return (false, "NAK (already on, or no AC?)");
                last = "unrecognized response";
            }
            Thread.Sleep(300 * (attempt + 1));
        }
        return (false, $"{last} after 3 attempts");
    }

    /// <summary>Deactivate protection via 1A 0D (guide p.7).</summary>
    public bool DeactivateProtection() => Pw2Civ.IsAck(SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x0D)), Pw2Addr);

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>Amp circuit state via 1A 09 read form: 0=STBY, 1=OPER, null=NAK/off.</summary>
    public int? ReadAmpCircuit()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x09));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x09);
        if (p.Length >= 1 && (p[0] == 0 || p[0] == 1)) return p[0];
        return null;
    }

    /// <summary>Raw meter via 15 &lt;sub&gt; (guide p.4): 0..9999 BCD, or null.</summary>
    public int? ReadMeterRaw(byte sub)
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x15, sub));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x15, sub);
        return p.Length >= 2 ? Pw2Civ.Bcd2ToInt(p[0], p[1]) : null;
    }

    public int? ReadPoWatts()      => Map(ReadMeterRaw(0x11), Pw2Civ.RawToWatts);
    public double? ReadSwr()       => Map(ReadMeterRaw(0x12), Pw2Civ.RawToSwr);
    public int? ReadAlcPercent()   => Map(ReadMeterRaw(0x13), Pw2Civ.RawToAlcPercent);
    public double? ReadVolts()     => Map(ReadMeterRaw(0x15), Pw2Civ.RawToVolts);
    public double? ReadAmps()      => Map(ReadMeterRaw(0x16), Pw2Civ.RawToAmps);

    /// <summary>Temperature in °C via 1A 0E (guide p.9), or null.</summary>
    public double? ReadTemperatureC()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x0E));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x0E);
        if (p.Length >= 3) return Pw2Civ.DecodeTemperatureC(p);
        if (p.Length >= 2) return Pw2Civ.DecodeTemperatureC(p); // older 2-byte firmware
        return null;
    }

    /// <summary>Humidity 0..99 via 1A 0F (guide p.7), or null.</summary>
    public int? ReadHumidity()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x0F));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x0F);
        if (p.Length >= 2) return Pw2Civ.Bcd2ToInt(p[0], p[1]);
        if (p.Length >= 1) return (p[0] >> 4) * 10 + (p[0] & 0x0F);
        return null;
    }

    /// <summary>Protection status string via 1A 0C (guide p.7), or null.</summary>
    public string? ReadProtection()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x0C));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x0C);
        if (p.Length >= 1)
            return Pw2Civ.ProtCodes.TryGetValue(p[0], out var s) ? s : $"ERR(0x{p[0]:X2})";
        return null;
    }

    /// <summary>Tuner state via 1C 01 (guide p.7): 0=OFF, 1=ON, 2=Tuning, or null.</summary>
    public int? ReadTunerState()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1C, 0x01));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1C, 0x01);
        return p.Length >= 1 ? p[0] : null;
    }

    /// <summary>Enable (in-line) or disable (bypass) the internal tuner via 1C 01 01 / 1C 01 00.</summary>
    public (bool ok, string message) SetTuner(bool enabled)
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1C, 0x01, new[] { (byte)(enabled ? 0x01 : 0x00) });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>
    /// Start a tuning cycle via 1C 01 02. This puts the amp's tuner into Tune
    /// mode; the operator still supplies the tuning carrier from the radio — this
    /// never keys the transmitter.
    /// </summary>
    public (bool ok, string message) StartTune()
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1C, 0x01, new[] { (byte)0x02 });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>Active antenna 1..6 for the given RF input via 1A 06 (guide p.6/p.9), or null.</summary>
    public int? ReadActiveAntenna(int rfInput = 0)
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x06, new[] { (byte)(rfInput & 1) });
        var r = SendRaw(pkt);
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x06);
        // Reply payload: <input> <ant>. Antenna code 00..05 -> 1..6.
        if (p.Length >= 2 && p[1] <= 5) return p[1] + 1;
        return null;
    }

    /// <summary>
    /// Set active antenna 1..6 for the given RF input via 1A 06 (guide p.9).
    /// IMPORTANT: the amp NAKs an antenna write aimed at an input it is not
    /// currently operating on, so callers must pass the live input from
    /// ReadRfInput(), not a stored default.
    /// </summary>
    public (bool ok, string message) SetAntenna(int antNum, int rfInput = 0)
    {
        if (antNum < 1 || antNum > 6) return (false, "ant out of range");
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x06, new[] { (byte)(rfInput & 1), (byte)(antNum - 1) });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>Read the antenna (1..6) currently set for the given input via 1A 06, or null.</summary>
    public int? ReadAntennaForInput(int rfInput)
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x06, new[] { (byte)(rfInput & 1) });
        var r = SendRaw(pkt);
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x06);
        if (p.Length >= 2 && p[1] <= 5) return p[1] + 1;
        return null;
    }

    /// <summary>Read the selected RF input connector via 1A 00 (guide p.4): 0=INPUT1, 1=INPUT2, or null.</summary>
    public int? ReadRfInput()
    {
        var r = SendRaw(Pw2Civ.Build(Pw2Addr, 0x1A, 0x00));
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x00);
        if (p.Length >= 1 && (p[0] == 0 || p[0] == 1)) return p[0];
        return null;
    }

    /// <summary>Set the RF input connector via 1A 00 (guide p.4): 0=INPUT1, 1=INPUT2.</summary>
    public (bool ok, string message) SetRfInput(int input)
    {
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x00, new[] { (byte)(input & 1) });
        return AckResult(SendRaw(pkt));
    }

    /// <summary>User antenna name for ANT 1..6 via 1A 05 00 &lt;sub&gt; (guide p.6/p.8), or null.</summary>
    public string? ReadAntennaName(int antNum)
    {
        // Guide p.6/p.8: ANT name settings are 1A 05 00 <n>, <n> hex 59/62/65/68/71/74.
        // Reply: FE FE E0 AA 1A 05 00 <n> <ascii name...> FD. Bench-confirmed:
        //   0x59 → "(Name 1)", 0x68 → "Dummy Load".
        byte sub = antNum switch { 1 => 0x59, 2 => 0x62, 3 => 0x65, 4 => 0x68, 5 => 0x71, 6 => 0x74, _ => 0 };
        if (sub == 0) return null;
        var pkt = Pw2Civ.Build(Pw2Addr, 0x1A, 0x05, new byte[] { 0x00, sub });
        var r = SendRaw(pkt);
        var p = Pw2Civ.FindReply(r, Pw2Addr, 0x1A, 0x05);
        // Payload = 00 <sub> <name...>. Require the 00 <sub> echo, then decode the
        // remainder as ASCII; anything shorter is a blank/failed read.
        if (p.Length < 3 || p[0] != 0x00 || p[1] != sub) return "";
        var ascii = Encoding.ASCII.GetString(p[2..].ToArray());
        return ascii.TrimEnd(' ', '\0');
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static T? Map<T>(int? raw, Func<int, T> f) where T : struct =>
        raw.HasValue ? f(raw.Value) : null;

    private (bool ok, string message) AckResult(byte[] resp)
    {
        if (resp.Length == 0) return (false, "no response");
        if (Pw2Civ.IsAck(resp, Pw2Addr)) return (true, "ACK");
        if (Pw2Civ.IsNak(resp, Pw2Addr)) return (false, "NAK");
        return (false, "unrecognized response");
    }

    public void Dispose() => Disconnect();
}
