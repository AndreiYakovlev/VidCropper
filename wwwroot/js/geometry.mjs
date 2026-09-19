export const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

export function fitCrop(width, height, ratio = null) {
  const w = ratio ? Math.min(width, height * ratio) : width;
  const h = ratio ? w / ratio : height;
  return { x: (width - w) / 2, y: (height - h) / 2, width: w, height: h };
}

export function moveCrop(crop, dx, dy, bounds) {
  return { ...crop, x: clamp(crop.x + dx, 0, bounds.width - crop.width),
    y: clamp(crop.y + dy, 0, bounds.height - crop.height) };
}

// The opposite corner stays anchored. A locked side handle expands around
// the perpendicular centre, bounded by the available source pixels.
export function resizeCrop(crop, handle, dx, dy, bounds, ratio = null) {
  const west = handle.includes('w'), east = handle.includes('e');
  const north = handle.includes('n'), south = handle.includes('s');
  const anchorX = west ? crop.x + crop.width : east ? crop.x : crop.x + crop.width / 2;
  const anchorY = north ? crop.y + crop.height : south ? crop.y : crop.y + crop.height / 2;
  const maxW = west ? anchorX : east ? bounds.width - anchorX : 2 * Math.min(anchorX, bounds.width - anchorX);
  const maxH = north ? anchorY : south ? bounds.height - anchorY : 2 * Math.min(anchorY, bounds.height - anchorY);
  let w = crop.width + (west ? -dx : east ? dx : 0);
  let h = crop.height + (north ? -dy : south ? dy : 0);
  if (ratio) {
    if (!(west || east) || ((north || south) && Math.abs(dy * ratio) > Math.abs(dx))) w = h * ratio;
    w = clamp(w, Math.min(Math.max(1, ratio), maxW, maxH * ratio), Math.min(maxW, maxH * ratio));
    h = w / ratio;
  } else {
    w = clamp(w, 1, maxW); h = clamp(h, 1, maxH);
  }
  return { x: west ? anchorX - w : east ? anchorX : anchorX - w / 2,
    y: north ? anchorY - h : south ? anchorY : anchorY - h / 2, width: w, height: h };
}

export function editCrop(crop, key, value, bounds, ratio = null) {
  if (!Number.isFinite(value)) return crop;
  if (key === 'x' || key === 'y') return moveCrop(crop, key === 'x' ? value - crop.x : 0, key === 'y' ? value - crop.y : 0, bounds);
  let w = key === 'width' ? clamp(value, 1, bounds.width) : crop.width;
  let h = key === 'height' ? clamp(value, 1, bounds.height) : crop.height;
  if (ratio) {
    if (key === 'height') w = h * ratio;
    w = Math.min(w, bounds.width, bounds.height * ratio);
    h = w / ratio;
  }
  return { x: clamp(crop.x, 0, bounds.width - w), y: clamp(crop.y, 0, bounds.height - h), width: w, height: h };
}

// Integer crop values used by the UI and future encoder. Kept inside source.
export function pixelCrop(crop, bounds) {
  const width = clamp(Math.round(crop.width), 1, bounds.width);
  const height = clamp(Math.round(crop.height), 1, bounds.height);
  return { x: clamp(Math.round(crop.x), 0, bounds.width - width),
    y: clamp(Math.round(crop.y), 0, bounds.height - height), width, height };
}

export function outputSize(crop, percent) {
  const scale = clamp(percent, 1, 100) / 100;
  return { width: Math.max(2, Math.floor(crop.width * scale / 2) * 2),
    height: Math.max(2, Math.floor(crop.height * scale / 2) * 2) };
}
