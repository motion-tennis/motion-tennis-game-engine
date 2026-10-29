# Game engine staged workflow

[Repository home](../README.md) · [Orientation](orientation.md) · [Project status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md) · [Canonical stages and gates](https://github.com/motion-tennis/documentation/blob/main/docs/project-stages.md) · [Shared task and handoff process](https://github.com/motion-tennis/documentation/blob/main/docs/contribution-workflow.md)

The central project status determines what is active. This table translates the shared stages into local work; it is not a separate schedule, acceptance record or instruction to implement every row. A completed task does not open the next stage.

Unity with C# is the selected Windows game-client, renderer and UI stack under [ADR 0012](https://github.com/motion-tennis/documentation/blob/main/docs/adr/0012-unity-game-engine.md). The headless committed-state service remains a separate process with native C++20 proposed. This stack decision does not implement a runtime or change the central stage; pin the verified Unity Editor/LTS release and packages when its implementation is in scope.

| Stage | Bounded local contribution | Evidence to hand off | Deferred scope |
| --- | --- | --- | --- |
| S0 — Shared contract/coordination baseline | Review the versioned full-doubles contract, profile compatibility, atomic-state boundaries and fixture specifications; plan bounded tasks | Reviewed field/invariant/size cases, dependencies and acceptance criteria linked to the shared contract | New runtime implementation, renderer and full rules implementation |
| S1 — Isolated minimal component slices | Build only an authorized headless versioned atomic state-store slice with a mock Pi writer and contract fixtures | Repeatable query, acceptance, conflict, duplicate and session-fencing results with trace identities | Polished renderer and real controller/pose integration |
| S2 — Feasibility/transport | Exercise the actual Windows state service with the real Pi using synthetic capacity traffic and bounded queues; isolate state handling from CPU/GPU work | Reproducible synthetic 800-transaction/s workload, queue/loss accounting and query/commit service measurements; clock/trace probe evidence | An integrated playable game or claimed four-human match |
| S3 — One-controller end-to-end diagnostic | Support one real pose/IMU chain through required query/response, atomic commit and feedback; expose diagnostic state | Correlated input/query/commit/feedback evidence, Pi mapping versions/uncertainty and marker links, including failures | Full doubles, final lobby and two-view match presentation |
| S4 — Four-controller 2v2 playable game | Add the Unity roster/lobby, two team views and serve/score presentation from the same complete committed match; Pi remains rules writer | Four-human readiness and identity evidence; identical committed results in both views through serves, scores and end changes | Transport comparison conclusions, visual polish unrelated to acceptance and release claims |
| S5 — Matched WLAN evaluation | Keep the state service, two-view load and publication policies identical for external AP versus Pi hotspot runs | Shared workload/configuration hashes, complete failures and timing under declared faults/load | Silent traffic, buffering, scoring or rendering changes between variants |
| S6 — Reproducible demonstrator | Package verified setup/startup/recovery, dependency versions and operator-facing evidence | A fresh-setup reproduction, recorded commands/configuration and run bundle satisfying central gates | Unrequested features or unsupported performance guarantees |
| S7 — Deferred true 3D/alternatives | Only a separately authorized research or migration slice | Explicit new scope, compatibility impact and evidence plan | Automatic progression from S6; unapproved true-3D tracking, engine replacement, sensor or transport replacement |

The S0 v0.3 candidate supplies full-match fields and bounded state-stream framing; its shared evidence and acceptance are recorded centrally. An isolated S1 store validates/stores the agreed data while its mock writer supplies candidates. Tennis rule calculation remains exclusively a Pi responsibility at every stage; it is never deferred PC work. Future work keeps service storage and renderer presentation separate from Pi authority.

## One task at a time

Before editing, inspect the working tree and read the canonical current status. Use the central task template to record stage, owner, repository/files, contract revision, bounded deliverable, dependencies, acceptance checks and explicit exclusions. For parallel human/AI work, reserve the affected interface/files and share concrete fixture expectations rather than letting each component invent a contract.

Complete that authorized scope and its appropriate checks without repeated approval requests for routine implementation choices. If a dependency is missing, record the gap and continue independent in-scope work. A broader idea belongs in the handoff, not silently in the patch. An explicit human request for a bounded future-stage slice can authorize it; record the exception and unmet dependencies, and do not advance the global stage.

If the central status cannot be read, use verified pinned status and task evidence before starting runtime work; otherwise restrict work to authorized inspection/planning until the missing scope is established. Do not infer permission from this roadmap or from another contributor starting a later task.

At handoff, identify changed files, actual checks/results, evidence locations, remaining limitations and the next bounded option using the [shared process](https://github.com/motion-tennis/documentation/blob/main/docs/contribution-workflow.md). A local test pass is component evidence; the central stage gate still requires the shared acceptance decision. Preserve other contributors' changes and do not commit or publish unless the task authorizes it.

## Verification boundary

Current executable checks are the README's whitespace check and the documentation repository's existing checker. Future runtime tasks add real commands with their build/tests. Keep functional fixture success, measured service latency and optical display timing distinct; a rendered frame proves neither screen presentation time nor controller feedback latency.
