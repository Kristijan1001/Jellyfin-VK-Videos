param([string]$Dotnet = (Join-Path $PSScriptRoot 'tools\dotnet\dotnet.exe'))
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
& $Dotnet build (Join-Path $PSScriptRoot 'src\Jellyfin.Plugin.VkVideos.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
$taskProject = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\Jellyfin.Plugin.VkVideos.csproj') -Raw)
$taskVersion = $taskProject.Project.PropertyGroup.AssemblyVersion
$taskPackageVersion = $taskProject.Project.PropertyGroup.Version
$taskDist = Join-Path $PSScriptRoot ("dist\VK Videos_" + $taskVersion)
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\bin\Release\net9.0\Jellyfin.Plugin.VkVideos.dll') -Destination $taskDist -Force
$taskMetadata = @{
    category = 'Channels'; changelog = 'Provide thumbnail aspect ratios on first load to prevent square-card layout changes.'; description = 'Browse VK playlists and stream their videos through Jellyfin.'
    guid = 'e4f461e0-710b-4b2d-b162-09cd1ed0a99e'; name = 'VK Videos'; overview = 'Your VK playlists in Jellyfin'
    owner = 'Kristijan1001'; status = 'Active'; targetAbi = '10.11.11.0'; version = $taskVersion
    timestamp = [DateTime]::UtcNow.ToString('o')
}
$taskMetadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDist 'meta.json') -Encoding utf8
Compress-Archive -Path (Join-Path $taskDist '*') -DestinationPath (Join-Path $PSScriptRoot ("dist\VK-Videos-" + $taskPackageVersion + '.zip')) -Force
Get-FileHash -LiteralPath (Join-Path $taskDist 'Jellyfin.Plugin.VkVideos.dll') -Algorithm SHA256
