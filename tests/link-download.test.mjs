import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, link, readFile, readdir, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const root = resolve(import.meta.dirname, '..');
const dll = process.env.VIDCROPPER_TEST_DLL || join(root, 'artifacts/backend-build/VidCropper.dll');
const headers = { 'X-VidCropper': '1', 'Content-Type': 'application/json' };
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function launch(cwd, extra = []) {
  const child = spawn('dotnet', [dll, '--port', '0', ...extra], { cwd, windowsHide: true });
  const exited = new Promise(resolve => child.on('exit', resolve));
  let logs = '';
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error(logs)); }, 15000);
    child.stdout.on('data', chunk => { logs += chunk; const match = logs.match(/VidCropper: (http:\/\/127\.0\.0\.1:\d+)/); if (match) { clearTimeout(timer); resolve(match[1]); } });
    child.stderr.on('data', chunk => { logs += chunk; });
    child.on('exit', code => { clearTimeout(timer); reject(new Error(`Exit ${code}: ${logs}`)); });
  });
  return { url, async stop() { await fetch(url + '/api/shutdown', { method: 'POST', headers }); await exited; } };
}
async function json(url, path, body) {
  const response = await fetch(url + path, { method: body === undefined ? 'GET' : 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  assert.ok(response.ok, JSON.stringify(data));
  return data;
}

test('link API is lazy, validates input and protects mutations', { timeout: 30000 }, async () => {
  const folder = await mkdtemp(join(tmpdir(), 'VidCropper-link-empty-'));
  const app = await launch(folder);
  try {
    assert.equal((await json(app.url, '/api/link-tools')).ytDlpInstalled, false);
    assert.deepEqual(await readdir(folder), [], 'readiness must not create tools or download anything');
    for (const url of ['file:///C:/video.mp4', '--exec calc', 'https://user:password@example.com/video']) {
      const response = await fetch(app.url + '/api/link-downloads/' + crypto.randomUUID(), { method: 'POST', headers, body: JSON.stringify({ url }) });
      assert.equal(response.status, 400);
    }
    const absent = await fetch(app.url + '/api/link-downloads/' + crypto.randomUUID(), { method: 'POST', headers, body: JSON.stringify({ url: 'https://example.com/video' }) });
    assert.equal(absent.status, 409);
    for (const height of [0, -1, 40000, '1080p']) {
      const invalid = await fetch(app.url + '/api/link-downloads/' + crypto.randomUUID(), { method: 'POST', headers, body: JSON.stringify({ url: 'https://example.com/video', height }) });
      assert.equal(invalid.status, 400);
    }
    const untrusted = await fetch(app.url + '/api/link-tools/' + crypto.randomUUID() + '/install', { method: 'POST' });
    assert.equal(untrusted.status, 403);
    assert.deepEqual(await readdir(folder), []);
  } finally { await app.stop(); await rm(folder, { recursive: true, force: true }); }
});

test('real yt-dlp: compatible MP4, conversion, ranges, cancellation, errors and size limit', { timeout: 180000 }, async t => {
  const installed = await readdir(join(root, 'tools')).catch(() => []);
  if (!installed.includes('yt-dlp.exe') || !installed.includes('deno.exe')) { t.skip('Install yt-dlp and Deno through the app first; this test never downloads tools.'); return; }
  const folder = await mkdtemp(join(tmpdir(), 'VidCropper-link-integration-'));
  await mkdir(join(folder, 'tools'));
  for (const name of ['yt-dlp.exe', 'deno.exe']) await link(join(root, 'tools', name), join(folder, 'tools', name));
  const source = join(folder, 'fixture.mp4');
  const webm = join(folder, 'fixture.webm');
  for (const [file, codec] of [[source, 'libx264'], [webm, 'libvpx-vp9']]) {
    const result = spawnSync('ffmpeg', ['-v','error','-f','lavfi','-i','testsrc2=size=320x180:rate=24','-t','2','-c:v',codec,'-y',file], { windowsHide: true, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
  }
  const mp4 = await readFile(source), vp9 = await readFile(webm);
  const hls = new Map();
  for (const height of [360, 1080]) {
    const result = spawnSync('ffmpeg', ['-v','error','-f','lavfi','-i',`color=size=${height * 16 / 9}x${height}:rate=24`,'-t','1','-c:v','libx264','-f','hls','-hls_list_size','0','-hls_segment_filename',join(folder,`${height}-%d.ts`),join(folder,`${height}.m3u8`)], { windowsHide:true, encoding:'utf8' });
    assert.equal(result.status,0,result.stderr);
    hls.set(`/${height}.m3u8`, await readFile(join(folder,`${height}.m3u8`)));
    hls.set(`/${height}-0.ts`, await readFile(join(folder,`${height}-0.ts`)));
  }
  // Deliberately advertise a different high-resolution codec to test that H.264 preference cannot beat resolution.
  // The small fixture payloads use H.264; real VP9 conversion is independently exercised by fixture.webm.
  hls.set('/master.m3u8', Buffer.from('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360,CODECS="avc1.64001e"\n360.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080,CODECS="vp09.00.40.08"\n1080.m3u8\n'));
  const fullHd = join(folder,'fullhd.mp4');
  const remux = spawnSync('ffmpeg',['-v','error','-i',join(folder,'1080-0.ts'),'-c','copy',fullHd],{windowsHide:true,encoding:'utf8'});
  assert.equal(remux.status,0,remux.stderr);
  hls.set('/fullhd.mp4',await readFile(fullHd));
  hls.set('/download',await readFile(fullHd));
  const portrait = join(folder,'portrait.mp4');
  const odd = spawnSync('ffmpeg',['-v','error','-f','lavfi','-i','color=size=810x1442:rate=24','-t','1','-vf','setsar=811/810:max=10000','-c:v','libx264',portrait],{windowsHide:true,encoding:'utf8'});
  assert.equal(odd.status,0,odd.stderr);
  hls.set('/portrait.mp4',await readFile(portrait));
  const http = createServer((request, response) => {
    if (request.url === '/redirect') { response.writeHead(302,{Location:'/download'}); response.end(); return; }
    if (hls.has(request.url)) {
      const data = hls.get(request.url);
      response.writeHead(200, { 'Content-Type':request.url.endsWith('.m3u8') ? 'application/vnd.apple.mpegurl' : (request.url.endsWith('.mp4') || request.url === '/download') ? 'video/mp4' : 'video/mp2t', 'Content-Length':data.length });
      response.end(request.method === 'HEAD' ? undefined : data); return;
    }
    if (!['/fixture.mp4','/fixture.webm','/slow.mp4'].includes(request.url)) { response.writeHead(404); response.end(); return; }
    const slow = request.url === '/slow.mp4';
    const data = request.url.endsWith('.webm') ? vp9 : mp4;
    response.writeHead(200, { 'Content-Type': request.url.endsWith('.webm') ? 'video/webm' : 'video/mp4', 'Content-Length': slow ? 50000000 : data.length });
    if (request.method === 'HEAD') { response.end(); return; }
    if (!slow) { response.end(data); return; }
    response.write(data);
    const timer = setInterval(() => response.write(Buffer.alloc(1024)), 100);
    response.on('close', () => clearInterval(timer));
  });
  await new Promise(resolve => http.listen(0, '127.0.0.1', resolve));
  const remote = `http://127.0.0.1:${http.address().port}`;
  const app = await launch(folder);
  try {
    const before = await readdir(join(folder, 'tools'));
    await json(app.url, '/api/link-tools/' + crypto.randomUUID() + '/install', {});
    assert.deepEqual(await readdir(join(folder, 'tools')), before, 'installation must reuse existing tools');
    await t.test('source inspection distinguishes files without extensions, redirects and multiresolution streams', async () => {
      for (const path of ['/fullhd.mp4','/download','/redirect']) {
        const info = await json(app.url,'/api/link-inspections/'+crypto.randomUUID(),{url:remote+path});
        assert.equal(info.kind,'file'); assert.deepEqual(info.heights,[]);
      }
      const info = await json(app.url,'/api/link-inspections/'+crypto.randomUUID(),{url:remote+'/master.m3u8'});
      assert.equal(info.kind,'stream'); assert.deepEqual(info.heights,[1080,360]);
      const result = await json(app.url,'/api/link-downloads/'+crypto.randomUUID(),{url:remote+'/master.m3u8',height:360,inspectionId:info.id});
      assert.equal(result.info.height,360);
      await fetch(app.url+'/api/media/'+result.id,{method:'DELETE',headers});
      const changed = await fetch(app.url+'/api/link-downloads/'+crypto.randomUUID(),{method:'POST',headers,body:JSON.stringify({url:remote+'/download',inspectionId:info.id})});
      assert.equal(changed.status,409,'inspection must be bound to its URL');
      const original = await json(app.url,'/api/link-downloads/'+crypto.randomUUID(),{url:remote+'/portrait.mp4',height:1080});
      assert.equal(original.info.height,1442); assert.equal(original.info.width,811);
      await fetch(app.url+'/api/media/'+original.id,{method:'DELETE',headers});
    });
    await t.test('best prioritizes 1080p over H.264 360p; exact quality never silently falls back', async () => {
      for (const [height, expected] of [[null,1080],[1080,1080],[360,360]]) {
        const result = await json(app.url, '/api/link-downloads/' + crypto.randomUUID(), { url:remote+'/master.m3u8', height });
        assert.equal(result.info.height,expected);
        await fetch(app.url+'/api/media/'+result.id,{method:'DELETE',headers});
      }
      const missing = await fetch(app.url+'/api/link-downloads/'+crypto.randomUUID(),{method:'POST',headers,body:JSON.stringify({url:remote+'/master.m3u8',height:720})});
      assert.equal(missing.status,422);
      assert.match((await missing.json()).error,/720p/);
      const direct = await json(app.url,'/api/link-downloads/'+crypto.randomUUID(),{url:remote+'/fullhd.mp4',height:1080});
      assert.equal(direct.info.height,1080,'direct files with initially unknown height remain usable');
      await fetch(app.url+'/api/media/'+direct.id,{method:'DELETE',headers});
      const original = await json(app.url,'/api/link-downloads/'+crypto.randomUUID(),{url:remote+'/fullhd.mp4',height:720});
      assert.equal(original.info.height,1080,'direct files ignore a stale quality selection and open the original');
      await fetch(app.url+'/api/media/'+original.id,{method:'DELETE',headers});
    });
    for (const file of ['fixture.mp4','fixture.webm']) {
      const result = await json(app.url, '/api/link-downloads/' + crypto.randomUUID(), { url: remote + '/' + file });
      assert.equal(result.info.codec, 'h264');
      assert.equal(result.info.width, 320); assert.equal(result.info.height, 180);
      const preview = await fetch(app.url + `/api/media/${result.id}/preview`, { headers: { Range: 'bytes=0-99' } });
      assert.equal(preview.status, 206); assert.equal((await preview.arrayBuffer()).byteLength, 100);
      const exported = await json(app.url, '/api/exports', { mediaId: result.id, crop: { x:0,y:0,width:320,height:180 }, scale:100, fps:24, audio:false, sourceWidth:320, sourceHeight:180 });
      let state;
      for (let i=0; i<100; i++) { state = await json(app.url, '/api/exports/' + exported.id); if (['completed','failed'].includes(state.status)) break; await pause(100); }
      assert.equal(state.status, 'completed', state.error);
      await fetch(app.url + '/api/exports/' + exported.id, { method:'DELETE', headers });
      await fetch(app.url + '/api/media/' + result.id, { method:'DELETE', headers });
      assert.equal((await fetch(app.url + `/api/media/${result.id}/preview`)).status, 404);
      const saved = await readFile(join(folder, 'downloads', result.name));
      assert.ok(saved.length > 0, 'releasing a session source must preserve the downloaded file');
      const again = await json(app.url, '/api/link-downloads/' + crypto.randomUUID(), { url: remote + '/' + file });
      assert.notEqual(again.name, result.name, 'duplicate downloads must have different names');
      assert.deepEqual(await readFile(join(folder, 'downloads', result.name)), saved, 'existing download must not be overwritten');
      await fetch(app.url + '/api/media/' + again.id, { method:'DELETE', headers });
    }
    const savedNames = (await readdir(join(folder, 'downloads'))).sort();
    const operation = crypto.randomUUID(), abort = new AbortController();
    const pending = fetch(app.url + '/api/link-downloads/' + operation, { method:'POST', headers, body:JSON.stringify({url: remote + '/slow.mp4'}), signal:abort.signal }).catch(error => error);
    let progress;
    for (let i=0; i<100; i++) { const res = await fetch(app.url + '/api/link-operations/' + operation); if (res.ok) { progress = await res.json(); if (progress.message.includes('Скачивание')) break; } await pause(100); }
    assert.ok(progress?.message.includes('Скачивание'), JSON.stringify(progress));
    const duplicate = await fetch(app.url + '/api/link-downloads/' + crypto.randomUUID(), { method:'POST', headers, body:JSON.stringify({url:remote+'/fixture.mp4'}) });
    assert.equal(duplicate.status, 409);
    abort.abort(); assert.equal((await pending).name, 'AbortError');
    let status;
    for (let i=0; i<100; i++) { status=(await fetch(app.url+'/api/link-operations/'+operation)).status; if (status===404) break; await pause(100); }
    assert.equal(status,404,'cancelled process must finish');
    const failed = await fetch(app.url + '/api/link-downloads/' + crypto.randomUUID(), { method:'POST', headers, body:JSON.stringify({url:remote+'/missing.mp4'}) });
    assert.equal(failed.status,422);
    assert.doesNotMatch((await failed.json()).error,/возраста/,'unrelated failures must not be labelled age restrictions');
    assert.deepEqual((await readdir(join(folder, 'downloads'))).sort(), savedNames, 'cancelled and failed downloads leave no permanent or partial files');
  } finally { await app.stop(); http.closeAllConnections(); await new Promise(resolve => http.close(resolve)); }
  assert.ok((await readdir(join(folder, 'downloads'))).length > 0, 'downloads survive server shutdown');
  const limited = await launch(folder, ['--Media:MaxUploadBytes', '1000']);
  const fileServer = createServer((req,res) => { res.writeHead(200,{'Content-Type':'video/mp4','Content-Length':mp4.length}); res.end(mp4); });
  await new Promise(resolve => fileServer.listen(0,'127.0.0.1',resolve));
  try {
    const result = await fetch(limited.url+'/api/link-downloads/'+crypto.randomUUID(), {method:'POST',headers,body:JSON.stringify({url:`http://127.0.0.1:${fileServer.address().port}/large.mp4`})});
    assert.equal(result.status, 413, await result.text());
  } finally { await limited.stop(); fileServer.closeAllConnections(); await new Promise(resolve=>fileServer.close(resolve)); await rm(folder,{recursive:true,force:true}); }
});
