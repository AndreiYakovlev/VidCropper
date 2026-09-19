export const state = { file: null, ready: false, width: 0, height: 0, duration: 0, serverDuration: null, trim: { start: 0, end: 0 },
  crop: null, preset: 'free', ratio: null, scale: 100, fps: 30, audio: true, view: 'source', busyExport: false };
const listeners = new Set();
export function subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); }
export function update(patch) { Object.assign(state, patch); for (const listener of listeners) listener(state); }
