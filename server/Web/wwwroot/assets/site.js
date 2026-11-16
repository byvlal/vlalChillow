/* ═══════════════════════════════════════════════════════════════════════════
   влалChillow — страница панели.

   Правила, которые держат страницу честной:
     • ни одна цифра не рисуется без ответа сервера (нет «демо-данных»);
     • любой 401 переводит на экран входа: сервер закрыл данные, и это не баг;
     • админка просит код и живёт отдельным ключом — выход с сайта её не включает;
     • тексты, пришедшие с сервера, всегда экранируются (чужой ник — чужой ввод).
   ═══════════════════════════════════════════════════════════════════════════ */
(() => {
  'use strict';

  /* ── состояние ────────────────────────────────────────────────────────── */
  const S = {
    me: null,             // {id, nick, since} или null
    admin: null,          // {admin:bool, adminId:bool, ...}
    status: null,
    rooms: [],
    modes: [],
    roomsMeta: {count: 0, maxRooms: 0, nextNumber: 1},
    filterMode: '',
    filterMap: '',
    page: 'home',
    authBusy: false,
  };

  const $ = id => document.getElementById(id);
  const el = (tag, cls, text) => {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  };

  /* ── сеть ─────────────────────────────────────────────────────────────── */

  /**
   * Запрос к панели. Заголовок X-Requested-With сервер требует для не-GET:
   * так чужая страница не сможет отправить запрос от твоего имени (CSRF).
   */
  async function api(method, path, body) {
    const opts = {
      method,
      credentials: 'same-origin',
      headers: { 'X-Requested-With': 'vlalchillow' },
    };
    if (body !== undefined) {
      opts.headers['Content-Type'] = 'application/json';
      opts.body = JSON.stringify(body);
    }

    let res;
    try {
      res = await fetch(path, opts);
    } catch (err) {
      return { ok: false, status: 0, data: { message: 'сервер не отвечает: ' + err.message } };
    }

    let data = null;
    const text = await res.text();
    if (text) {
      try { data = JSON.parse(text); } catch { data = { message: text }; }
    }

    // 401 — сессии нет. Это не ошибка страницы: показываем экран входа.
    if (res.status === 401) {
      S.me = null;
      renderProfile();
      showGate();
    }

    return { ok: res.ok, status: res.status, data };
  }

  const errText = (res, fallback) =>
    (res && res.data && (res.data.message || res.data.code)) || fallback || 'не получилось';

  /* ── уведомления ──────────────────────────────────────────────────────── */

  function toast(message, kind) {
    const node = el('div', 'toast' + (kind ? ' toast--' + kind : ''), message);
    $('toasts').appendChild(node);
    setTimeout(() => {
      node.classList.add('toast--out');
      setTimeout(() => node.remove(), 240);
    }, 3600);
  }

  /* ── профиль и боковая панель ─────────────────────────────────────────── */

  function avatarNode(node, nick, size) {
    if (!node) return;
    node.textContent = '';
    node.classList.add('avatar--fallback');
    node.textContent = (nick || '?').trim().slice(0, 1).toUpperCase() || '?';
    if (!S.me) return;

    // Аватар отдаётся только по сессии — вставляем картинку, и она сама
    // отваливается в букву, если сервер её не отдал.
    const img = new Image();
    img.alt = '';
    img.src = 'api/avatar?t=' + Date.now();
    img.onload = () => {
      node.textContent = '';
      node.classList.remove('avatar--fallback');
      img.className = size ? 'avatar ' + size : 'avatar';
      node.replaceWith(img);
      if (size === 'avatar--lg') img.id = 'sidebar-avatar';
      else img.id = 'avatar-mini';
    };
  }

  function renderProfile() {
    const nick = S.me ? S.me.nick : 'гость';
    const idText = S.me
      ? (S.me.displayId || String(S.me.id))
      : 'не авторизован';

    $('me-nick').textContent = nick;
    $('me-id').textContent = idText;
    $('sidebar-nick').textContent = nick;
    $('sidebar-id').textContent = idText;

    avatarNode($('avatar-mini'), nick, '');
    avatarNode($('sidebar-avatar'), nick, 'avatar--lg');

    const isAdmin = !!(S.me && (S.admin?.admin || S.admin?.adminId));
    $('sidebar-admin').hidden = !isAdmin;
    // Кнопка ведёт в админку всегда, когда id админский: код спросит сама админка.
    $('sidebar-admin-btn').hidden = !isAdmin;

    $('sidebar-hint').hidden = !!S.me;
    $('logout-btn').hidden = !S.me;

    $('play-nick').textContent = nick;
    $('play-id').textContent = idText;
    $('play-hint').hidden = !!S.me;
  }

  function renderStatus() {
    const st = S.status;
    const live = $('live');
    const text = $('live-text');

    if (!st) {
      live.classList.add('is-off');
      text.textContent = S.me ? 'нет данных' : 'скрыто';
    } else {
      live.classList.toggle('is-off', !st.gameListening);
      text.textContent = st.onlineTotal + ' онлайн';
    }

    if (st) {
      $('hero-online').textContent = st.onlineTotal;
      $('hero-rooms-count').textContent = st.roomsCount;
      if (st.tickMs) $('hero-ping').textContent = st.tickMs;
      $('sidebar-lobby').textContent = st.onlineLobby;
      $('sidebar-match').textContent = st.onlineMatch;
      $('sidebar-rooms').textContent = st.roomsCount + ' / ' + st.roomsMax;
      $('play-lobby').textContent = st.onlineLobby;
      $('play-match').textContent = st.onlineMatch;
      $('admin-online').textContent = st.onlineTotal;
      $('admin-rooms').textContent = st.roomsCount + ' / ' + st.roomsMax;
    }
  }

  /* ── экраны ───────────────────────────────────────────────────────────── */

  const VIEWS = { home: 'view-home', play: 'view-play', admin: 'view-admin', gate: 'view-gate' };

  function showView(name) {
    S.page = name;
    for (const [key, id] of Object.entries(VIEWS)) {
      const node = $(id);
      if (node) node.hidden = key !== name;
    }
    if (name !== 'gate') $('view-gate').hidden = true;
    window.scrollTo({ top: 0, behavior: 'instant' in window ? 'instant' : 'auto' });

    // Секции hero/rooms живут только на главной: остальные экраны — отдельные страницы.
    const footer = document.querySelector('.footer');
    if (footer) footer.hidden = false;
  }

  /** Экран входа — единственный, что видно без сессии. */
  function showGate() {
    showView('gate');
    const code = $('auth-code');
    if (code) setTimeout(() => code.focus(), 80);
  }

  /** Куда вернуть человека после входа (из адреса). */
  function intendedView() {
    const p = location.pathname.replace(/\/+$/, '');
    if (/\/admin$/.test(p)) return 'admin';
    if (/\/play$/.test(p)) return 'play';
    return 'home';
  }

  /* ── каталог режимов ──────────────────────────────────────────────────── */

  async function loadModes() {
    const res = await api('GET', 'api/modes');
    if (!res.ok || !res.data?.modes) return;
    S.modes = res.data.modes;
    renderModeSelects();
  }

  function renderModeSelects() {
    const create = $('c-mode');
    const filter = $('f-mode');

    create.textContent = '';
    for (const mode of S.modes) {
      const opt = el('option', '', mode.name);
      opt.value = mode.id;
      create.appendChild(opt);
    }

    filter.textContent = '';
    const all = el('option', '', 'Все режимы');
    all.value = '';
    filter.appendChild(all);
    for (const mode of S.modes) {
      const opt = el('option', '', mode.name);
      opt.value = mode.id;
      filter.appendChild(opt);
    }
    filter.value = S.filterMode;

    renderMaps();
    renderSizes();
  }

  function currentMode() {
    return S.modes.find(m => m.id === $('c-mode').value) || S.modes[0] || null;
  }

  function renderMaps() {
    const mode = currentMode();
    const select = $('c-map');
    select.textContent = '';
    if (!mode) return;
    for (const level of mode.levels || []) {
      const opt = el('option', '', level);
      opt.value = level;
      select.appendChild(opt);
    }
    if (mode.defaultLevel) select.value = mode.defaultLevel;

    // Фильтр карт — объединение по всем режимам, иначе карту одного режима
    // нельзя было бы найти, пока выбран другой.
    const mapFilter = $('f-map');
    typeof mapFilter.dataset.filled === 'undefined' && fillMapFilter();
  }

  function fillMapFilter() {
    const filter = $('f-map');
    filter.textContent = '';
    const all = el('option', '', 'Все карты');
    all.value = '';
    filter.appendChild(all);

    const seen = new Set();
    for (const mode of S.modes) {
      for (const level of mode.levels || []) {
        if (seen.has(level)) continue;
        seen.add(level);
        const opt = el('option', '', level);
        opt.value = level;
        filter.appendChild(opt);
      }
    }
    filter.dataset.filled = '1';
    filter.value = S.filterMap;
  }

  function renderSizes() {
    const mode = currentMode();
    const size = $('c-size');
    size.textContent = '';
    if (!mode) return;

    const sizes = mode.teamSizes || [];
    if (sizes.length === 0) {
      const opt = el('option', '', 'как в режиме');
      opt.value = '0';
      size.appendChild(opt);
      size.disabled = true;
      return;
    }

    size.disabled = false;
    const auto = el('option', '', 'как в режиме');
    auto.value = '0';
    size.appendChild(auto);
    for (const n of sizes) {
      const opt = el('option', '', n + ' на ' + n);
      opt.value = String(n);
      size.appendChild(opt);
    }
  }

  /* ── комнаты ──────────────────────────────────────────────────────────── */

  async function loadRooms() {
    const res = await api('GET', 'api/rooms');
    if (!res.ok) {
      if (res.status !== 401) toast(errText(res, 'не удалось загрузить комнаты'), 'err');
      renderRooms();
      return;
    }

    S.rooms = res.data.rooms || [];
    S.roomsMeta = {
      count: res.data.count || 0,
      maxRooms: res.data.maxRooms || 0,
      nextNumber: res.data.nextNumber || 1,
    };
    renderRooms();
    renderMyRoom();
  }

  function renderRooms() {
    const list = $('rooms-list');
    const filters = $('rooms-filters');
    if (!list) return;

    if (!S.me) {
      list.textContent = '';
      filters.hidden = true;
      const box = el('div', 'empty');
      box.appendChild(el('b', '', 'Список скрыт'));
      box.appendChild(el('span', '', 'Войди по коду из лобби — сервер не отдаёт комнаты без входа.'));
      list.appendChild(box);
      return;
    }

    filters.hidden = false;

    const visible = S.rooms.filter(room =>
      (!S.filterMode || room.mode === S.filterMode) &&
      (!S.filterMap || room.level === S.filterMap));

    list.textContent = '';

    if (visible.length === 0) {
      const box = el('div', 'empty');
      box.appendChild(el('b', '', S.rooms.length ? 'Под фильтр ничего не подошло' : 'Комнат пока нет'));
      box.appendChild(el('span', '', S.rooms.length
        ? 'Сбрось фильтры или открой свою комнату.'
        : 'Создай первую — кнопка справа сверху.'));
      list.appendChild(box);
      return;
    }

    for (const room of visible) {
      list.appendChild(roomRow(room));
    }
  }

  function roomRow(room) {
    const row = el('div', 'room' + (room.isMine ? ' room--mine' : ''));
    row.appendChild(el('div', 'room__num', '#' + room.number));

    const main = el('div', 'room__main');
    main.appendChild(el('div', 'room__name', room.name || 'Комната #' + room.number));

    const meta = el('div', 'room__meta');
    meta.appendChild(el('span', 'chip', room.modeName || room.mode));
    meta.appendChild(el('span', '', room.level));
    meta.appendChild(el('span', 'dot', '·'));
    meta.appendChild(el('span', '', room.owner));
    if (room.teamLabel) {
      meta.appendChild(el('span', 'dot', '·'));
      meta.appendChild(el('span', '', room.teamLabel));
    }
    meta.appendChild(el('span', 'dot', '·'));

    const state = el('span', 'chip ' + (room.state === 'live' ? 'chip--live' : 'chip--waiting'),
      room.state === 'live' ? 'идёт' : 'ожидание');
    meta.appendChild(state);

    if (room.isMine) meta.appendChild(el('span', 'chip chip--mine', 'моя'));
    main.appendChild(meta);
    row.appendChild(main);

    const score = el('div', 'room__score');
    score.appendChild(el('span', '', String(room.scoreCt ?? 0)));
    score.appendChild(el('span', '', '*'));
    score.appendChild(el('span', '', String(room.scoreTr ?? 0)));
    row.appendChild(score);

    const actions = el('div', 'room__actions');
    const join = el('button', 'btn btn--sm btn--silver', '');
    join.type = 'button';
    join.appendChild(el('span', '', 'Войти'));
    join.addEventListener('click', () => openJoin(room));
    actions.appendChild(join);
    row.appendChild(actions);

    return row;
  }

  function renderMyRoom() {
    const box = $('my-room');
    if (!box) return;
    box.textContent = '';

    const mine = S.rooms.find(r => r.isMine);
    if (!mine) {
      box.appendChild(el('div', 'faint', 'Пока нет — создай слева.'));
      return;
    }

    const card = el('div', 'panel');
    card.style.padding = '14px';

    const head = el('div', 'row row--between');
    head.appendChild(el('b', 'room__name', '#' + mine.number + ' ' + (mine.name || '')));
    head.appendChild(el('span', 'chip ' + (mine.state === 'live' ? 'chip--live' : 'chip--waiting'),
      mine.state === 'live' ? 'идёт' : 'ожидание'));
    card.appendChild(head);

    card.appendChild(el('div', 'room__meta mt8', mine.modeName + ' · ' + mine.level));

    const actions = el('div', 'row mt16');
    const end = el('button', 'btn btn--sm btn--danger');
    end.type = 'button';
    end.appendChild(el('span', '', 'Завершить игру'));
    end.addEventListener('click', () => endRoom(mine));
    actions.appendChild(end);

    const join = el('button', 'btn btn--sm btn--ghost');
    join.type = 'button';
    join.appendChild(el('span', '', 'Команда входа'));
    join.addEventListener('click', () => openJoin(mine));
    actions.appendChild(join);

    card.appendChild(actions);
    box.appendChild(card);
  }

  async function endRoom(room) {
    const res = await api('POST', 'api/rooms/' + room.number + '/end', {});
    if (!res.ok) {
      toast(errText(res, 'не удалось завершить'), 'err');
      return;
    }
    toast('катка завершается', 'ok');
    await loadRooms();
    await loadStatus();
  }

  /* ── создание комнаты ─────────────────────────────────────────────────── */

  async function createRoom(event) {
    if (event) event.preventDefault();
    if (!S.me) { showGate(); return; }

    const btn = $('create-btn');
    btn.disabled = true;

    const body = {
      mode: $('c-mode').value,
      level: $('c-map').value,
      name: $('c-name').value.trim(),
      perTeam: Number($('c-size').value || 0),
    };

    const res = await api('POST', 'api/rooms', body);
    btn.disabled = false;

    const note = $('create-note');
    note.hidden = false;

    if (!res.ok) {
      note.className = 'formnote formnote--danger mt16';
      note.textContent = errText(res, 'не удалось создать комнату');
      return;
    }

    const room = res.data;
    note.className = 'formnote mt16';
    note.textContent = 'Комната #' + room.number + ' создана. Команда входа скопирована.';
    openJoin(room);
    await loadRooms();
    await loadStatus();
  }

  /* ── вход в комнату ───────────────────────────────────────────────────── */

  async function openJoin(room) {
    const cmd = room.joinCommand || '/join';
    $('join-cmd').value = cmd;
    openModal('#modal-join');

    const copied = await copyText(cmd);
    $('join-copy-state').textContent = copied
      ? 'скопировано в буфер — вставь в чат лобби'
      : 'скопируй вручную: выдели строку выше';
  }

  async function copyText(text) {
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return true;
      }
    } catch { /* ниже — запасной путь */ }

    try {
      const area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.appendChild(area);
      area.select();
      const ok = document.execCommand('copy');
      area.remove();
      return ok;
    } catch {
      return false;
    }
  }

  /* ── сводка ───────────────────────────────────────────────────────────── */

  async function loadStatus() {
    const res = await api('GET', 'api/status');
    if (!res.ok) {
      if (res.status !== 401) renderStatus();
      return;
    }
    S.status = res.data;
    renderStatus();
  }

  /* ── авторизация ──────────────────────────────────────────────────────── */

  async function loadMe() {
    const res = await api('GET', 'api/me');
    if (!res.ok) {
      S.me = null;
      S.admin = null;
      renderProfile();
      return false;
    }

    S.me = res.data;
    // Публичный id мог быть подменён админом: сервер отдаёт displayId.
    await loadAdminMe();
    renderProfile();
    return true;
  }

  async function loadAdminMe() {
    const res = await api('GET', 'api/admin/me');
    S.admin = res.ok ? res.data : null;
  }

  async function doAuth(event) {
    if (event) event.preventDefault();
    if (S.authBusy) return;

    const code = ($('auth-code').value || '').trim();
    const note = $('auth-note');
    note.hidden = false;

    if (!/^\d{6}$/.test(code)) {
      note.className = 'formnote formnote--danger mt16';
      note.textContent = 'Код — шесть цифр из чата лобби.';
      return;
    }

    S.authBusy = true;
    $('auth-submit').disabled = true;

    const res = await api('POST', 'api/auth', { code });
    S.authBusy = false;
    $('auth-submit').disabled = false;

    if (!res.ok) {
      note.className = 'formnote formnote--danger mt16';
      note.textContent = errText(res, 'код не подошёл');
      return;
    }

    S.me = res.data;
    $('auth-code').value = '';
    await loadAdminMe();
    renderProfile();
    toast('добро пожаловать, ' + S.me.nick, 'ok');

    const target = intendedView();
    showView(target);
    await Promise.all([loadStatus(), loadRooms(), loadModes()]);
  }

  async function doLogout() {
    await api('POST', 'api/logout', {});
    // Админский ключ умирает вместе с сессией: иначе «вышел» оставлял бы права.
    await api('POST', 'api/admin/logout', {});
    S.me = null;
    S.admin = null;
    S.status = null;
    S.rooms = [];
    renderProfile();
    renderStatus();
    renderRooms();
    closeSidebar();
    showGate();
  }

  /* ── админка: вход ────────────────────────────────────────────────────── */

  function openAdminAuth() {
    closeSidebar();
    $('admin-code').value = '';
    $('admin-auth-note').hidden = true;
    openModal('#modal-admin');
    setTimeout(() => $('admin-code').focus(), 100);
  }

  async function doAdminAuth(event) {
    if (event) event.preventDefault();
    const code = ($('admin-code').value || '').trim();
    const note = $('admin-auth-note');
    note.hidden = false;

    if (!code) {
      note.className = 'formnote formnote--danger mt16';
      note.textContent = 'Введи код.';
      return;
    }

    const res = await api('POST', 'api/admin/auth', { code });
    // Ключ выдан сервером; странице нужно знать это сразу, а не после /api/admin/me:
    // иначе первое же действие в админке уходило бы без прав.
    S.admin = res.ok ? { admin: true, adminId: true, id: (res.data || {}).id || 0,
      nick: (res.data || {}).nick || (S.me ? S.me.nick : '') } : null;
    if (!res.ok) {
      note.className = 'formnote formnote--danger mt16';
      note.textContent = errText(res, 'код не подошёл');
      return;
    }

    closeModal('#modal-admin');
    await loadAdminMe();
    renderProfile();
    toast('админка открыта', 'ok');
    location.hash = '';
    showView('admin');
    await loadAdminData();
  }

  async function exitAdmin() {
    await api('POST', 'api/admin/logout', {});
    S.admin = null;
    renderProfile();
    toast('вышел из админки');
    showView('home');
    await refreshAll();
  }

  /* ── админка: данные ──────────────────────────────────────────────────── */

  let banTarget = null;

  async function loadAdminData() {
    await Promise.all([loadBans(), loadStatus()]);
    $('admin-who').textContent = S.me ? S.me.nick : '—';
  }

  async function loadBans() {
    const res = await api('GET', 'api/admin/bans');
    const list = $('bans-list');
    if (!res.ok) {
      list.textContent = '';
      const box = el('div', 'empty');
      box.appendChild(el('b', '', 'Нет доступа'));
      box.appendChild(el('span', '', errText(res, 'нужен код администратора')));
      list.appendChild(box);
      return;
    }

    const bans = res.data.bans || [];
    $('admin-bans-count').textContent = bans.length;
    list.textContent = '';

    if (bans.length === 0) {
      const box = el('div', 'empty');
      box.appendChild(el('b', '', 'Банов нет'));
      box.appendChild(el('span', '', 'Все чисты — пока что.'));
      list.appendChild(box);
      return;
    }

    for (const ban of bans) {
      const row = el('div', 'banrow');

      const who = el('div', 'banrow__who');
      who.appendChild(el('b', '', ban.nick));
      const idLine = el('div', '', (ban.displayId || ban.id ? 'ID ' + (ban.displayId || ban.id) : '') +
        ' · забанил ' + ban.bannedBy);
      who.appendChild(idLine);
      if (ban.reason) who.appendChild(el('div', 'faint', 'причина: ' + ban.reason));
      row.appendChild(who);

      row.appendChild(el('div', 'banrow__time', fmtRemaining(ban.remainingSec)));

      const unban = el('button', 'btn btn--sm btn--ghost');
      unban.type = 'button';
      unban.appendChild(el('span', '', 'Разбанить'));
      unban.addEventListener('click', () => unbanById(ban.id, ban.nick));
      row.appendChild(unban);

      list.appendChild(row);
    }
  }

  function fmtRemaining(sec) {
    if (!sec || sec <= 0) return 'истёк';
    const d = Math.floor(sec / 86400);
    const h = Math.floor((sec % 86400) / 3600);
    const m = Math.floor((sec % 3600) / 60);
    if (d > 0) return d + 'д ' + h + 'ч';
    if (h > 0) return h + 'ч ' + m + 'м';
    return m + 'м';
  }

  async function findBanTarget() {
    const raw = ($('ban-id').value || '').trim().replace(/^#/, '');
    const box = $('ban-preview');
    box.textContent = '';
    banTarget = null;
    $('ban-submit').disabled = true;

    if (!/^\d+$/.test(raw)) {
      box.appendChild(note('danger', 'ID — это число из игры или из /ids.'));
      return;
    }

    const res = await api('GET', 'api/admin/players?id=' + encodeURIComponent(raw));
    if (!res.ok) {
      box.appendChild(note('danger', errText(res, 'не удалось найти игрока')));
      return;
    }

    const p = res.data;
    if (!p.known) {
      box.appendChild(note('warn',
        'Сервер не знает id ' + raw + ': человек ещё не заходил в лобби. Банить некого.'));
      return;
    }

    banTarget = p;

    const card = el('div', 'banpreview');
    const avatar = el('span', 'avatar avatar--lg avatar--fallback',
      (p.nick || '?').slice(0, 1).toUpperCase());
    // Аватар цели отдаётся только по её собственной сессии, поэтому здесь —
    // буква: показывать чужую картинку сервер не умеет и не должен угадывать.
    card.appendChild(avatar);

    const info = el('div', 'banpreview__info');
    info.appendChild(el('b', '', p.nick));
    info.appendChild(el('span', '', 'ID ' + (p.displayId || p.id) +
      (p.online ? ' · сейчас в лобби' : ' · не в сети')));
    if (p.ips && p.ips.length) {
      info.appendChild(el('div', 'faint', 'IP: ' + p.ips.join(', ')));
    }
    if (p.banned) {
      info.appendChild(el('div', '', 'уже забанен'));
    }
    card.appendChild(info);

    box.appendChild(card);
    $('ban-submit').disabled = false;
  }

  function note(kind, text) {
    const box = el('div', 'formnote ' + (kind ? 'formnote--' + kind : ''));
    box.textContent = text;
    return box;
  }

  /* подтверждение бана: 3 секунды «точно?» — владелец просил именно так */
  function askBanConfirm() {
    if (!banTarget) return;

    const reason = ($('ban-reason').value || '').trim();
    const duration = $('ban-duration').value;
    const durationLabel = $('ban-duration').options[$('ban-duration').selectedIndex].textContent;

    const box = $('ban-confirm');
    box.hidden = false;
    box.textContent = '';

    const confirm = el('div', 'confirm');
    confirm.appendChild(el('b', '', 'Точно баним?'));

    const text = el('div', 'muted');
    text.appendChild(document.createTextNode('Игрок '));
    text.appendChild(el('b', '', banTarget.nick));
    text.appendChild(document.createTextNode(
      ' (ID ' + (banTarget.displayId || banTarget.id) + ') будет забанен на ' +
      durationLabel + (reason ? ' по причине «' + reason + '».' : ' без причины.')));
    confirm.appendChild(text);

    confirm.appendChild(el('div', 'faint',
      'В игре будет видно, что бан выдал ' + (S.me ? S.me.nick : 'админ') +
      ', а не сервер.'));

    const row = el('div', 'row mt8');

    const yes = el('button', 'btn btn--danger btn--sm');
    yes.type = 'button';
    const yesLabel = el('span', '', 'Да, забанить');
    yes.appendChild(yesLabel);
    yes.disabled = true;

    let left = 3;
    yesLabel.textContent = 'Да, забанить (' + left + ')';
    const timer = setInterval(() => {
      left -= 1;
      if (left <= 0) {
        clearInterval(timer);
        yes.disabled = false;
        yesLabel.textContent = 'Да, забанить';
        return;
      }
      yesLabel.textContent = 'Да, забанить (' + left + ')';
    }, 1000);

    yes.addEventListener('click', () => {
      clearInterval(timer);
      submitBan(banTarget.id, duration, reason);
    });

    const no = el('button', 'btn btn--ghost btn--sm');
    no.type = 'button';
    no.appendChild(el('span', '', 'Отмена'));
    no.addEventListener('click', () => {
      clearInterval(timer);
      box.hidden = true;
      box.textContent = '';
    });

    row.appendChild(yes);
    row.appendChild(no);
    confirm.appendChild(row);
    box.appendChild(confirm);
    box.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }

  async function submitBan(id, duration, reason) {
    const res = await api('POST', 'api/admin/bans', { id, duration, reason });
    $('ban-confirm').hidden = true;

    if (!res.ok) {
      toast(errText(res, 'не удалось забанить'), 'err');
      return;
    }

    toast('забанен ' + res.data.nick + ' · ' + res.data.duration, 'ok');
    $('ban-id').value = '';
    $('ban-reason').value = '';
    $('ban-preview').textContent = '';
    $('ban-submit').disabled = true;
    banTarget = null;
    await loadBans();
  }

  async function unbanById(id, nick) {
    if (!id) {
      toast('у этой записи нет серверного id — сними бан командой /unban в чате', 'err');
      return;
    }

    const res = await api('POST', 'api/admin/unban', { id });
    if (!res.ok) {
      toast(errText(res, 'не удалось разбанить'), 'err');
      return;
    }

    toast('разбанен ' + nick, 'ok');
    await loadBans();
  }

  /* ── админка: публичный id ────────────────────────────────────────────── */

  async function savePublicId() {
    const value = ($('id-value').value || '').trim();
    const res = await api('POST', 'api/admin/public-id', { id: 0, value });
    if (!res.ok) {
      toast(errText(res, 'не удалось сохранить id'), 'err');
      return;
    }

    toast('публичный id: ' + res.data.displayId, 'ok');
    await loadMe();
  }

  async function clearPublicId() {
    const res = await api('POST', 'api/admin/public-id', { id: 0, value: '' });
    if (!res.ok) {
      toast(errText(res, 'не удалось сбросить id'), 'err');
      return;
    }

    $('id-value').value = '';
    toast('снова числовой id', 'ok');
    await loadMe();
  }

  /* ── модальные окна и боковая панель ──────────────────────────────────── */

  function openModal(selector) {
    const node = document.querySelector(selector);
    if (!node) return;
    node.hidden = false;
    requestAnimationFrame(() => node.classList.add('is-open'));
  }

  function closeModal(selector) {
    const node = document.querySelector(selector);
    if (!node) return;
    node.classList.remove('is-open');
    setTimeout(() => { node.hidden = true; }, 220);
  }

  function openSidebar() {
    $('sidebar').classList.add('is-open');
    $('scrim').classList.add('is-open');
    $('avatar-btn').setAttribute('aria-expanded', 'true');
  }

  function closeSidebar() {
    $('sidebar').classList.remove('is-open');
    $('scrim').classList.remove('is-open');
    $('avatar-btn').setAttribute('aria-expanded', 'false');
  }

  /* ── вкладки админки ──────────────────────────────────────────────────── */

  function selectAdminTab(name) {
    const map = {
      ban: ['tab-ban', 'pane-ban'],
      bans: ['tab-bans', 'pane-bans'],
      id: ['tab-id', 'pane-id'],
    };
    for (const [key, [tab, pane]] of Object.entries(map)) {
      $(tab).setAttribute('aria-selected', key === name ? 'true' : 'false');
      $(pane).hidden = key !== name;
    }
    if (name === 'bans') loadBans();
  }

  /* ── видео-фон ────────────────────────────────────────────────────────── */

  /**
   * Видео — оформление, а не данные. Поэтому:
   *   • нет файла — остаётся градиент, страница не ломается;
   *   • экономия трафика и батареи — при reduced-motion и на слабом соединении
   *     видео не играет, показывается первый кадр;
   *   • звука нет и не будет: это фон.
   */
  function setupHeroVideo() {
    const video = $('hero-video');
    if (!video) return;

    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const slow = navigator.connection && /2g/.test(navigator.connection.effectiveType || '');

    const hideIfBroken = () => {
      video.style.display = 'none';
    };

    video.addEventListener('error', hideIfBroken, true);
    const last = video.querySelector('source:last-of-type');
    if (last) last.addEventListener('error', hideIfBroken);

    if (reduce || slow) {
      video.pause();
      video.removeAttribute('autoplay');
      return;
    }

    const play = () => {
      const attempt = video.play();
      if (attempt && attempt.catch) attempt.catch(() => { /* автоплей запрещён — не беда */ });
    };
    play();
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) video.pause();
      else play();
    });
  }

  /* ── появление секций ─────────────────────────────────────────────────── */

  function setupReveal() {
    const items = document.querySelectorAll('.reveal');
    if (!items.length) return;

    if (!('IntersectionObserver' in window)) {
      items.forEach(node => node.classList.add('is-in'));
      return;
    }

    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-in');
          observer.unobserve(entry.target);
        }
      }
    }, { rootMargin: '0px 0px -10% 0px' });

    items.forEach(node => observer.observe(node));
  }

  /* ── шапка при скролле ────────────────────────────────────────────────── */

  function setupTopbar() {
    const bar = $('topbar');
    if (!bar) return;
    const update = () => bar.classList.toggle('is-solid', window.scrollY > 24);
    window.addEventListener('scroll', update, { passive: true });
    update();
  }

  /* ── маршрутизация ────────────────────────────────────────────────────── */

  function routeForPath(path) {
    const p = path.replace(/\/+$/, '');
    if (/\/admin$/.test(p)) return 'admin';
    if (/\/play$/.test(p)) return 'play';
    return 'home';
  }

  async function navigate() {
    const target = routeForPath(location.pathname);

    if (!S.me) {
      showGate();
      return;
    }

    if (target === 'admin' && !(S.admin && S.admin.admin)) {
      // Нет ключа: просим код, но не выкидываем с сайта.
      showView('admin');
      openAdminAuth();
      return;
    }

    showView(target);
    if (target === 'admin') await loadAdminData();
    if (target === 'play') await loadRooms();
  }

  /* ── обновление данных ────────────────────────────────────────────────── */

  async function refreshAll() {
    const authed = await loadMe();
    if (!authed) {
      showGate();
      return;
    }
    await Promise.all([loadStatus(), loadRooms(), loadModes()]);
  }

  /* ── запуск ───────────────────────────────────────────────────────────── */

  function bind() {
    $('auth-form').addEventListener('submit', doAuth);
    $('admin-auth-form').addEventListener('submit', doAdminAuth);
    $('create-form').addEventListener('submit', createRoom);
    $('ban-form').addEventListener('submit', event => { event.preventDefault(); askBanConfirm(); });

    $('avatar-btn').addEventListener('click', () => {
      if ($('sidebar').classList.contains('is-open')) closeSidebar();
      else openSidebar();
    });
    $('sidebar-close').addEventListener('click', closeSidebar);
    $('scrim').addEventListener('click', closeSidebar);
    $('sidebar-admin-btn').addEventListener('click', () => {
      if (S.admin && S.admin.admin) {
        closeSidebar();
        showView('admin');
        loadAdminData();
      } else {
        openAdminAuth();
      }
    });

    $('logout-btn').addEventListener('click', doLogout);
    $('admin-exit').addEventListener('click', exitAdmin);

    $('c-mode').addEventListener('change', () => { renderMaps(); renderSizes(); });
    $('f-mode').addEventListener('change', () => {
      S.filterMode = $('f-mode').value;
      renderRooms();
    });
    $('f-map').addEventListener('change', () => {
      S.filterMap = $('f-map').value;
      renderRooms();
    });

    $('ban-find').addEventListener('click', findBanTarget);
    $('ban-id').addEventListener('keydown', event => {
      if (event.key === 'Enter') { event.preventDefault(); findBanTarget(); }
    });
    $('ban-submit').addEventListener('click', askBanConfirm);
    $('bans-refresh').addEventListener('click', loadBans);
    $('id-save').addEventListener('click', savePublicId);
    $('id-clear').addEventListener('click', clearPublicId);

    $('tab-ban').addEventListener('click', () => selectAdminTab('ban'));
    $('tab-bans').addEventListener('click', () => selectAdminTab('bans'));
    $('tab-id').addEventListener('click', () => selectAdminTab('id'));

    $('rooms-refresh').addEventListener('click', async () => {
      await loadRooms();
      await loadStatus();
      toast('обновлено');
    });

    document.querySelectorAll('[data-close]').forEach(btn => {
      btn.addEventListener('click', () => closeModal(btn.dataset.close));
    });

    document.querySelectorAll('.modal').forEach(modal => {
      modal.addEventListener('click', event => {
        if (event.target === modal) closeModal('#' + modal.id);
      });
    });

    document.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      document.querySelectorAll('.modal.is-open').forEach(modal => closeModal('#' + modal.id));
      closeSidebar();
    });

    // Ссылки на /play и /admin ведут внутри страницы, но адрес меняется —
    // иначе «назад» в браузере вёл бы не туда, куда человек шёл.
    document.querySelectorAll('a[href^="/"]').forEach(link => {
      link.addEventListener('click', event => {
        const href = link.getAttribute('href');
        if (!href || href.startsWith('/#')) return;
        event.preventDefault();
        history.pushState({}, '', href);
        navigate();
      });
    });

    window.addEventListener('popstate', navigate);

    setInterval(async () => {
      if (!S.me) return;
      await loadStatus();
      if (S.page === 'home' || S.page === 'play') await loadRooms();
    }, 15000);
  }

  function start() {
    bind();
    setupHeroVideo();
    setupReveal();
    setupTopbar();

    renderProfile();
    renderStatus();
    renderRooms();

    // Адрес сразу говорит, куда идти: /play и /admin — отдельные экраны.
    refreshAll().then(async () => {
      if (!S.me) return;
      await navigate();
    });
  }

  /* Тестовый шов: jsdom-проверка страницы дергает функции отсюда. */
  window.__panel = {
    state: S, api, loadModes, loadRooms, loadStatus, loadMe, renderRooms, renderProfile,
    renderStatus, doAuth, createRoom, openJoin, selectAdminTab, showView, navigate,
    routeForPath, findBanTarget, askBanConfirm, submitBan, savePublicId, clearPublicId,
    fmtRemaining,
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
