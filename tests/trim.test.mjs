import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fullRange, constrainRange, moveBoundary, endPreviewTime, formatTrimTime } from '../wwwroot/js/trim.mjs';

test('full range resets selection and supports sub-step files', () => {
  assert.deepEqual(fullRange(12.345), { start: 0, end: 12.345 });
  assert.deepEqual(fullRange(Infinity), { start: 0, end: 0 });
  assert.deepEqual(moveBoundary(fullRange(0.005), 'start', 1, 0.005), fullRange(0.005));
  assert.deepEqual(moveBoundary(fullRange(0.005), 'end', 0, 0.005), fullRange(0.005));
});

test('boundaries clamp independently without crossing and retain exact endpoints', () => {
  const range = { start: 1, end: 2 };
  assert.deepEqual(moveBoundary(range, 'start', 5, 3), { start: 1.99, end: 2 });
  assert.deepEqual(moveBoundary(range, 'end', -5, 3), { start: 1, end: 1.01 });
  assert.deepEqual(moveBoundary(range, 'start', -2, 3), { start: 0, end: 2 });
  assert.deepEqual(moveBoundary(range, 'end', 8, 3.333), { start: 1, end: 3.333 });
  assert.deepEqual(moveBoundary(range, 'start', NaN, 3), range);
  assert.equal(moveBoundary(range, 'start', 1.234, 3).start, 1.23);
  assert.equal(moveBoundary({ start: 0, end: 0.333 }, 'start', 0.323, 0.333).start, 0.323);
});

test('metadata reconciliation preserves selection or clamps it to the shorter source', () => {
  assert.deepEqual(constrainRange({ start: 1, end: 2 }, 4), { start: 1, end: 2 });
  assert.deepEqual(constrainRange({ start: 1, end: 4 }, 3), { start: 1, end: 3 });
  assert.deepEqual(constrainRange({ start: 4, end: 5 }, 3), { start: 2.99, end: 3 });
  assert.deepEqual(constrainRange({ start: 4, end: 5 }, 0.005), fullRange(0.005));
  assert.deepEqual(constrainRange(null, 3), fullRange(3));
});

test('preview is inside the exclusive end and time formatting carries correctly', () => {
  assert.equal(endPreviewTime({ start: 1, end: 2 }), 1.999);
  assert.equal(endPreviewTime({ start: 0, end: 0.0005 }), 0);
  assert.equal(formatTrimTime(59.999), '01:00.00');
  assert.equal(formatTrimTime(3600.12), '1:00:00.12');
  assert.equal(formatTrimTime(NaN), '—');
});
