// SPDX-License-Identifier: GPL-2.0-or-later
//
// IC-PW2 CI-V protocol layer.
//
// Pure byte logic: packet building, echo-safe reply parsing, ACK/NAK
// detection, BCD decoders, and meter calibration curves. No serial I/O and
// no Zeus dependencies live here, so this file is unit-testable on its own.
//
// Every command/scaling constant in this file was reconciled against the
// Icom "IC-PW2 CI-V Reference Guide" (A7736-5EX-2, Aug. 2024). Where the
// reconciliation corrected the original Python bridge, the comment says so.

using System;
using System.Collections.Generic;

namespace Zeus.Community.Pw2Bridge;

/// <summary>
/// Stateless helpers for framing and decoding IC-PW2 CI-V packets.
/// Frame layout (guide p.3):
///   Controller -> PW2:  FE FE &lt;pw2&gt; E0 &lt;cmd&gt; [sub] [data...] FD
///   PW2 -> Controller:  FE FE E0 &lt;pw2&gt; &lt;cmd&gt; [sub] [data...] FD
///   OK (ACK):           FE FE E0 &lt;pw2&gt; FB FD
///   NG (NAK):           FE FE E0 &lt;pw2&gt; FA FD
/// </summary>
public static class Pw2Civ
{
    public const byte DefaultPw2Addr = 0xAA;
    public const byte CtrlAddr = 0xE0;
    public const byte Preamble = 0xFE;
    public const byte EndOfMessage = 0xFD;
    public const byte AckCode = 0xFB;
    public const byte NakCode = 0xFA;

    // CI-V addresses we refuse to use as the PW2 address (framing/reserved).
    private static readonly HashSet<byte> ReservedAddrs =
        new() { 0x00, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF };

    /// <summary>True if addr is a usable PW2 CI-V address (0x01..0xEF, not reserved).</summary>
    public static bool IsSafeAddr(int addr) =>
        addr >= 0x01 && addr <= 0xEF && !ReservedAddrs.Contains((byte)addr);

    // ── Band codes for command 1A 03 (guide p.8) ────────────────────────────
    // The guide lists ONLY codes 00-09 (1.8-50 MHz). 60m has no band code, so
    // it is deliberately absent here and handled by early-return in the caller.
    public static readonly IReadOnlyDictionary<string, byte> BandCode =
        new Dictionary<string, byte>
        {
            ["160m"] = 0x00,
            ["80m"]  = 0x01,
            // 60m: no CI-V band code on the IC-PW2 — handled separately.
            ["40m"]  = 0x02,
            ["30m"]  = 0x03,
            ["20m"]  = 0x04,
            ["17m"]  = 0x05,
            ["15m"]  = 0x06,
            ["12m"]  = 0x07,
            ["10m"]  = 0x08,
            ["6m"]   = 0x09,
        };

    // Protection codes for 1A 0C (guide p.7).
    public static readonly IReadOnlyDictionary<int, string> ProtCodes =
        new Dictionary<int, string>
        {
            [0] = "None",
            [1] = "TEMP",
            [2] = "ALC",
            [3] = "POWER",
            [4] = "BAND",
            [5] = "POWER SUPPLY",
        };

    // ── Packet builder ──────────────────────────────────────────────────────

    /// <summary>Build a controller-&gt;PW2 packet: FE FE pw2 E0 cmd [sub] [data] FD.</summary>
    public static byte[] Build(byte pw2Addr, byte cmd, byte? sub = null, ReadOnlySpan<byte> data = default)
    {
        int len = 5 + (sub.HasValue ? 1 : 0) + data.Length + 1;
        var pkt = new byte[len];
        int i = 0;
        pkt[i++] = Preamble;
        pkt[i++] = Preamble;
        pkt[i++] = pw2Addr;
        pkt[i++] = CtrlAddr;
        pkt[i++] = cmd;
        if (sub.HasValue) pkt[i++] = sub.Value;
        foreach (var b in data) pkt[i++] = b;
        pkt[i] = EndOfMessage;
        return pkt;
    }

    /// <summary>
    /// Locate a valid reply FROM pw2Addr TO the controller (E0) matching cmd
    /// (and sub if given). Rejects echoes of our outgoing command, which have
    /// the reverse dest/src. Returns the payload span (first data byte through
    /// the byte before FD) or null if not found.
    ///
    /// Mirrors find_civ_reply() in the Python bridge.
    /// </summary>
    public static ReadOnlySpan<byte> FindReply(ReadOnlySpan<byte> buf, byte pw2Addr, byte cmd, byte? sub = null)
    {
        int n = buf.Length;
        for (int i = 0; i + 5 < n; i++)
        {
            if (buf[i] != Preamble || buf[i + 1] != Preamble) continue;
            if (buf[i + 2] != CtrlAddr) continue;   // dest must be controller
            if (buf[i + 3] != pw2Addr) continue;    // src must be the PW2
            if (buf[i + 4] != cmd) continue;

            int payloadStart = i + 5;
            if (sub.HasValue)
            {
                if (i + 5 >= n || buf[i + 5] != sub.Value) continue;
                payloadStart = i + 6;
            }

            int j = payloadStart;
            while (j < n && buf[j] != EndOfMessage) j++;
            if (j < n) return buf.Slice(payloadStart, j - payloadStart);
        }
        return default;
    }

