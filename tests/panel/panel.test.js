/**
 * Проверка живой страницы панели (server/Web/wwwroot/index.html) в jsdom.
 *
 * Сеть не нужна: fetch замокан ответами, которые отдаёт WebPanelRouter, поэтому тест
 * проверяет ровно то, что сломается в браузере, — до того, как это увидит игрок:
 *   • каталог режимов берётся с сервера, а не из копии в JS;
 *   • фильтры режима и карты работают, «Все режимы» выбран явно;
 *   • создание комнаты уходит одним POST, номер выдаёт сервер;
 *   • ошибки API показываются понятным текстом, а не кодом;
 *   • имя комнаты экранируется (XSS из чужого ввода);
 *   • закрытая комната не приглашает войти;
 *   • список показывает счёт CT * T и потолок комнаты;
 *   • «Войти» открывает окно с точной командой, не переключая вкладку;
 *   • свой ID не публикуется и на вход не нужен: владелец берётся из сессии;
 *   • баннер «игровой порт не слушается» — только у админа и только при провале.
 */
const fs = require('fs');
const path = require('path');
/* jsdom ставится отдельно (страница — единственный JS в проекте):
     npm install --prefix /tmp/paneltest jsdom
     NODE_PATH=/tmp/paneltest/node_modules node tests/panel/panel.test.js
   Путь к jsdom не зашит: бери тот, что видит Node. */
const { JSDOM } = require('jsdom');

const html = fs.readFileSync(
  path.join(__dirname, '..', '..', 'server', 'Web', 'wwwroot', 'index.html'), 'utf8');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, extra) {
  if (cond) { pass++; }
  else { fail++; failures.push(name + (extra ? ' — ' + extra : '')); }
}

/* ── данные, как их отдаёт сервер ─────────────────────────────────────── */
const MODES = {
  modes: [
    { id: 'Ranked2v2', name: 'Союзники', alias: '2v2', defaultLevel: 'Sandstone 2x2',
      levels: ['Prison 2x2', 'Sandstone 2x2'], aliases: ['2v2', 'союзники'],
      teamSizes: [1, 2], implemented: true },
    { id: 'Duel', name: 'Дуэль', alias: 'duel', defaultLevel: 'Yard',
      levels: ['Block', 'Yard'], aliases: ['duel'], teamSizes: [1, 2], implemented: false },
    { id: 'DeathMatch', name: 'Командный бой', alias: 'tdm', defaultLevel: 'Arena',
      levels: ['Arena'], aliases: ['tdm'], teamSizes: [], implemented: true },
  ],
};

let roomsState = [
  { number: 1, mode: 'Ranked2v2', level: 'Sandstone 2x2', name: 'рялна',
    owner: 'vlal', ownerId: 111, state: 'live', players: 4, joinCommand: '/join 2v2 #1',
    scoreCt: 5, scoreTr: 3, perTeam: 2, maxPlayers: 4, teamLabel: '2 на 2' },
  { number: 2, mode: 'Duel', level: 'Yard', name: '', owner: 'botx', ownerId: 222,
    state: 'waiting', players: 1, joinCommand: '/join duel #2', scoreCt: 1, scoreTr: 0 },
];

let statusState = {
  onlineLobby: 2, onlineMatch: 5, onlineTotal: 7,
  roomsCount: 2, roomsMax: 64, nextNumber: 3, gameListening: true,
};
let statsState = { kills: 120, deaths: 80, kd: 1.5, seconds: 5400, matches: 9 };

let posted = null;
let postResponse = null;
let copied = null;
let meState = null;          /* null → /api/me отвечает 401 */
let authResponse = null;     /* подмена ответа POST /api/auth */
let ended = null;            /* куда ушёл POST /api/rooms/{n}/end */
let endResponse = null;      /* подмена ответа завершения */
let adminState = null;       /* подмена ответа GET /api/admin */

