export function upscaleSize(crop, scale) {
  return {
    width: Math.floor((crop.width * scale) / 2) * 2,
    height: Math.floor((crop.height * scale) / 2) * 2,
  };
}

export function previewRange(trim, position) {
  let start = Math.max(trim.start, Math.min(position, trim.end));
  if (trim.end - start < Math.min(0.01, trim.end - trim.start))
    start = Math.max(trim.start, trim.end - 3);
  return { start, end: Math.min(trim.end, start + 3) };
}
