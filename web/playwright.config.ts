// End-to-end tests: a real API on a NEW empty database in a temporary folder + the Vite dev server, driven by Chromium.
// Never touches the database of an installed HAK. Run: npm run e2e (needs `npx playwright install chromium` once).
import { defineConfig } from '@playwright/test'
import { existsSync, mkdtempSync, readdirSync, rmSync, statSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

// The config is loaded again in every worker: the folder made by the first load is passed on through the environment.
if (!process.env.HAK_E2E_DIR) {
  // First load: remove the folders of earlier runs (older than an hour, so a run still going elsewhere keeps its own).
  for (const name of readdirSync(tmpdir()).filter(n => n.startsWith('hak-e2e-'))) {
    const old = join(tmpdir(), name)
    try { if (Date.now() - statSync(old).mtimeMs > 3_600_000) rmSync(old, { recursive: true, force: true }) } catch { /* in use: next time */ }
  }
}
const dir = process.env.HAK_E2E_DIR ?? (process.env.HAK_E2E_DIR = mkdtempSync(join(tmpdir(), 'hak-e2e-')))
const userDotnet = join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'dotnet', 'dotnet.exe')
const dotnet = process.env.HAK_DOTNET ?? (existsSync(userDotnet) ? userDotnet : 'dotnet')
const apiPort = 5091, webPort = 5191

export default defineConfig({
  testDir: 'e2e',
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  reporter: [['list']],
  use: { baseURL: `http://127.0.0.1:${webPort}`, locale: 'en-US', trace: 'retain-on-failure' },   // English browser: dates must still read dd/MM/yyyy
  webServer: [
    {
      command: `"${dotnet}" run --project ../src/Hak.Api --urls http://127.0.0.1:${apiPort} --Hak:Environment=Development --Hak:DatabasePath="${join(dir, 'hak.db')}" --Hak:BackupPath="${join(dir, 'backups')}"`,
      url: `http://127.0.0.1:${apiPort}/api/health`,
      timeout: 240_000,
      reuseExistingServer: false,
    },
    {
      command: `npx vite --host 127.0.0.1 --port ${webPort} --strictPort`,
      url: `http://127.0.0.1:${webPort}`,
      env: { HAK_API_URL: `http://127.0.0.1:${apiPort}` },
      timeout: 120_000,
      reuseExistingServer: false,
    },
  ],
  projects: [
    { name: 'seed', testMatch: /seed\.setup\.ts/, use: {} },
    { name: 'e2e', testMatch: /.*\.spec\.ts/, dependencies: ['seed'] },
  ],
})

export const e2e = { dir, api: `http://127.0.0.1:${apiPort}/api`, credentials: join(dir, 'credentials.json') }
