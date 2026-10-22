# Folder cleanup design

## Candidate queue

`PolicyRules.CleanupUnitPath(FileSnapshot)` returns the first path segment below the snapshot Root, or the file path itself for a loose file. Matching is case-insensitive and respects path separators. Invalid/outside-root snapshots stay isolated; WindowsFileSystem still rejects them before mutation.

The disposable SQLite candidate store gains a normalized unit key and `(runid, processed, unit, age, path)` index. `IJournal.TakeNextCandidate(runId, cleanupUnit = null)` keeps globally oldest selection for a new unit and restricts selection to an active unit when supplied. Audit schema and its FULL synchronous transactions remain unchanged.

## Engine

Maintain active unit, whether a confirmed deletion has started it, and whether target space has been observed during it. Check target before starting the first deletion, including after cache capture. After the first confirmed deletion in a folder, target space requests finishing that unit, including newly eligible empty ancestors, then stops before another unit. A loose file is an independent unit.

All other safety checks still run between every item and immediately before deletion. Cancellation and fatal I/O can interrupt a folder. Expired, excluded, protected, oversized, modified or locked items keep their existing validation/skip behavior; a partially retained folder is valid. Parent candidates may only remain within the active unit. `onlyPath` retries remain exact-path operations and never enqueue ancestor deletion.

Preview uses the same unit ordering without deleting or allocating recovery entries. Full scan must complete before any deletion. Windows Service remains a single background operation across policies to protect HDD I/O and cache/audit consistency.

## Validation

Use the real SQLite queue in engine tests, plus native NTFS tests for nested parent removal and preservation of recent/excluded/locked children. Verify case/path-boundary isolation, dynamic parent candidates, target during preparation and an active unit, safety interruptions, and exact-path retry. Run existing Windows build/tests/service/IPC/WPF smoke workflow.
