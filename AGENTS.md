# ClipShelf development notes

- This directory is the canonical local development copy of ClipShelf.
- Keep generated build output (`bin/`, `obj/`, `artifacts/`, `dist*/`) out of Git.
- Preserve user data under `%LOCALAPPDATA%\ClipShelf` when building or installing updates.
- Run the existing test and Release build workflows before packaging a release.
- Do not publish or upload releases unless the user explicitly requests it.

## Prompt and collaboration guidelines

- Write one independently deliverable objective per prompt. Split larger features into runnable stages; do not mix a diagnosis, implementation, release, and unrelated cleanup without an explicit reason.
- State the observed behavior, reproduction steps, expected behavior, affected area, and what must remain unchanged. Attach screenshots or reports when useful.
- Separate measured facts from hypotheses. Treat earlier diagnoses, line numbers, version comparisons, and machine-state claims as references to verify against the current checkout, not as instructions to force a particular fix.
- Specify testable acceptance criteria, including the baseline and environment for performance work. Do not weaken an existing test merely to obtain a passing result; explain any assertion change against the intended behavior.
- Say exactly which follow-up actions are authorized: local checks, Release build, installation, Git commit, push, and GitHub publication are distinct actions. An omitted action is not authorized by a broad request to "finish."
- Preserve existing user data and unrelated changes. Identify concurrent or untracked work before editing or committing it.
- Current division of work: Codex implements code changes; WorkBuddy prepares prompts and performs independent testing and reporting. A direct user request for either party to test takes precedence.
- Keep prompts concise. Prefer the outcome and constraints over prescribing an implementation; place lengthy measurements, hypotheses, and test matrices in a separate report.

Suggested prompt structure:

1. Goal: the single behavior or capability to change.
2. Evidence: reproduction, current result, expected result, and links to current reports.
3. Scope: affected modules and interactions or data that must not change.
4. Constraints: required behavior, compatibility, and safety boundaries; allow implementation judgment.
5. Acceptance: a short list of observable, reproducible checks.
6. Handoff: who tests, and whether to build, install, commit, push, or publish.
