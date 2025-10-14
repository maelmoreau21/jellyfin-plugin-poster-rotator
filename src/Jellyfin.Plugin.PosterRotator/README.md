# Jellyfin Poster Rotator

Rotate poster images in Jellyfin by building a small local pool of images next to each media item. The plugin fills the pool from your enabled metadata image providers, then cycles through those posters on a schedule without redownloading every time.

---

## Features

 - Builds a per-item `.poster_pool` folder next to the media file or item folder  
- Downloads posters from providers like TMDb and Fanart when available  
- Saves a snapshot of the current primary poster as `pool_currentprimary.*`  
- Option to lock the pool after it reaches the target size  
- Sequential or random rotation with a configurable cooldown  
- Best-effort cache bust so clients notice the change  

---

## Requirements

- Jellyfin **10.10.3** or newer  
- **.NET 8 runtime** on the server  
- At least one remote image provider enabled in Jellyfin (e.g., TMDb or Fanart)  

---

## Install

1. In your Plugin Catalog, Add the Repo: https://raw.githubusercontent.com/jonah-jamesen/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
2. In My Plugins, install the Poster Rotator plugin
3. Click the plugin tile to edit the settings
4. Restart Jellyfin
4. (Optional) In Scheduled Tasks, run the Rotate Posters (Pool Then Rotate) task to seed your Poster Pools.

## Manual Install

1. Download the latest release `.zip` from the **Releases** page.  
2. Stop Jellyfin.  
3. Extract and copy `Jellyfin.Plugin.PosterRotator.dll` to the server plugins folder:  
   - **Windows:** `C:\ProgramData\Jellyfin\Server\plugins`  
   - **Linux:** `/var/lib/jellyfin/plugins` or `/var/lib/jellyfin/plugins/local`  
   - **Docker:** bind mount a plugins folder and place the `.dll` there  
4. Start Jellyfin.  
5. Go to **Dashboard → Plugins → Poster Rotator → Settings**.  

---

## How it Works

When the scheduled task runs:  

 - The plugin looks for or creates `<media directory>/.poster_pool`.  
- It tops up the pool using your enabled metadata image providers until the pool size setting is reached.  
- If no remote images are found, it copies the current primary poster into the pool once.  
- It rotates to the next image in the pool and updates the **Primary poster** file.  
- The plugin touches the file time and nudges Jellyfin so clients refresh.  

**Files created per item:**
 - `.poster_pool/pool_currentprimary.<ext>` → snapshot of the current primary  
 - `.poster_pool/pool_<timestamp>.<ext>` → downloaded candidates  
 - `.poster_pool/rotation_state.json` → rotation state and cooldown tracking  
 - `.poster_pool/pool.lock` → created when **Lock After Fill** is enabled  

---

## Settings

- **Pool Size** → number of posters to keep in the pool  
- **Lock Images After Fill** → stop metadata refreshes from downloading once pool reaches size  
- **Sequential Rotation** → rotate in stable order; otherwise, random  
- **Min Hours Between Switches** → cooldown (default: 24)  Note: update this and the Scheduled Task Rotate Movie Posters (Pool Then Rotate) to process more frequently in order to cycle more often.
- **Extra Poster Patterns** → additional filename globs to include  
- **Dry Run** → log actions without changing files

💡 *Tip: The scheduled task can run more often than your cooldown. The plugin skips items still within the Min Hour window.*  
Note: The plugin settings page lists libraries based on the saved configuration. The UI does not provide an automatic refresh of server-discovered libraries; if a library is missing, update its exact name in the plugin settings.
---

## Running the Task

Go to: **Dashboard → Scheduled Tasks → Rotate Movie Posters (Pool Then Rotate)**  

- Run on demand, or schedule daily/hourly.  
- Cooldown prevents over-rotation.  

---

## Troubleshooting

**Pool stays at 1 and does not download more**  
- Ensure TMDb and Fanart are installed and enabled  
- Verify server can reach provider endpoints  
- Check server log for provider attempts  

**Posters do not appear to change**  
- For testing, set `Min Hours Between Switches` to 1 and run the task twice  
- Force refresh the browser or check **Images** tab in the movie  

**Posters do not appear to change**
- The plugin writes files to disk but Jellyfin (and clients) may cache images. To force Jellyfin to reindex and show changes:
   1. Run **Library Scan** for the affected library in Admin → Scheduled Tasks.
   2. Open the media item and use the **Images** tab → **Refresh** to reindex images for that item.
   3. Clear your browser cache or test in a private window.
   4. As a last resort, restart Jellyfin.

**What the plugin does to help**
- Touches poster files and parent folder timestamps.
- Writes/updates a `.posterrotator.touch` file in the library root to hint file watchers.
- Attempts best-effort refresh via reflection of server metadata/image refresh APIs (this may silently fail on some Jellyfin versions).

**No providers detected**
- Restart server after installing provider plugins
- Ensure the item has provider IDs and the provider plugins are enabled

---

## Build from Source

```bash
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release
