// SPDX-License-Identifier: GPL-2.0-or-later
//
// Frequency -> band-name mapping, including the 60m split introduced for the
// FCC rules effective Feb 13, 2026. Ported from freq_to_band()/FREQ_BANDS in
// the Python bridge. Band NAMES here are internal keys; the IC-PW2 CI-V band
// CODES live in Pw2Civ.BandCode.

using System.Collections.Generic;

namespace Zeus.Community.Pw2Bridge;

public static class BandPlan
{
    // 60m "new" sub-band: 5351.5-5366.5 kHz, 15W EIRP / 9.15W ERP — amp must
    // stay in STBY. Everything else on 60m is normal 100W ERP channel use.
    public const long Freq60mNewLo = 5_351_500;
    public const long Freq60mNewHi = 5_366_500;

    private static readonly (long Lo, long Hi, string Name)[] Ranges =
    {
        (1_800_000,   2_000_000,   "160m"),
        (3_500_000,   4_000_000,   "80m"),
        (5_330_000,   5_351_499,   "60m"),       // channels below the new sub-band
        (Freq60mNewLo, Freq60mNewHi, "60m-new"), // low-power FCC sub-band
        (5_366_501,   5_410_000,   "60m"),       // channels above the new sub-band
        (7_000_000,   7_300_000,   "40m"),
        (10_100_000,  10_150_000,  "30m"),
        (14_000_000,  14_350_000,  "20m"),
        (18_068_000,  18_168_000,  "17m"),
        (21_000_000,  21_450_000,  "15m"),
        (24_890_000,  24_990_000,  "12m"),
        (28_000_000,  29_700_000,  "10m"),
        (50_000_000,  54_000_000,  "6m"),
        (144_000_000, 148_000_000, "2m"),        // not amp-supported; still named
        (430_000_000, 450_000_000, "70cm"),      // not amp-supported; still named
    };

    /// <summary>Band name for a frequency in Hz, or null if out of band.</summary>
    public static string? FromHz(long hz)
    {
        foreach (var (lo, hi, name) in Ranges)
            if (hz >= lo && hz <= hi) return name;
        return null;
    }

    /// <summary>True for the 5351.5-5366.5 kHz FCC sub-band where the amp must stay in STBY.</summary>
    public static bool Is60mLowPower(string? band) => band == "60m-new";

    /// <summary>User-facing label: "20m" -> "20M", "70cm" -> "70CM", "60m-new" -> "60M-NEW".</summary>
    public static string Display(string? band)
    {
        if (string.IsNullOrEmpty(band)) return band ?? "";
        if (band == "60m-new") return "60M-NEW";
        if (band.EndsWith("cm")) return band[..^2].ToUpperInvariant() + "CM";
        if (band.EndsWith("m")) return band[..^1].ToUpperInvariant() + "M";
        return band;
    }

    /// <summary>True if the PW2 has a CI-V band code for this band (i.e. it can be forwarded).</summary>
    public static bool IsAmpSupported(string? band) =>
        band != null && Pw2Civ.BandCode.ContainsKey(band);
}
