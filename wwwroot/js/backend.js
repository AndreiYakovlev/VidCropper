import { state, subscribe, update } from './state.js';
import { aiReady } from './interpolation.mjs';
import { processingTimeText } from './processing-time.mjs';
import { pixelCrop } from './geometry.mjs';
import { constrainRange, formatTrimTime } from './trim.mjs';

const terminal = status => ['completed', 'cancelled', 'failed'].includes(status);
const $ = id => document.getElementById(id);

async function api(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { 'X-VidCropper': '1', ...options.headers } });
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error ?? `Ошибка сервера (${response.status}).`);
  return body;
}

export function exportRequest() {
  return { mediaId: state.mediaId, crop: pixelCrop(state.crop, state), scale: state.scale,
    fps: state.fps, quality: state.quality, audio: state.audio, sourceWidth: state.width, sourceHeight: state.height,
    startSeconds: state.trim.start, endSeconds: state.trim.end,
    upscale: state.aiEnabled ? { modelId: state.aiModel, scale: state.aiScale } : null,
    interpolation: state.rifeEnabled ? { modelId: state.rifeModel, multiplier: state.rifeMultiplier } : null };
}

export function setupBackend() {
  let observedFile = null, revision = 0, media = null, job = null, xhr = null, events = null;
  let uploading = null, preparing = false, starting = false, cancelRequested = false, config = null, stopped = false;
  let status = { text: 'Выберите видео для начала работы.', progress: null, busy: false };

  function setStatus(text, progress = null, busy = false, error = false) {
    status = { text, progress, busy, error }; render();
  }
  function render() {
    const busy = starting || (job && !terminal(job.status));
    $('export').disabled = state.busyAi || state.busyPreview || state.busyPhotoExport || state.busyPhotoPreview || !aiReady(state) || stopped || state.busyDownload || !state.ready || !config || Boolean(config.error) || busy || preparing || Boolean(uploading);
    $('export').textContent = uploading ? 'Подготовка видео…' : busy ? 'Экспорт выполняется…' : 'Экспортировать видео ↗';
    $('cancel').disabled = stopped || (!uploading && !busy) || cancelRequested;
    $('export-status').textContent = status.text;
    $('export-status').classList.toggle('status-error', Boolean(status.error));
    $('export-progress').hidden = !status.busy && status.progress === null;
    if (status.progress === null) $('export-progress').removeAttribute('value');
    else $('export-progress').value = status.progress;
    $('download').hidden = job?.status !== 'completed';
    if (job?.status === 'completed') {
      $('download').href = `/api/exports/${job.id}/download`;
      $('download').download = job.fileName;
    }
    $('result-details').hidden = !job?.result;
    if (job?.result) {
      const r = job.result;
      $('result-details').textContent = `${r.width} × ${r.height} · ${r.fps.toFixed(2).replace(/\.00$/, '')} FPS · ${formatTrimTime(r.duration)} · ${(r.size / 1024 / 1024).toFixed(2)} МБ · ${r.hasAudio ? 'со звуком' : 'без звука'}`;
    }
    $('source-technical').textContent = media ? `${media.info.fps.toFixed(2).replace(/\.00$/, '')} FPS · ${media.info.codec.toUpperCase()} · ${Math.round(media.info.bitRate / 1000)} кбит/с` : '—';
    $('source-meta-note').textContent = media ? (media.info.hasAudio ? 'Метаданные прочитаны · аудиодорожка найдена' : 'Метаданные прочитаны · без аудиодорожки') :
      uploading ? 'Передача исходника локальному приложению и чтение метаданных…' : 'Выберите видео, чтобы прочитать метаданные.';
  }
  function closeEvents() { events?.close(); events = null; }
  function release(path) { return api(path, { method: 'DELETE' }).catch(() => {}); }

  function upload(file, version) {
    const id = crypto.randomUUID();
    const task = new Promise((resolve, reject) => {
      const request = new XMLHttpRequest(); xhr = request;
      request.open('PUT', `/api/media/${id}?name=${encodeURIComponent(file.name)}`);
      request.setRequestHeader('Content-Type', 'application/octet-stream');
      request.setRequestHeader('X-VidCropper', '1');
      request.upload.onprogress = event => {
        if (version !== revision) return;
        const percent = event.lengthComputable ? event.loaded / event.total * 100 : null;
        setStatus(percent === null ? 'Передача исходника…' : `Передача исходника: ${Math.floor(percent)}%`, percent, true);
      };
      request.upload.onload = () => { if (version === revision) setStatus('Чтение метаданных видео…', null, true); };
      request.onload = () => {
        let result;
        try { result = JSON.parse(request.responseText); } catch { reject(new Error('Некорректный ответ сервера.')); return; }
        if (request.status < 200 || request.status >= 300) reject(new Error(result.error ?? `Ошибка загрузки (${request.status}).`));
        else resolve(result);
      };
      request.onerror = () => reject(new Error('Нет связи с локальным сервером. Проверьте, что приложение запущено.'));
      request.onabort = () => { void release(`/api/media/${id}`); reject(new DOMException('Загрузка отменена', 'AbortError')); };
      request.send(file);
    });
    uploading = task;
    setStatus('Передача исходника локальному приложению…', 0, true);
    return task.then(result => {
      if (version !== revision) { void release(`/api/media/${result.id}`); return null; }
      media = result;
      const serverDuration = result.info.duration;
      const duration = state.ready ? Math.min(state.duration, serverDuration) : state.duration;
      update({ mediaId: result.id, sourceFps: result.info.fps, serverDuration, duration, ...(state.ready ? { trim: constrainRange(state.trim, duration) } : {}) });
      setStatus('Видео готово к экспорту. Выберите отрезок, настройте кадр и размер.');
      return result;
    }).catch(error => {
      if (version === revision) setStatus(error.name === 'AbortError' ? 'Загрузка отменена. Нажмите «Экспортировать», чтобы повторить.' : error.message, null, false, error.name !== 'AbortError');
      return null;
    }).finally(() => {
      if (version === revision) { uploading = null; xhr = null; cancelRequested = false; render(); }
    });
  }

  async function newFile(file) {
    const version = ++revision;
    const remote = state.remoteMedia;
    preparing = true;
    xhr?.abort(); xhr = null; uploading = null;
    closeEvents();
    const previousMedia = media, previousJob = job;
    media = remote; job = null; cancelRequested = false;
    $('export-timing').textContent = '';
    update({ busyExport: false, mediaId: remote?.id ?? null, sourceFps: remote?.info.fps ?? null });
    if (previousJob) await release(`/api/exports/${previousJob.id}`);
    if (previousMedia) await release(`/api/media/${previousMedia.id}`);
    if (version !== revision) return;
    if (!file) { preparing = false; render(); return; }
    try {
      config = await api('/api/config');
      if (version !== revision) return;
      if (config.error) throw new Error(config.error);
      if (file.size > config.maxUploadBytes) throw new Error(`Файл превышает лимит ${(config.maxUploadBytes / 1024 ** 3).toFixed(1)} ГБ. Лимит задаётся в настройках сервера.`);
      preparing = false;
      if (remote) {
        media = remote;
        const serverDuration = remote.info.duration;
        const duration = state.ready ? Math.min(state.duration, serverDuration) : state.duration;
        update({ sourceFps: remote.info.fps, serverDuration, duration, ...(state.ready ? { trim: constrainRange(state.trim, duration) } : {}) });
        setStatus('Видео по ссылке готово к экспорту.');
      } else await upload(file, version);
    } catch (error) { if (version === revision) setStatus(error.message, null, false, true); }
    finally { if (version === revision) { preparing = false; render(); } }
  }

  function accept(snapshot) {
    job = snapshot;
    $('export-timing').textContent = processingTimeText(job);
    const finished = terminal(job.status);
    update({ busyExport: !finished });
    if (finished) { closeEvents(); cancelRequested = false; }
    const labels = { queued: 'Подготовка экспорта…', running: `${job.stage ?? 'Экспорт'}: ${(job.stageProgress ?? job.progress).toFixed(1)}%${job.framesTotal ? ` · ${job.framesDone}/${job.framesTotalEstimated ? '≈' : ''}${job.framesTotal} кадров` : ''}`,
      finalizing: 'Проверка готового файла…', completed: 'Экспорт завершён. Видео сохранено в output.',
      cancelled: 'Экспорт отменён. Незавершённый файл удалён.', failed: job.error ?? 'Ошибка экспорта.' };
    setStatus(labels[job.status], job.status === 'cancelled' || job.status === 'failed' ? null :
      job.status === 'running' ? (job.stageProgress ?? job.progress) : job.progress, !finished, job.status === 'failed');
  }

  async function startExport() {
    if (state.busyAi || state.busyPreview || starting || state.busyDownload || (job && !terminal(job.status)) || !state.ready || stopped) return;
    const version = revision;
    starting = true; cancelRequested = false; update({ busyExport: true }); render();
    $('export-timing').textContent = '';
    try {
      if (!media) {
        if (state.remoteMedia) throw new Error('Откройте видео по ссылке заново: исходник недоступен.');
        const result = await upload(state.file, version);
        if (!result || cancelRequested || version !== revision) return;
      }
      if (job) await release(`/api/exports/${job.id}`);
      job = null;
      setStatus('Запуск FFmpeg…', null, true);
      const request = exportRequest();
      const snapshot = await api('/api/exports', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
      if (version !== revision) { await release(`/api/exports/${snapshot.id}`); return; }
      const cancel = cancelRequested;
      accept(snapshot);
      if (cancel) { accept(await api(`/api/exports/${job.id}/cancel`, { method: 'POST' })); return; }
      closeEvents();
      const stream = new EventSource(`/api/exports/${job.id}/events`); events = stream;
      stream.onmessage = event => { if (version === revision && events === stream) accept(JSON.parse(event.data)); };
      stream.onerror = () => {
        if (version !== revision || events !== stream || terminal(job.status)) return;
        setStatus('Связь с сервером прервана. Восстанавливаем статус…', null, true);
        // EventSource reconnects automatically; a snapshot also detects an expired job.
        api(`/api/exports/${job.id}`).then(value => { if (version === revision) accept(value); }).catch(error => {
          if (version === revision) setStatus(`${error.message} Экспорт мог продолжиться на сервере.`, null, true, true);
        });
      };
    } catch (error) { setStatus(error.message, null, false, true); }
    finally { starting = false; update({ busyExport: Boolean(job && !terminal(job.status)) }); render(); }
  }

  $('export').addEventListener('click', startExport);
  $('cancel').addEventListener('click', async () => {
    cancelRequested = true; render();
    if (xhr) { xhr.abort(); return; }
    if (job && !terminal(job.status)) {
      setStatus('Остановка FFmpeg…', null, true);
      try { accept(await api(`/api/exports/${job.id}/cancel`, { method: 'POST' })); }
      catch (error) { cancelRequested = false; setStatus(error.message, null, true, true); }
    }
  });
  $('shutdown').addEventListener('click', async () => {
    if ((uploading || state.busyExport || state.busyDownload) && !confirm('Остановить приложение? Текущая обработка будет отменена.')) return;
    try {
      await api('/api/shutdown', { method: 'POST' });
      stopped = true; xhr?.abort(); closeEvents(); job = null;
      update({ busyExport: true });
      setStatus('Приложение остановлено. Временные файлы очищаются. Можно закрыть вкладку.');
      $('shutdown').disabled = true;
    } catch (error) { setStatus(error.message, null, false, true); }
  });
  subscribe(() => {
    if (state.file !== observedFile) { observedFile = state.file; void newFile(state.file); }
    render();
  });
  window.addEventListener('pagehide', () => {
    xhr?.abort(); closeEvents();
    for (const path of [job && `/api/exports/${job.id}`, media && `/api/media/${media.id}`].filter(Boolean))
      void fetch(path, { method: 'DELETE', headers: { 'X-VidCropper': '1' }, keepalive: true }).catch(() => {});
  });
  api('/api/config').then(value => { config = value; if (value.error) setStatus(value.error, null, false, true); else render(); })
    .catch(error => setStatus(error.message, null, false, true));
  render();
}
