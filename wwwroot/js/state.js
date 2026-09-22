export const state = { mode: 'video', file: null, remoteMedia: null, busyDownload: false, ready: false, width: 0, height: 0, duration: 0, serverDuration: null, trim: { start: 0, end: 0 },
  mediaId: null, aiEnabled: false, aiModel: 'nomos-weak', aiScale: 2, aiModels: [], aiReady: false, busyAi: false, busyPreview: false,
  rifeEnabled: false, rifeModel: 'rife-v4.26', rifeMultiplier: 2, rifeReady: false, rifeModels: [], sourceFps: null,
  crop: null, preset: 'free', ratio: null, scale: 100, fps: 30, quality: 'maximum', audio: true, view: 'source', busyExport: false,
  photo: { file: null, mediaId: null, ready: false, loading: false, width: 0, height: 0, sourceFormat: null,
    hasAlpha: false, crop: null, preset: 'free', ratio: null, scale: 100, format: 'png', quality: 92, view: 'source' },
  photoAiEnabled: false, photoAiModel: 'nomos-weak', photoAiScale: 2, photoAiModels: [], photoAiReady: false,
  busyPhotoExport: false, busyPhotoPreview: false };
const listeners = new Set();
export function subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); }
export function update(patch) { Object.assign(state, patch); for (const listener of listeners) listener(state); }
export function updatePhoto(patch) { update({ photo: { ...state.photo, ...patch } }); }
