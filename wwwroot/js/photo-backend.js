import { state, subscribe, update } from './state.js';
import { pixelCrop } from './geometry.mjs';
import { processingTimeText } from './processing-time.mjs';

const $ = id => document.getElementById(id);
const terminal = status => ['completed', 'failed', 'cancelled'].includes(status);

async function api(path, options = {}) {
  const response = await fetch(path, { ...options,
    headers: { 'X-VidCropper': '1', ...options.headers } });
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? `Ошибка сервера (${response.status}).`);
  return body;
}

export function photoExportRequest() {
  const photo = state.photo;
  return { mediaId: photo.mediaId, crop: pixelCrop(photo.crop, photo), scale: photo.scale,
    sourceWidth: photo.width, sourceHeight: photo.height, format: photo.format,
    quality: photo.format === 'png' ? null : photo.quality,
    upscale: state.photoAiEnabled ? { modelId: state.photoAiModel, scale: state.photoAiScale } : null };
}

export function setupPhotoBackend() {
  let job = null, events = null, starting = false, cancelRequested = false;
  let observedMediaId = state.photo.mediaId;
  let status = { text: 'Выберите фото для начала работы.', progress: null, error: false };

  function closeEvents() { events?.close(); events = null; }
  const release = id => api(`/api/photo-exports/${id}`, { method: 'DELETE' }).catch(() => {});
  function setStatus(text, progress = null, error = false) { status = { text, progress, error }; render(); }
  function render() {
    const running = starting || (job && !terminal(job.status));
    const readyAi = !state.photoAiEnabled || state.photoAiReady;
    $('photo-export').disabled = !state.photo.ready || !state.photo.mediaId || !readyAi ||
      state.busyAi || state.busyExport || state.busyPreview || state.busyPhotoPreview || running;
    $('photo-export').textContent = running ? 'Обработка фото…' : 'Экспортировать фото ↗';
    $('photo-cancel').disabled = !running || cancelRequested;
    $('photo-export-status').textContent = status.text;
    $('photo-export-status').classList.toggle('status-error', status.error);
    $('photo-export-progress').hidden = !running && status.progress === null;
    if (status.progress === null) $('photo-export-progress').removeAttribute('value');
    else $('photo-export-progress').value = status.progress;
    $('photo-download').hidden = job?.status !== 'completed';
    if (job?.status === 'completed') {
      $('photo-download').href = `/api/photo-exports/${job.id}/download`;
      $('photo-download').download = job.fileName;
    }
    $('photo-result-details').hidden = !job?.result;
    if (job?.result) {
      const result = job.result;
      $('photo-result-details').textContent = `${result.width} × ${result.height} · ${result.format.toUpperCase()} · ${(result.size / 1024 / 1024).toFixed(2)} МБ${result.hasAlpha ? ' · с прозрачностью' : ''}`;
    }
  }
  function accept(snapshot) {
    job = snapshot;
    $('photo-export-timing').textContent = processingTimeText(snapshot);
    const finished = terminal(snapshot.status);
    update({ busyPhotoExport: !finished });
    if (finished) { closeEvents(); cancelRequested = false; }
    const labels = { queued: 'Подготовка обработки…', running: `${snapshot.stage ?? 'Обработка'}: ${(snapshot.stageProgress ?? snapshot.progress).toFixed(1)}%`,
      completed: 'Экспорт завершён. Фото сохранено в output.', cancelled: 'Обработка отменена. Незавершённые файлы удалены.',
      failed: snapshot.error ?? 'Ошибка обработки фото.' };
    setStatus(labels[snapshot.status] ?? 'Завершение обработки…', finished ? null : snapshot.progress,
      snapshot.status === 'failed');
  }
  async function startExport() {
    if (starting || state.busyPhotoExport || state.busyPhotoPreview || state.busyExport || state.busyPreview || state.busyAi || !state.photo.ready) return;
    starting = true; cancelRequested = false; update({ busyPhotoExport: true }); render();
    $('photo-export-timing').textContent = '';
    try {
      if (job) await release(job.id);
      job = null;
      const snapshot = await api('/api/photo-exports', { method: 'POST',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(photoExportRequest()) });
      accept(snapshot);
      const stream = new EventSource(`/api/photo-exports/${snapshot.id}/events`);
      events = stream;
      stream.onmessage = event => accept(JSON.parse(event.data));
      stream.onerror = () => {
        if (terminal(job.status)) return;
        setStatus('Связь с сервером прервана. Восстанавливаем статус…', null, true);
        api(`/api/photo-exports/${job.id}`).then(accept).catch(error => setStatus(error.message, null, true));
      };
    } catch (error) {
      update({ busyPhotoExport: false });
      setStatus(error.message, null, true);
    } finally { starting = false; render(); }
  }
  $('photo-export').addEventListener('click', () => void startExport());
  $('photo-cancel').addEventListener('click', async () => {
    if (!job || terminal(job.status)) return;
    cancelRequested = true; render();
    try { accept(await api(`/api/photo-exports/${job.id}/cancel`, { method: 'POST' })); }
    catch (error) { cancelRequested = false; setStatus(error.message, null, true); }
  });
  subscribe(() => {
    if (state.photo.mediaId !== observedMediaId) {
      observedMediaId = state.photo.mediaId;
      const previous = job;
      job = null; closeEvents(); cancelRequested = false;
      $('photo-export-timing').textContent = '';
      $('photo-result-details').hidden = true;
      status = { text: state.photo.ready ? 'Фото готово. Настройте crop, размер и формат.' :
        'Выберите фото для начала работы.', progress: null, error: false };
      if (previous) void release(previous.id);
    }
    render();
  });
  render();
  window.addEventListener('pagehide', () => {
    closeEvents();
    if (job) void fetch(`/api/photo-exports/${job.id}`, {
      method: 'DELETE', headers: { 'X-VidCropper': '1' }, keepalive: true,
    }).catch(() => {});
  });
}
