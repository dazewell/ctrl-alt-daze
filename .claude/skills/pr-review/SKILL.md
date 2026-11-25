---
name: pr-review
description: Handle a PR's bug-hunt review (local claude CLI), review threads, CI wait/rerun, readiness check and merge with the deterministic scripts/pr-*.ps1 scripts (cheap, short output). Use whenever you open, review-wait, fix review comments on, check, or merge a PR here, or when a brief says "follow the pr-review skill". Never hand-roll gh/graphql for these.
---

# PR review and merge (D-122, D-155)

Every script takes the PR number, works from any worktree, prints one short line per fact, and has `-DryRun` where it writes. Run from the repo root: `pwsh scripts/<name>.ps1 <pr> ...`.

## Standing rules (children follow these; briefs just say "follow the pr-review skill")

- **Never `gh pr merge --auto`** (it fires before review threads are answered), `--squash` or `--admin`. Merge only through `pr-merge.ps1` (merge commit).
- **The bug hunt is required** when the PR adds more than 100 lines of non-test code (docs and tests excluded). Not for docs-only or small fixes whose tests cover the change. It replaced the Copilot review (no longer available, D-155): a cold `claude -p` session that sees only the `bug-hunt` skill, the code diff and read-only access to the checkout. **Never load the `bug-hunt` skill yourself**; only the script runs it. Max **2 runs per PR** (the script refuses a third; `-Force` only when the user asks by name). Findings still open after the second: stop and report in `## Resume` (the design likely needs revisiting).
- **Verify before acting.** Read every cited `path:line` yourself. A `plausible` finding needs its unproved step proved, or a reason it can't happen. Fix real ones as new commits; decline false positives with a reason.
- **Reply to AND resolve every thread, verify `unresolved: 0`.**
- Never wait on CI with your own loop or model turns: `pr-wait.ps1` in the background. Run the bug hunt in the background too (minutes).
- If a finding needs the user's decision (scope, UX, contradicts a D-xxx): stop, write it in the PR `## Resume`, leave it.

## Flow

1. `gh pr create`. If this environment exposes PR monitoring tools, discover their schema and enable Auto-fix/address-comments for this PR. If unavailable or refused, record that in `## Resume` and handle checks and comments through the scripts below; do not call tools from another agent app by name.
2. `scripts/pr-wait.ps1 <pr>` in a background process or persistent terminal session (blocks until CI finished; prints CI and the bug-hunt state; exit 0 ok, 1 CI failed, 2 timeout, 3 closed).
   Bug hunt needed: once CI is green, with the PR head checked out and a clean tree, run `scripts/pr-bug-hunt.ps1 <pr>` in the background. It prints the level and why, then each finding (`severity confidence file:line title`), and posts them as one review with a thread each (inline where the line is in the PR diff, file-level otherwise). `-PacketOnly` shows the level and packet without running; `-NoPost` runs without posting; `-Path` narrows a diff over 160 KB. It answers `no-code` for docs/test-only diffs. Not signed in: the user runs `Set-BugHuntToken` once (machine-wide, from their PowerShell profile); ask them.
3. `scripts/pr-threads.ps1 <pr>` lists unresolved threads (`id file:line @author body≤300`) plus review bodies; `-Full <id>` shows one in full, `-All` includes resolved.
4. Fix, push, then `scripts/pr-reply.ps1 <pr> "<id>=<reply>" "<id>=<reply>"` (ids may be unique prefixes; replies the thread, resolves it, re-queries and prints `unresolved: N`). Same message for all: `-All "Fixed in <sha>"`.
5. `scripts/pr-status.ps1 <pr>` (one block ending READY or the exact blockers; `-Ready` prints only that) then `scripts/pr-merge.ps1 <pr>` (ready check + `gh pr merge --merge`, prints the merge commit; `-DryRun` to preview). If merge is blocked by the permission check, say so in your report; the coordinator merges.
6. CI red: `scripts/pr-rerun.ps1 <pr>` prints the failed test names and reruns only known flakes (`TriggerServiceTests`, DEADENDS X-012); a real failing test is printed and not rerun: fix it. To wait for someone else's merge: `scripts/wait-pr.ps1 <n>` in the background.

GitHub's mergeable/conflict notice can be stale: `pr-status`/`pr-merge` re-check a reported conflict with `git merge-tree` on the exact head SHA and warn when it was stale. If main moved: `git merge origin/main`, resolve, push (never rebase or force-push).

## Coordinators

`pr-status.ps1 <pr>` per child PR instead of `gh pr view` JSON. To let children merge without the auto-mode classifier blocking `gh pr merge`, the user can allow-list the single command `pwsh scripts/pr-merge.ps1` (done in the checked-in `.claude/settings.json`: `permissions.allow` has `PowerShell(pwsh scripts/pr-merge.ps1:*)` and the Bash form).

Tests for the pure logic: `pwsh scripts/tests/pr-lib.tests.ps1` (runs in CI).
