import { state, subscribe, updatePhoto } from './state.js';
import { fitCrop } from './geometry.mjs';

const $ = id => document.getElementById(id);
const apiDelete = id => fetch(`/api/photos/${id}`, {
  method: 'DELETE', headers: { 'X-VidCropper': '1' }, keepalive: true,
}).catch(() => {});

export function setupPhotoSource() {
  const image = $('photo-image');
  let currentId = null;
  let upload = null;
  let revision = 0;

  function showError(message) {
    $('photo-error').textContent = message;
    $('photo-error').hidden = false;
  }

  function uploadPhoto(file, id, version) {
    return new Promise((resolve, reject) => {
      const request = new XMLHttpRequest();
      upload = request;
      request.open('PUT', `/api/photos/${id}?name=${encodeURIComponent(file.name || 'clipboard.png')}`);
      request.setRequestHeader('Content-Type', 'application/octet-stream');
      request.setRequestHeader('X-VidCropper', '1');
      request.upload.onprogress = event => {
        if (version !== revision) return;
        const progress = event.lengthComputable ? `: ${Math.floor(event.loaded / event.total * 100)}%` : '…';
        $('photo-source-note').textContent = `Передача и проверка фото${progress}`;
      };
      request.onload = () => {
        let value;
        try { value = JSON.parse(request.responseText); }
        catch { reject(new Error('Некорректный ответ локального сервера.')); return; }
        if (request.status >= 200 && request.status < 300) resolve(value);
        else reject(new Error(value?.error ?? `Ошибка загрузки (${request.status}).`));
      };
      request.onerror = () => reject(new Error('Нет связи с локальным сервером.'));
      request.onabort = () => reject(new DOMException('Загрузка отменена', 'AbortError'));
      request.send(file);
    });
  }

  async function openPhoto(file) {
    if (!file || state.busyPhotoExport || state.busyPhotoPreview) return;
    const version = ++revision;
    const id = crypto.randomUUID();
    upload?.abort();
    $('photo-error').hidden = true;
    updatePhoto({ loading: true });
    try {
      const source = await uploadPhoto(file, id, version);
      if (version !== revision) { void apiDelete(id); return; }
      const loaded = new Promise((resolve, reject) => {
        image.onload = resolve;
        image.onerror = () => reject(new Error('Не удалось открыть нормализованный предпросмотр фото.'));
      });
      image.src = `/api/photos/${id}/preview?v=${encodeURIComponent(id)}`;
      await loaded;
      if (version !== revision) { void apiDelete(id); return; }
      const previous = currentId;
      currentId = id;
      const info = source.info;
      updatePhoto({ file, mediaId: id, ready: true, loading: false, width: info.width, height: info.height,
        sourceFormat: info.format, hasAlpha: info.hasAlpha, crop: fitCrop(info.width, info.height),
        preset: 'free', ratio: null, view: 'source', format: info.format });
      $('photo-empty').hidden = true;
      if (previous) void apiDelete(previous);
    } catch (error) {
      void apiDelete(id);
      if (version === revision) {
        updatePhoto({ loading: false });
        if (error.name !== 'AbortError') showError(error.message);
      }
    } finally { if (version === revision) upload = null; }
  }

  function render() {
    const photo = state.photo;
    $('photo-file-badge').textContent = photo.loading ? 'Проверка…' : photo.ready ? 'Локальный файл' : 'Файл не выбран';
    if (!photo.loading) $('photo-source-note').textContent = photo.ready
      ? 'Ориентация применена к пикселям · метаданные будут удалены'
      : 'Выберите, перетащите или вставьте фото.';
  }
  subscribe(render);
  render();
  window.addEventListener('pagehide', () => {
    upload?.abort();
    if (currentId) void apiDelete(currentId);
  });
  return { image, openPhoto };
}
