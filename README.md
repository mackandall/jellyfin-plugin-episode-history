# Episode History (Jellyfin plugin)

SageTV-style episode awareness for Jellyfin's DVR. SageTV remembers every episode it has
recorded or you have watched, shows that in the guide, and doesn't record the same episode
twice. Jellyfin forgets an episode as soon as its file is deleted, and its guide has no
watched state at all. This plugin adds:

- **A permanent history.** A SQLite database of its own records every finished recording
  and every watched episode or movie. Rows outlive the files, like MythTV's `oldrecorded`.
- **Guide marks.** Upcoming guide entries get symbols in front of their episode title,
  which is the text jellyfin-web, Wholphin, Moonfin and Android TV show in the guide cell:

  | Mark | Meaning |
  |---|---|
  | `✓` | watched by any user |
  | `●` | recorded, and the recording is in the library now |
  | `○` | recorded before, since deleted |

  They combine, e.g. `✓● What's in the Box?`. Entries with no episode title (most movies)
  get a word instead, e.g. `✓ Watched`.
- **Duplicate skipping.** An upcoming series-recording episode is cancelled while a
  recorded copy of that episode is in the library. Delete the copy and the next airing
  records again.

Target: Jellyfin 12.0.x (.NET 10).

## How episodes are matched

1. **Gracenote/TMS program id** (`EP000191100039`) when both sides have one. It identifies
   the episode on any channel and at any airtime. Jellyfin's XMLTV reader only reads ids
   given as `<episode-num system="dd_progid">`. Channels DVR sends them as
   `system="tms"`, so pair this plugin with **XMLTV Guide Cleaner** (1.0.0.0 built after
   2026-09-25), which copies them across.
2. Otherwise **series name + season/episode**, then **series name + episode title**.
   Names are compared case-, accent-, punctuation- and "The"-insensitively, with a
   trailing `(YYYY)` ignored. Recordings also match under their series folder name, which
   survives a metadata provider renaming the series.
3. Movies match on **title + year**.

Recording NFOs don't store the program id, so recordings made before this plugin was
installed match by name only. Recordings made after it store the id from their timer.

## What it touches

- **Guide entries**: only their episode title, and only for entries that haven't ended.
  Jellyfin's "Refresh Guide" leaves an entry alone while its guide data is unchanged, so
  marks survive refreshes. Entries the refresh does rewrite are re-marked right after it.
- **Scheduled recordings**: it cancels a series-recording episode already in the library.
  Jellyfin records that as a manual cancellation, so its own series refresh won't bring it
  back. Each recording is cancelled at most once, so re-enabling one by hand sticks.
  One-off (manual) recordings are never cancelled.
- **Keeping marks out of recordings**: Jellyfin copies an entry's episode title into the
  recording's file name and NFO when the recording is scheduled. So entries with a
  scheduled recording are never marked. A recording scheduled on an already-marked entry
  gets its copy refreshed: a series recording's series timer is re-saved unchanged, and a
  one-off recording is re-created. As a last resort, a finished recording whose name still
  carries a mark has it removed. The file name would keep it in that last case.
- A finished recording only counts as recorded if it ran for at least *Minimum share
  recorded* (default 80%) of the programme. Recordings are found by polling Jellyfin's
  in-progress recordings every 30 seconds. Jellyfin raises no library event for a DVR
  recording until it has finished, and by then the timer details are gone.
- Episodes and movies copied into a recordings folder by hand, and anything found by the
  library import, count as recorded only if they are at least *Minimum length for imported
  or hand-added recordings* (default 5 minutes) long, so stubs of stopped recordings don't.
- *Count episodes and movies from all libraries as in library* (default off) makes items
  in any library, not only DVR recordings, count as in library for the marks and for
  duplicate skipping.
- Changing a mark symbol is safe: the plugin remembers every symbol it has used and
  removes old marks before adding new ones.

## Build and install

```bash
dotnet build -c Release
sudo ./install.sh   # copies DLL + meta.json + icon.png into the plugin dir, restarts Jellyfin
```

For the "Mark as watched" button in jellyfin-web's guide, also patch the web client once
(and again after every jellyfin-web package upgrade, which overwrites the patch):

```bash
sudo ./install-web.sh   # adds one <script> line to /usr/share/jellyfin/web/index.html
```

Clicking a guide program then shows **Mark as watched** / **Mark as unwatched** under
the Record buttons. Admins also get **Forget this episode** when the history knows
something about the episode: press it twice to delete that history (watched marks, and
records of recordings that have since been deleted), so the guide stops marking it and a
series recording will record it again. Items still in the library keep their history.
The script is served by the plugin (`/EpisodeHistory/web/guidemenu.js`),
so plugin updates update it too.

On first start the history is empty, so it is seeded from the library: recordings become
"recorded" and played items become "watched". The guide is marked about 90 seconds after
startup. Dashboard → Plugins → Episode History shows counts, the last sweep, recent
history and skipped recordings, with buttons to re-import or re-sweep.

## Styling marks in the web guide

With the web guide script installed, marked guide cells get CSS classes: `eh-marked`,
plus `eh-watched`, `eh-inlibrary` and `eh-recordedbefore`. The symbols themselves are
wrapped in `span.eh-mark`. Style them under Dashboard → General → Custom CSS, e.g.:

```css
.programCell.eh-watched .guideProgramNameText { font-style: italic; opacity: .6; }
.programCell.eh-inlibrary .programSecondaryTitle { color: #64b5f6; }
.eh-mark { font-size: 1.2em; }        /* or display: none, to hide the symbols */
```

This only affects web-based clients; TV apps ignore Custom CSS. It needs jellyfin-web's
"Show episode titles" guide setting on (the default), since the marks live in that line.

## Backup and restore

The history lives in Jellyfin's data folder, so it already survives plugin upgrades and
reinstalls. For rebuilding or moving the server, the settings page has **Download
backup** (one JSON file with every history row and skipped recording) and **Restore from
backup**:

- **Merge** (default) adds what the backup has and the server doesn't, and keeps
  everything already there. Restoring the same file twice changes nothing.
- **Replace** deletes the server's history first. The file is checked before anything
  is deleted.
- Watched history is mapped to users by **name**, because user IDs differ between
  servers. Rows for names with no matching user are skipped and reported; create the
  user and restore again to add them.
- Recordings are re-linked by **file path**, so a recording at the same path counts as
  "in library" again after the restore.

## Files

- History database: `<jellyfin data>/episode-history/history.db`
- API (admin): `GET /EpisodeHistory/Status`, `GET /EpisodeHistory/Recent`,
  `POST /EpisodeHistory/Sweep`, `POST /EpisodeHistory/Import`,
  `GET /EpisodeHistory/Backup`, `POST /EpisodeHistory/Restore?replace=false`,
  `DELETE /EpisodeHistory/Program/{programId}/History` forgets the episode that entry
  airs (every user's watched marks and records of deleted recordings; replies with how
  many rows were forgotten and how many were kept because their item is still in the library);
  (any user) `GET /EpisodeHistory/Program/{programId}` shows what the history knows about
  one guide entry, including whether the caller marked it watched by hand and (CanForget) whether an admin
  could forget it;
  `POST` / `DELETE /EpisodeHistory/Program/{programId}/Watched` marks or unmarks the
  episode that entry airs as watched by the caller (for something seen elsewhere). Every
  airing of that episode then gets the watched mark. The Wholphin fork's guide dialog
  offers this as "Mark as watched".
