# vlalChillow — ВЕСЬ КОНТЕКСТ ПРОЕКТА (обновлено 17.11.2026)

> Файл-«страховка»: тут всё, что нужно, чтобы продолжить проект с нуля, не потеряв стиль
> и договорённости. Владелец проекта: vlal (Telegram https://t.me/vlalikoffcChillow,
> GitHub https://github.com/byvlal/vlalChillow). Отвечать пользователю по-русски, коротко,
> показывать скриншоты после визуальных правок, работать быстро и без лишних вопросов.

## 1. Что это

- **vlalChillow** — свой сервер для **StandChillow** (C# / .NET 8,
  LAN/dedicated). Работает и в локальной сети, и **снаружи** (подключение по IP).
- Задача: веб-панель сервера, перестроенная в стиль интерфейса **StandChillow**.
- Состав сайта (одностраничник + роутер по хэшам):
  - `#/` — лендинг (видео-hero, «о проекте», 8 шагов, репорты, «прямо сейчас», подвал);
  - `#/docs` — полная инструкция со скриншотами (для «совсем тупых», шаг за шагом)
    + sticky сайдбар-оглавление (десктоп слева, мобайл — свёрнутый режим);
  - `#/play` — страницы комнат (панель so-*, работает с сервером);
  - `#/profile` — личный кабинет: K/D-пончик, убийства/смерти/время/уникальные матчи
    (статистика живёт на сервере, `stats.json`);
  - `#/admin` — админ панель (только для `admin.ids`: вход по коду из auth.txt,
    кастомизация своего ID, баны с живым превью и объявлением в чат лобби);
  - на каждой странице — кнопка «ПАНЕЛЬ» → правый выезжающий drawer (аккаунт/вход по коду,
    Профиль, Админ панель — видна только админам, Играть, Репорты, Выйти);
  - `#/how`, `#/reports` — якоря лендинга (остались для совместимости).
- Примерка вариантов дизайна: `http://host:8080/?select` (+`title=1|2|3`,
  `accent=blue|orange|yellow`, `btn=blue|white`) — панелька справа снизу.

## 2. Стиль — 1:1 UI StandChillow

Принципы: **никакого неона/фиолетового/AI-слоупа**. Тёмный фон `#0b141c`, полупрозрачные
панели, дисплейный шрифт Bebasbolt, текст Roboto (как в игре). Акценты — цвета команд игры.

Токены (`:root` в index.html):
- фоны: `--so-bg:#0b141c`, `--so-bg-2:#0e1d29`, `--so-bg-3:#13293a`;
- панель `--so-panel:rgba(12,27,39,.55)`; линии `--so-line:rgba(154,196,226,.14)`,
  `--so-line-strong:rgba(154,196,226,.32)`;
- текст `--so-text:#e9f0f6`, глухой `--so-muted:#90a7b8`;
- CT-синий `--so-ct:#8fb4e8` (акцент по умолчанию), T-оранж `--so-t:#e8a33d`,
  HP-жёлтый `--so-hp:#f0c33c`, красный `--so-red:#c03b3b`, золото `--so-gold:#d9a441`;
- `--so-acc` = акцент (переключается `html[data-accent]`), `--so-acc-ink` — тёмный текст на акценте;
- шрифты: `--so-font` (RobotoV), `--so-display` (Bebasbolt).

### Шрифты (отдаются сервером, /media/*.woff2, вкомпилированы в сборку)
- **Bebasbolt** — свободный шрифт (Wanja01 & Snowy),
  Wanja01 & Snowy, лицензия **CC-BY-4.0** (упоминание в подвале обязательно!):
  https://github.com/Wanja01YT/bebasbolt. Файл `Bebasbolt-1.1.4-release.woff2`.
- **Roboto** — «тот самый» шрифт игры; 6 файлов: 400/500/700 × cyrillic/latin
  (`roboto-400-cyrillic.woff2` и т.д.), подключены через `unicode-range`.
- В `<head>` — preload двух главных woff2, `theme-color:#0b141c`,
  title «влалChillow — свой сервер для StandChillow».

### Классы so-* (система главной и доки)
- верхняя планка `.so-top` (absolute над видео) и `.so-top--static` (sticky, для #/docs);
  лого `.so-top__logo` (b — акцент), nav-ссылки Bebasbolt 17px, точки онлайна `.so-online__dot`
  (+`--off` красная, когда игровой порт не слушает).
- hero `.so-hero`: видео `.so-hero__video` (autoplay muted loop playsinline, poster
  `/media/poster.jpg`), затемнение `.so-hero__scrim` (двойной градиент), внутренний блок
  `.so-hero__inner`; overline `.so-hero__over` (12px, letter-spacing .34em);
  h1 Bebasbolt clamp(62px,10.5vw,148px), `<span>` — акцент; подзаголовок `.so-hero__sub`.
  Три варианта заголовка `.t1/.t2/.t3` переключаются `html[data-title]`.
- кнопки: `.so-btn--play` (светло-голубая плашка, градиент #b6cbe7→#9db6d7, тёмный текст;
  `html[data-btn="white"]` — белая), `.so-btn--ghost` (тёмная полупрозрачная с рамкой);
  кликабельный текст `.so-howlink` (акцент + стрелка, hover — белая и стрелка уезжает);
  живой статус `.so-hero__live` («СЕРВЕР В СЕТИ · N ОНЛАЙН»);
  стрелка «листай» `.so-cue` (пружинка).
- бегущая строка режимов `.so-marquee` — JS заполняет из каталога сервера (MODES).
- секции `.so-sec` (max 1240px, паддинг 96px), метка `.so-sec__label` (Bebasbolt 15px,
  letter-spacing .34em, линия после), h2 Bebasbolt clamp(42px,6vw,76px), `em` — акцент.
- манифест `.so-manifesto` (крупный серый, ключевые слова белым через `b`);
  факты `.so-facts__row` (сетка 190px/1fr/auto: ключ — описание — значение акцентом).
- шаги на главной `.so-steps`/`.so-step` (сетка 120px/1fr/auto, номер обводкой
  `-webkit-text-stroke`, заголовок Bebasbolt 27px, описание muted, ссылка `.so-step__a`);
  метки групп `.so-how-group` (оранжевый ромб `◆` + Bebasbolt 18px, letter-spacing .24em).
- репорты `.so-report` (панель + список + кнопка Telegram), статус `.so-now` (4 ячейки,
  Bebasbolt 58px), подвал `.so-foot` (ссылки с svg, бренд, лицензионный абзац `.so-foot__credit`).
- **страница #/docs**: заголовок `.so-docs-head h1` (+`.so-docs-sub`), шаг-инструкция
  `.so-doc` (голова: номер `.so-doc__n` 56px обводкой + название `.so-doc__t` 29px + текст
  `.so-doc__d`; ниже фигура `.so-doc__fig`: скрин во всю ширину с рамкой/радиусом 10px и
  подписью figcaption с оранжевым ромбом), группы шагов — те же `.so-how-group`;
  «если что-то не так» — `.so-trouble__row` (симптом слева Bebasbolt, ответ справа);
  репорт-блок и подвал переиспользуются как на главной; скриншоты `loading="lazy"`.

## 3. Тексты (тональность: свой, честный, без «пинговых» обещаний)

- hero overline: «Онлайн от комьюнити для StandChillow»; h1 по умолчанию (t1):
  «СВОЯ КАТКА. СВОЙ СЕРВЕР.»; sub: «Свои комнаты, свои правила. Сервер стоит на домашнем
  ПК — подключайся по IP, из дома или извне.»
- манифест: один ПК в квартире; подключение по IP и порту 7778 — из дома и извне.
- факты: ВХОД (IP : 7778), КОМНАТЫ (ДО 64), РЕЖИМЫ (ВСЕ), РЕПОРТЫ (24/7).
- **LAN-запрещённое слово на сайте** — не пишем «LAN-сервер/LAN-хост/локалку/пинг ~1мс».
  Исключение: реальное имя вкладки в игре **«LAN Мультиплеер»** — это UI игры, так и пишем.
- игроки подключаются **и из локалки, и снаружи** — обещаний нулевого пинга нет.
- подвал: Telegram + GitHub + кредит Bebasbolt (CC-BY-4.0) — обязателен на каждой вью.

## 4. Инструкция (8 шагов) — канон

В игре: 1) вкладка «LAN Мультиплеер» → 2) «Присоединиться» → 3) «Ввести IP Адрес» →
4) IP + порт **7778** (пример 1.2.3.4 : 7778; IP может смениться — свежий в Telegram) →
5) «Подключиться» → 6) код в чате лобби («Ты <ник> · ID: … · код: 123456», живёт 30 мин,
новый — `/code`).
На сайте: 7) «НАЧАТЬ ИГРУ» (не авторизован → форма входа) → 8) вбить код.
Плюс на #/docs: создать свою комнату (вкладка «Создать комнату», четыре шага:
режим → игроки в команде (если режим их выбирает) → карта → название, можно не писать),
войти в чужую («Публичные комнаты» → «Войти» → команда `/join … #номер` копируется →
вставить в чат лобби), troubleshooting («если что-то не так»), репорт-блок, подвал.
Везде универсально — примеров одного режима (`/join tdm #1`) в текстах нет.
Пример реального подключения владельца: IP 1.2.3.4, лобби-порт 7778
(матч-порт UDP 7777, discovery 5056).

