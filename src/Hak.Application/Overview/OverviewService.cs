using Hak.Application.Accounting;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Application.Settlement;
using Hak.Data;
using Hak.Domain.Assets;
using Hak.Domain.Banking;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Fx;
using Hak.Domain.Inventory;
using Hak.Domain.Ledger;
using Hak.Domain.Loans;
using Hak.Domain.Organization;
using Hak.Domain.Production;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Overview;

public sealed record CashBalance(Guid AccountId, string AccountCode, string AccountName, string Kind, decimal Balance);

public sealed record PeriodSummary(Guid Id, int PeriodNo, DateOnly StartDate, DateOnly EndDate, string Status);

/// <summary>What is waiting for the user today. Report figures (cash, overdue) are null without report.view.</summary>
public sealed record Dashboard(DateOnly AsOf, int MyDrafts, int Drafts, int AwaitingApproval, int AwaitingPosting, int DraftStockDocuments,
    int UnmatchedStatementLines, IReadOnlyList<CashBalance>? Cash, decimal? OverdueReceivables, decimal? OverduePayables,
    PeriodSummary? CurrentPeriod, PeriodSummary? OldestOpenPeriod);

/// <summary>Done · Todo (blocks a clean close) · Optional (worth a look, does not block) · NotApplicable (the company does not use it).</summary>
public enum CheckStatus { Done, Todo, Optional, NotApplicable }

/// <summary>One step of the month-end close. Screen is the menu key of the screen where the step is carried out.</summary>
public sealed record ChecklistItem(string Key, string Title, CheckStatus Status, string Detail, string Screen);

public sealed record ClosingChecklist(PeriodSummary Period, IReadOnlyList<ChecklistItem> Items)
{
    public bool ReadyToLock => Items.All(i => i.Status != CheckStatus.Todo);
}

