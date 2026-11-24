# Wego

Wego is the support team's softphone for Windows: answer and place calls through the company phone system (SIP over WebRTC), keep a live call sheet with recordings and customer details, and see teammates' call status and chat in real time.

## Get the Windows installer

1. Open the **Actions** tab of this repository.
2. Click the latest green **Build Windows app** run.
3. Under **Artifacts**, download **Wego-Windows-Installer**, unzip it and run `Wego-Setup-x.y.z.exe`.

For a permanent download link, create a version tag (for example `v1.0.1`). The installer is then attached to a **Release**.

Windows may show "Windows protected your PC" because the app is not code-signed. Click **More info → Run anyway**.

## Set up the team server (Mac mini)

The team server passes live status, "answered this call" alerts and team chat between Wego apps.

```bash
node server/wego-server.js
```

It prints an address such as `ws://192.168.1.20:8787`. Enter it in **Wego → Settings → Team server** on every laptop. Keep the server running while the team works.

## Connect the phone system

In **Wego → Settings**, enter the SIP username, domain, password and the provider's **WebSocket (WSS)** address. The provider must have WebRTC enabled on the extension.

## Project layout

| Path | What it is |
| --- | --- |
| `app/index.html` | The Wego app (screens, calling, call sheet, team features) |
| `main.js`, `preload.js` | The Windows app shell (Electron) |
| `server/wego-server.js` | Team server, no packages needed |
| `.github/workflows/build-windows.yml` | Builds the Windows installer on GitHub |

## Run from source

```bash
npm install
npm start
```
