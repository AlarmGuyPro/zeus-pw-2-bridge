# IC-PW2 Bridge for Zeus SDR

A Zeus SDR community feature that controls and monitors an **Icom IC-PW2** 1 kW
linear amplifier over its CI-V serial link: live metering, OPER/STBY, power,
input/antenna/tuner control, automatic band-follow from Zeus radio state, and
safety interlocks (60 m sub-band STBY, overheat latch, protection alarm,
stale-data failsafe). It never keys the transmitter and never touches
PureSignal.

- **Feature ID:** `com.kq4wlr.zeus.pw2bridge`
- **Author:** KQ4WLR
- **License:** GPL-2.0-or-later (`LICENSE`); bundled components in
  `THIRD_PARTY_NOTICES.md`
- **Platform:** Windows x64

**Operators:** start with the user guide in
[`src/Pw2Bridge/README.md`](src/Pw2Bridge/README.md) (wiring, baud rate,
what the meters mean, safety interlocks).
**Maintainers:** [`HANDOFF.md`](HANDOFF.md) records the design and the
hardware-verified CI-V quirks. Read it before changing protocol code.

## Repository layout

```
src/Pw2Bridge/         the feature: C# backend, ui/ panel, plugin.json,
                       operator README, build-package.ps1 (catalog template,
                       unmodified)
tools/Pw2CivHarness/   console harness sharing the protocol source:
                       `selftest` (no hardware) and `serial` (live amp)
sdk/                   public Zeus SDK contracts (ABI 1 / SDK 1.5.0), vendored
                       unmodified from Zeus-SDR/zeus-community-features
```

## Build and package (Windows)

Requires the .NET 10 SDK and PowerShell 7 (`pwsh`).

```powershell
# 1. Protocol self-test (no hardware needed; exit code 0 = pass)
dotnet run --project tools/Pw2CivHarness -- selftest

# 2. Build + package the feature
pwsh src/Pw2Bridge/build-package.ps1 -ManagedDependency System.IO.Ports.dll
```

The package and its checksum are written to
`artifacts/com.kq4wlr.zeus.pw2bridge/`:

- `com.kq4wlr.zeus.pw2bridge-<version>.zip`
- `com.kq4wlr.zeus.pw2bridge-<version>.zip.sha256`

The ZIP contains `plugin.json`, `Zeus.Community.Pw2Bridge.dll` and its
`.deps.json`, `System.IO.Ports.dll`, `ui/pw2bridge.es.js`, `README.md`,
`LICENSE`, and `THIRD_PARTY_NOTICES.md`.

Install it in Zeus with **Features → Community → Install local feature**.

## Releasing a new version

1. Bump the version in `src/Pw2Bridge/plugin.json` **and** `ManifestVersion`
   in `src/Pw2Bridge/Pw2BridgePlugin.cs`.
2. Build and package from a clean checkout, install the ZIP in Zeus, and test
   on the amplifier.
3. Commit, tag `v<version>`, push the tag, and publish a GitHub Release with
   that exact ZIP attached. Never replace a released ZIP; publish a new version
   instead.
4. Submit a `feat(registry): release com.kq4wlr.zeus.pw2bridge <version>` pull
   request to
   [Zeus-SDR/zeus-community-features](https://github.com/Zeus-SDR/zeus-community-features)
   per its `CONTRIBUTING.md`.
