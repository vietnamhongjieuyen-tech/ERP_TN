using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Settlement;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Settlement;

public sealed record OpenItem(Guid JournalLineId, Guid VoucherId, string VoucherNumber, string VoucherType, DateOnly PostingDate, DateOnly DueDate,
    Guid AccountId, Guid PartnerId, Side Side, decimal Amount, decimal Allocated, decimal Open, string? Description,
    string? ForeignCurrency = null, decimal? ForeignAmount = null, decimal? OpenForeign = null);

public sealed record AgingRow(Guid PartnerId, string PartnerCode, string PartnerName, decimal NotDue, IReadOnlyList<decimal> Buckets,
    decimal OpenObligations, decimal UnappliedCredits, decimal Net);

public sealed record AgingReport(Guid AccountId, string AccountCode, DateOnly AsOf, IReadOnlyList<string> BucketLabels, IReadOnlyList<AgingRow> Rows);

public sealed record AllocationRow(Guid Id, DateOnly AllocationDate, Guid DebitLineId, string DebitVoucherNumber, Guid CreditLineId, string CreditVoucherNumber,
    decimal Amount, decimal? ForeignAmount, string Origin, bool IsActive, DateTime? VoidedAt, string? VoidReason);

public sealed record ReconcileRow(Guid AccountId, string AccountCode, Guid? PartnerId, decimal LedgerBalance, decimal SubledgerBalance, decimal Difference, string? Issue);

public sealed record SubledgerReconciliation(DateOnly AsOf, IReadOnlyList<ReconcileRow> Rows)
{
    public bool IsReconciled => Rows.All(r => r.Difference == 0 && r.Issue is null);
}

/// <summary>
/// Receivables/payables subledger built on open items (master prompt §14). An open item is a posted
/// journal line on an account with <see cref="OpenItemNature"/> ≠ None. Its remaining balance is the
/// line amount minus active <see cref="SettlementAllocation"/>s. Voiding an allocation is retroactive
/// (it was wrong), so historical reports re-run after a void reflect the correction; both the original
/// allocation and the void remain in the table and the audit log.
/// </summary>
public sealed class SettlementService(HakDbContext db, AuditService audit, IClock clock)
{
    // ---------------------------------------------------------------- commands

    /// <summary>
    /// Manual matching. Two foreign-currency items must be matched with the foreign amount and only when that amount has the same
    /// carrying amount on both items; otherwise the difference is a realised FX gain/loss and needs a voucher (thu/chi ngoại tệ).
    /// </summary>
    public async Task<SettlementAllocation> AllocateAsync(SessionContext ctx, Guid lineA, Guid lineB, decimal amount, DateOnly? date = null, decimal? foreignAmount = null)
    {
        ctx.Require(Permissions.SettlementManage);
        return await db.InTransactionAsync(async () =>
        {
            var a = await FindLineAsync(ctx.CompanyId, lineA);
            var b = await FindLineAsync(ctx.CompanyId, lineB);
            var (debit, credit) = a.Side == Side.Debit ? (a, b) : (b, a);
            var effective = date ?? Max(a.PostingDate, b.PostingDate);
            await RequireOpenPeriodAsync(ctx.CompanyId, effective);
            if (debit.ForeignCurrency is not null && debit.ForeignAmount is not null && credit.ForeignAmount is not null)
            {
                if (foreignAmount is not { } fa)
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Khoản ngoại tệ {debit.ForeignCurrency}: phải nhập số nguyên tệ phân bổ.");
                var dp = await FunctionalDecimalsAsync(ctx.CompanyId);
                var (dF, dV) = await OpenBothAsync(debit);
                var (cF, cV) = await OpenBothAsync(credit);
                var dBook = Domain.Fx.FxSettlementMath.BookValue(dF, dV, fa, dp);
                var cBook = Domain.Fx.FxSettlementMath.BookValue(cF, cV, fa, dp);
                if (dBook != amount || cBook != amount)
                    throw new DomainException(ErrorCodes.FxBookValue,
                        $"{fa} {debit.ForeignCurrency} có giá trị ghi sổ {dBook} bên Nợ và {cBook} bên Có (khác {amount}); lập chứng từ bù trừ có dòng chênh lệch tỷ giá.");
            }
            var alloc = await AllocateCoreAsync(ctx.CompanyId, debit, credit, amount, effective, SettlementOrigins.Manual, null, ctx.UserId, foreignAmount);
            await audit.AppendAsync(ctx.CompanyId, ctx, "settlement.allocate", "settlement_allocation", alloc.Id.ToString(),
                new { debit = debit.Id, credit = credit.Id, amount, foreignAmount, effective });
            await db.SaveChangesAsync();
            return alloc;
        });
    }

