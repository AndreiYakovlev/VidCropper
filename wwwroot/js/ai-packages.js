import { state, update, subscribe } from './state.js';

const terminal = status => ['completed', 'failed', 'cancelled'].includes(status);
export async function aiApi(path, method = 'GET', body, signal) {
  const response = await fetch(path, { method, signal, headers: { 'X-VidCropper': '1', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  const result = response.status === 204 ? null : await response.json();
  if (!response.ok) throw new Error(result?.error ?? `Ошибка сервера (${response.status}).`);
  return result;
}

// Package lifecycle UI is shared; each effect retains independent model/settings state.
export function setupModelPanel(prefix, catalogKey) {
  const $ = suffix => document.getElementById(`${prefix}-${suffix}`);
  const key = suffix => prefix + suffix;
  let catalog = null, operation = null, timer = null, loading = false, error = null, disposed = false;
  const selected = () => catalog?.packages.find(p => p.family === catalog?.[catalogKey].find(m => m.id === state[key('Model')])?.family);
  function ready() { return Boolean(catalog?.supported && selected()?.installed); }
  async function refresh() {
    if (loading) return;
    loading = true; error = null; render();
    try {
      catalog = await aiApi('/api/ai/catalog', 'GET', undefined, AbortSignal.timeout(10000));
      $('model').replaceChildren(...catalog[catalogKey].map(m => new Option(m.name, m.id)));
      update({ [key('Models')]: catalog[catalogKey], [key('Ready')]: ready() });
    } catch (e) { error = `Не удалось загрузить каталог: ${e.message}`; }
    finally { loading = false; render(); }
  }
  function render() {
    const busy = state.busyExport || state.busyAi || state.busyPreview;
    $('enabled').checked = state[key('Enabled')];
    $('enabled').disabled = busy;
    $('fields').disabled = !state[key('Enabled')] || busy || !catalog?.supported;
    $('model').value = state[key('Model')];
    $('description').textContent = catalog?.[catalogKey].find(m => m.id === state[key('Model')])?.description ?? 'Получение каталога…';
    $('retry-catalog').hidden = !error;
    $('retry-catalog').disabled = busy || loading;
    const pkg = selected();
    $('package-status').textContent = catalog && !catalog.supported ? 'AI-модуль поддерживает Windows x64.' : !pkg ? error ?? 'Получение каталога…' :
      `${pkg.installed ? `Установлена версия ${pkg.activeVersion}.` : 'Требуется установка или восстановление.'} ${pkg.updateAvailable ? 'Доступно обновление. ' : ''}${!pkg.installed || pkg.updateAvailable ? `Загрузка: ${(pkg.downloadBytes / 1024 ** 2).toFixed(1)} МиБ.` : ''}`;
    $('install').textContent = pkg?.updateAvailable ? 'Обновить AI-модуль' : pkg?.activeVersion ? 'Восстановить AI-модуль' : prefix === 'rife' ? 'Скачать полный пакет' : 'Скачать необходимые компоненты';
    $('install').disabled = !pkg;
    $('rollback').hidden = !pkg?.canRollback;
    $('check').hidden = !pkg?.installed;
  }
  function accept(snapshot) {
    operation = snapshot;
    $('operation-status').textContent = snapshot.error ?? snapshot.stage;
    $('progress').hidden = terminal(snapshot.status);
    if (snapshot.progress == null) $('progress').removeAttribute('value');
    else $('progress').value = snapshot.progress;
    $('cancel').hidden = terminal(snapshot.status);
    if (terminal(snapshot.status)) { update({ busyAi: false }); void refresh(); }
    else timer = setTimeout(poll, 500);
  }
  async function poll() {
    if (disposed || !operation) return;
    try { accept(await aiApi(`/api/ai/operations/${operation.id}`)); }
    catch (e) { $('operation-status').textContent = `${e.message} Повторное подключение…`; timer = setTimeout(poll, 2000); }
  }
  async function change(action) {
    if (state.busyAi || state.busyExport || state.busyPreview) return;
    update({ busyAi: true });
    $('operation-status').textContent = 'Подготовка AI-модуля…';
    try { accept(await aiApi(`/api/ai/models/${state[key('Model')]}/${action}`, 'POST')); }
    catch (e) { update({ busyAi: false }); $('operation-status').textContent = e.message; }
  }
  $('enabled').addEventListener('change', e => update({ [key('Enabled')]: e.target.checked }));
  $('model').addEventListener('change', e => {
    update({ [key('Model')]: e.target.value });
    update({ [key('Ready')]: ready() });
  });
  $('retry-catalog').addEventListener('click', () => void refresh());
  for (const action of ['install', 'rollback', 'check']) $(action).addEventListener('click', () => void change(action));
  $('cancel').addEventListener('click', async () => {
    if (!operation || terminal(operation.status)) return;
    clearTimeout(timer);
    try { accept(await aiApi(`/api/ai/operations/${operation.id}/cancel`, 'POST')); }
    catch (e) { $('operation-status').textContent = e.message; timer = setTimeout(poll, 1000); }
  });
  window.addEventListener('pagehide', () => {
    disposed = true; clearTimeout(timer);
    if (operation && !terminal(operation.status))
      void fetch(`/api/ai/operations/${operation.id}/cancel`, { method: 'POST', headers: { 'X-VidCropper': '1' }, keepalive: true }).catch(() => {});
  });
  subscribe(render); render(); void refresh();
}
