/**
 * SVG'yi <img> içinde gösterir: bu bağlamda SVG'deki betikler ve dış kaynaklar çalışmaz, yalnızca çizim
 * görünür. Damalı arka plan saydamlığı belli eder; tıklamak sığdır ↔ gerçek boyut arasında geçiş yapar.
 */
export function renderSvg(text: string, target: HTMLElement): () => void {
  const url = URL.createObjectURL(new Blob([text], { type: 'image/svg+xml' }));

  const frame = document.createElement('div');
  frame.className = 'svg-frame fit';
  const image = document.createElement('img');
  image.alt = 'SVG önizlemesi';
  image.src = url;
  image.draggable = false;
  frame.append(image);
  frame.addEventListener('click', () => frame.classList.toggle('fit'));

  target.replaceChildren(frame);
  return () => URL.revokeObjectURL(url);
}
