/**
 * Проверка живой страницы панели (server/Web/wwwroot/index.html) в jsdom.
 *
 * Сеть не нужна: fetch замокан ответами, которые отдаёт WebPanelRouter, поэтому тест
 * проверяет ровно то, что сломается в браузере, — до того, как это увидит игрок:
 *   • без входа сервер не отдаёт данные, страница показывает экран входа;
 *   • каталог режимов и карт берётся с сервера, а не из копии в JS;
 *   • фильтры режима и карты работают;
 *   • создание комнаты уходит одним POST, номер выдаёт сервер;
 *   • ошибки API показываются понятным текстом, а не кодом;
 *   • чужой ввод экранируется (XSS из ника/названия комнаты);
 *   • /play и /admin — отдельные экраны, доступные по адресу;
 *   • админка без ключа не показывает ни банов, ни id (сервер отвечает 403);
 *   • бан спрашивает подтверждение, и «да» недоступно три секунды;
 *   • публичный id: только латиница и цифры, пустое значение снимает подмену;
 *   • видео-фон не ломает страницу, если файла нет.
 *
 * jsdom ставится отдельно (страница — единственный JS в проекте):
 *     npm install --prefix /tmp/paneltest jsdom
 *     NODE_PATH=/tmp/paneltest/node_modules node tests/panel/panel.test.js
 */
const fs = require('fs');
const path = require('path');
const { JSDOM } = require('jsdom');

const root = path.join(__dirname, '..', '..');
const html = fs.readFileSync(path.join(root, 'server', 'Web', 'wwwroot', 'index.html'), 'utf8');
const js = fs.readFileSync(path.join(root, 'server', 'Web', 'wwwroot', 'assets', 'site.js'), 'utf8');
const css = fs.readFileSync(path.join(root, 'server', 'Web', 'wwwroot', 'assets', 'site.css'), 'utf8');
const css2 = fs.readFileSync(path.join(root, 'server', 'Web', 'wwwroot', 'assets', 'site.extras.css'), 'utf8');

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
      teamSizes: [1, 2] },
    { id: 'Duel', name: 'Дуэль', alias: 'duel', defaultLevel: 'Yard',
      levels: ['Block', 'Yard'], aliases: ['duel'], teamSizes: [1, 2] },
    { id: 'DeathMatch', name: 'Командный бой', alias: 'tdm', defaultLevel: 'Arena',
      levels: ['Arena'], aliases: ['tdm'], teamSizes: [] },
  ],
};

let roomsState = [
  { number: 1, mode: 'Ranked2v2', modeName: 'Союзники', level: 'Sandstone 2x2', name: 'рялна',
    owner: 'vlal', ownerId: 111, state: 'live', players: 4, joinCommand: '/join 2v2 #1',
    scoreCt: 5, scoreTr: 3, perTeam: 2, maxPlayers: 4, teamLabel: '2 на 2', isMine: false },
  { number: 2, mode: 'Duel', modeName: 'Дуэль', level: 'Yard', name: '', owner: 'botx',
    ownerId: 222, state: 'waiting', players: 1, joinCommand: '/join duel #2',
    scoreCt: 1, scoreTr: 0, isMine: false },
];

let statusState = {
  onlineLobby: 2, onlineMatch: 5, onlineTotal: 7,
  roomsCount: 2, roomsMax: 64, nextNumber: 3, gameListening: true,
};

const ME = { id: 333, displayId: 'vlal', nick: 'новичок', since: '2026-11-16T00:00:00Z' };
const ADMIN_ME = { admin: true, adminId: true, id: 333, nick: 'новичок' };

let authOn = false;
let adminOn = false;
let posted = null;
let ended = null;
let authResponse = null;
let adminAuthResponse = null;
let banPost = null;
let unbanPost = null;
let publicIdPost = null;
let playerResponse = null;
const calls = [];