    /// <summary>True if buf contains a real ACK frame (FE FE E0 pw2 FB FD).</summary>
    public static bool IsAck(ReadOnlySpan<byte> buf, byte pw2Addr) =>
        HasStatusFrame(buf, pw2Addr, AckCode);

    /// <summary>True if buf contains a real NAK frame (FE FE E0 pw2 FA FD).</summary>
    public static bool IsNak(ReadOnlySpan<byte> buf, byte pw2Addr) =>
        HasStatusFrame(buf, pw2Addr, NakCode);

    private static bool HasStatusFrame(ReadOnlySpan<byte> buf, byte pw2Addr, byte code)
    {
        int n = buf.Length;
        for (int i = 0; i + 5 < n; i++)
        {
            if (buf[i] == Preamble && buf[i + 1] == Preamble &&
                buf[i + 2] == CtrlAddr && buf[i + 3] == pw2Addr &&
                buf[i + 4] == code && buf[i + 5] == EndOfMessage)
                return true;
        }
        return false;
    }

    // ── BCD decoders (guide p.4 / p.9) ──────────────────────────────────────

    /// <summary>Two BCD bytes as a 4-digit integer. 0x02 0x01 -&gt; 201.</summary>
    public static int Bcd2ToInt(byte hi, byte lo) =>
        (hi >> 4) * 1000 + (hi & 0x0F) * 100 + (lo >> 4) * 10 + (lo & 0x0F);

    /// <summary>
    /// Temperature decode for 1A 0E (guide p.9): three data bytes.
    ///   byte0 = 100°C/10°C BCD, byte1 = 1°C/0.1°C BCD, byte2 = sign (00=+,01=-).
    /// Bench example from the guide: 03 29 00 -&gt; +032.9°C.
    /// </summary>
    public static double DecodeTemperatureC(ReadOnlySpan<byte> payload)
    {
        double whole = (payload[0] >> 4) * 100 + (payload[0] & 0x0F) * 10 + (payload[1] >> 4);
        int frac = payload[1] & 0x0F;
        if (frac > 9) frac = 0;
        double value = whole + frac / 10.0;
        bool negative = payload.Length >= 3 && payload[2] == 0x01;
        return negative ? -value : value;
    }

    // ── Meter calibration curves (guide p.4) ────────────────────────────────
    // Raw meter value is a BCD 0..255 read via command 15 <sub>.

    /// <summary>Po: 0=0W, 161=500W, 201=1kW. Piecewise per the guide's cal points.</summary>
    public static int RawToWatts(int raw)
    {
        raw = Math.Max(0, raw);
        if (raw <= 161) return (int)Math.Round(raw * 500.0 / 161.0);
        if (raw <= 201) return (int)Math.Round(500.0 + (raw - 161) * 500.0 / 40.0);
        return Math.Min(1100, (int)Math.Round(500.0 + (raw - 161) * 500.0 / 40.0));
    }

    /// <summary>SWR: 0=1.0, 40=1.5, 80=2.0, 120=3.0. Piecewise; display capped at 9.99.</summary>
    public static double RawToSwr(int raw)
    {
        raw = Math.Clamp(raw, 0, 255);
        double v;
        if (raw <= 40)      v = 1.0 + raw * 0.5 / 40.0;
        else if (raw <= 80) v = 1.5 + (raw - 40) * 0.5 / 40.0;
        else if (raw <= 120)v = 2.0 + (raw - 80) * 1.0 / 40.0;
        else                v = Math.Min(9.99, 3.0 + (raw - 120) * 0.025);
        return Math.Round(v, 2);
    }

    /// <summary>
    /// ALC as a percentage. Guide p.4: 0000=Minimum ~ 0120=Maximum, so full
    /// scale is raw 120, not 255. Returns 0..100.
    ///
    /// NOTE: the Python bridge read ALC (15 13) but never scaled it — it showed
    /// the raw count. This scaler is the reconciliation fix.
    /// </summary>
    public static int RawToAlcPercent(int raw)
    {
        raw = Math.Clamp(raw, 0, 120);
        return (int)Math.Round(raw * 100.0 / 120.0);
    }

    /// <summary>Vd: 0=0V, 120=30V, 241=60V. Linear (clean ADC reading).</summary>
    public static double RawToVolts(int raw) => Math.Round(raw * 60.0 / 241.0, 1);

    /// <summary>Id: piecewise per guide cal points 0=0A .. 241=50A.</summary>
    public static double RawToAmps(int raw)
    {
        raw = Math.Max(0, raw);
        (int r, double a)[] pts =
        {
            (0, 0.0), (48, 10.0), (96, 20.0), (144, 30.0), (193, 40.0), (241, 50.0)
        };
        if (raw >= pts[^1].r)
        {
            var (r0, a0) = (pts[^2].r, pts[^2].a);
            var (r1, a1) = (pts[^1].r, pts[^1].a);
            return Math.Round(a1 + (raw - r1) * (a1 - a0) / (r1 - r0), 1);
        }
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var (r0, a0) = pts[i];
            var (r1, a1) = pts[i + 1];
            if (raw <= r1) return Math.Round(a0 + (raw - r0) * (a1 - a0) / (r1 - r0), 1);
        }
        return Math.Round(raw * 50.0 / 241.0, 1); // unreachable fallback
    }

    public static double CToF(double c) => Math.Round(c * 9.0 / 5.0 + 32.0, 1);
}
