// Small, safe bridge between the Wego page and the Windows app.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('wegoDesktop', {
  attention: () => ipcRenderer.send('wego:attention'), // bring Wego to the front (incoming call)
  focus: () => ipcRenderer.send('wego:focus'),
  platform: process.platform
});
