import type { TreeNode } from './tree';

type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;

/** JSON metnini ağaca çevirir. Geçersizse (yorumlu JSONC, yarım dosya) hata fırlatır; çağıran kaynak görünümüne düşer. */
export function parseJsonTree(text: string): TreeNode {
  const value = JSON.parse(stripBom(text)) as Json;
  return toNode(value, undefined, '$');
}

/** JSON Lines (.jsonl): her satır ayrı bir değer. */
export function parseJsonLinesTree(text: string): TreeNode {
  const lines = stripBom(text).split(/\r?\n/).filter((line) => line.trim().length > 0);
  const values = lines.map((line) => JSON.parse(line) as Json);
  return toNode(values, undefined, '$');
}

function toNode(value: Json, label: string | undefined, path: string): TreeNode {
  if (Array.isArray(value)) {
    return {
      label,
      kind: 'array',
      summary: value.length === 0 ? '[]' : `[ ${preview(value)} ]`,
      path,
      childCount: value.length,
      children: () => value.map((item, i) => toNode(item, String(i), `${path}[${i}]`)),
    };
  }

  if (value !== null && typeof value === 'object') {
    const keys = Object.keys(value);
    return {
      label,
      kind: 'object',
      summary: keys.length === 0 ? '{}' : `{ ${keys.slice(0, 4).join(', ')}${keys.length > 4 ? ', …' : ''} }`,
      path,
      childCount: keys.length,
      children: () => keys.map((key) => toNode(value[key], key, IDENTIFIER.test(key) ? `${path}.${key}` : `${path}[${JSON.stringify(key)}]`)),
    };
  }

  return { label, kind: value === null ? 'null' : typeof value, summary: JSON.stringify(value), path };
}

function preview(items: Json[]): string {
  const parts = items.slice(0, 5).map((item) =>
    Array.isArray(item) ? '[…]' : item !== null && typeof item === 'object' ? '{…}' : truncate(JSON.stringify(item), 24),
  );
  return parts.join(', ') + (items.length > 5 ? ', …' : '');
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function stripBom(text: string): string {
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}
