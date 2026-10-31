/**
 * JSON ve XML ağaçları için tembel (lazy) ağaç. Çocuklar yalnızca düğüm açıldığında oluşturulur ve büyük
 * diziler parça parça gösterilir; 5 MB'lık bir JSON da DOM'u şişirmeden açılır.
 */

export interface TreeNode {
  /** Satırın sol kısmı: anahtar, indeks veya etiket adı. */
  label?: string;
  /** Kapalıyken de görünen özet ya da yaprak değer. */
  summary: Node | string;
  /** Değerin türü; renklendirme için CSS sınıfı. */
  kind: string;
  /** Hover'da durum çubuğunda gösterilen yol ($.oyuncular[3].ad, /kök/öğe[2]). */
  path: string;
  childCount?: number;
  children?: () => TreeNode[];
}

const PAGE = 500;
const AUTO_EXPAND_LIMIT = 200;

export function renderTree(root: TreeNode, onHover: (path: string) => void): HTMLElement {
  const container = document.createElement('div');
  container.className = 'tree';
  container.append(renderNode(root, 0));

  container.addEventListener('click', (event) => {
    const toggle = (event.target as HTMLElement).closest<HTMLElement>('.row');
    if (!toggle || !toggle.parentElement?.classList.contains('branch')) return;
    // Alt+tık: alt düğümleri de aç (sınırlı sayıda).
    setExpanded(toggle.parentElement, !toggle.parentElement.classList.contains('open'), event.altKey ? { budget: 2000 } : undefined);
  });

  container.addEventListener('mouseover', (event) => {
    const row = (event.target as HTMLElement).closest<HTMLElement>('.row');
    if (row?.dataset.path) onHover(row.dataset.path);
  });

  return container;
}

function renderNode(node: TreeNode, depth: number): HTMLElement {
  const element = document.createElement('div');
  const branch = !!node.children && (node.childCount ?? 1) > 0;
  element.className = branch ? 'node branch' : 'node leaf';

  const row = document.createElement('div');
  row.className = 'row';
  row.dataset.path = node.path;
  row.style.setProperty('--depth', String(depth));

  const caret = document.createElement('span');
  caret.className = 'caret';
  caret.setAttribute('aria-hidden', 'true');
  row.append(caret);

  if (node.label !== undefined) {
    const label = document.createElement('span');
    label.className = 'label';
    label.textContent = node.label;
    row.append(label);
  }

  const summary = document.createElement('span');
  summary.className = `value ${node.kind}`;
  summary.append(node.summary);
  row.append(summary);

  if (node.childCount !== undefined && branch) {
    const count = document.createElement('span');
    count.className = 'count';
    count.textContent = String(node.childCount);
    row.append(count);
  }

  element.append(row);
  if (branch) {
    (element as NodeElement).treeNode = node;
    (element as NodeElement).depth = depth;
    if (depth === 0 || (depth === 1 && (node.childCount ?? 0) <= AUTO_EXPAND_LIMIT)) setExpanded(element, true);
  }
  return element;
}

interface NodeElement extends HTMLElement {
  treeNode?: TreeNode;
  depth?: number;
  rendered?: boolean;
}

function setExpanded(element: HTMLElement, open: boolean, recursive?: { budget: number }): void {
  const el = element as NodeElement;
  el.classList.toggle('open', open);
  el.querySelector(':scope > .row')?.setAttribute('aria-expanded', String(open));
  if (!open || el.rendered || !el.treeNode?.children) {
    if (open && recursive) expandChildren(el, recursive);
    return;
  }

  el.rendered = true;
  const list = document.createElement('div');
  list.className = 'children';
  el.append(list);
  appendPage(list, el.treeNode.children(), 0, (el.depth ?? 0) + 1);
  if (recursive) expandChildren(el, recursive);
}

function expandChildren(el: NodeElement, recursive: { budget: number }): void {
  for (const child of el.querySelectorAll<HTMLElement>(':scope > .children > .branch')) {
    if (--recursive.budget <= 0) return;
    setExpanded(child, true, recursive);
  }
}

function appendPage(list: HTMLElement, children: TreeNode[], start: number, depth: number): void {
  const end = Math.min(children.length, start + PAGE);
  const fragment = document.createDocumentFragment();
  for (let i = start; i < end; i++) fragment.append(renderNode(children[i], depth));
  list.append(fragment);

  if (end < children.length) {
    const more = document.createElement('button');
    more.type = 'button';
    more.className = 'more';
    more.style.setProperty('--depth', String(depth));
    more.textContent = `${(children.length - end).toLocaleString('tr-TR')} öğe daha göster`;
    more.addEventListener('click', (event) => {
      event.stopPropagation();
      more.remove();
      appendPage(list, children, end, depth);
    });
    list.append(more);
  }
}
