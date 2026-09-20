import { state, subscribe, update } from "./state.js";
import { processingTimeText } from "./processing-time.mjs";
import { exportRequest } from "./backend.js";
import { pixelCrop } from "./geometry.mjs";
import { upscaleSize } from "./upscale.mjs";

const $ = (id) => document.getElementById(id);
const terminal = (status) => ["completed", "failed", "cancelled"].includes(status);
async function api(path, method = "GET", body, signal) {
  const response = await fetch(path, {
    method,
    signal,
    headers: { "X-VidCropper": "1", "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const result = response.status === 204 ? null : await response.json();
  if (!response.ok) throw new Error(result?.error ?? `Ошибка сервера (${response.status}).`);
  return result;
}

export function setupUpscaler(video) {
  let catalog = null,
    operation = null,
    operationTimer = null,
    preview = null,
    events = null;
  let startingPreview = null;
  let catalogError = null,
    loadingCatalog = false;
  let previewSignature = null,
    generation = 0,
    previewReady = false,
    closing = false,
    disposed = false;
  const dialog = $("ai-preview-dialog"),
    before = $("ai-before"),
    after = $("ai-after");
  function selectedPackage(id = state.aiModel) {
    const model = catalog?.models.find((m) => m.id === id);
    return catalog?.packages.find((p) => p.family === model?.family);
  }
  const ready = (id) => Boolean(catalog?.supported && selectedPackage(id)?.installed);
  const signature = () => (state.crop ? JSON.stringify(exportRequest()) : null);

  async function refresh() {
    if (loadingCatalog) return;
    loadingCatalog = true;
    catalogError = null;
    render();
    try {
      catalog = await api("/api/ai/catalog", "GET", undefined, AbortSignal.timeout(10000));
      $("ai-model").replaceChildren(
        ...catalog.models.map((model) => new Option(model.name, model.id)),
      );
      update({ aiModels: catalog.models, aiReady: ready(state.aiModel) });
    } catch (error) {
      catalogError =
        "Не удалось загрузить каталог моделей. Проверьте, что локальный сервер VidCropper запущен, и повторите попытку.";
    } finally {
      loadingCatalog = false;
      render();
    }
  }
  function render() {
    const busy = state.busyExport || state.busyAi;
    $("ai-retry-catalog").hidden = !catalogError;
    $("ai-retry-catalog").disabled = busy || loadingCatalog;
    $("ai-enabled").checked = state.aiEnabled;
    $("ai-enabled").disabled = busy;
    $("ai-fields").disabled = !state.aiEnabled || busy || !catalog?.supported;
    $("ai-model").value = state.aiModel;
    $("ai-scale").value = String(state.aiScale);
    const model = catalog?.models.find((m) => m.id === state.aiModel);
    const pkg = selectedPackage();
    $("ai-description").textContent = model?.description ?? "Получение каталога моделей…";
    $("ai-native-note").hidden =
      !model || model.variableScale || model.nativeScale === state.aiScale;
    if (state.crop) {
      const crop = pixelCrop(state.crop, state),
        size = upscaleSize(crop, state.aiScale);
      $("ai-size").textContent =
        `${crop.width} × ${crop.height} → ${size.width} × ${size.height} px`;
    } else $("ai-size").textContent = "Выберите видео";
    $("ai-package-status").textContent =
      catalog && !catalog.supported
        ? "AI-модуль поддерживает Windows x64."
        : !pkg
          ? (catalogError ?? "Получение каталога…")
          : `${pkg.installed ? `Установлена версия ${pkg.activeVersion}.` : "Требуется установка или восстановление."} ${pkg.updateAvailable ? "Доступно обновление AI-модуля. " : ""}${!pkg.installed || pkg.updateAvailable ? `Загрузка пакета: ${(pkg.downloadBytes / 1024 ** 2).toFixed(1)} МиБ.` : ""}`;
    $("ai-install").textContent = pkg?.updateAvailable
      ? "Обновить AI-модуль"
      : pkg?.activeVersion
        ? "Восстановить AI-модуль"
        : "Скачать необходимые компоненты";
    $("ai-install").disabled = !pkg;
    $("ai-rollback").hidden = !pkg?.canRollback;
    $("ai-check").hidden = !pkg?.installed;
    $("ai-preview").disabled =
      !state.ready || !state.mediaId || !state.aiReady || state.busyDownload || closing;
    if (previewSignature && previewSignature !== signature()) $("ai-preview-stale").hidden = false;
  }

  $("ai-enabled").addEventListener("change", (event) =>
    update({ aiEnabled: event.target.checked }),
  );
  $("ai-retry-catalog").addEventListener("click", () => void refresh());
  $("ai-model").addEventListener("change", (event) =>
    update({ aiModel: event.target.value, aiReady: ready(event.target.value) }),
  );
  $("ai-scale").addEventListener("change", (event) =>
    update({ aiScale: Number(event.target.value) }),
  );

  function operationStatus(snapshot) {
    operation = snapshot;
    $("ai-operation-status").textContent = snapshot.error ?? snapshot.stage;
    $("ai-progress").hidden = terminal(snapshot.status);
    if (snapshot.progress === null) $("ai-progress").removeAttribute("value");
    else $("ai-progress").value = snapshot.progress;
    $("ai-cancel").hidden = terminal(snapshot.status);
    if (terminal(snapshot.status)) {
      update({ busyAi: false });
      void refresh();
    } else operationTimer = setTimeout(pollOperation, 500);
  }
  async function pollOperation() {
    if (disposed || !operation) return;
    try {
      operationStatus(await api(`/api/ai/operations/${operation.id}`));
    } catch (error) {
      $("ai-operation-status").textContent = `${error.message} Повторное подключение…`;
      operationTimer = setTimeout(pollOperation, 2000);
    }
  }
  async function changePackage(action) {
    if (state.busyExport || state.busyAi) return;
    update({ busyAi: true });
    $("ai-operation-status").textContent = "Подготовка AI-модуля…";
    try {
      operationStatus(await api(`/api/ai/models/${state.aiModel}/${action}`, "POST"));
    } catch (error) {
      update({ busyAi: false });
      $("ai-operation-status").textContent = error.message;
    }
  }
  $("ai-install").addEventListener("click", () => void changePackage("install"));
  $("ai-rollback").addEventListener("click", () => void changePackage("rollback"));
  $("ai-check").addEventListener("click", () => void changePackage("check"));
  $("ai-cancel").addEventListener("click", async () => {
    if (!operation || terminal(operation.status)) return;
    clearTimeout(operationTimer);
    try {
      operationStatus(await api(`/api/ai/operations/${operation.id}/cancel`, "POST"));
    } catch (error) {
      $("ai-operation-status").textContent = error.message;
      operationTimer = setTimeout(pollOperation, 1000);
    }
  });

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
  async function startPreview() {
    if (state.busyExport || state.busyAi || !state.mediaId || closing) return;
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
    disposed = true;
    clearTimeout(operationTimer);
    events?.close();
    const paths = [
      preview && `/api/exports/${preview.id}`,
      operation && !terminal(operation.status) && `/api/ai/operations/${operation.id}/cancel`,
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
  void refresh();
}
