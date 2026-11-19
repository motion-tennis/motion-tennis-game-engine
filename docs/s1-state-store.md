# S1 C# headless state store

**Stage and scope:** S1 isolated component slice. This implementation follows the game handoff in [S0 contract review](s0-contract-review.md) and the central accepted v0.3 schema/profile baseline recorded in the [project status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md). It does not advance the shared stage.

## Implemented

- A .NET 8 C# library models full-match configuration, snapshot, query, decision, update and rule-event data.
- `GameStateStore` fences game sessions and writer epochs; setup is immutable and digest-checked; initialization accepts a validated complete snapshot from the mock adapter.
- Queries return a coherent immutable snapshot and preserve the original response for an identical batch retry. A changed request under the same batch key conflicts.
- Updates require a retained query for the same session/epoch/batch, the exact queried/current version, a later simulation tick and a complete valid candidate. The store validates the four-player roster, configuration references, finite geometry, serve/score bounds, decision correlation/order and rule-event indices before replacing the snapshot under one lock.
- Accepted and rejected update outcomes are retained. An identical update retry returns its original result before the now-stale base version is considered; changed content under the same key is rejected. An expired lookup returns `UNKNOWN_BATCH` and never infers outcome from the current snapshot.
- Retention is bounded to 512 batches for 2 seconds. Unexpired outcomes are not evicted; full retention returns an explicit overload result. Replacing a physics writer requires a newer epoch and a new control session; it preserves the committed snapshot, clears old-epoch deduplication records, and pauses queries until the matching setup acknowledgement is reconciled.
- The test executable supplies deterministic mock-Pi query/commit fixtures and a controllable monotonic clock.

## Build and run

Target framework: `net8.0`, C# 12. The implementation has no external NuGet dependencies.

```powershell
dotnet build src\MotionTennis.GameState\MotionTennis.GameState.csproj
dotnet run --project tests\MotionTennis.GameState.Tests\MotionTennis.GameState.Tests.csproj
```

The harness runs 16 checks covering the eight-query/four-controller batch, setup retries, atomic commit/rollback, duplicate and changed-payload retries, stale versions, explicit rejected-input accounting, writer restart/reconciliation, periodic-only updates, score-award and technical-pause invariants, expiry/unknown status, bounded retention and tiebreak scores.

On 2026-11-19, both new tiebreak checks reproduced the defects against game base `f34c226` and passed with the repair. All 16 checks passed on .NET SDK 10.0.401 targeting `net8.0`. The snapshot tests accept an active 100–99 tiebreak and a score at the unsigned 56-bit limit. The commit tests cover completed tiebreaks at that limit, a one-point lead and a mismatched winner. An out-of-range counter for either team leaves the initial store empty or the committed snapshot intact.

### Shared S1 batch with physics

The [shared fixture runner](https://github.com/motion-tennis/documentation/blob/main/scripts/check_s1_shared_batch.py) drives the C++ physics core, checks its messages against the shared v0.3 contract and passes the decoded fixture to this repository's test executable. It supplies the canonical Protobuf configuration bytes and digest, the starting snapshot, the three-entry query, the rebuilt update and the physics rejection record. The runner uses UUID strings in network byte order and preserves the full controller/session/request key.

The [game fixture consumer](../tests/MotionTennis.GameState.Tests/SharedBatchFixture.cs) registers the writer, installs the setup and checks that the store echoes all three query identities with the initial snapshot. On 2026-11-19 the check passed with game base `a3b6eb6`, physics base `08d59a7` and documentation base `2535e62`. Controller 102's middle request went stale at Pi time 521,000 µs. Its explicit rejection accompanied a hit from controller 101 and a miss from controller 103, in source order. The store committed one new contact at state version 2, tick 101. Replaying the identical batch returned the original commit result and version.

Five independent stores rejected missing, wrong or duplicate rejection identities, a missing survivor and reversed survivor order. Each preserved its complete starting snapshot. A sixth check changed the rejected-key list after acceptance and verified that the original commit outcome and snapshot remained intact.

To reproduce the cross-component check, follow the [fixture handoff](https://github.com/motion-tennis/documentation/blob/main/docs/s1-physics-game-fixture.md). To replay the game side with the runner’s generated game input, pass `--shared-batch <game-input.json>` after `--` in the `dotnet run` command above. The game check prints the accepted version and tick, surviving event identities, rejected query identity and negative-case results as JSON.

## Boundaries and remaining work

This is the in-memory state core, not a running Windows service. It has no TCP framing, control socket, local publication transport, persistence, Unity client or generated Protobuf C# types. The S1 test adapter supplies typed records and canonical configuration bytes; production input must first pass the exact v0.3 canonical-wire validation, decode the accepted schema/profile and preserve the original bounded wire identity for retries. The JSON fingerprint is an internal typed-object identity for this slice, not a replacement wire encoding or protocol hash.

The fixture seeds a complete version-one snapshot after writer registration and setup, matching the shared contract fixture shape. The live startup/initial-state handshake still needs to be pinned with the physics producer. The current test evidence establishes host-side storage semantics only; it does not establish network interoperability, Unity isolation under load, Windows service timing, Pi behavior or stage acceptance.

The shared fixture exercises one mixed host batch against the in-memory store. Its rejected-key list enters the existing `Commit` API alongside the state update. Trace export, publication of committed decisions and rule events, and a deployed handoff for rejected keys and their timestamps remain open.
