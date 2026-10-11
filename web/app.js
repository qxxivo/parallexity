(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  let state = null, selected = '', connected = false, pairing = false, polling = false;
  let editingVolume = false, volumeTimer = null, pendingVolume = null, volumeSending = false;
  let commandPending = false, textSending = false, toastTimer = null;
  const pairToken = new URLSearchParams(location.hash.slice(1)).get('pair');
  if (pairToken) history.replaceState(null, '', location.pathname);
  $('device-name').value = /iPhone|iPad/.test(navigator.userAgent) ? 'Мой iPhone' : /Android/.test(navigator.userAgent) ? 'Мой Android' : 'Мой телефон';

  function toast(text) { $('toast').textContent = text; $('toast').style.display = 'block'; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').style.display = 'none', 5000); }
  async function api(path, body) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), path === '/api/pair' ? 75000 : 7000);
    try {
      const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
      if (!response.ok) { const data = await response.json().catch(() => ({})); const error = new Error(data.error || 'Не удалось связаться с ПК.'); error.status = response.status; throw error; }
      return await response.json();
    } finally { clearTimeout(timer); }
  }
  function setConnection(ok, text) {
    connected = ok; $('connection').classList.toggle('online', ok); $('connection').querySelector('b').textContent = text;
  }
  function disableControls() {
    ['volume', 'mute', 'previous', 'play', 'next', 'session', 'send-text'].forEach(id => $(id).disabled = true);
    document.querySelectorAll('#app-mixer button,#app-mixer input,[data-input],[data-key],[data-scroll]').forEach(node => node.disabled = true);
    $('touchpad').classList.add('disabled');
  }
  function render() {
    if (!state) return;
    $('computer').textContent = state.computer;
    const media = state.media || [];
    if (!media.some(m => m.id === selected)) selected = media[0]?.id || '';
    const optionKey = media.map(m => m.id + ':' + m.app).join('|');
    if ($('session').dataset.key !== optionKey) {
      $('session').replaceChildren(...media.map(m => { const option = document.createElement('option'); option.value = m.id; option.textContent = m.app; return option; }));
      $('session').dataset.key = optionKey;
    }
    $('session').value = selected;
    $('session').classList.toggle('hidden', media.length < 2);
    $('session').disabled = !connected || commandPending;
    const current = media.find(m => m.id === selected);
    $('media-app').textContent = current?.app || 'Медиа';
    $('title').textContent = current?.title || (current ? 'Без названия' : 'Открой музыку или видео');
    $('artist').textContent = current?.artist || (current ? current.app : 'Плееры появятся здесь автоматически');
    $('play').textContent = current?.playing ? 'Ⅱ' : '▶';
    $('play').setAttribute('aria-label', current?.playing ? 'Пауза' : 'Воспроизвести');
    $('play').disabled = !connected || commandPending || !current || !(current.playing ? current.canPause : current.canPlay);
    $('next').disabled = !connected || commandPending || !current?.canNext;
    $('previous').disabled = !connected || commandPending || !current?.canPrevious;
    $('media-note').textContent = state.mediaError || 'Показываем плееры, которыми разрешает управлять Windows.';
    const audio = state.audio;
    if (!editingVolume && pendingVolume === null && !volumeSending) { $('volume').value = audio.volume; $('volume-number').textContent = audio.available ? audio.volume + '%' : '—'; }
    $('volume').disabled = !connected || !audio.available;
    $('mute').disabled = !connected || commandPending || !audio.available;
    $('mute').querySelector('span').textContent = audio.muted ? 'Включить звук' : 'Выключить звук';
    $('mute').setAttribute('aria-pressed', String(audio.muted));
    $('audio-note').textContent = audio.available ? (audio.muted ? 'Звук выключен' : 'Общий звук ПК') : 'Нет устройства вывода';
    renderMixer();
    const enabled = connected && state.inputEnabled;
    $('input-status').textContent = enabled ? 'Разрешено' : 'Выключено';
    $('input-note').textContent = enabled ? 'Управляешь активным окном ПК. Держи экран компьютера в поле зрения.' : 'Разреши мышь и клавиатуру в окне parallexity на ПК.';
    document.querySelectorAll('[data-input],[data-key],[data-scroll],#send-text').forEach(node => node.disabled = !enabled);
    $('send-text').disabled = !enabled || textSending;
    $('touchpad').classList.toggle('disabled', !enabled);
  }
  async function poll() {
    if (polling || pairing || document.hidden) return;
    polling = true;
    try {
      state = await api('/api/state');
      setConnection(true, 'Подключено');
      $('pairing').classList.add('hidden'); $('unpaired').classList.add('hidden'); $('remote').classList.remove('hidden'); $('reconnect').classList.add('hidden');
      render();
    } catch (error) {
      setConnection(false, error.status === 401 ? 'Нужно подключение' : 'Нет связи'); disableControls();
      if (error.status === 401) {
        $('remote').classList.add('hidden');
        $(pairToken ? 'pairing' : 'unpaired').classList.remove('hidden');
      } else if (state) { $('reconnect').classList.remove('hidden'); }
      else { $('unpaired').classList.remove('hidden'); }
    } finally { polling = false; }
  }
  $('pair-form').addEventListener('submit', async event => {
    event.preventDefault(); if (pairing || !pairToken) return;
    pairing = true; $('pair-button').disabled = true; $('pair-message').textContent = 'Ждём подтверждения на компьютере…';
    try { await api('/api/pair', { token: pairToken, name: $('device-name').value.trim() || 'Телефон' }); $('pair-message').textContent = ''; }
    catch (error) { $('pair-message').textContent = error.message; }
    finally { pairing = false; $('pair-button').disabled = false; await poll(); }
  });
  async function command(action, extra = {}) {
    if (!connected || commandPending) return;
    commandPending = true; render();
    try { await api('/api/command', { action, session: selected, ...extra }); }
    catch (error) { toast(error.message); }
    finally { commandPending = false; await poll(); render(); }
  }
  $('play').addEventListener('click', () => command(state.media.find(m => m.id === selected)?.playing ? 'pause' : 'play'));
  $('next').addEventListener('click', () => command('next'));
  $('previous').addEventListener('click', () => command('previous'));
  $('mute').addEventListener('click', () => command('mute'));
  $('session').addEventListener('change', () => { selected = $('session').value; render(); });
  async function sendVolume() {
    if (volumeSending || pendingVolume === null || !connected) return;
    volumeSending = true;
    try {
      while (pendingVolume !== null && connected) {
        const value = pendingVolume; pendingVolume = null;
        await api('/api/command', { action: 'volume', volume: value });
      }
    } catch (error) { pendingVolume = null; toast(error.message); }
    finally { volumeSending = false; if (!editingVolume) await poll(); }
  }
  $('volume').addEventListener('input', () => {
    editingVolume = true; pendingVolume = Number($('volume').value); $('volume-number').textContent = pendingVolume + '%';
    clearTimeout(volumeTimer); volumeTimer = setTimeout(sendVolume, 120);
  });
  function finishVolume() { editingVolume = false; clearTimeout(volumeTimer); void sendVolume(); }
  $('volume').addEventListener('change', finishVolume);
  $('volume').addEventListener('pointercancel', finishVolume);
  $('volume').addEventListener('blur', finishVolume);
  $('retry').addEventListener('click', poll); $('reconnect-button').addEventListener('click', poll);
  const mixerRows = new Map();
  function renderMixer() {
    const apps = state.apps || [];
    $('mixer-empty').classList.toggle('hidden', apps.length > 0);
    for (const [id, row] of mixerRows) if (!apps.some(app => app.id === id)) { row.root.remove(); mixerRows.delete(id); }
    for (const app of apps) {
      let row = mixerRows.get(app.id);
      if (!row) {
        const root = document.createElement('div'); root.className = 'mixer-row';
        const heading = document.createElement('div'); heading.className = 'section-heading';
        const name = document.createElement('strong'), number = document.createElement('span'); number.className = 'hint';
        heading.append(name, number);
        const slider = document.createElement('input'); slider.type = 'range'; slider.min = 0; slider.max = 100;
        const mute = document.createElement('button'); mute.className = 'secondary';
        root.append(heading, slider, mute); $('app-mixer').append(root);
        row = { root, name, number, slider, mute, editing:false, pending:null, sending:false, timer:null };
        mixerRows.set(app.id, row);
        async function send() {
          if (row.sending || row.pending === null || !connected) return;
          row.sending = true;
          try { while (row.pending !== null && connected) { const volume = row.pending; row.pending = null; await api('/api/command', {action:'app-volume', app:app.id, volume}); } }
          catch (error) { row.pending = null; toast(error.message); }
          finally { row.sending = false; if (!row.editing) void poll(); }
        }
        slider.addEventListener('input', () => { row.editing = true; row.pending = Number(slider.value); number.textContent = row.pending + '%'; clearTimeout(row.timer); row.timer = setTimeout(send,120); });
        const finish = () => { row.editing = false; clearTimeout(row.timer); void send(); };
        slider.addEventListener('change',finish); slider.addEventListener('pointercancel',finish); slider.addEventListener('blur',finish);
        mute.addEventListener('click', () => command('app-mute',{app:app.id}));
      }
      row.name.textContent = app.name;
      row.slider.setAttribute('aria-label', 'Громкость ' + app.name);
      if (!row.editing && !row.sending && row.pending === null) { row.slider.value = app.volume; row.number.textContent = app.volume + '%'; }
      row.slider.disabled = !connected; row.mute.disabled = !connected || commandPending;
      row.mute.textContent = app.muted ? 'Включить звук' : 'Выключить звук'; row.mute.setAttribute('aria-pressed',String(app.muted));
    }
  }
  document.querySelectorAll('.tab').forEach(button => button.addEventListener('click', () => {
    document.querySelectorAll('.tab-panel').forEach(panel => panel.classList.toggle('hidden', panel.id !== button.dataset.panel));
    document.querySelectorAll('.tab').forEach(tab => { tab.classList.toggle('active',tab === button); tab.setAttribute('aria-pressed',String(tab === button)); });
  }));
  let inputChain = Promise.resolve(), inputQueued = 0, motionBusy = false, dx = 0, dy = 0, wheel = 0;
  const inputAllowed = () => connected && state?.inputEnabled && !document.hidden;
  function sendInput(body) {
    if (!inputAllowed() || inputQueued >= 5) return Promise.resolve(false);
    inputQueued++;
    const request = inputChain.then(async () => {
      if (!inputAllowed()) return false;
      await api('/api/command',body); return true;
    }).catch(error => { dx = dy = wheel = 0; toast(error.message); return false; });
    inputChain = request.finally(() => inputQueued--);
    return inputChain;
  }
  setInterval(async () => {
    if (!inputAllowed()) { dx = dy = wheel = 0; return; }
    if (motionBusy) return;
    const x = Math.trunc(dx), y = Math.trunc(dy), scroll = Math.trunc(wheel);
    if (!x && !y && !scroll) return;
    dx -= x; dy -= y; wheel -= scroll; motionBusy = true;
    try {
      if (scroll) await sendInput({action:'scroll',dy:Math.max(-1200,Math.min(1200,scroll))});
      if (x || y) await sendInput({action:'pointer',dx:Math.max(-200,Math.min(200,x)),dy:Math.max(-200,Math.min(200,y))});
    } finally { motionBusy = false; }
  },40);
  document.querySelectorAll('[data-input]').forEach(button => button.addEventListener('click',() => sendInput({action:button.dataset.input})));
  document.querySelectorAll('[data-key]').forEach(button => button.addEventListener('click',() => sendInput({action:'key',key:button.dataset.key})));
  document.querySelectorAll('[data-scroll]').forEach(button => button.addEventListener('click',() => sendInput({action:'scroll',dy:Number(button.dataset.scroll)})));
  $('send-text').addEventListener('click',async () => {
    if (textSending) return;
    const text = $('remote-text').value;
    if (!text.length) return toast('Сначала напиши текст.');
    textSending = true;
    $('send-text').disabled = true;
    const done = await sendInput({action:'text',text});
    if (done) { if ($('remote-text').value === text) $('remote-text').value = ''; toast('Текст введён на ПК.'); }
    textSending = false;
    $('send-text').disabled = !inputAllowed();
  });
  const pointers = new Map(); let gestureTime = 0, gestureDistance = 0, hadMultiple = false;
  const pad = $('touchpad');
  pad.addEventListener('contextmenu',event => event.preventDefault());
  pad.addEventListener('pointerdown',event => {
    if (!inputAllowed()) return;
    event.preventDefault(); pad.setPointerCapture(event.pointerId);
    if (!pointers.size) { gestureTime = performance.now(); gestureDistance = 0; hadMultiple = false; }
    pointers.set(event.pointerId,{x:event.clientX,y:event.clientY});
    if (pointers.size > 1) hadMultiple = true;
  });
  pad.addEventListener('pointermove',event => {
    const old = pointers.get(event.pointerId); if (!old || !inputAllowed()) return;
    const x = event.clientX - old.x, y = event.clientY - old.y;
    pointers.set(event.pointerId,{x:event.clientX,y:event.clientY}); gestureDistance += Math.hypot(x,y);
    if (pointers.size > 1) wheel = Math.max(-1200,Math.min(1200,wheel + y * -3));
    else if (!hadMultiple) { const gain = $('precise').checked ? .35 : 1.2; dx = Math.max(-200,Math.min(200,dx + x * gain)); dy = Math.max(-200,Math.min(200,dy + y * gain)); }
  });
  function endGesture(event) {
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId);
    if (!pointers.size && !hadMultiple && gestureDistance < 8 && performance.now() - gestureTime < 250 && event.type === 'pointerup') void sendInput({action:'click'});
  }
  pad.addEventListener('pointerup',endGesture); pad.addEventListener('pointercancel',endGesture); pad.addEventListener('lostpointercapture',event => pointers.delete(event.pointerId));
  document.addEventListener('visibilitychange', () => { dx = dy = wheel = 0; pointers.clear(); if (!document.hidden) void poll(); });
  window.addEventListener('online', poll);
  setInterval(poll, 1500); void poll();
})();