## 5. Скриншоты инструкции (`server/Web/Media/doc-*.jpg`)

- Игровые (прислал владелец, исходники — у владельца, на них уже нарисованы номера шагов): сконвертированы
  `ffmpeg -i in.jpg -vf scale=1280:-2 -q:v 4 out.jpg` →
  **doc-1.jpg** (меню/вкладка), **doc-2.jpg** («Присоединиться»), **doc-3.jpg**
  («Ввести IP Адрес»), **doc-45.jpg** (IP+порт+«Подключиться», используется для шагов 04-05),
  **doc-6.jpg** (чат с кодом 550877). Размер каждой **1280×582**.
- Сайтовые (сняты с реального UI через браузер, есть демо-данные — ок для инструкции):
  **doc-7.jpg** (hero с кнопкой «НАЧАТЬ ИГРУ»), **doc-8.jpg** (форма входа «Код из лобби»),
  **doc-create.jpg** (вкладка «Создать комнату» — визард на чипах: режим/игроков в команде/
  карта/название), **doc-rooms.jpg** (публичные комнаты), **doc-join.jpg** (окно входа
  с шагами 01-03). Размеры 1280×886. Скрины снимаются `browser.screenshot` (вьюпорт
  1280×886) и копируются как есть (уже JPEG). Демо-данные ставятся оверрайдом
  `window.fetch` (осторожно: `api()` зовёт `fetch('api/rooms')` **без слэша** — матчить
  по `indexOf('api/...')`, не по `/api/...`); `showJoinModal` принимает объект комнаты
  (r.cmd или joinCmd(r)), НЕ строку.
  Скрины кладутся в **два места**: `server/Web/Media/` (для embed при пересборке) и
  `server/bin/Debug/net8.0/webmedia/` (диск — работает без пересборки).
  ВАЖНО: сломанный lazy-img не перезапрашивается — если скрин добавлен после открытия
  страницы, вкладку надо перезагрузить.
