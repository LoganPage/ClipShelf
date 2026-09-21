# ClipShelf development notes

- This directory is the canonical local development copy of ClipShelf.
- Keep generated build output (`bin/`, `obj/`, `artifacts/`, `dist*/`) out of Git.
- Preserve user data under `%LOCALAPPDATA%\ClipShelf` when building or installing updates.
- Run the existing test and Release build workflows before packaging a release.
- Do not publish or upload releases unless the user explicitly requests it.