function fakeFetch(url, opts) {
  const method = (opts && opts.method) || 'GET';
  const u = String(url);
  calls.push(method + ' ' + u);
  let body = null, status = 200;

  const needAuth = () => {
    if (authOn) return false;
    status = 401;
    body = { code: 'no_auth', message: 'нужно ввести код из лобби' };
    return true;
  };

  /* Порядок важен: «…/api/rooms/1/end» тоже начинается с api/rooms. */
  if (/\/end$/.test(u) && method === 'POST') {
    ended = u;
    body = { ok: true, number: 1 };
  } else if (u.indexOf('api/admin/auth') >= 0 && method === 'POST') {
    if (adminAuthResponse) { body = adminAuthResponse.body; status = adminAuthResponse.status; }
    else if (!authOn) { status = 401; body = { code: 'no_auth', message: 'нужно ввести код из лобби' }; }
    else { adminOn = true; body = ADMIN_ME; }
  } else if (u.indexOf('api/admin/me') >= 0) {
    if (!authOn) { status = 401; body = { code: 'no_auth', message: 'нужно ввести код из лобби' }; }
    else body = { admin: adminOn, adminId: true, id: 333, nick: 'новичок' };
  } else if (u.indexOf('api/admin/players') >= 0) {
    if (!adminOn) { status = 403; body = { code: 'admin_required', message: 'это админский раздел' }; }
    else if (playerResponse) body = playerResponse;
    else body = { known: true, id: 67776161, displayId: 'Ktulxy', nick: 'Ktulxy',
      banned: false, online: true, ips: ['192.168.1.50'], lastSeen: '2026-11-16T00:00:00Z' };
  } else if (u.indexOf('api/admin/bans') >= 0 && method === 'POST') {
    if (!adminOn) { status = 403; body = { code: 'admin_required', message: 'это админский раздел' }; }
    else {
      banPost = JSON.parse(opts.body);
      body = { nick: 'Ktulxy', reason: banPost.reason, bannedBy: 'новичок',
        expires: '2026-11-19T00:00:00Z', duration: '3д', remainingSec: 259200 };
      status = 201;
    }
  } else if (u.indexOf('api/admin/bans') >= 0) {
    if (!adminOn) { status = 403; body = { code: 'admin_required', message: 'это админский раздел' }; }
    else body = { bans: [{ id: 67776161, displayId: 'Ktulxy', nick: 'Ktulxy', reason: 'читы',
      bannedBy: 'новичок', expires: '2026-11-19T00:00:00Z', remainingSec: 90000, duration: '3д' }] };
  } else if (u.indexOf('api/admin/unban') >= 0) {
    if (!adminOn) { status = 403; body = { code: 'admin_required', message: 'это админский раздел' }; }
    else { unbanPost = JSON.parse(opts.body); body = { ok: true, removed: 1 }; }
  } else if (u.indexOf('api/admin/public-id') >= 0) {
    if (!adminOn) { status = 403; body = { code: 'admin_required', message: 'это админский раздел' }; }
    else { publicIdPost = JSON.parse(opts.body); body = { ok: true, id: 333, displayId: publicIdPost.value || '333' }; }
  } else if (u.indexOf('api/admin/logout') >= 0) {
    adminOn = false;
    body = { ok: true };
  } else if (u.indexOf('api/auth') >= 0 && method === 'POST') {
    if (authResponse) { body = authResponse.body; status = authResponse.status; }
    else { authOn = true; body = ME; }
  } else if (u.indexOf('api/me') >= 0) {
    if (!authOn) { status = 401; body = { code: 'no_auth', message: 'нужно ввести код из лобби' }; }
    else body = ME;
  } else if (u.indexOf('api/logout') >= 0) {
    authOn = false; adminOn = false;
    body = { ok: true };
  } else if (u.indexOf('api/avatar') >= 0) { body = null; status = 404; }
  else if (u.indexOf('api/modes') >= 0) {
    if (needAuth()) { /* 401 */ } else body = MODES;
  } else if (u.indexOf('api/rooms') >= 0 && method === 'GET') {
    if (needAuth()) { /* 401 */ }
    else body = { nextNumber: 3, count: roomsState.length, maxRooms: 64, rooms: roomsState };
  } else if (u.indexOf('api/rooms') >= 0 && method === 'POST') {
    if (needAuth()) { /* 401 */ }
    else {
      posted = JSON.parse(opts.body);
      body = { number: 3, mode: 'Ranked2v2', modeName: 'Союзники', level: 'Sandstone 2x2',
        name: 'новая', owner: 'новичок', ownerId: 333,
        joinCommand: '/join 2v2 #3', state: 'waiting',
        perTeam: 2, maxPlayers: 4, teamLabel: '2 на 2' };
      status = 201;
    }
  } else if (u.indexOf('api/status') >= 0) {
    if (needAuth()) { /* 401 */ } else body = statusState;
  } else { body = null; status = 404; }

  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    text: () => Promise.resolve(body === null ? '' : JSON.stringify(body)),
  });
}