- Медиа-сервер: `/media/<name>` — сначала файл с диска `webmedia/` рядом с exe
  (`MediaAssets.DiskDirectory = AppContext.BaseDirectory + "webmedia"`), иначе из embed;
  Range-запросы 206/416, защита от path traversal (имена без `/`), кэш-заголовки.
- csproj embed: `Web\Media\*.mp4;*.jpg;*.png;*.woff2`, LogicalName
  `web.media.%(Filename)%(Extension)` — новые jpg подхватываются при пересборке.

## 6. Архитектура сервера

- `server/StandChillow.LanServer.csproj` — всё в одном: игровой сервер + веб-панель.
- Веб: `server/Web/wwwroot/index.html` (весь сайт, **вкомпилирован в сборку**),
  `server/Web/WebPanelRouter*.cs` (роутер: страницы, API, /media),
  `server/Web/WebPanelHost.cs` (опции-лямбды, обе сессии), `server/Web/MediaAssets.cs` (медиа),
  `server/Web/WebPanelRouter.Auth.cs` (сессии сайта), `server/Web/WebPanelRouter.Admin.cs`
  (админ эндпоинты + тройной шлюз AdminGate).
- API панельки: статус/онлайн, каталог режимов, комнаты (создание, список, вход),
  вход по коду из лобби, `GET /api/stats/me` (401 без сессии), `api/me` отдаёт поле `admin`.
  Админ: `GET /api/admin` (анонимно 200 `{ok,admin:false}` без данных), `POST /api/admin/login`
  (код из auth.txt, кука sc_admin живёт до рестарта), `POST /api/admin/logout`,
  `GET /api/admin/lookup?id=` (превью для бана), `POST /api/admin/customid`,
  `POST /api/admin/ban` / `unban`, `GET /api/admin/bans`, `GET /api/admin/avatar`.
  Не-админ получает 403 `not_admin` на всё, кроме анонимного GET. Публикуется на
  **http://192.168.1.65:8080/** (локально 127.0.0.1).
