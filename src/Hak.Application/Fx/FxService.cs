using System.Text.Json;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Fx;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Fx;

public sealed record FxBalanceRow(Guid AccountId, string AccountCode, Guid? PartnerId, string? PartnerCode, string Currency,
    decimal ForeignBalance, decimal BookBalance, decimal? BookRate, bool Revalued, string? Note);

/// <summary>
/// Foreign-currency monetary items (master prompt: FX). Balances are tracked per (account, partner, currency) from the
/// currency-tagged journal lines. The period-end run revalues every bucket of a non-open-item account (cash, bank, loans…)
/// at the closing rate the user enters, and books the difference against the account mapped to role FX_REVALUATION
/// (where it goes afterwards — straight to financial income/expense or via a revaluation-difference account — is the
/// company's closing rule, [CẦN XÁC MINH căn cứ TT99]). Receivables/payables are revalued per open item: the item is closed
/// at its carrying amount and re-opened at the closing rate (debit items at the debit-balance rate, advances at the
/// credit-balance rate). Realised differences on payment are booked by <see cref="FxPaymentService"/>.
/// </summary>
public sealed class FxService(HakDbContext db, AccountingEngine engine, AuditService audit, IClock clock)
{
    public const string FxSource = "HAK.FX";
    public const string RevaluationRole = "FX_REVALUATION";

