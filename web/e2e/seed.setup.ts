// Fills the empty test database with the development sample company (users admin / ketoan / ktt, accounts, rules…).
import { test } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { e2e } from '../playwright.config'

test('seed the test company', () => {
  execFileSync(process.execPath, ['scripts/dev-seed.mjs'], {
    env: { ...process.env, HAK_API: e2e.api, HAK_CREDENTIALS_FILE: e2e.credentials },
    stdio: 'inherit',
  })
})
