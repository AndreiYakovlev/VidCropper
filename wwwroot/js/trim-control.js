import { state, subscribe, update } from './state.js';
import { TRIM_STEP, fullRange, moveBoundary, endPreviewTime, formatTrimTime } from './trim.mjs';

export function setupTrim(video) {
  const track = document.getElementById('trim-track');
  const reset = document.getElementById('trim-reset');
  const handles = ['start', 'end'].map(boundary => document.getElementById(`trim-${boundary}`));
  let drag = null;

  function change(boundary, value) {
    if (!state.ready || (state.busyExport || state.busyAi)) return;
    video.pause();
    const trim = moveBoundary(state.trim, boundary, value, state.duration);
    update({ trim });
    video.currentTime = boundary === 'start' ? trim.start : endPreviewTime(trim);
  }

  for (const [index, handle] of handles.entries()) {
    const boundary = index === 0 ? 'start' : 'end';
    handle.addEventListener('pointerdown', event => {
      if (!state.ready || (state.busyExport || state.busyAi) || event.button !== 0 || drag) return;
      video.pause();
      handle.focus();
      drag = { pointerId: event.pointerId, boundary, x: event.clientX, value: state.trim[boundary] };
      handle.setPointerCapture(event.pointerId);
      event.preventDefault();
    });
    handle.addEventListener('pointermove', event => {
      if (!drag || drag.pointerId !== event.pointerId) return;
      const width = track.getBoundingClientRect().width;
      if (width > 0) change(drag.boundary, drag.value + (event.clientX - drag.x) / width * state.duration);
    });
    for (const eventName of ['pointerup', 'pointercancel', 'lostpointercapture']) {
      handle.addEventListener(eventName, event => {
        if (drag?.pointerId !== event.pointerId) return;
        drag = null;
        if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
      });
    }
    handle.addEventListener('keydown', event => {
      const gap = Math.min(TRIM_STEP, state.duration);
      const values = {
        ArrowLeft: state.trim[boundary] - TRIM_STEP,
        ArrowDown: state.trim[boundary] - TRIM_STEP,
        ArrowRight: state.trim[boundary] + TRIM_STEP,
        ArrowUp: state.trim[boundary] + TRIM_STEP,
        PageDown: state.trim[boundary] - 1,
        PageUp: state.trim[boundary] + 1,
        Home: boundary === 'start' ? 0 : state.trim.start + gap,
        End: boundary === 'start' ? state.trim.end - gap : state.duration
      };
      if (!(event.key in values)) return;
      event.preventDefault();
      change(boundary, values[event.key]);
    });
  }
  reset.addEventListener('click', () => {
    if (!state.ready || (state.busyExport || state.busyAi)) return;
    video.pause();
    update({ trim: fullRange(state.duration) });
    video.currentTime = 0;
  });

  function render() {
    const { trim, duration } = state;
    const gap = Math.min(TRIM_STEP, duration);
    track.style.setProperty('--trim-start', `${duration ? trim.start / duration * 100 : 0}%`);
    track.style.setProperty('--trim-end', `${duration ? trim.end / duration * 100 : 100}%`);
    for (const [index, handle] of handles.entries()) {
      const boundary = index === 0 ? 'start' : 'end';
      handle.disabled = !state.ready || (state.busyExport || state.busyAi);
      handle.setAttribute('aria-valuemin', index === 0 ? 0 : trim.start + gap);
      handle.setAttribute('aria-valuemax', index === 0 ? Math.max(0, trim.end - gap) : duration);
      handle.setAttribute('aria-valuenow', trim[boundary]);
      handle.setAttribute('aria-valuetext', formatTrimTime(trim[boundary]));
      document.getElementById(`trim-${boundary}-time`).textContent = state.ready ? formatTrimTime(trim[boundary]) : '—';
    }
    reset.disabled = !state.ready || (state.busyExport || state.busyAi);
    const length = state.ready ? formatTrimTime(trim.end - trim.start) : '—';
    document.getElementById('trim-length').textContent = length;
    document.getElementById('summary-duration').textContent = length;
  }
  subscribe(render);
  render();
}
