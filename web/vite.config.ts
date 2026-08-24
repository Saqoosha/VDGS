import path from 'node:path'
import { fileURLToPath } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'
import { mockApi } from './vite-mock-api.ts'

const root = path.dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [mockApi(), react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(root, './src') },
  },
  build: {
    rollupOptions: {
      // Two pages out of one project: index.html is served by the plugin from
      // <game>/vdgs/ui while the game runs, companion.html is loaded by the setup app
      // before it does. They share the theme, the components and the fonts, which is the
      // entire reason they are not separate projects.
      input: {
        index: path.resolve(root, 'index.html'),
        companion: path.resolve(root, 'companion.html'),
        site: path.resolve(root, 'site.html'),
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
    globals: true,
  },
})
