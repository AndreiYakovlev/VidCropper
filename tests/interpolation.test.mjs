import { test } from 'node:test';
import assert from 'node:assert/strict';
import { outputFps, fpsLabel, aiReady } from '../wwwroot/js/interpolation.mjs';
import { state } from '../wwwroot/js/state.js';
import { exportRequest } from '../wwwroot/js/backend.js';

test('interpolation multiplies source FPS without changing the normal FPS choice', () => {
  const settings = { fps: 25, sourceFps: 24000 / 1001, rifeEnabled: true, rifeMultiplier: 3 };
  assert.equal(outputFps(settings), 72000 / 1001);
  assert.equal(fpsLabel(outputFps(settings)), '71.928');
  assert.equal(outputFps({ ...settings, rifeEnabled: false }), 25);
  assert.equal(settings.fps, 25);
  assert.equal(outputFps({ ...settings, sourceFps: null }), null);
  assert.equal(outputFps({ ...settings, rifeMultiplier: 4 }), null);
  assert.equal(outputFps({ ...settings, sourceFps: 60, rifeMultiplier: 2 }), 120);
  assert.equal(outputFps({ ...settings, sourceFps: 60 }), 180);
});

test('combined effects require both packages, disabled effects do not block export', () => {
  assert.equal(aiReady({ aiEnabled: false, rifeEnabled: false }), true);
  assert.equal(aiReady({ aiEnabled: false, rifeEnabled: true, rifeReady: true }), true);
  assert.equal(aiReady({ aiEnabled: true, aiReady: true, rifeEnabled: true, rifeReady: false }), false);
  assert.equal(aiReady({ aiEnabled: true, aiReady: false, rifeEnabled: true, rifeReady: true }), false);
  assert.equal(aiReady({ aiEnabled: true, aiReady: true, rifeEnabled: true, rifeReady: true }), true);
});

test('export and preview payload retains independent model, resolution and frame multipliers', () => {
  const saved = { ...state };
  try {
    Object.assign(state, { mediaId: 'fixture', width: 640, height: 360, crop: { x: 0, y: 0, width: 640, height: 360 },
      trim: { start: 1, end: 3 }, fps: 25, aiEnabled: true, aiModel: 'nomos-weak', aiScale: 4,
      rifeEnabled: true, rifeModel: 'rife-v4.26', rifeMultiplier: 3 });
    const request = exportRequest();
    assert.deepEqual(request.upscale, { modelId: 'nomos-weak', scale: 4 });
    assert.deepEqual(request.interpolation, { modelId: 'rife-v4.26', multiplier: 3 });
    assert.equal(request.fps, 25);
    assert.equal(request.startSeconds, 1);
    assert.equal(request.endSeconds, 3);
    state.rifeEnabled = false;
    assert.equal(exportRequest().interpolation, null);
    assert.deepEqual(exportRequest().upscale, request.upscale);
  } finally { Object.assign(state, saved); }
});
