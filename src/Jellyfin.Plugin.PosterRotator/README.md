# Jellyfin Poster Rotator

Rotate poster images in Jellyfin by building a small local pool of images next to each media item. The plugin fills the pool from your enabled metadata image providers, then cycles through those posters on a schedule without redownloading every time.

---

## Features

- Works with Movies, TV Shows, Seasons, and Episodes (toggleable in settings)
- Per-library enable/disable control (choose which libraries participate)
- Builds a per-item `.poster_pool` folder for each media item
- Downloads posters from providers like TMDb and Fanart when available
- Saves a snapshot of the current primary poster as `pool_currentprimary.*`
- Sequential or random rotation with a configurable cooldown
- Language preferences: filter posters by language, auto-detect original language (VO)
- Automatic cleanup of orphaned pools
- Lock pools after fill to prevent metadata updates from changing them

---

## Requirements

- Jellyfin **10.11.6** or newer
- **.NET 9 runtime** on the server
- At least one remote image provider enabled in Jellyfin (e.g., TMDb or Fanart)

---

## Install

1. In your Plugin Catalog, Add the Repo:
```
https://raw.githubusercontent.com/maelmoreau21/jellyfin-plugin-poster-rotator/refs/heads/main/manifest.json
```
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
- It tops up the pool using your enabled metadata image providers until the pool size setting is reached.
- If no images are found, it copies the current primary poster into the pool once.
- It rotates to the next image in the pool and updates the **Primary poster** file.
- The plugin nudges Jellyfin so clients refresh.

Important: Jellyfin and clients sometimes cache images. After the plugin rotates posters you may need to:

- Run a library scan (Dashboard → Scheduled Tasks → Library Scan) so Jellyfin notices file changes, or
- Use the Images tab on the media item and hit Refresh, or
- Restart Jellyfin if you don't see updated posters.

**Files created per item:**
- `.poster_pool/pool_currentprimary.<ext>` → snapshot of the current primary
- `.poster_pool/pool_<timestamp>.<ext>` → downloaded candidates
- `.poster_pool/rotation_state.json` → rotation state and cooldown tracking
- `.poster_pool/pool_languages.json` → language metadata per image
- `.poster_pool/pool.lock` → created when **Lock After Fill** is enabled

---

## Settings

- **Pool Size** → number of posters to keep in the pool
- **Sequential Rotation** → rotate in stable order; otherwise, random
- **Min Hours Between Switches** → cooldown (default: 24). Update this and the Scheduled Task to process more frequently in order to cycle more often.
- **Lock After Fill** → prevents metadata updates from changing the pool once full
- **Include Seasons / Episodes** → extend rotation to season and episode posters

### Language Preferences

- **Enable Language Filter** → control the number of posters per language in the pool
- **Preferred Language** → ISO code for priority posters (e.g., `fr` for French)
- **Max Preferred Language Images** → maximum posters in preferred language (remainder in VO)
- **Use Original Language** → auto-detect the media's original language for VO
- **Fallback Language** → used when auto-detection fails
- **Include Unknown Language** → include posters without language metadata

### Pool Management

- **Auto Cleanup Orphaned Pools** → automatically remove pools for deleted media
- **Cleanup Interval (days)** → how often to run orphan cleanup

💡 *Tip: The scheduled task can run more often than your cooldown. The plugin skips fetching new images for movies still within the Min Hour window.*

---

## Running the Task

Go to: **Dashboard → Scheduled Tasks → Rotate Movie Posters (Pool Then Rotate)**

- Run on demand, or schedule daily/hourly.
- Cooldown prevents pinging metadata providers too much.
- Library Scan may be required to actually see the new posters.

---

## Troubleshooting

**Pool stays at 1 and does not download more**
- Ensure TMDb and Fanart are installed and enabled
- Verify server can reach provider endpoints
- Check server log for details

**Posters do not appear to change**
- Jellyfin and clients often cache images; the plugin updates files on disk but the server or clients may keep the old image in cache.
- Steps to force Jellyfin to notice changes:
   1. In Jellyfin Admin → Scheduled Tasks → run the appropriate **Library Scan**.
   2. Open the media item in Jellyfin, go to the **Images** tab and click **Refresh**.
   3. Clear browser cache or open in a private window.
   4. If nothing else works, restart the Jellyfin server.

**What the plugin does to nudge Jellyfin**
- Updates the target poster file and touches its last-write timestamp.
- Updates the parent folder timestamp and writes a `.posterrotator.touch` file in the library root.
- Calls `UpdateToRepositoryAsync()` on the item to notify Jellyfin of the image change.

**No providers detected**
- Restart server after installing provider plugins
- Ensure the item has provider IDs (TMDb, TVDB, etc.)

---

## Build from Source

```bash
dotnet build src/Jellyfin.Plugin.PosterRotator/Jellyfin.Plugin.PosterRotator.csproj -c Release
```