    public async Task<IReadOnlyList<FxBalanceRow>> BalancesAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var (buckets, accounts) = await BucketsAsync(ctx.CompanyId, asOf);
        var partnerIds = buckets.Where(b => b.PartnerId is not null).Select(b => b.PartnerId!.Value).Distinct().ToList();
        var partners = await db.BusinessPartners.AsNoTracking().Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code);
        return buckets.Select(b =>
        {
            var acc = accounts[b.AccountId];
            return new FxBalanceRow(b.AccountId, acc.Code, b.PartnerId, b.PartnerId is { } p ? partners[p] : null, b.Currency, b.ForeignBalance, b.BookBalance,
                b.BookRate, true, acc.OpenItemNature == OpenItemNature.None ? null : "Đánh giá lại theo từng hóa đơn còn mở");
        }).OrderBy(r => r.AccountCode, StringComparer.Ordinal).ThenBy(r => r.PartnerCode).ThenBy(r => r.Currency).ToList();
    }

    public async Task<FxRevaluationRun> RevalueAsync(SessionContext ctx, DateOnly date, IReadOnlyList<FxRate> rates, string rateSource)
    {
        ctx.Require(Permissions.VoucherPost);
        if (string.IsNullOrWhiteSpace(rateSource)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi nguồn tỷ giá (ngân hàng, thời điểm).");
        if (rates.Select(r => r.Currency.ToUpperInvariant()).Distinct().Count() != rates.Count)
            throw new DomainException(ErrorCodes.ValidationFailed, "Mỗi loại tiền chỉ nhập một bộ tỷ giá.");
        return await db.InTransactionAsync(async () =>
        {
            var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == ctx.CompanyId && p.StartDate <= date && p.EndDate >= date)
                         ?? throw new DomainException(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {date:dd/MM/yyyy}.");
            if (period.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, $"Kỳ {period.StartDate:MM/yyyy} đã khóa sổ.");
            var last = await LastActiveAsync(ctx.CompanyId);
            if (last is not null && last.RevaluationDate >= date)
                throw new DomainException(ErrorCodes.InvalidStateTransition,
                    $"Đã đánh giá lại tỷ giá ngày {last.RevaluationDate:dd/MM/yyyy}; hủy lần đó trước nếu cần đánh giá lại từ ngày sớm hơn.");
            var role = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.RoleCode == RevaluationRole)
                           .Select(m => (Guid?)m.AccountId).FirstOrDefaultAsync()
                       ?? throw new DomainException(ErrorCodes.RuleRoleUnmapped, $"Chưa ánh xạ vai trò tài khoản {RevaluationRole} (TK nhận chênh lệch đánh giá lại).");

            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var dp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
            var (all, accounts) = await BucketsAsync(ctx.CompanyId, date);
            var buckets = all.Where(b => accounts[b.AccountId].OpenItemNature == OpenItemNature.None && b.Currency != company.FunctionalCurrency).ToList();
            var byCurrency = rates.ToDictionary(r => r.Currency.ToUpperInvariant());
            var missing = buckets.Where(b => b.ForeignBalance != 0 && !byCurrency.ContainsKey(b.Currency)).Select(b => b.Currency).Distinct().ToList();
            if (missing.Count > 0)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Thiếu tỷ giá cuối kỳ cho: {string.Join(", ", missing)}.");

            var run = new FxRevaluationRun
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, RevaluationDate = date, RatesJson = JsonSerializer.Serialize(rates),
                RateSource = rateSource.Trim(), CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            };
            var gl = new List<VoucherLineInput>();
            var result = new List<object>();
            decimal gains = 0, losses = 0;
            foreach (var b in buckets)
            {
                var rate = byCurrency.GetValueOrDefault(b.Currency) ?? new FxRate(b.Currency, 1, 1); // only reached for zero foreign balances
                var (target, adjustment) = FxCalculator.Revalue(b, rate, dp);
                if (adjustment == 0) continue;
                var label = b.ForeignBalance == 0 ? $"Xử lý số dư quy đổi còn lại {b.Currency}" : $"Đánh giá lại {b.Currency} {b.ForeignBalance:N2} theo tỷ giá cuối kỳ";
                gl.Add(new VoucherLineInput(b.AccountId, adjustment > 0 ? Side.Debit : Side.Credit, Math.Abs(adjustment), b.PartnerId, ForeignCurrency: b.Currency, Description: label));
                if (adjustment > 0) gains += adjustment; else losses -= adjustment;
                result.Add(new { account = accounts[b.AccountId].Code, b.PartnerId, b.Currency, b.ForeignBalance, book = b.BookBalance, target, adjustment });
            }
            // Receivables/payables: each open foreign item is closed at its carrying amount and re-opened at the closing rate
            // (same partner, due date and foreign amount), so later payments settle it at the revalued rate.
            var settlement = new Settlement.SettlementService(db, audit, clock);
            var openItemAccounts = all.Select(b => b.AccountId).Distinct().Where(a => accounts[a].OpenItemNature != OpenItemNature.None).ToList();
            foreach (var accountId in openItemAccounts)
            {
                foreach (var item in (await settlement.OpenItemsAsync(ctx, accountId, null, date)).Where(i => i.ForeignCurrency is not null && i.ForeignCurrency != company.FunctionalCurrency))
                {
                    var openForeign = item.OpenForeign ?? 0;
                    var cur = item.ForeignCurrency!;
                    var closeSide = item.Side == Side.Debit ? Side.Credit : Side.Debit;
                    decimal target = 0;
                    if (openForeign != 0)
                    {
                        if (!byCurrency.TryGetValue(cur, out var r)) throw new DomainException(ErrorCodes.ValidationFailed, $"Thiếu tỷ giá cuối kỳ cho: {cur}.");
                        var rate = item.Side == Side.Debit ? r.DebitBalanceRate : r.CreditBalanceRate;
                        target = MoneyMath.Convert(openForeign, rate, dp);
                        if (target == item.Open) continue;
                        var closeRate = Domain.Fx.FxSettlementMath.ExactRate(openForeign, item.Open, dp)
                                        ?? throw new DomainException(ErrorCodes.FxBookValue, $"Không biểu diễn được giá trị ghi sổ của {item.VoucherNumber} theo tỷ giá 6 chữ số.");
                        gl.Add(new VoucherLineInput(accountId, closeSide, item.Open, item.PartnerId, cur, openForeign, closeRate,
                            $"Tất toán {item.VoucherNumber} theo giá trị ghi sổ để đánh giá lại", SettleJournalLineId: item.JournalLineId));
                        gl.Add(new VoucherLineInput(accountId, item.Side, target, item.PartnerId, cur, openForeign, rate,
                            $"{item.VoucherNumber} đánh giá lại {openForeign:N2} {cur} theo tỷ giá {rate:N2}", item.DueDate));
                    }
                    else if (item.Open != 0)
                        gl.Add(new VoucherLineInput(accountId, closeSide, item.Open, item.PartnerId, ForeignCurrency: cur,
                            Description: $"Xử lý số quy đổi còn lại của {item.VoucherNumber}", SettleJournalLineId: item.JournalLineId));
                    else continue;
                    var delta = (int)item.Side * (target - item.Open);   // debit-positive change of the account
                    if (delta > 0) gains += delta; else losses -= delta;
                    result.Add(new { account = accounts[accountId].Code, item.PartnerId, item = item.VoucherNumber, cur, openForeign, book = item.Open, target, adjustment = delta });
                }
            }

            if (gains > 0) gl.Add(new VoucherLineInput(role, Side.Credit, gains, Description: "Lãi chênh lệch tỷ giá do đánh giá lại"));
            if (losses > 0) gl.Add(new VoucherLineInput(role, Side.Debit, losses, Description: "Lỗ chênh lệch tỷ giá do đánh giá lại"));
            db.FxRevaluationRuns.Add(run);
            run.ResultJson = JsonSerializer.Serialize(result);
            if (gl.Count > 0)
            {
                var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(VoucherTypes.Revaluation, date, date,
                    $"Đánh giá lại khoản mục tiền tệ có gốc ngoại tệ ngày {date:dd/MM/yyyy} ({rateSource.Trim()})", gl, SourceSystem: FxSource, SourceId: run.Id.ToString()));
                run.VoucherId = v.Id;
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "fx.revalue", "fx_revaluation_run", run.Id.ToString(), new { date, rates, rateSource, result });
            await db.SaveChangesAsync();
            return run;
        });
    }

    public async Task ReverseAsync(SessionContext ctx, Guid runId, string reason)
    {
        ctx.Require(Permissions.VoucherReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy đánh giá lại.");
        await db.InTransactionAsync(async () =>
        {
            var run = await db.FxRevaluationRuns.FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Lần đánh giá lại không tồn tại.");
            if (run.Status != FxRunStatus.Active) throw new DomainException(ErrorCodes.InvalidStateTransition, "Lần đánh giá lại đã bị hủy.");
            var last = await LastActiveAsync(ctx.CompanyId);
            if (last!.Id != run.Id) throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ hủy được lần đánh giá lại gần nhất.");
            if (run.VoucherId is { } runVoucher)
            {
                var lineIds = await (from l in db.JournalLines join e in db.JournalEntries on l.JournalEntryId equals e.Id
                                     where e.VoucherId == runVoucher select l.Id).ToListAsync();
                if (await db.SettlementAllocations.AnyAsync(a => a.VoidedAt == null && a.SourceVoucherId != runVoucher
                                                                 && (lineIds.Contains(a.DebitLineId) || lineIds.Contains(a.CreditLineId))))
                    throw new DomainException(ErrorCodes.InvalidStateTransition,
                        "Khoản công nợ sau đánh giá lại đã được thanh toán / phân bổ; đảo chứng từ thanh toán hoặc hủy phân bổ đó trước.");
            }
            if (run.VoucherId is { } vid) await engine.ReverseCoreAsync(ctx, vid, run.RevaluationDate, $"Hủy đánh giá lại tỷ giá: {reason}", allowModuleOwned: true);
            run.Status = FxRunStatus.Reversed;
            run.ReversedAt = clock.UtcNow;
            run.ReversedBy = ctx.UserId;
            await audit.AppendAsync(ctx.CompanyId, ctx, "fx.reverse", "fx_revaluation_run", run.Id.ToString(), new { reason });
            await db.SaveChangesAsync();
        });
    }

    public async Task<IReadOnlyList<FxRevaluationRun>> RunsAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.ReportView);
        return await db.FxRevaluationRuns.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId)
            .OrderByDescending(r => r.RevaluationDate).ThenByDescending(r => r.CreatedAt).ToListAsync();
    }

    private Task<FxRevaluationRun?> LastActiveAsync(Guid companyId)
        => db.FxRevaluationRuns.AsNoTracking().Where(r => r.CompanyId == companyId && r.Status == FxRunStatus.Active)
            .OrderByDescending(r => r.RevaluationDate).FirstOrDefaultAsync();

    private async Task<(List<FxBucket> Buckets, Dictionary<Guid, Account> Accounts)> BucketsAsync(Guid companyId, DateOnly asOf)
    {
        var lines = await db.JournalLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.ForeignCurrency != null && l.PostingDate <= asOf)
            .Select(l => new { l.AccountId, l.PartnerId, l.ForeignCurrency, l.Side, l.Amount, l.ForeignAmount }).ToListAsync();
        var buckets = FxCalculator.Buckets(lines.Select(l => (l.AccountId, l.PartnerId, l.ForeignCurrency!.ToUpperInvariant(), l.Side, l.Amount, l.ForeignAmount)));
        var ids = buckets.Select(b => b.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
        return (buckets, accounts);
    }
}
