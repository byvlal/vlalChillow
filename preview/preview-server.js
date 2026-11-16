#!/usr/bin/env node
/**
 * Preview-сервер панели: отдаёт настоящий index.html / site.css / site.js / шрифт,
 * а /api/* мокает теми же JSON-ами, что отдаёт C# WebPanelRouter.
 *
 * Зачем: .NET SDK в песочнице нет, а посмотреть страницу надо сейчас. Это НЕ часть
 * сервера — только стенд для глаз. Запуск:
 *
 *     node preview/preview-server.js            # http://127.0.0.1:8080
 *     PORT=3000 node preview/preview-server.js
 *
 * Отличия от настоящего сервера (осознанные):
 *   • вход по любому 6-значному коду, админ-код — PREVIEW (в бою он в auth.txt);
 *   • комната создаётся в памяти, баны тоже;
 *   • вместо видео-фона — скриншот из docs/screenshots, пока файла нет.
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const WWW = path.join(ROOT, 'server', 'Web', 'wwwroot');
const SHOTS = path.join(ROOT, 'docs', 'screenshots');

const PORT = Number(process.env.PORT || 8080);
const ADMIN_CODE = process.env.ADMIN_CODE || 'PREVIEW';

/* ── состояние стенда ──────────────────────────────────────────────────── */

const MODES = [
  { id: 'Ranked2v2', name: 'Союзники', alias: '2v2', defaultLevel: 'Sandstone 2x2',
    levels: ['Prison 2x2', 'Sandstone 2x2'], aliases: ['2v2'], teamSizes: [1, 2] },
  { id: 'RankedDefuse', name: 'Рейтинговый дефьюз', alias: 'rdef', defaultLevel: 'Sandstone',
    levels: ['Sandstone', 'Prison'], aliases: ['rdef'], teamSizes: [1] },
  { id: 'Defuse', name: 'Дефьюз', alias: 'defuse', defaultLevel: 'Sandstone',
    levels: ['Sandstone', 'Prison'], aliases: ['defuse'], teamSizes: [1] },
  { id: 'Duel', name: 'Дуэль', alias: 'duel', defaultLevel: 'Yard',
    levels: ['Block', 'Yard'], aliases: ['duel'], teamSizes: [1, 2] },
  { id: 'Escalation', name: 'Эскалация', alias: 'esc', defaultLevel: 'Prison',
    levels: ['Prison'], aliases: ['esc'], teamSizes: [1] },
];

const players = [
  { id: 25354325, nick: 'влал', ips: ['192.168.1.10'], online: true },
  { id: 67776161, nick: 'Ktulxy', ips: ['192.168.1.50'], online: true },
  { id: 41120304, nick: 'FinalSapper78', ips: ['192.168.1.71'], online: false },
];

let rooms = [
  { number: 1, mode: 'Ranked2v2', level: 'Sandstone 2x2', name: 'рялна',
    ownerId: 25354325, state: 'live', players: 4, scoreCt: 5, scoreTr: 3, perTeam: 2 },
  { number: 2, mode: 'Duel', level: 'Yard', name: '',
    ownerId: 67776161, state: 'waiting', players: 1, scoreCt: 0, scoreTr: 0, perTeam: 0 },
];
let nextNumber = 3;
let bans = [];
let publicIds = new Map();   // числовой id → строка
const sessions = new Map();  // токен → {id, nick}
const adminTokens = new Set();

/* ── утилиты ───────────────────────────────────────────────────────────── */

const json = (res, status, body) => {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
  });
  res.end(text);
};

const displayId = id => publicIds.get(id) || String(id);

const playerById = id => players.find(p => p.id === id) || null;

function roomJson(room, viewerId) {
  const mode = MODES.find(m => m.id === room.mode) || { name: room.mode };
  const size = room.perTeam ? `${room.perTeam} на ${room.perTeam}` : 'как в режиме';
  return {
    number: room.number,
    mode: room.mode,
    modeName: mode.name,
    level: room.level,
    name: room.name,
    owner: playerById(room.ownerId)?.nick || 'Server',
    ownerId: room.ownerId,
    state: room.state,
    players: room.players,
    scoreCt: room.scoreCt,
    scoreTr: room.scoreTr,
    perTeam: room.perTeam,
    maxPlayers: room.perTeam ? room.perTeam * 2 : 8,
    teamLabel: size,
    joinCommand: `/join ${mode.alias || room.mode} #${room.number}`,
    createdUtc: new Date().toISOString(),
    isMine: viewerId > 0 && room.ownerId === viewerId,
  };
}