function fakeFetch(url, opts) {
  const method = (opts && opts.method) || 'GET';
  const u = String(url);
  let body = null, status = 200;

  /* Порядок важен: «…/api/rooms/1/end» тоже начинается с api/rooms. */
  if (/\/end$/.test(u) && method === 'POST') {
    ended = u;
    if (endResponse) { body = endResponse.body; status = endResponse.status; }
    else { body = { ok: true, number: 1 }; status = 200; }
  } else if (u.indexOf('api/auth') >= 0 && method === 'POST') {
    if (authResponse) { body = authResponse.body; status = authResponse.status; }
    else { body = { id: 333, nick: 'новичок' }; status = 200; }
  } else if (u.indexOf('api/me') >= 0 && method === 'GET') {
    if (meState) { body = meState; status = 200; }
    else { body = { code: 'no_auth', message: 'нужно ввести код из лобби' }; status = 401; }
  } else if (u.indexOf('api/logout') >= 0) { body = { ok: true }; status = 200; }
  else if (u.indexOf('api/avatar') >= 0) { body = null; status = 404; }
  else if (u.indexOf('api/stats/me') >= 0) {
    if (meState) { body = statsState; status = 200; }
    else { body = { code: 'no_auth', message: 'x' }; status = 401; }
  }
  else if (u.indexOf('api/admin/login') >= 0) {
    body = { ok: true, admin: true, authed: true, id: 999, nick: 'влал', customId: null,
             bans: { enabled: true, minMinutes: 10, maxDays: 14 } };
    status = 200;
  }
  else if (u.indexOf('api/admin') >= 0) { body = adminState; status = 200; }
  else if (u.indexOf('api/modes') >= 0) body = MODES;
  else if (u.indexOf('api/rooms') >= 0 && method === 'GET') {
    body = { nextNumber: 3, count: roomsState.length, maxRooms: 64, rooms: roomsState };
  } else if (u.indexOf('api/rooms') >= 0 && method === 'POST') {
    posted = JSON.parse(opts.body);
    if (postResponse) { body = postResponse.body; status = postResponse.status; }
    else {
      body = { number: 3, mode: 'Ranked2v2', modeName: 'Союзники', level: 'Sandstone 2x2',
               name: 'новая', owner: 'новичок', ownerId: 333,
               joinCommand: '/join 2v2 #3', state: 'waiting',
               perTeam: 2, maxPlayers: 4, teamLabel: '2 на 2' };
      status = 201;
    }
  } else if (u.indexOf('api/status') >= 0) body = statusState;
  else { body = null; status = 404; }

  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    text: () => Promise.resolve(body === null ? '' : JSON.stringify(body)),
  });
}

/* Серверная форма комнаты → та, что держит страница (как в loadRooms). */
function toPage(list) {
  return list.map(function (r) {
    return {
      n: r.number, name: r.name, mode: r.mode, map: r.level,
      players: r.players || 0, state: r.state, owner: r.owner, ownerId: r.ownerId,
      scoreCt: r.scoreCt || 0, scoreTr: r.scoreTr || 0,
      cmd: r.joinCommand || '',
      perTeam: r.perTeam, maxPlayers: r.maxPlayers, teamLabel: r.teamLabel,
      isMine: r.isMine === true,
    };
  });
}

