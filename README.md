# fossegrim


## features
- [x] queue (right click add to queue) (#2)
- [x] playlists (right click add to playlist, edit playlist page, sharing playlists between users) (#3)
- [x] editing id3 tags (#1)
- [x] editing album art (search MusicBrainz + Cover Art Archive and Deezer, or upload)
- [x] tracking to database (#6)
- [x] last.fm integration and tracking (#7, #8)
- [x] visualizer(s) (#4)
  - analyse audio with the Web Audio API (BaseAudioContext): https://developer.mozilla.org/en-US/docs/Web/API/BaseAudioContext
  - render the final visualization with WebGL, not a 2D canvas
- [x] theming (#5), with custom themes and movable modules (#9)
- [x] library scanning: background scans with progress, quick (changed files only) and full, right-click rescan of an album or artist, scans started by other apps with an API key
- [x] renaming files from a template (`{artist}/{album}/{track:00} - {title}`), with a preview
- [x] library health: ID3v1/ID3v2 disagreements and files not named as the template says
- [x] random albums and tracks on the front page, fully random or adjusted (favours what you haven't played recently, or ever); playing from them keeps going with more random tracks or albums, and the preferred kind of random is a per-user setting
- [x] optional auto-fill when the queue ends (per-user setting): another random album after an album, more random tracks after a track
- [x] groundwork for apps: sign-ins that renew themselves and can be signed out from Profile -> Signed-in devices, disc numbers (albums play disc by disc), album and file details on every track, and resized, cached covers (`/cover/{id}?size=256`)
- [x] gapless playback and crossfade (1-12 s), per-user settings in the player bar and on the Profile page; tracks from the same album stay gapless
- [x] library search (ignores case and accents, matches the starts of words) from the top bar and in each list, plus sorting by name, artist, album, year, recently added, and your most or most recently played, in either direction; the API pages every list (`/api/albums?q=&sort=&order=&page=&pageSize=`) and searches everything at once (`/api/search?q=`)