const STATUS = {
  onlineLobby: 3, onlineMatch: 5, onlineTotal: 8,
  roomsCount: () => rooms.length, roomsMax: 64, nextNumber: () => nextNumber,
  gameListening: true, matchRunning: false,
  mode: 'Ranked2v2', levels: 'Sandstone 2x2',
  serverUtc: () => new Date().toISOString(),
};

// В live-ответе комнаты считает роутер, здесь — по фактическому списку.
const statusJson = () => JSON.stringify({
  onlineLobby: 3, onlineMatch: 5, onlineTotal: 8,
  roomsCount: rooms.length, roomsMax: 64, nextNumber,
  gameListening: true, matchRunning: false,
  mode: 'Ranked2v2', levels: 'Sandstone 2x2',
  serverUtc: new Date().toISOString(),
});

function parseCookies(header) {
  const out = {};
  (header || '').split(';').forEach(part => {
    const i = part.indexOf('=');
    if (i > 0) out[part.slice(0, i).trim()] = part.slice(i + 1).trim();
  });
  return out;
}

const me = req => {
  const token = parseCookies(req.headers.cookie).sc_session;
  return token ? sessions.get(token) || null : null;
};

const isAdmin = req => adminTokens.has(parseCookies(req.headers.cookie).sc_admin);

/* ── API ───────────────────────────────────────────────────────────────── */