(async () => {
  const dom = new JSDOM(html, {
    runScripts: 'dangerously',
    pretendToBeVisual: true,
    url: 'https://example.com/vlalchillow/',
    beforeParse(window) {
      window.fetch = fakeFetch;
      Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
      Object.defineProperty(window.navigator, 'clipboard', {
        configurable: true,
        value: { writeText: text => { copied = text; return Promise.resolve(); } },
      });
      window.matchMedia = () => ({ matches: false, addListener() {}, removeListener() {} });
      window.scrollTo = () => {};
      window.confirm = () => true;
    },
  });

  const { window } = dom;
  await new Promise(r => setTimeout(r, 150));

  const P = window.__panel;
  const $ = id => window.document.getElementById(id);
  const tick = ms => new Promise(r => setTimeout(r, ms));

  /* ── ассеты под подпутём ──────────────────────────────────────────────
     Панель открывается как example.com/vlalchillow/, поэтому страница отдаётся с
     <base href="/vlalchillow/"> (его вставляет WebPanelRouter), а ссылки на
     медиа — относительные. Корневой абсолютный путь /media/… уехал бы на
     example.com/media/…, мимо прокси: видео и шрифты не пришли бы вовсе. */
  const assetHrefs = [];
  window.document.querySelectorAll('[src],[href],[poster]').forEach(function(n){
    ['src','href','poster'].forEach(function(a){
      const v = n.getAttribute(a);
      if (v) assetHrefs.push(v);
    });
  });
  const cssText = (html.split('<style>')[1] || '').split('</style>')[0];
  const urlRe = /url\(\s*["']?([^"')]+)["']?\s*\)/gi;
  let um;
  while ((um = urlRe.exec(cssText)) !== null) assetHrefs.push(um[1]);

  const local = assetHrefs.filter(function(u){
    return u && u.charAt(0) !== '#' && u.indexOf('data:') !== 0 && !/^https?:/.test(u);
  });
  const rootAbsolute = local.filter(function(u){ return /^\/(?!\/)/.test(u); });
  check('ни один ассет не уходит в корень домена', rootAbsolute.length === 0,
    rootAbsolute.join(', '));
  const escaped = local
    .map(function(u){ return new window.URL(u, window.location.href).pathname; })
    .filter(function(p){ return p.indexOf('/vlalchillow/') !== 0; });
  check('все ассеты остаются под /vlalchillow/', escaped.length === 0, escaped.join(', '));

  check('window.__panel есть', !!P);
  if (!P) { report(); return; }

  /* ── цифры с сервера (init уже успел их принести) ── */
  check('онлайн всего с сервера', $('s-total').textContent === '7', $('s-total').textContent);
  check('в лобби с сервера', $('s-lobby').textContent === '2', $('s-lobby').textContent);
  check('в матчах с сервера', $('s-match').textContent === '5', $('s-match').textContent);
  check('комнат из сервера', $('s-rooms').textContent.indexOf('/ 64') >= 0, $('s-rooms').textContent);

  /* ── каталог пришёл с сервера ── */
  P.setMode(MODES.modes);
  check('каталог загружен с /api/modes', P.MODES.length === 3, 'MODES=' + P.MODES.length);
  check('фильтр режимов заполнен', $('f-mode').options.length === 4,
    'options=' + $('f-mode').options.length);
  check('фильтр режимов начинается с «Все режимы»',
    $('f-mode').options[0].value === '' && $('f-mode').selectedIndex === 0,
    $('f-mode').options[0].value);
  check('карточки режимов построены', $('mode-chips').children.length === 3,
    'chips=' + $('mode-chips').children.length);
  const chipRanked = Array.from($('mode-chips').children)
    .find(b => b.textContent.indexOf('Союзники') >= 0);
  check('режим по умолчанию — первый из каталога',
    chipRanked && chipRanked.getAttribute('aria-checked') === 'true');

  /* ── создание без входа: запрос не уходит, уводит на профиль ── */
  posted = null;
  $('room-name').value = 'новая';
  P.createRoom();
  await tick(60);
  check('без входа создание не уходит на сервер', posted === null, JSON.stringify(posted));
  check('без входа сказано, что нужен код',
    $('create-err').textContent.indexOf('код') >= 0, $('create-err').textContent);
  check('без входа страница уводит на профиль',
    String(window.location.hash).indexOf('profile') >= 0, String(window.location.hash));

  /* ── вход по коду (форма профиля) ── */
  meState = { id: 333, nick: 'новичок' };
  await P.loadMe();
  $('pf-code').value = '123456';
  P.pfLogin();
  await tick(120);
  check('вход по коду показал профиль', $('pf-when-auth').hidden === false,
    'hidden=' + $('pf-when-auth').hidden);
  check('вход по коду: ник из лобби', $('pf-nick').textContent === 'новичок',
    $('pf-nick').textContent);
  check('вход по коду: ID из лобби', $('pf-id').textContent.indexOf('333') >= 0,
    $('pf-id').textContent);
  check('статистика профиля с сервера', $('pf-kills').textContent.indexOf('120') >= 0,
    $('pf-kills').textContent);
  check('K/D посчитан', $('pf-kd').textContent === '1.50', $('pf-kd').textContent);

  /* ── комнаты с сервера ── */
  P.setRooms(toPage(roomsState));
  P.switchTab('list');
  check('строк в списке столько же, сколько комнат',
    $('rooms').children.length === 2, 'rows=' + $('rooms').children.length);
  check('номер комнаты на месте', $('rooms').textContent.indexOf('#1') >= 0);
  check('состояние live отрисовано', $('rooms').textContent.indexOf('идёт') >= 0);
  check('состояние waiting отрисовано', $('rooms').textContent.indexOf('ожидание') >= 0);
  check('счёт CT * T показан в списке', $('rooms').textContent.indexOf('5:3') >= 0,
    $('rooms').textContent);
  check('заполненность показана страницей', $('rooms').textContent.indexOf('4 / 4') >= 0,
    $('rooms').textContent);

  /* ── «Войти»: окно, точная команда, автокопирование, вкладка не меняется ── */
  const joinButton = Array.from($('rooms').children)[1].querySelector('.so-join-btn');
  copied = null;
  joinButton.dispatchEvent(new window.Event('click'));
  check('кнопка «Войти» открыла окно', $('join-dialog').hidden === false);
  check('в окне точная команда из /api/rooms', $('join-cmd').value === '/join duel #2',
    $('join-cmd').value);
  check('команда скопирована автоматически', copied === '/join duel #2', String(copied));
  check('сказано про вставку в чат лобби',
    $('join-copy-state').textContent.indexOf('чат лобби') >= 0,
    $('join-copy-state').textContent);
  check('команда доступна для ручного выделения',
    $('join-cmd').readOnly === true && $('join-cmd').value.length > 0);
  check('список комнат остался активен',
    $('play-grid').getAttribute('data-tab') === 'list' &&
    $('tab-list').getAttribute('aria-selected') === 'true');
  $('join-close-btn').dispatchEvent(new window.Event('click'));
  check('окно закрывается кнопкой', $('join-dialog').hidden === true);

  /* ── фильтры ── */
  $('f-mode').value = 'Duel';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('фильтр по режиму сужает список', $('rooms').children.length === 1,
    'rows=' + $('rooms').children.length);

  $('f-mode').value = '';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('сброс фильтра возвращает всё', $('rooms').children.length === 2);

  $('f-map').value = 'Yard';
  $('f-map').dispatchEvent(new window.Event('change'));
  check('фильтр по карте работает', $('rooms').children.length === 1);
  $('f-map').value = '';
  $('f-map').dispatchEvent(new window.Event('change'));

  /* ── нереализованные режимы: приглушены, выбрать нельзя ── */
  var modeCards = Array.prototype.slice.call($('mode-chips').children);
  check('карточек режимов столько же, сколько в каталоге', modeCards.length === 3,
    'cards=' + modeCards.length);
  var offCards = modeCards.filter(function(b){ return /so-mode--off/.test(b.className); });
  check('ровно один режим помечен как недоступный', offCards.length === 1,
    'off=' + offCards.length);
  check('у недоступного режима aria-checked=false',
    offCards[0].getAttribute('aria-checked') === 'false');
  check('по умолчанию выбран реализованный режим',
    modeCards.filter(function(b){ return b.getAttribute('aria-checked') === 'true'; }).length === 1 &&
    !/so-mode--off/.test(modeCards.filter(function(b){ return b.getAttribute('aria-checked') === 'true'; })[0].className));
  offCards[0].dispatchEvent(new window.Event('click'));
  check('клик по недоступному режиму не выбирает его',
    offCards[0].getAttribute('aria-checked') === 'false');
  check('показывается текст «<mode> not implemented»',
    $('mode-note').textContent === 'duel not implemented', $('mode-note').textContent);
  check('кнопка «Создать комнату» остаётся активной', $('create-btn').disabled === false);

  /* ── создание комнаты: один POST, номер от сервера ── */
  P.setRooms([]);
  $('room-name').value = 'новая';
  posted = null;
  $('create-btn').dispatchEvent(new window.Event('click'));
  await tick(120);
  check('создание ушло на сервер', posted !== null, JSON.stringify(posted));
  check('номер комнаты выдал сервер', P.rooms.length === 1 && P.rooms[0].n === 3,
    'rooms=' + P.rooms.length);
  check('результат показан', $('result').hidden === false);
  check('команда из ответа сервера в результате',
    $('result-cmd').value === '/join 2v2 #3', $('result-cmd').value);
  check('поле имени очищено', $('room-name').value === '');

  /* ── ошибки сервера ── */
  P.setRooms([]);
  postResponse = { status: 409, body: { code: 'one_room_per_player', message: 'x' } };
  P.createRoom();
  await tick(120);
  check('409 показан понятным текстом',
    $('create-err').textContent.indexOf('уже есть комната') >= 0, $('create-err').textContent);
  check('после ошибки новых комнат не появилось', P.rooms.length === 0);

  postResponse = { status: 409, body: { code: 'player_offline', message: 'x' } };
  P.createRoom();
  await tick(120);
  check('«не в сети» объяснён по-русски',
    $('create-err').textContent.indexOf('не в сети') >= 0, $('create-err').textContent);

  postResponse = { status: 401, body: { code: 'no_auth', message: 'x' } };
  P.createRoom();
  await tick(120);
  check('401 откатывает страницу к форме входа', $('pf-when-anon').hidden === false);
  check('401 объяснён по-русски',
    $('create-err').textContent.indexOf('код') >= 0, $('create-err').textContent);

  await P.loadMe();
  postResponse = { status: 429, body: { code: 'rate' } };
  P.createRoom();
  await tick(120);
  check('429 объяснён', $('create-err').textContent.indexOf('слишком часто') >= 0,
    $('create-err').textContent);
  postResponse = null;

  /* ── бан: авторизация слетает ── */
  meState = { id: 333, nick: 'новичок' };
  await P.loadMe();
  meState = null;
  authResponse = { status: 403, body: { code: 'banned', message: 'аккаунт забанен' } };
  $('pf-code').value = '123456';
  P.pfLogin();
  await tick(120);
  check('бан при входе показан', $('pf-err').textContent === 'аккаунт забанен',
    $('pf-err').textContent);
  check('после бана профиль скрыт', $('pf-when-auth').hidden === true);
  authResponse = null;

  /* ── «Моя комната» ── */
  meState = { id: 333, nick: 'новичок' };
  await P.loadMe();
  P.setRooms(toPage([
    { number: 1, name: 'чужая', mode: 'Duel', level: 'Yard', players: 1, state: 'waiting',
      owner: 'x', ownerId: 111, joinCommand: '/join duel #1', isMine: false },
    { number: 2, name: 'моя', mode: 'Duel', level: 'Yard', players: 1, state: 'waiting',
      owner: 'новичок', ownerId: 333, joinCommand: '/join duel #2', isMine: true },
  ]));
  P.switchTab('list');
  const rows = Array.from($('rooms').children);
  check('своя комната поднята наверх', rows[0].textContent.indexOf('#2') >= 0,
    rows[0].textContent);
  check('своя комната подписана «моя»', rows[0].textContent.indexOf('моя') >= 0);
  check('у чужой комнаты нет кнопки завершения',
    rows[1].querySelector('.md-end-btn') === null);
  check('у своей комнаты есть «Завершить»',
    !!rows[0].querySelector('.md-end-btn'));

  /* ── завершение своей катки ── */
  ended = null;
  rows[0].querySelector('.md-end-btn').dispatchEvent(new window.Event('click'));
  await tick(120);
  check('завершение уходит на /api/rooms/{n}/end',
    ended && ended.indexOf('api/rooms/2/end') >= 0, String(ended));

  /* ── чужой отказ по завершению объяснён ── */
  endResponse = { status: 403, body: { code: 'not_owner', message: 'это не твоя комната' } };
  P.setRooms(toPage([
    { number: 4, name: 'моя', mode: 'Duel', level: 'Yard', players: 1, state: 'waiting',
      owner: 'новичок', ownerId: 333, joinCommand: '/join duel #4', isMine: true },
  ]));
  P.switchTab('list');
  $('rooms').querySelector('.md-end-btn').dispatchEvent(new window.Event('click'));
  await tick(120);
  check('отказ «не твоя комната» показан',
    $('create-err').textContent.indexOf('не твоя') >= 0, $('create-err').textContent);
  endResponse = null;

  /* ── XSS: имя комнаты экранируется ── */
  P.setRooms(toPage([{ number: 9, name: '<img src=x onerror=alert(1)>', mode: 'Duel',
    level: 'Yard', players: 0, state: 'waiting', owner: 'x' }]));
  P.switchTab('list');
  check('имя комнаты экранировано',
    $('rooms').querySelector('img') === null && $('rooms').textContent.indexOf('<img') >= 0);

  /* ── мёртвая комната не приглашает ── */
  P.setRooms(toPage([{ number: 5, name: '', mode: 'Duel', level: 'Yard', players: 0,
    state: 'finished', owner: 'x' }]));
  P.switchTab('list');
  const deadBtn = $('rooms').querySelector('.so-join-btn');
  check('закрытая комната: кнопка неактивна', deadBtn && deadBtn.disabled === true);
  check('закрытая комната подписана', $('rooms').textContent.indexOf('закрыта') >= 0);

  /* ── потолок комнаты берётся из самой комнаты ── */
  P.setRooms(toPage([{ number: 7, name: 'малая', mode: 'Ranked2v2', level: 'Sandstone 2x2',
    players: 2, state: 'live', owner: 'x', ownerId: 999, joinCommand: '/join 2v2 #7',
    scoreCt: 4, scoreTr: 2, perTeam: 1, maxPlayers: 2, teamLabel: '1 на 1' }]));
  P.switchTab('list');
  const row = $('rooms').textContent;
  check('вместо счётчика игроков показан счёт 4:2', row.indexOf('4:2') >= 0, row);
  check('число мест показано страницей', row.indexOf('2 / 2') >= 0, row);
  check('заполненность посчитана по игрокам', row.indexOf('заполнена') >= 0, row);
  check('подпись размера в списке', row.indexOf('1 на 1') >= 0, row);

  /* ── выбор размера команды ── */
  P.setMode(MODES.modes);
  check('союзники: выбор размера виден', $('team-section').hidden === false);
  check('союзники: два варианта (1 и 2)', $('teams').children.length === 2);
  check('союзники: подписи «1 на 1» и «2 на 2»',
    $('teams').textContent.indexOf('1 на 1') >= 0 &&
    $('teams').textContent.indexOf('2 на 2') >= 0);

  let chosen = Array.from($('teams').children)
    .filter(b => b.getAttribute('aria-checked') === 'true');
  check('союзники: выбран ровно один вариант', chosen.length === 1);
  check('союзники: по умолчанию 2 на 2',
    chosen[0] && chosen[0].textContent === '2 на 2');
  check('союзники: подпись про 4 человека',
    $('team-note').textContent.indexOf('4') >= 0);

  const oneOnOne = Array.from($('teams').children)
    .find(b => b.textContent === '1 на 1');
  oneOnOne.dispatchEvent(new window.Event('click'));
  check('1 на 1: стало выбрано',
    oneOnOne.getAttribute('aria-checked') === 'true' &&
    $('teams').querySelectorAll('[aria-checked="true"]').length === 1);
  check('1 на 1: подпись про 2 человека',
    $('team-note').textContent.indexOf('2') >= 0);

  const chipTdm = Array.from($('mode-chips').children)
    .find(b => b.textContent.indexOf('Командный бой') >= 0);
  chipTdm.dispatchEvent(new window.Event('click'));
  check('tdm: выбор размера скрыт', $('team-section').hidden === true);

  chipRanked.dispatchEvent(new window.Event('click'));
  const again = Array.from($('teams').children)
    .find(b => b.getAttribute('aria-checked') === 'true');
  check('возврат на союзников: выбор помнится',
    again && again.textContent === '1 на 1');

  // Создание комнаты отправляет perTeam (и только авторизованным).
  P.setRooms([]);
  posted = null;
  $('create-btn').dispatchEvent(new window.Event('click'));
  await tick(120);
  check('perTeam уходит на сервер', posted && posted.perTeam === 1, JSON.stringify(posted));

  /* ── баннер «игровой порт не слушается»: только админ и только провал ── */
  P.setRooms([]);
  check('порт в порядке — баннера нет', $('s-warn').hidden === true);
  meState = { id: 999, nick: 'влал', admin: true };
  await P.loadMe();
  statusState = Object.assign({}, statusState, { gamePortFailed: true });
  await tick(2300);  // тик живого обновления
  check('провал порта — баннер показан админу', $('s-warn').hidden === false,
    'hidden=' + $('s-warn').hidden);
  statusState = Object.assign({}, statusState, { gamePortFailed: false });
  await tick(2300);
  check('порт восстановлен — баннер убран', $('s-warn').hidden === true);

  /* ── админка: аноним не видит ничего, админ видит панель ── */
  meState = null;
  await P.loadMe();
  adminState = { ok: true, admin: false, authed: false };
  await P.loadAdminState();
  check('аноним: админка закрыта', $('adm-notadmin').hidden === false);
  check('аноним: панель скрыта', $('adm-panel').hidden === true);
  check('аноним: ника админа в странице нет',
    $('adm-nick').textContent === '' || $('adm-nick').textContent === '—',
    $('adm-nick').textContent);

  meState = { id: 999, nick: 'влал', admin: true };
  await P.loadMe();
  adminState = { ok: true, admin: true, authed: true, id: 999, nick: 'влал', customId: null,
                 bans: { enabled: true, minMinutes: 10, maxDays: 14 } };
  await P.loadAdminState();
  check('админ: панель открыта', $('adm-panel').hidden === false);
  check('админ: ник из сервера', $('adm-nick').textContent === 'влал',
    $('adm-nick').textContent);
  check('админ: диапазон сроков из сервера',
    $('adm-brange').textContent.indexOf('10') >= 0, $('adm-brange').textContent);

  report();

  function report() {
    console.log('\n──────────────────────────────────────────');
    if (fail === 0) console.log('ВСЁ ЗЕЛЁНО: ' + pass + ' проверок');
    else {
      console.log('ПРОВАЛОВ: ' + fail + ' из ' + (pass + fail));
      failures.forEach(f => console.log('  ✗ ' + f));
    }
    console.log('──────────────────────────────────────────');
    window.close();
    process.exit(fail === 0 ? 0 : 1);
  }
})();
