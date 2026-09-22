# Maintenance scripts

## Verify an installed update

`verify-install.ps1` verifies a prepared self-contained build against the
current per-user installation. It performs a normal ClipShelf shutdown, stages
the build through the production updater, verifies installed hashes and version,
checks that `history.json` and `settings.json` did not change, and confirms that
the restarted process becomes interactive.

Example:

```powershell
.\scripts\verify-install.ps1 `
  -BuildDirectory .\dist-1.2.10\ClipShelf `
  -ExpectedVersion 1.2.10 `
  -ResultPath .\artifacts\v1.2.10-install.json
```

The script changes the installed application and restarts ClipShelf. Use it only
with a trusted build after the normal tests pass. Generated result files belong
under the ignored `artifacts/` directory and should not be committed because
they can contain local backup paths.
