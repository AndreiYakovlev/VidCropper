import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile, readdir, rm } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { tmpdir } from 'node:os';

const root = resolve(import.meta.dirname, '..');
const build = process.env.VIDCROPPER_TEST_DLL || resolve(root, 'artifacts/backend-build/VidCropper.dll');
const fixtures = resolve(root, 'artifacts/backend-tests');
const headers = { 'X-VidCropper': '1' };

function ffmpeg(args) {
  const result = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...args], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
}

async function launch(args = []) {
  const process = spawn('dotnet', [build, '--port', '0', ...args], { cwd: root, windowsHide: true });
  let logs = '';
  const exited = new Promise(resolve => process.once('exit', code => resolve(code)));
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { process.kill(); reject(new Error(`Server startup timed out: ${logs}`)); }, 15000);
    process.stdout.on('data', data => {
      logs += data;
      const match = logs.match(/VidCropper: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    process.stderr.on('data', data => { logs += data; });
    process.once('exit', code => { clearTimeout(timer); reject(new Error(`Server exited ${code}: ${logs}`)); });
  });
  return { url, process, exited };
}

async function json(url, path, method = 'GET', body) {
  const response = await fetch(url + path, { method, headers: { ...headers, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  const value = response.status === 204 ? null : await response.json();
  assert.ok(response.ok, `${response.status} ${JSON.stringify(value)}`);
  return value;
}

test('real FFmpeg pipeline, cancellation, cleanup and configurable ports', { timeout: 120000 }, async t => {
  await mkdir(fixtures, { recursive: true });
  ffmpeg(['-f','lavfi','-i','testsrc2=size=640x360:rate=30','-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-t','3','-c:v','libx264','-c:a','aac',join(fixtures,'source.mp4')]);
  ffmpeg(['-display_rotation:v:0','90','-i',join(fixtures,'source.mp4'),'-c','copy',join(fixtures,'rotated.mp4')]);
  ffmpeg(['-f','lavfi','-i','testsrc2=size=1920x1080:rate=30','-t','6','-c:v','libx264','-preset','ultrafast',join(fixtures,'large.mp4')]);
  ffmpeg(['-f','lavfi','-i','color=red:size=160x90:rate=200','-frames:v','1','-c:v','libx264',join(fixtures,'tiny.mp4')]);
  // Every second has a distinct picture and tone, exposing wrong seek offsets and A/V drift.
  ffmpeg(['-f','lavfi','-i',"color=red:size=160x90:rate=30:duration=3,drawbox=color=lime:t=fill:enable='between(t,1,1.999)',drawbox=color=blue:t=fill:enable='gte(t,2)'",
    '-f','lavfi','-i','aevalsrc=if(lt(t\\,1)\\,sin(2*PI*440*t)\\,if(lt(t\\,2)\\,sin(2*PI*880*t)\\,sin(2*PI*1320*t))):s=48000:d=3',
    '-c:v','libx264','-g','90','-c:a','aac',join(fixtures,'timeline.mp4')]);
  const tempRoot = join(tmpdir(), 'VidCropper');
  const before = new Set(await readdir(tempRoot).catch(() => []));
  const server = await launch();
  const { url } = server;
  for (const path of ['/', '/js/app.js', '/js/upscaler.js', '/styles.css']) {
    const response = await fetch(url + path);
    assert.equal(response.headers.get('cache-control'), 'no-cache', `${path} must revalidate after upgrades`);
    await response.arrayBuffer();
  }
  async function upload(name) {
    const id = crypto.randomUUID();
    const response = await fetch(`${url}/api/media/${id}?name=${encodeURIComponent('тест " видео ' + name)}`, {
      method: 'PUT', headers: { ...headers, 'Content-Type': 'application/octet-stream' }, body: await readFile(join(fixtures, name)) });
    assert.equal(response.status, 200, await response.clone().text());
    return response.json();
  }
  const archived = new Map();
  async function finish(job) {
    const response = await fetch(`${url}/api/exports/${job.id}/events`);
    assert.equal(response.headers.get('content-type'), 'text/event-stream');
    const snapshots = (await response.text()).trim().split('\n\n').map(line => JSON.parse(line.slice(6)));
    const end = snapshots.at(-1);
    assert.equal(end.status, 'completed', JSON.stringify(end));
    assert.equal(end.progress, 100);
    const savedPath = join(root,'output',end.fileName);
    assert.ok(!archived.has(savedPath), 'exports never overwrite an earlier result');
    archived.set(savedPath, await readFile(savedPath));
    for (let i = 1; i < snapshots.length; i++) assert.ok(snapshots[i].progress >= snapshots[i-1].progress);
    return end;
  }
  try {
    const config = await json(url, '/api/config');
    assert.equal(config.error, null);
    const source = await upload('source.mp4');
    assert.equal(source.info.width, 640); assert.equal(source.info.height, 360);
    assert.equal(source.info.hasAudio, true); assert.equal(source.info.fps, 30);
    const request = { mediaId: source.id, crop: { x: 141, y: 1, width: 359, height: 359 }, scale: 50, fps: 25, audio: false, sourceWidth:640, sourceHeight:360 };

    await t.test('export odd crop, resize, FPS and remove audio; downloaded file verified', async () => {
      const result = await finish(await json(url, '/api/exports', 'POST', request));
      assert.equal(result.result.width,178); assert.equal(result.result.height,178);
      assert.equal(result.result.fps,25); assert.equal(result.result.hasAudio,false);
      const response = await fetch(`${url}/api/exports/${result.id}/download`);
      assert.equal(response.status,200);
      const bytes = Buffer.from(await response.arrayBuffer()); assert.equal(bytes.length,result.result.size);
      assert.match(bytes.toString('latin1'), /crf=16\.0\b/, 'omitted quality retains CRF 16');
      const path = join(fixtures,'result.mp4'); await writeFile(path,bytes);
      const probe = spawnSync('ffprobe',['-v','error','-show_streams','-of','json',path],{encoding:'utf8'});
      assert.equal(probe.status,0); const stream=JSON.parse(probe.stdout).streams[0];
      assert.equal(stream.codec_name,'h264'); assert.equal(stream.pix_fmt,'yuv420p'); assert.equal(stream.width,178);
      await json(url, `/api/exports/${result.id}`, 'DELETE');
      assert.equal((await fetch(`${url}/api/exports/${result.id}/download`)).status,404);
    });
    await t.test('quality presets reach x264 and preserve output properties', async () => {
      const sizes = new Map();
      for (const [quality, crf] of [['maximum',16], ['high',20], ['balanced',23], ['compact',28], [null,16]]) {
        const result = await finish(await json(url, '/api/exports', 'POST', { ...request, quality, audio:true,
          crop:{x:0,y:0,width:640,height:360}, scale:100, fps:30 }));
        assert.equal(result.result.width,640); assert.equal(result.result.height,360);
        assert.equal(result.result.fps,30); assert.equal(result.result.hasAudio,true);
        assert.ok(Math.abs(result.result.duration - 3) < 0.1);
        const response = await fetch(`${url}/api/exports/${result.id}/download`);
        assert.equal(response.status,200);
        const bytes = Buffer.from(await response.arrayBuffer());
        // x264's SEI records the actual encoding settings, independently of our argument builder.
        assert.ok(bytes.toString('latin1').includes(`crf=${crf}.0`), `actual CRF for ${quality}`);
        sizes.set(quality, bytes.length);
        await json(url, `/api/exports/${result.id}`, 'DELETE');
      }
      assert.ok(sizes.get('compact') < sizes.get('maximum'));
      t.diagnostic(`CRF 16: ${sizes.get('maximum')} bytes; CRF 28: ${sizes.get('compact')} bytes`);
      for (const quality of ['unknown', '', 'MAXIMUM', 16]) {
        const response = await fetch(url + '/api/exports', { method:'POST',
          headers:{...headers,'Content-Type':'application/json'}, body:JSON.stringify({...request,quality}) });
        assert.equal(response.status,400, `invalid quality ${quality}`);
      }
    });
    await t.test('audio is preserved when requested', async () => {
      const result = await finish(await json(url, '/api/exports', 'POST', { ...request, audio:true }));
      assert.equal(result.result.hasAudio,true);
      await json(url, `/api/exports/${result.id}`, 'DELETE');
    });
    await t.test('odd portrait crop exports exact square-pixel dimensions', async () => {
      const result = await finish(await json(url, '/api/exports', 'POST', { ...request, crop:{x:219,y:0,width:203,height:360} }));
      assert.equal(result.result.width,100); assert.equal(result.result.height,180);
      await json(url, `/api/exports/${result.id}`, 'DELETE');
    });
    await t.test('rotation uses displayed geometry', async () => {
      const rotated = await upload('rotated.mp4');
      assert.equal(rotated.info.width,360); assert.equal(rotated.info.height,640);
      const result = await finish(await json(url,'/api/exports','POST',{...request,mediaId:rotated.id,sourceWidth:360,sourceHeight:640,crop:{x:0,y:100,width:360,height:400}}));
      assert.equal(result.result.width,180); assert.equal(result.result.height,200);
      await json(url, `/api/exports/${result.id}`, 'DELETE'); await json(url, `/api/media/${rotated.id}`, 'DELETE');
    });
    await t.test('trim exports the selected picture and synchronized audio at fractional boundaries', async () => {
      const timeline = await upload('timeline.mp4');
      const base = { ...request, mediaId: timeline.id, sourceWidth: 160, sourceHeight: 90,
        crop: { x: 0, y: 0, width: 160, height: 90 }, scale: 100, fps: 30 };
      const cases = [
        { name: 'legacy-full', start: 0, end: 3, audio: true, bounds: {} },
        { name: 'explicit-full', start: 0, end: 3, audio: false, bounds: { startSeconds: 0, endSeconds: 3 } },
        { name: 'start', start: 1.13, end: 3, audio: true, bounds: { startSeconds: 1.13 } },
        { name: 'end', start: 0, end: 1.77, audio: true, bounds: { endSeconds: 1.77 } },
        { name: 'middle', start: 0.75, end: 2.25, audio: true, bounds: { startSeconds: 0.75, endSeconds: 2.25 } },
        { name: 'silent-middle', start: 2.13, end: 2.73, audio: false, bounds: { startSeconds: 2.13, endSeconds: 2.73 } }
      ];
      for (const item of cases) {
        const result = await finish(await json(url, '/api/exports', 'POST', { ...base, audio: item.audio, ...item.bounds }));
        assert.ok(Math.abs(result.result.duration - (item.end - item.start)) < 0.08, JSON.stringify({ item, result }));
        assert.equal(result.result.hasAudio, item.audio);
        const path = join(fixtures, `trim-${item.name}.mp4`);
        await writeFile(path, Buffer.from(await (await fetch(`${url}/api/exports/${result.id}/download`)).arrayBuffer()));
        const probe = spawnSync('ffprobe', ['-v', 'error', '-show_streams', '-of', 'json', path], { encoding: 'utf8' });
        assert.equal(probe.status, 0, probe.stderr);
        const streams = JSON.parse(probe.stdout).streams;
        for (const stream of streams) {
          assert.ok(Math.abs(Number(stream.start_time)) < 0.05, `${item.name}: timestamps start at zero`);
          assert.ok(Math.abs(Number(stream.duration) - (item.end - item.start)) < 0.08, `${item.name}: stream duration`);
        }
        for (const offset of [0.08, item.end - item.start - 0.18]) {
          const second = Math.floor(item.start + offset);
          const pixels = spawnSync('ffmpeg', ['-v', 'error', '-ss', String(offset), '-i', path, '-frames:v', '1', '-vf', 'scale=1:1', '-pix_fmt', 'rgb24', '-f', 'rawvideo', 'pipe:1']);
          assert.equal(pixels.status, 0, pixels.stderr.toString());
          assert.equal(pixels.stdout.length, 3);
          assert.ok(pixels.stdout[second] > 180, `${item.name}: expected color ${second}, got ${[...pixels.stdout]}`);
          if (item.audio) {
            const samples = spawnSync('ffmpeg', ['-v', 'error', '-ss', String(offset), '-i', path, '-t', '0.05', '-vn', '-ac', '1', '-ar', '48000', '-f', 'f32le', 'pipe:1']);
            assert.equal(samples.status, 0, samples.stderr.toString());
            let crossings = 0;
            for (let i = 4; i < samples.stdout.length; i += 4)
              if (samples.stdout.readFloatLE(i - 4) <= 0 && samples.stdout.readFloatLE(i) > 0) crossings++;
            const frequency = crossings / (samples.stdout.length / 4 / 48000);
            assert.ok(Math.abs(frequency - 440 * (second + 1)) < 30, `${item.name}: wrong audio moment (${frequency} Hz)`);
          }
        }
        await json(url, `/api/exports/${result.id}`, 'DELETE');
      }
      await json(url, `/api/media/${timeline.id}`, 'DELETE');
    });
    await t.test('invalid trim ranges are rejected before an export starts', async () => {
      for (const bounds of [{ startSeconds: -1 }, { endSeconds: 4 }, { startSeconds: 2, endSeconds: 1 },
        { startSeconds: 1, endSeconds: 1 }, { startSeconds: 1, endSeconds: 1.005 }, { endSeconds: 0 },
        { startSeconds: 'NaN' }, { endSeconds: 'Infinity' }]) {
        const response = await fetch(url + '/api/exports', { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ ...request, ...bounds }) });
        assert.equal(response.status, 400, JSON.stringify(bounds));
      }
    });
    await t.test('minimum length selections remain playable including the last frame', async () => {
      for (const startSeconds of [1.01, 2.99]) {
        const result = await finish(await json(url, '/api/exports', 'POST', { ...request, startSeconds, endSeconds: startSeconds + 0.01 }));
        assert.ok(result.result.duration > 0 && result.result.duration < 0.1);
        await json(url, `/api/exports/${result.id}`, 'DELETE');
      }
      const tiny = await upload('tiny.mp4');
      assert.ok(tiny.info.duration < 0.01);
      const result = await finish(await json(url, '/api/exports', 'POST', { ...request, mediaId: tiny.id,
        sourceWidth: 160, sourceHeight: 90, crop: { x: 0, y: 0, width: 160, height: 90 },
        startSeconds: 0, endSeconds: tiny.info.duration }));
      assert.ok(result.result.duration > 0 && result.result.duration < 0.1);
      await json(url, `/api/exports/${result.id}`, 'DELETE');
      await json(url, `/api/media/${tiny.id}`, 'DELETE');
    });
    await t.test('invalid geometry and cross-origin mutations are rejected', async () => {
      for (const invalid of [{...request,scale:101},{...request,fps:1000},{...request,crop:{x:630,y:0,width:50,height:50}},{...request,sourceWidth:999}]) {
        const response=await fetch(url+'/api/exports',{method:'POST',headers:{...headers,'Content-Type':'application/json'},body:JSON.stringify(invalid)});
        assert.equal(response.status,400);
      }
      assert.equal((await fetch(url+'/api/exports',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)})).status,403);
      assert.equal((await fetch(url+'/api/exports',{method:'POST',headers:{...headers,Origin:'https://example.com','Content-Type':'application/json'},body:JSON.stringify(request)})).status,403);
      assert.equal((await fetch(url+'/api/media/'+crypto.randomUUID(),{method:'PUT',headers:{...headers,'Content-Type':'application/octet-stream'},body:'not a video'})).status,422);
    });
    await t.test('cancel stops encoding and releases source and output', async () => {
      const large = await upload('large.mp4');
      const job = await json(url,'/api/exports','POST',{...request,mediaId:large.id,sourceWidth:1920,sourceHeight:1080,crop:{x:0,y:0,width:1920,height:1080},scale:100,fps:60});
      await json(url, `/api/media/${large.id}`, 'DELETE');
      const cancelled = await json(url, `/api/exports/${job.id}/cancel`, 'POST');
      assert.equal(cancelled.status,'cancelled');
      assert.equal((await fetch(`${url}/api/exports/${job.id}/download`)).status,409);
      await json(url, `/api/exports/${job.id}`, 'DELETE');
      const sourcePath = (await readdir(tempRoot)).filter(x=>!before.has(x));
      for (const directory of sourcePath) assert.ok(!(await readdir(join(tempRoot,directory))).includes(large.id.replaceAll('-','')+'.source'));
    });
    await t.test('occupied port gives a useful failure', async () => {
      const duplicate = spawn('dotnet',[build,'--port',new URL(url).port],{cwd:root,windowsHide:true});
      let logs=''; duplicate.stdout.on('data',data=>logs+=data); duplicate.stderr.on('data',data=>logs+=data);
      const code=await new Promise(resolve=>duplicate.on('exit',resolve));
      assert.notEqual(code,0); assert.match(logs,/Start.cmd 5300/);
    });
    await json(url, `/api/media/${source.id}`, 'DELETE');
  } finally {
    await json(url,'/api/shutdown','POST').catch(()=>server.process.kill());
    await server.exited;
    for (const [path, data] of archived) {
      assert.deepEqual(await readFile(path), data, 'exports survive job deletion and server shutdown');
      await rm(path);
    }
  }
  const after = await readdir(tempRoot).catch(()=>[]);
  assert.deepEqual(after.filter(x=>!before.has(x)),[], 'session temp directories must be removed on shutdown');
});

test('configured upload limit and missing FFmpeg are reported clearly', { timeout: 30000 }, async () => {
  const server = await launch(['--Media:MaxUploadBytes','16','--Media:FfmpegPath',join(fixtures,'missing-ffmpeg.exe')]);
  try {
    const config = await json(server.url,'/api/config');
    assert.equal(config.maxUploadBytes,16);
    assert.match(config.error,/FfmpegPath/);
    const response = await fetch(server.url+'/api/media/'+crypto.randomUUID(),{
      method:'PUT',headers:{...headers,'Content-Type':'application/octet-stream'},body:'x'.repeat(17)
    });
    assert.equal(response.status,413);
  } finally {
    await json(server.url,'/api/shutdown','POST').catch(()=>server.process.kill());
    await server.exited;
  }
});
