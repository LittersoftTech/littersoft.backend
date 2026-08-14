# Local Firebase credentials (development only)

**This host needs these to do its job.** `Pawfront.ChatApi` is the one API host
that sends its own pushes, because for chat the latency *is* the feature. With no
usable credential every instant send fails, the outbox row falls through to the
1-minute `NotificationDispatchFunction`, and chat messages arrive a minute or more
late — the batch latency this host exists to avoid. Nothing errors visibly; the
message is delivered, just slowly. Watch for `Firebase initialisation failed` in
the host log, and for `LastError` on `Notification.NotificationOutbox`.

Note a populated `CredentialsSecretName` is **not** sufficient on its own: with
`AzureKeyVault:Enabled = false` the local secret provider looks for
`LocalSecrets:<name>` in configuration and throws when it is absent. Locally, use
the file path below.

Drop the two Firebase Admin SDK service-account JSONs here:

| File | Firebase project | Used for |
|---|---|---|
| `provider-firebase.json`  | `littersoftprovider`   | pushes to the **provider** app |
| `parent-firebase.json`    | `pawfrontparent-89296` | pushes to the **pet-parent** app |

Get each from the Firebase Console:
**⚙ Project settings → Service accounts → Generate new private key**.

Then point `appsettings.json` at them — a relative path is resolved against the
application base directory, so no absolute path is needed:

```json
"Provider":  { "CredentialsFilePath": "Secrets/provider-firebase.json" },
"PetParent": { "CredentialsFilePath": "Secrets/parent-firebase.json" }
```

Check each file's `"project_id"` matches the `ProjectId` beside it in config —
swapping the two produces `SENDER_ID_MISMATCH` at send time, which is an
irritating thing to trace back.

## Do not use this for deployed environments

`*.json` here is gitignored (`**/Secrets/*.json`) and copied to the build output,
which is what makes local development work — and exactly why it is wrong for
anything deployed: the private key would ship inside the zip payload, readable by
anyone who can download it or open a console on the App Service.

Deployed environments leave `CredentialsFilePath` empty and use
`CredentialsSecretName` with `AzureKeyVault__Enabled=true`, so the key is fetched
from Key Vault at runtime and never written to disk.

This README is committed; the JSON files never are.
