import './styles.css';
import { listen, post, type HostMessage, type ShowMessage, type Theme, type ViewKind } from './protocol';
import { CodeView } from './views/code';
import { renderMarkdown } from './views/markdown';
import { parseJsonLinesTree, parseJsonTree } from './views/json';
import { parseXmlTree } from './views/xml';
import { renderSvg } from './views/svg';
import { renderTree, type TreeNode } from './views/tree';

type Mode = 'rendered' | 'source';

const CONTENT_ENTRY = 'https://content.afuzip.preview/entry/';
const MODE_KEY = 'afuzip.preview.modes';

/** İşlenmiş görünümü olan türler; diğerleri doğrudan Monaco'da açılır. */
const RENDERABLE: Record<ViewKind, string | undefined> = {
  code: undefined,
  markdown: 'Markdown',
  json: 'JSON',
  xml: 'XML',
  svg: 'SVG',
};

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const ui = {
  toolbar: $('toolbar'),
  banner: $('banner'),
  modes: $('modes'),
  code: $('code'),
  rendered: $('rendered'),
  empty: $('empty'),
  status: $('status'),
};

const code = new CodeView(ui.code);
const modes = loadModes();
let theme: Theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
let current: ShowMessage | undefined;
let disposeRendered: (() => void) | undefined;

applyTheme(theme);

listen((message: HostMessage) => {
  switch (message.type) {
    case 'show':
      show(message);
      break;
    case 'theme':
      applyTheme(message.theme);
      break;
    case 'clear':
      clear();
      break;
  }
});

ui.modes.addEventListener('click', (event) => {
  const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-mode]');
  if (!button || !current) return;
  modes[current.view] = button.dataset.mode as Mode;
  saveModes();
  show(current);
});

document.addEventListener('click', onLinkClick);
post({ type: 'ready' });

// --- Gösterim ------------------------------------------------------------------------------------

function show(message: ShowMessage): void {
  const started = performance.now();
  current = message;
  disposeRendered?.();
  disposeRendered = undefined;

  const kindName = RENDERABLE[message.view];
  const notes: string[] = [];
  if (message.truncated) notes.push('Dosya büyük; yalnızca baş kısmı gösteriliyor.');

  let mode: Mode = kindName ? (modes[message.view] ?? 'rendered') : 'source';
  // Yarım JSON/XML ayrıştırılamaz; doğrudan kaynağı göster.
  if (mode === 'rendered' && message.truncated && (message.view === 'json' || message.view === 'xml')) {
    mode = 'source';
  }

  if (mode === 'rendered') {
    try {
      renderView(message);
    } catch (error) {
      notes.push(`${kindName} ayrıştırılamadı (${(error as Error).message}); kaynak gösteriliyor.`);
      mode = 'source';
    }
  }

  if (mode === 'source') {
    ui.rendered.hidden = true;
    ui.rendered.replaceChildren();
    ui.code.hidden = false;
    code.show(message.text, message.language, message.name, theme);
    setStatus(undefined);
  }

  ui.empty.hidden = true;
  ui.banner.textContent = notes.join(' ');
  ui.banner.hidden = notes.length === 0;
  ui.modes.hidden = !kindName;
  ui.toolbar.hidden = !kindName && notes.length === 0;
  for (const button of ui.modes.querySelectorAll<HTMLButtonElement>('button')) {
    button.setAttribute('aria-selected', String(button.dataset.mode === mode));
  }

  requestAnimationFrame(() => post({ type: 'shown', id: message.id, ms: Math.round(performance.now() - started) }));
}

function renderView(message: ShowMessage): void {
  let tree: TreeNode | undefined;
  switch (message.view) {
    case 'markdown':
      renderMarkdown(message.text, ui.rendered, { baseUrl: message.baseUrl });
      break;
    case 'json':
      tree = /\.jsonl$/i.test(message.name) ? parseJsonLinesTree(message.text) : parseJsonTree(message.text);
      break;
    case 'xml':
      tree = parseXmlTree(message.text);
      break;
    case 'svg':
      disposeRendered = renderSvg(message.text, ui.rendered);
      break;
    default:
      throw new Error('işlenmiş görünüm yok');
  }

  if (tree) ui.rendered.replaceChildren(renderTree(tree, (path) => setStatus(path)));
  setStatus(tree ? tree.path : undefined);

  ui.code.hidden = true;
  ui.rendered.hidden = false;
  ui.rendered.scrollTo(0, 0);
}

function clear(): void {
  current = undefined;
  disposeRendered?.();
  disposeRendered = undefined;
  ui.code.hidden = true;
  ui.rendered.hidden = true;
  ui.rendered.replaceChildren();
  ui.toolbar.hidden = true;
  ui.empty.hidden = false;
  setStatus(undefined);
}

function setStatus(text: string | undefined): void {
  ui.status.hidden = text === undefined;
  ui.status.textContent = text ?? '';
}

function applyTheme(next: Theme): void {
  theme = next;
  document.body.dataset.theme = next;
  code.setTheme(next);
}

// --- Bağlantılar ---------------------------------------------------------------------------------

function onLinkClick(event: MouseEvent): void {
  const anchor = (event.target as HTMLElement).closest<HTMLAnchorElement>('a[href]');
  if (!anchor) return;
  // Sayfa hiçbir zaman kendisi gezinmez: dış bağlantılar sistem tarayıcısında, arşiv içi bağlantılar uygulamada açılır.
  event.preventDefault();

  const href = anchor.getAttribute('href')!;
  if (href.startsWith('#')) {
    document.getElementById(decodeURIComponent(href.slice(1)))?.scrollIntoView({ block: 'start' });
    return;
  }

  const url = anchor.dataset.href ?? href;
  if (url.startsWith(CONTENT_ENTRY)) {
    const path = decodeURIComponent(url.slice(CONTENT_ENTRY.length).split(/[?#]/)[0]);
    post({ type: 'openEntry', path });
  } else if (/^(https?:|mailto:)/i.test(url)) {
    post({ type: 'openExternal', url });
  }
}

// --- Tercihler -----------------------------------------------------------------------------------

function loadModes(): Partial<Record<ViewKind, Mode>> {
  try {
    return JSON.parse(localStorage.getItem(MODE_KEY) ?? '{}') as Partial<Record<ViewKind, Mode>>;
  } catch {
    return {};
  }
}

function saveModes(): void {
  try {
    localStorage.setItem(MODE_KEY, JSON.stringify(modes));
  } catch {
    // Depolama kapalıysa tercih yalnızca bu oturumda geçerli.
  }
}
