# Media Cleanup

A Windows service that watches download folders, waits until each download has finished copying,
cleans it up and moves it to your library. It comes with a small configuration app.

```
www.movieRuz.xyz - Mummy 2001 Dual Tel+Eng.mkv   ->  Mummy 2001 Dual Tel+Eng\Mummy 2001 Dual Tel+Eng.mkv
tamilmv.us - Don.2003.Web-DL.mp4                 ->  Don 2003 Web-DL\Don 2003 Web-DL.mp4
Devil.2005.Web-DL.mp4                            ->  Devil 2005 Web-DL\Devil 2005 Web-DL.mp4

www.movieRuz.xyz - House 2020 Dual Tel+Eng\      ->  House 2020 Dual Tel+Eng\
    www.movieRuz.xyz - House S01E01.mkv                  House S01E01.mkv
    www.movieRuz.xyz - House S01E02.mkv                  House S01E02.mkv
```

## What it does

You add any number of **Movie** and **TV Show** sources. Each source has a source folder, an
optional destination folder (local or UNC, such as `\\nas\media\Movies`), an Active tick box and
its own set of actions:

| Action | What happens |
| --- | --- |
| Name cleanup | Removes website names from file and folder names and turns dots and underscores into spaces. Tags such as `Dual Tel+Eng`, `Web-DL`, `DD5.1` and `H.264` are kept. |
| Track names cleanup | Uses MKVToolNix to remove website names from the MKV title and track names and to remove tags. Nothing is re-encoded. |
| Set default audio track | Uses MKVToolNix to set the default audio track: Telugu 5.1, English 5.1, Telugu stereo, English stereo, Hindi 5.1, Hindi stereo. Files with none of these languages are left alone. |
| Subtitle cleanup | Cleans subtitle file names (language parts such as `.en.forced` are kept) and removes cues that advertise a website from `.srt`, `.vtt`, `.ass` and `.ssa` files. |
| Move under folder (movies) | A loose movie file gets its own folder named after the cleaned file name. Subtitles with the same name move with it. |
| Remove unwanted files | Deletes `.png`, `.txt` and `.nfo` files that belong to the download. The list is editable. |

After the enabled actions have run, the download is moved to the destination. A TV show folder
that already exists at the destination is merged, so new episodes join the existing ones. If you
leave the destination empty, everything is cleaned up in place.

The **Recent Activity** tab lists every download the service has handled, newest first. Select one to
see each step done to it: renames, MKV track changes, subtitle cleanup, removed files and where it was
moved. Failed downloads are shown in red with the reason.

The **Manual Cleanup** tab runs chosen actions once over a folder you already have, such as your existing
library. Pick the folder, say whether it holds movies or TV shows, and tick the actions you want; none are
ticked to begin with. **Preview** lists every change without touching anything, and **Run cleanup** does it.
Everything is cleaned in place and nothing is moved elsewhere. The run happens in the app under your own
Windows account, not in the service, and it refuses a folder that overlaps an active monitored source.

The big **Monitoring: ACTIVE / INACTIVE** button is a master switch for all sources. Changes made
in the app are saved straight away and the running service picks them up within a few seconds.

### When is a download "complete"?

The service scans each source every 30 seconds. A download is processed only when all of this is true:

- the size and modified time of every file have not changed for 60 seconds,
- no file has a partial-download extension (`.part`, `.!qB`, `.crdownload`, `.tmp` and similar),
- every file can be opened exclusively, meaning no download client or copy is still writing it.

Both times can be changed on the Settings tab. Point a source at the folder your download client
uses for **finished** downloads. A torrent that is still seeding from the source folder will stop
seeding once its files are renamed or moved.

### Safety

- Files are never overwritten. If a different file with the same name exists, the new one is named `Name (1).ext`. A file with the same name and size is treated as a duplicate.
- Loose files that do not share a name with a media file are never touched.
- If MKVToolNix reports an error for a file, that download stays where it is and is retried later (after 1, 2, 4 ... up to 60 minutes), like the original script, which skipped the move in that case.
- If a source or destination share is offline, the service logs a warning and tries again on the next scan.

## Install

