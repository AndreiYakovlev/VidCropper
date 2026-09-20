export function outputFps(state) {
  if (!state.rifeEnabled) return state.fps;
  return Number.isFinite(state.sourceFps) && state.sourceFps > 0 && [2, 3].includes(state.rifeMultiplier)
    ? state.sourceFps * state.rifeMultiplier : null;
}

export function fpsLabel(value) {
  return Number.isFinite(value) && value > 0 ? Number(value.toFixed(3)).toString() : '—';
}

export function aiReady(state) {
  return (!state.aiEnabled || state.aiReady) && (!state.rifeEnabled || state.rifeReady);
}
