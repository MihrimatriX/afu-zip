import * as monaco from './editor';
import { jsonDefaults } from 'monaco-editor/languages/features/json/register.js';
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker';
import JsonWorker from 'monaco-editor/languages/features/json/json.worker.js?worker';

declare global {
  interface Window {
    MonacoEnvironment?: { getWorker(workerId: string, label: string): Worker };
  }
}

window.MonacoEnvironment = {
  getWorker: (_workerId, label) => (label === 'json' ? new JsonWorker() : new EditorWorker()),
};

// Önizleme salt okunur: .jsonc yorumları, sondaki virgüller için kırmızı dalga göstermeye gerek yok.
jsonDefaults.setDiagnosticsOptions({ validate: false, allowComments: true, enableSchemaRequest: false });

export { monaco };
