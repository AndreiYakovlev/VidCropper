export function formatProcessingTime(seconds) {
  const value = Number.isFinite(seconds) ? Math.max(0, Math.floor(seconds)) : 0;
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  return [hours, minutes, value % 60].map((part) => String(part).padStart(2, "0")).join(":");
}

export function processingTimeText(snapshot) {
  const elapsed = `Общее время: ${formatProcessingTime(snapshot.elapsedSeconds)}`;
  if (["completed", "cancelled", "failed"].includes(snapshot.status)) return elapsed;
  const remaining =
    snapshot.remainingSeconds == null
      ? "расчёт…"
      : `≈ ${formatProcessingTime(Math.ceil(snapshot.remainingSeconds))}`;
  return `До конца этапа: ${remaining} · ${elapsed}`;
}
