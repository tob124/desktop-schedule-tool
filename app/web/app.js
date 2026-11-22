import { defineXlsxEditor, detectWorkbookFormat, loadWorkbook } from 'xlsx-vanilla-viewer';

defineXlsxEditor();

const webview = window.chrome.webview;
const notice = document.querySelector('#notice');
const empty = document.querySelector('#empty');
const menu = document.querySelector('#menu');
const browseMenu = document.querySelector('#browse-menu');
const sheetPicker = document.querySelector('#browse-sheets');
const settings = document.querySelector('#settings');
const settingsForm = document.querySelector('#settings-form');
const settingsError = document.querySelector('#settings-message');
const settingsSubmit = document.querySelector('#settings-save');
const setting = name => document.querySelector('#setting-' + name);
const editor = document.createElement('xlsx-editor');

editor.locale = 'zh-CN';
editor.readOnly = true;
editor.googleFonts = false;
editor.theme = 'light';
editor.showToolbar = false;
editor.showFormulaBar = true;
editor.hidden = true;
document.querySelector('#viewer').prepend(editor);

let revision = 0;
let displayedRevision = 0;
let sheetViews = Object.create(null);
let watchedSheet;
let applyingWorkbook = false;
let restoring = false;
let restoreGeneration = 0;
let zoom = 1;
let saveTimer;
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
let reveal;

function revealSheet() {
  if (editor.hidden || reduceMotion.matches) return;
  reveal?.cancel();
  reveal = editor.animate([{ opacity: .8 }, { opacity: 1 }], {
    duration: 160,
    easing: 'ease-out',
  });
}

// The public element API handles the workbook and selection. Its pinned
// implementation keeps both scroll axes on this native scroll container.
const scroller = editor.shadowRoot.querySelector('.xg-scroller');

function send(type, payload = {}) {
  webview.postMessage({ type, ...payload });
}

function activeSheetName() {
  return editor.workbook?.sheets[editor.activeSheet]?.name;
}

function visibleSheets() {
  return (editor.workbook?.sheets ?? [])
    .map((sheet, index) => ({ sheet, index }))
    .filter(({ sheet }) => sheet.state === 'visible');
}

function updateSheetPicker() {
  sheetPicker.replaceChildren(...visibleSheets().map(
    ({ sheet, index }) => new Option(sheet.name, String(index))
  ));
  sheetPicker.value = String(editor.activeSheet);
  sheetPicker.disabled = !sheetPicker.options.length;
}

function captureSheet() {
  const name = activeSheetName();
  if (!name || name !== watchedSheet || restoring || !scroller) return;
  sheetViews[name] = {
    Selection: editor.getSelection(),
    Left: scroller.scrollLeft,
    Top: scroller.scrollTop,
  };
}

function publishView() {
  clearTimeout(saveTimer);
  if (!displayedRevision) return;
  captureSheet();
  send('view', {
    revision: displayedRevision,
    view: { ActiveSheet: activeSheetName(), Zoom: zoom, Sheets: sheetViews },
  });
}

function scheduleSave() {
  if (restoring) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(publishView, 160);
}

function afterLayout() {
  return new Promise(resolve => {
    const fallback = setTimeout(resolve, 140);
    requestAnimationFrame(() => requestAnimationFrame(() => {
      clearTimeout(fallback);
      resolve();
    }));
  });
}

async function restoreSheet(view) {
  const generation = ++restoreGeneration;
  if (!view) {
    restoring = false;
    return;
  }
  restoring = true;
  if (view.Selection) editor.select(view.Selection);
  await afterLayout();
  if (generation !== restoreGeneration) return;
  if (scroller) {
    scroller.scrollLeft = view.Left;
    scroller.scrollTop = view.Top;
  }
  await afterLayout();
  if (generation === restoreGeneration) restoring = false;
}