- Админ-инфраструктура: `AdminSessions.cs`/`AdminCookie.cs` (кука `sc_admin`, память,
  умирает с рестартом), `Net/GameNetHost.AdminAuth.cs` (`IsAdminLive` читает живой
  `_config` — хот-релоад безопасен; `AdminCodeMatches` — константное сравнение кода),
  `Net/CustomIdStore.cs` + `GameNetHost.CustomIds.cs` (кастомные ID → `customids.json`,
  паттерн `^[A-Za-z0-9]{1,24}$`, уникальность без регистра; подмена — в
  `GameMatchHost.PublicIds.cs`, оригинальный числовой ID нигде не теряется),
  верификация админов форсируется в `GameMatchHost.Properties.cs` (спуф `isVerified`
  не-админом перезаписывается), веб-баны — `GameNetHost.WebAdmin.cs` + `GameNetHost.Bans.cs`
  (`TryBanTarget(bannedBy)`, `ResolveBanTargetById`, `RemoveBansById`), объявление —
  тот же `BroadcastServerChat`, что и у внутриигрового бана.
- Галочка `verified` (проп `verified`, `MatchRoomPropKeys.Verified`) — только сервер:
  1) релей свойств (`GameMatchHost.Properties.cs`) переписывает на лету любое клиентское
  значение: админ (id из `admin.ids` + код запуска, `IsAdminVerifiedFor` =
  `IsAdminAuthorized`) → `true`, все остальные → `false`;
  2) при бутстрапе `EnsureServerVerifiedProp` (`GameMatchHost.PublicIds.cs`, вызов в конце
  `SendWorldBootstrap` — покрывает joiner-ready / soft-timeout / reconnect-trusted) сам
  досылает решение, даже если клиент проп не присылал вовсе; бродкаст только если
  сохранённое значение отсутствует/отличается, у актора-хоста (1/'Server') остаётся `false`.
  E2E-подтверждено пробами: ProbeAdm (не-админ) → `verified … false`; «влал» (админ с кодом)
  → `verified … true (админ)`, клиент получает соответствующее значение в снапшоте.
- Статистика: `server/Stats/PlayerStatsStore.cs` (диск `stats.json` рядом с exe, атомарная
  запись tmp+replace, дебаунс 10 с + Flush на Stop), хуки убийств/смертей в
  `GameMatchHost.DeathMatchCombat.cs` / `.DuelCombat.cs` / `.Ranked2v2Combat.cs` и
  `GameMatchHost.Stats.cs` (снапшот комнат каждые `StatsSampleSeconds=15` с, фазы
  «играет» 3,4,5,6,8,9,12,13,14,15; «матч закончен» 0,7,10,11). Уникальность матча —
  ключ `MatchPublicId.ForNick(nick)`, повторный вход в ту же катку не считается.
- Порты игры: матч 7777/udp, discovery 5056, лобби 7778.

## 7. JS-контракты (нельзя ломать)

