import { state, subscribe, update } from './state.js';

const $ = id => document.getElementById(id);
async function api(path, { method = 'GET', body, signal } = {}) {
  const response = await fetch(path, { method, signal, headers: { 'X-VidCropper': '1', ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const result = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(result?.error ?? `Ошибка сервера (${response.status}).`);
  return result;
}

export function setupLinkDialog(openRemote) {
  const dialog = $('link-dialog'), form = $('link-form'), input = $('video-url');
  const submit = $('link-submit'), error = $('link-error'), installation = $('link-installation');
  const working = $('link-working'), failure = $('link-failure');
  const openers = ['open-link-top', 'open-link-empty'].map($);
  let opener = null, controller = null, revision = 0, polling = null, operationId = null;
  let inspected = null;
  const qualityField = $('link-quality-field'), qualitySelect = $('link-quality');
  function hasInspection() { return inspected?.url === input.value.trim() && Date.now() - inspected.time < 9 * 60 * 1000; }
  function resetInspection() {
    inspected = null;
    qualityField.hidden = true;
    submit.textContent = 'Проверить ссылку';
  }
  function showSource(info, url) {
    inspected = { info, url, time: Date.now() };
    const single = info.kind === 'file';
    qualitySelect.replaceChildren(new Option(single ? 'Оригинал' : 'Лучшее доступное', 'best'));
    for (const height of info.heights) qualitySelect.add(new Option(`${height}p`, String(height)));
    qualitySelect.disabled = single || info.heights.length === 0;
    $('link-quality-help').textContent = single
      ? 'Прямая ссылка на файл. Скачаем исходное видео: отдельного выбора разрешения у этого источника нет.'
      : info.heights.length
        ? 'Доступное качество источника. Число p — высота кадра. Если формат станет недоступен, сообщим об этом без подмены качества.'
        : 'Источник не сообщает варианты разрешения. Будет скачано лучшее доступное видео.';
    qualityField.hidden = false;
    submit.textContent = 'Скачать';
  }
  resetInspection();

  function clearError() {
    error.hidden = true;
    input.removeAttribute('aria-invalid');
    submit.disabled = !input.value.trim();
    submit.textContent = hasInspection() ? 'Скачать' : 'Проверить ссылку';
  }
  function showForm() {
    installation.hidden = true;
    working.hidden = true;
    form.hidden = false;
    clearError();
    input.focus();
  }
  function stopPolling() { clearTimeout(polling); polling = null; }
  function stop() {
    revision++;
    controller?.abort(); controller = null;
    stopPolling(); operationId = null;
    update({ busyDownload: false });
  }
  function showProgress(message, progress = null) {
    $('link-working-status').textContent = message;
    if (progress === null) $('link-progress').removeAttribute('value');
    else $('link-progress').value = progress;
  }
  function poll(id, version, signal) {
    polling = setTimeout(async () => {
      try {
        const result = await api(`/api/link-operations/${id}`, { signal });
        if (version === revision && operationId === id) showProgress(result.message, result.progress);
      } catch { /* The POST response owns errors; a polling race can return 404 at either end. */ }
      if (version === revision && operationId === id) poll(id, version, signal);
    }, 400);
  }
  async function operation(path, body, version, signal) {
    const id = crypto.randomUUID(); operationId = id;
    poll(id, version, signal);
    try { return await api(path(id), { method: 'POST', body, signal }); }
    finally { if (operationId === id) { operationId = null; stopPolling(); } }
  }
  async function download(install = false) {
    if (controller || state.busyExport || !input.value.trim()) return;
    const version = ++revision;
    const current = new AbortController(); controller = current;
    const url = input.value.trim();
    const quality = $('link-quality').value;
    const height = quality === 'best' ? null : Number(quality);
    const known = hasInspection() ? inspected : null;
    failure.hidden = true;
    form.hidden = installation.hidden = true;
    working.hidden = false;
    showProgress('Проверка инструментов…');
    $('link-working-status').focus();
    update({ busyDownload: true });
    try {
      if (install) {
        await operation(id => `/api/link-tools/${id}/install`, null, version, current.signal);
      }
      const installed = await api('/api/link-tools', { signal: current.signal });
      if (version !== revision) return;
      if (!installed.sourceInspectionSupported) throw new Error('Приложение обновлено. Перезапустите VidCropper, чтобы определять тип источника.');
      if (!installed.ytDlpInstalled || !installed.denoInstalled) {
        working.hidden = true; installation.hidden = false;
        $('link-install-status').textContent = `Папка установки: ${installed.toolsDirectory}`;
        $('link-install-title').focus();
        return;
      }
      if (!known) {
        showProgress('Определение источника и доступного качества…');
        const info = await operation(id => `/api/link-inspections/${id}`, { url }, version, current.signal);
        if (version !== revision) return;
        showSource(info, url);
        showForm();
        submit.focus();
        return;
      }
      showProgress('Получение информации о видео…');
      const source = await operation(id => `/api/link-downloads/${id}`, { url, height: known.info.kind === 'file' ? null : height, inspectionId: known.info.id }, version, current.signal);
      if (version !== revision) {
        void api(`/api/media/${source.id}`, { method: 'DELETE' }).catch(() => {});
        return;
      }
      openRemote(source);
      dialog.close();
    } catch (exception) {
      if (version !== revision || exception.name === 'AbortError') return;
      resetInspection();
      showForm();
      failure.textContent = exception.message;
      failure.hidden = false;
    } finally {
      if (version === revision) { controller = null; stopPolling(); update({ busyDownload: false }); }
    }
  }
  for (const button of openers) button.addEventListener('click', () => {
    if (state.busyExport || dialog.open) return;
    opener = button;
    failure.hidden = true;
    dialog.showModal(); showForm();
  });
  for (const button of dialog.querySelectorAll('[data-close-link]'))
    button.addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => { stop(); opener?.focus(); });
  input.addEventListener('input', () => { resetInspection(); clearError(); failure.hidden = true; });
  form.addEventListener('submit', event => {
    event.preventDefault();
    if (!input.value.trim()) return;
    try {
      const url = new URL(input.value.trim());
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error();
    } catch {
      error.textContent = 'Введите корректную HTTP/HTTPS-ссылку без логина и пароля в адресе.';
      error.hidden = false; input.setAttribute('aria-invalid', 'true'); input.focus(); return;
    }
    void download();
  });
  $('link-back').addEventListener('click', showForm);
  $('link-recheck').addEventListener('click', () => void download());
  $('link-install').addEventListener('click', () => void download(true));
  $('link-abort').addEventListener('click', () => {
    stop(); showForm();
    failure.textContent = 'Загрузка отменена. Незавершённые файлы удаляются.';
    failure.hidden = false;
  });
  dialog.addEventListener('keydown', event => {
    if (event.key !== 'Tab') return;
    const focusable = [...dialog.querySelectorAll('button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled)')]
      .filter(element => element.getClientRects().length > 0);
    const first = focusable[0], last = focusable.at(-1);
    if (event.shiftKey && (document.activeElement === first || !focusable.includes(document.activeElement))) {
      event.preventDefault(); last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault(); first.focus();
    }
  });
  function render() {
    for (const button of openers) button.disabled = state.busyExport || state.busyDownload;
    if (state.busyExport && dialog.open) dialog.close();
  }
  window.addEventListener('pagehide', stop);
  subscribe(render); render();
}
