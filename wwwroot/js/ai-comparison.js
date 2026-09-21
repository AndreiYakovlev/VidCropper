import { clamp, splitPercent, zoomAroundPoint } from "./ai-comparison.mjs";

const $ = (id) => document.getElementById(id);
const MODES = ["split", "toggle", "side"];
const ZOOMS = ["fit", "100", "200", "400"];

export function setupAiComparison(before, after) {
  const comparison = $("ai-comparison");
  const viewport = $("ai-comparison-viewport");
  const divider = $("ai-compare-divider");
  const hold = $("ai-hold-before");
  let mode = "split";
  let zoom = "fit";
  let pixelScale = null;
  let pan = { x: 0, y: 0 };
  let pointer = null;
  let split = 50;

  function paneRect(target = viewport) {
    if (mode !== "side") return viewport.getBoundingClientRect();
    const pane = target.closest?.(".ai-video-pane") ?? $("ai-after-clip");
    return pane.getBoundingClientRect();
  }

  function fitScale() {
    const rect = paneRect();
    if (!after.videoWidth || !after.videoHeight || !rect.width || !rect.height) return 1;
    return Math.min(rect.width / after.videoWidth, rect.height / after.videoHeight);
  }

  function transformScale() {
    const fitted = fitScale();
    return pixelScale === null ? 1 : pixelScale / fitted;
  }

  function clampPan() {
    const rect = paneRect();
    if (!after.videoWidth || !after.videoHeight || !rect.width || !rect.height) {
      pan = { x: 0, y: 0 };
      return;
    }
    const fitted = fitScale();
    const scale = transformScale();
    const width = after.videoWidth * fitted * scale;
    const height = after.videoHeight * fitted * scale;
    pan.x = clamp(pan.x, -Math.max(0, (width - rect.width) / 2), Math.max(0, (width - rect.width) / 2));
    pan.y = clamp(pan.y, -Math.max(0, (height - rect.height) / 2), Math.max(0, (height - rect.height) / 2));
  }

  function renderTransform() {
    clampPan();
    viewport.style.setProperty("--view-scale", String(transformScale()));
    viewport.style.setProperty("--pan-x", `${pan.x}px`);
    viewport.style.setProperty("--pan-y", `${pan.y}px`);
  }

  function renderButtons() {
    for (const value of MODES) $("ai-mode-" + value).setAttribute("aria-pressed", String(mode === value));
    for (const value of ZOOMS) $("ai-zoom-" + value).setAttribute("aria-pressed", String(zoom === value));
  }

  function setMode(value) {
    mode = MODES.includes(value) ? value : "split";
    comparison.classList.remove(...MODES.map(item => "mode-" + item));
    comparison.classList.add("mode-" + mode);
    endHold();
    renderButtons();
    requestAnimationFrame(renderTransform);
  }

  function setZoom(value, anchor = null) {
    if (!ZOOMS.includes(value)) return;
    const rect = paneRect(anchor?.target ?? viewport);
    const previousScale = transformScale();
    const point = anchor
      ? { x: anchor.clientX - rect.left - rect.width / 2, y: anchor.clientY - rect.top - rect.height / 2 }
      : { x: 0, y: 0 };
    zoom = value;
    pixelScale = value === "fit" ? null : Number(value) / 100;
    const nextScale = transformScale();
    pan = value === "fit" ? { x: 0, y: 0 } : zoomAroundPoint(point, pan, previousScale, nextScale);
    renderButtons();
    renderTransform();
  }

  function setContinuousZoom(nextPixelScale, event) {
    const fitted = fitScale();
    const previousScale = transformScale();
    const rect = paneRect(event.target);
    const point = { x: event.clientX - rect.left - rect.width / 2, y: event.clientY - rect.top - rect.height / 2 };
    pixelScale = clamp(nextPixelScale, fitted, 4);
    zoom = pixelScale <= fitted * 1.001 ? "fit" : "custom";
    const nextScale = transformScale();
    pan = zoom === "fit" ? { x: 0, y: 0 } : zoomAroundPoint(point, pan, previousScale, nextScale);
    renderButtons();
    renderTransform();
  }

  function updateSplit(clientX) {
    const rect = viewport.getBoundingClientRect();
    split = splitPercent(clientX, rect.left, rect.width);
    viewport.style.setProperty("--split", `${split}%`);
    divider.setAttribute("aria-valuenow", String(Math.round(split)));
  }

  function startHold(event) {
    if (hold.disabled) return;
    event.preventDefault();
    if (event.pointerId !== undefined) hold.setPointerCapture(event.pointerId);
    comparison.classList.add("is-holding-before");
    hold.classList.add("is-active");
  }

  function endHold() {
    comparison.classList.remove("is-holding-before");
    hold.classList.remove("is-active");
  }

  for (const value of MODES) $("ai-mode-" + value).addEventListener("click", () => setMode(value));
  for (const value of ZOOMS) $("ai-zoom-" + value).addEventListener("click", () => setZoom(value));
  hold.addEventListener("pointerdown", startHold);
  for (const event of ["pointerup", "pointercancel", "lostpointercapture"]) hold.addEventListener(event, endHold);
  hold.addEventListener("keydown", event => {
    if ((event.code === "Space" || event.code === "Enter") && !event.repeat) startHold(event);
  });
  hold.addEventListener("keyup", event => {
    if (event.code === "Space" || event.code === "Enter") endHold();
  });
  hold.addEventListener("blur", endHold);

  divider.addEventListener("pointerdown", event => {
    event.preventDefault();
    event.stopPropagation();
    divider.setPointerCapture(event.pointerId);
    updateSplit(event.clientX);
  });
  divider.addEventListener("pointermove", event => {
    if (divider.hasPointerCapture(event.pointerId)) updateSplit(event.clientX);
  });
  divider.addEventListener("keydown", event => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    event.preventDefault();
    split = clamp(split + (event.key === "ArrowLeft" ? -1 : 1) * (event.shiftKey ? 10 : 2), 0, 100);
    viewport.style.setProperty("--split", `${split}%`);
    divider.setAttribute("aria-valuenow", String(Math.round(split)));
  });

  viewport.addEventListener("wheel", event => {
    event.preventDefault();
    const current = pixelScale ?? fitScale();
    setContinuousZoom(current * Math.exp(-event.deltaY * 0.0015), event);
  }, { passive: false });
  viewport.addEventListener("dblclick", event => setZoom(zoom === "fit" ? "100" : "fit", event));
  viewport.addEventListener("pointerdown", event => {
    if (event.button !== 0 || event.target.closest(".ai-compare-divider") || transformScale() <= 1.001) return;
    pointer = { id: event.pointerId, x: event.clientX, y: event.clientY, pan: { ...pan } };
    viewport.setPointerCapture(event.pointerId);
    viewport.classList.add("is-panning");
  });
  viewport.addEventListener("pointermove", event => {
    if (!pointer || event.pointerId !== pointer.id) return;
    pan = { x: pointer.pan.x + event.clientX - pointer.x, y: pointer.pan.y + event.clientY - pointer.y };
    renderTransform();
  });
  for (const event of ["pointerup", "pointercancel", "lostpointercapture"])
    viewport.addEventListener(event, () => { pointer = null; viewport.classList.remove("is-panning"); });

  after.addEventListener("loadedmetadata", renderTransform);
  new ResizeObserver(renderTransform).observe(viewport);
  setMode("split");
  setZoom("fit");

  const setReady = ready => { hold.disabled = !ready; };
  return {
    setReady,
    reset() {
      split = 50;
      viewport.style.setProperty("--split", "50%");
      divider.setAttribute("aria-valuenow", "50");
      setMode("split");
      setZoom("fit");
      setReady(false);
    },
  };
}
