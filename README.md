# Motion Tennis Game Engine

The state-storage and visualization component of **Motion Tennis**, a doubles 2v2 tennis demonstrator inspired by Wii Tennis. It runs on the user's **Windows laptop with an RTX 5080**, stores the committed match state under a planned extended contract, answers Pi queries and displays two team-relative views, automatic avatar movement, body-local racket gestures, scoring and timing diagnostics. Human participants remain at their calibrated physical play stations.

## Status and reference implementation

| Status | Meaning |
| --- | --- |
| **Implemented** | Documentation and an MIT license only; no state service, renderer, dependency lock, build files, assets or tests exist |
| **Agreed design** | Windows host; doubles 2v2 with automatic avatar movement and stationary participants; controller-lift toss followed by overhead serve; normal tennis scoring; two elevated behind-team views of one shared world; active camera/IMU input and required Pi state query/response |
| **Proposed reference** | Native C++20 state service plus a separate C++/SDL3 simple renderer process; version-checked atomic batches and bounded state publication |
| **Open** | Versioned full-match schema/size proof, verified Windows/compiler/SDL3 versions, calibrated body-local gesture mapping, camera/split-layout tuning, measured load isolation, display latency and packaging |

The Windows reference supersedes the earlier Linux-first PC assumption. C++20/SDL3 remains a proposed stack; a stylized perspective/2.5D projection is possible, and no full 3D engine, Unity, Godot or Unreal implementation is implied. The GPU camera worker is a separate service/repository on the same laptop; shared CPU/GPU resources can still affect timing. See the [gameplay rules](https://github.com/motion-tennis/documentation/blob/main/docs/gameplay.md), [player assignment](https://github.com/motion-tennis/documentation/blob/main/docs/player-assignment.md), [ADR 0010](https://github.com/motion-tennis/documentation/blob/main/docs/adr/0010-doubles-auto-movement-gameplay.md) and [game integration design](https://github.com/motion-tennis/documentation/blob/main/docs/game-engine.md).

## Responsibilities and state ownership

The planned state service owns the sole committed match snapshot and ordered committed-event history. Physics on the Pi calculates every authoritative advance: ball movement/collisions, automatic avatar movement, serve phases and scoring. The game service validates and commits the entire candidate atomically; the renderer reads. There is no second PC scorer or independent avatar AI. Interpolation for display never becomes competing game authority.

**Contract gap:** the current proposed `motion.proto` snapshot contains ball state and transaction/event metadata, but no complete match roster, avatar state, serve state or score fields. A reviewed versioned extension must define those bounded fields and their invariants, including current court ends and server/receiver. This documentation does not modify the schema or implement those features. All participants must adopt the extended contract before full-match integration; the mandatory ball query/response remains part of that design.

The planned [IMU threshold-marker overlay](https://github.com/motion-tennis/documentation/blob/main/docs/imu-trigger-markers.md) uses existing transaction/query/batch/commit/event identities to show state-service and rendering stages. Keep their trace provenance without adding marker fields or dependencies to gameplay messages. The 200 Hz controller stream and ordinary query/commit handling remain unchanged; display timing is a separate branch from controller feedback.

For each valid `ball_state_request`, return `ball_state_response` echoing every logical query entry and one atomic snapshot with game session, version, simulation tick and tick duration. A repeated query ID returns its retained response within the protocol lifetime; a previous push does not replace the required query/response.

Accept a `state_update` only for the current game session and fenced physics writer epoch with an exact matching base version. Under the planned extended contract, validate the bounded ordered batch, final ball/avatar/serve/score state, roster associations and input/calibration metadata, then commit the whole match state and event list atomically. Return `state_commit_result`; reject the entire conflicting/invalid batch without partial effects. Duplicate update keys return the original outcome instead of committing twice. A current snapshot alone cannot resolve whether a particular uncertain batch committed.

Four controllers at 200 Hz are grouped by the Pi into 5 ms batches, normally four decisions, at most eight and at most two/controller. Every measurement keeps its own logical ball query and result. One batch shares a committed version; `event_index` preserves intra-batch order. Only one physics state batch is in flight. Hit/miss controller feedback follows accepted commit confirmation. See [physics consistency](https://github.com/motion-tennis/documentation/blob/main/docs/physics.md).

## Connections and process separation

These are the current proposed [`0.2-proposed` transport contracts](https://github.com/motion-tennis/documentation/blob/main/docs/protocol.md), with the shared [schema](https://github.com/motion-tennis/documentation/blob/main/spec/motion.proto) and [reference profile](https://github.com/motion-tennis/documentation/blob/main/spec/reference-profile.json). Message names below do not imply that the missing full-match fields already exist; that contract extension remains required.

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

## Pi reference clock mapping

The [agreed clock design](https://github.com/motion-tennis/documentation/blob/main/docs/coordinates-and-time.md#pi-reference-clock-mapping) uses Pi-owned offset/drift mappings, `T_pi = a * t_source + b`, while preserving native Windows monotonic timestamps and clock/session identities. Respond to Pi-coordinated `ClockExchange` on existing TCP control with timestamps at complete-frame receipt and the declared response-send boundary. Game, renderer and tracking may share a verified host-clock fit, but each producer session retains its own bound mapping record/ID; distinct clock domains require separate fits. Do not step their clocks.

Runtime timing checks and debug displays use the same immutable mapping version, uncertainty and source/Pi reference epochs. Invalidate a restarted source's mapping; a new Pi reference epoch invalidates every live mapping. Unknown, expired or insufficiently accurate maps make cross-device freshness and duration claims unavailable; retain transaction/failure accounting and controller-reported local RTT. Mapping integration remains unimplemented and adds no fields to gameplay messages or changes to the continuous controller stream.

## Lobby, teams and split views

The startup lobby supports explicit team selection and physical person/controller/hand confirmation. Fixed slots are `1 = A1`, `2 = A2`, `3 = B1`, `4 = B2`, each binding one controller, person and avatar. Team identity is stable; front/back positioning is a changeable court role. Use the existing association-installation acknowledgement and resume barrier before readiness. All four assigned players must be ready for a full match; no automatic AI substitute fills an empty slot. One-through-four-controller tests are diagnostic workloads. Follow the canonical [assignment contract](https://github.com/motion-tennis/documentation/blob/main/docs/player-assignment.md).

Display two simultaneous views of the same committed world/version. Each camera is elevated and angled from behind its own team's baseline: own team near, opponents far. A horizontal split with team A above and team B below is proposed; split orientation is not an agreed requirement. On normal court-end changes, cameras move to their teams' new sides while slot/team/controller/person/avatar identity and viewport ownership remain fixed. Camera transforms affect presentation only, not input meaning, collision geometry or world state.

Avatar running comes from Pi-calculated match state. Stationary participants still supply active camera/IMU arm and racket gestures, orientation and timing; a pose subscription may animate body-local motion but cannot replace the committed avatar root position or compute another hit. Mark constrained webcam geometry as 2.5D and stale/ambiguous observations as faults; do not invent measured depth or let displayed animation restore invalid hit eligibility.

Bound interpolation to event-consistent segments and split it at racket contacts, bounces, net contacts, point/serve transitions and resets. Both views must show the same contact result; smoothing must not carry the ball through a court/net surface or feed a visual collision into physics. Deduplicate effects by `(game_session_id, writer_epoch, batch_id, event_index)`.

## Scoreboard, serve cues and timing

Read normal point/game/set scoring and active tiebreak context from committed state according to the [gameplay rules](https://github.com/motion-tennis/documentation/blob/main/docs/gameplay.md). Show `0/15/30/40/AD`, deuce/advantage, games/sets, team/slot labels, server and current serve attempt. The selected server lifts the controller to initiate the toss, then performs the overhead serve contact through the normal tracked input path. Show the committed toss/serve phase; UI animations or ordinary buttons do not replace these gestures or award points.

Keep connection/battery/readiness, tracking/calibration faults and pause state visible. A proposed toggleable detailed overlay exposes session/association, pose age/prediction, clock uncertainty/status, version/tick, simulation lag, queues, transaction/marker/event IDs and complete timing/failure counts. Toggling it cannot hide essential faults or change gameplay. Show tracking failures, stale inputs, overload, conflicts and timeouts separately from ordinary misses; stale diagnostics do not imply a healthy source.

Show controller-reported `tr - t0`; do not calculate it by subtracting a PC clock from an ESP32 clock. The targets are p99 application response at most 30 ms, eligible electrical feedback at most 40 ms and controller-local expiry 50 ms. Rendering completion and screen photons are separate from both metrics. A committed event may be displayed even when controller delivery timed out; retain both facts. See [timing](https://github.com/motion-tennis/documentation/blob/main/docs/timing.md) and [debugging](https://github.com/motion-tennis/documentation/blob/main/docs/debugging.md).

## Prerequisites and setup

Git and a Markdown viewer are enough for current documentation. C++20, SDL3 and generated codecs are proposed implementation dependencies whose exact verified versions must be added with real build files. No executable or launch command exists yet.

Clone using the verified remote:

```sh
git clone git@github.com:motion-tennis/motion-tennis-game-engine.git
cd motion-tennis-game-engine
```

Configured GitHub access is required. Planned modules are versioned full-match codecs, session/writer and roster management, atomic snapshot/query handling, match-batch validation/deduplication, bounded state publication, a separate two-view renderer, team/pairing lobby, scoreboard/serve UI and trace adapters. Keep dependency locks, configuration and build/run commands beside their first implementations.

The [integrated startup plan](https://github.com/motion-tennis/documentation/blob/main/docs/development.md) initializes the game session before enabling physics writes, team/person assignment and gameplay. A game restart creates a new session; a replacement physics process receives a new writer epoch. Team selection, pairing, pause/resume and reset use versioned control handling. Normal serves come from controller gestures; explicit diagnostic serve/reset commands go through physics or a deliberately fenced new session rather than directly moving committed state.

## Planned smoke test and current checks

First specify the extended match contract and prove its maximum encoded size. Answer a batch query with an atomic snapshot; accept one matching ordered match update; publish the committed result; then replay the update and submit a stale base version. Verify no duplicate commit/effect, partial batch acceptance or score/serve/avatar inconsistency. Test ambiguous replies, restarted writers, periodic movement without controller input and response latency under two-view rendering/GPU load.

Verify four-player lobby readiness, fixed team/avatar identity through role and court-end changes, lift/toss/overhead serve transitions, scoreboard cases from canonical rule fixtures, and agreement of both cameras on world state and collision events. Preserve the continuous 200 Hz stream, marker tracing and existing timing populations. Diagnostic tests with one through four controllers follow the [test matrix](https://github.com/motion-tennis/documentation/blob/main/docs/testing.md); a full match requires all four ready players.

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
