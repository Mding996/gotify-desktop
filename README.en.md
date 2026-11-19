# Gotify Desktop 1.8.1

Native Windows notifications for uniquely recognized verification codes now have one Copy Code button. Each action resolves its own DPAPI-encrypted snapshot; protocol activation copies through a short-lived Win32-only process without opening the main window or reconnecting, including after a full exit. Leading zeros are preserved. Existing notification eligibility rules still apply; OTP messages bypass burst aggregation. Snapshot storage is bounded to 512 records and seven days. Run the new EXE once to update protocol registration; existing notifications are not retroactively modified.

The composer now offers an application picker. On Gotify 3.x it sends with the current Client Token and the chosen application's ID, without needing that application's token. Catalog refreshes and management edits update the choices; app default priorities are used unless the user has edited the draft priority. Existing Application Token sending remains an explicit option. Requests snapshot their target and credentials; failures and edited drafts are preserved, and an invalidated target is never silently replaced.

Memory update: native Shell tray integration avoids loading Windows Forms; 64-pixel frozen icon bitmaps are shared through a bounded cache; history uses streamed JSON I/O; verification-code results and unchanged rich documents are reused; read-only controls disable undo; large readers release their document while hidden and restore it on reopening. History capacity and background notifications are unchanged. See MEMORY_COMPARISON.md for paired measurements and test conditions.

Adds native account/password login, application management (create/edit/icon/default priority/delete/token rotation), client management (create/rename/inactivity expiry/elevate/revoke), a DPAPI-encrypted token vault with explicit reveal/copy/use actions, and confirmed deletion of individual, per-application, or all server messages. Open 管理 in the main header. Existing manual token setup is retained.

Management targets the Gotify 3.1.1 API. Passwords are never persisted. Tokens created or rotated here are captured locally; unknown historical tokens cannot be recovered from Gotify 3.x. Sensitive operations verify the current account password. OIDC/browser login, user administration and plugin management are outside this release.

The native Windows title bar now refreshes immediately when changing between dark and light mode, with a Windows 10 compatibility fallback and explicit Windows 11 caption colors.

Messages with a recognized verification code now include a copy-code button directly in the left-hand message list. No detail-view selection is needed.

C# / WPF / .NET 8 native Windows 10/11 x64 client, published as a self-contained single-file EXE.

Includes verification-code quick actions, safe rich-message links, basic native Markdown, burst summaries, quiet hours, application/time filters, persistent per-application notification overrides, light/dark themes, master-detail reading, persistent local unread states, a collapsible draft-preserving composer, keyboard shortcuts, actionable feedback, and realtime receive/send, recent history and pagination, local search/copy/clear, authenticated application names/icons, draft-preserving send guards, per-user single-instance IPC, silent startup, DPAPI token storage, and Windows Action Center toast notifications with protocol activation (tray fallback).

See 使用说明.md for setup, storage, build instructions and verification limits.

Build: .\build-release.ps1 (or -Offline -OfflineSource <local-feed-directory> with the required runtime packages).

The original Gotify logo is at Assets\logo.png; multi-resolution ICO at Assets\gotify.ico. Source: https://gotify.net/img/logo.png. This client is independent of the Gotify project.

Windows ABI references: https://github.com/microsoft/windows-rs/tree/master/crates/libs/windows/src/Windows/UI/Notifications and https://learn.microsoft.com/en-us/windows/win32/shell/enable-desktop-toast-with-appusermodelid . API contract: https://github.com/gotify/server/blob/master/docs/spec.json .







The default server is empty. Configure your own Gotify server before connecting. Project code is MIT licensed; the Gotify logo remains CC BY 4.0. See LICENSE and THIRD_PARTY_NOTICES.md.

