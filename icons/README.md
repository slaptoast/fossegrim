# Fossegrim icon set

Concept 9 ("Play on stone with stream"), exported from a 1024 px master.

## Folders

- **svg/**: vector masters.
  - `fossegrim-icon-rounded.svg`, `-square.svg`, `-circle.svg`: the full icon in each shape.
  - `android-adaptive-background.svg` and `-foreground.svg`: the two layers for Android adaptive icons.
  - `monochrome-play.svg`: the play button alone in black, for single-color uses.
- **png/**: general-purpose PNGs at 16–1024 px.
  - `rounded/`: transparent corners.
  - `square/`: full-bleed, no transparency.
  - `circle/`: round crop.
- **webp/**, **avif/**: rounded icon in modern web formats (192–1024 px).
- **gif/**: rounded icon at 16–512 px. GIF only supports on/off transparency, so the corners are slightly hard-edged; prefer PNG where you can.
- **web/**: drop these files in your site root and paste `head-snippet.html` into `<head>`.
  - `favicon.ico` (16/32/48) and an SVG favicon.
  - `apple-touch-icon.png`, `android-chrome-*`, `maskable-icon-*`.
  - `site.webmanifest`, `browserconfig.xml` and `mstile-150x150.png`.
  - `safari-pinned-tab.svg`.
  - `og-image-1200x630.png` for link previews.
- **ios/AppIcon.appiconset/**: drag into an Xcode asset catalog. All files are full-bleed squares with no transparency, since iOS applies its own rounded mask.
- **android/res/**: merge into your app's `res/` folder.
  - `mipmap-*/ic_launcher.png` and `ic_launcher_round.png`: legacy icons.
  - Background, foreground and monochrome adaptive layers at every density, with `mipmap-anydpi-v26/*.xml` wiring them up. The monochrome layer is for Android 13+ themed icons.
  - `play-store-icon-512.png` for the Play Console.
- **desktop/**:
  - `fossegrim.ico` for Windows, 16–256 px.
  - `fossegrim.icns` and `fossegrim-macos-1024.png` for macOS, padded to the macOS icon grid with a soft drop shadow.

## Notes

- Brand colors: stream #667eea → #764ba2 (slightly richer), play button #d6d6d6, chrome/theme color #212529.
- The Android adaptive background is the stone pattern zoomed about 1.5×, because Android crops the outer third of that layer. The play button stays the same size relative to the visible icon.
- At 16 px the facets blur together, but the button and stream still read.