function handleApi(req, res, url) {
  const p = url.pathname;
  const method = req.method;

  let body = '';
  req.on('data', chunk => { body += chunk; });
  req.on('end', () => {
    let data = {};
    try { data = body ? JSON.parse(body) : {}; } catch { data = {}; }

    /* ── вход ── */
    if (p === '/api/auth' && method === 'POST') {
      if (!/^\d{6}$/.test(String(data.code || ''))) {
        return json(res, 401, { code: 'bad_code', message: 'код не существует или неактивен' });
      }
      const who = players[0];
      const token = 'preview-' + Math.random().toString(36).slice(2);
      sessions.set(token, { id: who.id, nick: who.nick });
      res.setHeader('Set-Cookie', `sc_session=${token}; Path=/; HttpOnly; SameSite=Lax`);
      return json(res, 200, { id: who.id, displayId: displayId(who.id), nick: who.nick,
        since: new Date().toISOString() });
    }

    if (p === '/api/logout' && method === 'POST') {
      const token = parseCookies(req.headers.cookie).sc_session;
      if (token) sessions.delete(token);
      adminTokens.clear();
      res.setHeader('Set-Cookie', 'sc_session=; Path=/; Max-Age=0');
      return json(res, 200, { ok: true });
    }

    const session = me(req);

    if (p === '/api/me') {
      if (!session) return json(res, 401, { code: 'no_auth', message: 'нужно ввести код из лобби' });
      return json(res, 200, { id: session.id, displayId: displayId(session.id),
        nick: session.nick, since: new Date().toISOString() });
    }

    if (p === '/api/avatar') {
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      return res.end('нет аватара');
    }

    /* ── данные: без сессии не отдаём ── */
    if (!session) return json(res, 401, { code: 'no_auth', message: 'нужно ввести код из лобби' });

    if (p === '/api/status') {
      res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
      return res.end(statusJson());
    }

    if (p === '/api/modes') return json(res, 200, { modes: MODES });

    if (p === '/api/rooms' && method === 'GET') {
      return json(res, 200, {
        nextNumber, count: rooms.length, maxRooms: 64,
        rooms: rooms.map(r => roomJson(r, session.id)),
      });
    }

    if (p === '/api/rooms' && method === 'POST') {
      if (rooms.some(r => r.ownerId === session.id)) {
        return json(res, 409, { code: 'one_room_per_player', message: 'у этого игрока уже есть комната' });
      }
      const mode = MODES.find(m => m.id === data.mode) || MODES[0];
      const room = {
        number: nextNumber++,
        mode: mode.id,
        level: data.level || mode.defaultLevel,
        name: (data.name || '').slice(0, 40),
        ownerId: session.id,
        state: 'waiting', players: 1, scoreCt: 0, scoreTr: 0,
        perTeam: Number(data.perTeam) || 0,
      };
      rooms.unshift(room);
      return json(res, 201, roomJson(room, session.id));
    }

    const endMatch = p.match(/^\/api\/rooms\/(\d+)\/end$/);
    if (endMatch && method === 'POST') {
      const number = Number(endMatch[1]);
      const room = rooms.find(r => r.number === number);
      if (!room) return json(res, 404, { code: 'no_room', message: 'комнаты нет — обнови' });
      if (room.ownerId !== session.id) {
        return json(res, 403, { code: 'not_owner', message: 'это не твоя комната' });
      }
      rooms = rooms.filter(r => r.number !== number);
      return json(res, 200, { ok: true, number });
    }

    /* ── админка ── */
    if (p === '/api/admin/me') {
      return json(res, 200, {
        admin: isAdmin(req), adminId: true, id: session.id,
        displayId: displayId(session.id), nick: session.nick,
      });
    }

    if (p === '/api/admin/auth' && method === 'POST') {
      if (String(data.code || '').trim().toUpperCase() !== ADMIN_CODE.toUpperCase()) {
        return json(res, 403, { code: 'bad_admin_code',
          message: 'код не подошёл — он меняется при каждом запуске сервера (смотри auth.txt рядом с config.json)' });
      }
      const token = 'adm-' + Math.random().toString(36).slice(2);
      adminTokens.add(token);
      res.setHeader('Set-Cookie', `sc_admin=${token}; Path=/; HttpOnly; SameSite=Lax`);
      return json(res, 200, { admin: true, adminId: true, id: session.id, nick: session.nick });
    }

    if (p === '/api/admin/logout' && method === 'POST') {
      adminTokens.clear();
      res.setHeader('Set-Cookie', 'sc_admin=; Path=/; Max-Age=0');
      return json(res, 200, { ok: true });
    }

    if (p.startsWith('/api/admin/')) {
      if (!isAdmin(req)) {
        return json(res, 403, { code: 'admin_code_required',
          message: 'id админский, но код этого запуска не предъявлен — введи код из auth.txt рядом с config.json' });
      }

      if (p === '/api/admin/bans' && method === 'GET') {
        return json(res, 200, { bans: bans.map(b => ({ ...b, displayId: displayId(b.id) })) });
      }

      if (p === '/api/admin/bans' && method === 'POST') {
        const target = playerById(Number(data.id));
        if (!target) {
          return json(res, 400, { code: 'ban_refused',
            message: `id #${data.id} сервер не знает — человек ещё не заходил в лобби` });
        }
        if (target.id === session.id) return json(res, 400, { code: 'ban_refused', message: 'себя банить нельзя' });
        const seconds = { '30m': 1800, '6h': 21600, '1d': 86400, '3d': 259200, '7d': 604800, '14d': 1209600 }[data.duration] || 259200;
        const record = {
          id: target.id, nick: target.nick, reason: data.reason || 'без причины',
          bannedBy: session.nick, expires: new Date(Date.now() + seconds * 1000).toISOString(),
          remainingSec: seconds, duration: data.duration || '3d',
        };
        bans = bans.filter(b => b.id !== target.id).concat([record]);
        console.log(`[preview] бан: ${target.nick} на ${record.duration} (${record.reason}) от ${session.nick}`);
        return json(res, 201, record);
      }

      if (p === '/api/admin/unban' && method === 'POST') {
        const before = bans.length;
        bans = bans.filter(b => b.id !== Number(data.id));
        if (before === bans.length) {
          return json(res, 400, { code: 'unban_refused', message: `под id #${data.id} активных банов нет` });
        }
        return json(res, 200, { ok: true, removed: before - bans.length });
      }

      if (p === '/api/admin/players') {
        const id = Number(url.searchParams.get('id') || 0);
        const target = playerById(id);
        if (!target) return json(res, 200, { known: false, id, displayId: String(id) });
        return json(res, 200, {
          known: true, id,
          displayId: displayId(id),
          nick: target.nick,
          banned: bans.some(b => b.id === id),
          online: target.online,
          ips: target.ips,
          lastSeen: new Date().toISOString(),
        });
      }

      if (p === '/api/admin/ids') return json(res, 200, { ids: [25354325] });

      if (p === '/api/admin/status') {
        return json(res, 200, {
          status: JSON.parse(statusJson()), bans: bans.length, adminIds: [25354325],
        });
      }

      if (p === '/api/admin/public-id' && method === 'POST') {
        const targetId = Number(data.id) || session.id;
        const value = String(data.value || '').trim();
        if (!value) {
          publicIds.delete(targetId);
          return json(res, 200, { ok: true, id: targetId, displayId: String(targetId) });
        }
        if (value.length < 2) {
          return json(res, 400, { code: 'bad_public_id', message: 'публичный id короче 2 символов' });
        }
        if (!/^[A-Za-z0-9]+$/.test(value)) {
          return json(res, 400, { code: 'bad_public_id',
            message: 'в публичном id только латинские буквы и цифры, без пробелов и спецсимволов' });
        }
        if (/^\d+$/.test(value)) {
          return json(res, 400, { code: 'bad_public_id',
            message: 'публичный id не может состоять только из цифр — это чужие числовые id' });
        }
        for (const [owner, taken] of publicIds) {
          if (owner !== targetId && taken.toLowerCase() === value.toLowerCase()) {
            return json(res, 400, { code: 'bad_public_id',
              message: `публичный id '${value}' уже занят другим игроком` });
          }
        }
        publicIds.set(targetId, value);
        return json(res, 200, { ok: true, id: targetId, displayId: value });
      }

      return json(res, 404, { code: 'not_found', message: 'нет такого маршрута' });
    }

    json(res, 404, { code: 'not_found', message: 'нет такого маршрута' });
  });
}

