# VK Videos for Jellyfin

Browse your VK playlists under one **VK Videos** tile in Jellyfin, then stream the videos through your server. No separate VK website or Python service is needed.

![VK Videos](src/Assets/channel.png)

Requires **Jellyfin 10.11.11**. Built with .NET 9.

## Install

1. Open **Dashboard → Plugins → Manage Repositories** and add:
   - Name: `VK Videos`
   - URL: `https://raw.githubusercontent.com/Kristijan1001/Jellyfin-VK-Videos/main/manifest.json`
2. Find **VK Videos** in the plugin catalog and install it.
3. Restart Jellyfin, then open **Dashboard → Plugins → VK Videos → Settings**.
4. Enter your **VK owner ID** and **access token**, then save.

Use a negative owner ID for a community, or a positive ID for a personal account. The token must have access to that owner's video playlists.

For manual installation, download [the latest release](https://github.com/Kristijan1001/Jellyfin-VK-Videos/releases/latest), extract the ZIP into a `VK Videos_1.0.2.0` folder inside Jellyfin's `plugins` directory, and restart Jellyfin.

## Features

- Playlist folders with video titles and thumbnails.
- Complete pagination for large playlists.
- Correct thumbnail proportions on first load and when changing pages.
- Fresh playback URLs, actual codec detection, and seeking through Jellyfin.
- MP4 playback with HLS fallback when VK provides it.
- Preferred quality up to 2160p, with fallback to an available quality.
- Optional **All Videos** folder, disabled by default.
- Per-user access through **Allowed Jellyfin user IDs**. An empty list allows all users with channel access.

The access token stays in the server's plugin configuration. Playback clients do not need it. The plugin only reads from VK; it does not upload, edit, or delete videos.

## Refresh

Listings refresh while browsing as their cache ages. The default interval is 15 minutes; Jellyfin's own listing cache can make a visible update take roughly two intervals.

For a full refresh, run **Dashboard → Scheduled Tasks → Refresh VK Videos**. It runs every six hours by default, and you can change its schedule there. Incomplete or failed listings preserve the previous complete cache.

## Playback notes

Jellyfin handles client compatibility through direct streaming or transcoding. The server needs access to VK and its video CDN. Videos are streamed on demand; Jellyfin may create its normal temporary playback files.

Jellyfin caches playback-source lookups for five minutes, so changing the preferred quality can take that long to affect a recently played video. A video appearing in multiple playlists has a separate Jellyfin item and watched state in each playlist.

## Build

Install the .NET 9 SDK, then run:

```sh
dotnet build src/Jellyfin.Plugin.VkVideos.csproj -c Release
dotnet test tests/VkVideos.Tests.csproj -c Release
```

On Windows, `build.ps1` also creates the release ZIP in `dist/`. `Install.ps1 -DataDirectory 'YOUR JELLYFIN DATA DIRECTORY'` stages that build without restarting the server.

The optional `tools/import-vk-settings.py` helper can import literal settings from an existing local VK uploader without executing it. Python is only needed for this helper.

## Version 1.0.2

The **VK Videos** tile now also opens in clients that only open known library types. The plugin reports it as a folder-style library; it is still a channel, and Jellyfin Web opens it on the same page as before. Jellyfin Web's home screen does not show a "Recently Added" row for folder-style libraries, so it no longer shows one for VK Videos.

Validated with 19 automated tests and on a live Jellyfin 10.11.11 server.

## Version 1.0.1

Fixes square thumbnails appearing until the page is refreshed. The plugin supplies the selected thumbnail's dimensions before Jellyfin chooses the card layout, preserving dimensions already known to Jellyfin. No theme or web-client changes are required.

Validated with 18 automated tests, live playlist pagination, first-load and next-page card layouts, 2160p browser playback, and byte-range seeking. Other Jellyfin clients and HLS-only VK streams have not been live-tested.
