# Operator options and interlocks design

## Shared contract

AppSettings adds LockedFileNotificationMode (Popup/Tray/LogOnly), LockedFileNotificationIntervalSeconds (default300), InterlockTripSeconds (default60), InterlockRecoverySeconds (default60), InterlockManualReset (defaultfalse), and InterlockProcessNames (empty). Existing MinimumAvailableRam (default1GiB) and HistoryMaxBytes (default2GiB) gain validated MiB editors. Delivery changes only UI notification; logs/Alerts remain and process termination still requires approval.

DrivePolicy retains StartHour/EndHour JSON and adds StartMinute/EndMinute (0..59; 24:00 end only) and a ScheduleDays flags mask (all days default). Overnight windows belong to the starting selected day. End is exclusive. Full-day0:00–24:00 and existing overnight schedules remain compatible.

InterlockStatus contains Code, Entity, State, Blocking, Latched, SinceUtc and Detail. Dashboard supplies Interlocks. Monitoring exposes resource statuses, current blocking reason, and ResetInterlocks() returning whether current conditions have safely recovered. Pure domain ResourceInterlocks evaluates finite valid metrics, sustained CPU/temperature/active disk latency, immediate low RAM and stable recovery. Blocked channels without fresh evidence remain blocked; unsupported sensors do not block a never-tripped channel. Config changes/reset never clear a currently unsafe condition.

## Runtime

Monitoring records trip/clear transitions. Service merges manual pause, audit/config consistency, resource states, critical disk alerts and configured process blockers. ResetInterlocks is an authorized, idempotent command separate from Resume; it refuses active/recovering conditions and does not acknowledge disk alerts, clear manual Pause or bypass telemetry/volume/file protection. Process checks only inspect names, never terminate apps; inspection failures fail closed when blockers are configured. Blocked conditions are checked between items and before mutation. Active cache copying will be canceled when resource Interlock trips; partial copies are discarded before deletion.

Use stable state codes and detailed reasons in Dashboard. Data loss or stale measurements cannot clear an already active resource latch. Global telemetry startup/staleness is shown and blocks service cleanup when monitoring was started but has no fresh sample. Restore remains available subject to single-operation locking and underlying I/O errors.

## Presentation and persistence

Settings groups Interlocks (RAM/trip/recovery/manual-reset/processes), Notifications (mode/seconds), History (days/MiB), retaining cache controls. Dashboard shows all blockers/statuses and a Reset interlocks action. Policies exposes weekday checks and HH:mm values with validation. Existing MVVM and framework4.8/C#7.3 requirements remain. UI drafts survive polling; notification preferences are applied from server configuration while unsaved drafts remain local.

SQLite settings+audit transaction remains. Saving resource options takes effect on next monitor sample and synchronously before subsequent deletion gates where needed. Saving lower history limit triggers maintenance independent of unrelated settings. Retention bounds preserve pending intents, recoverable metadata and unresolved alerts, and report when protected records prevent meeting the soft DB bound.

## Verification

Pure schedule tests include overnight weekdays/week boundary, exact minutes,24:00,legacy JSON/defaults. Pure interlocks cover trip/debounce/recovery, missing/NaN/stale, setting changes, manual reset refusal and clear, clock reversal, RAM immediacy and multiple simultaneous reasons. UI tests cover modes/throttle/log-only, persisted vs draft settings, range validation, clear state and settings edit preservation. Windows tests exercise runtime process blockers/reset/no-bypass, setting persistence/ranges and existing cleanup safety plus full service/WPF CI.
