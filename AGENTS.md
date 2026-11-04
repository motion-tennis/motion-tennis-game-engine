# Game engine contributor instructions

These instructions apply to this repository. Read [orientation](docs/orientation.md), [workflow](docs/workflow.md) and the canonical [project status](https://github.com/motion-tennis/documentation/blob/main/docs/project-status.md) before work. The central status is the authority for the active stage; this repository does not maintain an independent stage schedule.

## Git branch and remote workflow

Follow the [shared Git workflow](https://github.com/motion-tennis/documentation/blob/main/docs/contribution-workflow.md#git-branch-and-remote-workflow) for every task.

- Before any file edit, verify the repository, current branch and working-tree status. Work on a dedicated branch for this task, never on `main`, detached HEAD or another task's branch. Preserve existing work; use a separate worktree when needed rather than resetting, discarding or automatically stashing it.
- Fetch before starting and create new task branches from the freshly fetched `origin/main` with `--no-track`; verify the remote/default branch first. If fetching fails, record the failure and known base revision before continuing local work on a task branch. Remote freshness remains unverified, and pushing must wait for a successful fetch and synchronization.
- Before every authorized push, run `git fetch --prune origin`, review changes to `origin/main` since the previous synchronization, and integrate the latest main and any new commits on the remote task branch into the task branch.
- Prefer merges to preserve published history. Resolve conflicts while preserving both contributors' intent; ask only when the intended resolution is ambiguous. Rerun relevant checks after integration and require `origin/main` and the existing remote task branch to be ancestors of `HEAD` before pushing.
- Push explicitly to the intended remote task branch. If the remote advances or rejects a push, fetch, integrate, resolve conflicts and revalidate before retrying. Do not force-push, rewrite shared history or push directly to `main` without explicit authorization for that action.
- Branch preparation and synchronization do not authorize committing or publishing. Honor existing user authorization without asking for it again; otherwise leave the reviewed changes local and report the branch, checks and any synchronization limits.

## Contribution rules

- Inspect Git status and current source before claiming implementation or editing. Preserve other contributors' work. All project documentation, code, comments and identifiers must be English; use `//` for new C/C++ comments and preserve license attribution.
- Name the task's stage, bounded scope, acceptance evidence and deferred work using the [shared contribution workflow](https://github.com/motion-tennis/documentation/blob/main/docs/contribution-workflow.md). Complete the authorized task without repeatedly requesting approval for routine choices inside it.
- Do not advance to another stage because one task passed. An explicit human request can authorize a bounded future-stage slice; record that scope, dependency gaps and handoff without advancing the global stage. If central status is unavailable, runtime work requires verified pinned status/task evidence; do not guess that a stage is open.
- Preserve the required Pi `ball_state_request`/`ball_state_response`, session/epoch fencing and atomic versioned commits. Pi calculates authoritative ball, avatars, serve/rules and score; this service stores committed state, and the renderer only presents it.
- Implement the Windows game client, renderer, UI and committed-state service in C#, using Unity for the client and presentation, as recorded in [ADR 0012](https://github.com/motion-tennis/documentation/blob/main/docs/adr/0012-unity-game-engine.md). Keep the headless service in a separate process from Unity rendering. Unity physics and frame updates must not advance authoritative match state. Pin the verified Unity Editor/LTS release, .NET/runtime target and packages with implementation; the selection does not establish a working game service or release a later stage.
- Use the proposed central v0.3 full-match contract and dedicated bounded state stream; do not implement a private incompatible extension or reuse the historical ball-only schema as a complete match. Follow the central version/hash and size/fixture gates before corresponding runtime work.
- Keep native timestamps, trace identities and Pi-issued mapping versions/uncertainty. Rendering and debug export must not become synchronous gameplay dependencies. A marker is diagnostic, never a new gameplay trigger.
- Run the real checks appropriate to the scoped change, document what they establish, and leave a handoff with remaining limits. No runtime build/test command exists until actual build files and tests are added. Stage acceptance follows the central process, not a local success claim.
