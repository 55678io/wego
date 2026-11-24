// CI smoke test: start the real engine on the Windows runner and check that it answers.
const { spawn } = require('child_process');
const path = require('path');
const exe = path.join(__dirname, '..', 'engine', 'publish', 'wego-engine.exe');
const p = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
const seen = {}; const lines = [];
let buf = '';
p.stdout.on('data', d => {
  buf += d; let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const l = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
    if (!l) continue; lines.push(l);
    try { const e = JSON.parse(l); seen[e.ev] = (seen[e.ev] || 0) + 1; } catch { seen.badjson = (seen.badjson || 0) + 1; }
  }
});
p.stderr.on('data', d => lines.push('STDERR ' + String(d).slice(0, 300)));
const send = m => p.stdin.write(JSON.stringify(m) + '\n');
setTimeout(() => send({ cmd: 'devices' }), 1500);
setTimeout(() => send({ cmd: 'setAudio', agc: true, micGain: 1.2, spkGain: 1 }), 1800);
setTimeout(() => send({ cmd: 'register', acc: 'a1', user: '1001', pass: 'x', domain: '127.0.0.1', transport: 'udp', srtp: true }), 2500);
setTimeout(() => send({ cmd: 'register', acc: 'a2', user: '2001', pass: 'x', domain: '127.0.0.1', transport: 'tcp' }), 2700);
setTimeout(() => send({ cmd: 'forward', mode: 'busy', target: '1005' }), 3000);
setTimeout(() => send({ cmd: 'unregister' }), 20000);
setTimeout(() => send({ cmd: 'quit' }), 21000);
const done = code => {
  const short = lines.map(l => l.slice(0, 160)).join(' | ').replace(/[\r\n]/g, ' ').slice(0, 3500);
  console.log('::notice::Engine events: ' + JSON.stringify(seen) + ' exit=' + code);
  console.log('::notice::Engine output: ' + short);
  const ok = seen.ready && seen.devices && seen.reg && seen.hold && !seen.badjson;
  if (!ok) { console.log('::error::Engine smoke test failed'); process.exit(1); }
  process.exit(0);
};
p.on('exit', code => done(code));
setTimeout(() => { try { p.kill(); } catch {} }, 30000);
