import { defineConfig } from 'vite';

// Çıktı AfuZip.App'in yanına "PreviewWeb" klasörü olarak kopyalanır ve WebView2'de
// https://app.afuzip.preview/ sanal host'u üzerinden açılır; bu yüzden yollar göreli.
export default defineConfig({
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2022',
    sourcemap: false,
    // Monaco tek başına ~3 MB; uyarı eşiği buna göre.
    chunkSizeWarningLimit: 4096,
  },
  worker: {
    format: 'es',
  },
});
