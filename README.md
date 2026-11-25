# Presentation Player

This is a Windows app for running presentations at events. It opens Google Slides decks, PowerPoint files and PDFs, and has two outputs:

- **Slides** is the presentation itself. Google Slides decks play with their animations, transitions and video.
- **Presenter view** is the current slide, the next slide and your speaker notes.

Both are sent over NDI (as **Presentation Slides** and **Presentation Notes**), and each can also be shown full screen on a display or in a window.

You drive it from the app window, the keyboard, a presentation clicker, a phone on the same network, or over HTTP from something like Companion or a Stream Deck.

## What you need

- Windows 10 (version 2004 or later) or Windows 11, 64-bit.
- The NDI 6 runtime. Installing [NDI Tools](https://ndi.video/tools/) gets you this.
- The Microsoft Edge WebView2 runtime. Windows 11 already has it.
- For PowerPoint files, PowerPoint or [LibreOffice](https://www.libreoffice.org/) (free). Not needed for Google Slides or PDFs.
- The [.NET 9 SDK](https://dotnet.microsoft.com/download) if you're building from source.

For Google Slides, the simplest setup is a deck shared as "Anyone with the link can view", which works without signing in. For decks that are only shared with particular people, sign in to a Google account that can open them (see [Private decks](#private-decks)).

## Download

Download and run [PresentationPlayer-Setup.exe](https://github.com/internal-events-support/presentation-player/releases/latest/download/PresentationPlayer-Setup.exe), also on the [Releases](https://github.com/internal-events-support/presentation-player/releases) page. It installs the app for your Windows account, without needing an administrator, adds it to the Start menu and desktop, and opens it. It also installs the WebView2 runtime if that's missing, but not the NDI one. Windows SmartScreen may warn you because the installer isn't code-signed.

If you have one of the single-file versions (2.0.0 and earlier), close it, run the installer and delete the old exe. Your settings and Google sign-in are kept. Windows may ask about the firewall again.

To remove it, use Settings → Apps in Windows. Settings and the Google sign-in stay on the computer in case you install it again.

### Updates

The app looks for a new version when it starts, and every six hours or so while no presentation is open. It downloads it in the background and installs it when you close the app. It never updates or restarts by itself while it's running, or when it starts, so reopening it during a show gives you the version you had.

When a version is ready an Update button appears in the top bar, to install it straight away: the app closes, updates and opens again, which takes a few seconds. If another copy of the app is running it isn't installed until that's closed too.

Settings → Updates has the version you're running, a button to check now, and a switch to turn automatic updates off. With it off the app only looks when you press the button, and only installs when you press Update.

## Building and running

```bash
dotnet run -c Release --project PresentationPlayer
```

To make a folder you can copy to another PC:

```bash
dotnet publish PresentationPlayer -c Release -r win-x64 --self-contained -o publish
```

## Using it

Paste the deck's share link (the normal `docs.google.com/presentation/d/.../edit` one) and press Open. For a PowerPoint or PDF file press Browse, drop it on the window, or paste its path. The two monitors in the middle of the window show exactly what each output is showing.

Next and Previous behave like a clicker: Next plays the slide's next animation and only moves on once there are none left. Click any slide in the strip at the bottom to jump to it, or type its number and press Enter.

Under the presenter view there's a text size control for the notes, an Auto-fit switch that makes each slide's notes as big as will fit, and buttons to scroll long notes. Bold and italic text in the notes is shown that way. Settings → Presenter view sets whether the next slide is as wide as the current one or half that.

Slides skipped in Google Slides, or hidden in PowerPoint, are dimmed in the strip and marked Skipped. They aren't shown, but they keep their number, so the numbers match.

If a slide has a video or audio clip, a Play button appears on the slide monitor with how far through it is, next to a button that takes it back to the start. Audio set to play on click in Slides only starts when you press Play here. While the slide is making any sound, a small level meter shows on the right of the monitor.

The (i) next to the deck's name lists every slide with video or audio: what it is, whether it starts automatically, on click or manually, whether its sound is muted, and whether it can actually play. Slides only plays a Drive video or audio file for people who can open that file, even if they can open the deck, so a clip that isn't shared with the Google account signed in here (or publicly) shows as "No access". Click it to open Google's "You need access" page on that account, with Viewer picked, and press Request access to ask the owner. The same button appears on the message you get when the presentation itself can't be opened. YouTube videos that are private, removed or not allowed to be embedded are flagged too. When anything can't play, the (i) turns red. It's worth a look before the show so nothing catches you out.

If someone edits the deck in Google Slides, or saves the file, while it's open, the app notices within about 30 seconds: the Refresh button, under the open deck in the list on the left, gets an amber dot and a message pops up. Press Refresh to pull the changes in. You stay on the slide you were on. The app never refreshes by itself, so nothing changes on air until you decide. Close (Ctrl+W) puts both outputs back to a holding screen ("The presentation will begin shortly" on the slides feed). If you close by accident, the message that pops up has a Reopen button.

The app remembers recent decks, the last one you had open, your notes settings and the window position.

### Displays and windows

The menu at the top right of each monitor in the app sets where that output is shown on this computer, as well as on NDI:

- **Full screen** on any of the displays connected to the computer. If the other output is already on that display they swap.
- **Window**, which can be moved and resized, and captured or shared by other software.
- **No display**, which is how it starts.

It's remembered for next time. If a display is unplugged, its output closes, the menu says it isn't connected, and the output comes back when the display does. The computer won't go to sleep while an output is open.

To leave full screen press Esc or double-click it, or pick No display. The presenting keys below work in the output windows too, so a clicker works whichever is in front.

### PowerPoint and PDF files

PDF pages are shown as slides. There are no notes.

PowerPoint files (.pptx, .ppsx, .pptm, .ppsm) have their notes and hidden slides. The slides are drawn by PowerPoint if it's on the computer, otherwise by LibreOffice, the first time the file is opened and again after it changes. PowerPoint's own are exact; LibreOffice's can differ in places, mostly fonts, so look through them first. Slides are still pictures with everything on them, so animations and transitions don't play.

Video and audio do play: by themselves, on a click of Next, or with the Play button, as they're set up in the file, with their trimming, volume and looping. MP4 (H.264), WebM, MP3, M4A and WAV clips work, and YouTube videos. The (i) list flags anything that won't play, such as WMV.

Files can be in master decks along with Google Slides decks. Phones and the API can open files that have been opened in the app before, but not others.

### Keyboard

| Keys | Does |
|---|---|
| → or Space or PgDn | Next |
| ← or PgUp | Previous |
| Home / End | First / last slide |
| A number, then Enter | Go to that slide |
| P | Play or pause the video or audio |
| Shift+P | Video or audio back to the start |
| + / − | Bigger / smaller notes |
| A | Auto-fit notes on or off |
| ↑ / ↓ | Scroll the notes |
| R | Refresh |
| Ctrl+W | Close the presentation |
| Ctrl+O | Open a presentation |
| Ctrl+, | Settings |
| ? | List the shortcuts (Settings → Keyboard shortcuts) |

### From a phone or tablet

Settings → Remote control shows one or more addresses. Open one in a browser on any device on the same network and you get the same controls, laid out for a small screen. Settings can only be changed on the PC running the app.

## Master decks

A master deck plays several presentations as one, without copying anything into a combined deck. Press + next to Master decks, give it a name, and paste the links, or browse for files, in the order they'll be shown. For each one you can skip a number of slides from its start and from its end, for example to leave out a title slide and a Q&A slide. Those are counts from each end rather than slide numbers, so slides added in the middle of a presentation come along by themselves. Hidden slides don't count.

Only the links (and where the files are) are saved. The slides are pulled fresh each time you open the master deck, and Refresh picks up edits to any of its presentations, keeping your place. The app tells you when one of them has been edited.

Moving from one presentation to the next is a straight cut, because the next one is already loaded out of sight. If the next slide starts with a video or sound, that starts when you get there. Numbers, jumping to a slide and the presenter view's next slide all run through the whole master deck, and the strip shows where each presentation starts. Google decks in it have to be public or shared with the signed-in account.

## Private decks

If a deck isn't shared as "Anyone with the link", go to Settings → Google account and press Sign in. A small window opens with Google's normal sign-in page. Sign in there with an account that can open the deck, and the window closes on its own once you're in. After that, decks shared with that account open the same way as public ones, with notes, previews, animations and video. Public decks keep working whether you're signed in or not.

Google ends a session after a while, sooner for some organisations. The app checks every ten minutes, when it starts and when a deck is refused. If Google has signed you out it says so, and a Sign in again button stays in the top bar until you do. Meanwhile it works as if you weren't signed in, so public decks and their videos still play.

One account is signed in at a time. Sign out from the same place. If a deck won't open, the app tells you whether it isn't shared publicly (sign in) or isn't shared with the account you're signed in with (ask the owner to share it).

The app doesn't keep a password or token of its own. Your Google session lives in the app's built-in browser, in its own profile, the same way a browser remembers you. Windows encrypts it for your user account. Signing in and out can only be done in the app window, never from a phone or over the API. A second copy started with `--settings` has its own sign-in.

## Audio

Sound from videos (and any audio on the slides) doesn't come out of Windows' default speakers on its own. The app takes it over and plays it where you tell it to. In Settings → Audio you can pick:

- an **Output device**, which would normally be the interface feeding your mixer. This starts out as the system default.
- a **Monitor device**, for example headphones for the operator. This starts out off.

Each one has its own volume. Changes apply as soon as you make them, so you can set levels while a video is playing. Audio isn't sent over NDI.

### ASIO

For the lowest delay, pick an **ASIO** driver as the device. Three more settings then appear:

- **ASIO outputs** picks which pair of the interface's outputs to use.
- **Safety buffer** is how much audio is kept queued to cover uneven delivery from the browser. It starts at 60 ms. Go lower for less delay, and go back up if you hear dropouts. It always stays above one driver buffer.
- **Driver settings** opens the driver's own window, which is where the buffer size is usually set. Smaller buffers mean less delay and more load on the PC.

The line next to it shows the driver's buffer, its reported output latency and an estimate of the total delay.

A few things to know:

- Only one of Output and Monitor can use ASIO at a time, but the other can still be a normal device.
- It needs a 64-bit driver. Interfaces that only come with a 32-bit driver won't be listed.
- The app runs the driver at 48 kHz and will switch the interface to that rate if it's set to something else. Check this if the interface is clocked with other gear.
- Many drivers can only be used by one app at a time, so other software using the same driver may stop it opening.

## Settings

Settings also covers the NDI source names, output resolution and frame rate, the remote control port, and whether other devices are allowed to connect at all. These take effect at startup, so if you change one the app offers to restart when you save. You can leave it for later; a Restart to apply button stays in the top bar. Everything else applies straight away.

If your network uses an NDI discovery server, put its address in **Discovery server** (for example `192.168.1.10`, or `ndi-ds.local:5959` if it isn't on the default port; separate several with commas). Leave it empty and NDI finds receivers automatically, the same as before. The setting only applies to this app. Everything else from NDI Access Manager (groups, adapters, multicast and so on) still applies, and other NDI apps on the PC aren't affected.

Everything is saved in `%APPDATA%\PresentationPlayer\settings.json`. Settings and the Google sign-in from when the app was called Google Slides NDI Presenter are brought along the first time.

To run a second copy with different source names and its own port, give it its own settings file:

```bash
PresentationPlayer.exe --settings "C:\path\to\second-settings.json"
```

## HTTP API

The app listens on port 8080 by default. Every action takes a GET or a POST and replies with the current state as JSON, so you can paste these straight into Companion.

| Endpoint | What it does |
|---|---|
| `/api/next`, `/api/previous` | Next / previous (`/api/prev` works too) |
| `/api/goto/{n}` | Go to slide n, counting from 1 |
| `/api/load?url=...` | Open a presentation. You can also POST `{"url": "..."}`. For a master deck use `master:` and its name, e.g. `master:Day 1 plenary`. For a file that's been opened before, its full path |
| `/api/refresh` | Reload the open presentation |
| `/api/close` | Close it and show the holding screen |
| `/api/video/play`, `/pause`, `/toggle`, `/restart` | Control the video or audio on the current slide |
| `/api/notes/increase`, `/decrease` | Notes text 4px bigger / smaller |
| `/api/notes/size/{px}` | Set the notes size (12 to 300) |
| `/api/notes/autofit/on`, `/off`, `/toggle` | Auto-fit notes |
| `/api/notes/scroll/up`, `/down`, `/top` | Scroll the notes |
| `GET /api/state` | The current state |
| `GET /api/preview/slide.png`, `/notes.png` | A still of each output |

The state includes `outOfDate`, which is true when the deck has been edited since it was opened or last refreshed.

Some of what the app's own interface uses could also be handy elsewhere: `/api/events` (state as Server-Sent Events), `/api/meter` (audio levels), `/api/stream/slide.mjpg` and `/notes.mjpg` (live previews), `/api/slides/{n}.jpg` (slide thumbnails) and `/api/masters` (the saved master decks).

There's no password on the API. If you're on a network you don't trust, turn off "Allow other devices" in Settings and only this PC will be able to control the app. Windows may ask about the firewall the first time it runs. If port 8080 is taken, the app still works, but remote control stays off until you choose another port.

## How it works

The slides feed is Google's own embedded Slides player running in a hidden browser (WebView2). The app records its screen with the Chrome DevTools screencast API and sends each frame over NDI. That's why animations and video look the same as they do in Slides.

The list of slides, the speaker notes and which slides are skipped come from the page of Google's player, so a deck opens in about a second. The slide pictures follow: the ones around the current slide first, then those in view in the strip, then the rest. They're drawn in a hidden browser from the vector drawings of the slides that are also in the player's page, so a few hundred slides take around 20 seconds. Pictures at full resolution come from Google's PDF export, which takes Google up to a minute to make for a deck that size. A slide that can't be drawn uses Google's own preview of it, but Google only allows a few of those at a time.

The drawn pictures of the last dozen decks are kept in `%LOCALAPPDATA%\PresentationPlayer\Pictures`, so opening one that hasn't changed shows them straight away, even after a restart. If it has been edited the old pictures stay until the new ones are ready. They're only used once Google has let the deck be opened, and signing out of Google in the app deletes them.

Google keeps a revision number for every deck that goes up each time it's edited. While a deck is open, the app reads it from the deck's page every 30 seconds, and if it's higher than when you opened or refreshed the deck, the deck is out of date.

PowerPoint and PDF files play in a page of the app's own that behaves like Google's player. PDF pages are drawn by Windows. Converted PowerPoint files are kept in `%LOCALAPPDATA%\PresentationPlayer\Converted`.

The display windows draw the same frames that go to NDI, scaled by the graphics card.

Videos are locked to the best quality Google offers for them. Google sets that limit, though; the Drive video in the test deck, for example, only goes up to 720p.

For audio, a small script inside the player routes each video's sound into the app instead of the speakers. The app then plays it on the devices you picked.

NDI reports connections rather than receivers, and each receiver opens two, so the receiver numbers in the API are that count halved.

## Cutting a release

Bump `<Version>` in `PresentationPlayer.csproj`, then publish a release on GitHub with a tag like `v1.2.0`. The Release workflow builds that tag (taking the version from the tag) and attaches these a few minutes later:

- `PresentationPlayer-Setup.exe`, the installer.
- `…-full.nupkg` and `…-delta.nupkg`, what installed copies update from. The delta is only what's changed since the version before.
- `releases.win.json`, which lists them for the app. Installed copies update once it's there.

Publishing a release sends it to every installed copy. Pre-releases aren't offered as updates. If an asset ever needs rebuilding, run the workflow by hand from the Actions tab and give it the tag.

The installer and updates are made with [Velopack](https://velopack.io). Keep the `Velopack` package and the `vpk` tool (in `.config/dotnet-tools.json`) on the same version. To try an update without publishing, make the packages locally with `dotnet vpk pack`, as the workflow does, and start an installed copy with the environment variable `PRESENTATIONPLAYER_UPDATES` set to their folder.

## Known limitations

- "Publish to web" links (the ones with `/d/e/` in them) can't be used. Use the share link instead.
- Google's sign-in page can refuse to work inside apps. If it ever does, share the deck as "Anyone with the link can view" instead.
- Animations and transitions in PowerPoint files don't play, and files with a password can't be opened.
- The next-slide preview shows the slide as it looks once all its animations have played.
- The slides feed goes through JPEG compression on the way out of the browser, so very fine detail can soften slightly.
- If Google won't let the app capture a slide's audio file, it still plays, but straight to the Windows default device rather than the ones picked in Settings → Audio.
- Audio runs about a tenth of a second behind the picture on a normal device. With ASIO and a small buffer it can arrive before the picture, so you may need to delay it at the mixer to keep video clips in sync.
