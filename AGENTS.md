# Development workflow

After changing application code, rebuild the affected resource through Aspire and verify it is healthy before handing back to the user. For this app, use `aspire resource secret-santa rebuild --non-interactive`. After AppHost changes, restart the exact AppHost with `aspire stop` and `aspire start --non-interactive --apphost src/SecretSanta.AppHost/SecretSanta.AppHost.csproj` so its model is rebuilt. Use the same explicit `--apphost` selector for resource commands, stop, and readiness checks. Leave the app running for the user.

Use the repository's Aspire skills for lifecycle operations. Never use `aspire stop --force` for a rebuild; it deletes persistent resources. Keep database passwords in local user secrets, never source control.
