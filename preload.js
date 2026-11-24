// Small, safe bridge between the Wego page and the Windows app.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('wegoDesktop', {
  platform: process.platform,
  attention: () => ipcRenderer.send('wego:attention'), // bring Wego to the front (incoming call)
  focus: () => ipcRenderer.send('wego:focus'),
  showItem: p => ipcRenderer.send('wego:showItem', p),
  openRecordings: () => ipcRenderer.send('wego:openRecordings'),
  pickAudio: () => ipcRenderer.invoke('wego:pickAudio'),
  getStartup: () => ipcRenderer.invoke('wego:getStartup'),
  setStartup: on => ipcRenderer.send('wego:setStartup', !!on),
  version: () => ipcRenderer.invoke('wego:version'),
  onHotkey: cb => ipcRenderer.on('wego:hotkey', (_e, a) => cb(a)),
  onUpdate: cb => ipcRenderer.on('wego:update', (_e, u) => cb(u)),
  installUpdate: () => ipcRenderer.send('wego:installUpdate'),
  engine: {
    available: () => ipcRenderer.invoke('engine:available'),
    send: msg => ipcRenderer.send('engine:send', msg),
    on: cb => ipcRenderer.on('engine:event', (_e, ev) => cb(ev))
  }
});
