# HTTP API

The app listens on port 8080 unless you change it in Settings → Remote control. Actions work with GET or POST and reply with the current state as JSON, so you can paste these straight into Companion.

| Endpoint | What it does |
|---|---|
| `/api/next`, `/api/previous` | Next / previous (`/api/prev` works too) |
| `/api/goto/{n}` | Go to slide n, counting from 1 |
| `/api/load?url=...` | Open a presentation (see below) |
| `/api/refresh` | Reload the open presentation |
| `/api/close` | Close it and show the holding screen |
| `/api/video/play`, `/pause`, `/toggle`, `/restart` | Control the video or audio on the current slide |
| `/api/notes/increase`, `/decrease` | Notes text 4px bigger / smaller |
| `/api/notes/size/{px}` | Set the notes size (12 to 300) |
| `/api/notes/autofit/on`, `/off`, `/toggle` | Auto-fit notes |
| `/api/notes/scroll/up`, `/down`, `/top` | Scroll the notes |
| `GET /api/state` | The current state |
| `GET /api/preview/slide.png`, `/notes.png` | A still of each output |

`/api/load` also takes a POST with `{"url": "..."}`. The URL can be:

- a Google Slides link
- `master:` and the name of a master deck, e.g. `master:Day 1 plenary`
- the full path of a file that has been opened in the app before

`outOfDate` in the state is true when the deck has been edited since it was opened or last refreshed. The receiver numbers are NDI's connection count halved, because each receiver opens two connections.

The app's own interface uses these, which may be handy elsewhere:

| Endpoint | What it is |
|---|---|
| `/api/events` | The state as Server-Sent Events |
| `/api/meter` | Audio levels |
| `/api/stream/slide.mjpg`, `/notes.mjpg` | Live previews |
| `/api/slides/{n}.jpg` | Slide thumbnails |
| `/api/masters` | The saved master decks |

## Security

There is no password on the API. On a network you don't trust, turn off "Allow other devices" in Settings → Remote control, and only this PC will be able to control the app.

Windows may ask about the firewall the first time the app runs. If port 8080 is taken, the app still works but remote control stays off until you choose another port.
