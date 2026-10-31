import { monaco } from '../monaco/setup';
import type { Theme } from '../protocol';

/** Wrap'in açık olduğu diller: satır uzunluğu anlam taşımayan düz metinler. */
const WRAPPED = new Set(['plaintext', 'markdown', 'restructuredtext']);

/**
 * Tek bir salt okunur Monaco editörü. Her önizlemede yeni model takılır, eskisi atılır; editörün kendisi
 * yeniden oluşturulmaz (ilk açılış ~100 ms, sonrakiler birkaç ms).
 */
export class CodeView {
  private editor: monaco.editor.IStandaloneCodeEditor | undefined;
  private model: monaco.editor.ITextModel | undefined;

  constructor(private readonly host: HTMLElement) {}

  show(text: string, language: string, name: string, theme: Theme): void {
    const editor = (this.editor ??= this.create(theme));
    const known = monaco.languages.getLanguages().some((l) => l.id === language);

    const previous = this.model;
    // Aynı URI iki modelde olamaz; sayaç benzersiz kılar, ad Monaco'nun ekran okuyucu metninde görünür.
    this.model = monaco.editor.createModel(text, known ? language : 'plaintext', monaco.Uri.from({
      scheme: 'afuzip',
      path: `/${counter++}/${encodeURIComponent(name)}`,
    }));
    editor.setModel(this.model);
    previous?.dispose();

    const lines = this.model.getLineCount();
    editor.updateOptions({
      wordWrap: WRAPPED.has(language) ? 'on' : 'off',
      minimap: { enabled: lines > 300, renderCharacters: false, maxColumn: 80 },
    });
    editor.setScrollPosition({ scrollTop: 0, scrollLeft: 0 });
  }

  setTheme(theme: Theme): void {
    monaco.editor.setTheme(theme === 'dark' ? 'vs-dark' : 'vs');
  }

  focus(): void {
    this.editor?.focus();
  }

  layout(): void {
    this.editor?.layout();
  }

  private create(theme: Theme): monaco.editor.IStandaloneCodeEditor {
    return monaco.editor.create(this.host, {
      readOnly: true,
      domReadOnly: true,
      readOnlyMessage: { value: 'Önizleme salt okunur.' },
      theme: theme === 'dark' ? 'vs-dark' : 'vs',
      automaticLayout: true,
      fontFamily: "'Cascadia Code', 'Cascadia Mono', Consolas, 'Courier New', monospace",
      fontSize: 13,
      fontLigatures: true,
      lineNumbersMinChars: 4,
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      stickyScroll: { enabled: true },
      folding: true,
      guides: { indentation: true, bracketPairs: false },
      bracketPairColorization: { enabled: true },
      renderWhitespace: 'selection',
      occurrencesHighlight: 'off',
      quickSuggestions: false,
      links: false, // arşivdeki metinde tıklanabilir bağlantı olmasın
      // Türkçe karakterler (ı, ş, ğ) "belirsiz karakter" olarak işaretlenmesin; görünmez karakterler işaretlensin.
      unicodeHighlight: { ambiguousCharacters: false, invisibleCharacters: true, nonBasicASCII: false },
      padding: { top: 8, bottom: 8 },
      contextmenu: true,
      ariaLabel: 'Dosya önizlemesi',
    });
  }
}

let counter = 0;
