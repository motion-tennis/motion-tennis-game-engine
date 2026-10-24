# Motion Tennis Game Engine

The state-storage and visualization component of **Motion Tennis**, a multiplayer tennis demonstrator for up to four players. It runs on the user's **Windows laptop with an RTX 5080**, stores committed ball snapshots, answers Pi queries and displays player/racket motion, ball motion, gameplay events and timing diagnostics.

## Status and reference implementation

| Status | Meaning |
| --- | --- |
| **Implemented** | Documentation and an MIT license only; no state service, renderer, dependency lock, build files, assets or tests exist |
| **Agreed design** | Windows host, webcam-first calibrated 2.5D gameplay, state queries/results exchanged with the Raspberry Pi 3 Model B, and observable four-player behavior |
| **Proposed reference** | Native C++20 state service plus a separate C++/SDL3 simple renderer process; version-checked atomic batches and bounded state publication |
| **Open** | Verified Windows/compiler/SDL3 versions, calibrated scene geometry, measured load isolation, display latency and deployment packaging |

The Windows reference supersedes the earlier Linux-first PC assumption. No Unity, Godot or Unreal implementation is implied. The GPU camera worker is a separate service/repository on the same laptop; shared CPU/GPU resources can still affect timing. See the [master plan](https://github.com/motion-tennis/documentation/blob/main/docs/master-plan.md) and [game integration design](https://github.com/motion-tennis/documentation/blob/main/docs/game-engine.md).

## Responsibilities and state ownership

The state service owns the sole committed ball snapshot and ordered committed-event history. Physics on the Pi calculates every authoritative advance, including periodic movement, collisions and UI-requested resets/serves. The game service validates and commits; the renderer reads. Interpolation for display never becomes a competing ball integrator.

For each valid `ball_state_request`, return `ball_state_response` echoing every logical query entry and one atomic snapshot with game session, version, simulation tick and tick duration. A repeated query ID returns its retained response within the protocol lifetime; a previous push does not replace the required query/response.

Accept a `state_update` only for the current game session and fenced physics writer epoch with an exact matching base version. Validate the bounded ordered batch, final ball state and input/calibration metadata, then commit the state and event list atomically. Return `state_commit_result`; reject the entire conflicting/invalid batch without partial effects. Duplicate update keys return the original outcome instead of committing twice. A current snapshot alone cannot resolve whether a particular uncertain batch committed.

Four controllers at 200 Hz are grouped by the Pi into 5 ms batches, normally four decisions, at most eight and at most two/controller. Every measurement keeps its own logical ball query and result. One batch shares a committed version; `event_index` preserves intra-batch order. Only one physics state batch is in flight. Hit/miss controller feedback follows accepted commit confirmation. See [physics consistency](https://github.com/motion-tennis/documentation/blob/main/docs/physics.md).

## Connections and process separation

These are proposed [`0.2-proposed` contracts](https://github.com/motion-tennis/documentation/blob/main/docs/protocol.md), with the shared [schema](https://github.com/motion-tennis/documentation/blob/main/spec/motion.proto) and [reference profile](https://github.com/motion-tennis/documentation/blob/main/spec/reference-profile.json).

| Direction | Interface | Component |
| --- | --- | --- |
| Input / output | `ball_state_request` / `ball_state_response`, UDP 41010 | Pi physics |
| Input / output | `state_update` / `state_commit_result`, same UDP endpoint | Pi physics |
| Control | Writer setup, profile/hash agreement, clock mapping, commit lookup and state subscription, TCP 41041 | Physics, local renderer and debug clients |
| Local publication | `state_publication` with committed snapshot and ordered events over a bounded subscription | Separate renderer/debug consumers |
| Optional input | Independent pose/frame-status display subscription | Webcam tracking service |
| Diagnostics | Buffered state, query, commit and render records | `debug-units` |

Live UDP/Protobuf messages are limited to 1,200 bytes including the envelope. An atomic update must fit; it is never split into separately applied fragments. Network ports are reference reservations, not currently listening services. Wire the Windows host to the dedicated LAN and keep camera video local. See [networking](https://github.com/motion-tennis/documentation/blob/main/docs/networking.md).

The state service must respond independently of rendering and GPU inference. The renderer targets 60 Hz and may skip obsolete visual snapshots while retaining event identity/accounting. This process separation does not prove a latency bound; test CPU/GPU saturation and Windows scheduling effects. Native state-service handling, IPC/subscription buffering and diagnostics must remain bounded.

## Visualization and timing

Start with a simple court, calibrated lane/player/racket display, ball path, distinct hit/miss effects and explicit error overlays. Mark 2.5D positions as constrained estimates; do not present webcam joints as measured metric depth. Hide or flag stale/ambiguous player poses and suspend their hit eligibility.

Deduplicate effects by `(game_session_id, writer_epoch, batch_id, event_index)`. Show tracking failures, stale inputs, overload, conflicts and timeouts separately from ordinary misses. The UI should expose session/association, frame age, uncertainty, state version/tick, simulation lag, queue depth, and complete latency/failure counts.

Show controller-reported `tr - t0`; do not calculate it by subtracting a PC clock from an ESP32 clock. The targets are p99 application response at most 30 ms, eligible electrical feedback at most 40 ms and controller-local expiry 50 ms. Rendering completion and screen photons are separate from both metrics. A committed event may be displayed even when controller delivery timed out; retain both facts. See [timing](https://github.com/motion-tennis/documentation/blob/main/docs/timing.md) and [debugging](https://github.com/motion-tennis/documentation/blob/main/docs/debugging.md).

## Prerequisites and setup

Git and a Markdown viewer are enough for current documentation. C++20, SDL3 and generated codecs are proposed implementation dependencies whose exact verified versions must be added with real build files. No executable or launch command exists yet.

Clone using the verified remote:

```sh
git clone git@github.com:motion-tennis/motion-tennis-game-engine.git
cd motion-tennis-game-engine
```

Configured GitHub access is required. Planned modules are session/writer management, atomic snapshot/query handling, batch validation/deduplication, bounded state publication, separate renderer/operator controls, and trace adapters. Keep dependency locks, configuration and build/run commands beside their first implementations.

The [integrated startup plan](https://github.com/motion-tennis/documentation/blob/main/docs/development.md) initializes the game session before enabling physics writes, pairing and gameplay. A game restart creates a new session; a replacement physics process receives a new writer epoch. UI reset/serve actions go through physics or a deliberately fenced new session rather than directly moving committed state.

## Planned smoke test and current checks

Answer a batch query with an atomic snapshot; accept one matching ordered update; publish the committed result; then replay the update and submit a stale base version. Verify no duplicate commit/effect and no partial batch acceptance. Test ambiguous replies, restarted writers, periodic movement without controller input and response latency under rendering/GPU load. Repeat with one through four controllers using the [test matrix](https://github.com/motion-tennis/documentation/blob/main/docs/testing.md).

No state service, renderer or hardware/display timing test exists. Current repository-local check:

```sh
git diff --check
```

Use the central [development checker](https://github.com/motion-tennis/documentation/blob/main/docs/development.md) for cross-repository documentation. A rendered frame is not optical presentation evidence; photodiode measurements require a separate experiment.

## Layout, limitations, and contribution

```text
README.md  Component reference profile and implementation status
LICENSE    Existing MIT license
```

State-service capacity, renderer behavior, process interference, calibration, restart recovery and display timing are unverified. See [open decisions](https://github.com/motion-tennis/documentation/blob/main/docs/roadmap.md).

All project edits must be in English, including code, identifiers, comments, docstrings, examples and diagrams. New C/C++ comments use `//`. Keep shared contracts and all affected READMEs synchronized, and distinguish proposed behavior from implemented and measured results.

Related repositories: [documentation](https://github.com/motion-tennis/documentation), [controller](https://github.com/motion-tennis/motion-tennis-controller), [camera tracking](https://github.com/motion-tennis/motion-tennis-camera-tracking), [physics](https://github.com/motion-tennis/motion-tennis-physics-engine), and [debug-units](https://github.com/motion-tennis/debug-units).

Licensed under the [MIT License](LICENSE), copyright 2026 Viktor Hofer. Preserve this attribution.
