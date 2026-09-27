<!-- SPDX-License-Identifier: GPL-2.0-or-later -->
# SDK source boundary

The SDK is an ABI-matched source snapshot of only the public
`Zeus.Plugins.Contracts/` contract project. Package metadata is pinned to SDK
version 1.5.0 so this standalone repository does not inherit the Zeus product
version.

Included boundary:

- public interfaces and records in the `Zeus.Plugins.Contracts` namespace;
- manifest and registry DTOs;
- public audio, logbook, backend, and UI extension contracts;
- the SDK project file.

Excluded boundary:

- ZeusProduct and all proprietary `zeus-web` code;
- plugin-host/loading/installation implementations;
- station-engine services and persistence;
- DSP, radio protocol, transmit, and PureSignal logic;
- private configuration, credentials, native libraries, and compiled binaries.

Every SDK source/project file carries
`SPDX-License-Identifier: GPL-2.0-or-later`. CI rejects a missing or different
SPDX identifier, unexpected file types, compiled artifacts, and references to
the excluded implementation assemblies.