(async () => {
  /* ── разметка и стили ── */
  check('нет надписи «демо/макет»', !/демо|макет/i.test(html), 'нашлось «демо» или «макет»');
  check('есть видео-фон', html.indexOf('hero__video') >= 0);
  const videoTag = (html.match(/<video[\s\S]*?>/) || [''])[0];
  check('видео без звука и с автоплеем',
    /autoplay/.test(videoTag) && /muted/.test(videoTag) && /playsinline/.test(videoTag),
    videoTag.slice(0, 120));
  check('нет слова controls у фонового видео', !/controls/.test(videoTag));
  check('фон обрезается по краям (object-fit: cover)',
    /object-fit:\s*cover/.test(css2), 'нет object-fit: cover для .hero__video');
  check('фон центрирован (object-position)',
    /object-position:\s*center/.test(css2));
  check('видео — только на самом верху (в hero, не фон body)',
    css.indexOf('hero__video') < 0);
  check('шрифт вшит локально, без внешнего CDN',
    /@font-face/.test(css) && /bebasbolt\.woff2/.test(css) && !/fonts\.googleapis/.test(html + css));
  check('стили в стиле игры: Bebas/Eurostile в комментарии',
    /Bebas Neue/.test(css) && /Eurostile/.test(css));
  // [hidden] в браузере слабее любого нашего display: без явного правила
  // экран входа висел поверх админки — это ловилось только глазами.
  check('CSS уважает hidden (иначе скрытые экраны висят поверх)',
    /\[hidden\]\s*\{[^}]*display:\s*none\s*!important/.test(css));
  check('есть боковая панель профиля', html.indexOf('class="sidebar"') >= 0);
  check('кнопки «Начать игру» и «Создать комнату»',
    html.indexOf('Начать игру') >= 0 && html.indexOf('Создать комнату') >= 0);

  const dom = new JSDOM(html, {
    runScripts: 'outside-only',
    pretendToBeVisual: true,
    url: 'https://example.com/',
    beforeParse(window) {
      window.fetch = fakeFetch;
      Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
      Object.defineProperty(window.navigator, 'clipboard', {
        configurable: true,
        value: { writeText: text => { window.__copied = text; return Promise.resolve(); } },
      });
      window.matchMedia = () => ({ matches: false, addListener() {}, removeListener() {} });
      window.scrollTo = () => {};
      window.HTMLElement.prototype.scrollIntoView = () => {};
      window.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    },
  });

  const { window } = dom;
  window.eval(js);

  await new Promise(r => setTimeout(r, 150));

  const P = window.__panel;
  const $ = id => window.document.getElementById(id);
  const text = id => ($(id) ? $(id).textContent : '');

  check('window.__panel есть', !!P);
  if (!P) { report(); return; }

  /* ── без входа: данные скрыты, показан вход ── */
  // Гость видит главную — она открыта намеренно, чтобы было куда ввести код.
  // Экран входа появляется только на /play и /admin (проверяется ниже).
  check('гостю открыта главная', $('view-home').hidden === false);
  check('экран входа не перекрывает главную', $('view-gate').hidden === true);
  check('в шапке гость', text('me-nick') === 'гость', text('me-nick'));

  // /play гостю закрыт — вот где нужен вход.
  window.history.pushState({}, '', '/play');
  await P.navigate();
  check('/play гостю показывает вход', $('view-gate').hidden === false);
  window.history.pushState({}, '', '/');
  await P.navigate();
  check('с главной вход убирается', $('view-gate').hidden === true);
  check('онлайн скрыт без сессии', text('live-text') === 'скрыто', text('live-text'));
  check('статус не запрашивался без сессии (нет 401-шторма)',
    !calls.some(c => c === 'GET api/status'));

  /* ── вход по коду ── */
  $('auth-code').value = '123456';
  $('auth-form').dispatchEvent(new window.Event('submit'));
  await new Promise(r => setTimeout(r, 200));

  check('после входа главная показана', $('view-home').hidden === false);
  check('экран входа скрыт', $('view-gate').hidden === true);
  check('ник с сервера в шапке', text('me-nick') === 'новичок', text('me-nick'));
  check('публичный id (displayId) в шапке', text('me-id') === 'vlal', text('me-id'));

  /* ── каталог и комнаты ── */
  check('каталог загружен с /api/modes', P.state.modes.length === 3, 'modes=' + P.state.modes.length);
  check('селект режимов заполнен', $('c-mode').options.length === 3);
  check('фильтр режимов начинается с «Все режимы»',
    $('f-mode').options[0].value === '' && $('f-mode').selectedIndex === 0);
  check('карты пришли из режима', $('c-map').options.length === 2);

  check('комнаты загружены', P.state.rooms.length === 2, 'rooms=' + P.state.rooms.length);
  check('комнат в списке столько же, сколько пришло',
    $('rooms-list').children.length === 2);
  check('номер комнаты на месте', text('rooms-list').indexOf('#1') >= 0);
  check('состояние live отрисовано', text('rooms-list').indexOf('идёт') >= 0);
  check('счёт CT * T показан', text('rooms-list').indexOf('5') >= 0 && text('rooms-list').indexOf('3') >= 0);
  check('онлайн в шапке с сервера', text('live-text') === '7 онлайн', text('live-text'));
  check('герой-счётчик онлайна с сервера', text('hero-online') === '7');

  /* ── XSS: чужой ник не должен стать разметкой ── */
  roomsState = roomsState.concat([{
    number: 9, mode: 'Duel', modeName: '<img src=x onerror=alert(1)>',
    level: 'Yard', name: '<script>alert(2)</script>', owner: '<b>злой</b>',
    ownerId: 999, state: 'waiting', joinCommand: '/join duel #9',
    scoreCt: 0, scoreTr: 0, isMine: false,
  }]);
  await P.loadRooms();
  check('XSS из названия комнаты не стал тегом',
    $('rooms-list').querySelectorAll('script').length === 0 &&
    $('rooms-list').querySelectorAll('img').length === 0);
  roomsState = roomsState.slice(0, 2);
  await P.loadRooms();

  /* ── фильтры ── */
  $('f-mode').value = 'Duel';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('фильтр по режиму сужает список', $('rooms-list').children.length === 1,
    'rows=' + $('rooms-list').children.length);

  $('f-mode').value = '';
  $('f-mode').dispatchEvent(new window.Event('change'));
  check('сброс режима возвращает всё', $('rooms-list').children.length === 2);

  $('f-map').value = 'Yard';
  $('f-map').dispatchEvent(new window.Event('change'));
  check('фильтр по карте работает', $('rooms-list').children.length === 1);
  $('f-map').value = '';
  $('f-map').dispatchEvent(new window.Event('change'));

  /* ── «Войти» в комнату ── */
  const joinBtn = $('rooms-list').children[1].querySelector('button');
  window.__copied = null;
  joinBtn.dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 30));
  check('окно входа открылось', $('modal-join').classList.contains('is-open'));
  check('команда из сервера в окне', $('join-cmd').value === '/join duel #2',
    $('join-cmd').value);
  check('команда скопирована автоматически', window.__copied === '/join duel #2',
    String(window.__copied));

  /* ── создание комнаты ── */
  $('c-name').value = 'новая';
  $('create-form').dispatchEvent(new window.Event('submit'));
  await new Promise(r => setTimeout(r, 120));
  check('создание ушло одним POST', posted !== null && posted.mode === 'Ranked2v2',
    JSON.stringify(posted));
  check('название ушло как есть', posted && posted.name === 'новая');

  /* ── /play по адресу ── */
  check('routeForPath /play', P.routeForPath('/play') === 'play');
  check('routeForPath /admin', P.routeForPath('/admin') === 'admin');
  check('routeForPath /', P.routeForPath('/') === 'home');

  /* ── админка: без ключа не показывает данные ── */
  adminOn = false;
  window.history.pushState({}, '', '/admin');
  await P.navigate();
  await new Promise(r => setTimeout(r, 80));
  check('/admin открывает экран админки', $('view-admin').hidden === false);
  check('без ключа просится код', $('modal-admin').hidden === false ||
    $('modal-admin').classList.contains('is-open'));

  /* ── админка: неверный код ── */
  const authLog = [];
  const realFetch = window.fetch;
  window.fetch = (u, o) => { authLog.push((o && o.method || 'GET') + ' ' + u); return realFetch(u, o); };

  adminAuthResponse = { body: { code: 'bad_admin_code', message: 'код не подошёл' }, status: 403 };
  $('admin-code').value = 'wrong';
  $('admin-auth-form').dispatchEvent(new window.Event('submit'));
  await new Promise(r => setTimeout(r, 80));
  check('неверный код показан текстом', text('admin-auth-note').indexOf('не подошёл') >= 0,
    text('admin-auth-note'));
  adminAuthResponse = null;

  /* ── админка: верный код ── */
  $('admin-code').value = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
  $('admin-auth-form').dispatchEvent(new window.Event('submit'));
  await new Promise(r => setTimeout(r, 150));
  check('верный код открывает админку', $('modal-admin').classList.contains('is-open') === false);
  check('состояние админа получено', P.state.admin && P.state.admin.admin === true);

  /* баны: поиск цели */
  $('ban-id').value = '67776161';
  $('ban-find').dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 80));
  check('цель бана найдена: ник виден', text('ban-preview').indexOf('Ktulxy') >= 0,
    text('ban-preview'));
  check('кнопка «Забанить» доступна после поиска', $('ban-submit').disabled === false);

  /* подтверждение: три секунды */
  $('ban-reason').value = 'читы';
  $('ban-submit').dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 30));
  check('спросили «точно баним?»', text('ban-confirm').indexOf('Точно баним') >= 0,
    text('ban-confirm'));
  const yesBtn = $('ban-confirm').querySelector('.btn--danger');
  check('«да» заблокировано сразу', !!yesBtn && yesBtn.disabled === true);
  check('видно, кто забанил, а не «сервер»', text('ban-confirm').indexOf('новичок') >= 0);

  /* ждать три секунды в тесте нельзя — проверяем, что таймер вообще заведён */
  check('на кнопке идёт обратный отсчёт', /\(\d\)/.test(yesBtn.textContent), yesBtn.textContent);

  /* баним напрямую через функцию страницы (как будто отсчёт прошёл) */
  await P.submitBan(67776161, '3d', 'читы');
  await new Promise(r => setTimeout(r, 80));
  check('бан ушёл на сервер', banPost && banPost.id === 67776161 && banPost.duration === '3d',
    JSON.stringify(banPost));
  check('причина ушла на сервер', banPost && banPost.reason === 'читы');

  /* список банов */
  P.selectAdminTab('bans');
  await new Promise(r => setTimeout(r, 100));
  check('баны загружены', text('bans-list').indexOf('Ktulxy') >= 0, text('bans-list'));
  check('срок бана по-человечески', /д|ч|м/.test(text('bans-list')));

  /* разбан */
  const unbanBtn = $('bans-list').querySelector('button');
  unbanBtn.dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 100));
  check('разбан ушёл с id', unbanPost && unbanPost.id === 67776161, JSON.stringify(unbanPost));

  /* публичный id */
  P.selectAdminTab('id');
  $('id-value').value = 'vlal';
  $('id-save').dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 100));
  check('публичный id сохранён', publicIdPost && publicIdPost.value === 'vlal',
    JSON.stringify(publicIdPost));

  $('id-clear').dispatchEvent(new window.Event('click'));
  await new Promise(r => setTimeout(r, 100));
  check('пустое значение снимает подмену', publicIdPost && publicIdPost.value === '');

  /* ── выход гасит и админку ── */
  await P.api('POST', 'api/logout', {});
  await P.api('POST', 'api/admin/logout', {});
  check('после выхода админ-ключ снят', adminOn === false);

  /* ── подписи на кнопках: без undefined/NaN ── */
  check('на странице нет undefined', html.indexOf('undefined') < 0);
  check('нет английского «demo»', !/demo/i.test(html));

  report();

  function report() {
    console.log('\n' + '─'.repeat(50));
    if (fail === 0) {
      console.log('ВСЁ ЗЕЛЕНО: ' + pass + ' проверок');
    } else {
      console.log('ПРОВАЛЕНО: ' + fail + ' из ' + (pass + fail));
      for (const f of failures) console.log('  ✗ ' + f);
      process.exitCode = 1;
    }
    console.log('─'.repeat(50) + '\n');

    // Страница держит setInterval автообновления, поэтому без явного выхода
    // процесс не завершится никогда.
    process.exit(fail === 0 ? 0 : 1);
  }
})();
