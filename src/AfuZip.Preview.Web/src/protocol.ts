// Uygulama (C#) ile sayfa arasındaki mesajlar. C# tarafındaki karşılığı: AfuZip.App/Preview/WebPreviewProtocol.cs.
// WebView2'de host → sayfa: CoreWebView2.PostWebMessageAsJson, sayfa → host: chrome.webview.postMessage.

export type ViewKind = 'code' | 'markdown' | 'json' | 'xml' | 'svg';
export type Theme = 'light' | 'dark';

export interface ShowMessage {
  type: 'show';
  /** Her önizlemede artar; geç kalan yanıtlar bununla ayıklanır. */
  id: number;
  view: ViewKind;
  name: string;
  /** Arşiv içindeki yol (ör. "docs/README.md"); göreli bağlantılar buna göre çözülür. */
  path: string;
  /** Monaco dil kimliği. */
  language: string;
  text: string;
  /** Dosya önizleme sınırından büyüktü; yalnızca baş kısmı gönderildi. */
  truncated: boolean;
  /** Markdown'daki göreli resimler için: https://content.afuzip.preview/entry/<klasör>/ */
  baseUrl?: string;
}

export type HostMessage =
  | ShowMessage
  | { type: 'theme'; theme: Theme }
  | { type: 'clear' };

export type PageMessage =
  | { type: 'ready' }
  | { type: 'shown'; id: number; ms: number }
  | { type: 'openExternal'; url: string }
  | { type: 'openEntry'; path: string }
  | { type: 'error'; id: number; message: string };

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
    /** WebView2 dışında (tarayıcıda geliştirme, otomatik testler) host'un yerine geçer. */
    afuPreview?: { receive(message: HostMessage): void; outbox: PageMessage[] };
  }
}

export function post(message: PageMessage): void {
  const bridge = window.chrome?.webview;
  if (bridge) bridge.postMessage(message);
  else window.afuPreview?.outbox.push(message);
}

export function listen(handler: (message: HostMessage) => void): void {
  const bridge = window.chrome?.webview;
  bridge?.addEventListener('message', (event) => handler(event.data as HostMessage));
  window.afuPreview = { receive: handler, outbox: window.afuPreview?.outbox ?? [] };
}