function setNotice(message, error) {
  notice.textContent = message;
  notice.title = message;
  notice.dataset.error = String(error);
  if (!displayedRevision) {
    document.querySelector('#empty-text').textContent = message;
  }
}

async function showWorkbook(message) {
  revision = message.revision;
  try {
    const response = await fetch('/workbook/' + message.revision, { cache: 'no-store' });
    if (!response.ok) throw new Error('Workbook snapshot unavailable');
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (detectWorkbookFormat(bytes) !== 'xlsx') throw new Error('Expected an XLSX workbook');
    const workbook = await loadWorkbook(bytes);
    if (revision !== message.revision) return;

    if (!scroller) throw new Error('Spreadsheet scroll container unavailable');
    if (!workbook?.sheets?.length) throw new Error('No worksheets');

    const oldSheet = message.same ? activeSheetName() : null;
    if (message.same) captureSheet();
    else sheetViews = Object.assign(Object.create(null), message.view?.Sheets);
    restoring = true;
    applyingWorkbook = true;
    try {
      editor.fileName = message.name;
      editor.hidden = false;
      empty.hidden = true;
      editor.workbook = workbook;
      const wantedSheet = oldSheet ?? message.view?.ActiveSheet;
      const savedIndex = workbook.sheets.findIndex(sheet => sheet.name === wantedSheet);
      if (savedIndex >= 0) editor.setActiveSheet(savedIndex);
      watchedSheet = activeSheetName();
    } finally {
      applyingWorkbook = false;
    }
    await restoreSheet(sheetViews[watchedSheet]);
    if (revision !== message.revision) return;

    displayedRevision = message.revision;
    updateSheetPicker();
    document.querySelector('#source-name').textContent = message.name;
    document.querySelector('#source-name').title = message.name;
    document.querySelector('#open').disabled = false;
    setNotice('', false);
    send('loaded', { revision: displayedRevision });
    publishView();
  } catch (error) {
    if (revision !== message.revision) return;
    restoring = false;
    console.error(error);
    if (!displayedRevision) {
      editor.hidden = true;
      empty.hidden = false;
    }
    send('failed', { revision: message.revision, reason: String(error?.message || error) });
  }
}

scroller?.addEventListener('scroll', () => {
  captureSheet();
  scheduleSave();
}, { passive: true });

editor.addEventListener('selection-change', () => {
  captureSheet();
  scheduleSave();
});

editor.addEventListener('sheet-change', async () => {
  if (applyingWorkbook || !displayedRevision) return;
  sheetPicker.value = String(editor.activeSheet);
  const name = activeSheetName();
  if (name === watchedSheet) return;
  watchedSheet = name;
  await restoreSheet(sheetViews[name]);
  if (name === watchedSheet) scheduleSave();
});

editor.addEventListener('file-command', event => event.preventDefault());

sheetPicker.addEventListener('change', () => {
  if (!sheetPicker.disabled) editor.setActiveSheet(Number(sheetPicker.value));
});

function closeMenus() {
  menu.open = false;
  browseMenu.open = false;
}

document.body.addEventListener('click', event => {
  const command = event.target.closest('[data-command]')?.dataset.command;
  if (command) {
    if (!command.startsWith('zoom-')) closeMenus();
    send(command);
  } else if (!event.composedPath().includes(menu) && !event.composedPath().includes(browseMenu)) {
    closeMenus();
  }
});

document.body.addEventListener('pointerdown', event => {
  if (document.body.classList.contains('title-hidden') && !settings.open && event.button === 0 &&
      (event.altKey || event.target.closest('[data-drag]'))) {
    event.preventDefault();
    send('move');
  }
}, { capture: true });

document.addEventListener('keydown', event => {
  if (settings.open) return;
  if (event.key === 'Escape') closeMenus();
  if (event.key === 'F11') {
    event.preventDefault();
    if (!event.repeat) send('browse');
    return;
  }
  if (event.ctrlKey && !event.altKey && !event.shiftKey) {
    const command = { r: 'refresh', o: 'choose', ',': 'settings' }[event.key.toLowerCase()];
    if (command) {
      event.preventDefault();
      event.stopPropagation();
      send(command);
    }
  }
}, { capture: true });

