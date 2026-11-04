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

The harness runs 14 deterministic checks, including the full eight-query/four-controller batch, setup retries, atomic commit/rollback, duplicate and changed-payload retries, stale versions, explicit rejected-input accounting, writer restart/reconciliation, periodic-only updates, score-award and technical-pause invariants, expiry/unknown status, and bounded retention.

## Boundaries and remaining work

This is the in-memory state core, not a running Windows service. It has no TCP framing, control socket, local publication transport, persistence, Unity client or generated Protobuf C# types. The S1 test adapter supplies typed records and canonical configuration bytes; production input must first pass the exact v0.3 canonical-wire validation, decode the accepted schema/profile and preserve the original bounded wire identity for retries. The JSON fingerprint is an internal typed-object identity for this slice, not a replacement wire encoding or protocol hash.

The fixture seeds a complete version-one snapshot after writer registration and setup, matching the shared contract fixture shape. The live startup/initial-state handshake still needs to be pinned with the physics producer. The current test evidence establishes host-side storage semantics only; it does not establish network interoperability, Unity isolation under load, Windows service timing, Pi behavior or stage acceptance.
