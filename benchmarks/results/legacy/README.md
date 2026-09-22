# Legacy performance baselines

These reports were produced by earlier ClipShelf benchmark and synthetic WPF
test runs. They are kept for regression comparison only.

- `search-baseline.json` / `search-optimized.json`: search latency and
  allocation measurements before and after the indexed-search optimization.
- `storage-baseline.json` / `storage-deferred.json`: synchronous versus
  deferred history persistence.
- `v1.2.0-scroll.json` / `v1.2.0-multi-scroll.json`: synthetic single- and
  multi-selection scroll checks.

Times depend on the machine and runtime. Scroll callback intervals are not
physical display FPS and must not be presented as a refresh-rate guarantee.
These files contain no clipboard history, document content, or absolute paths.
