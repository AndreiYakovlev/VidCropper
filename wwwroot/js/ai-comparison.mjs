export function clamp(value, minimum, maximum) {
  return Math.min(maximum, Math.max(minimum, value));
}

export function splitPercent(clientX, left, width) {
  if (!(width > 0)) return 50;
  return clamp(((clientX - left) / width) * 100, 0, 100);
}

export function zoomAroundPoint(point, pan, currentScale, nextScale) {
  if (!(currentScale > 0) || !(nextScale > 0)) return { ...pan };
  return {
    x: point.x - ((point.x - pan.x) / currentScale) * nextScale,
    y: point.y - ((point.y - pan.y) / currentScale) * nextScale,
  };
}
