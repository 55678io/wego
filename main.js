// Wego desktop app (Windows) – Electron main process
const { app, BrowserWindow, session, shell, ipcMain, Menu, Tray, globalShortcut, nativeImage, dialog } = require('electron');
const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');
const readline = require('readline');

app.setAppUserModelId('com.kdn.wego'); // needed for Windows notifications

let win = null, tray = null, quitting = false;
let engine = null, engineQueue = [];

// Only one Wego window at a time: a second launch focuses the first one.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => showWindow());
}

function showWindow() {
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}
function send(channel, payload) { if (win && !win.isDestroyed()) win.webContents.send(channel, payload); }

function createWindow() {
  win = new BrowserWindow({
    width: 1360,
    height: 880,
    minWidth: 900,
    minHeight: 640,
    title: 'Wego',
    icon: path.join(__dirname, 'build', 'icon.ico'),
    backgroundColor: '#f3f4f6',
    autoHideMenuBar: true,
    show: !process.argv.includes('--hidden'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      backgroundThrottling: false // keep ringing and timers accurate when minimised
    }
  });

  win.loadFile(path.join(__dirname, 'app', 'index.html'));

  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (e, url) => {
    if (!url.startsWith('file:')) { e.preventDefault(); shell.openExternal(url); }
  });

  // Closing the window keeps Wego running in the tray so calls still ring.
  win.on('close', e => {
    if (!quitting) { e.preventDefault(); win.hide(); }
  });
  win.on('focus', () => win.flashFrame(false));
  win.on('closed', () => { win = null; });
}

/* ---------------- tray ---------------- */
function createTray() {
  const icon = nativeImage.createFromPath(path.join(__dirname, 'build', 'icon.ico')).resize({ width: 16, height: 16 });
  tray = new Tray(icon);
  tray.setToolTip('Wego');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Open Wego', click: showWindow },
    { type: 'separator' },
    { label: 'Quit Wego', click: () => { quitting = true; app.quit(); } }
  ]));
  tray.on('click', showWindow);
}

/* ---------------- SIP engine (UDP / TCP / TLS) ---------------- */
function enginePath() {
  const packaged = path.join(process.resourcesPath || '', 'engine', 'wego-engine.exe');
  const dev = path.join(__dirname, 'engine', 'bin', 'wego-engine.exe');
  return app.isPackaged ? packaged : dev;
}
function engineAvailable() {
  try { return process.platform === 'win32' && fs.existsSync(enginePath()); } catch { return false; }
}
function startEngine() {
  if (engine || !engineAvailable()) return;
  engine = spawn(enginePath(), [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const rl = readline.createInterface({ input: engine.stdout });
  rl.on('line', line => { try { send('engine:event', JSON.parse(line)); } catch { /* ignore non-JSON */ } });
  engine.stderr.on('data', d => send('engine:event', { ev: 'log', msg: String(d).slice(0, 500) }));
  engine.on('exit', code => {
    engine = null;
    send('engine:event', { ev: 'engineExit', code });
  });
  for (const m of engineQueue) engine.stdin.write(JSON.stringify(m) + '\n');
  engineQueue = [];
}
ipcMain.on('engine:send', (_e, msg) => {
  if (!engineAvailable()) { send('engine:event', { ev: 'error', msg: 'The calling engine is not installed.' }); return; }
  if (!engine) { engineQueue.push(msg); startEngine(); return; }
  try { engine.stdin.write(JSON.stringify(msg) + '\n'); } catch (err) { send('engine:event', { ev: 'error', msg: String(err) }); }
});
ipcMain.handle('engine:available', () => engineAvailable());

/* ---------------- window / OS helpers ---------------- */
ipcMain.on('wego:attention', () => {
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.flashFrame(true);
  win.setAlwaysOnTop(true);
  win.focus();
  setTimeout(() => win && win.setAlwaysOnTop(false), 1500);
});
ipcMain.on('wego:focus', () => showWindow());
ipcMain.on('wego:showItem', (_e, p) => { if (typeof p === 'string' && fs.existsSync(p)) shell.showItemInFolder(p); });
ipcMain.on('wego:openRecordings', () => {
  const dir = path.join(app.getPath('documents'), 'Wego Recordings');
  fs.mkdirSync(dir, { recursive: true });
  shell.openPath(dir);
});
// Choose hold music: copied into Wego's own folder so it keeps working if the original moves.
ipcMain.handle('wego:pickAudio', async () => {
  const r = await dialog.showOpenDialog(win, {
    title: 'Choose hold music',
    properties: ['openFile'],
    filters: [{ name: 'Audio', extensions: ['mp3', 'wav', 'm4a', 'aac', 'wma'] }]
  });
  if (r.canceled || !r.filePaths[0]) return null;
  const src = r.filePaths[0];
  const dir = path.join(app.getPath('userData'), 'hold-music');
  fs.mkdirSync(dir, { recursive: true });
  const dest = path.join(dir, path.basename(src));
  try { fs.copyFileSync(src, dest); return dest; } catch { return src; }
});
ipcMain.handle('wego:getStartup', () => app.getLoginItemSettings().openAtLogin);
ipcMain.on('wego:setStartup', (_e, on) => app.setLoginItemSettings({ openAtLogin: !!on, args: ['--hidden'] }));
ipcMain.handle('wego:version', () => app.getVersion());

/* ---------------- global hotkeys ---------------- */
const HOTKEYS = { 'Control+Alt+A': 'answer', 'Control+Alt+H': 'hangup', 'Control+Alt+M': 'mute', 'Control+Alt+W': 'show' };
function registerHotkeys() {
  for (const [acc, action] of Object.entries(HOTKEYS)) {
    try {
      globalShortcut.register(acc, () => {
        if (action === 'show') { showWindow(); return; }
        send('wego:hotkey', action);
      });
    } catch { /* key already in use by another app */ }
  }
}

/* ---------------- auto-update ---------------- */
function setupUpdates() {
  if (!app.isPackaged) return;
  let autoUpdater;
  try { ({ autoUpdater } = require('electron-updater')); } catch { return; }
  autoUpdater.autoDownload = true;
  autoUpdater.autoInstallOnAppQuit = true;
  autoUpdater.on('update-available', info => send('wego:update', { state: 'downloading', version: info.version }));
  autoUpdater.on('update-downloaded', info => send('wego:update', { state: 'ready', version: info.version }));
  autoUpdater.on('error', () => { /* offline or no release yet: try again later */ });
  const check = () => autoUpdater.checkForUpdates().catch(() => {});
  setTimeout(check, 10000);
  setInterval(check, 2 * 60 * 60 * 1000);
  ipcMain.on('wego:installUpdate', () => { quitting = true; autoUpdater.quitAndInstall(); });
}

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);

  // Allow microphone (for calls) and notifications without extra prompts.
  const allowed = new Set(['media', 'notifications', 'audioCapture', 'clipboard-sanitized-write']);
  session.defaultSession.setPermissionRequestHandler((_wc, permission, callback) => callback(allowed.has(permission)));
  session.defaultSession.setPermissionCheckHandler((_wc, permission) => allowed.has(permission));

  createWindow();
  createTray();
  registerHotkeys();
  setupUpdates();
});

app.on('before-quit', () => {
  quitting = true;
  if (engine) { try { engine.stdin.write(JSON.stringify({ cmd: 'quit' }) + '\n'); } catch {} }
});
app.on('will-quit', () => globalShortcut.unregisterAll());
app.on('window-all-closed', () => { if (quitting) app.quit(); });
