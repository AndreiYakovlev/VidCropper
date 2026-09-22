import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fileKind, photoOutputSize } from '../wwwroot/js/photo.mjs';
import { state, updatePhoto } from '../wwwroot/js/state.js';
import { photoExportRequest } from '../wwwroot/js/photo-backend.js';

test('video is the default and photo updates remain isolated', () => {
  assert.equal(state.mode, 'video');
  const videoCrop = { x: 0, y: 0, width: 640, height: 360 };
  state.crop = videoCrop;
  updatePhoto({ width: 101, height: 77, crop: { x: 3, y: 5, width: 95, height: 67 } });
  assert.equal(state.crop, videoCrop);
  assert.deepEqual(state.photo.crop, { x: 3, y: 5, width: 95, height: 67 });
});

test('drop and paste routing recognizes only supported first-version formats', () => {
  assert.equal(fileKind({ name: 'photo.JPG', type: '' }), 'photo');
  assert.equal(fileKind({ name: 'clipboard', type: 'image/png' }), 'photo');
  assert.equal(fileKind({ name: 'clip.mp4', type: '' }), 'video');
  assert.equal(fileKind({ name: 'photo.heic', type: 'image/heic' }), null);
  assert.equal(fileKind({ name: 'animation.gif', type: 'image/gif' }), null);
});

test('photo sizes preserve odd pixels and AI multiplication exactly', () => {
  const crop = { width: 95, height: 67 };
  assert.deepEqual(photoOutputSize(crop, 100), { width: 95, height: 67 });
  assert.deepEqual(photoOutputSize(crop, 50), { width: 48, height: 34 });
  assert.deepEqual(photoOutputSize(crop, 1, 3), { width: 285, height: 201 });
});

test('photo export payload keeps format quality and independent AI settings', () => {
  updatePhoto({ mediaId: 'photo-id', ready: true, width: 101, height: 77,
    crop: { x: 3, y: 5, width: 95, height: 67 }, scale: 73, format: 'jpeg', quality: 92 });
  Object.assign(state, { photoAiEnabled: true, photoAiModel: 'spankendata', photoAiScale: 3 });
  assert.deepEqual(photoExportRequest(), { mediaId: 'photo-id', crop: { x: 3, y: 5, width: 95, height: 67 },
    scale: 73, sourceWidth: 101, sourceHeight: 77, format: 'jpeg', quality: 92,
    upscale: { modelId: 'spankendata', scale: 3 } });
  updatePhoto({ format: 'png' });
  assert.equal(photoExportRequest().quality, null);
});
