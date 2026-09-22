import { setupModelPanel, aiApi as api } from './ai-packages.js';
import { state, subscribe, update } from './state.js';
import { allowedScale } from './upscale.mjs';
import { pixelCrop } from './geometry.mjs';
import { photoOutputSize } from './photo.mjs';
import { photoExportRequest } from './photo-backend.js';
import { processingTimeText } from './processing-time.mjs';
import { setupPhotoComparison } from './photo-comparison.js';

const $ = id => document.getElementById(id);
const terminal = status => ['completed', 'failed', 'cancelled'].includes(status);

export function setupPhotoUpscaler() {
  setupModelPanel('photo-ai', 'models', 'photoAi');
  const dialog = $('photo-ai-preview-dialog'), before = $('photo-ai-before'), after = $('photo-ai-after');
  const comparison = setupPhotoComparison(before, after);
  let preview = null, events = null, starting = false, closing = false;

  function render() {
    const model = state.photoAiModels.find(value => value.id === state.photoAiModel);
    const scales = model?.allowedScales?.length ? model.allowedScales : [2, 3, 4];
    const select = $('photo-ai-scale');
    if (select.options.length !== scales.length || scales.some((scale, i) => select.options[i]?.value !== String(scale)))
      select.replaceChildren(...scales.map(scale => new Option(`×${scale}`, String(scale))));
    const selected = allowedScale(model, state.photoAiScale);
    if (selected !== state.photoAiScale) { update({ photoAiScale: selected }); return; }
    select.value = String(selected);
    $('photo-ai-native-note').hidden = !model || model.variableScale || model.nativeScale === selected;
    $('photo-ai-native-note').textContent = model
      ? `Модель обрабатывает в ×${model.nativeScale}, затем фото уменьшается до выбранного размера.` : '';
    if (state.photo.crop) {
      const crop = pixelCrop(state.photo.crop, state.photo), size = photoOutputSize(crop, state.photo.scale, selected);
      $('photo-ai-size').textContent = `${crop.width} × ${crop.height} → ${size.width} × ${size.height} px`;
    } else $('photo-ai-size').textContent = 'Выберите фото';
    $('photo-ai-preview').disabled = !state.photo.ready || !state.photo.mediaId || !state.photoAiEnabled ||
      !state.photoAiReady || state.busyExport || state.busyPreview || state.busyPhotoExport || state.busyPhotoPreview || state.busyAi || closing;
  }
  $('photo-ai-scale').addEventListener('change', event => update({ photoAiScale: Number(event.target.value) }));

  function accept(snapshot) {
    preview = snapshot;
    $('photo-ai-preview-timing').textContent = processingTimeText(snapshot);
    $('photo-ai-preview-progress').value = snapshot.stageProgress ?? snapshot.progress;
    $('photo-ai-preview-status').textContent = snapshot.error ?? (snapshot.status === 'completed'
      ? 'Готово · сравнение текущего crop' : snapshot.status === 'cancelled' ? 'Сравнение отменено'
        : `${snapshot.stage ?? 'Подготовка'} · ${(snapshot.stageProgress ?? snapshot.progress).toFixed(1)}%`);
    if (!terminal(snapshot.status)) return;
    events?.close(); events = null;
    update({ busyPhotoPreview: false });
    $('photo-ai-preview-progress').hidden = true;
    $('photo-ai-preview-cancel').hidden = true;
    $('photo-ai-preview-retry').hidden = snapshot.status === 'completed';
    if (snapshot.status === 'completed') {
      comparison.reset();
      let loaded = 0;
      const ready = () => { if (++loaded === 2) comparison.setReady(true); };
      before.onload = ready; after.onload = ready;
      before.src = `/api/ai/photo-previews/${snapshot.id}/before?v=${snapshot.id}`;
      after.src = `/api/ai/photo-previews/${snapshot.id}/after?v=${snapshot.id}`;
    }
  }
  async function closePreview(keepOpen = false) {
    if (closing) return false;
    closing = true;
    events?.close(); events = null;
    before.removeAttribute('src'); after.removeAttribute('src'); comparison.reset();
    try {
      if (preview) {
        if (!terminal(preview.status)) await api(`/api/photo-exports/${preview.id}/cancel`, 'POST');
        await api(`/api/photo-exports/${preview.id}`, 'DELETE');
      }
      preview = null; update({ busyPhotoPreview: false });
      if (!keepOpen) dialog.close();
      return true;
    } catch (error) {
      $('photo-ai-preview-status').textContent = `Не удалось закрыть сравнение: ${error.message}`;
      return false;
    } finally { closing = false; render(); }
  }
  async function startPreview() {
    if (starting || closing || state.busyExport || state.busyPreview || state.busyPhotoExport || state.busyPhotoPreview || state.busyAi || !state.photoAiReady) return;
    starting = true; update({ busyPhotoPreview: true }); comparison.reset();
    $('photo-ai-preview-progress').hidden = false; $('photo-ai-preview-progress').value = 0;
    $('photo-ai-preview-cancel').hidden = false; $('photo-ai-preview-retry').hidden = true;
    $('photo-ai-preview-status').textContent = 'Подготовка сравнения…';
    $('photo-ai-preview-timing').textContent = 'Общее время: 00:00:00';
    if (!dialog.open) dialog.showModal();
    try {
      preview = await api('/api/ai/photo-previews', 'POST', { export: photoExportRequest() });
      accept(preview);
      events = new EventSource(`/api/photo-exports/${preview.id}/events`);
      events.onmessage = event => accept(JSON.parse(event.data));
      events.onerror = () => {
        if (!preview || terminal(preview.status)) return;
        api(`/api/photo-exports/${preview.id}`).then(accept)
          .catch(error => { $('photo-ai-preview-status').textContent = error.message; });
      };
    } catch (error) {
      update({ busyPhotoPreview: false });
      $('photo-ai-preview-status').textContent = error.message;
      $('photo-ai-preview-progress').hidden = true; $('photo-ai-preview-cancel').hidden = true;
      $('photo-ai-preview-retry').hidden = false;
    } finally { starting = false; render(); }
  }
  $('photo-ai-preview').addEventListener('click', () => void startPreview());
  $('photo-ai-preview-close').addEventListener('click', () => void closePreview());
  $('photo-ai-preview-dismiss').addEventListener('click', () => void closePreview());
  $('photo-ai-preview-cancel').addEventListener('click', () => void closePreview(true));
  $('photo-ai-preview-retry').addEventListener('click', async () => { if (await closePreview(true)) await startPreview(); });
  dialog.addEventListener('cancel', event => { event.preventDefault(); void closePreview(); });
  subscribe(render); render();
  window.addEventListener('pagehide', () => { events?.close(); if (preview) void fetch(`/api/photo-exports/${preview.id}`, { method: 'DELETE', headers: { 'X-VidCropper': '1' }, keepalive: true }).catch(() => {}); });
}
