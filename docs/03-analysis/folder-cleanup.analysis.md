# Folder cleanup verification

| Requirement | Implementation / evidence |
| --- | --- |
| Active folder completes after target | CleanupEngine active-unit queue and latched stop; FinishesActiveFolderAfterTargetAndKeepsInterleavedNextFolder |
| Oldest folder first, oldest within folder | Indexed SQLite global/unit dequeue; CandidateQueueTests |
| Nested empty ancestors removed | Same-unit parent enqueue after target; NewlyEmptyAncestorsStayInActiveFolderAfterTarget and native NTFS test |
| New/protected/excluded/locked survivors preserved | Existing scan/verified handle checks; active-folder engine and native survivor tests |
| Exact-path retry stays exact | No active unit or ancestor enqueue when onlyPath is supplied; native retry and missing-path tests |
| Cancellation/health/limits stay immediate | Per-item and pre-delete checks; cancellation, gate, byte, item and post-capture elapsed-time tests |
| Concurrent writes do not start another folder | Latched target; same observed volume snapshot used by history and stop decision |
| Framework/MVVM compatibility | C# 7.3/net48 semantic compilation passed; Windows Framework build/service/IPC/design-time/WPF CI passed (95/95 tests, 29 renders, no binding errors) |

Audit database schema is unchanged. The new unit index exists only in the disposable scan database. Recovery data still uses the global bounded RAM cache, and per-file durable DeleteIntent records remain mandatory.

Physical HDD throughput and long-duration production load remain device acceptance tests. Functional grouping does not imply atomic directory deletion or unlimited read speed.

Verified code: `da9f0543ad9122531030015218cfc1ae5e18569b`. [Windows CI](https://github.com/dmjeong/HardDisk-Manager/actions/runs/39867537429).
