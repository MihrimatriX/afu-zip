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
    rolldownOptions: {
      output: {
        // Windows App SDK'nin makepri'si dosya adındaki ara noktayı kaynak niteleyicisi sanıp uyarır
        // (PRI249, "editor.worker-HASH.js"): worker adlarında nokta yerine tire kullanılır.
        entryFileNames: (chunk) => `assets/${chunk.name.replaceAll('.', '-')}-[hash].js`,
      },
    },
  },
});