- Имена id/классов, которые трогает JS: `view-home`, `view-docs`, `view-play`,
  `view-profile`, `view-admin`, `hero-video`, `so-dot`/`so-dot-hero`/`so-dot-docs`/`so-dot-play`/
  `so-dot-profile`/`so-dot-admin`, `so-hero-status`, `so-hero-count`, `so-marquee`, `n-online`,
  `n-lobby`, `n-match`, `n-rooms`, `join-dialog`, `join-cmd`, `join-copy-state`,
  `join-copy-btn`, `join-close-btn`, `s-*` (статы),
  гейт комнат `play-gate`/`play-main` (`gate-go` — ссылка на `#/profile`,
  `gate-how` → `gotoDoc('doc-6')`: цель ждёт в `docJumpTarget` и скроллится
  в `applyRoute` docs-ветке; переключение гейта — `renderPlayGate()` из
  `renderProfile()` и `applyRoute`, синхронно с `me`),
  сетка `play-grid` с `data-tab="create|list"` (≥981px видны обе панели,
  ниже — мини-табы `tab-create`/`tab-list` переключают их через `switchTab()`,
  `hidden` больше не используется),
  `panel-create` (карточки режимов `mode-chips`/`.so-mode`, `mode-note`,
  `team-section`/`teams`/`team-note`, плитки карт `maps`/`.so-map` c `data-map`,
  `map-note`, `room-name` — input вешает превью, `create-preview`
  (`updateCreatePreview()`: режим ◆ размер ◆ карта ◆ имя), `create-btn`,
  `create-err`, `result`/`result-cmd`/`copy-btn`; `renumberSteps()` и подписи
  `label-map`/`label-name` удалены — шаги не нумерованы),
  `panel-list` (`f-count` — только число, фильтры `f-mode`/`f-map`, `rooms` с
  делегированием `[data-empty-create]` → `switchTab('create')`), drawer (`so-scrim`, `side-acc`/`side-anon`,
  `side-avatar`/`side-fb`, `side-nick`/`side-id`, `side-admin`, `side-profile`,
  `side-play`, `side-reports`, `side-logout`, `side-code`/`side-login-btn`/
  `side-auth-err`), профиль (`pf-code`/`pf-btn`/`pf-err`, `pf-nick`/`pf-id`,
  `pf-avatar`/`pf-avatar-fb`, дуга K/D `pf-arc-k`/`pf-arc-d`/`pf-kd` r=58 viewBox
  140, C≈364.4, `pf-kills`/`pf-deaths`/`pf-time`/`pf-matches`, легенды
  `pf-kills-leg`/`pf-deaths-leg`), docs (`data-goto`, `doc-1..doc-b`, `doc-trouble`,
  `doc-reports`, scroll-margin-top 84/118px), админка (`adm-notadmin`/`adm-notadmin-d`,
  `adm-auth`/`adm-code`/`adm-login`/`adm-auth-err`, `adm-panel`, `adm-avatar`/`adm-avatar-fb`/
  `adm-nick`/`adm-id`/`adm-logout`, вкладки `adm-tab-custom`/`adm-tab-bans`, кастомизация
  `adm-cid`/`adm-cid-save`/`adm-cid-clear`/`adm-cid-now`/`adm-cid-err`, баны `adm-bstep-1..3`/
  `adm-bstep1..3`/`adm-bid`/`adm-bnext`/`adm-bprev`/`adm-berr`/`adm-bconfirm`/`adm-byes`/
  `adm-bback`/`adm-breason`/`adm-bdur`/`adm-bunit`/`adm-ban-go`/`adm-brange`/`adm-ban-err`/
  `adm-bdone`/`adm-bans`/`adm-bans-empty`).
- Выбор режима — карточки `.so-mode` (`#mode-chips`, состояние `curMode`),
  НЕ `<select id="mode">` и не чипы (селект остался только у фильтра списка `f-mode`).
- Формы входа на странице комнат больше нет: неавторизованным `#/play` показывает
  только гейт (`renderPlayGate()`); `createRoom()` при `!me` уводит на `#/profile`.
- Роутер: `currentRoute()` парсит `location.hash` (strip `^#\/?`); `applyRoute()` — вью
  home/docs/play/profile/admin; `#/admin` каждый раз переспрашивает `GET api/admin`
  (сессия могла умереть с рестартом); якоря how/reports скроллят к `#sec-*`;
  `docJumpTarget` — глубокая ссылка в доки (гейт → шаг 06), скролл в docs-ветке.
- Видео: играет только когда hero на экране + вкладка активна + нет prefers-reduced-motion
  (`syncHeroVideo`, IntersectionObserver, visibilitychange); параллакс
  translateY = 0.22×scrollY, scale(1.14), через rAF.
