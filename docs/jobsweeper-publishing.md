# Publish jobsweeper with mapped settings

In Visual Studio, open Pawfront.Functions > Publish and select **jobsweeper-configured**. This targets the existing Windows Function App in LittersoftRG. The existing Zip Deploy profile is unchanged.

Before packaging, the profile runs Configure-Jobsweeper.ps1 to apply the mapped Azure settings. It requires Azure CLI and an authenticated `az login` session. Visual Studio's Azure sign-in alone does not sign in the CLI. The script runs with a process-only execution policy override; it does not change the machine's execution policy. Organizational policy may still require a signed script.

The mapping uses Key Vault references for SQL, invoice/queue/blob storage and Cosmos credentials. Other values come from the project's invoice/payment/notification configuration. The Cosmos endpoint is mapped as an HTTPS URL. Firebase file paths are cleared so production uses the existing Firebase secret names. The published package excludes local configuration and credential JSON files for this profile.

Prerequisites on the existing Function App:

- Windows hosting with .NET 10 isolated worker support.
- Valid AzureWebJobsStorage, preserved from Azure rather than replaced with local emulator storage.
- A managed identity with secret-read access to littersoft Key Vault. A user-assigned identity also needs correct credential selection for the worker's DefaultAzureCredential.
- Secrets SQLKey, BlobStorageKey, CosmosKey, FirebaseProviderServiceAccount and FirebaseParentServiceAccount must exist and contain valid values. BlobStorageKey must contain the complete storage connection string, not only an account key.
- Network access to SQL, storage, Cosmos and Key Vault; invoice stored procedures must already be deployed.

The profile stops before publishing if its configuration script fails. Applying settings can restart the app, and settings remain applied if a later code deployment fails. Unrelated Azure settings, including monitoring, are preserved. The script does not grant permissions or create infrastructure.

To review the mapping locally without contacting Azure:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src/Pawfront.Functions/Properties/PublishProfiles/Configure-Jobsweeper.ps1
```

Only setting names are printed. Add `-Apply` to apply settings without deploying code. Publishing through the new profile applies settings automatically.

Local validation does not establish that Azure credentials resolve or that the live app is healthy. After publishing, verify both invoice triggers start, then confirm a paid booking reaches Generated with a downloadable PDF.
