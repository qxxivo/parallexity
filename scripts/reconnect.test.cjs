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
  replaceChildren(...items) { this.items=items; } querySelectorAll() {return this.items||[];} setAttribute() {} append() {} remove() {}
}
function harness(preferences = {}, storageUnavailable = false) {
  const nodes = new Map(), timers = new Map(); let sequence = 0;
  const node = id => { if (!nodes.has(id)) nodes.set(id, new Element()); return nodes.get(id); };
  const document = new Element(); document.hidden = false;
  const panels = ['media-panel','sound-panel','input-panel'].map(id => { const panel = node(id); panel.id = id; return panel; });
  const tabs = panels.map(panel => { const tab = node('tab-' + panel.id); tab.dataset.panel = panel.id; return tab; });
  document.getElementById = node; document.querySelectorAll = selector => selector === '.tab' ? tabs : selector === '.tab-panel' ? panels : []; document.createElement = () => new Element();
  const window = new Element(); window.matchMedia = () => ({ matches: false }); window.scrollTo = () => {};
  const location = { hash: '', pathname: '/', origin: 'http://192.168.1.4:8765', assign: url => { location.assigned = url; } };
  const requests = [];
  const storage = new Map(Object.entries(preferences));
  const env = { document, window, location, navigator: { userAgent: 'Android' }, history: { replaceState() {} }, localStorage: { getItem(key) { if(storageUnavailable) throw new Error('Storage blocked'); return storage.get(key) ?? null; }, setItem(key,value) { if(storageUnavailable) throw new Error('Storage blocked'); storage.set(key,value); } }, URL, URLSearchParams, AbortController, performance, crypto: require('node:crypto').webcrypto,
    setTimeout(callback, delay) { const id = ++sequence; timers.set(id, { callback, delay }); return id; }, clearTimeout: id => timers.delete(id), setInterval() {},
    fetch(url, options) {
      requests.push({ url, options });
      if (env.mode === 'pending') return new Promise((resolve,reject) => options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true }));
      if (env.mode === 'offline') return Promise.reject(new TypeError('Network unavailable'));
      return Promise.resolve({ ok: env.mode !== 'unauthorized', status: env.mode === 'unauthorized' ? 401 : 200,
        json: async () => ({ computer: 'Test PC', media: [], apps: [], audio: { available: true, volume: 42, muted: false }, inputEnabled: false, ...env.state }) });
    }, mode: 'success' };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../web/app.js'), 'utf8'), env);
  return { env, node, timers, requests, document, window, location, storage, async tick(delay) {
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
  const remembered = harness({'parallexity:panel':'input-panel','parallexity:precise':'1'}); await settle();
  assert.equal(remembered.node('input-panel').classList.contains('hidden'), false, 'Last selected tab is restored');
  assert.equal(remembered.node('precise').checked, true, 'Precision preference is restored');
  remembered.node('tab-sound-panel').emit('click');
  assert.equal(remembered.storage.get('parallexity:panel'), 'sound-panel');
  remembered.node('precise').checked = false; remembered.node('precise').emit('change');
  assert.equal(remembered.storage.get('parallexity:precise'), '0');
  const invalid = harness({'parallexity:panel':'not-a-panel'}); await settle();
  assert.equal(invalid.node('remote').classList.contains('hidden'), false, 'Invalid preference cannot break connection');
  assert.equal(invalid.node('media-panel').classList.contains('hidden'), false, 'Invalid tab falls back to media');
  const blocked = harness({}, true); await settle();
  assert.equal(blocked.node('volume').disabled, false, 'Blocked browser storage does not prevent remote use');
  const extras = harness(); await settle();
  extras.env.state={browser:{connected:true,title:'Test video',actions:['pause','skip-intro','unknown']},timer:{remaining:61,action:'pause'}};
  await extras.tick(1500);
  assert.equal(extras.node('browser-card').classList.contains('hidden'),false);
  assert.equal(extras.node('browser-actions').items.length,2,'Unknown actions never become buttons');
  assert.equal(extras.node('timer-status').textContent,'Осталось 1:01');
  extras.node('timer-action').value='display-off';extras.node('timer-minutes').value='30';
  extras.node('timer-form').emit('submit',{preventDefault(){}});await settle();
  let command=extras.requests.findLast(request=>request.url==='/api/timer');
  assert.deepEqual(JSON.parse(command.options.body),{minutes:30,action:'display-off',session:null},'Screen timer cannot route to browser');
  extras.node('timer-action').value='browser-pause';extras.node('timer-form').emit('submit',{preventDefault(){}});await settle();
  command=extras.requests.findLast(request=>request.url==='/api/timer');assert.equal(JSON.parse(command.options.body).session,'browser');
  extras.node('browser-actions').items.find(button=>button.textContent==='Пропустить заставку').emit('click');await settle();
  assert.equal(JSON.parse(extras.requests.findLast(request=>request.url==='/api/browser').options.body).action,'skip-intro');
  extras.node('timer-cancel').emit('click');await settle();
  assert.equal(JSON.parse(extras.requests.findLast(request=>request.url==='/api/timer').options.body).action,'cancel');
  console.log('Passed reconnection, visibility, stale requests, revocation, pairing links, browser buttons and timer actions.');
})().catch(error => { console.error(error); process.exitCode = 1; });
