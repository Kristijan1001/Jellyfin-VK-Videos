param(
    [Parameter(Mandatory=$true)][string]$DataDirectory,
    [string]$VkSourceScript,
    [string]$JellyfinUserId
)
$ErrorActionPreference = 'Stop'
$taskData = (Resolve-Path -LiteralPath $DataDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $taskData 'config\system.xml'))) { throw 'This does not look like a Jellyfin data directory.' }
$taskProject = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Jellyfin.Plugin.VkVideos.csproj') -Raw)
$taskFolder = 'VK Videos_' + $taskProject.Project.PropertyGroup.AssemblyVersion
$taskSource = Join-Path (Join-Path $PSScriptRoot 'dist') $taskFolder
$taskDll = Join-Path $taskSource 'Jellyfin.Plugin.VkVideos.dll'
if (-not (Test-Path -LiteralPath $taskDll)) { throw 'Build the plugin first with build.ps1.' }
$taskTarget = Join-Path (Join-Path $taskData 'plugins') $taskFolder
$taskInstalledDll = Join-Path $taskTarget 'Jellyfin.Plugin.VkVideos.dll'
if (Test-Path -LiteralPath $taskInstalledDll) {
    if ((Get-FileHash -LiteralPath $taskDll).Hash -ne (Get-FileHash -LiteralPath $taskInstalledDll).Hash) {
        throw 'A different VK Videos build already exists. Stop Jellyfin and back up that plugin folder before replacing it.'
    }
    Write-Output 'This plugin build is already present.'
} else {
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    Copy-Item -LiteralPath $taskDll -Destination $taskTarget
    Copy-Item -LiteralPath (Join-Path $taskSource 'meta.json') -Destination $taskTarget -Force
}
$taskConfig = Join-Path $taskData 'plugins\configurations\Jellyfin.Plugin.VkVideos.xml'
if ($VkSourceScript -and -not (Test-Path -LiteralPath $taskConfig)) {
    if (-not $JellyfinUserId) { throw 'Supply JellyfinUserId when importing private VK settings, or configure the plugin in the dashboard.' }
    $taskGuid = [Guid]::Parse($JellyfinUserId)
    & python (Join-Path $PSScriptRoot 'tools\import-vk-settings.py') --source $VkSourceScript --data-dir $taskData --user-id $taskGuid.ToString('N')
    if ($LASTEXITCODE -ne 0) { throw 'Settings import failed. Configure VK Videos in the dashboard before use.' }
}
if ((Get-FileHash -LiteralPath $taskDll).Hash -ne (Get-FileHash -LiteralPath $taskInstalledDll).Hash) { throw 'Installed DLL verification failed.' }
Write-Output "Plugin staged in $taskTarget"
Write-Output 'Restart Jellyfin once to load the plugin. This installer does not stop or restart the server.'
