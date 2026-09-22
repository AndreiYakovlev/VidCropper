import { state, updatePhoto, subscribe } from './state.js';
import { moveCrop, resizeCrop, pixelCrop } from './geometry.mjs';

export function setupPhotoCrop(image) {
  const stage = document.getElementById('photo-stage');
  const surface = document.getElementById('photo-surface');
  const frame = document.getElementById('photo-crop-frame');
  const canvas = document.getElementById('photo-crop-canvas');
  const context = canvas.getContext('2d');
  let drag = null;

  function draw() {
    const photo = state.photo;
    if (!photo.ready || photo.view !== 'crop' || !image.complete || !image.naturalWidth) return;
    const crop = pixelCrop(photo.crop, photo);
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, crop.x, crop.y, crop.width, crop.height, 0, 0, canvas.width, canvas.height);
  }
  function render() {
    const photo = state.photo;
    if (!photo.ready || stage.clientWidth === 0 || stage.clientHeight === 0) {
      surface.hidden = true; canvas.hidden = true; return;
    }
    const availableW = stage.clientWidth - 24, availableH = stage.clientHeight - 24;
    const scale = Math.min(availableW / photo.width, availableH / photo.height);
    surface.style.width = `${photo.width * scale}px`;
    surface.style.height = `${photo.height * scale}px`;
    surface.hidden = false;
    surface.style.position = photo.view === 'crop' ? 'absolute' : 'relative';
    surface.style.visibility = photo.view === 'crop' ? 'hidden' : 'visible';
    canvas.hidden = photo.view !== 'crop';
    const crop = pixelCrop(photo.crop, photo);
    Object.assign(frame.style, { left: `${crop.x * scale}px`, top: `${crop.y * scale}px`,
      width: `${crop.width * scale}px`, height: `${crop.height * scale}px` });
    document.getElementById('photo-frame-label').textContent = `${crop.width} × ${crop.height}`;
    if (photo.view === 'crop') {
      const factor = Math.min(availableW / crop.width, availableH / crop.height);
      const width = Math.max(1, Math.round(crop.width * factor));
      const height = Math.max(1, Math.round(crop.height * factor));
      if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      draw();
    }
  }
  frame.addEventListener('pointerdown', event => {
    const photo = state.photo;
    if (event.button !== 0 || !photo.ready || state.busyPhotoExport || state.busyPhotoPreview || state.busyAi || photo.view !== 'source') return;
    event.preventDefault(); frame.focus();
    drag = { id: event.pointerId, x: event.clientX, y: event.clientY, crop: { ...photo.crop },
      handle: event.target.dataset.handle, scale: surface.clientWidth / photo.width };
    frame.setPointerCapture(event.pointerId);
  });
  frame.addEventListener('pointermove', event => {
    const photo = state.photo;
    if (!drag || event.pointerId !== drag.id || state.busyPhotoExport || state.busyPhotoPreview || state.busyAi) return;
    const dx = (event.clientX - drag.x) / drag.scale, dy = (event.clientY - drag.y) / drag.scale;
    updatePhoto({ crop: drag.handle ? resizeCrop(drag.crop, drag.handle, dx, dy, photo, photo.ratio)
      : moveCrop(drag.crop, dx, dy, photo) });
  });
  for (const event of ['pointerup', 'pointercancel', 'lostpointercapture']) frame.addEventListener(event, () => { drag = null; });
  frame.addEventListener('keydown', event => {
    const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
    const photo = state.photo;
    if (!direction || !photo.ready || state.busyPhotoExport || state.busyPhotoPreview || state.busyAi) return;
    event.preventDefault();
    const step = event.shiftKey ? 10 : 1;
    updatePhoto({ crop: moveCrop(photo.crop, direction[0] * step, direction[1] * step, photo) });
  });
  image.addEventListener('load', render);
  new ResizeObserver(render).observe(stage);
  subscribe(render);
}
