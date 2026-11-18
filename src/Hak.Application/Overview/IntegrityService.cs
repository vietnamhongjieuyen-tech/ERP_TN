using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Inventory;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Overview;

/// <summary>One finding: what it is about (voucher number, account / item code…) and the figures that do not agree.</summary>
public sealed record IntegrityFinding(string Reference, string Detail, Guid? VoucherId = null);

/// <summary>One check: Error = the books are wrong and must be investigated; Warning = not wrong by itself, but worth a look.</summary>
public sealed record IntegrityCheck(string Code, string Title, string Severity, int Count, IReadOnlyList<IntegrityFinding> Findings);

public sealed record IntegrityReport(DateOnly AsOf, DateTime RanAt, IReadOnlyList<IntegrityCheck> Checks)
{
    public int Errors => Checks.Where(c => c.Severity == "Error").Sum(c => c.Count);
}

/// <summary>
/// Kiểm soát toàn vẹn sổ sách: the ledger invariants the tests assert, run read-only on the company's own data up to a date.
/// It never changes anything; each finding names the voucher / account / item so it can be traced. Amounts are summed here (SQLite
/// stores money as integers), which is fine for the volumes of one company.
/// </summary>
public sealed class IntegrityService(HakDbContext db)
{
    private const int Sample = 20;

    public async Task<IntegrityReport> RunAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var co = ctx.CompanyId;
        var checks = new List<IntegrityCheck>();
        IntegrityCheck Check(string code, string title, string severity, List<IntegrityFinding> f) => new(code, title, severity, f.Count, f.Take(Sample).ToList());

        // 1. Every journal entry balances (Σ Nợ = Σ Có).
        var lines = await db.JournalLines.AsNoTracking().Where(l => l.CompanyId == co && l.PostingDate <= asOf)
            .Select(l => new { l.Id, l.JournalEntryId, l.AccountId, l.Side, l.Amount }).ToListAsync();
        var entries = await db.JournalEntries.AsNoTracking().Where(e => e.CompanyId == co && e.PostingDate <= asOf)
            .Select(e => new { e.Id, e.VoucherId, e.VoucherNumber }).ToListAsync();
        var entryById = entries.ToDictionary(e => e.Id);
        var unbalanced = lines.GroupBy(l => l.JournalEntryId)
            .Select(g => new { g.Key, D = g.Where(x => x.Side == Side.Debit).Sum(x => x.Amount), C = g.Where(x => x.Side == Side.Credit).Sum(x => x.Amount) })
            .Where(x => x.D != x.C)
            .Select(x => new IntegrityFinding(entryById.TryGetValue(x.Key, out var e) ? e.VoucherNumber : x.Key.ToString(), $"Nợ {x.D:#,0.####} ≠ Có {x.C:#,0.####}",
                entryById.TryGetValue(x.Key, out var e2) ? e2.VoucherId : null)).ToList();
        checks.Add(Check("unbalanced_entry", "Bút toán không cân", "Error", unbalanced));

        var totalD = lines.Where(l => l.Side == Side.Debit).Sum(l => l.Amount);
        var totalC = lines.Where(l => l.Side == Side.Credit).Sum(l => l.Amount);
        checks.Add(Check("ledger_total", "Tổng phát sinh Nợ = Có toàn sổ", "Error",
            totalD == totalC ? [] : [new("Sổ cái", $"Tổng Nợ {totalD:#,0.####} ≠ tổng Có {totalC:#,0.####}")]));

        // 2. Posted ⇔ one journal entry.
        var vouchers = await db.Vouchers.AsNoTracking().Where(v => v.CompanyId == co && v.PostingDate <= asOf)
            .Select(v => new { v.Id, v.Number, v.Status }).ToListAsync();
        var entriesByVoucher = entries.GroupBy(e => e.VoucherId).ToDictionary(g => g.Key, g => g.Count());
        checks.Add(Check("posted_without_entry", "Chứng từ đã ghi sổ nhưng không có bút toán", "Error",
            vouchers.Where(v => (v.Status == VoucherStatus.Posted || v.Status == VoucherStatus.Reversed) && !entriesByVoucher.ContainsKey(v.Id))
                .Select(v => new IntegrityFinding(v.Number, "Trạng thái đã ghi sổ, sổ cái không có bút toán", v.Id)).ToList()));
        var voucherById = vouchers.ToDictionary(v => v.Id);
        checks.Add(Check("entry_without_posted_voucher", "Bút toán không có chứng từ đã ghi sổ", "Error",
            entries.Where(e => !voucherById.TryGetValue(e.VoucherId, out var v) || (v.Status != VoucherStatus.Posted && v.Status != VoucherStatus.Reversed))
                .Select(e => new IntegrityFinding(e.VoucherNumber, voucherById.TryGetValue(e.VoucherId, out var v) ? $"Chứng từ đang ở trạng thái {v.Status}" : "Không tìm thấy chứng từ", e.VoucherId)).ToList()));
        checks.Add(Check("entry_twice", "Một chứng từ có nhiều bút toán", "Error",
            entriesByVoucher.Where(kv => kv.Value > 1).Select(kv => new IntegrityFinding(voucherById.TryGetValue(kv.Key, out var v) ? v.Number : kv.Key.ToString(), $"{kv.Value} bút toán", kv.Key)).ToList()));

