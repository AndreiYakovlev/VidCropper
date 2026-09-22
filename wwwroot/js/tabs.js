import { state, subscribe, update } from './state.js';

const modes = ['video', 'photo'];

export function setEditorMode(mode, focus = false) {
  if (!modes.includes(mode)) return;
  update({ mode });
  if (focus) document.getElementById(`${mode}-tab`).focus();
}

export function setupTabs() {
  const tabs = modes.map(mode => document.getElementById(`${mode}-tab`));
  for (const [index, tab] of tabs.entries()) {
    tab.addEventListener('click', () => setEditorMode(modes[index]));
    tab.addEventListener('keydown', event => {
      let next = null;
      if (event.key === 'ArrowUp') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'ArrowDown') next = (index + 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next === null) return;
      event.preventDefault();
      setEditorMode(modes[next], true);
    });
  }
  function render() {
    for (const mode of modes) {
      const active = state.mode === mode;
      const tab = document.getElementById(`${mode}-tab`);
      tab.classList.toggle('active', active);
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
      document.getElementById(`${mode}-panel`).hidden = !active;
    }
    for (const element of document.querySelectorAll('.mode-video-only')) element.hidden = state.mode !== 'video';
    for (const element of document.querySelectorAll('.mode-photo-only')) element.hidden = state.mode !== 'photo';
    document.querySelector('#video-tab .tab-activity').hidden = !(state.busyExport || state.busyPreview || state.busyDownload);
    document.querySelector('#photo-tab .tab-activity').hidden = !(state.busyPhotoExport || state.busyPhotoPreview);
  }
  subscribe(render);
  render();
}