/// <summary>
/// Read-only overviews built from what the modules already record: the start page (work waiting, cash, overdue items) and the
/// month-end checklist (every periodic run and reconciliation of one accounting period, in the order they are carried out).
/// Nothing here posts or changes data.
/// </summary>
public sealed class OverviewService(HakDbContext db, LedgerQueryService ledger, SettlementService settlement)
{
    public async Task<Dashboard> DashboardAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.VoucherView);
        var byStatus = await db.Vouchers.Where(v => v.CompanyId == ctx.CompanyId && (v.Status == VoucherStatus.Draft || v.Status == VoucherStatus.Submitted || v.Status == VoucherStatus.Approved))
            .GroupBy(v => v.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        int Count(VoucherStatus s) => byStatus.Where(x => x.Key == s).Sum(x => x.Count);
        var myDrafts = await db.Vouchers.CountAsync(v => v.CompanyId == ctx.CompanyId && v.Status == VoucherStatus.Draft && v.CreatedBy == ctx.UserId);
        var draftStock = await db.StockDocuments.CountAsync(d => d.CompanyId == ctx.CompanyId && d.Status == StockDocumentStatus.Draft);
        var unmatched = await db.BankStatementLines.CountAsync(l => l.CompanyId == ctx.CompanyId && l.Status != StatementLineStatus.Matched);

        var periods = await db.AccountingPeriods.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).OrderBy(p => p.StartDate).ToListAsync();
        var current = periods.FirstOrDefault(p => p.StartDate <= asOf && p.EndDate >= asOf);
        var oldestOpen = periods.FirstOrDefault(p => p.Status == PeriodStatus.Open);

        List<CashBalance>? cash = null;
        decimal? overdueAr = null, overdueAp = null;
        if (ctx.Permissions.Contains(Permissions.ReportView))
        {
            var cashAccounts = await db.Accounts.AsNoTracking()
                .Where(a => a.CompanyId == ctx.CompanyId && a.IsPostable && a.IsActive && a.CashKind != CashKind.None).OrderBy(a => a.Code).ToListAsync();
            var ids = cashAccounts.Select(a => a.Id).ToList();
            var sums = await db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && ids.Contains(l.AccountId) && l.PostingDate <= asOf)
                .GroupBy(l => new { l.AccountId, l.Side }).Select(g => new { g.Key.AccountId, g.Key.Side, Total = g.Sum(x => x.Amount) }).ToListAsync();
            cash = cashAccounts.Select(a => new CashBalance(a.Id, a.Code, a.Name, a.CashKind.ToString(),
                sums.Where(s => s.AccountId == a.Id).Sum(s => (int)s.Side * s.Total))).ToList();

            // null = no account tracks receivables / payables by invoice yet ("chưa khai báo"), not "nothing overdue".
            foreach (var acc in await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.OpenItemNature != OpenItemNature.None).ToListAsync())
            {
                var overdue = (await settlement.AgingAsync(ctx, acc.Id, asOf)).Rows.Sum(r => r.Buckets.Sum());
                if (acc.OpenItemNature == OpenItemNature.Receivable) overdueAr = (overdueAr ?? 0) + overdue; else overdueAp = (overdueAp ?? 0) + overdue;
            }
        }
        return new Dashboard(asOf, myDrafts, Count(VoucherStatus.Draft), Count(VoucherStatus.Submitted), Count(VoucherStatus.Approved), draftStock, unmatched,
            cash, overdueAr, overdueAp, current is null ? null : Summary(current), oldestOpen is null ? null : Summary(oldestOpen));
    }

    public async Task<ClosingChecklist> ClosingChecklistAsync(SessionContext ctx, Guid periodId)
    {
        ctx.Require(Permissions.ReportView);
        var p = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Id == periodId && x.CompanyId == ctx.CompanyId)
                ?? throw new DomainException(ErrorCodes.PeriodNotFound, "Kỳ kế toán không tồn tại.");
        var co = ctx.CompanyId;
        var items = new List<ChecklistItem>();
        void Add(string key, string title, CheckStatus status, string detail, string screen) => items.Add(new(key, title, status, detail, screen));

        // 1. Documents of the period that have not reached the ledger.
        var open = await db.Vouchers.Where(v => v.CompanyId == co && v.PostingDate >= p.StartDate && v.PostingDate <= p.EndDate
                && (v.Status == VoucherStatus.Draft || v.Status == VoucherStatus.Submitted || v.Status == VoucherStatus.Approved))
            .GroupBy(v => v.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var openCount = open.Sum(x => x.Count);
        Add("vouchers", "Ghi sổ hết chứng từ trong kỳ", openCount == 0 ? CheckStatus.Done : CheckStatus.Todo,
            openCount == 0 ? "Không còn chứng từ nháp, chờ duyệt hay chờ ghi sổ."
                : $"Còn {openCount} chứng từ chưa ghi sổ ({string.Join(", ", open.Select(x => $"{x.Count} {StatusName(x.Key)}"))}): ghi sổ hoặc hủy.", "vouchers");

        var draftStock = await db.StockDocuments.CountAsync(d => d.CompanyId == co && d.Status == StockDocumentStatus.Draft && d.PostingDate >= p.StartDate && d.PostingDate <= p.EndDate);
        var anyStock = draftStock > 0 || await db.StockDocuments.AnyAsync(d => d.CompanyId == co);
        Add("stock_documents", "Ghi sổ hết phiếu nhập / xuất kho trong kỳ", !anyStock ? CheckStatus.NotApplicable : draftStock == 0 ? CheckStatus.Done : CheckStatus.Todo,
            !anyStock ? "Chưa dùng phân hệ kho." : draftStock == 0 ? "Không còn phiếu kho nháp." : $"Còn {draftStock} phiếu kho nháp trong kỳ.", "stock");

        // 2. Bank: statement lines of the period not matched to the books.
        var unmatched = await db.BankStatementLines.CountAsync(l => l.CompanyId == co && l.Status != StatementLineStatus.Matched
            && l.TransactionDate >= p.StartDate && l.TransactionDate <= p.EndDate);
        var anyStatement = await db.BankStatements.AnyAsync(s => s.CompanyId == co && s.FromDate <= p.EndDate && s.ToDate >= p.StartDate);
        var bankAccounts = await db.Accounts.CountAsync(a => a.CompanyId == co && a.IsActive && a.IsPostable && a.CashKind == CashKind.Bank);
        Add("bank", "Đối chiếu sao kê ngân hàng", bankAccounts == 0 ? CheckStatus.NotApplicable : !anyStatement ? CheckStatus.Optional : unmatched == 0 ? CheckStatus.Done : CheckStatus.Todo,
            bankAccounts == 0 ? "Chưa có tài khoản ngân hàng." : !anyStatement ? "Chưa nhập sao kê của kỳ này — nên nhập để đối chiếu số dư tiền gửi."
                : unmatched == 0 ? "Mọi dòng sao kê trong kỳ đã khớp với sổ." : $"Còn {unmatched} dòng sao kê trong kỳ chưa khớp (phí, lãi, giao dịch chưa hạch toán).", "bank");

        // 3. Periodic runs, in the order they feed each other: stock costing → production cost → depreciation → loan interest → FX.
        var monthlyWarehouses = await (from w in db.Warehouses
                                       where w.CompanyId == co
                                       let method = db.CostingPolicies.Where(c => c.WarehouseId == w.Id && c.EffectiveFrom <= p.EndDate)
                                           .OrderByDescending(c => c.EffectiveFrom).Select(c => (CostingMethod?)c.Method).FirstOrDefault()
                                       where method == CostingMethod.MonthlyAverage
                                             // only warehouses that moved stock in the period have anything to value
                                             && db.StockMovements.Any(m => m.WarehouseId == w.Id && m.MovementDate >= p.StartDate && m.MovementDate <= p.EndDate)
                                       select new { w.Id, w.Code }).ToListAsync();
        var costed = await db.CostingRuns.Where(r => r.CompanyId == co && r.PeriodId == p.Id && r.Status == CostingRunStatus.Active).Select(r => r.WarehouseId).ToListAsync();
        var notCosted = monthlyWarehouses.Where(w => !costed.Contains(w.Id)).Select(w => w.Code).ToList();
        Add("costing", "Tính giá xuất kho cuối tháng", monthlyWarehouses.Count == 0 ? CheckStatus.NotApplicable : notCosted.Count == 0 ? CheckStatus.Done : CheckStatus.Todo,
            monthlyWarehouses.Count == 0 ? "Không có kho bình quân cuối tháng nào phát sinh nhập / xuất trong kỳ (FIFO / bình quân tức thời tính ngay khi xuất)."
                : notCosted.Count == 0 ? $"Đã tính giá {monthlyWarehouses.Count} kho bình quân cuối tháng." : $"Chưa tính giá kho: {string.Join(", ", notCosted)}.", "stock");

        // Cost objects with cost collected on their work-in-progress account in the period.
        var costObjects = await db.CostObjects.Where(c => c.CompanyId == co && c.IsActive
                && db.JournalLines.Any(l => l.CompanyId == co && l.AccountId == c.WipAccountId && l.PostingDate >= p.StartDate && l.PostingDate <= p.EndDate))
            .Select(c => new { c.Id, c.Code }).ToListAsync();
        var produced = await db.ProductionCostRuns.Where(r => r.CompanyId == co && r.PeriodId == p.Id && r.Status == ProductionRunStatus.Active).Select(r => r.CostObjectId).ToListAsync();
        var notProduced = costObjects.Where(c => !produced.Contains(c.Id)).Select(c => c.Code).ToList();
        Add("production", "Tính giá thành sản xuất", costObjects.Count == 0 ? CheckStatus.NotApplicable : notProduced.Count == 0 ? CheckStatus.Done : CheckStatus.Todo,
            costObjects.Count == 0 ? "Không có đối tượng tính giá thành nào phát sinh chi phí dở dang trong kỳ." : notProduced.Count == 0 ? "Đã tính giá thành mọi đối tượng."
                : $"Chưa tính giá thành: {string.Join(", ", notProduced)}.", "production");

        var activeAssets = await db.LongTermAssets.CountAsync(a => a.CompanyId == co && a.Status == AssetStatus.Active);
        var depreciated = await db.DepreciationRuns.AnyAsync(r => r.CompanyId == co && r.PeriodId == p.Id && r.Status == DepreciationRunStatus.Active);
        Add("depreciation", "Khấu hao TSCĐ, phân bổ CCDC", activeAssets == 0 ? CheckStatus.NotApplicable : depreciated ? CheckStatus.Done : CheckStatus.Todo,
            activeAssets == 0 ? "Không có thẻ tài sản / CCDC đang dùng." : depreciated ? "Đã chạy khấu hao / phân bổ kỳ này." : $"Chưa chạy khấu hao / phân bổ cho {activeAssets} thẻ đang dùng.", "assets");

        var activeLoans = await db.LoanContracts.CountAsync(l => l.CompanyId == co && l.Status == LoanStatus.Active && l.InterestFrom <= p.EndDate);
        var accrued = await db.LoanInterestRuns.AnyAsync(r => r.CompanyId == co && r.PeriodId == p.Id && r.Status == LoanRunStatus.Active);
        Add("loan_interest", "Trích lãi vay", activeLoans == 0 ? CheckStatus.NotApplicable : accrued ? CheckStatus.Done : CheckStatus.Todo,
            activeLoans == 0 ? "Không có khế ước vay đang hiệu lực." : accrued ? "Đã trích lãi vay kỳ này." : $"Chưa trích lãi cho {activeLoans} khế ước vay.", "loans");

        var hasForeign = await db.JournalLines.AnyAsync(l => l.CompanyId == co && l.ForeignCurrency != null && l.PostingDate <= p.EndDate);
        var revalued = await db.FxRevaluationRuns.AnyAsync(r => r.CompanyId == co && r.RevaluationDate == p.EndDate && r.Status == FxRunStatus.Active);
        Add("fx", "Đánh giá lại số dư ngoại tệ", !hasForeign ? CheckStatus.NotApplicable : revalued ? CheckStatus.Done : CheckStatus.Optional,
            !hasForeign ? "Không có số dư ngoại tệ." : revalued ? "Đã đánh giá lại tại ngày cuối kỳ."
                : "Chưa đánh giá lại tại ngày cuối kỳ (làm khi lập báo cáo tài chính; theo chính sách của doanh nghiệp với các tháng khác).", "fx");

        // 4. Closing entries, then the checks on the books themselves.
        var hasClosingRules = await db.ClosingRuleSets.AnyAsync(r => r.CompanyId == co);
        var closed = await db.JournalEntries.AnyAsync(e => e.CompanyId == co && e.VoucherType == VoucherTypes.Closing && e.PostingDate >= p.StartDate && e.PostingDate <= p.EndDate);
        Add("closing", "Kết chuyển doanh thu, chi phí", closed ? CheckStatus.Done : hasClosingRules ? CheckStatus.Todo : CheckStatus.Optional,
            closed ? "Đã có bút toán kết chuyển trong kỳ." : hasClosingRules ? "Chưa chạy kết chuyển kỳ này." : "Chưa khai báo quy tắc kết chuyển; kết chuyển bằng chứng từ loại Kết chuyển nếu doanh nghiệp kết chuyển hằng tháng.", "periods");

        var subledger = await settlement.ReconcileAsync(ctx, p.EndDate);
        Add("subledger", "Sổ chi tiết công nợ khớp sổ cái", subledger.IsReconciled ? CheckStatus.Done : CheckStatus.Todo,
            subledger.IsReconciled ? "Công nợ theo chứng từ khớp số dư sổ cái của từng đối tượng." : $"Có {subledger.Rows.Count} dòng lệch giữa sổ chi tiết công nợ và sổ cái.", "arap");

        var tb = await ledger.TrialBalanceAsync(ctx, p.StartDate, p.EndDate);
        Add("trial_balance", "Bảng cân đối số phát sinh cân", tb.IsBalanced ? CheckStatus.Done : CheckStatus.Todo,
            tb.IsBalanced ? $"Tổng phát sinh Nợ = Có = {tb.TotalPeriodDebit:#,0}." : "Bảng cân đối số phát sinh KHÔNG cân — kiểm tra ngay.", "tb");

        Add("lock", "Khóa sổ kỳ", p.Status == PeriodStatus.Locked ? CheckStatus.Done : CheckStatus.Optional,
            p.Status == PeriodStatus.Locked ? "Kỳ đã khóa sổ." : "Kỳ đang mở. Khóa sổ sau khi các bước trên hoàn tất để số liệu không bị thay đổi.", "periods");

        return new ClosingChecklist(Summary(p), items);
    }

    private static PeriodSummary Summary(AccountingPeriod p) => new(p.Id, p.PeriodNo, p.StartDate, p.EndDate, p.Status.ToString());

    private static string StatusName(VoucherStatus s) => s switch
    {
        VoucherStatus.Draft => "nháp", VoucherStatus.Submitted => "chờ duyệt", VoucherStatus.Approved => "chờ ghi sổ", _ => s.ToString(),
    };
}