    public async Task VoidAsync(SessionContext ctx, Guid allocationId, string reason)
    {
        ctx.Require(Permissions.SettlementManage);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy phân bổ.");
        await db.InTransactionAsync(async () =>
        {
            var a = await db.SettlementAllocations.FirstOrDefaultAsync(x => x.Id == allocationId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Phân bổ không tồn tại.");
            if (!a.IsActive) throw new DomainException(ErrorCodes.InvalidStateTransition, "Phân bổ đã bị hủy trước đó.");
            await RequireOpenPeriodAsync(ctx.CompanyId, a.AllocationDate);
            Void(a, ctx.UserId, reason);
            await audit.AppendAsync(ctx.CompanyId, ctx, "settlement.void", "settlement_allocation", a.Id.ToString(), new { reason, a.Amount });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Matches the oldest open credits against the oldest open obligations (by due date, then posting date). Functional-currency
    /// items only: foreign items carry different book rates, so their matching produces FX differences and needs a voucher.
    /// </summary>
    public async Task<IReadOnlyList<SettlementAllocation>> AutoAllocateAsync(SessionContext ctx, Guid accountId, Guid partnerId, DateOnly date)
    {
        ctx.Require(Permissions.SettlementManage);
        return await db.InTransactionAsync(async () =>
        {
            await RequireOpenPeriodAsync(ctx.CompanyId, date);
            var items = (await OpenItemsCoreAsync(ctx.CompanyId, accountId, partnerId, date)).Where(i => i.ForeignCurrency is null).ToList();
            var debits = new Queue<OpenItem>(items.Where(i => i.Side == Side.Debit && i.Open > 0).OrderBy(i => i.DueDate).ThenBy(i => i.PostingDate).ThenBy(i => i.VoucherNumber));
            var credits = new Queue<OpenItem>(items.Where(i => i.Side == Side.Credit && i.Open > 0).OrderBy(i => i.DueDate).ThenBy(i => i.PostingDate).ThenBy(i => i.VoucherNumber));
            var created = new List<SettlementAllocation>();
            decimal dRem = 0, cRem = 0;
            OpenItem? d = null, c = null;
            while (true)
            {
                if (d is null || dRem == 0) { if (!debits.TryDequeue(out d)) break; dRem = d.Open; }
                if (c is null || cRem == 0) { if (!credits.TryDequeue(out c)) break; cRem = c.Open; }
                var amt = Math.Min(dRem, cRem);
                var debitLine = await FindLineAsync(ctx.CompanyId, d.JournalLineId);
                var creditLine = await FindLineAsync(ctx.CompanyId, c.JournalLineId);
                created.Add(await AllocateCoreAsync(ctx.CompanyId, debitLine, creditLine, amt, date, SettlementOrigins.Auto, null, ctx.UserId));
                dRem -= amt; cRem -= amt;
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "settlement.auto", "partner", partnerId.ToString(),
                new { accountId, date, count = created.Count, total = created.Sum(x => x.Amount) });
            await db.SaveChangesAsync();
            return (IReadOnlyList<SettlementAllocation>)created;
        });
    }

    // ---------------------------------------------------------------- queries

    public async Task<IReadOnlyList<OpenItem>> OpenItemsAsync(SessionContext ctx, Guid accountId, Guid? partnerId, DateOnly asOf, bool includeSettled = false)
    {
        ctx.Require(Permissions.ReportView);
        var items = await OpenItemsCoreAsync(ctx.CompanyId, accountId, partnerId, asOf);
        return includeSettled ? items : items.Where(i => i.Open != 0).ToList();
    }

    /// <summary>Allocations of one partner on one account (newest first), voided ones included so the history stays visible.</summary>
    public async Task<IReadOnlyList<AllocationRow>> AllocationsAsync(SessionContext ctx, Guid accountId, Guid partnerId)
    {
        ctx.Require(Permissions.ReportView);
        var allocs = await db.SettlementAllocations.AsNoTracking()
            .Where(a => a.CompanyId == ctx.CompanyId && a.AccountId == accountId && a.PartnerId == partnerId).ToListAsync();
        var lineIds = allocs.SelectMany(a => new[] { a.DebitLineId, a.CreditLineId }).Distinct().ToList();
        var numbers = await (from l in db.JournalLines
                             join e in db.JournalEntries on l.JournalEntryId equals e.Id
                             where lineIds.Contains(l.Id)
                             select new { l.Id, e.VoucherNumber }).AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.VoucherNumber);
        return allocs
            .OrderByDescending(a => a.AllocationDate).ThenByDescending(a => a.CreatedAt)
            .Select(a => new AllocationRow(a.Id, a.AllocationDate, a.DebitLineId, numbers.GetValueOrDefault(a.DebitLineId, "?"),
                a.CreditLineId, numbers.GetValueOrDefault(a.CreditLineId, "?"), a.Amount, a.ForeignAmount, a.Origin,
                a.VoidedAt is null, a.VoidedAt, a.VoidReason))
            .ToList();
    }

    public async Task<AgingReport> AgingAsync(SessionContext ctx, Guid accountId, DateOnly asOf, int[]? bucketDays = null)
    {
        ctx.Require(Permissions.ReportView);
        var bounds = bucketDays is { Length: > 0 } ? bucketDays.OrderBy(x => x).ToArray() : [30, 60, 90];
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
        if (account.OpenItemNature == OpenItemNature.None)
            throw new DomainException(ErrorCodes.ValidationFailed, $"Tài khoản {account.Code} không theo dõi công nợ theo chứng từ.");
        var obligationSide = account.OpenItemNature == OpenItemNature.Receivable ? Side.Debit : Side.Credit;

        var labels = new List<string>();
        for (var i = 0; i < bounds.Length; i++) labels.Add($"{(i == 0 ? 1 : bounds[i - 1] + 1)}-{bounds[i]}");
        labels.Add($">{bounds[^1]}");

        var items = await OpenItemsCoreAsync(ctx.CompanyId, accountId, null, asOf);
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId).ToDictionaryAsync(p => p.Id);
        var rows = new List<AgingRow>();
        foreach (var g in items.Where(i => i.Open != 0).GroupBy(i => i.PartnerId))
        {
            decimal notDue = 0;
            var buckets = new decimal[labels.Count];
            foreach (var o in g.Where(i => i.Side == obligationSide))
            {
                var overdue = asOf.DayNumber - o.DueDate.DayNumber;
                if (overdue <= 0) { notDue += o.Open; continue; }
                var idx = Array.FindIndex(bounds, b => overdue <= b);
                buckets[idx < 0 ? bounds.Length : idx] += o.Open;
            }
            var obligations = g.Where(i => i.Side == obligationSide).Sum(i => i.Open);
            var unapplied = g.Where(i => i.Side != obligationSide).Sum(i => i.Open);
            var p = partners[g.Key];
            rows.Add(new AgingRow(p.Id, p.Code, p.Name, notDue, buckets, obligations, unapplied, obligations - unapplied));
        }
        return new AgingReport(account.Id, account.Code, asOf, labels, rows.OrderBy(r => r.PartnerCode, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// ReconcileSubledger: for every open-item account and partner, Σ open debits − Σ open credits must
    /// equal the general-ledger balance, and no line may be allocated beyond its amount.
    /// </summary>
    public async Task<SubledgerReconciliation> ReconcileAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == ctx.CompanyId && a.OpenItemNature != OpenItemNature.None).ToListAsync();
        var rows = new List<ReconcileRow>();
        foreach (var acc in accounts)
        {
            var items = await OpenItemsCoreAsync(ctx.CompanyId, acc.Id, null, asOf);
            foreach (var over in items.Where(i => i.Open < 0))
                rows.Add(new ReconcileRow(acc.Id, acc.Code, over.PartnerId, 0, 0, 0, $"Dòng {over.VoucherNumber} bị phân bổ vượt {-over.Open}."));

            var ledger = await db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == acc.Id && l.PostingDate <= asOf)
                .GroupBy(l => new { l.PartnerId, l.Side }).Select(g => new { g.Key.PartnerId, g.Key.Side, Total = g.Sum(x => x.Amount) }).ToListAsync();
            var partnerIds = ledger.Select(x => x.PartnerId).Concat(items.Select(i => (Guid?)i.PartnerId)).Distinct();
            foreach (var pid in partnerIds)
            {
                var lb = ledger.Where(x => x.PartnerId == pid).Sum(x => (int)x.Side * x.Total);
                var sb = items.Where(i => i.PartnerId == pid).Sum(i => (int)i.Side * i.Open);
                if (lb != sb || pid is null)
                    rows.Add(new ReconcileRow(acc.Id, acc.Code, pid, lb, sb, sb - lb, pid is null ? "Dòng sổ cái thiếu đối tượng trên TK theo dõi công nợ." : null));
            }
        }
        return new SubledgerReconciliation(asOf, rows);
    }

    // ---------------------------------------------------------------- engine hooks (inside caller's transaction)

    internal async Task<decimal> OpenAmountAsync(JournalLine line) => line.Amount - (await AllocatedAsync(line.Id)).Amount;

    /// <summary>Open foreign amount and open functional amount of a line.</summary>
    internal async Task<(decimal Foreign, decimal Amount)> OpenBothAsync(JournalLine line)
    {
        var a = await AllocatedAsync(line.Id);
        return ((line.ForeignAmount ?? 0) - a.Foreign, line.Amount - a.Amount);
    }

    internal async Task<int> FunctionalDecimalsAsync(Guid companyId)
    {
        var currency = await db.Companies.Where(c => c.Id == companyId).Select(c => c.FunctionalCurrency).FirstAsync();
        return await db.Currencies.Where(c => c.Code == currency).Select(c => c.DecimalPlaces).FirstAsync();
    }

    internal async Task<JournalLine> FindLineAsync(Guid companyId, Guid lineId)
    {
        var tracked = db.ChangeTracker.Entries<JournalLine>().FirstOrDefault(e => e.Entity.Id == lineId)?.Entity;
        var line = tracked ?? await db.JournalLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId);
        if (line is null || line.CompanyId != companyId)
            throw new DomainException(ErrorCodes.NotFound, "Dòng sổ cái cần phân bổ không tồn tại.");
        return line;
    }

    internal async Task<SettlementAllocation> AllocateCoreAsync(Guid companyId, JournalLine debit, JournalLine credit, decimal amount,
        DateOnly date, string origin, Guid? sourceVoucherId, Guid userId, decimal? foreignAmount = null)
    {
        if (amount <= 0) throw new DomainException(ErrorCodes.AmountNotPositive, "Số tiền phân bổ phải lớn hơn 0.");
        if (debit.Side != Side.Debit || credit.Side != Side.Credit)
            throw new DomainException(ErrorCodes.SettlementMismatch, "Phân bổ phải ghép một dòng Nợ với một dòng Có.");
        if (debit.CompanyId != companyId || credit.CompanyId != companyId || debit.AccountId != credit.AccountId)
            throw new DomainException(ErrorCodes.SettlementMismatch, "Hai dòng phân bổ phải cùng công ty và cùng tài khoản.");
        if (debit.PartnerId is null || debit.PartnerId != credit.PartnerId)
            throw new DomainException(ErrorCodes.SettlementMismatch, "Hai dòng phân bổ phải cùng đối tượng công nợ.");
        var account = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == debit.AccountId);
        if (account.OpenItemNature == OpenItemNature.None)
            throw new DomainException(ErrorCodes.SettlementMismatch, $"Tài khoản {account.Code} không theo dõi công nợ theo chứng từ.");
        if (date < Max(debit.PostingDate, credit.PostingDate))
            throw new DomainException(ErrorCodes.ValidationFailed, "Ngày phân bổ không được trước ngày hạch toán của các dòng.");

        if (debit.ForeignCurrency != credit.ForeignCurrency)
            throw new DomainException(ErrorCodes.SettlementMismatch,
                $"Không phân bổ khoản {debit.ForeignCurrency ?? "VND"} với khoản {credit.ForeignCurrency ?? "VND"}; dùng chức năng thu/chi ngoại tệ theo hóa đơn.");
        var (dOpenF, dOpen) = await OpenBothAsync(debit);
        var (cOpenF, cOpen) = await OpenBothAsync(credit);
        if (amount > dOpen || amount > cOpen)
            throw new DomainException(ErrorCodes.SettlementExceedsOpen,
                $"Số phân bổ {amount} vượt số còn lại (Nợ còn {dOpen}, Có còn {cOpen}). Phần vượt phải để là khoản ứng trước chưa phân bổ.");
        if (debit.ForeignCurrency is null) foreignAmount = null;
        else
        {
            foreignAmount ??= 0;
            if (foreignAmount < 0 || foreignAmount > dOpenF || foreignAmount > cOpenF)
                throw new DomainException(ErrorCodes.SettlementExceedsOpen,
                    $"Số nguyên tệ phân bổ {foreignAmount} vượt số còn lại (Nợ còn {dOpenF}, Có còn {cOpenF} {debit.ForeignCurrency}).");
        }

        var a = new SettlementAllocation
        {
            Id = Ids.New(), CompanyId = companyId, AccountId = debit.AccountId, PartnerId = debit.PartnerId.Value,
            DebitLineId = debit.Id, CreditLineId = credit.Id, Amount = amount, ForeignAmount = foreignAmount, AllocationDate = date,
            Origin = origin, SourceVoucherId = sourceVoucherId, CreatedAt = clock.UtcNow, CreatedBy = userId,
        };
        db.SettlementAllocations.Add(a);
        return a;
    }

