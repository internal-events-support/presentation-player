# Development

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download). To build and run:

```bash
dotnet run -c Release --project PresentationPlayer
```

To make a folder you can copy to another PC:

```bash
dotnet publish PresentationPlayer -c Release -r win-x64 --self-contained -o publish
```

## How it works

### Slides output

Google's embedded Slides player runs in a hidden WebView2 browser. The app records its screen with the Chrome DevTools screencast API and sends each frame over NDI, so animations and video look the same as they do in Slides. The display windows draw the same frames, scaled by the graphics card.

Videos are locked to the best quality Google offers for them, which for some Drive videos is only 720p.

A small script inside the player routes each video's sound into the app, which plays it on the devices picked in Settings → Audio.

### Opening a deck

The list of slides, the speaker notes and which slides are skipped come from the page of Google's player, so a deck opens in about a second.

The slide pictures follow: the ones around the current slide first, then those in view in the strip, then the rest. They're drawn in a second hidden browser from the vector drawings in the player's page. A few hundred slides take around 20 seconds. Full resolution pictures come from Google's PDF export, which can take Google a minute for a deck that size. A slide that can't be drawn uses Google's own preview of it, but Google only allows a few of those at a time.

The drawn pictures of the last dozen decks are kept in `%LOCALAPPDATA%\PresentationPlayer\Pictures`, so a deck that hasn't changed shows them straight away, even after a restart. If it has been edited, the old ones stay until the new ones are ready. They're only used once Google has let the deck be opened, and signing out of Google in the app deletes them.

### Noticing edits

Google gives every deck a revision number that goes up with each edit. While a deck is open the app reads it from the deck's page every 30 seconds, and compares it with the one from when the deck was opened or refreshed.

### PowerPoint and PDF

These play in a page of the app's own that behaves like Google's player. Windows draws the PDF pages. Converted PowerPoint files are kept in `%LOCALAPPDATA%\PresentationPlayer\Converted`.

### Co-presenter clicker

The clicker page is `clicker/index.html`. The Clicker page workflow publishes it to GitHub Pages with a copy of `PresentationPlayer/wwwroot/peer-link.js` next to it, whenever either changes on `main`. The app's copy of that file has to match the published one, because both ends read and write the same codes.

The connection is a WebRTC data channel, set up without a signalling server. The app makes an offer, waits for its addresses, and packs what's needed (ICE username and password, the certificate fingerprint and up to six addresses) into the link after the `#`. The page answers the same way with its reply code. Each side rebuilds the other's session description from the code. The page can send only `next` and `previous`, and the app sends back the slide number.

To try the page locally, serve `clicker/index.html` and `peer-link.js` from the same folder over `http://127.0.0.1`, and swap the start of the app's link for that address.

## Cutting a release

Bump `<Version>` in `PresentationPlayer.csproj`, then publish a release on GitHub with a tag like `v1.2.0`. The Release workflow builds that tag, taking the version from it, and attaches these a few minutes later:

- `PresentationPlayer-Setup.exe`, the installer
- `…-full.nupkg` and `…-delta.nupkg`, which installed copies update from (the delta is what's changed since the version before)
- `releases.win.json`, which lists them for the app

Publishing a release sends it to every installed copy as soon as `releases.win.json` is there. Pre-releases aren't offered as updates. To rebuild the files for a release, run the workflow by hand from the Actions tab and give it the tag.

The installer and updates are made with [Velopack](https://velopack.io). Keep the `Velopack` package and the `vpk` tool (in `.config/dotnet-tools.json`) on the same version.

To try an update without publishing, make the packages with `dotnet vpk pack`, as the workflow does, and start an installed copy with the environment variable `PRESENTATIONPLAYER_UPDATES` set to their folder.