- `loadModes` грузится первым, потом `loadMe`/`loadRooms`/`loadStatus`; поллинг 2 с,
  перерисовывает список только когда `playView` виден; `loadStatus(res)` вызывает
  `renderHomeStatus(res)`; `renderStats()` → `renderHomeNow()`
  (hoisting function declarations — работает).
- Баннер `s-warn` — только для админов и только при `api/status → gamePortFailed=true`
  (реальный провал перебинда UDP 7777). `gameListening=false` — норма: после конца
  последней катки порт паркуют намеренно (`SuspendMatchListen`), следующая комната
  поднимет его сама (`EnsureMatchListening`); флаг `gamePortFailed` возвращает
  `GameMatchHost.ListenFailed`, снимается при любом успехе/парковке.
- `showJoinModal(r)` — принимает объект комнаты; `escHtml` для вставки имён в marquee.
- Экспорт для демо/тестов: `window.__panel` — старое (setRooms, renderRooms, switchTab,
  createRoom, setTheme, joinCmd, describeError, showJoinModal, closeJoinModal, login,
  logout, loadMe, renderProfile, endRoom, setMe, setMode) плюс новое: `sideLogin`,
  `pfLogin`, `loadStats`, `renderProfileView`, `renderSide`, `openSide`, `closeSide`,
  `loadAdminState`, `renderAdmin`, `adminLogin`, `adminLogout`, `switchAdminTab`,
  `adminSaveCid`, `adminFilterCid`, `adminLookupBan`, `adminBanNext`, `adminBanGo`,
  `adminBanStep`, `loadAdminBans`, `renderPlayGate`, `updateCreatePreview`, `gotoDoc`.
  ВНИМАНИЕ: `loadRooms`/`loadStatus`/`loadModes` в экспорт не входят.

## 8. Цикл пересборки (ВАЖНО — грабли)

1. Убить процесс: `Get-Process -Name "StandChillow.LanServer","dotnet" | Stop-Process -Force`
   (иначе MSB3027 — файл занят).
2. `dotnet build server/StandChillow.LanServer.csproj -v q`.
3. Запуск в фоне: `dotnet run --project server/StandChillow.LanServer.csproj --no-build`
   (порт 8080).
4. **index.html вкомпилирован** → любые правки HTML требуют пересборки.
   Медиа из `webmedia/` (диск рядом с exe) можно менять без пересборки — диск приоритетнее.
5. ffmpeg: `ffmpeg` на машине владельца (`$ff\ffmpeg.exe`, `$ff\ffprobe.exe`).

## 9. Документация проекта

- `docs/DESIGN.md` — спецификация дизайн-системы so-* (обновлять при изменениях).
- `docs/WEB_PANEL.md` — API, конфиг, медиа-сервер, маршруты (таблица роутов).

## 10. Роадмап (дальше по порядку)

1. ~~Админ-панель~~ — **готово** (17.11.2026): `#/admin` — тройной шлюз (сессия сайта →
   живой `admin.ids` → код запуска из auth.txt в куке `sc_admin`), вкладки «Кастомизация»
   (свой отображаемый ID, `customids.json`) и «Баны» (3 шага: ввод ID с живым превью →
   «всё верно?» → причина+срок; список активных с разбаном; объявление в чат лобби —
   то же, что у игрового бана). E2E пройден: шлюзы 401/403/405, хот-релоад админа,
   бан+кик+объявление, разбан, отказ админ/себя/ниже минимума/неизвестный id.
2. **Страница репортов** (`#/reports` сейчас якорь на главной → потом отдельная вью).
3. Контент главной страницы — владелец обещал прислать своё описание («я поясню»).
4. Статистика уже живёт на сервере (kills/deaths/KD/часы/уникальные матчи, `stats.json`).

## 11. Мелочи, которые легко потерять

- В `#/play` демо-данные не оставлять: после снятия скринов страница перезагружалась начисто.
- Копия скринов браузера лежит во временном каталоге — имена директорий НЕ
  соответствуют смыслу, смысл определяется содержимым (проверять глазами).
- Бегущая строка — не декорация, она из каталога сервера (MODES), с escHtml.
- `?select` — только для примерки; параметры через `html[data-*]` + history.replaceState.
- Фавикон/шрифты/медиа кэшируются на год — при смене файлов менять имена.
- Пользователь общается коротко: «показывай скрины, делай, не спрашивай по мелочи».