    /// <summary>Voids every active allocation touching the given journal lines (used by reversal).</summary>
    internal async Task<int> VoidForLinesAsync(Guid companyId, IReadOnlyCollection<Guid> lineIds, Guid userId, string reason)
    {
        var list = await db.SettlementAllocations
            .Where(a => a.CompanyId == companyId && a.VoidedAt == null && (lineIds.Contains(a.DebitLineId) || lineIds.Contains(a.CreditLineId)))
            .ToListAsync();
        foreach (var a in list) Void(a, userId, reason);
        return list.Count;
    }

    // ---------------------------------------------------------------- helpers

    private void Void(SettlementAllocation a, Guid userId, string reason)
    {
        a.VoidedAt = clock.UtcNow;
        a.VoidedBy = userId;
        a.VoidReason = reason;
    }

    /// <summary>Active allocations of a line (functional and foreign): database rows merged with this unit of work's pending changes.</summary>
    private async Task<(decimal Amount, decimal Foreign)> AllocatedAsync(Guid lineId)
    {
        var tracked = db.ChangeTracker.Entries<SettlementAllocation>()
            .Where(e => e.Entity.DebitLineId == lineId || e.Entity.CreditLineId == lineId)
            .ToDictionary(e => e.Entity.Id, e => e.Entity);
        var stored = await db.SettlementAllocations.AsNoTracking()
            .Where(a => a.DebitLineId == lineId || a.CreditLineId == lineId)
            .Select(a => new { a.Id, a.Amount, a.ForeignAmount, a.VoidedAt }).ToListAsync();
        var fromDb = stored.Where(s => !tracked.ContainsKey(s.Id) && s.VoidedAt == null).ToList();
        var pending = tracked.Values.Where(t => t.VoidedAt == null).ToList();
        return (fromDb.Sum(s => s.Amount) + pending.Sum(t => t.Amount), fromDb.Sum(s => s.ForeignAmount ?? 0) + pending.Sum(t => t.ForeignAmount ?? 0));
    }

