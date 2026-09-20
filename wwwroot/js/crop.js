import { state, update, subscribe } from './state.js';
import { moveCrop, resizeCrop, pixelCrop } from './geometry.mjs';

export function setupCrop(video) {
  const stage = document.querySelector('#stage');
  const surface = document.querySelector('#video-surface');
  const frame = document.querySelector('#crop-frame');
  const canvas = document.querySelector('#crop-canvas');
  const context = canvas.getContext('2d');
  let drag = null, animation = 0;

  function draw() {
    if (!state.ready || state.view !== 'crop' || video.readyState < 2) return;
    const crop = pixelCrop(state.crop, state);
    context.drawImage(video, crop.x, crop.y, crop.width, crop.height, 0, 0, canvas.width, canvas.height);
  }
  function render() {
    if (!state.ready) { surface.hidden = true; canvas.hidden = true; return; }
    const availableW = stage.clientWidth - 24, availableH = stage.clientHeight - 24;
    const scale = Math.min(availableW / state.width, availableH / state.height);
    surface.style.width = `${state.width * scale}px`;
    surface.style.height = `${state.height * scale}px`;
    // Keep the video laid out and decoding when the canvas view is selected.
    surface.hidden = false;
    surface.style.position = state.view === 'crop' ? 'absolute' : 'relative';
    surface.style.visibility = state.view === 'crop' ? 'hidden' : 'visible';
    canvas.hidden = state.view !== 'crop';
    const c = pixelCrop(state.crop, state);
    Object.assign(frame.style, { left: `${c.x * scale}px`, top: `${c.y * scale}px`, width: `${c.width * scale}px`, height: `${c.height * scale}px` });
    document.querySelector('#frame-label').textContent = `${c.width} × ${c.height}`;
    if (state.view === 'crop') {
      const factor = Math.min(availableW / c.width, availableH / c.height);
      const w = Math.max(1, Math.round(c.width * factor)), h = Math.max(1, Math.round(c.height * factor));
      if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
      draw();
    }
  }
  function tick() { draw(); if (!video.paused && !video.ended) animation = requestAnimationFrame(tick); }
  video.addEventListener('play', () => { cancelAnimationFrame(animation); tick(); });
  for (const event of ['seeked', 'loadeddata', 'pause']) video.addEventListener(event, draw);
  frame.addEventListener('pointerdown', event => {
    if (event.button !== 0 || !state.ready || (state.busyExport || state.busyAi) || state.view !== 'source') return;
    event.preventDefault(); frame.focus();
    drag = { id: event.pointerId, x: event.clientX, y: event.clientY, crop: { ...state.crop },
      handle: event.target.dataset.handle, scale: surface.clientWidth / state.width };
    frame.setPointerCapture(event.pointerId);
  });
  frame.addEventListener('pointermove', event => {
    if (!drag || event.pointerId !== drag.id || !state.ready || (state.busyExport || state.busyAi)) return;
    const dx = (event.clientX - drag.x) / drag.scale, dy = (event.clientY - drag.y) / drag.scale;
    update({ crop: drag.handle ? resizeCrop(drag.crop, drag.handle, dx, dy, state, state.ratio) : moveCrop(drag.crop, dx, dy, state) });
  });
  for (const event of ['pointerup', 'pointercancel', 'lostpointercapture']) frame.addEventListener(event, () => { drag = null; });
  frame.addEventListener('keydown', event => {
    const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
    if (!direction || !state.ready || (state.busyExport || state.busyAi)) return;
    event.preventDefault(); const step = event.shiftKey ? 10 : 1;
    update({ crop: moveCrop(state.crop, direction[0] * step, direction[1] * step, state) });
  });
  new ResizeObserver(render).observe(stage);
  subscribe(render);
}
