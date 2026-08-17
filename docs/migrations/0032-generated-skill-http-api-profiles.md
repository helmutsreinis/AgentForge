# 0032 — Generated-skill HTTP API profiles

Migration `20260817071904_HttpApiProfiles` adds `http_api_profiles`, keyed by installation and bounded profile ID.
Rows contain display metadata, fixed HTTPS base endpoint, relative verification path, non-secret static header JSON,
OS-secret store/key references, enabled state, optimistic version, actor/correlation IDs, and timestamps. Bearer token
material is never a relational column.

Before upgrade, stop the host and back up the SQLite database plus WAL/SHM files and the artifact/secret-store state.
Start the upgraded host once; normal initialization applies the forward migration. Verify `doctor`, list API profiles,
and create a disabled fixture or perform a credential-equipped live preview. Restart and confirm the same profile
version/reference is readable. Existing installations contain zero profile rows and retain all prior behavior.

Rollback requires first disabling generated API skills and removing any operational dependency on these profiles.
Back up again, then apply the EF down migration or restore the pre-upgrade whole-store backup. Down migration drops
`http_api_profiles`; it cannot delete OS-secret entries automatically. Delete orphaned references through the secret
store adapter only after confirming the database rollback/restore succeeded. Never copy a token into SQL, migration
scripts, logs, or recovery notes.
