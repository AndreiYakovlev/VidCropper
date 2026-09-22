import { fileKind } from './photo.mjs';
import { setEditorMode } from './tabs.js';

const $ = id => document.getElementById(id);

export function setupMediaInput(openVideo, openPhoto) {
  const videoInput = $('file-input');
  const photoInput = $('photo-file-input');
  const indicator = $('drop-indicator');
  let dragDepth = 0;

  function error(message, mode) {
    const target = $(mode === 'photo' ? 'photo-error' : 'error');
    target.textContent = message;
    target.hidden = false;
  }
  function route(file, source = 'file') {
    const kind = fileKind(file);
    if (!kind) {
      error('Поддерживаются видеофайлы либо статические фото JPG, PNG и WebP.', 'video');
      return;
    }
    setEditorMode(kind);
    if (kind === 'photo') {
      if (source === 'paste' && (!file.name || file.name === 'image.png'))
        file = new File([file], 'clipboard.png', { type: file.type || 'image/png' });
      void openPhoto(file);
    } else openVideo(file);
  }

  for (const id of ['open-top', 'open-empty']) $(id).addEventListener('click', () => videoInput.click());
  for (const id of ['photo-open-top', 'photo-open-empty']) $(id).addEventListener('click', () => photoInput.click());
  videoInput.addEventListener('change', () => { if (videoInput.files[0]) route(videoInput.files[0]); videoInput.value = ''; });
  photoInput.addEventListener('change', () => { if (photoInput.files[0]) route(photoInput.files[0]); photoInput.value = ''; });

  document.addEventListener('dragenter', event => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    if (document.querySelector('dialog[open]')) return;
    dragDepth++;
    indicator.hidden = false;
  });
  document.addEventListener('dragover', event => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = document.querySelector('dialog[open]') ? 'none' : 'copy';
  });
  document.addEventListener('dragleave', () => {
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) indicator.hidden = true;
  });
  document.addEventListener('drop', event => {
    if (!event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    dragDepth = 0;
    indicator.hidden = true;
    if (document.querySelector('dialog[open]')) return;
    if (event.dataTransfer.files.length !== 1) {
      error('Откройте один файл за раз. Текущие файлы не изменены.', 'video');
      return;
    }
    route(event.dataTransfer.files[0], 'drop');
  });
  document.addEventListener('paste', event => {
    if (document.querySelector('dialog[open]')) return;
    const active = document.activeElement;
    if (active?.matches('input, textarea, select, [contenteditable="true"]')) return;
    const item = [...(event.clipboardData?.items ?? [])].find(value => value.kind === 'file' && value.type.startsWith('image/'));
    if (!item) return;
    event.preventDefault();
    const file = item.getAsFile();
    if (!file || !['image/jpeg', 'image/png', 'image/webp'].includes(file.type)) {
      error('Из буфера можно вставить статическое JPG, PNG или WebP.', 'photo');
      setEditorMode('photo');
      return;
    }
    route(file, 'paste');
  });
}
