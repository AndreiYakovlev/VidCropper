import { upscaleSize } from './upscale.mjs';
import { state, update, subscribe } from './state.js';
import { clamp, fitCrop, editCrop, outputSize, pixelCrop } from './geometry.mjs';
import { formatTime } from './player.js';
import { formatTrimTime } from './trim.mjs';

const presets = [['free', 'Свободно', null], ['original', 'Оригинал', null], ['1:1', '1:1', 1],
  ['16:9', '16:9', 16 / 9], ['9:16', '9:16', 9 / 16], ['4:3', '4:3', 4 / 3], ['3:4', '3:4', 3 / 4], ['4:5', '4:5', 4 / 5], ['21:9', '21:9', 21 / 9]];
const $ = id => document.getElementById(id);
const text = (id, value) => { $(id).textContent = value; };
function sizeLabel(bytes) { return bytes >= 1024 ** 3 ? `${(bytes / 1024 ** 3).toFixed(2)} ГБ` : bytes >= 1024 ** 2 ? `${(bytes / 1024 ** 2).toFixed(1)} МБ` : `${(bytes / 1024).toFixed(1)} КБ`; }

export function setupSettings() {
  for (const [id, label, ratio] of presets) {
    const button = document.createElement('button'); button.type = 'button'; button.className = 'preset'; button.dataset.preset = id;
    const icon = document.createElement('span'); icon.className = 'ratio-icon'; icon.setAttribute('aria-hidden', 'true');
    const r = ratio ?? 1.4; icon.style.width = `${Math.min(17, 14 * r)}px`; icon.style.height = `${Math.min(16, 14 / r)}px`;
    button.append(icon, document.createTextNode(label));
    button.addEventListener('click', () => {
      if (id === 'free') {
        update({ preset: id, ratio: null });
        return;
      }
      const value = id === 'original' ? state.width / state.height : ratio;
      update({ preset: id, ratio: value, crop: fitCrop(state.width, state.height, value) });
    });
    $('presets').append(button);
  }
  $('reset').addEventListener('click', () => update({ preset: 'free', ratio: null, crop: fitCrop(state.width, state.height) }));
  for (const key of ['x', 'y', 'width', 'height']) {
    $('crop-' + key).addEventListener('input', event => {
      if (state.ready && Number.isFinite(event.target.valueAsNumber)) {
        update({ crop: editCrop(state.crop, key, event.target.valueAsNumber, state, state.ratio) });
      }
    });
    $('crop-' + key).addEventListener('blur', render);
  }
  function scale(value) { update({ scale: Number.isFinite(value) ? clamp(Math.round(value), 1, 100) : state.scale }); }
  $('scale').addEventListener('input', event => scale(Number(event.target.value)));
  $('scale-number').addEventListener('input', event => {
    if (Number.isFinite(event.target.valueAsNumber)) scale(event.target.valueAsNumber);
  });
  $('scale-number').addEventListener('blur', render);
  $('fps').addEventListener('change', event => update({ fps: Number(event.target.value) }));
  $('quality').addEventListener('change', event => update({ quality: event.target.value }));
  $('audio').addEventListener('change', event => update({ audio: event.target.checked }));
  for (const mode of ['source', 'crop']) $(mode + '-view').addEventListener('click', () => update({ view: mode }));

  function render() {
    const busy = state.busyExport || state.busyAi;
    $('audio').checked = state.audio;
    $('crop-settings').disabled = !state.ready || busy;
    for (const id of ['scale', 'scale-number', 'fps', 'quality', 'audio', 'open-top', 'open-empty']) $(id).disabled = busy;
    for (const id of ['scale', 'scale-number']) $(id).disabled = busy || state.aiEnabled;
    $('scale-ai-note').hidden = !state.aiEnabled;
    for (const id of ['play', 'seek', 'mute', 'source-view', 'crop-view']) $(id).disabled = !state.ready;
    for (const mode of ['source', 'crop']) {
      $(mode + '-view').classList.toggle('active', state.view === mode);
      $(mode + '-view').setAttribute('aria-pressed', String(state.view === mode));
    }
    for (const button of $('presets').children) {
      button.classList.toggle('active', button.dataset.preset === state.preset);
      button.setAttribute('aria-pressed', String(button.dataset.preset === state.preset));
    }
    const crop = state.crop ? pixelCrop(state.crop, state) : null;
    for (const key of ['x', 'y', 'width', 'height']) {
      if (document.activeElement !== $('crop-' + key)) $('crop-' + key).value = crop ? crop[key] : '';
      $('crop-' + key).max = key === 'x' ? state.width - (crop?.width ?? 0) : key === 'y' ? state.height - (crop?.height ?? 0) : key === 'width' ? state.width : state.height;
    }
    $('scale').value = state.scale;
    if (document.activeElement !== $('scale-number')) $('scale-number').value = state.scale;
    text('filename', state.file?.name ?? 'Здесь появится информация о вашем видео');
    text('file-badge', state.ready ? 'Локальный файл' : state.file ? 'Нет предпросмотра' : 'Файл не выбран');
    text('file-size', state.file ? sizeLabel(state.file.size) : '—');
    text('source-size', state.ready ? `${state.width} × ${state.height}` : '—');
    text('source-duration', state.ready ? formatTime(state.duration) : '—');
    text('duration', state.ready ? formatTrimTime(state.trim.end) : '00:00.00');
    const output = crop ? (state.aiEnabled ? upscaleSize(crop, state.aiScale) : outputSize(crop, state.scale)) : null;
    text('output-size', output ? `${output.width} × ${output.height} px` : '— × — px');
    text('summary-crop', crop ? `${crop.width} × ${crop.height}` : '—');
    text('summary-scale', state.aiEnabled ? `×${state.aiScale}` : `${state.scale}%`);
    text('summary-size', output ? `${output.width} × ${output.height}` : '—');
    $('summary-ai-row').hidden = !state.aiEnabled;
    text('summary-ai', state.aiModels.find(model => model.id === state.aiModel)?.name ?? state.aiModel);
    text('summary-video', `H.264 · ${state.fps} FPS`);
    $('quality').value = state.quality;
    text('summary-quality', $('quality').selectedOptions[0].textContent);
    text('summary-audio', state.audio ? 'Сохранить при наличии' : 'Без звука');
  }
  subscribe(render); render();
}
