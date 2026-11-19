# 1.8.1 validation record

| Check | Result | Scope |
|---|---|---|
| First installation without configuration | Passed | Empty default address; onboarding text; no HTTP or WebSocket started by refresh, reconnect, pagination or sending before setup |
| Existing configuration upgrade | Passed | Explicit server and protected synthetic token preserved |
| Custom server sending | Passed | Mock transport verified the exact configured host and base path |
| Regression checks | 330 passed | Windows 10 x64, build 19045; synthetic messages and isolated profiles |
| Native clipboard and protocol | 9 passed | Leading zeros, per-notification snapshots, helper exit, contention and restored clipboard/registration |
| Actual Copy Code notification click | Passed | User clicked the synthetic notification; harness verified the result and restored the clipboard |
| Project/logo/runtime notices in single EXE | Passed | All six required license resources present |
| Windows 11 physical device | Not tested | Deferred; no device available |

Reports contain check names and outcomes, not clipboard contents or real message data. Notification appearance and Action Center behavior can depend on system configuration. Test result counts are evidence for the listed scenarios, not a guarantee for every configuration.
