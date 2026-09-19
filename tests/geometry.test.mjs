import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fitCrop, moveCrop, resizeCrop, editCrop, pixelCrop, outputSize } from '../wwwroot/js/geometry.mjs';

const bounds = { width: 1920, height: 1080 };
test('presets fit centrally inside landscape and portrait sources', () => {
  for (const b of [bounds, { width: 1080, height: 1920 }]) {
    for (const ratio of [1, 16/9, 9/16, 4/3, 3/4, 4/5, 21/9]) {
      const c = fitCrop(b.width, b.height, ratio);
      assert.ok(Math.abs(c.width / c.height - ratio) < 1e-9);
      assert.ok(c.x >= 0 && c.y >= 0);
      assert.ok(c.x + c.width <= b.width + 1e-9 && c.y + c.height <= b.height + 1e-9);
    }
  }
});
test('drag clamps at all source edges', () => {
  const crop = fitCrop(1920, 1080, 1);
  assert.equal(moveCrop(crop, -5000, 0, bounds).x, 0);
  assert.equal(moveCrop(crop, 5000, 0, bounds).x, 840);
});
test('every handle preserves ratio and stays in bounds under large movement', () => {
  const c = { x: 300, y: 200, width: 400, height: 400 };
  for (const handle of ['nw','n','ne','e','se','s','sw','w']) {
    for (const dx of [-5000, -10, 20, 5000]) for (const dy of [-5000, 0, 5000]) {
      const r = resizeCrop(c, handle, dx, dy, bounds, 1);
      assert.ok(r.x >= -1e-9 && r.y >= -1e-9);
      assert.ok(r.x + r.width <= 1920 + 1e-9 && r.y + r.height <= 1080 + 1e-9);
      assert.ok(r.width > 0 && r.height > 0);
      assert.ok(Math.abs(r.width / r.height - 1) < 1e-9);
    }
  }
});
test('numeric dimensions keep locked ratio, moving origin if needed', () => {
  const c = editCrop({ x: 1800, y: 900, width: 100, height: 100 }, 'width', 5000, bounds, 1);
  assert.deepEqual(c, { x: 840, y: 0, width: 1080, height: 1080 });
  assert.deepEqual(editCrop(c, 'width', NaN, bounds), c);
});
test('output is even, scales crop and handles tiny or odd inputs', () => {
  assert.deepEqual(outputSize({ width:1080, height:1080 },50), { width:540, height:540 });
  assert.deepEqual(outputSize({ width:609, height:1081 },100), { width:608, height:1080 });
  assert.deepEqual(outputSize({ width:1, height:1 },1), { width:2, height:2 });
});
test('integer crop stays inside source after rounding', () => {
  assert.deepEqual(pixelCrop({ x: 1.7, y: 1.7, width: 1918.7, height: 1078.7 }, bounds), { x:1, y:1, width:1919, height:1079 });
});
