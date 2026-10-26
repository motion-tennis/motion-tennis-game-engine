# S0 game contract review and S1 handoff

[Repository home](../README.md) · [Local workflow](workflow.md) · [Central status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md) · [Shared contract](https://github.com/motion-tennis/documentation/blob/main/docs/protocol.md)

## Review scope and evidence

Task: S0 component-side contract review and bounded S1 handoff, 2026-10-26. The starting shared revision is `documentation@95c2592`; this review accompanies the S0 schema/profile revision identified by the exact hashes in the shared evidence. The [S0 baseline](https://github.com/motion-tennis/documentation/blob/main/docs/s0-baseline.md) pins the final contract hashes and codec/size results. The candidate is `motion.v03`, protocol `0.3-proposed`, profile `webcam-pi3-doubles-v3`; [ADR 0011](https://github.com/motion-tennis/documentation/blob/main/docs/adr/0011-full-match-contract-and-state-stream.md) records the full-match state channel. This document records an AI technical review, not a human stage decision or an implemented component. The central ledger alone releases S1.

Allowed work here is documentation only. Shared schema, codec fixtures and resource checks belong in `documentation`; runtime integration and hardware measurements retain their later stage gates. Proposed numerical targets remain unmeasured. No later-stage exception is claimed.

## Producer and consumer expectations

- The service owns one committed match snapshot; Pi owns all authoritative calculations. Validate structure, identities, revisions and bounded fields without becoming a second scorer, physics engine or avatar planner.
- Answer each valid `ball_state_request` with the exact logical query identities and one coherent full snapshot. A retry receives its retained response; unrelated new queries must not inherit stale snapshot content.
- Commit ball, four avatars, active roster/configuration, serve/rally state, score and ordered events atomically against the exact base version and writer epoch. Rejection leaves every committed field and publication unchanged.
- Identical accepted retries return the retained result without another version/event/score increment. Reuse of a batch identity with different content is rejected. Recovery cannot fabricate a new batch to replay an ambiguous point.
- Fence typed `MatchSetup` by its current `writer_epoch` and registered physics control session. An identical command/digest retry returns retained acceptance; changed content or a superseded writer is rejected.
- Setup installation preserves explicit operator-confirmed assignments, configuration identity and revision fences. Slot/team identity persists through court-end changes. A new player/team match configuration requires the documented new-match policy.
- Publish both views from one accepted snapshot/version and ordered committed events. Renderer interpolation, camera transforms and debug export are outside authoritative updates and cannot block state processing.
- Preserve source clocks, Pi map provenance and transaction/batch/event IDs in diagnostics. Rule-event identity is distinct from per-sample `event_index`; a sample `MISS` or tracking failure cannot independently award a point.

The proposed state path uses dedicated TCP 41010 with a four-byte big-endian length prefix and an 8,192-byte envelope cap, separate from control TCP 41041. Controller and pose UDP remain capped at 1,200 bytes. Bounded partial-frame assembly, coalesced frames, connection loss and head-of-line deadline exposure need explicit runtime tests in S1/S2; the transport change establishes no latency guarantee.

## Required contract cases

| Case | Expected result and handoff evidence |
| --- | --- |
| Full candidate accepted | Exactly one state-version increment and complete state replacement; both readers observe the same committed version |
| Stale version, wrong session/epoch or unknown setup revision | Explicit rejection; byte-equivalent committed state and no new publication/event |
| Same accepted request twice | Same retained result, no repeated score/event/avatar effects |
| Same identity with altered payload | Explicit conflict/rejection, even if the altered candidate is otherwise valid |
| Torn/oversized frame or incomplete candidate | No partial decode/commit; bounded input buffering and explicit failure |
| Game/physics restart and unknown commit | No guessed outcome; new session/epoch fences and explicit recovery policy |
| Periodic-only commit | Valid versioned state advance with zero controller decisions |
| Setup and publication | Four unique bindings and one configuration; both team views derive from one snapshot, not separate simulations |

## First bounded S1 task

**Outcome:** a headless in-memory state store with a mock Pi writer and deterministic query/commit fixtures. Allow state/validation modules, a minimal build and tests. Defer sockets, persistence beyond the declared process/session policy, renderer/UI, scoring and other gameplay calculations.

**Dependencies:** accepted schema/profile, pinned full-match fixtures and canonical candidate-content identity/retry rules shared with physics. Pin actual toolchain versions with implementation.

**Acceptance:** coherent query snapshots; atomic accept/reject; duplicate and conflicting retry behavior; stale version/session/epoch/setup rejection; periodic-only updates; bounded request/result retention and explicit expiry/unknown status. Check that invalid candidates leave all stored fields unchanged and that two read consumers receive the same version. Retain test commands, fixtures and output for the Pi adapter; host tests establish storage semantics only.

## Review disposition

The game boundary is compatible when it stores the complete candidate atomically and keeps calculation on the Pi. Shared schema/codec size evidence and any blocking findings are recorded centrally. This review supplies no state runtime, renderer, Windows interoperability or display measurement.
