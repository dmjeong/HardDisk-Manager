# Recovery cache options validation

| Requirement | Evidence |
| --- | --- |
| Configurable persisted budget | AppSettings limits (1 MiB–16 GiB, default1 GiB); existing CacheBytes JSON; Settings RAM limit field |
| Hot grow preserves data | Same cache/epoch, lazy allocation; GrowPreservesContentsHashAndEpochWithoutAllocatingTheNewBudget |
| Shrink removes oldest only as required | Preflight FIFO victim plan, new metadata/data budgets, free block trimming; cache shrink tests |
| Protected work and persistence failure preserve state | Pinned/preparing/CacheReady preflight, persist callback before mutation; no-partial-eviction and failure tests |
| Service serializes resize with operations | Shared _sync idle check; runtime busy/other-settings/restart/range Windows tests |
| Audit failure cannot enable mismatched cleanup | Atomic settings+audit transaction; SettingsAndAuditAreCommittedTogether; independent StopReason capacity consistency check |
| UI preserves drafts and shows actual applied state | MiB validation, post-save Dashboard+Recovery refresh after any older refresh, presentation tests |
| Framework/MVVM compatibility | Source compile and portable cache tests passed; full Windows CI pending |

All additional operator options in operator-options.plan.md remain proposals. Existing deletion folder semantics and per-file durable audit are unchanged.
