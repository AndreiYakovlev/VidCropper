import { clamp, splitPercent, zoomAroundPoint } from './ai-comparison.mjs';

const MODES = ['split', 'toggle', 'side'];
const ZOOMS = ['fit', '100', '200', '400'];

export function setupPhotoComparison(before, after) {
  const $ = suffix => document.getElementById(`photo-ai-${suffix}`);
  const comparison = $('comparison'), viewport = $('comparison-viewport');
  const divider = $('compare-divider'), hold = $('hold-before');
  let mode = 'split', zoom = 'fit', pixelScale = null, pan = { x: 0, y: 0 }, pointer = null, split = 50;
  const paneRect = target => mode === 'side'
    ? (target?.closest?.('.ai-video-pane') ?? $('after-clip')).getBoundingClientRect()
    : viewport.getBoundingClientRect();
  const fitScale = () => {
    const rect = paneRect();
    return after.naturalWidth && rect.width ? Math.min(rect.width / after.naturalWidth, rect.height / after.naturalHeight) : 1;
  };
  const transformScale = () => pixelScale === null ? 1 : pixelScale / fitScale();
  function renderTransform() {
    const rect = paneRect();
    const width = after.naturalWidth * fitScale() * transformScale();
    const height = after.naturalHeight * fitScale() * transformScale();
    pan.x = clamp(pan.x, -Math.max(0, (width - rect.width) / 2), Math.max(0, (width - rect.width) / 2));
    pan.y = clamp(pan.y, -Math.max(0, (height - rect.height) / 2), Math.max(0, (height - rect.height) / 2));
    viewport.style.setProperty('--view-scale', String(transformScale()));
    viewport.style.setProperty('--pan-x', `${pan.x}px`); viewport.style.setProperty('--pan-y', `${pan.y}px`);
  }
  function buttons() {
    for (const value of MODES) $(`mode-${value}`).setAttribute('aria-pressed', String(mode === value));
    for (const value of ZOOMS) $(`zoom-${value}`).setAttribute('aria-pressed', String(zoom === value));
  }
  function endHold() { comparison.classList.remove('is-holding-before'); hold.classList.remove('is-active'); }
  function setMode(value) {
    mode = MODES.includes(value) ? value : 'split';
    comparison.classList.remove(...MODES.map(item => `mode-${item}`)); comparison.classList.add(`mode-${mode}`);
    endHold(); buttons(); requestAnimationFrame(renderTransform);
  }
  function setZoom(value, event = null) {
    const rect = paneRect(event?.target); const previous = transformScale();
    const point = event ? { x: event.clientX - rect.left - rect.width / 2, y: event.clientY - rect.top - rect.height / 2 } : { x: 0, y: 0 };
    zoom = value; pixelScale = value === 'fit' ? null : Number(value) / 100;
    pan = value === 'fit' ? { x: 0, y: 0 } : zoomAroundPoint(point, pan, previous, transformScale());
    buttons(); renderTransform();
  }
  function updateSplit(clientX) {
    const rect = viewport.getBoundingClientRect(); split = splitPercent(clientX, rect.left, rect.width);
    viewport.style.setProperty('--split', `${split}%`); divider.setAttribute('aria-valuenow', String(Math.round(split)));
  }
  for (const value of MODES) $(`mode-${value}`).addEventListener('click', () => setMode(value));
  for (const value of ZOOMS) $(`zoom-${value}`).addEventListener('click', () => setZoom(value));
  hold.addEventListener('pointerdown', event => { if (!hold.disabled) { event.preventDefault(); comparison.classList.add('is-holding-before'); hold.classList.add('is-active'); } });
  for (const event of ['pointerup', 'pointercancel', 'lostpointercapture', 'blur']) hold.addEventListener(event, endHold);
  divider.addEventListener('pointerdown', event => { event.preventDefault(); divider.setPointerCapture(event.pointerId); updateSplit(event.clientX); });
  divider.addEventListener('pointermove', event => { if (divider.hasPointerCapture(event.pointerId)) updateSplit(event.clientX); });
  divider.addEventListener('keydown', event => {
    if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    event.preventDefault(); split = clamp(split + (event.key === 'ArrowLeft' ? -1 : 1) * (event.shiftKey ? 10 : 2), 0, 100);
    viewport.style.setProperty('--split', `${split}%`); divider.setAttribute('aria-valuenow', String(Math.round(split)));
  });
  viewport.addEventListener('wheel', event => {
    event.preventDefault(); const fitted = fitScale(), previous = transformScale();
    const rect = paneRect(event.target); const point = { x: event.clientX - rect.left - rect.width / 2, y: event.clientY - rect.top - rect.height / 2 };
    pixelScale = clamp((pixelScale ?? fitted) * Math.exp(-event.deltaY * .0015), fitted, 4);
    zoom = pixelScale <= fitted * 1.001 ? 'fit' : 'custom'; pan = zoom === 'fit' ? { x: 0, y: 0 } : zoomAroundPoint(point, pan, previous, transformScale());
    buttons(); renderTransform();
  }, { passive: false });
  viewport.addEventListener('dblclick', event => setZoom(zoom === 'fit' ? '100' : 'fit', event));
  viewport.addEventListener('pointerdown', event => {
    if (event.button !== 0 || event.target.closest('.ai-compare-divider') || transformScale() <= 1.001) return;
    pointer = { id: event.pointerId, x: event.clientX, y: event.clientY, pan: { ...pan } };
    viewport.setPointerCapture(event.pointerId); viewport.classList.add('is-panning');
  });
  viewport.addEventListener('pointermove', event => {
    if (!pointer || event.pointerId !== pointer.id) return;
    pan = { x: pointer.pan.x + event.clientX - pointer.x, y: pointer.pan.y + event.clientY - pointer.y }; renderTransform();
  });
  for (const event of ['pointerup', 'pointercancel', 'lostpointercapture']) viewport.addEventListener(event, () => { pointer = null; viewport.classList.remove('is-panning'); });
  after.addEventListener('load', renderTransform); new ResizeObserver(renderTransform).observe(viewport);
  setMode('split'); setZoom('fit'); hold.disabled = true;
  return { setReady(value) { hold.disabled = !value; }, reset() { split = 50; viewport.style.setProperty('--split', '50%'); setMode('split'); setZoom('fit'); hold.disabled = true; } };
}
