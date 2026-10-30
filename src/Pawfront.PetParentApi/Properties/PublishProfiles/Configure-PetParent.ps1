[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Get-Content -Raw (Join-Path $projectDirectory 'appsettings.json') | ConvertFrom-Json
[xml]$profile = Get-Content -Raw (Join-Path $PSScriptRoot 'littersoft-petparent - Web Deploy.pubxml')
$resourceId = [string]$profile.Project.PropertyGroup.ResourceId
if ($resourceId -notmatch '^/subscriptions/([^/]+)/resourceGroups/([^/]+)/providers/Microsoft.Web/sites/([^/]+)$') {
    throw 'Invalid App Service resource ID in the publish profile.'
}
$subscription = $Matches[1]
$resourceGroup = $Matches[2]
$appName = $Matches[3]
$vaultUri = [string]$source.AzureKeyVault.VaultUri
$secretName = [string]$source.Notifications.PetParent.CredentialsSecretName
foreach ($value in @($vaultUri, $secretName, $source.Firebase.ProjectId, $source.Notifications.PetParent.FirestoreDatabaseId)) {
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'Required Key Vault or Parent Firebase configuration is missing.' }
}
if ($source.Notifications.PetParent.ProjectId -ne $source.Firebase.ProjectId) {
    throw 'The notification project must match the Firebase authentication project.'
}
$settings = @{
    ASPNETCORE_ENVIRONMENT = 'Production'
    DOTNET_ENVIRONMENT = 'Production'
    AzureKeyVault__Enabled = 'true'
    AzureKeyVault__VaultUri = $vaultUri
    Notifications__PetParent__ProjectId = [string]$source.Firebase.ProjectId
    Notifications__PetParent__CredentialsSecretName = $secretName
    Notifications__PetParent__CredentialsFilePath = ''
    Notifications__PetParent__FirestoreDatabaseId = [string]$source.Notifications.PetParent.FirestoreDatabaseId
}
# Enabling Key Vault also switches these existing consumers to vault secrets.
foreach ($name in @('SqlConnectionString', 'CosmosKey', 'BlobStorageKey')) {
    $value = [string]$source.AzureKeyVault.Secrets.$name
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Missing AzureKeyVault:Secrets:$name." }
    $settings['AzureKeyVault__Secrets__' + $name] = $value
}
Write-Host "Target: $appName / $resourceGroup"
$settings.Keys | Sort-Object | ForEach-Object { Write-Host "Mapped: $_" }
# Default is local validation only; no Azure access or mutation.
if (-not $Apply) { return }
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Install it and run az login before publishing.'
}
$scope = @('--subscription', $subscription, '--resource-group', $resourceGroup, '--name', $appName)
$appJson = & az webapp show @scope --output json --only-show-errors 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the target App Service. Check Azure login and access.' }
$app = ($appJson -join "`n") | ConvertFrom-Json
if (-not $app.identity.principalId) {
    throw 'Enable the App Service system-assigned managed identity and grant it Key Vault secret read access before publishing.'
}
# Check existence using the publishing identity; runtime access belongs to the app identity.
# Never print or persist secret values.
$vaultName = ([uri]$vaultUri).Host.Split('.')[0]
$secretNames = @($secretName) + @($source.AzureKeyVault.Secrets.SqlConnectionString, $source.AzureKeyVault.Secrets.CosmosKey, $source.AzureKeyVault.Secrets.BlobStorageKey)
foreach ($secret in ($secretNames | Sort-Object -Unique)) {
    & az keyvault secret show --subscription $subscription --vault-name $vaultName --name $secret --query id --output none --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify required Key Vault secret '$secret'. Check its existence and your access." }
}
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) ('petparent-settings-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($temporaryPath, ($settings | ConvertTo-Json))
    & az webapp config appsettings set @scope --settings "@$temporaryPath" --output none --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply App Service settings. Publishing stopped.' }
} finally {
    Remove-Item -LiteralPath $temporaryPath -ErrorAction SilentlyContinue
}
Write-Host 'Parent API configuration applied. Verify app identity access to Key Vault and service-account access to Firestore after deployment.'