    private async Task<List<OpenItem>> OpenItemsCoreAsync(Guid companyId, Guid accountId, Guid? partnerId, DateOnly asOf)
    {
        var q = from l in db.JournalLines
                join e in db.JournalEntries on l.JournalEntryId equals e.Id
                where l.CompanyId == companyId && l.AccountId == accountId && l.PostingDate <= asOf && l.PartnerId != null
                select new { l, e.VoucherId, e.VoucherNumber, e.VoucherType };
        if (partnerId is not null) q = q.Where(x => x.l.PartnerId == partnerId);
        var lines = await q.AsNoTracking().ToListAsync();

        var allocs = await db.SettlementAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountId == accountId && a.VoidedAt == null && a.AllocationDate <= asOf)
            .Select(a => new { a.DebitLineId, a.CreditLineId, a.Amount, a.ForeignAmount }).ToListAsync();
        var allocated = new Dictionary<Guid, decimal>();
        var allocatedForeign = new Dictionary<Guid, decimal>();
        foreach (var a in allocs)
        {
            allocated[a.DebitLineId] = allocated.GetValueOrDefault(a.DebitLineId) + a.Amount;
            allocated[a.CreditLineId] = allocated.GetValueOrDefault(a.CreditLineId) + a.Amount;
            allocatedForeign[a.DebitLineId] = allocatedForeign.GetValueOrDefault(a.DebitLineId) + (a.ForeignAmount ?? 0);
            allocatedForeign[a.CreditLineId] = allocatedForeign.GetValueOrDefault(a.CreditLineId) + (a.ForeignAmount ?? 0);
        }
        return lines.Select(x =>
        {
            var alloc = allocated.GetValueOrDefault(x.l.Id);
            return new OpenItem(x.l.Id, x.VoucherId, x.VoucherNumber, x.VoucherType, x.l.PostingDate, x.l.DueDate ?? x.l.PostingDate,
                x.l.AccountId, x.l.PartnerId!.Value, x.l.Side, x.l.Amount, alloc, x.l.Amount - alloc, x.l.Description,
                x.l.ForeignCurrency, x.l.ForeignAmount, x.l.ForeignCurrency is null ? null : (x.l.ForeignAmount ?? 0) - allocatedForeign.GetValueOrDefault(x.l.Id));
        }).OrderBy(i => i.PostingDate).ThenBy(i => i.VoucherNumber).ToList();
    }

    private async Task RequireOpenPeriodAsync(Guid companyId, DateOnly date)
    {
        var p = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.StartDate <= date && x.EndDate >= date)
                ?? throw new DomainException(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {date}.");
        if (p.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, $"Kỳ {p.StartDate:MM/yyyy} đã khóa sổ.");
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
