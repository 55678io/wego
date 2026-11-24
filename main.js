// Wego desktop app (Windows) – Electron main process
const { app, BrowserWindow, session, shell, ipcMain, Menu } = require('electron');
const path = require('path');

app.setAppUserModelId('com.kdn.wego'); // needed for Windows notifications

let win = null;

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
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      backgroundThrottling: false // keep ringing and timers accurate when minimised
    }
  });

  win.loadFile(path.join(__dirname, 'app', 'index.html'));

  // Links to other websites open in the normal browser, not inside Wego.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (e, url) => {
    if (!url.startsWith('file:')) { e.preventDefault(); shell.openExternal(url); }
  });

  win.on('focus', () => win.flashFrame(false));
  win.on('closed', () => { win = null; });
}

// The page can ask for attention on an incoming call.
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

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);

  // Allow microphone (for calls) and notifications without extra prompts.
  const allowed = new Set(['media', 'notifications', 'audioCapture', 'clipboard-sanitized-write']);
  session.defaultSession.setPermissionRequestHandler((_wc, permission, callback) => callback(allowed.has(permission)));
  session.defaultSession.setPermissionCheckHandler((_wc, permission) => allowed.has(permission));

  createWindow();
});

app.on('window-all-closed', () => app.quit());
