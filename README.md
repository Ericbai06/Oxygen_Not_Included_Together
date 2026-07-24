# ONI ONLINE

ONI ONLINE is an independent, unofficial multiplayer fork for Oxygen Not Included. Version 1.0.8 targets game build `U59-740622-S` and uses network protocol version `11`.

The project is based on the MIT-licensed [ONI Together](https://github.com/Lyraedan/Oxygen_Not_Included_Together) project by Lyraedan (ItsLuke), Sgt_Imalas, and its contributors. The upstream project remains active. ONI ONLINE has a separate name, Workshop item, release line, and network protocol; it does not replace or represent the upstream project.

## Status

- The base game and Spaced Out! have completed two-machine runtime validation.
- Frosty Planet Pack, Bionic Booster Pack, Prehistoric Planet Pack, Neutronium Cosmetics Pack, and Aquatic Planet Pack have dedicated synchronization code and compile in the release build.
- Shared colony mutations follow one authority path: a client submits a request, the host validates the sender, session, target, revision, and domain rules, then executes the accepted action and broadcasts the resulting authoritative state. Clients do not treat their local action as committed state.
- Joining and reconnecting clients load a generation-bound full snapshot before reliable live updates resume.
- Steam play uses friends-only lobbies over SteamNetworkingSockets. Players can join by lobby code or Steam invite without port forwarding, a public IP address, or a LAN tunnel.
- LAN play uses Riptide UDP. Large saves use the adjacent TCP port, with chunked UDP transfer as a fallback.
- The handshake admits a peer when its `ONI_Together.dll` SHA-256 and active DLC set exactly match the host.
- The dedicated-server prototype remains in the repository but is not included in the Workshop release.

## Installation

See [INSTALL.md](INSTALL.md) for Steam Workshop installation, source builds, LAN ports, and release packaging.

Do not enable a Workshop copy and a local source build at the same time. Every player must use an `ONI_Together.dll` with the same SHA-256 and enable the same DLC set. A rejected client now sees the specific DLL or DLC mismatch instead of a generic connection-loss message. Other enabled Mods, load order, and configuration are not admission checks.

## Usage

1. Enable `ONI ONLINE` for the active DLC on the Mods screen and restart the game.
2. Open Multiplayer from the main menu.
3. For Steam play, the host creates a lobby and shares its code or sends a Steam invite. Each player runs the game from a separate Steam account.
4. For LAN play, the host listens on UDP `8080` by default. Save transfer uses TCP `8081`.

SteamNetworkingSockets handles NAT traversal and relay selection for Steam sessions. Steam play does not use the LAN address or require a port-forwarding rule. Tunnels are only relevant to the separate direct-LAN transport.

## Synchronization model

The host owns authoritative colony state. A joining client receives a full snapshot tied to the current session generation. Reliable changes produced during loading are retained until the client confirms that the snapshot is loaded.

For shared colony mutations, the client sends intent rather than applying a second authoritative game action. The host checks connection identity, session generation, target validity, permissions, and revision ordering. Accepted requests run through the host's game state and produce a state packet for every peer. Duplicate, stale, and out-of-order state is rejected before application. Client patches suppress the corresponding original local side effect while applying the host result.

Runtime validation splits state into five domains: `grid`, `entity`, `storage`, `world`, and `rocket`. Each validation segment records the raw hashes, applies a host keyframe, waits for its acknowledgement, and compares the post-keyframe hashes.

Entity lifecycle updates use monotonic revisions and tombstones. Failed identity claims restore the previous NetId, lifecycle journal, position, and active state instead of leaving a partial binding.

## Relationship to upstream

ONI ONLINE keeps the upstream MIT copyright and license notices and links back to the active ONI Together project. Development continues in this fork because protocol 11 changes the network contract across request authority, transport, replay, reconnect, entity lifecycle, revision handling, DLC synchronization, and the integration-test harness. Those changes are interdependent and are not wire-compatible with the upstream 0.7.x release line. Submitting the entire fork as one pull request would create a large, non-incremental review and mix architectural decisions with individual fixes.

Fixes that can be isolated from protocol 11 and reviewed independently should be proposed upstream as focused pull requests. The separate fork is retained for the protocol-level work and its release history.

## Validation record

On July 20, 2026, the v1.0.5 code candidate completed 554 in-game Debug checks on both the macOS host and an Alienware Windows client: 528 passed, none failed, and 26 were skipped because the required runtime state was not present. The Windows client then downloaded the host save, applied all 874 world-baseline parts, and entered `InGame` after Ready acknowledgement 1.

The two-machine Steam friends soak used two Steam accounts and ran for 21 segments and 37,800 ticks with ONI MCP Server disabled. Time and all five post-keyframe domain hashes matched in every segment. The final record reported `postMismatchSeen=False`, `keyframeApplyFailureSeen=False`, and `postKeyframeEqual=True`; lifecycle missing, unexpected, tombstoned-live, and unassigned counts were all zero.

The client was then closed, restarted through Steam, and joined the same lobby code. It reapplied all 1,040 world-baseline parts, entered `InGame`, and completed reconnect setup after Ready acknowledgement 2.

Raw drift appeared in `grid`, `entity`, `world`, and `storage` at the first segment. This mod uses host-authoritative repair rather than deterministic lockstep. A post-keyframe mismatch is a release failure.

## Development

Debug builds expose three in-game entry points:

- `Shift+F2` opens the test menu.
- `Shift+F3` discovers and runs all in-game unit tests.
- `Shift+F4` runs the Riptide loopback smoke test on `127.0.0.1:27777`.

The protocol 11 test harness defines 22 real two-machine business scenarios covering colony controls, buildings, inventory, presentation, entity lifecycle, DLC systems, rockets, and reconnect. Each scenario requires host submission, client application, blocked client-side original execution, revision ordering, and matching final state hashes.

The v1.0.8 replacement-admission candidate ran 816 in-game Debug tests on macOS: 799 passed, 15 were skipped because their runtime prerequisites were absent, and two existing unrelated tests failed because the PlanScreen was active and the world-repair staging queue was full. All five replacement tests passed, including same-definition material changes and rotated footprints, and the causal log window contained no duplicate replacement-tile warning. The 83 headless contracts also passed. Full protocol 11 two-machine scenario and soak acceptance remains pending for this release candidate.

The July 20 macOS-to-macOS regression now uses identical Debug DLLs with SHA-256 `7dae355a37ddc67d9bd18aa857831da1edb7f1f6fefb5d9077629866c56796a9`. The in-game suite reported `total=600, passed=574, failed=0, notRun=26`. A loaded Cycle 119 world completed all 1,040 baseline parts in about 300.6 seconds and entered `InGame`, crossing the former 240-second client limit while valid progress renewed the host idle lease. The immediately preceding transport-equivalent build also completed an in-place hard sync with 512 retained reliable frames carrying 4,096-byte payloads: the client applied the full baseline and 2,109,440-byte replay before the host committed Ready. A paused production checkpoint reported `mismatch=None`. Queued Tile construction at cell `94290` passed through client `BuildStatePacket`, remote worker activity, host `BuildCompletePacket`, and client finalization without `Constructable.OnSpawn`, `SelectedElementsTags`, or NetId collision errors.

The same final DLL completed the 21-segment agent-driven soak in the loaded Cycle 119 world. It advanced 37,800 simulation ticks, or 630 simulation seconds. All 21 post-keyframe comparisons matched time, grid, entity, world, storage, and cluster-rocket state; all lifecycle counters stayed at zero. The terminal record reported `postMismatchSeen=False`, `keyframeApplyFailureSeen=False`, and `postKeyframeEqual=True`. No queue overflow, snapshot-lease expiry, barrier timeout, soak failure, soak abort, or connection loss occurred after `BASELINE_READY`.

The project targets `netstandard2.1`. One DLL is used on macOS, Windows, and Linux, while the release directory includes the UI asset bundle for each platform.

## License

ONI ONLINE is distributed under the upstream project's [MIT License](LICENSE.md). Original project credit and third-party notices are preserved in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
