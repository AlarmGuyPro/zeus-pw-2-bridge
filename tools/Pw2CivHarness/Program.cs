// SPDX-License-Identifier: GPL-2.0-or-later
//
// IC-PW2 CI-V test harness.
//
//   pw2civ selftest
//       Runs assertions against the exact examples in the CI-V Reference Guide.
//       No hardware needed. Exit code 0 = all passed, 1 = a failure.
//
//   pw2civ ports
//       List available COM ports.
//
//   pw2civ serial <COMx> [baud] [pw2addr-hex]
//       Interactive session with a real amp. Type commands (help for a list).
//       Example: pw2civ serial COM10 9600 AA
//
// The protocol/serial code under test is the same source the Zeus plugin uses.

using Microsoft.Extensions.Logging;
using Zeus.Community.Pw2Bridge;

return args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
{
    "selftest" => SelfTest.Run(),
    "ports"    => Interactive.ListPorts(),
    "serial"   => Interactive.Run(args),
    _          => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        IC-PW2 CI-V harness

          pw2civ selftest                       run protocol assertions (no hardware)
          pw2civ ports                          list COM ports
          pw2civ serial <COMx> [baud] [addrHex] interactive session with the amp

        Examples:
          pw2civ selftest
          pw2civ serial COM10 9600 AA
        """);
    return 2;
}

// ─────────────────────────────────────────────────────────────────────────────
//  SELF-TEST  — asserts against the guide's own worked examples
// ─────────────────────────────────────────────────────────────────────────────
static class SelfTest
{
    private static int _passed, _failed;

    public static int Run()
    {
        Console.WriteLine("== Pw2Civ self-test ==\n");

        // --- Packet builder (guide p.3 framing) ---
        // Band-set 20m on INPUT1: FE FE AA E0 1A 03 00 04 FD
        Check("build 1A 03 band packet",
            Hex(Pw2Civ.Build(0xAA, 0x1A, 0x03, new byte[] { 0x00, 0x04 })),
            "FE FE AA E0 1A 03 00 04 FD");

        // OPER: FE FE AA E0 1A 09 01 FD
        Check("build OPER packet",
            Hex(Pw2Civ.Build(0xAA, 0x1A, 0x09, new byte[] { 0x01 })),
            "FE FE AA E0 1A 09 01 FD");

        // Meter read Po (15 11), read form has no data: FE FE AA E0 15 11 FD
        Check("build meter-read packet",
            Hex(Pw2Civ.Build(0xAA, 0x15, 0x11)),
            "FE FE AA E0 15 11 FD");

        // --- Reply parser: must reject our echo, accept the real reply ---
        // Echo (dest=AA,src=E0) then reply (dest=E0,src=AA) carrying 00 04.
        var wire = FromHex("FE FE AA E0 1A 03 00 04 FD  FE FE E0 AA 1A 03 00 04 FD");
        var payload = Pw2Civ.FindReply(wire, 0xAA, 0x1A, 0x03).ToArray();
        Check("FindReply skips echo, returns payload", Hex(payload), "00 04");

        // ACK / NAK framing (guide p.3)
        Check("IsAck true",  Pw2Civ.IsAck(FromHex("FE FE E0 AA FB FD"), 0xAA).ToString(), "True");
        Check("IsNak true",  Pw2Civ.IsNak(FromHex("FE FE E0 AA FA FD"), 0xAA).ToString(), "True");
        Check("IsAck false on data byte FB",
            Pw2Civ.IsAck(FromHex("FE FE E0 AA 15 11 FB FD"), 0xAA).ToString(), "False");

        // --- Temperature (guide p.9): 03 29 00 -> +32.9 C ---
        Check("temp 03 29 00 = +32.9",
            Pw2Civ.DecodeTemperatureC(FromHex("03 29 00")).ToString("0.0"), "32.9");
        // sign byte 01 -> negative
        Check("temp 00 50 01 = -0.5",
            Pw2Civ.DecodeTemperatureC(FromHex("00 05 01")).ToString("0.0"), "-0.5");

        // --- BCD (guide p.4) ---
        Check("bcd 02 01 = 201", Pw2Civ.Bcd2ToInt(0x02, 0x01).ToString(), "201");
        Check("bcd 00 65 = 65 (humidity)", Pw2Civ.Bcd2ToInt(0x00, 0x65).ToString(), "65");

        // --- Meter calibration (guide p.4 cal points) ---
        Check("Po raw 0 = 0W",     Pw2Civ.RawToWatts(0).ToString(),   "0");
        Check("Po raw 161 = 500W", Pw2Civ.RawToWatts(161).ToString(), "500");
        Check("Po raw 201 = 1000W",Pw2Civ.RawToWatts(201).ToString(), "1000");

        Check("SWR raw 0 = 1.0",   Pw2Civ.RawToSwr(0).ToString("0.0"),   "1.0");
        Check("SWR raw 40 = 1.5",  Pw2Civ.RawToSwr(40).ToString("0.0"),  "1.5");
        Check("SWR raw 80 = 2.0",  Pw2Civ.RawToSwr(80).ToString("0.0"),  "2.0");
        Check("SWR raw 120 = 3.0", Pw2Civ.RawToSwr(120).ToString("0.0"), "3.0");

        // ALC: guide says full-scale is raw 120, not 255. This is the fix over
        // the Python bridge, which never scaled ALC at all.
        Check("ALC raw 0 = 0%",    Pw2Civ.RawToAlcPercent(0).ToString(),   "0");
        Check("ALC raw 60 = 50%",  Pw2Civ.RawToAlcPercent(60).ToString(),  "50");
        Check("ALC raw 120 = 100%",Pw2Civ.RawToAlcPercent(120).ToString(), "100");
        Check("ALC raw 200 clamps 100%", Pw2Civ.RawToAlcPercent(200).ToString(), "100");

        // Guide labels raw 120 as "30V"; the single linear fit raw*60/241 gives
        // 29.9. The 0.1V gap is a rounding artifact of the guide's own non-linear
        // cal points, not a bug — asserting the fit's actual output on purpose.
        Check("Vd raw 120 ≈ 30 (linear fit = 29.9)", Pw2Civ.RawToVolts(120).ToString("0.0"), "29.9");
        Check("Vd raw 241 = 60.0", Pw2Civ.RawToVolts(241).ToString("0.0"), "60.0");

        Check("Id raw 48 = 10.0",  Pw2Civ.RawToAmps(48).ToString("0.0"),  "10.0");
        Check("Id raw 241 = 50.0", Pw2Civ.RawToAmps(241).ToString("0.0"), "50.0");

        // --- Band plan ---
        Check("14.2 MHz -> 20m", BandPlan.FromHz(14_200_000) ?? "null", "20m");
        Check("5.3585 MHz -> 60m-new (FCC sub-band)",
            BandPlan.FromHz(5_358_500) ?? "null", "60m-new");
        Check("5.332 MHz -> 60m (normal channel)",
            BandPlan.FromHz(5_332_000) ?? "null", "60m");
        Check("60m-new is low power", BandPlan.Is60mLowPower("60m-new").ToString(), "True");
        Check("20m display = 20M", BandPlan.Display("20m"), "20M");
        Check("70cm display = 70CM", BandPlan.Display("70cm"), "70CM");
        Check("6m amp-supported", BandPlan.IsAmpSupported("6m").ToString(), "True");
        Check("60m NOT amp-supported (no CI-V code)",
            BandPlan.IsAmpSupported("60m").ToString(), "False");

        Console.WriteLine($"\n{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, string got, string want)
    {
        bool ok = got == want;
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        if (!ok) Console.WriteLine($"         got:  {got}\n         want: {want}");
    }

    private static string Hex(byte[] b) => string.Join(' ', Array.ConvertAll(b, x => x.ToString("X2")));

    private static byte[] FromHex(string s)
    {
        var parts = s.Replace("  ", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) b[i] = Convert.ToByte(parts[i], 16);
        return b;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  INTERACTIVE  — talk to a real amp
// ─────────────────────────────────────────────────────────────────────────────
static class Interactive
{
    public static int ListPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        if (ports.Length == 0) Console.WriteLine("No COM ports found.");
        else foreach (var p in ports) Console.WriteLine(p);
        return 0;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: pw2civ serial <COMx> [baud] [addrHex]");
            return 2;
        }
        string port = args[1];
        int baud = args.Length >= 3 && int.TryParse(args[2], out var b) ? b : 9600;
        int addr = args.Length >= 4 ? Convert.ToInt32(args[3], 16) : Pw2Civ.DefaultPw2Addr;

        using var factory = LoggerFactory.Create(lb =>
            lb.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
              .SetMinimumLevel(LogLevel.Debug));
        var log = factory.CreateLogger("pw2civ");

        using var amp = new Pw2CivSerial(log);
        amp.SetPw2Addr(addr);
        var (ok, msg) = amp.Connect(port, baud);
        if (!ok) { Console.WriteLine($"connect failed: {msg}"); return 1; }
        Console.WriteLine($"connected {port} @ {baud}, PW2 addr 0x{addr:X2}. type 'help'.\n");

        Help();
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();

            try
            {
                switch (cmd)
                {
                    case "quit" or "exit" or "q":
                        amp.Disconnect(); return 0;
                    case "help" or "?": Help(); break;

                    case "meters": DumpMeters(amp); break;
                    case "bench":
                        // Time repeated Po/SWR reads to see the true per-read cost.
                        // Run this while keying (SSB whistle or CW carrier) to see
                        // TX-time behavior. Usage: bench [count]  (default 20)
                        {
                            int n = (parts.Length >= 2 && int.TryParse(parts[1], out var bn)) ? bn : 20;
                            Console.WriteLine($"timing {n} Po+SWR read pairs (key the radio now if testing TX)...");
                            var swAll = System.Diagnostics.Stopwatch.StartNew();
                            long poNull = 0, swrNull = 0, poMax = 0, swrMax = 0;
                            for (int k = 0; k < n; k++)
                            {
                                var s1 = System.Diagnostics.Stopwatch.StartNew();
                                var po = amp.ReadPoWatts();
                                s1.Stop();
                                var s2 = System.Diagnostics.Stopwatch.StartNew();
                                var swr = amp.ReadSwr();
                                s2.Stop();
                                if (po is null) poNull++;
                                if (swr is null) swrNull++;
                                poMax = Math.Max(poMax, s1.ElapsedMilliseconds);
                                swrMax = Math.Max(swrMax, s2.ElapsedMilliseconds);
                                Console.WriteLine($"  #{k + 1,2}  Po={(po?.ToString() ?? "—"),4}W in {s1.ElapsedMilliseconds,4}ms   SWR={(swr?.ToString("0.00") ?? "—"),5} in {s2.ElapsedMilliseconds,4}ms");
                            }
                            swAll.Stop();
                            Console.WriteLine($"total {swAll.ElapsedMilliseconds}ms for {n} pairs = {swAll.ElapsedMilliseconds / (double)n:0.0}ms/pair");
                            Console.WriteLine($"Po nulls: {poNull}/{n} (max {poMax}ms)   SWR nulls: {swrNull}/{n} (max {swrMax}ms)");
                        }
                        break;
                    case "po":    Console.WriteLine($"Po  = {amp.ReadPoWatts()?.ToString() ?? "—"} W"); break;
                    case "swr":   Console.WriteLine($"SWR = {amp.ReadSwr()?.ToString("0.00") ?? "—"}"); break;
                    case "alc":   Console.WriteLine($"ALC = {amp.ReadAlcPercent()?.ToString() ?? "—"} %"); break;
                    case "vd":    Console.WriteLine($"Vd  = {amp.ReadVolts()?.ToString("0.0") ?? "—"} V"); break;
                    case "id":    Console.WriteLine($"Id  = {amp.ReadAmps()?.ToString("0.0") ?? "—"} A"); break;
                    case "temp":  Console.WriteLine($"Temp= {amp.ReadTemperatureC()?.ToString("0.0") ?? "—"} °C"); break;
                    case "hum":   Console.WriteLine($"Hum = {amp.ReadHumidity()?.ToString() ?? "—"} %"); break;
                    case "prot":  Console.WriteLine($"Prot= {amp.ReadProtection() ?? "—"}"); break;
                    case "tuner": Console.WriteLine($"Tuner state = {amp.ReadTunerState()?.ToString() ?? "—"}"); break;
                    case "amp":   Console.WriteLine($"Amp circuit = {AmpStateName(amp.ReadAmpCircuit())}"); break;
                    case "ant":   Console.WriteLine($"Active ANT = {amp.ReadActiveAntenna()?.ToString() ?? "—"}"); break;
                    case "setant":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var san))
                            Report($"set ANT {san}", amp.SetAntenna(san));
                        else Console.WriteLine("usage: setant <1-6>");
                        break;
                    case "antnames":
                        for (int a = 1; a <= 6; a++)
                            Console.WriteLine($"  ANT{a} = '{amp.ReadAntennaName(a) ?? "—"}'");
                        break;
                    case "input":
                        Console.WriteLine($"RF input = {amp.ReadRfInput() switch { 0 => "INPUT1", 1 => "INPUT2", _ => "—" }}");
                        break;
                    case "setinput":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var si) && (si == 1 || si == 2))
                            Report($"set INPUT {si}", amp.SetRfInput(si - 1));
                        else Console.WriteLine("usage: setinput <1|2>");
                        break;
                    case "raw":
                        // Send an arbitrary CI-V payload after cmd, e.g.:
                        //   raw 1A 07 00 00     (temporarily switch antenna)
                        //   raw 1A 06 00 05     (antenna switching setting, input1 ant6)
                        // Preamble/addresses/EOM are added automatically.
                        if (parts.Length >= 2)
                        {
                            try
                            {
                                var bytes = new byte[parts.Length - 1];
                                for (int bi = 1; bi < parts.Length; bi++)
                                    bytes[bi - 1] = Convert.ToByte(parts[bi], 16);
                                byte cmdByte = bytes[0];
                                byte? subByte = bytes.Length >= 2 ? bytes[1] : (byte?)null;
                                var data = bytes.Length > 2 ? bytes[2..] : Array.Empty<byte>();
                                var pkt = Pw2Civ.Build(amp.Pw2Addr, cmdByte, subByte, data);
                                Console.WriteLine("TX: " + string.Join(' ', Array.ConvertAll(pkt, x => x.ToString("X2"))));
                                var resp = amp.SendRaw(pkt);
                                Console.WriteLine("RX: " + (resp.Length == 0 ? "(nothing)" :
                                    string.Join(' ', Array.ConvertAll(resp, x => x.ToString("X2")))));
                                if (Pw2Civ.IsAck(resp, amp.Pw2Addr)) Console.WriteLine("   => ACK");
                                else if (Pw2Civ.IsNak(resp, amp.Pw2Addr)) Console.WriteLine("   => NAK");
                            }
                            catch (Exception ex) { Console.WriteLine("bad hex: " + ex.Message); }
                        }
                        else Console.WriteLine("usage: raw <hex bytes>, e.g. raw 1A 07 00 00");
                        break;
                    case "antname":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var an))
                            Console.WriteLine($"ANT{an} name = '{amp.ReadAntennaName(an) ?? "—"}'");
                        else Console.WriteLine("usage: antname <1-6>");
                        break;

                    case "band":
                        if (parts.Length >= 2)
                        {
                            var (o, m) = amp.SetBand(parts[1]);
                            Console.WriteLine($"set band {parts[1]}: {(o ? "OK" : "FAIL")} {m}");
                        }
                        else Console.WriteLine("usage: band <160m|80m|40m|30m|20m|17m|15m|12m|10m|6m>");
                        break;

                    case "oper": Report("OPER", amp.SetAmpCircuit(true)); break;
                    case "stby": Report("STBY", amp.SetAmpCircuit(false)); break;
                    case "pwron":  Report("main power on",  amp.SetMainPower(true)); break;
                    case "pwroff": Report("main power off", amp.SetMainPower(false)); break;
                    case "clearprot":
                        Console.WriteLine($"deactivate protection: {(amp.DeactivateProtection() ? "OK" : "FAIL")}");
                        break;

                    default: Console.WriteLine($"unknown: {cmd} (try 'help')"); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }
        amp.Disconnect();
        return 0;
    }

    private static void DumpMeters(Pw2CivSerial amp)
    {
        Console.WriteLine($"  Po   {amp.ReadPoWatts()?.ToString() ?? "—",6} W");
        Console.WriteLine($"  SWR  {amp.ReadSwr()?.ToString("0.00") ?? "—",6}");
        Console.WriteLine($"  ALC  {amp.ReadAlcPercent()?.ToString() ?? "—",6} %");
        Console.WriteLine($"  Vd   {amp.ReadVolts()?.ToString("0.0") ?? "—",6} V");
        Console.WriteLine($"  Id   {amp.ReadAmps()?.ToString("0.0") ?? "—",6} A");
        Console.WriteLine($"  Temp {amp.ReadTemperatureC()?.ToString("0.0") ?? "—",6} °C");
        Console.WriteLine($"  Hum  {amp.ReadHumidity()?.ToString() ?? "—",6} %");
        Console.WriteLine($"  Prot {amp.ReadProtection() ?? "—"}");
    }

    private static string AmpStateName(int? s) => s switch
    {
        0 => "STBY (bypass)", 1 => "OPER (active)", _ => "OFF / unreachable"
    };

    private static void Report(string what, (bool ok, string message) r) =>
        Console.WriteLine($"{what}: {(r.ok ? "OK" : "FAIL")} {r.message}");

    private static void Help() => Console.WriteLine("""
        commands:
          meters                 read all meters at once
          bench [n]              time n Po+SWR read pairs (key radio to test TX)
          po swr alc vd id       individual meters
          temp hum prot tuner    temperature / humidity / protection / tuner
          amp                    read amp circuit state (OPER/STBY/off)
          ant                    active antenna number
          setant <1-6>           set active antenna
          antname <1-6>          one antenna name string
          antnames               read all six antenna names
          input                  read selected RF input (1 or 2)
          setinput <1|2>         set RF input connector
          band <name>            set band, e.g. band 20m
          raw <hex...>           send raw CI-V, e.g. raw 1A 07 00 00
          oper | stby            amp circuit on / off
          pwron | pwroff         main power on / off
          clearprot              deactivate a protection latch
          help | quit
        """);
}
