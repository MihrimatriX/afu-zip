import MarkdownIt from 'markdown-it';
import DOMPurify from 'dompurify';
import { monaco } from '../monaco/setup';

// Arşivdeki Markdown güvenilmez: ham HTML kapalı ve çıktı ayrıca DOMPurify'dan geçer.
const md = new MarkdownIt({ html: false, linkify: true, typographer: false });

const LANGUAGE_ALIASES: Record<string, string> = {
  cs: 'csharp', 'c#': 'csharp', ts: 'typescript', js: 'javascript', sh: 'shell', bash: 'shell', zsh: 'shell',
  ps: 'powershell', ps1: 'powershell', yml: 'yaml', py: 'python', rb: 'ruby', rs: 'rust', 'c++': 'cpp', c: 'cpp',
  hlsl: 'cpp', glsl: 'cpp', jsonc: 'json', xaml: 'xml', csproj: 'xml', html: 'html', md: 'markdown', kt: 'kotlin',
};

export interface MarkdownLinks {
  /** Arşiv içi göreli bağlantılar için temel adres (https://content.afuzip.preview/entry/<klasör>/). */
  baseUrl?: string;
}

export function renderMarkdown(text: string, target: HTMLElement, links: MarkdownLinks): void {
  const html = DOMPurify.sanitize(md.render(text), {
    USE_PROFILES: { html: true },
    FORBID_TAGS: ['style', 'form', 'input', 'button', 'iframe'],
    ALLOW_UNKNOWN_PROTOCOLS: false,
  });

  // <template> içeriği etkisizdir: resimler, adresleri aşağıda denetlenmeden yüklenmeye başlamaz.
  // Belgeye eklenince yüklenirler; bu yüzden önce şablon içinde ayıklanırlar.
  const template = document.createElement('template');
  template.innerHTML = html;
  const content = template.content;

  for (const image of content.querySelectorAll('img')) {
    const src = image.getAttribute('src') ?? '';
    const resolved = resolve(src, links.baseUrl);
    // Yalnızca arşivin kendi içeriği ve data: resimleri; uzak resimler (izleme pikselleri) yüklenmez.
    if (resolved && (resolved.startsWith('https://content.afuzip.preview/') || resolved.startsWith('data:image/'))) {
      image.src = resolved;
      image.loading = 'lazy';
    } else {
      image.replaceWith(placeholder(image.getAttribute('alt') || src));
    }
  }

  const article = document.createElement('article');
  article.className = 'markdown';
  article.append(content);

  for (const anchor of article.querySelectorAll('a[href]')) {
    const href = anchor.getAttribute('href')!;
    if (!href.startsWith('#')) anchor.setAttribute('data-href', resolve(href, links.baseUrl) ?? href);
  }

  // Başlıklara kimlik ver: README'lerdeki "#kurulum" gibi iç bağlantılar çalışsın.
  const used = new Map<string, number>();
  for (const heading of article.querySelectorAll('h1, h2, h3, h4, h5, h6')) {
    const base = slug(heading.textContent ?? '');
    const n = used.get(base) ?? 0;
    used.set(base, n + 1);
    heading.id = n === 0 ? base : `${base}-${n}`;
  }

  target.replaceChildren(article);
  void colorizeCodeBlocks(article);
}

/** Kod bloklarını Monaco'nun renklendiricisiyle boyar (editör açmadan, yalnızca token'lar). */
async function colorizeCodeBlocks(root: HTMLElement): Promise<void> {
  for (const code of root.querySelectorAll<HTMLElement>('pre > code[class*="language-"]')) {
    const raw = /language-([\w#+.-]+)/.exec(code.className)?.[1]?.toLowerCase();
    if (!raw) continue;
    const language = LANGUAGE_ALIASES[raw] ?? raw;
    if (!monaco.languages.getLanguages().some((l) => l.id === language)) continue;
    try {
      // colorize çıktısı Monaco'nun kendi kaçışladığı span'lardır; kaynak metin HTML olarak yorumlanmaz.
      code.innerHTML = await monaco.editor.colorize(code.textContent ?? '', language, { tabSize: 4 });
      code.classList.add('colorized');
    } catch {
      // Renklendirilemeyen blok düz kalır.
    }
  }
}

function resolve(url: string, base: string | undefined): string | undefined {
  try {
    return new URL(url, base ?? 'https://content.afuzip.preview/entry/').href;
  } catch {
    return undefined;
  }
}

function placeholder(text: string): HTMLElement {
  const element = document.createElement('span');
  element.className = 'image-placeholder';
  element.textContent = `🖼 ${text}`;
  element.title = 'Uzak resimler önizlemede yüklenmez.';
  return element;
}

function slug(text: string): string {
  return text
    .trim()
    .toLocaleLowerCase('tr-TR')
    .replace(/[^\p{L}\p{N}\s-]/gu, '')
    .replace(/\s+/g, '-');
}
