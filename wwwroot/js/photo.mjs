export const photoExtensions = new Set(['jpg', 'jpeg', 'png', 'webp']);
export const videoExtensions = new Set(['mkv', 'avi', 'mov', 'webm', 'mp4', 'm4v']);

export function fileKind(file) {
  const type = String(file?.type ?? '').toLowerCase();
  const extension = String(file?.name ?? '').split('.').pop().toLowerCase();
  if (['image/jpeg', 'image/png', 'image/webp'].includes(type) || photoExtensions.has(extension)) return 'photo';
  if (type.startsWith('video/') || videoExtensions.has(extension)) return 'video';
  return null;
}

export function photoOutputSize(crop, scale, aiScale = null) {
  if (!crop) return null;
  if (aiScale !== null) return {
    width: Math.max(1, Math.round(crop.width) * aiScale),
    height: Math.max(1, Math.round(crop.height) * aiScale),
  };
  return {
    width: Math.max(1, Math.round(crop.width * scale / 100)),
    height: Math.max(1, Math.round(crop.height * scale / 100)),
  };
}
