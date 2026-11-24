# Wego

Wego is the support team's softphone for Windows: answer and place calls through the company phone system, keep a live call sheet with recordings and customer details, and see teammates' call status and chat in real time.

## Features

- SIP over **UDP, TCP or TLS** (any SIP phone system, like MicroSIP) or **WebRTC / WSS**
- Hold, mute, in-call keypad (DTMF), blind transfer, call waiting with swap
- Automatic call recording (stereo WAV: customer left, agent right) in `Documents\Wego Recordings`
- Steady, clear audio: auto volume for both sides, noise reduction, jitter buffer, boost sliders
- Hold music for the customer (original jazz included, or your own MP3) with a female-voice announcement after 30 seconds
- Choice of microphone and speaker/headset, auto-answer, outbound proxy
- Light and dark theme
- Live call sheet with customer details, search, filters and CSV copy
- Team alerts ("James answered this call"), live teammate status and team chat
- Runs in the tray, starts with Windows, global hotkeys, desktop notifications
- Updates itself from GitHub Releases

## Get the Windows installer

**Download:** https://github.com/55678io/wego/releases/latest/download/Wego-Setup.exe

No GitHub account is needed. This link always gives the newest version.

Every push to `main` builds a new installer and publishes it as a new Release automatically.

Windows may show "Windows protected your PC" because the app is not code-signed. Click **More info → Run anyway**.

## Set up the team server (Mac mini)

The team server passes live status, "answered this call" alerts and team chat between Wego apps.

```bash
node server/wego-server.js
```

It prints an address such as `ws://192.168.1.20:8787`. Enter it in **Wego → Settings → Team server** on every laptop. Keep the server running while the team works.

## Connect the phone system

In **Wego → Settings**, choose the transport (UDP is what MicroSIP uses by default), then enter the SIP username, domain/server and password. Auth ID and outbound proxy are optional. For **WebRTC / WSS**, also enter the provider's WSS address.

Hotkeys: **Ctrl+Alt+A** answer · **Ctrl+Alt+H** hang up · **Ctrl+Alt+M** mute · **Ctrl+Alt+W** show Wego.

## Project layout

| Path | What it is |
| --- | --- |
| `app/index.html` | The Wego app (screens, calling, call sheet, team features) |
| `main.js`, `preload.js` | The Windows app shell (Electron): tray, hotkeys, updates, engine bridge |
| `engine/` | Native SIP engine (C#, SIPSorcery): UDP/TCP/TLS, audio, recording |
| `server/wego-server.js` | Team server, no packages needed |
| `.github/workflows/build-windows.yml` | Builds the Windows installer on GitHub |

## Run from source

```bash
npm install
npm start
```
