# Gotify Desktop 1.8.1 build and validation

- Date: 2026-11-19.
- Windows 10 x64, build 19045; .NET SDK 8.0.427 and bundled runtime 8.0.33.
- New installations start with an empty server address. Existing explicitly configured addresses and DPAPI-protected tokens remain usable.
- Final EXE passed 330 isolated regression checks, including first-run network guards and embedded project/logo/runtime license notices. See test-results.json.
- Final EXE passed 9 real clipboard and public protocol integration checks. The previous clipboard and protocol registration were restored. See copy-native-results.json.
- A user manually clicked the native single Copy Code toast button on Windows 10 for this 1.8.1 functional revision. The test harness confirmed the exact synthetic code, then restored the previous clipboard. No main window was opened and no real SMS was used.
- Windows 11 physical-device testing is deferred because no device is available. Do not treat Windows 10 tests as Windows 11 verification.
- EXE is unsigned. Notification presentation depends on Windows notification settings.
- Project code: MIT. Gotify artwork: CC BY 4.0. Full project, logo and bundled runtime notices are shipped beside the EXE and embedded inside the standalone EXE.
- Real user profiles, tokens, messages, signing keys and private backups are excluded from the source and release packages.

Build: .\build-release.ps1. Offline: .\build-release.ps1 -Offline -OfflineSource <local-feed-directory>.
