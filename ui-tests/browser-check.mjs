// Real Edge/Chrome integration checks. Uses the already-installed taste skill CDP driver.
import { createServer } from 'node:http';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
const skillRoot = process.env.VOLUME_GUARD_TASTE_SKILL || resolve(process.env.USERPROFILE || '', '.agents/skills/design-taste-frontend');
const { Browser } = await import(pathToFileURL(resolve(skillRoot, 'scripts/lib/cdp.mjs')));
const out = resolve(import.meta.dirname);
const html = readFileSync(resolve(import.meta.dirname, '../server/VolumeGuard/Web/config.html'), 'utf8');
const config = { enabled: true, defaultMode: 'Auto', daytimeMode: 'Balanced', timeZoneId: 'America/New_York', scheduleEnabled: true, nightWindows: [{ days: [0,1,2,3,4,5,6], start: '22:00', end: '07:00' }], balanced: { targetRmsDb: -20, maxGainDb: 6, limiterCeilingDb: -1, attackMs: 10, releaseMs: 500 }, night: { targetRmsDb: -26, maxGainDb: 9, limiterCeilingDb: -4, attackMs: 5, releaseMs: 800 } };
// Preview-only approximation of the host dashboard's inherited styles, never shipped with the plugin.
const hostStyle = `<style>body{margin:0;background:#101010;color:#eee;font:16px Arial,sans-serif}input,select{background:#252525;color:inherit;border:1px solid #777;padding:.5em}button{background:#333;border:0;color:inherit;padding:.7em 1em;cursor:pointer}button[type=submit]{background:#007a9f;color:#fff}button:disabled{opacity:.5}#vg-error{color:#ffafaf}summary{font-weight:600}fieldset{color:inherit}h1,h2{font-weight:600}</style>`;
const playbackStatus = {serverTimeUtc:'2026-10-24T12:00:00Z',devices:[{deviceId:'chromecast-id',deviceName:'Bedroom Chromecast',client:'Moonfin',mode:'Night',processing:true,reason:'Audio filter launched.',dialogue:'3 dB',analysis:'Cached title measurement'}],analysis:{summary:'1 title measured.'}};
const mock = `<script>window.requests=[];window.failGet=0;window.failStatus=0;window.failPost=0;window.saved=null;window.ApiClient={getUrl:function(path){return '/jellyfin/'+path},ajax:async function(options){requests.push(options);if(options.url==='/jellyfin/VolumeGuard/status'){if(failStatus)throw{status:failStatus};return ${JSON.stringify(playbackStatus)}}if(options.url!=='/jellyfin/VolumeGuard/configuration')throw new Error('Base path lost');if(options.type==='GET'){if(failGet)throw{status:failGet};return ${JSON.stringify(config)}}if(failPost)throw{status:failPost};saved=JSON.parse(options.data);}};</script>`;
const preview = html.replace('</head>', hostStyle + mock + '</head>').replace('</body>', `<script>document.getElementById('volumeGuardPage').dispatchEvent(new Event('viewshow'));</script></body>`);
writeFileSync(resolve(out, 'preview.html'), preview);
const server = createServer((req, res) => { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end(preview); });
await new Promise(r => server.listen(0, '127.0.0.1', r));
const browser = await Browser.launch();
try {
  const page = await browser.newPage();
  await page.goto(`http://127.0.0.1:${server.address().port}/jellyfin/web/configurationpage`);
  assert.equal(await page.eval(() => document.getElementById('vg-zone').value), config.timeZoneId);
  assert.equal(await page.eval(() => document.getElementById('vg-save').disabled), false);
  assert.equal(await page.eval(() => requests[0].dataType), 'json');
  assert.equal(await page.eval(() => document.getElementById('vg-server').checked), false, 'server processing requires opt-in');
  await page.eval(() => {document.getElementById('vg-server').checked=true;document.getElementById('vg-server').dispatchEvent(new Event('input',{bubbles:true}));});
  // Existing edits survive host navigation events and a cancelled reload.
  await page.eval(() => { const input = document.getElementById('vg-zone'); input.value = 'Europe/London'; input.dispatchEvent(new Event('input', {bubbles:true})); document.getElementById('volumeGuardPage').dispatchEvent(new Event('viewshow')); document.getElementById('vg-reload').click(); });
  assert.equal(await page.eval(() => document.getElementById('vg-zone').value), 'Europe/London');
  assert.equal(await page.eval(() => requests.filter(r=>r.url.endsWith('/configuration')).length), 1);
  // Status refresh must not overwrite unsaved configuration or clear its warning.
  await page.eval(() => document.getElementById('vg-refresh').click());
  assert.equal(await page.eval(() => document.getElementById('vg-zone').value), 'Europe/London');
  assert.equal(await page.eval(() => document.getElementById('vg-dirty').hidden), false);
  assert.match(await page.eval(() => document.getElementById('vg-sessions').textContent), /Bedroom Chromecast.*Processing launched/s);
  assert.equal(await page.eval(() => document.querySelector('#vg-known-devices option').value), 'chromecast-id');
  // New controls and per-device overrides round-trip through the same explicit Save.
  await page.eval(() => {
    document.getElementById('vg-dialogue').value='4.5';document.getElementById('vg-analysis').checked=true;document.getElementById('vg-lufs').value='-22';document.getElementById('vg-switch').checked=true;
    document.getElementById('vg-add-device').click();let row=document.querySelector('.vg-device');row.querySelector('.vg-device-deviceId').value='chromecast-id';row.querySelector('.vg-device-deviceName').value='Bedroom';row.querySelector('.vg-device-mode').value='Night';row.querySelector('.vg-device-dialogueBoostDb').value='3';
  });
  // Add/remove and overnight start-day serialization.
  await page.eval(() => { document.getElementById('vg-add').click(); document.querySelectorAll('.vg-window')[1].querySelector('button').click(); const days = document.querySelectorAll('.vg-window .vg-day'); days.forEach((day,i) => day.checked = i === 5); document.getElementById('vg-form').requestSubmit(); });
  assert.deepEqual(await page.eval(() => saved.nightWindows), [{days:[5],start:'22:00',end:'07:00'}]);
  assert.equal(await page.eval(() => saved.timeZoneId), 'Europe/London');
  assert.equal(await page.eval(() => saved.serverProcessingEnabled), true, 'server processing toggle is saved');
  assert.equal(await page.eval(() => saved.dialogueBoostDb), 4.5);
  assert.equal(await page.eval(() => saved.loudnessAnalysisEnabled), true);
  assert.equal(await page.eval(() => saved.analysisTargetLufs), -22);
  assert.equal(await page.eval(() => saved.switchDuringPlayback), true);
  assert.deepEqual(await page.eval(() => saved.deviceOverrides), [{deviceId:'chromecast-id',deviceName:'Bedroom',enabled:true,mode:'Night',dialogueBoostDb:3}]);
  assert.equal(await page.eval(() => requests.at(-1).contentType), 'application/json');
  assert.equal(await page.eval(() => requests.at(-1).dataType || null), null, '204 save must not parse JSON');
  // Validation and errors never clear entries or mark unsuccessful writes saved.
  await page.eval(() => { failPost=403; const zone=document.getElementById('vg-zone');zone.value='UTC';zone.dispatchEvent(new Event('input',{bubbles:true}));document.getElementById('vg-form').requestSubmit(); });
  assert.match(await page.eval(() => document.getElementById('vg-error').textContent), /administrator/);
  assert.equal(await page.eval(() => document.getElementById('vg-zone').value), 'UTC');
  assert.equal(await page.eval(() => document.getElementById('vg-dirty').hidden), false);
  await page.eval(() => {document.querySelector('.vg-end').value='22:00';document.getElementById('vg-form').requestSubmit();});
  assert.match(await page.eval(() => document.getElementById('vg-error').textContent), /different start and end/);
  await page.eval(() => {document.querySelector('.vg-end').value='07:00';document.getElementById('vg-add-device').click();let row=document.querySelectorAll('.vg-device')[1];row.querySelector('.vg-device-deviceId').value='chromecast-id';document.getElementById('vg-form').requestSubmit();});
  assert.match(await page.eval(() => document.getElementById('vg-error').textContent), /only one override/);
  await page.eval(() => {document.querySelectorAll('.vg-device')[1].querySelector('button').click();failStatus=403;document.getElementById('vg-refresh').click();});
  assert.match(await page.eval(() => document.getElementById('vg-status-error').textContent), /administrator/);
  assert.match(await page.eval(() => document.getElementById('vg-sessions').textContent), /Bedroom Chromecast/);
  // Take screenshots from successfully reloaded real controls, across responsive bands.
  await page.eval(() => {window.confirm=()=>true;failPost=0;failStatus=0;document.getElementById('vg-reload').click();document.getElementById('vg-refresh').click();});
  for (const width of [1440,768,390]) {
    await page.setViewport({width,height:900});
    assert.equal(await page.eval(() => document.documentElement.scrollWidth <= innerWidth), true, `no overflow at ${width}`);
    await page.screenshot({path:resolve(out,`settings-${width}.png`),format:'png',fullPage:true});
  }
  await page.eval(() => {failGet=401;document.getElementById('vg-reload').click();});
  assert.match(await page.eval(() => document.getElementById('vg-error').textContent), /administrator/);
  assert.equal(await page.eval(() => document.getElementById('vg-zone').value), config.timeZoneId);
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.eval(() => { failGet=503; document.getElementById('vg-reload').click(); });
  assert.match(await page.eval(() => document.getElementById('vg-error').textContent), /Could not load/);
  assert.deepEqual(page.consoleErrors, []);
  console.log('PASS: settings/status URLs, 204 save, dialogue/analysis/switch fields, device overrides and duplicate validation, refresh preserves unsaved edits and last good status, overnight windows, admin failures, responsive widths; no browser exceptions.');
} finally { await browser.close(); server.close(); }
