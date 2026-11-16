// Снимки страницы панели для визуальной проверки.
//
// Зачем отдельный файл: в браузере видно то, чего не видно в тестах — севший
// шрифт, слипшиеся блоки, обрезанный текст. Здесь страница открывается в
// настоящем Chromium (в CI он есть) и складывается в PNG, чтобы верстку можно
// было посмотреть глазами, а не догадываться по CSS.
//
//   node preview/shots.mjs [baseUrl] [outDir]
//
// Логин: заглушка (`preview/preview-server.js`) принимает любой 6-значный код,
// поэтому снимки «вошёл» делаются тем же путём, что и у живого человека.

import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const base = (process.argv[2] || 'http://127.0.0.1:8080').replace(/\/$/, '');
const outDir = process.argv[3] || 'preview/shots';

const shots = [
  { name: '01-home-1440', url: '/', w: 1440, h: 900, full: false },
  { name: '02-home-full', url: '/', w: 1440, h: 900, full: true },
  { name: '03-home-mobile', url: '/', w: 390, h: 844, full: false },
  { name: '04-play', url: '/play', w: 1440, h: 900, full: false, login: true },
  { name: '05-admin-gate', url: '/admin', w: 1440, h: 900, full: false, login: true },
  { name: '06-admin-open', url: '/admin', w: 1440, h: 900, full: false, login: true, admin: true },
  { name: '07-play-mobile', url: '/play', w: 390, h: 844, full: false, login: true },
];

const browser = await chromium.launch();
await mkdir(outDir, { recursive: true });

const problems = [];
for (const shot of shots) {
  const context = await browser.newContext({
    viewport: { width: shot.w, height: shot.h },
    deviceScaleFactor: 1,
    locale: 'ru-RU',
  });
  const page = await context.newPage();
  page.on('console', msg => {
    if (msg.type() === 'error') problems.push(`${shot.name}: console ${msg.text()}`);
  });
  page.on('requestfailed', req =>
    problems.push(`${shot.name}: failed ${req.url()} (${req.failure()?.errorText})`));
  page.on('response', res => {
    if (res.status() >= 400) problems.push(`${shot.name}: ${res.status()} ${res.url()}`);
  });

  if (shot.login) {
    await page.request.post(`${base}/api/auth`, { data: { code: '123456' } });
  }
  if (shot.admin) {
    await page.request.post(`${base}/api/admin/auth`, { data: { code: 'PREVIEW' } });
  }

  await page.goto(base + shot.url, { waitUntil: 'networkidle', timeout: 45000 });
  await page.evaluate(() => {
    // Анимации появления гасят прозрачность; для снимка они должны быть уже
    // доиграны, иначе половина блоков выглядит пустой.
    document.querySelectorAll('.reveal').forEach(el => el.classList.add('is-in'));
    document.querySelectorAll('video').forEach(v => v.pause());
  });
  await page.waitForTimeout(900);
  // JPEG, а не PNG: снимки коммитятся в ветку, и 8 PNG по 2 МБ там не нужны.
  const file = path.join(outDir, `${shot.name}.jpg`);
  await page.screenshot({ path: file, fullPage: shot.full, type: 'jpeg', quality: 82 });
  // Что реально применилось: севший шрифт видно по замеренной ширине строки.
  const fonts = await page.evaluate(async () => {
    await document.fonts.ready;
    const h1 = document.querySelector('h1, .hero__title');
    const loaded = [...document.fonts].map(f => `${f.family} ${f.weight} ${f.status}`);
    return {
      heading: h1 ? getComputedStyle(h1).fontFamily : null,
      body: getComputedStyle(document.body).fontFamily,
      loaded,
      oswald: document.fonts.check('700 40px Oswald', 'ПРОВЕРКА'),
    };
  });
  console.log(`${shot.name}: ${file}`, JSON.stringify(fonts));
  // Аннотация прогона: лог из закрытой сети не читается, а это — читается.
  // Здесь именно то, что нельзя проверить тестом: применился ли шрифт.
  const mark = fonts.oswald ? 'ok' : 'FAIL';
  console.log(`::notice title=font ${shot.name}::Bebasbolt=${fonts.oswald} ` +
    `heading=${fonts.heading} body=${fonts.body} loaded=${fonts.loaded.length}`);
  await context.close();
}

await writeFile(path.join(outDir, 'report.json'), JSON.stringify(problems, null, 2));
if (problems.length) {
  console.log('\nПроблемы при загрузке:');
  for (const p of [...new Set(problems)]) console.log(' -', p);
}
await browser.close();