        // 3. Lines point at accounts of the same company.
        var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.CompanyId, a.Code }).ToListAsync();
        var accountById = accounts.ToDictionary(a => a.Id);
        checks.Add(Check("foreign_account", "Dòng sổ dùng tài khoản của công ty khác", "Error",
            lines.Where(l => !accountById.TryGetValue(l.AccountId, out var a) || a.CompanyId != co)
                .Select(l => new IntegrityFinding(entryById.TryGetValue(l.JournalEntryId, out var e) ? e.VoucherNumber : l.JournalEntryId.ToString(), "Tài khoản không thuộc công ty")).ToList()));

        // 4. Settlement: active allocations of a line never exceed its amount.
        var allocations = await db.SettlementAllocations.AsNoTracking().Where(a => a.CompanyId == co && a.VoidedAt == null)
            .Select(a => new { a.DebitLineId, a.CreditLineId, a.Amount }).ToListAsync();
        var allocated = allocations.Select(a => (Line: a.DebitLineId, a.Amount)).Concat(allocations.Select(a => (Line: a.CreditLineId, a.Amount)))
            .GroupBy(x => x.Line).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var lineById = lines.ToDictionary(l => l.Id);
        checks.Add(Check("over_allocated", "Phân bổ công nợ vượt số tiền của dòng", "Error",
            allocated.Where(kv => lineById.TryGetValue(kv.Key, out var l) && kv.Value > l.Amount)
                .Select(kv => { var l = lineById[kv.Key]; var e = entryById[l.JournalEntryId];
                    return new IntegrityFinding($"{e.VoucherNumber} · TK {accountById[l.AccountId].Code}", $"Đã phân bổ {kv.Value:#,0.####} > số tiền dòng {l.Amount:#,0.####}", e.VoucherId); }).ToList()));

        // 5. Stock: no negative quantity, and each movement valued at most once.
        var movements = await db.StockMovements.AsNoTracking().Where(m => m.CompanyId == co && m.MovementDate <= asOf)
            .Select(m => new { m.Id, m.WarehouseId, m.ItemId, m.Quantity }).ToListAsync();
        var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == co).Select(i => new { i.Id, i.Code }).ToDictionaryAsync(i => i.Id, i => i.Code);
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => w.CompanyId == co).Select(w => new { w.Id, w.Code }).ToDictionaryAsync(w => w.Id, w => w.Code);
        checks.Add(Check("negative_stock", "Tồn kho âm", "Error",
            movements.GroupBy(m => (m.WarehouseId, m.ItemId)).Select(g => new { g.Key, Qty = g.Sum(m => m.Quantity) }).Where(x => x.Qty < 0)
                .Select(x => new IntegrityFinding($"Kho {warehouses.GetValueOrDefault(x.Key.WarehouseId)} · {items.GetValueOrDefault(x.Key.ItemId)}", $"Tồn {x.Qty:#,0.####}")).ToList()));
        var valuations = await db.StockValuations.AsNoTracking().Where(v => v.CompanyId == co && v.VoidedAt == null).Select(v => v.MovementId).ToListAsync();
        var valuedTwice = valuations.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        checks.Add(Check("valued_twice", "Biến động kho được định giá hai lần", "Error",
            movements.Where(m => valuedTwice.Contains(m.Id)).Select(m => new IntegrityFinding($"Kho {warehouses.GetValueOrDefault(m.WarehouseId)} · {items.GetValueOrDefault(m.ItemId)}", "Có nhiều hơn một giá trị hiệu lực")).ToList()));
        var valued = valuations.ToHashSet();
        checks.Add(Check("not_valued", "Biến động kho chưa có giá trị", "Warning",
            movements.Where(m => !valued.Contains(m.Id)).Select(m => new IntegrityFinding($"Kho {warehouses.GetValueOrDefault(m.WarehouseId)} · {items.GetValueOrDefault(m.ItemId)}",
                "Chưa định giá — bình thường với kho tính giá bình quân cuối tháng chưa chạy tính giá")).ToList()));

        return new IntegrityReport(asOf, DateTime.UtcNow, checks);
    }
}

