import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Screen tests (*.ui.test.tsx) in a simulated browser; logic tests (*.test.ts) run under node --test (npm test).
export default defineConfig({
  plugins: [react()],
  test: { environment: 'jsdom', include: ['src/**/*.ui.test.tsx'], restoreMocks: true },
})