You need Windows 10/11 or Windows Server, and [MKVToolNix](https://mkvtoolnix.download/) for the two MKV actions.

1. Get the installer, either way:
   - Open the **Installer** workflow on the repository's Actions tab, click **Run workflow**, and download `MediaCleanup-Setup-<version>.exe` from the finished run. Pushing a tag such as `v1.2.0` also publishes it as a GitHub Release.
   - Or build it yourself: with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Inno Setup 6](https://jrsoftware.org/isinfo.php) installed, run `.\scripts\publish.ps1` and then `ISCC.exe /DAppVersion=1.2.0 installer\MediaCleanup.iss`. The installer lands in `dist\`.
2. Run the installer. It copies the files to `C:\Program Files\MediaCleanup`, installs and starts the Windows service, and adds a Start menu shortcut. Installing a newer version over an old one keeps your configuration and the service's log-on account.
3. Open **Media Cleanup** from the Start menu (it asks for administrator rights).
4. **Give the service an account that can open your network shares.** A new service runs as
   Local System, which normally cannot open UNC paths. Click **Windows Services...**, open
   **Media Cleanup Service**, go to the **Log On** tab, choose **This account** and enter a Windows
   user that can read and write your shares. Windows grants that user the right to run services.
5. Back in the app, click **Stop service** and then **Start service** so the new account takes effect, add your sources on the Movies and TV Shows tabs,
   and check the Settings tab shows that MKVToolNix was found.

The service starts automatically with Windows and restarts itself if it crashes.

To install without the installer, unzip the `MediaCleanup-win-x64` artifact of the **Build** workflow (or the `publish\` folder) to a permanent place and either click **Install service** in the app or run these in an elevated prompt:

```
sc create MediaCleanupService binPath= "\"C:\Program Files\MediaCleanup\MediaCleanup.Service.exe\"" start= delayed-auto DisplayName= "Media Cleanup Service" obj= ".\YourUser" password= "YourPassword"
sc start MediaCleanupService
```

With `sc`, the account must already have the "Log on as a service" right; setting the account on the Log On tab grants it for you.

To try it without installing anything, run `MediaCleanup.Service.exe` in a terminal. It works the
same way and stops with Ctrl+C.

To uninstall, use **Apps > Installed apps** in Windows Settings. It stops and removes the service; your configuration and logs in `%ProgramData%\MediaCleanup` are kept.

The version number lives in `Directory.Build.props`. Change it there before building a new installer.

## Files

| Path | Purpose |
| --- | --- |
| `%ProgramData%\MediaCleanup\config.json` | The configuration. Edited by the app; can be edited by hand. |
| `%ProgramData%\MediaCleanup\logs\` | One log file per day, kept for 14 days. Every rename, edit, delete and move is logged. |
| `%ProgramData%\MediaCleanup\processed.json` | Remembers items cleaned in place so they are not processed twice. |

## Settings worth knowing

- **MKVToolNix folder**: leave empty to search `Program Files` and `PATH`. If your copy lives on a share, enter that folder; the service account must be able to open it.
- **Extra words to remove**: one per line. Web addresses are detected automatically, but a bare site tag such as `TamilMV`, or an address with an unusual ending that is written without `www.`, needs to be listed here. Listed words are removed from file names, folder names, MKV track names and subtitle lines.
- **Name cleanup is careful with dots.** In a name like `Movie.In.Time.2011` the words `In` and `Time` are kept, even though `.in` is a web ending. An address is removed when it starts with `www.` or `http`, is in brackets, is followed or preceded by ` - `, or ends in an unmistakable ending such as `.com`, `.net`, `.org` or `.xyz`.

## Development

```
dotnet test tests/MediaCleanup.Tests
```

| Project | Purpose |
| --- | --- |
| `src/MediaCleanup.Core` | All cleanup logic: name cleaner, MKV cleaner, subtitle cleaner, completion detection, moving, the scan engine. |
| `src/MediaCleanup.Service` | The Windows service host and file logger. |
| `src/MediaCleanup.ConfigApp` | The WinForms configuration app (`MediaCleanup.Config.exe`). Builds on Windows only. |
| `tests/MediaCleanup.Tests` | Unit and end-to-end tests, including every example above. |
