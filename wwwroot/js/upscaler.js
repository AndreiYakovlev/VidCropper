import { setupModelPanel, aiApi as api } from './ai-packages.js';
import { outputFps, fpsLabel, aiReady } from './interpolation.mjs';
import { state, subscribe, update } from "./state.js";
import { processingTimeText } from "./processing-time.mjs";
import { exportRequest } from "./backend.js";
import { pixelCrop } from "./geometry.mjs";
import { upscaleSize } from "./upscale.mjs";

const $ = (id) => document.getElementById(id);
const terminal = (status) => ["completed", "failed", "cancelled"].includes(status);
export function setupUpscaler(video) {
  setupModelPanel('ai', 'models');
  setupModelPanel('rife', 'interpolationModels');
  let preview = null, events = null, startingPreview = null;
  let previewSignature = null, generation = 0, previewReady = false, closing = false;
  const dialog = $('ai-preview-dialog'), before = $('ai-before'), after = $('ai-after');
  const signature = () => state.crop ? JSON.stringify(exportRequest()) : null;
  function render() {
    $('ai-scale').value = String(state.aiScale);
    $('rife-multiplier').value = String(state.rifeMultiplier);
    const model = state.aiModels.find(m => m.id === state.aiModel);
    $('ai-native-note').hidden = !model || model.variableScale || model.nativeScale === state.aiScale;
    if (state.crop) {
      const crop = pixelCrop(state.crop, state), size = upscaleSize(crop, state.aiScale);
      $('ai-size').textContent = `${crop.width} × ${crop.height} → ${size.width} × ${size.height} px`;
    } else $('ai-size').textContent = 'Выберите видео';
    $('rife-fps').textContent = state.sourceFps ? `${fpsLabel(state.sourceFps)} → ${fpsLabel(outputFps({ ...state, rifeEnabled: true }))} FPS · длительность сохраняется` : 'Ожидание метаданных видео';
    const unavailable = !state.ready || !state.mediaId || !aiReady(state) || state.busyDownload || state.busyExport || state.busyAi || closing;
    $('ai-preview').disabled = unavailable || !state.aiEnabled;
    $('rife-preview').disabled = unavailable || !state.rifeEnabled;
    if (previewSignature && previewSignature !== signature()) $('ai-preview-stale').hidden = false;
  }
  $('ai-scale').addEventListener('change', e => update({ aiScale: Number(e.target.value) }));
  $('rife-multiplier').addEventListener('change', e => update({ rifeMultiplier: Number(e.target.value) }));
  function pause() {
    before.pause();
    after.pause();
    $("ai-preview-play").textContent = "▶";
    $("ai-preview-play").setAttribute("aria-label", "Воспроизвести");
    $("ai-preview-play").title = "Воспроизвести";
  }
  function accept(snapshot, version) {
    if (version !== generation || closing) return;
    preview = snapshot;
    $("ai-preview-timing").textContent = processingTimeText(snapshot);
    $("ai-preview-progress").value = snapshot.stageProgress ?? snapshot.progress;
    $("ai-preview-status").textContent =
      snapshot.error ??
      (snapshot.status === "completed"
        ? "Готово · сравнение без звука"
        : snapshot.status === "cancelled"
          ? "Проба отменена"
          : `${snapshot.stage ?? "Подготовка"} · ${(snapshot.stageProgress ?? snapshot.progress).toFixed(1)}%${snapshot.framesTotal ? ` · ${snapshot.framesDone}/${snapshot.framesTotalEstimated ? "≈" : ""}${snapshot.framesTotal} кадров` : ""}`);
    if (!terminal(snapshot.status)) return;
    events?.close();
    events = null;
    update({ busyPreview: false, busyExport: false });
    $("ai-preview-progress").hidden = true;
    $("ai-preview-cancel").hidden = true;
    $("ai-preview-retry").hidden = snapshot.status === "completed";
    if (snapshot.status === "completed") {
      before.src = `/api/ai/previews/${snapshot.id}/before`;
      after.src = `/api/ai/previews/${snapshot.id}/after`;
      previewReady = true;
      $("ai-preview-seek").max = String(snapshot.result.duration);
      $("ai-preview-seek").disabled = false;
      $("ai-preview-play").disabled = false;
    }
  }
  async function closePreview(keepOpen = false) {
    if (closing) return false;
    closing = true;
    $("ai-preview-cancel").disabled = true;
    $("ai-preview-retry").disabled = true;
    $("ai-preview-status").textContent = "Отмена…";
    ++generation;
    events?.close();
    events = null;
    pause();
    for (const player of [before, after]) {
      player.removeAttribute("src");
      player.load();
    }
    previewReady = false;
    previewSignature = null;
    let previous = preview;
    preview = null;
    try {
      if (!previous && startingPreview) previous = await startingPreview.catch(() => null);
      if (previous) {
        const stopped = await api(`/api/exports/${previous.id}/cancel`, "POST");
        $("ai-preview-timing").textContent = processingTimeText(stopped);
        await api(`/api/exports/${previous.id}`, "DELETE");
      }
      $("ai-preview-status").textContent = "Проба отменена";
      $("ai-preview-progress").hidden = true;
      $("ai-preview-cancel").hidden = true;
      $("ai-preview-retry").hidden = false;
      $("ai-preview-play").disabled = true;
      $("ai-preview-seek").disabled = true;
      update({ busyPreview: false, busyExport: false });
      if (!keepOpen) dialog.close();
      return true;
    } catch (error) {
      preview = previous;
      $("ai-preview-status").textContent =
        `Не удалось отменить пробу: ${error.message}. Повторите отмену.`;
      return false;
    } finally {
      closing = false;
      $("ai-preview-cancel").disabled = false;
      $("ai-preview-retry").disabled = false;
      render();
    }
  }
  $("ai-preview-close").addEventListener("click", () => void closePreview());
  $("ai-preview-dismiss").addEventListener("click", () => void closePreview());
  $("ai-preview-cancel").addEventListener("click", () => void closePreview(true));
  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    void closePreview();
  });
  $("ai-preview-retry").addEventListener("click", async () => {
    if (await closePreview(true)) await startPreview();
  });
  $("ai-preview").addEventListener("click", () => void startPreview());
  $("rife-preview").addEventListener("click", () => void startPreview());
  async function startPreview() {
    if (state.busyExport || state.busyAi || !state.mediaId || !aiReady(state) || closing) return;
    const version = ++generation;
    const request = exportRequest();
    previewSignature = signature();
    update({ busyPreview: true, busyExport: true });
    $("ai-preview-stale").hidden = true;
    $("ai-preview-progress").hidden = false;
    $("ai-preview-progress").value = 0;
    $("ai-preview-play").disabled = true;
    $("ai-preview-seek").disabled = true;
    $("ai-preview-seek").value = 0;
    $("ai-preview-status").textContent = "Подготовка пробы…";
    $("ai-preview-timing").textContent = "Общее время: 00:00:00";
    $("ai-preview-cancel").hidden = false;
    $("ai-preview-retry").hidden = true;
    if (!dialog.open) dialog.showModal();
    try {
      startingPreview = api("/api/ai/previews", "POST", {
        export: request,
        position: video.currentTime,
      });
      const snapshot = await startingPreview;
      startingPreview = null;
      if (version !== generation) return;
      accept(snapshot, version);
      events = new EventSource(`/api/exports/${snapshot.id}/events`);
      events.onmessage = (event) => accept(JSON.parse(event.data), version);
      events.onerror = () => {
        if (version !== generation) return;
        $("ai-preview-status").textContent = "Восстановление связи с сервером…";
        api(`/api/exports/${snapshot.id}`)
          .then((value) => accept(value, version))
          .catch((error) => {
            $("ai-preview-status").textContent = error.message;
          });
      };
    } catch (error) {
      startingPreview = null;
      if (version === generation) {
        $("ai-preview-status").textContent = error.message;
        $("ai-preview-cancel").hidden = true;
        $("ai-preview-retry").hidden = false;
        update({ busyPreview: false, busyExport: false });
      }
    }
  }
  $("ai-preview-play").addEventListener("click", async () => {
    if (!previewReady) return;
    if (!after.paused) {
      pause();
      return;
    }
    if (after.ended) {
      before.currentTime = 0;
      after.currentTime = 0;
    }
    try {
      await Promise.all([before.play(), after.play()]);
      $("ai-preview-play").textContent = "⏸";
      $("ai-preview-play").setAttribute("aria-label", "Пауза");
      $("ai-preview-play").title = "Пауза";
    } catch (error) {
      pause();
      $("ai-preview-status").textContent = error.message;
    }
  });
  $("ai-preview-seek").addEventListener("input", (event) => {
    pause();
    before.currentTime = Number(event.target.value);
    after.currentTime = Number(event.target.value);
  });
  after.addEventListener("timeupdate", () => {
    $("ai-preview-seek").value = after.currentTime;
    if (Math.abs(before.currentTime - after.currentTime) > 0.08)
      before.currentTime = after.currentTime;
  });
  after.addEventListener("ended", pause);
  for (const player of [before, after])
    player.addEventListener("error", () => {
      if (player.hasAttribute("src")) {
        pause();
        $("ai-preview-status").textContent = "Браузер не смог воспроизвести пробу.";
      }
    });
  window.addEventListener("pagehide", () => {
    events?.close();
    const paths = [
      preview && `/api/exports/${preview.id}`,
    ];
    for (const path of paths.filter(Boolean))
      void fetch(path, {
        method: path.endsWith("/cancel") ? "POST" : "DELETE",
        headers: { "X-VidCropper": "1" },
        keepalive: true,
      }).catch(() => {});
  });
  subscribe(render);
  render();
}
