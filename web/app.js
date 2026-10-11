(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  function preference(key, value) {
    try {
      if (value !== undefined) localStorage.setItem('parallexity:' + key, value);
      return localStorage.getItem('parallexity:' + key);
    } catch { return null; }
  }
  let state = null, selected = '', connected = false, pairing = false, polling = false;
  let editingVolume = false, volumeTimer = null, pendingVolume = null, volumeSending = false;
  let commandPending = false, textSending = false, toastTimer = null;
  let pollTimer = null, pollFailures = 0, pollAbort = null, wakeRequested = false;
  const pairToken = new URLSearchParams(location.hash.slice(1)).get('pair');
  if (pairToken) history.replaceState(null, '', location.pathname);
  $('device-name').value = /iPhone|iPad/.test(navigator.userAgent) ? 'Мой iPhone' : /Android/.test(navigator.userAgent) ? 'Мой Android' : 'Мой телефон';

  function paintRange(node) { node.style.setProperty('--fill', node.value + '%'); }
  function toast(text) { $('toast').textContent = text; $('toast').style.display = 'block'; clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').style.display = 'none', 5000); }
  async function api(path, body, signal) {
    const controller = new AbortController();
    const abort = () => controller.abort();
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) controller.abort();
    const timer = setTimeout(() => controller.abort(), path === '/api/pair' ? 75000 : 7000);
    try {
      const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', cache: 'no-store', signal: controller.signal,
        headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
      if (!response.ok) { const data = await response.json().catch(() => ({})); const error = new Error(data.error || 'Не удалось связаться с ПК.'); error.status = response.status; throw error; }
      return await response.json();
    } finally { clearTimeout(timer); signal?.removeEventListener('abort', abort); }
  }
  function setConnection(ok, text) {
    connected = ok; $('connection').classList.toggle('online', ok); $('connection').querySelector('b').textContent = text;
  }
  function disableControls() {
    pendingVolume = null; editingVolume = false; clearTimeout(volumeTimer);
    for (const row of mixerRows.values()) { row.pending = null; row.editing = false; clearTimeout(row.timer); }
    ['volume', 'mute', 'previous', 'play', 'next', 'session', 'send-text', 'display-off'].forEach(id => $(id).disabled = true);
    document.querySelectorAll('#app-mixer button,#app-mixer input,[data-input],[data-key],[data-scroll]').forEach(node => node.disabled = true);
    $('touchpad').classList.add('disabled');
    $('timer-start').disabled = true; $('timer-cancel').disabled = true;
    $('browser-actions').querySelectorAll?.('button').forEach(button => button.disabled = true);
    cancelGesture();
  }
  function render() {
    if (!state) return;
    $('computer').textContent = state.computer;
    renderExtras();
    $('display-off').disabled = !connected || commandPending;
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
    $('play').querySelector('use').setAttribute('href', current?.playing ? '#i-pause' : '#i-play');
    $('play').setAttribute('aria-label', current?.playing ? 'Пауза' : 'Воспроизвести');
    $('play').disabled = !connected || commandPending || !current || !(current.playing ? current.canPause : current.canPlay);
    $('next').disabled = !connected || commandPending || !current?.canNext;
    $('previous').disabled = !connected || commandPending || !current?.canPrevious;
    $('media-note').textContent = state.mediaError || '';
    const audio = state.audio;
    if (!editingVolume && pendingVolume === null && !volumeSending) { $('volume').value = audio.volume; $('volume-number').textContent = audio.available ? audio.volume + '%' : '—'; }
    $('volume').disabled = !connected || !audio.available;
    $('mute').disabled = !connected || commandPending || !audio.available;
    $('mute').querySelector('span').textContent = audio.muted ? 'Включить звук' : 'Выключить звук';
    $('mute').setAttribute('aria-pressed', String(audio.muted));
    $('audio-note').textContent = audio.available ? (audio.muted ? 'Звук выключен' : 'Общий звук ПК') : 'Нет устройства вывода';
    paintRange($('volume'));
    renderMixer();
    const enabled = connected && state.inputEnabled;
    $('input-status').textContent = enabled ? 'Разрешено' : 'Выключено';
    $('input-note').textContent = enabled ? 'Касание — клик. Держи экран ПК в поле зрения.' : 'Разреши мышь и клавиатуру в окне parallexity на ПК.';
    document.querySelectorAll('[data-input],[data-key],[data-scroll],#send-text').forEach(node => node.disabled = !enabled);
    $('send-text').disabled = !enabled || textSending;
    $('touchpad').classList.toggle('disabled', !enabled);
    if (!enabled && (pointers.size || drag)) cancelGesture();
  }
  async function poll() {
    if (polling || pairing || document.hidden) return;
    clearTimeout(pollTimer);
    pollAbort = new AbortController();
    polling = true;
    try {
      state = await api('/api/state', undefined, pollAbort.signal);
      pollFailures = 0;
      setConnection(true, 'Подключено');
      $('pairing').classList.add('hidden'); $('unpaired').classList.add('hidden'); $('remote').classList.remove('hidden'); $('reconnect').classList.add('hidden');
      render();
    } catch (error) {
      if (pollAbort.signal.aborted) return;
      pollFailures++;
      setConnection(false, error.status === 401 ? 'Нужно подключение' : 'Нет связи'); disableControls();
      if (error.status === 401) {
        $('reconnect').classList.add('hidden');
        $('remote').classList.add('hidden');
        $(pairToken ? 'pairing' : 'unpaired').classList.remove('hidden');
      } else {
        $('pairing').classList.add('hidden'); $('unpaired').classList.add('hidden');
        $('reconnect').classList.remove('hidden');
      }
    } finally {
      polling = false; pollAbort = null;
      if (!document.hidden) pollTimer = setTimeout(poll, wakeRequested ? 0 : Math.min(15000, 1500 * 2 ** Math.min(pollFailures, 4)));
      wakeRequested = false;
    }
  }
  const browserLabels = {'play':'Воспроизвести','pause':'Пауза','seek-back':'−10 сек','seek-forward':'+10 сек','skip-intro':'Пропустить заставку','skip-ad':'Пропустить рекламу','next-episode':'Следующая серия'};
  let extraPending = false;
  function renderExtras() {
    const browser = state.browser;
    $('browser-card').classList.toggle('hidden', !browser?.connected);
    $('browser-title').textContent = browser?.title || '';
    const actions = (browser?.actions || []).filter(action => Object.hasOwn(browserLabels, action));
    const key = actions.join('|');
    if ($('browser-actions').dataset.key !== key) {
      const buttons = actions.map(action => {
        const button = document.createElement('button'); button.className = 'secondary'; button.type = 'button'; button.textContent = browserLabels[action];
        button.addEventListener('click', () => extraCommand('/api/browser', {action})); return button;
      });
      $('browser-actions').replaceChildren(...buttons); $('browser-actions').dataset.key = key;
    }
    $('browser-actions').querySelectorAll?.('button').forEach(button => button.disabled = !connected || extraPending);
    const timer = state.timer;
    const remaining = timer?.remaining || 0;
    const text = remaining ? Math.ceil(remaining / 60) + ' мин · ' + (timer.action === 'pause' ? 'Пауза' : 'Экран') : 'Выключен';
    $('timer-summary').textContent = text;
    $('timer-status').textContent = remaining ? 'Осталось ' + Math.floor(remaining / 60) + ':' + String(remaining % 60).padStart(2, '0') : timer?.result || '';
    $('timer-cancel').classList.toggle('hidden', !remaining);
    $('timer-start').disabled = !connected || extraPending;
    $('timer-cancel').disabled = !connected || extraPending;
  }
  async function extraCommand(path, body) {
    if (!connected || extraPending) return;
    extraPending = true; renderExtras();
    try { await api(path, body); }
    catch (error) { toast(error.message); }
    finally { extraPending = false; await poll(); renderExtras(); }
  }
  $('timer-form').addEventListener('submit', event => {
    event.preventDefault();
    const choice = $('timer-action').value;
    const action = choice === 'browser-pause' ? 'pause' : choice;
    const session = action !== 'pause' ? null : choice === 'browser-pause' ? (state?.browser?.connected ? 'browser' : '') : selected;
    if (action === 'pause' && !session) return toast('Открой плеер на ПК или подключи вкладку браузера.');
    void extraCommand('/api/timer', {minutes:Number($('timer-minutes').value), action, session});
  });
  $('timer-cancel').addEventListener('click', () => extraCommand('/api/timer', {minutes:0,action:'cancel'}));
  function reconnectNow() {
    pollFailures = 0; clearTimeout(pollTimer);
    if (polling) { wakeRequested = true; pollAbort?.abort(); }
    else void poll();
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
    try { await api('/api/command', { action, session: selected, ...extra }); return true; }
    catch (error) { toast(error.message); return false; }
    finally { commandPending = false; await poll(); render(); }
  }
  $('play').addEventListener('click', () => command(state.media.find(m => m.id === selected)?.playing ? 'pause' : 'play'));
  $('next').addEventListener('click', () => command('next'));
  $('previous').addEventListener('click', () => command('previous'));
  $('mute').addEventListener('click', () => command('mute'));
  $('display-off').addEventListener('click', async () => {
    if (!connected || commandPending) return;
    if (await command('display-off')) toast('Команда выключения экрана отправлена.');
  });
  $('session').addEventListener('change', () => { selected = $('session').value; preference('player', selected); render(); });
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
    paintRange($('volume')); editingVolume = true; pendingVolume = Number($('volume').value); $('volume-number').textContent = pendingVolume + '%';
    clearTimeout(volumeTimer); volumeTimer = setTimeout(sendVolume, 120);
  });
  function finishVolume() { editingVolume = false; clearTimeout(volumeTimer); void sendVolume(); }
  $('volume').addEventListener('change', finishVolume);
  $('volume').addEventListener('pointercancel', finishVolume);
  $('volume').addEventListener('blur', finishVolume);
  $('retry').addEventListener('click', reconnectNow); $('reconnect-button').addEventListener('click', reconnectNow);
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
        slider.addEventListener('input', () => { paintRange(slider); row.editing = true; row.pending = Number(slider.value); number.textContent = row.pending + '%'; clearTimeout(row.timer); row.timer = setTimeout(send,120); });
        const finish = () => { row.editing = false; clearTimeout(row.timer); void send(); };
        slider.addEventListener('change',finish); slider.addEventListener('pointercancel',finish); slider.addEventListener('blur',finish);
        mute.addEventListener('click', () => command('app-mute',{app:app.id}));
      }
      row.name.textContent = app.name;
      row.slider.setAttribute('aria-label', 'Громкость ' + app.name);
      if (!row.editing && !row.sending && row.pending === null) { row.slider.value = app.volume; row.number.textContent = app.volume + '%'; }
      paintRange(row.slider);
      row.slider.disabled = !connected; row.mute.disabled = !connected || commandPending;
      row.mute.textContent = app.muted ? 'Включить звук' : 'Выключить звук'; row.mute.setAttribute('aria-pressed',String(app.muted));
    }
  }
  function selectPanel(id, remember = true) {
    if (!['media-panel', 'sound-panel', 'input-panel'].includes(id)) id = 'media-panel';
    cancelGesture();
    document.querySelectorAll('.tab-panel').forEach(panel => panel.classList.toggle('hidden', panel.id !== id));
    document.querySelectorAll('.tab').forEach(tab => { const active = tab.dataset.panel === id; tab.classList.toggle('active', active); tab.setAttribute('aria-pressed', String(active)); });
    if (remember) preference('panel', id);
    window.scrollTo({top:0,behavior:'auto'});
  }
  document.querySelectorAll('.tab').forEach(button => button.addEventListener('click', () => selectPanel(button.dataset.panel)));
  let inputChain = Promise.resolve(), inputQueued = 0, motionBusy = false, dx = 0, dy = 0, wheel = 0, inputEpoch = 0;
  let drag = null, holdTimer = null, lastDragMotion = 0;
  const pointers = new Map();
  let gestureTime = 0, gestureDistance = 0, hadMultiple = false, hadDrag = false;
  const pad = $('touchpad');
  const inputAllowed = () => connected && state?.inputEnabled && !document.hidden;
  function dragAppearance(active) {
    pad.classList.toggle('dragging',active);
    $('pad-label').textContent = active ? 'Перетаскиваешь' : 'Двигай пальцем';
    $('pad-hint').textContent = active ? 'Отпусти палец, чтобы отпустить мышь' : 'Зажми и двигай — перетаскивание';
  }
  function sendInput(body, release = false) {
    if (!release && (!inputAllowed() || inputQueued >= 5)) return Promise.resolve(false);
    const epoch = inputEpoch;
    inputQueued++;
    const request = inputChain.then(async () => {
      if (!release && (!inputAllowed() || epoch !== inputEpoch)) return false;
      await api('/api/command',body); return true;
    }).catch(error => { if (!release) cancelGesture(); toast(error.message); return false; });
    inputChain = request.finally(() => inputQueued--);
    return inputChain;
  }
  function releaseDrag() {
    const previous = drag; drag = null; dragAppearance(false);
    if (previous) void sendInput({action:'drag-end',drag:previous},true);
  }
  function cancelGesture() {
    clearTimeout(holdTimer); holdTimer = null;
    dx = dy = wheel = 0; pointers.clear(); inputEpoch++;
    releaseDrag();
  }
  async function flushMotion() {
    if (!inputAllowed() || motionBusy) return;
    const x = Math.trunc(dx), y = Math.trunc(dy), scroll = Math.trunc(wheel);
    const heartbeat = drag && performance.now() - lastDragMotion >= 150;
    if (!x && !y && !scroll && !heartbeat) return;
    dx -= x; dy -= y; wheel -= scroll; motionBusy = true;
    try {
      if (scroll) await sendInput({action:'scroll',dy:Math.max(-1200,Math.min(1200,scroll))});
      if (x || y || heartbeat) {
        const held = drag;
        if (held) lastDragMotion = performance.now();
        await sendInput({action:held ? 'drag-move' : 'pointer',drag:held,dx:Math.max(-200,Math.min(200,x)),dy:Math.max(-200,Math.min(200,y))});
      }
    } finally { motionBusy = false; }
  }
  setInterval(() => { if (!inputAllowed()) { if (pointers.size || drag) cancelGesture(); return; } void flushMotion(); },25);
  document.querySelectorAll('[data-input]').forEach(button => button.addEventListener('click',() => sendInput({action:button.dataset.input})));
  document.querySelectorAll('[data-key]').forEach(button => button.addEventListener('click',() => sendInput({action:'key',key:button.dataset.key})));
  document.querySelectorAll('[data-scroll]').forEach(button => button.addEventListener('click',() => sendInput({action:'scroll',dy:Number(button.dataset.scroll)})));
  $('send-text').addEventListener('click',async () => {
    if (textSending) return;
    const text = $('remote-text').value;
    if (!text.length) return toast('Сначала напиши текст.');
    textSending = true; $('send-text').disabled = true;
    const done = await sendInput({action:'text',text});
    if (done) { if ($('remote-text').value === text) $('remote-text').value = ''; toast('Текст введён на ПК.'); }
    textSending = false; $('send-text').disabled = !inputAllowed();
  });
  pad.addEventListener('contextmenu',event => event.preventDefault());
  pad.addEventListener('pointerdown',event => {
    if (!inputAllowed()) return;
    event.preventDefault(); pad.setPointerCapture(event.pointerId);
    if (!pointers.size) {
      gestureTime = performance.now(); gestureDistance = 0; hadMultiple = hadDrag = false;
      holdTimer = setTimeout(async () => {
        if (pointers.size !== 1 || gestureDistance >= 7 || hadMultiple || !inputAllowed()) return;
        const bytes = crypto.getRandomValues(new Uint8Array(16));
        const id = Array.from(bytes,value=>value.toString(16).padStart(2,'0')).join('');
        drag = id; hadDrag = true; lastDragMotion = performance.now(); dragAppearance(true);
        const done = await sendInput({action:'drag-start',drag:id});
        if (!done && drag === id) cancelGesture();
      },450);
    }
    pointers.set(event.pointerId,{x:event.clientX,y:event.clientY});
    if (pointers.size > 1) { hadMultiple = true; clearTimeout(holdTimer); releaseDrag(); dx = dy = 0; }
  });
  pad.addEventListener('pointermove',event => {
    const old = pointers.get(event.pointerId); if (!old || !inputAllowed()) return;
    const x = event.clientX - old.x, y = event.clientY - old.y;
    pointers.set(event.pointerId,{x:event.clientX,y:event.clientY}); gestureDistance += Math.hypot(x,y);
    if (gestureDistance >= 7) clearTimeout(holdTimer);
    if (pointers.size > 1) wheel = Math.max(-1200,Math.min(1200,wheel - y * 1.5));
    else if (!hadMultiple) {
      const gain = $('precise').checked ? .35 : 1.15;
      dx = Math.max(-200,Math.min(200,dx + x * gain)); dy = Math.max(-200,Math.min(200,dy + y * gain));
    }
  });
  function endGesture(event) {
    if (!pointers.has(event.pointerId)) return;
    pointers.delete(event.pointerId); clearTimeout(holdTimer);
    if (!pointers.size) {
      if (drag) {
        const held = drag, x = Math.trunc(dx), y = Math.trunc(dy);
        dx = dy = 0;
        if (x || y) void sendInput({action:'drag-move',drag:held,dx:x,dy:y});
        releaseDrag();
      } else if (!hadMultiple && !hadDrag && gestureDistance < 8 && performance.now() - gestureTime < 250 && event.type === 'pointerup') void sendInput({action:'click'});
    }
  }
  pad.addEventListener('pointerup',endGesture);
  pad.addEventListener('pointercancel',cancelGesture);
  pad.addEventListener('lostpointercapture',event => { if (pointers.has(event.pointerId)) cancelGesture(); });
  document.addEventListener('visibilitychange', () => { cancelGesture(); clearTimeout(pollTimer); if (document.hidden) { disableControls(); pollAbort?.abort(); } else reconnectNow(); });
  window.addEventListener('blur',cancelGesture);
  window.addEventListener('pagehide', () => { cancelGesture(); clearTimeout(pollTimer); pollAbort?.abort(); });
  window.addEventListener('pageshow', reconnectNow);
  window.addEventListener('focus', reconnectNow);
  window.addEventListener('online', reconnectNow);
  window.addEventListener('offline', () => { setConnection(false, 'Нет сети'); disableControls(); pollAbort?.abort(); $('reconnect').classList.remove('hidden'); });
  if (navigator.standalone || window.matchMedia('(display-mode: standalone)').matches) $('home-screen').classList.add('hidden');
  else $('home-instructions').textContent = /iPhone|iPad/.test(navigator.userAgent)
    ? 'В Safari открой «Поделиться» → «На экран Домой». Если есть переключатель «Открывать как веб-приложение», включи его. В отдельном окне может понадобиться подключить телефон заново: открой новый QR-код на ПК и вставь ссылку ниже.'
    : 'В меню браузера выбери «Добавить на главный экран» или «Установить приложение», если этот пункт доступен. В локальной сети браузер может создать обычный ярлык. Сохраняй пульт после подключения телефона. Если адрес ПК изменится, добавь ярлык заново.';
  $('home-pair-form').addEventListener('submit', event => {
    event.preventDefault();
    try {
      const url = new URL($('home-pair-link').value);
      const token = new URLSearchParams(url.hash.slice(1)).get('pair');
      if (url.origin !== location.origin || url.pathname !== '/' || !token || token.length > 64) throw new Error();
      location.assign('/?connect#pair=' + encodeURIComponent(token));
    } catch { toast('Скопируй новую ссылку из окна parallexity на ПК. Она должна относиться к этому компьютеру.'); }
  });
  selected = preference('player') || '';
  $('precise').checked = preference('precise') === '1';
  $('precise').addEventListener('change', () => preference('precise', $('precise').checked ? '1' : '0'));
  selectPanel(preference('panel') || 'media-panel', false);
  void poll();
})();