function applyAppearance({ preferences, glass, titleHidden }) {
  document.body.classList.toggle('browse-only', preferences.BrowseOnly);
  document.body.classList.toggle('title-hidden', titleHidden);
  document.body.classList.toggle('glass', glass);
  document.documentElement.style.setProperty('--paper', preferences.Color);
  document.documentElement.style.setProperty('--tint', preferences.Opacity + '%');
  editor.showFormulaBar = !preferences.BrowseOnly;
}

function showSettingsError(text) {
  settingsError.textContent = text;
  settingsError.hidden = !text;
  settingsSubmit.disabled = false;
}

function updateOpacityLabel() {
  setting('percent').textContent = setting('opacity').value + '%';
  document.querySelector('#opacity-label').textContent =
    setting('blur').checked ? '衬底不透明度' : '窗口不透明度';
  document.querySelector('#glass-note').hidden = !setting('blur').checked;
}

function showSettings({ preferences, startOnLogin }) {
  setting('startup').disabled = startOnLogin == null;
  setting('startup').checked = startOnLogin === true;
  setting('browse').checked = preferences.BrowseOnly;
  setting('titlebar').checked = preferences.HideTitleBar;
  setting('color').value = preferences.Color;
  setting('blur').checked = preferences.Blur;
  setting('opacity').value = preferences.Opacity;
  updateOpacityLabel();
  showSettingsError(startOnLogin == null ? '当前无法读取 Windows 启动设置。' : '');
  closeMenus();
  if (!settings.open) settings.showModal();
}

setting('opacity').addEventListener('input', updateOpacityLabel);
setting('blur').addEventListener('change', () => {
  if (setting('blur').checked && setting('opacity').value === '100') setting('opacity').value = '85';
  updateOpacityLabel();
});
function closeSettings() {
  if (!settings.open || settingsSubmit.disabled) return;
  settings.close();
  send('settings-closed');
}

document.querySelector('#settings-cancel').addEventListener('click', closeSettings);
settings.addEventListener('cancel', event => {
  event.preventDefault();
  closeSettings();
});
settingsForm.addEventListener('submit', event => {
  event.preventDefault();
  settingsSubmit.disabled = true;
  send('settings-apply', {
    startOnLogin: setting('startup').disabled ? null : setting('startup').checked,
    appearance: {
      BrowseOnly: setting('browse').checked,
      HideTitleBar: setting('titlebar').checked,
      Color: setting('color').value,
      Blur: setting('blur').checked,
      Opacity: Number(setting('opacity').value),
    },
  });
});

webview.addEventListener('message', ({ data: message }) => {
  switch (message.type) {
    case 'load':
      void showWorkbook(message);
      break;
    case 'cancel':
      revision = message.revision;
      ++restoreGeneration;
      restoring = false;
      break;
    case 'notice':
      setNotice(message.text, message.error);
      break;
    case 'mode':
      document.querySelector('#toggle').textContent = message.expanded ? '收起' : '展开';
      document.querySelector('#browse-toggle').textContent = message.expanded ? '收起' : '展开';
      break;
    case 'appearance':
      applyAppearance(message);
      break;
    case 'desktop-reveal':
      revealSheet();
      break;
    case 'open-settings':
      showSettings(message);
      break;
    case 'settings-saved':
      settingsSubmit.disabled = false;
      closeSettings();
      break;
    case 'settings-error':
      showSettingsError(message.text);
      break;
    case 'zoom':
      zoom = message.factor;
      document.querySelector('#zoom').textContent = Math.round(zoom * 100) + '%';
      if (message.save) scheduleSave();
      break;
    case 'flush':
      publishView();
      send('flushed');
      break;
  }
});

send('ready');
