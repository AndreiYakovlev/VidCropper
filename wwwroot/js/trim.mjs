export const TRIM_STEP = 0.01;
const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

export function fullRange(duration) {
  return { start: 0, end: Number.isFinite(duration) ? Math.max(0, duration) : 0 };
}

export function constrainRange(range, duration) {
  const full = fullRange(duration), gap = Math.min(TRIM_STEP, full.end);
  const start = clamp(Number.isFinite(range?.start) ? range.start : 0, 0, full.end - gap);
  const end = clamp(Number.isFinite(range?.end) ? range.end : full.end, start + gap, full.end);
  return { start, end };
}

export function moveBoundary(range, boundary, value, duration) {
  const current = constrainRange(range, duration);
  if (!Number.isFinite(value)) return current;
  const gap = Math.min(TRIM_STEP, duration);
  const min = boundary === 'start' ? 0 : current.start + gap;
  const max = boundary === 'start' ? current.end - gap : duration;
  const rounded = value <= min ? min : value >= max ? max : Math.round(value / TRIM_STEP) * TRIM_STEP;
  return boundary === 'start'
    ? { start: clamp(rounded, 0, current.end - gap), end: current.end }
    : { start: current.start, end: clamp(rounded, current.start + gap, duration) };
}

export function endPreviewTime(range) {
  // Seek just inside the exclusive endpoint so the decoder shows the preceding frame.
  return Math.max(range.start, range.end - 0.001);
}

export function formatTrimTime(value) {
  if (!Number.isFinite(value)) return '—';
  const total = Math.round(Math.max(0, value) * 100);
  const hours = Math.floor(total / 360000);
  return `${hours ? `${hours}:` : ''}${String(Math.floor(total / 6000) % 60).padStart(2, '0')}:${String(Math.floor(total / 100) % 60).padStart(2, '0')}.${String(total % 100).padStart(2, '0')}`;
}
