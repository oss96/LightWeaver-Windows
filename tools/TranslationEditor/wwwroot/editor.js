const $ = id => document.getElementById(id);
const pageSize = 35;
const state = { workspace: null, languages: [], drafts: new Map(), rows: [], page: 0, changed: null, changedBaseline: '', busy: false };
const fields = ['q', 'source', 'lang', 'status', 'changed', 'keys'];
let debounce;

async function api(path, method = 'GET', data) {
  const response = await fetch(path, { method, headers: method === 'GET' ? {} : { 'Content-Type': 'application/json', 'X-Translation-Editor': '1' }, body: data === undefined ? undefined : JSON.stringify(data) });
  let result;
  try { result = await response.json(); } catch { throw new Error(`The editor returned an unreadable response (${response.status}). Your unsaved edits are kept.`); }
  if (!response.ok) throw new Error(result.error || `Request failed (${response.status}). Your unsaved edits are kept.`);
  return result;
}
function message(text, error = false) { $('message').textContent = text; $('message').classList.toggle('error', error); $('message').hidden = !text; }
function locale(code) { return state.workspace.locales.find(item => item.code === code); }
function dirtyCount() { return [...state.drafts.values()].reduce((sum, entries) => sum + entries.size, 0); }
function updateActions() {
  const count = dirtyCount();
  $('save').disabled = state.busy || !count;
  $('save').textContent = state.busy === 'save' ? 'Saving…' : 'Save changes';
  $('reload').disabled = state.busy;
  $('reload').textContent = state.busy === 'load' ? 'Reloading…' : 'Reload';
  $('open-language').disabled = state.busy || !state.workspace;
  $('table-body').inert = !!state.busy;
  $('filters').inert = !!state.busy;
  document.querySelector('.table-footer').inert = !!state.busy;
  $('language-form').inert = state.busy === 'create';
  $('add-language').textContent = state.busy === 'create' ? 'Adding…' : 'Add column';
  $('dirty-count').textContent = count ? `${count} unsaved ${count === 1 ? 'translation' : 'translations'}` : 'No unsaved changes';
  $('dirty-count').classList.toggle('dirty', count > 0);
}
function cellText(code, key) { return state.drafts.get(code)?.get(key)?.text ?? locale(code).strings[key] ?? ''; }
function visibleLocales() { return state.workspace.locales.filter(item => !$('lang').value || item.code === $('lang').value); }
function cellStatus(item, key) {
  if (state.drafts.get(item.code)?.has(key)) return cellText(item.code, key).trim() ? 'translated' : 'missing';
  if (!item.strings[key]?.trim()) return 'missing';
  return item.stale.includes(key) ? 'stale' : 'translated';
}
function setDraft(code, key, text, reviewed = false) {
  if (state.busy) return;
  const item = locale(code);
  if (!state.drafts.has(code)) state.drafts.set(code, new Map());
  const entries = state.drafts.get(code);
  if (text === (item.strings[key] ?? '') && !reviewed) entries.delete(key);
  else entries.set(key, { text, sourceHash: state.workspace.sourceHashes[key] });
  if (!entries.size) state.drafts.delete(code);
  updateActions();
}
function fillSelect(id, entries, emptyLabel) {
  const selected = $(id).value;
  $(id).replaceChildren();
  if (emptyLabel) $(id).append(new Option(emptyLabel, ''));
  for (const [value, label] of entries) $(id).append(new Option(label, value));
  if ([...$(id).options].some(option => option.value === selected)) $(id).value = selected;
}
function updateOptions() {
  fillSelect('lang', state.workspace.locales.map(item => [item.code, `${item.name} · ${item.code}`]), 'All languages');
  const sources = [...new Set(Object.values(state.workspace.catalog.strings).flatMap(entry => entry.sources))].sort();
  fillSelect('source', sources.map(source => [source, source.replace('src/LightWeaver/', '')]), 'All source files');
}
function readUrl() {
  const params = new URLSearchParams(location.search);
  for (const field of fields) $(field).value = params.get(field) || (field === 'keys' ? params.getAll('key').join(',') : '');
  if ($('changed').value || $('keys').value) document.querySelector('.more-filters').open = true;
}
function writeUrl() {
  const params = new URLSearchParams();
  for (const field of fields) if ($(field).value.trim()) params.set(field, $(field).value.trim());
  history.replaceState(null, '', `${location.pathname}${params.size ? `?${params}` : ''}`);
}
async function applyFilters(resetPage = true) {
  if (!state.workspace) return;
  const baseline = $('changed').value.trim();
  try {
    if (baseline && baseline !== state.changedBaseline) {
      const result = await api(`/api/changes?baseline=${encodeURIComponent(baseline)}`);
      // A slower response must not replace a newer baseline selection.
      if ($('changed').value.trim() !== baseline) return false;
      state.changed = new Set(result.keys); state.changedBaseline = baseline;
    } else if (!baseline) { state.changed = null; state.changedBaseline = ''; }
  } catch (error) { message(error.message, true); return false; }
  const query = $('q').value.trim().toLocaleLowerCase();
  const selectedKeys = new Set($('keys').value.split(',').map(key => key.trim()).filter(Boolean));
  const selectedLocales = visibleLocales();
  const status = $('status').value;
  state.rows = Object.entries(state.workspace.catalog.strings).filter(([key, entry]) => {
    if (state.changed && !state.changed.has(key)) return false;
    if (selectedKeys.size && !selectedKeys.has(key)) return false;
    if ($('source').value && !entry.sources.includes($('source').value)) return false;
    if (query && ![key, entry.text, entry.context, ...entry.sources, ...selectedLocales.map(item => cellText(item.code, key))].join('\n').toLocaleLowerCase().includes(query)) return false;
    if (status) {
      const statuses = selectedLocales.map(item => cellStatus(item, key));
      if (status === 'translated' ? !statuses.length || !statuses.every(value => value === status) : !statuses.some(value => value === status)) return false;
    }
    return true;
  });
  if (resetPage) state.page = 0;
  writeUrl(); render(); return true;
}
function element(tag, className, text) { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; }
function render() {
  const selectedLocales = visibleLocales();
  const header = element('tr');
  header.append(element('th', '', 'English source'));
  for (const item of selectedLocales) { const th = element('th', 'translation', item.name); th.append(element('span', 'language-code', item.code)); header.append(th); }
  if (!selectedLocales.length) header.append(element('th', 'translation', 'Your translations'));
  $('table-head').replaceChildren(header);
  const pages = Math.max(1, Math.ceil(state.rows.length / pageSize));
  state.page = Math.min(state.page, pages - 1);
  const body = document.createDocumentFragment();
  for (const [key, entry] of state.rows.slice(state.page * pageSize, (state.page + 1) * pageSize)) {
    const row = element('tr');
    const source = element('td');
    source.append(element('div', 'source-text', entry.text), element('p', 'string-key', key));
    const details = element('details', 'source-details');
    details.append(element('summary', '', `${entry.sources.length} source ${entry.sources.length === 1 ? 'location' : 'locations'}`), element('p', '', entry.context));
    for (const file of entry.sources) details.append(element('p', '', file));
    source.append(details); row.append(source);
    for (const item of selectedLocales) {
      const cell = element('td', 'translation-cell');
      const input = element('textarea');
      input.value = cellText(item.code, key); input.maxLength = 16000; input.spellcheck = true;
      input.lang = item.code; input.dir = 'auto'; input.setAttribute('aria-label', `${item.name} translation for ${entry.text}`);
      input.placeholder = 'Enter a translation…';
      const info = element('div', 'cell-info');
      const badge = element('span', 'badge');
      const refreshStatus = () => {
        const draft = state.drafts.get(item.code)?.has(key);
        const status = cellStatus(item, key);
        badge.className = `badge ${draft ? 'draft' : status}`;
        badge.textContent = draft ? 'Unsaved' : { missing: 'Missing translation', stale: 'Needs review', translated: 'Translated' }[status];
        input.classList.toggle('dirty', !!draft); input.classList.toggle('stale', !draft && status === 'stale');
      };
      input.addEventListener('input', () => { setDraft(item.code, key, input.value); refreshStatus(); });
      info.append(badge);
      if (item.stale.includes(key)) {
        const review = element('button', 'review', 'Mark reviewed'); review.type = 'button';
        review.title = 'Confirm this translation matches the current English text';
        review.addEventListener('click', () => { setDraft(item.code, key, input.value, true); refreshStatus(); }); info.append(review);
      }
      refreshStatus(); cell.append(input, info); row.append(cell);
    }
    if (!selectedLocales.length) { const cell = element('td', 'no-language'); cell.append(element('p', '', 'Add a language to start translating. Each language appears as a column here.')); row.append(cell); }
    body.append(row);
  }
  $('table-body').replaceChildren(body);
  const total = Object.keys(state.workspace.catalog.strings).length;
  $('result-count').textContent = `${state.rows.length.toLocaleString()} of ${total.toLocaleString()} strings`;
  $('filter-note').textContent = state.changed ? `Created or modified since ${state.changedBaseline}` : '';
  $('empty').hidden = state.rows.length > 0;
  $('page-info').textContent = state.rows.length ? `${state.page * pageSize + 1}–${Math.min((state.page + 1) * pageSize, state.rows.length)} · Page ${state.page + 1} of ${pages}` : '0 strings';
  $('previous').disabled = state.page === 0; $('next').disabled = state.page >= pages - 1;
  updateActions();
}
async function load(initial = false) {
  if (state.busy) return;
  if (!initial && dirtyCount() && !confirm(`Reload will discard ${dirtyCount()} unsaved translations. Reload anyway?`)) return;
  const previous = { workspace: state.workspace, drafts: state.drafts, rows: state.rows, page: state.page, changed: state.changed, changedBaseline: state.changedBaseline };
  const previousFilters = fields.map(field => $(field).value);
  const restore = () => {
    Object.assign(state, previous);
    if (state.workspace) {
      updateOptions();
      fields.forEach((field, index) => { $(field).value = previousFilters[index]; });
      render();
    }
  };
  state.busy = 'load'; clearTimeout(debounce); updateActions();
  try {
    const workspace = await api('/api/workspace');
    state.workspace = workspace; state.drafts = new Map(); state.changedBaseline = ''; updateOptions();
    if (initial) readUrl();
    const filtered = await applyFilters();
    if (!filtered) { restore(); return; }
    if (!initial && filtered) message('Loaded the latest catalog and translations.');
  } catch (error) { restore(); message(error.message, true); }
  finally { state.busy = false; updateActions(); }
}
async function save() {
  if (state.busy) return;
  state.busy = 'save'; clearTimeout(debounce); updateActions();
  const count = dirtyCount();
  try {
    for (const [code, edits] of state.drafts) {
      const item = locale(code);
      const result = await api(`/api/languages/${encodeURIComponent(code)}`, 'PUT', { revision: item.revision, changes: Object.fromEntries(edits) });
      item.revision = result.revision;
      for (const [key, edit] of edits) {
        if (edit.text.trim()) item.strings[key] = edit.text; else delete item.strings[key];
        item.stale = item.stale.filter(staleKey => staleKey !== key);
      }
      state.drafts.delete(code);
    }
    // Translation edits may change the active changed/status filters.
    state.changedBaseline = '';
    if (await applyFilters(false)) message(`Saved ${count} ${count === 1 ? 'translation' : 'translations'}.`);
  } catch (error) { message(error.message, true); render(); }
  finally { state.busy = false; updateActions(); }
}
function languageOptions() {
  const query = $('language-search').value.trim().toLocaleLowerCase();
  const existing = new Set(state.workspace.locales.map(item => item.code.toLowerCase()));
  const choices = state.languages.filter(item => !existing.has(item.code.toLowerCase()) && `${item.name} ${item.englishName} ${item.code}`.toLocaleLowerCase().includes(query));
  fillSelect('language-choice', choices.map(item => [item.code, `${item.code} · ${item.englishName} · ${item.name}`]));
  languageSelection();
  $('add-language').disabled = !choices.length;
}
function languageSelection() {
  $('language-selection').textContent = $('language-choice').selectedOptions[0]?.textContent || 'No matching languages.';
}
$('open-language').addEventListener('click', async () => {
  if (state.busy) return;
  try {
    if (!state.languages.length) state.languages = await api('/api/languages');
    if (state.busy) return;
    $('language-search').value = ''; $('language-error').hidden = true; languageOptions(); $('language-dialog').showModal(); $('language-search').focus();
  } catch (error) { message(error.message, true); }
});
for (const id of ['close-language', 'cancel-language']) $(id).addEventListener('click', () => $('language-dialog').close());
$('language-dialog').addEventListener('cancel', event => { if (state.busy === 'create') event.preventDefault(); });
$('language-search').addEventListener('input', languageOptions);
$('language-choice').addEventListener('change', languageSelection);
$('language-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (state.busy) return;
  state.busy = 'create'; $('add-language').disabled = true; updateActions();
  try {
    const item = await api('/api/languages', 'POST', { language: $('language-choice').value });
    if (!state.workspace.locales.some(existing => existing.code === item.code)) state.workspace.locales.push(item);
    updateOptions(); $('lang').value = ''; $('language-dialog').close(); await applyFilters(); message(`Added ${item.name}.`);
  } catch (error) { $('language-error').textContent = error.message; $('language-error').hidden = false; }
  finally { state.busy = false; $('add-language').disabled = false; updateActions(); }
});
$('filters').addEventListener('submit', event => { event.preventDefault(); applyFilters(); });
for (const id of ['q', 'source', 'lang', 'status', 'keys']) $(id).addEventListener(id === 'q' || id === 'keys' ? 'input' : 'change', () => { clearTimeout(debounce); debounce = setTimeout(() => applyFilters(), 180); });
$('clear-filters').addEventListener('click', () => { for (const field of fields) $(field).value = ''; applyFilters(); });
$('previous').addEventListener('click', () => { state.page--; render(); document.querySelector('.table-scroll').scrollTop = 0; });
$('next').addEventListener('click', () => { state.page++; render(); document.querySelector('.table-scroll').scrollTop = 0; });
$('reload').addEventListener('click', () => load()); $('save').addEventListener('click', save);
window.addEventListener('beforeunload', event => { if (dirtyCount()) { event.preventDefault(); event.returnValue = ''; } });
window.addEventListener('popstate', () => { readUrl(); applyFilters(); });
load(true);
