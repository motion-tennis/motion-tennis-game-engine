# Game engine orientation

[Repository home](../README.md) · [Local workflow](workflow.md) · [Current project status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md) · [Shared stages](https://github.com/motion-tennis/documentation/blob/main/docs/project-stages.md)

## Purpose and boundary

This repository is the Windows committed-state service and separate renderer for Motion Tennis. The intended game is four-human doubles with Pi-controlled automatic movement, human pose/IMU racket gestures and two elevated team views. The state service stores one versioned match; it does not calculate a competing result on the PC.

| Interface | Responsibility |
| --- | --- |
| Pi query | Answer each `ball_state_request` with a matching `ball_state_response` and atomic snapshot; a push never replaces that exchange |
| Pi candidate update | Validate session, writer epoch, base version and bounded complete state; accept atomically or reject without partial effects |
| Commit result | Return the original outcome for duplicate updates; support explicit recovery of uncertain commits |
| Renderer/subscribers | Publish committed state and ordered event identities through bounded subscriptions; both cameras show the same world |
| Control/diagnostics | Participate in setup and Pi-coordinated clock exchange; preserve native times and export bounded asynchronous trace evidence |

Pi owns ball motion, avatar roots/headings, contact/rule decisions, serving and score changes. This service owns storage/versioning of accepted state. Rendering owns presentation, including camera transforms and nonauthoritative interpolation. GPU tracking is a separate repository/process on the same laptop.

## What exists

Repository inspection on 2026-10-25 found documentation and the MIT license only. No state service, renderer, build configuration, dependency lock, codec, runtime tests or measured performance exists. C++20 and a separate SDL3 renderer remain proposed. Reinspect files before using this statement in a later task; the [central status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md) records project progress.

The proposed v0.3 `motion.proto` defines complete match setup/state and atomic update/publication over the bounded state stream. The [S0 baseline](https://github.com/motion-tennis/documentation/blob/main/docs/s0-baseline.md) records compatible hashes and codec/resource evidence; the [component review](s0-contract-review.md) defines local invariants and the first S1 handoff. Runtime integration still needs the released stage and a bounded task.

## Reading order

1. Read [project status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md), [shared stages](https://github.com/motion-tennis/documentation/blob/main/docs/project-stages.md) and the [task/handoff workflow](https://github.com/motion-tennis/documentation/blob/main/docs/contribution-workflow.md); identify the authorized stage and slice.
2. Read this page, [local workflow](workflow.md) and the [component README](../README.md).
3. Read [gameplay](https://github.com/motion-tennis/documentation/blob/main/docs/gameplay.md), [player assignment](https://github.com/motion-tennis/documentation/blob/main/docs/player-assignment.md) and the [state-service design](https://github.com/motion-tennis/documentation/blob/main/docs/game-engine.md).
4. For the assigned interface, read the [protocol](https://github.com/motion-tennis/documentation/blob/main/docs/protocol.md), [schema](https://github.com/motion-tennis/documentation/blob/main/spec/motion.proto), [profile](https://github.com/motion-tennis/documentation/blob/main/spec/reference-profile.json), [timing](https://github.com/motion-tennis/documentation/blob/main/docs/timing.md) and [debug contract](https://github.com/motion-tennis/documentation/blob/main/docs/debugging.md).

## Bounded S0 task options

When central status authorizes S0, choose one named deliverable: review full-match snapshot/update/publication fields and invariants; specify fixtures for atomic acceptance, duplicate commits, stale versions and restarted writers; document a maximum-size contract case; or define the headless service/renderer boundary and later acceptance evidence. These are contract/coordination tasks, not permission to implement a renderer or complete state service. Coordinate shared-schema edits in the documentation repository through the task's explicit scope.

## Checks available now

From this repository, `git diff --check` checks tracked-change whitespace. Check new files for final newlines and trailing whitespace separately. When the documentation checkout is available, run its real checker from that checkout: `python3 scripts/check_docs.py`. Its supported local link/format checks do not prove runtime interoperability, Windows performance or display timing. Add build/run/test commands only when their actual implementation exists and has been exercised.
