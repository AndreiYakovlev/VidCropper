import { state, update, subscribe } from './state.js';
import { fitCrop } from './geometry.mjs';
import { fullRange, endPreviewTime, formatTrimTime } from './trim.mjs';

export function formatTime(value) {
  if (!Number.isFinite(value)) return '—';
  const total = Math.floor(value), hours = Math.floor(total / 3600);
  return `${hours ? `${hours}:` : ''}${String(Math.floor(total / 60) % 60).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`;
}

export function setupPlayer() {
  const video = document.querySelector('#video'), input = document.querySelector('#file-input');
  const seek = document.querySelector('#seek'), play = document.querySelector('#play');
  const error = document.querySelector('#error');
  let objectUrl = null, generation = 0;
  let reachedEnd = false, animationFrame = null, observedTrim = state.trim;
  video.muted = true;

  function renderPosition() {
    seek.value = video.currentTime;
    document.querySelector('#current-time').textContent = formatTrimTime(video.currentTime);
  }
  function checkEnd() {
    if (!state.ready || video.paused) return;
    if (video.currentTime >= state.trim.end) {
      reachedEnd = true;
      video.pause();
      video.currentTime = endPreviewTime(state.trim);
    }
  }
  function tick() {
    animationFrame = null;
    checkEnd();
    renderPosition();
    if (!video.paused) animationFrame = requestAnimationFrame(tick);
  }
  subscribe(() => {
    seek.min = state.trim.start;
    seek.max = state.trim.end;
    if (observedTrim !== state.trim) {
      observedTrim = state.trim;
      reachedEnd = false;
      if (state.ready && (video.currentTime < state.trim.start || video.currentTime >= state.trim.end)) {
        video.pause();
        video.currentTime = state.trim.start;
      }
    }
    renderPosition();
  });

  function showError(message) { error.textContent = message; error.hidden = false; }
  function clearSource() {
    video.pause(); video.removeAttribute('src'); video.load();
    if (objectUrl) URL.revokeObjectURL(objectUrl);
    objectUrl = null;
  }
  function fail() {
    clearSource(); update({ ready: false, crop: null });
    document.querySelector('#empty').hidden = false;
    showError('Браузер не смог воспроизвести это видео. Выберите файл с поддерживаемым кодеком, например MP4/H.264. Копии для предпросмотра не создаются.');
  }
  function open(file, remoteMedia = null) {
    if (!file) return;
    if (state.busyExport) { showError('Дождитесь завершения экспорта или отмените его перед заменой видео.'); return; }
    const version = ++generation;
    video.onloadedmetadata = null; video.onerror = null;
    clearSource(); error.hidden = true;
    update({ file, remoteMedia, ready: false, crop: null, width: 0, height: 0, duration: 0, serverDuration: null, trim: fullRange(0), view: 'source', preset: 'free', ratio: null });
    seek.value = 0; document.querySelector('#current-time').textContent = '00:00.00';
    document.querySelector('#empty').hidden = false;
    if (!file.size) { showError('Файл пуст. Выберите другое видео.'); return; }
    if (!remoteMedia) objectUrl = URL.createObjectURL(file);
    video.onloadedmetadata = () => {
      if (version !== generation) return;
      if (!video.videoWidth || !video.videoHeight || !Number.isFinite(video.duration) || video.duration <= 0) { fail(); return; }
      const width = video.videoWidth, height = video.videoHeight;
      document.querySelector('#empty').hidden = true;
      const duration = Math.min(video.duration, state.serverDuration ?? video.duration);
      update({ ready: true, width, height, duration, trim: fullRange(duration), crop: fitCrop(width, height) });
    };
    video.onerror = () => { if (version === generation) fail(); };
    video.src = remoteMedia ? `/api/media/${remoteMedia.id}/preview` : objectUrl;
  }
  for (const id of ['open-top', 'open-empty']) document.getElementById(id).addEventListener('click', () => input.click());
  input.addEventListener('change', () => { open(input.files[0]); input.value = ''; });
  let dragDepth = 0;
  const indicator = document.querySelector('#drop-indicator');
  document.addEventListener('dragenter', event => {
    if (document.querySelector('dialog[open]')) { event.preventDefault(); return; }
    if (!event.dataTransfer.types.includes('Files')) return;
    event.preventDefault(); dragDepth++; indicator.hidden = false;
  });
  document.addEventListener('dragover', event => {
    if (!event.dataTransfer.types.includes('Files')) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = document.querySelector('dialog[open]') ? 'none' : 'copy';
  });
  document.addEventListener('dragleave', () => { dragDepth = Math.max(0, dragDepth - 1); if (!dragDepth) indicator.hidden = true; });
  document.addEventListener('drop', event => {
    event.preventDefault(); dragDepth = 0; indicator.hidden = true;
    if (document.querySelector('dialog[open]')) return;
    if (event.dataTransfer.files.length > 1) { showError('Выберите одно видео за раз. Текущее видео не изменено.'); return; }
    open(event.dataTransfer.files[0]);
  });
  play.addEventListener('click', async () => {
    if (!video.paused) { video.pause(); return; }
    if (reachedEnd || video.ended || video.currentTime < state.trim.start || video.currentTime >= endPreviewTime(state.trim))
      video.currentTime = state.trim.start;
    reachedEnd = false;
    try { await video.play(); } catch { showError('Не удалось начать воспроизведение. Попробуйте другой видеофайл.'); }
  });
  for (const event of ['play', 'pause', 'ended']) video.addEventListener(event, () => {
    play.textContent = video.paused ? '▶' : 'Ⅱ'; play.setAttribute('aria-label', video.paused ? 'Воспроизвести' : 'Пауза');
  });
  video.addEventListener('play', () => { if (animationFrame === null) animationFrame = requestAnimationFrame(tick); });
  video.addEventListener('pause', () => { cancelAnimationFrame(animationFrame); animationFrame = null; });
  video.addEventListener('ended', () => { reachedEnd = true; });
  video.addEventListener('timeupdate', () => { checkEnd(); renderPosition(); });
  video.addEventListener('seeked', renderPosition);
  seek.addEventListener('input', () => {
    if (!state.ready) return;
    const value = Number(seek.value);
    reachedEnd = value >= state.trim.end;
    if (reachedEnd) video.pause();
    video.currentTime = Math.max(state.trim.start, Math.min(value, endPreviewTime(state.trim)));
  });
  document.querySelector('#mute').addEventListener('click', event => {
    video.muted = !video.muted; event.target.textContent = video.muted ? 'Звук' : 'Без звука';
    event.target.setAttribute('aria-pressed', String(!video.muted));
  });
  window.addEventListener('pagehide', () => { if (objectUrl) URL.revokeObjectURL(objectUrl); });
  return { video, openRemote: source => open({ name: source.name, size: source.info.size }, source) };
}
