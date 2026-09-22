# Development records

This directory keeps small, durable records that are useful when reproducing a
release or comparing behavior over time. It intentionally excludes build
outputs, user data, screenshots, caches, local installation reports, and
machine-specific paths.

- [`releases/`](releases/): verified public release metadata and integrity
  hashes.
- [`../benchmarks/results/legacy/`](../benchmarks/results/legacy/): selected
  historical performance baselines.
- [`../scripts/verify-install.ps1`](../scripts/verify-install.ps1): reusable
  installed-update verification.

New reports should be committed only when they are stable, small, free of user
content, and useful as a future comparison point.
