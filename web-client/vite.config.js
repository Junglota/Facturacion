import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_DEV_API_URL || 'http://127.0.0.1:62703'
  return {
    plugins: [vue()],
    server: { host: 'localhost', port: 5173, strictPort: true,
      proxy: { '/api': { target, changeOrigin: true }, '/health': { target, changeOrigin: true } } },
  }
})
