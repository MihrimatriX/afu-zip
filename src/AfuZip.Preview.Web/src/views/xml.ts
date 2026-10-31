import type { TreeNode } from './tree';

/**
 * XML'i DOMParser ile ayrıştırıp ağaca çevirir. "application/xml" kipinde betik çalışmaz, dış varlıklar
 * (XXE) yüklenmez; sonuç yalnızca metin olarak gösterilir.
 */
export function parseXmlTree(text: string): TreeNode {
  const doc = new DOMParser().parseFromString(text, 'application/xml');
  const error = doc.querySelector('parsererror');
  if (error) throw new Error(firstLine(error.textContent ?? 'XML ayrıştırılamadı'));
  return elementNode(doc.documentElement, `/${doc.documentElement.nodeName}`);
}

function elementNode(element: Element, path: string): TreeNode {
  const children = meaningfulChildren(element);
  const onlyText = children.length === 1 && children[0].nodeType === Node.TEXT_NODE;

  // <ad>Ahmet</ad> gibi kısa metin içeren öğeler tek satırda.
  if (children.length === 0 || (onlyText && (children[0].textContent ?? '').trim().length <= 120)) {
    const summary = document.createDocumentFragment();
    summary.append(openTag(element, children.length === 0));
    if (onlyText) {
      const value = document.createElement('span');
      value.className = 'text';
      value.textContent = children[0].textContent!.trim();
      summary.append(value, closeTag(element));
    }
    return { kind: 'element', summary, path };
  }

  const counts = new Map<string, number>();
  return {
    kind: 'element',
    summary: openTag(element, false),
    path,
    childCount: children.length,
    children: () =>
      children.map((child) => {
        if (child.nodeType === Node.ELEMENT_NODE) {
          const name = child.nodeName;
          const index = (counts.get(name) ?? 0) + 1;
          counts.set(name, index);
          return elementNode(child as Element, `${path}/${name}[${index}]`);
        }
        return leafNode(child, path);
      }),
  };
}

function leafNode(node: Node, parentPath: string): TreeNode {
  const text = (node.textContent ?? '').trim();
  switch (node.nodeType) {
    case Node.COMMENT_NODE:
      return { kind: 'comment', summary: `<!-- ${text} -->`, path: parentPath };
    case Node.CDATA_SECTION_NODE:
      return { kind: 'text', summary: `<![CDATA[${text}]]>`, path: `${parentPath}/text()` };
    case Node.PROCESSING_INSTRUCTION_NODE:
      return { kind: 'comment', summary: `<?${node.nodeName} ${text}?>`, path: parentPath };
    default:
      return { kind: 'text', summary: text, path: `${parentPath}/text()` };
  }
}

function meaningfulChildren(element: Element): ChildNode[] {
  return [...element.childNodes].filter(
    (node) => node.nodeType !== Node.TEXT_NODE || (node.textContent ?? '').trim().length > 0,
  );
}

function openTag(element: Element, selfClosing: boolean): DocumentFragment {
  const fragment = document.createDocumentFragment();
  fragment.append(span('punct', '<'), span('tag', element.nodeName));
  for (const attribute of element.attributes) {
    fragment.append(' ', span('attr', attribute.name), span('punct', '="'), span('string', attribute.value), span('punct', '"'));
  }
  fragment.append(span('punct', selfClosing ? ' />' : '>'));
  return fragment;
}

function closeTag(element: Element): DocumentFragment {
  const fragment = document.createDocumentFragment();
  fragment.append(span('punct', '</'), span('tag', element.nodeName), span('punct', '>'));
  return fragment;
}

function span(className: string, text: string): HTMLSpanElement {
  const element = document.createElement('span');
  element.className = className;
  element.textContent = text;
  return element;
}

function firstLine(text: string): string {
  return text.split('\n').find((line) => line.trim().length > 0)?.trim() ?? text;
}
