[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$mapping = Get-Content -Raw (Join-Path $PSScriptRoot 'jobsweeper.settings-map.json') | ConvertFrom-Json
$projectDirectory = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Get-Content -Raw (Join-Path $projectDirectory 'appsettings.json') | ConvertFrom-Json
$settings = @{}

function Copy-Section($Node, [string]$Prefix) {
    foreach ($property in $Node.PSObject.Properties) {
        if ($property.Name.StartsWith('_')) { continue }
        $name = "$Prefix`__$($property.Name)"
        if ($property.Value -is [pscustomobject]) {
            Copy-Section $property.Value $name
        } elseif ($null -ne $property.Value) {
            $settings[$name] = [Convert]::ToString($property.Value, [Globalization.CultureInfo]::InvariantCulture)
        }
    }
}

foreach ($section in $mapping.copySections) { Copy-Section $source.$section $section }
foreach ($path in $mapping.copyPaths) {
    $value = $source
    foreach ($part in $path.Split(':')) { $value = $value.$part }
    if ([string]::IsNullOrWhiteSpace([string]$value)) { throw "Missing configuration source: $path" }
    $settings[$path.Replace(':', '__')] = [string]$value
}
foreach ($property in $mapping.values.PSObject.Properties) { $settings[$property.Name] = [string]$property.Value }
foreach ($property in $mapping.keyVaultReferences.PSObject.Properties) {
    $settings[$property.Name] = '@Microsoft.KeyVault(SecretUri=' + $mapping.vaultUri.TrimEnd('/') + '/secrets/' + $property.Value + ')'
}

# Validation mode prints names only and makes no Azure calls or changes.
Write-Host "Target: $($mapping.functionApp) / $($mapping.resourceGroup)"
$settings.Keys | Sort-Object | ForEach-Object { Write-Host "Mapped: $_" }
Write-Host 'Preserved from Azure: AzureWebJobsStorage, monitoring and other unrelated settings.'
if (-not $Apply) { return }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Install it and run az login before publishing with this profile.'
}
$scope = @('--subscription', $mapping.subscription, '--resource-group', $mapping.resourceGroup, '--name', $mapping.functionApp)

# Do not allow CLI diagnostics or setting values into the publish log.
$appJson = & az functionapp show @scope --output json --only-show-errors 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the target Function App. Check Azure login and access.' }
$app = ($appJson -join "`n") | ConvertFrom-Json
if ($app.reserved) { throw 'This profile targets Windows x64; the target app is Linux.' }
if (-not $app.identity.principalId -and -not $app.keyVaultReferenceIdentity) {
    throw 'Configure a managed identity with Key Vault secret access on jobsweeper before publishing.'
}
$remoteJson = & az functionapp config appsettings list @scope --output json --only-show-errors 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Cannot read remote application settings.' }
$remote = ($remoteJson -join "`n") | ConvertFrom-Json
foreach ($name in $mapping.preserveRequiredRemoteSettings) {
    $entry = $remote | Where-Object name -EQ $name
    $identityStorage = $remote | Where-Object { $_.name -eq 'AzureWebJobsStorage__accountName' -or $_.name -eq 'AzureWebJobsStorage__blobServiceUri' }
    if ((-not $entry -or [string]::IsNullOrWhiteSpace($entry.value)) -and -not $identityStorage) {
        throw "Configure $name on the Function App first. Local emulator storage must not be deployed."
    }
    if ($entry.value -eq 'UseDevelopmentStorage=true') { throw 'AzureWebJobsStorage points to the local emulator.' }
}

# Check required secret existence without printing or persisting secret values.
# The app identity separately needs permission to read them at runtime.
$secretNames = @($mapping.keyVaultReferences.PSObject.Properties.Value) + @(
    $settings['Notifications__Provider__CredentialsSecretName'],
    $settings['Notifications__PetParent__CredentialsSecretName'])
$vaultName = ([uri]$mapping.vaultUri).Host.Split('.')[0]
foreach ($secret in ($secretNames | Sort-Object -Unique)) {
    & az keyvault secret show --subscription $mapping.subscription --vault-name $vaultName --name $secret --query id --output none --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Cannot verify required Key Vault secret '$secret'. Check that it exists and your login can access it." }
}

# Only nonsecret values and Key Vault references are written to this temporary file.
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) ('jobsweeper-settings-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($temporaryPath, ($settings | ConvertTo-Json -Depth 8))
    & az functionapp config appsettings set @scope --settings "@$temporaryPath" --output none --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply Function App settings. Publishing stopped.' }
} finally {
    Remove-Item -LiteralPath $temporaryPath -ErrorAction SilentlyContinue
}
Write-Host 'Application settings applied. Continue publishing; verify trigger startup and Key Vault resolution afterward.'
