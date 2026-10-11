const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Element {
  constructor() { this.listeners = {}; this.children = new Map(); this.dataset = {}; this.value = '0'; this.disabled = false;
    const classes = new Set(['hidden']);
    this.classList = { add: x => classes.add(x), remove: x => classes.delete(x), contains: x => classes.has(x), toggle: (x,on) => { if (on ?? !classes.has(x)) classes.add(x); else classes.delete(x); } };
    this.style = { setProperty() {} };
  }
  addEventListener(name, callback) { (this.listeners[name] ??= []).push(callback); }
  emit(name, event = {}) { for (const callback of this.listeners[name] ?? []) callback(event); }
  querySelector(key) { if (!this.children.has(key)) this.children.set(key, new Element()); return this.children.get(key); }
  replaceChildren() {} setAttribute() {} append() {} remove() {}
}
function harness() {
  const nodes = new Map(), timers = new Map(); let sequence = 0;
  const node = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  const document = new Element(); document.hidden = false;
  document.getElementById = node; document.querySelectorAll = () => []; document.createElement = () => new Element();
  const window = new Element(); window.matchMedia = () => ({ matches: false }); window.scrollTo = () => {};
  const location = { hash: '', pathname: '/', origin: 'http://192.168.1.4:8765', assign: url => { location.assigned = url; } };
  const requests = [];
  const env = { document, window, location, navigator: { userAgent: 'Android' }, history: { replaceState() {} }, URL, URLSearchParams, AbortController, performance, crypto: require('node:crypto').webcrypto,
    setTimeout(callback, delay) { const id = ++sequence; timers.set(id, { callback, delay }); return id; }, clearTimeout: id => timers.delete(id), setInterval() {},
    fetch(url, options) {
      requests.push({ url, options });
      if (env.mode === 'pending') return new Promise((resolve,reject) => options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true }));
      if (env.mode === 'offline') return Promise.reject(new TypeError('Network unavailable'));
      return Promise.resolve({ ok: env.mode !== 'unauthorized', status: env.mode === 'unauthorized' ? 401 : 200,
        json: async () => ({ computer: 'Test PC', media: [], apps: [], audio: { available: true, volume: 42, muted: false }, inputEnabled: false }) });
    }, mode: 'success' };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../web/app.js'), 'utf8'), env);
  return { env, node, timers, requests, document, window, location, async tick(delay) {
    const entry = [...timers].find(([,timer]) => timer.delay === delay); assert.ok(entry, `Missing timer ${delay}`);
    timers.delete(entry[0]); entry[1].callback(); await settle();
  } };
}
async function settle() { for (let i = 0; i < 20; i++) await Promise.resolve(); }
(async () => {
  const h = harness(); await settle();
  assert.equal(h.node('connection').querySelector('b').textContent, 'Подключено');
  assert.equal(h.node('volume').disabled, false);
  h.env.mode = 'offline'; await h.tick(1500);
  assert.equal(h.node('volume').disabled, true);
  assert.equal(h.node('reconnect').classList.contains('hidden'), false);
  assert.equal(h.node('unpaired').classList.contains('hidden'), true, 'Network loss must not ask for pairing');
  for (const delay of [3000,6000,12000,15000]) await h.tick(delay);
  assert.ok([...h.timers.values()].some(t => t.delay === 15000), 'Retries have a bounded backoff');
  h.env.mode = 'success'; h.window.emit('online'); await settle();
  assert.equal(h.node('volume').disabled, false);
  assert.equal(h.node('reconnect').classList.contains('hidden'), true);
  h.env.mode = 'pending'; await h.tick(1500);
  h.document.hidden = true; h.document.emit('visibilitychange'); await settle();
  assert.equal(h.requests.at(-1).options.signal.aborted, true, 'Hidden page cancels pending state request');
  assert.equal(h.timers.size, 0, 'Hidden page stops polling');
  h.env.mode = 'success'; h.document.hidden = false; h.document.emit('visibilitychange'); await settle();
  assert.equal(h.node('connection').querySelector('b').textContent, 'Подключено', 'Visible page reconnects immediately');
  h.env.mode = 'pending'; await h.tick(1500);
  h.env.mode = 'success'; h.window.emit('pageshow'); await settle();
  await h.tick(0);
  assert.equal(h.node('connection').querySelector('b').textContent, 'Подключено', 'Restored page replaces stale in-flight request');
  h.env.mode = 'unauthorized'; await h.tick(1500);
  assert.equal(h.node('unpaired').classList.contains('hidden'), false, 'Revoked device must pair again');
  assert.equal(h.node('reconnect').classList.contains('hidden'), true);
  h.node('home-pair-link').value = 'https://other.invalid/#pair=test';
  h.node('home-pair-form').emit('submit', { preventDefault() {} });
  assert.equal(h.location.assigned, undefined, 'Pairing link rejects another origin');
  h.node('home-pair-link').value = h.location.origin + '/#pair=test-token';
  h.node('home-pair-form').emit('submit', { preventDefault() {} });
  assert.equal(h.location.assigned, '/?connect#pair=test-token');
  assert.ok(h.requests.every(r => r.url === '/api/state'), 'Reconnection never replays control commands');
  console.log('Passed reconnection, visibility, stale-request, revocation and pairing-link scenarios.');
})().catch(error => { console.error(error); process.exitCode = 1; });
