// Wego Team Server
// Relays live team events (who is on a call, call answered/ended, team chat)
// between Wego apps on different computers. No packages needed: just Node.js 18+.
//
// Start it:   node wego-server.js
// Optional:   PORT=8787 node wego-server.js
// Then in Wego > Settings > Team server, enter:  ws://<this-computer-ip>:8787

const http = require('http');
const crypto = require('crypto');
const os = require('os');

const PORT = +process.env.PORT || 8787;
const HISTORY_MAX = 300;          // chat messages kept for people who join later
const clients = new Set();
const history = [];               // recent chat messages
const presence = new Map();       // agentId -> last presence message

const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/plain' });
  res.end(`Wego Team Server is running. Connected apps: ${clients.size}\n`);
});

server.on('upgrade', (req, socket) => {
  const key = req.headers['sec-websocket-key'];
  if (!key || (req.headers.upgrade || '').toLowerCase() !== 'websocket') { socket.destroy(); return; }
  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
               `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
  socket.setNoDelay(true);
  const client = { socket, buf: Buffer.alloc(0), agent: null, frags: [] };
  clients.add(client);

  // send the newcomer recent chat and the current presence of everyone
  send(client, { type: 'history', msgs: history });
  for (const p of presence.values()) send(client, p);

  socket.on('data', chunk => { client.buf = Buffer.concat([client.buf, chunk]); readFrames(client); });
  socket.on('close', () => drop(client));
  socket.on('error', () => drop(client));
});

function drop(client) {
  if (!clients.delete(client)) return;
  if (client.agent && ![...clients].some(c => c.agent === client.agent)) {
    const bye = { type: 'presence', id: crypto.randomUUID(), from: client.agent, fromName: client.agentName,
                  state: 'offline', ts: Date.now() };
    presence.set(client.agent, bye);
    broadcast(bye, null);
  }
  log(`app disconnected (${clients.size} online)`);
}

function readFrames(client) {
  while (client.buf.length >= 2) {
    const b0 = client.buf[0], b1 = client.buf[1];
    const fin = (b0 & 0x80) !== 0, op = b0 & 0x0f, masked = (b1 & 0x80) !== 0;
    let len = b1 & 0x7f, off = 2;
    if (len === 126) { if (client.buf.length < 4) return; len = client.buf.readUInt16BE(2); off = 4; }
    else if (len === 127) { if (client.buf.length < 10) return; len = Number(client.buf.readBigUInt64BE(2)); off = 10; }
    if (len > 1_000_000) { client.socket.destroy(); return; }
    const need = off + (masked ? 4 : 0) + len;
    if (client.buf.length < need) return;
    let payload = client.buf.subarray(off + (masked ? 4 : 0), need);
    if (masked) {
      const mask = client.buf.subarray(off, off + 4);
      payload = Buffer.from(payload.map((v, i) => v ^ mask[i % 4]));
    }
    client.buf = client.buf.subarray(need);

    if (op === 0x8) { client.socket.end(); return; }                 // close
    if (op === 0x9) { writeFrame(client.socket, payload, 0xA); continue; } // ping -> pong
    if (op === 0xA) continue;                                         // pong
    if (op === 0x1 || op === 0x0) {
      client.frags.push(payload);
      if (!fin) continue;
      const text = Buffer.concat(client.frags).toString('utf8'); client.frags = [];
      onMessage(client, text);
    }
  }
}

function onMessage(client, text) {
  let msg; try { msg = JSON.parse(text); } catch { return; }
  if (!msg || typeof msg.type !== 'string') return;
  if (msg.from) { client.agent = String(msg.from); client.agentName = String(msg.fromName || msg.from); }
  if (msg.type === 'chat') {
    history.push(msg); if (history.length > HISTORY_MAX) history.shift();
    log(`chat ${msg.fromName}: ${String(msg.text).slice(0, 60)}`);
  }
  if (msg.type === 'presence') presence.set(client.agent, msg);
  if (msg.type === 'answered' || msg.type === 'ended') log(`${msg.fromName} ${msg.type} ${msg.number || ''}`);
  broadcast(msg, client);
}

function broadcast(msg, except) { for (const c of clients) if (c !== except) send(c, msg); }
function send(client, obj) { try { writeFrame(client.socket, Buffer.from(JSON.stringify(obj)), 0x1); } catch {} }
function writeFrame(socket, data, op) {
  const len = data.length;
  let head;
  if (len < 126) head = Buffer.from([0x80 | op, len]);
  else if (len < 65536) { head = Buffer.alloc(4); head[0] = 0x80 | op; head[1] = 126; head.writeUInt16BE(len, 2); }
  else { head = Buffer.alloc(10); head[0] = 0x80 | op; head[1] = 127; head.writeBigUInt64BE(BigInt(len), 2); }
  socket.write(Buffer.concat([head, data]));
}
function log(s) { console.log(new Date().toLocaleTimeString(), s); }

// keep connections alive through routers
setInterval(() => { for (const c of clients) try { writeFrame(c.socket, Buffer.alloc(0), 0x9); } catch {} }, 25000);

server.listen(PORT, '0.0.0.0', () => {
  const ips = Object.values(os.networkInterfaces()).flat().filter(i => i && i.family === 'IPv4' && !i.internal).map(i => i.address);
  console.log(`\nWego Team Server running on port ${PORT}`);
  console.log('In Wego > Settings > Team server, enter one of:');
  for (const ip of ips) console.log(`   ws://${ip}:${PORT}`);
  console.log(`   ws://localhost:${PORT}   (only if Wego runs on this same computer)\n`);
});