/* ── статика ───────────────────────────────────────────────────────────── */

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.woff2': 'font/woff2',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
};

function serveFile(res, filePath, extraHeaders) {
  fs.readFile(filePath, (err, bytes) => {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      return res.end('404');
    }
    const type = TYPES[path.extname(filePath).toLowerCase()] || 'application/octet-stream';
    res.writeHead(200, Object.assign({
      'Content-Type': type,
      'Content-Length': bytes.length,
      'Cache-Control': 'no-store',
    }, extraHeaders || {}));
    res.end(bytes);
  });
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const p = url.pathname;

  if (p.startsWith('/api/')) return handleApi(req, res, url);

  if (p === '/' || p === '/index.html' || p === '/play' || p === '/admin') {
    return serveFile(res, path.join(WWW, 'index.html'));
  }

  // Заглушка вместо видео: пока файла нет, показываем скриншот проекта,
  // чтобы было видно, как ляжет кадр под заголовок (в бою здесь hero.mp4).
  // Проверяем до общей статики: иначе чтение несуществующего файла съест запрос.
  if (p === '/assets/media/hero.jpg' || p === '/assets/media/hero.png') {
    return serveFile(res, path.join(SHOTS, 'bomb-defused-mvp.png'));
  }

  if (p === '/assets/media/hero.mp4' || p === '/assets/media/hero.webm') {
    res.writeHead(404, { 'Content-Type': 'text/plain' });
    return res.end('нет видео');
  }

  if (p.startsWith('/assets/')) {
    const rel = p.replace(/^\/assets\//, '');
    const file = path.join(WWW, 'assets', rel);
    if (file.startsWith(path.join(WWW, 'assets'))) return serveFile(res, file);
  }

  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
  res.end('404');
});

server.listen(PORT, '0.0.0.0', () => {
  console.log(`[preview] панель: http://127.0.0.1:${PORT}/`);
  console.log(`[preview] вход по любому 6-значному коду, админ-код: ${ADMIN_CODE}`);
});
