import { state, update, updatePhoto, subscribe } from './state.js';
import { clamp, fitCrop, editCrop, pixelCrop } from './geometry.mjs';
import { photoOutputSize } from './photo.mjs';

const presets = [['free', 'Свободно', null], ['original', 'Оригинал', null], ['1:1', '1:1', 1],
  ['16:9', '16:9', 16 / 9], ['9:16', '9:16', 9 / 16], ['4:3', '4:3', 4 / 3],
  ['3:4', '3:4', 3 / 4], ['4:5', '4:5', 4 / 5], ['21:9', '21:9', 21 / 9]];
const $ = id => document.getElementById(id);
const text = (id, value) => { $(id).textContent = value; };
const formatLabel = value => value === 'jpeg' ? 'JPEG' : String(value ?? '').toUpperCase();
function sizeLabel(bytes) { return bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} МБ` : `${(bytes / 1024).toFixed(1)} КБ`; }

export function setupPhotoSettings() {
  for (const [id, label, ratio] of presets) {
    const button = document.createElement('button');
    button.type = 'button'; button.className = 'preset'; button.dataset.preset = id;
    const icon = document.createElement('span'); icon.className = 'ratio-icon'; icon.setAttribute('aria-hidden', 'true');
    const visualRatio = ratio ?? 1.4;
    icon.style.width = `${Math.min(17, 14 * visualRatio)}px`; icon.style.height = `${Math.min(16, 14 / visualRatio)}px`;
    button.append(icon, document.createTextNode(label));
    button.addEventListener('click', () => {
      const photo = state.photo;
      if (id === 'free') { updatePhoto({ preset: id, ratio: null }); return; }
      const value = id === 'original' ? photo.width / photo.height : ratio;
      updatePhoto({ preset: id, ratio: value, crop: fitCrop(photo.width, photo.height, value) });
    });
    $('photo-presets').append(button);
  }
  $('photo-reset').addEventListener('click', () => {
    const photo = state.photo;
    updatePhoto({ preset: 'free', ratio: null, crop: fitCrop(photo.width, photo.height) });
  });
  for (const key of ['x', 'y', 'width', 'height']) {
    const input = $(`photo-crop-${key}`);
    input.addEventListener('input', event => {
      const photo = state.photo;
      if (photo.ready && Number.isFinite(event.target.valueAsNumber))
        updatePhoto({ crop: editCrop(photo.crop, key, event.target.valueAsNumber, photo, photo.ratio) });
    });
    input.addEventListener('blur', render);
  }
  function setScale(value) {
    const photo = state.photo;
    updatePhoto({ scale: Number.isFinite(value) ? clamp(Math.round(value), 1, 100) : photo.scale });
  }
  $('photo-scale').addEventListener('input', event => setScale(Number(event.target.value)));
  $('photo-scale-number').addEventListener('input', event => {
    if (Number.isFinite(event.target.valueAsNumber)) setScale(event.target.valueAsNumber);
  });
  $('photo-scale-number').addEventListener('blur', render);
  for (const view of ['source', 'crop']) $(`photo-${view}-view`).addEventListener('click', () => updatePhoto({ view }));
  $('photo-format').addEventListener('change', event => updatePhoto({ format: event.target.value }));
  $('photo-quality').addEventListener('input', event => updatePhoto({ quality: Number(event.target.value) }));

  function render() {
    const photo = state.photo;
    const busy = state.busyPhotoExport || state.busyPhotoPreview || state.busyAi;
    $('photo-crop-settings').disabled = !photo.ready || busy;
    for (const id of ['photo-scale', 'photo-scale-number', 'photo-format', 'photo-quality', 'photo-open-top', 'photo-open-empty'])
      $(id).disabled = busy;
    $('photo-scale').disabled = busy || state.photoAiEnabled;
    $('photo-scale-number').disabled = busy || state.photoAiEnabled;
    for (const id of ['photo-source-view', 'photo-crop-view']) $(id).disabled = !photo.ready;
    for (const view of ['source', 'crop']) {
      const button = $(`photo-${view}-view`);
      button.classList.toggle('active', photo.view === view);
      button.setAttribute('aria-pressed', String(photo.view === view));
    }
    for (const button of $('photo-presets').children) {
      button.classList.toggle('active', button.dataset.preset === photo.preset);
      button.setAttribute('aria-pressed', String(button.dataset.preset === photo.preset));
    }
    const crop = photo.crop ? pixelCrop(photo.crop, photo) : null;
    for (const key of ['x', 'y', 'width', 'height']) {
      const input = $(`photo-crop-${key}`);
      if (document.activeElement !== input) input.value = crop ? crop[key] : '';
      input.max = key === 'x' ? photo.width - (crop?.width ?? 0) : key === 'y' ? photo.height - (crop?.height ?? 0)
        : key === 'width' ? photo.width : photo.height;
    }
    $('photo-scale').value = photo.scale;
    if (document.activeElement !== $('photo-scale-number')) $('photo-scale-number').value = photo.scale;
    $('photo-format').value = photo.format;
    $('photo-quality').value = photo.quality;
    text('photo-quality-value', photo.quality);
    $('photo-quality-row').hidden = photo.format === 'png';
    $('photo-alpha-warning').hidden = !(photo.hasAlpha && photo.format === 'jpeg');
    $('photo-scale-ai-note').hidden = !state.photoAiEnabled;

    const output = crop ? photoOutputSize(crop, photo.scale, state.photoAiEnabled ? state.photoAiScale : null) : null;
    text('photo-filename', photo.file?.name ?? 'Здесь появится информация о фото');
    text('photo-file-size', photo.file ? sizeLabel(photo.file.size) : '—');
    text('photo-source-size', photo.ready ? `${photo.width} × ${photo.height}` : '—');
    text('photo-source-format', photo.sourceFormat ? formatLabel(photo.sourceFormat) : '—');
    text('photo-source-alpha', photo.ready ? (photo.hasAlpha ? 'Есть' : 'Нет') : '—');
    text('photo-output-size', output ? `${output.width} × ${output.height} px` : '— × — px');
    text('photo-summary-crop', crop ? `${crop.width} × ${crop.height}` : '—');
    text('photo-summary-scale', state.photoAiEnabled ? `×${state.photoAiScale}` : `${photo.scale}%`);
    text('photo-summary-size', output ? `${output.width} × ${output.height}` : '—');
    $('photo-summary-ai-row').hidden = !state.photoAiEnabled;
    text('photo-summary-ai', state.photoAiModels.find(model => model.id === state.photoAiModel)?.name ?? state.photoAiModel);
    text('photo-summary-format', `${formatLabel(photo.format)}${photo.format === 'png' ? '' : ` · качество ${photo.quality}`}`);
    text('photo-format-badge', formatLabel(photo.format));
  }
  subscribe(render);
  render();
}
