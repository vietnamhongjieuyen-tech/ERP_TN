// The everyday path through the real program: the accountant enters a voucher, the chief accountant approves and posts it,
// and the amount reaches the trial balance. Also checks the shared date (dd/MM/yyyy) and amount fields in a real browser.
import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { e2e } from '../playwright.config'

const password = (user: 'admin' | 'ketoan' | 'ktt') => (JSON.parse(readFileSync(e2e.credentials, 'utf8')) as { users: Record<string, string> }).users[user]

async function login(page: Page, user: 'ketoan' | 'ktt') {
  await page.goto('/')
  await page.getByLabel('Tên đăng nhập').fill(user)
  await page.getByLabel('Mật khẩu').fill(password(user))
  await page.getByRole('button', { name: 'Đăng nhập' }).click()
  await page.getByRole('button', { name: /^DEV01/ }).click()
  await expect(page.getByRole('button', { name: 'Đăng xuất' })).toBeVisible()
}

async function open(page: Page, menu: string) {
  await page.getByRole('navigation', { name: 'Phân hệ' }).getByRole('button', { name: menu, exact: true }).click()
}

const description = `E2E chi phí ${Date.now()}`

test('kế toán lập, kế toán trưởng duyệt và ghi sổ; số tiền lên bảng cân đối', async ({ page }) => {
  await login(page, 'ketoan')
  await open(page, 'Chứng từ')
  await page.getByRole('button', { name: 'Thêm chứng từ' }).click()

  // Dates read dd/MM/yyyy although the browser runs in English.
  const voucherDate = page.locator('label', { hasText: 'Ngày chứng từ' }).locator('input[type="text"]')
  await expect(voucherDate).toHaveValue(/^\d{2}\/\d{2}\/\d{4}$/)

  await page.getByLabel('Diễn giải', { exact: true }).fill(description)
  const accounts = page.getByRole('combobox', { name: 'Tài khoản' })
  const amounts = page.getByRole('textbox', { name: 'Số tiền' })
  await accounts.nth(0).fill('EXP'); await accounts.nth(0).press('Enter')
  await amounts.nth(0).fill('1234567'); await amounts.nth(0).press('Tab')
  await expect(amounts.nth(0)).toHaveValue('1.234.567')

  // A wrong amount is marked, then corrected.
  await amounts.nth(1).fill('1,5,0'); await amounts.nth(1).blur()
  await expect(amounts.nth(1)).toHaveAttribute('aria-invalid', 'true')
  await amounts.nth(1).fill('1234567'); await amounts.nth(1).blur()
  await accounts.nth(1).fill('REV'); await accounts.nth(1).press('Enter')
  await expect(page.getByText('Cân bằng', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Lưu và gửi duyệt' }).click()
  await expect(page.getByText(/Đã lưu và gửi duyệt/)).toBeVisible()
  await page.getByRole('button', { name: 'Đăng xuất' }).click()

  await login(page, 'ktt')
  await open(page, 'Chứng từ')
  await page.getByText(description).first().click()
  await page.getByRole('button', { name: 'Duyệt', exact: true }).click()
  await expect(page.getByText('Đã duyệt', { exact: true }).first()).toBeVisible()
  await page.getByRole('button', { name: 'Ghi sổ', exact: true }).click()
  await expect(page.getByText('Đã ghi sổ', { exact: true }).first()).toBeVisible()

  await open(page, 'Cân đối phát sinh')
  await expect(page.getByRole('row', { name: /EXP/ }).first()).toContainText('1.234.567')
})
