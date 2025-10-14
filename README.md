# Jellyfin Poster Rotator

Rotate poster images in Jellyfin by building a small local pool of images next to each media item. The plugin fills the pool from your enabled metadata image providers, then cycles through those posters on a schedule without redownloading every time.

---

## Features

-Works with both Movies and TV Shows (toggleable in settings)
-Per-library enable/disable control (choose which libraries participate)
 - Builds a per-item .poster_pool folder for each movie or show
-Downloads posters from providers like TMDb and Fanart when available
-Saves a snapshot of the current primary poster as pool_currentprimary.*
-Sequential or random rotation with a configurable cooldown
-Supports extra poster patterns (e.g. cover.jpg, *-alt.png)

---

## Requirements

- Jellyfin **10.10.3** or newer  
- **.NET 8 runtime** on the server  
- At least one remote image provider enabled in Jellyfin (e.g., TMDb or Fanart)  

---

## Install

1. In your Plugin Catalog, Add the Repo: https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
2. In My Plugins, install the Poster Rotator plugin
3. Click the plugin tile to edit the settings
4. Restart Jellyfin

## Manual Install

1. Download the latest release `.zip` from the **Releases** page.  
2. Stop Jellyfin.  
3. Extract and copy `Jellyfin.Plugin.PosterRotator.dll` to a folder in the server plugins folder:  
   - **Windows:** `C:\ProgramData\Jellyfin\Server\plugins`  
   - **Linux:** `/var/lib/jellyfin/plugins` or `/var/lib/jellyfin/plugins/local`  
   - **Docker:** bind mount a plugins folder and place the `.dll` there  
4. Start Jellyfin.  
5. Go to **Dashboard → Plugins → Poster Rotator → Settings**.  

---

## How it Works

When the scheduled task runs:  

 - The plugin looks for or creates `<media directory>/.poster_pool`.  
 - The plugin looks for or creates `<media directory>/.poster_pool`.
- It tops up the pool using your enabled metadata image providers until the pool size setting is reached.  
- If no images are found, it copies the current primary poster into the pool once.  
- It rotates to the next image in the pool and updates the **Primary poster** file.  
- The plugin nudges Jellyfin so clients refresh.

Important: Jellyfin and clients sometimes cache images. After the plugin rotates posters you may need to:

- Run a library scan (Dashboard → Scheduled Tasks → Library Scan) so Jellyfin notices file changes, or
- Use the Images tab on the media item and hit Refresh, or
- Restart Jellyfin if you don't see updated posters.

Note: The settings page uses the saved configuration to list libraries. The UI does not attempt an automatic refresh of server-side library discovery; edit the saved library names in the plugin settings if a library is missing.

**Files created per item:**  
- `.poster_pool/pool_currentprimary.<ext>` → snapshot of the current primary  
- `.poster_pool/pool_<timestamp>.<ext>` → downloaded candidates  
- `.poster_pool/rotation_state.json` → rotation state and cooldown tracking  
- `.poster_pool/pool.lock` → created when **Lock After Fill** is enabled  

---

## Settings

- **Pool Size** → number of posters to keep in the pool  
- **Sequential Rotation** → rotate in stable order; otherwise, random  
- **Min Hours Between Switches** → cooldown (default: 24)  Note: update this and the Scheduled Task Rotate Movie Posters (Pool Then Rotate) to process more frequently in order to cycle more often.
- **Extra Poster Patterns** → additional filename globs to include  

💡 *Tip: The scheduled task can run more often than your cooldown. The plugin skips fetching new images for movies still within the Min Hour window.*  

---

## Running the Task

Go to: **Dashboard → Scheduled Tasks → Rotate Movie Posters (Pool Then Rotate)**  

- Run on demand, or schedule daily/hourly.  
- Cooldown prevents pinging metadata providors too much.
- Note: Library Scan required to actually see the new posters. You can manually run one or sync up the library scan scheduled task with the rotate posters task.  

---

## Troubleshooting

**Pool stays at 1 and does not download more**  
- Ensure TMDb and Fanart are installed and enabled  
- Verify server can reach provider endpoints  
- Check server log for details

**Posters do not appear to change**  
-Run a full scan of the media library after rotating posters
-Check images in the library card

**Posters do not appear to change**
- Jellyfin and clients often cache images; the plugin updates files on disk but the server or clients may keep the old image in cache.
- Steps to force Jellyfin to notice changes:
   1. In Jellyfin Admin → Scheduled Tasks → run the appropriate **Library Scan** (or run the full library scan) for the library that contains the changed item.
   2. Open the media item in Jellyfin, go to the **Images** tab and click **Refresh** to force image reindex for that item.
   3. Clear browser cache or open the instance in a private window to avoid client-side cache.
   4. If nothing else works, restart the Jellyfin server to force re-indexing.

**What the plugin already does to nudge Jellyfin**
- The plugin updates the target poster file and touches its last-write timestamp.
- It updates the parent folder timestamp and writes/updates a `.posterrotator.touch` file in the library root as a lightweight 'nudge' for file watchers.
- It attempts best-effort calls via reflection to invoke refresh methods on the library/metadata services when available. These reflection calls are version-dependent and may silently no-op if the server's API differs.

These measures work on many setups but are not guaranteed to force an immediate UI refresh on all Jellyfin versions or with all reverse proxies/caches.

**No providers detected**
- Restart server after installing provider plugins
- Ensure the item is a Movie and has provider IDs  

---

## Build from Source

```bash
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release
