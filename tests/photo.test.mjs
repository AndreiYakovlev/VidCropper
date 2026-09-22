import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { resolve, join } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const build = process.env.VIDCROPPER_TEST_DLL || resolve(root, 'artifacts/backend-build/VidCropper.dll');
const fixtures = resolve(root, 'artifacts/photo-tests');
const headers = { 'X-VidCropper': '1' };

function ffmpeg(args) {
  const result = spawnSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...args], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
}
async function launch() {
  const process = spawn('dotnet', [build, '--port', '0'], { cwd: root, windowsHide: true });
  let logs = '';
  const url = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => { process.kill(); reject(new Error(logs)); }, 15000);
    process.stdout.on('data', data => {
      logs += data;
      const match = logs.match(/VidCropper: (http:\/\/127\.0\.0\.1:\d+)/);
      if (match) { clearTimeout(timer); resolveUrl(match[1]); }
    });
    process.stderr.on('data', data => { logs += data; });
    process.once('exit', code => reject(new Error(`Server exited ${code}: ${logs}`)));
  });
  return { process, url };
}
async function json(url, path, method = 'GET', body) {
  const response = await fetch(url + path, { method, headers: { ...headers, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const value = response.status === 204 ? null : await response.json();
  assert.ok(response.ok, `${response.status} ${JSON.stringify(value)}`);
  return value;
}
async function upload(url, name) {
  const id = crypto.randomUUID();
  const response = await fetch(`${url}/api/photos/${id}?name=${encodeURIComponent(name)}`, {
    method: 'PUT', headers: { ...headers, 'Content-Type': 'application/octet-stream' },
    body: await readFile(join(fixtures, name)),
  });
  return { response, id, value: response.ok ? await response.json() : await response.json() };
}
async function finish(url, job) {
  const response = await fetch(`${url}/api/photo-exports/${job.id}/events`);
  assert.equal(response.status, 200);
  const values = (await response.text()).trim().split('\n\n').map(line => JSON.parse(line.slice(6)));
  const result = values.at(-1);
  assert.equal(result.status, 'completed', JSON.stringify(result));
  return result;
}

test('photo upload, crop, formats, alpha, metadata, rejection and cleanup', { timeout: 120000 }, async () => {
  await mkdir(fixtures, { recursive: true });
  ffmpeg(['-f', 'lavfi', '-i', 'color=c=red@0.45:s=101x77,format=rgba', '-frames:v', '1',
    '-metadata', 'comment=secret-location', join(fixtures, 'alpha.png')]);
  ffmpeg(['-f', 'lavfi', '-i', 'testsrc2=s=80x60', '-frames:v', '1', join(fixtures, 'source.jpg')]);
  ffmpeg(['-f', 'lavfi', '-i', 'testsrc2=s=64x48', '-frames:v', '1', '-c:v', 'libwebp', join(fixtures, 'source.webp')]);
  ffmpeg(['-f', 'lavfi', '-i', 'testsrc2=s=32x24:r=2', '-t', '1', join(fixtures, 'animated.gif')]);
  const server = await launch();
  const outputs = [];
  try {
    const uploaded = await upload(server.url, 'alpha.png');
    assert.equal(uploaded.response.status, 200);
    assert.deepEqual([uploaded.value.info.width, uploaded.value.info.height], [101, 77]);
    assert.equal(uploaded.value.info.format, 'png');
    assert.equal(uploaded.value.info.hasAlpha, true);
    const preview = await fetch(`${server.url}/api/photos/${uploaded.id}/preview`);
    assert.equal(preview.headers.get('content-type'), 'image/png');

    const base = { mediaId: uploaded.id, crop: { x: 3, y: 5, width: 95, height: 67 }, scale: 100,
      sourceWidth: 101, sourceHeight: 77, format: 'png', quality: null };
    for (const item of [
      { format: 'png', quality: null, type: 'image/png', alpha: true },
      { format: 'jpeg', quality: 92, type: 'image/jpeg', alpha: false },
      { format: 'webp', quality: 92, type: 'image/webp', alpha: true },
    ]) {
      const result = await finish(server.url, await json(server.url, '/api/photo-exports', 'POST', { ...base, ...item }));
      assert.deepEqual([result.result.width, result.result.height], [95, 67], item.format);
      assert.equal(result.result.hasAlpha, item.alpha);
      const response = await fetch(`${server.url}/api/photo-exports/${result.id}/download`);
      assert.equal(response.headers.get('content-type'), item.type);
      const path = join(fixtures, `result.${item.format === 'jpeg' ? 'jpg' : item.format}`);
      await writeFile(path, Buffer.from(await response.arrayBuffer()));
      const probe = spawnSync('ffprobe', ['-v', 'error', '-show_entries', 'format_tags', '-of', 'json', path], { encoding: 'utf8' });
      assert.equal(probe.status, 0, probe.stderr);
      assert.ok(!JSON.stringify(JSON.parse(probe.stdout)).includes('secret-location'));
      const pixel = spawnSync('ffmpeg', ['-v', 'error', '-i', path, '-vf', 'scale=1:1', '-frames:v', '1',
        '-pix_fmt', 'rgba', '-f', 'rawvideo', 'pipe:1']);
      assert.equal(pixel.status, 0, pixel.stderr.toString());
      assert.equal(pixel.stdout.length, 4);
      if (item.format === 'jpeg') {
        assert.equal(pixel.stdout[3], 255);
        assert.ok(pixel.stdout[1] > 100 && pixel.stdout[2] > 100, 'transparent red is composited onto white');
      } else assert.ok(pixel.stdout[3] < 240, `${item.format} retains non-opaque alpha`);
      outputs.push(join(root, 'output', result.fileName));
      await json(server.url, `/api/photo-exports/${result.id}`, 'DELETE');
    }
    const half = await finish(server.url, await json(server.url, '/api/photo-exports', 'POST', { ...base, scale: 50 }));
    assert.deepEqual([half.result.width, half.result.height], [48, 34]);
    outputs.push(join(root, 'output', half.fileName));
    await json(server.url, `/api/photo-exports/${half.id}`, 'DELETE');

    const animated = await upload(server.url, 'animated.gif');
    assert.equal(animated.response.status, 400);
    assert.match(animated.value.error, /JPG|PNG|WebP|статическ/i);
    await json(server.url, `/api/photos/${uploaded.id}`, 'DELETE');
  } finally {
    await fetch(server.url + '/api/shutdown', { method: 'POST', headers }).catch(() => {});
    await new Promise(resolveExit => server.process.once('exit', resolveExit));
    for (const path of outputs) await rm(path, { force: true });
  }
});
