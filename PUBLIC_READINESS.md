# Public-distribution preparation

- Default server removed from program and setup documentation. Each new user must configure their own Gotify endpoint.
- MIT license added for project code and documentation.
- Gotify logo attribution and CC BY 4.0 text included; PNG-to-ICO conversion noted.
- .NET runtime notices included in packages and the single EXE.
- Original private-use history and release files retained in a verified local backup outside the repository.
- Historical pattern scan found no PEM private-key markers, GitHub credential patterns or personal private-key paths. Synthetic test credentials are fixtures.
- Legacy history contained a personal default endpoint. The distributable branch and tags are replaced with a clean 1.8.1 root before public visibility is considered.
- Legacy 1.8.0 download assets are retained as an owner/write-access-only draft rather than offered as a public download. The old public-facing tag is removed after backup.
- Only new source and release packages are intended for public distribution. No user profile files or private backups are included.

The repository remains private. Public visibility is a separate owner decision. This check covers reachable source history and intended downloads; it does not claim to purge GitHub's internal unreachable-object caches.
