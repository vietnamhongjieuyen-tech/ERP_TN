using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Accounting;

/// <summary>Level = depth in the chart (0 = top). IsGroup rows are parent (summary) accounts: each column is the sum of its descendants.</summary>
public sealed record TrialBalanceRow(
    Guid AccountId, string AccountCode, string AccountName,
    decimal OpeningDebit, decimal OpeningCredit, decimal PeriodDebit, decimal PeriodCredit,
    decimal ClosingDebit, decimal ClosingCredit, int Level = 0, bool IsGroup = false);

/// <summary>Rows = detail accounts (the totals and the balance check use these only); Groups = parent accounts when rolled up.</summary>
public sealed record TrialBalance(DateOnly From, DateOnly To, IReadOnlyList<TrialBalanceRow> Rows)
{
    public IReadOnlyList<TrialBalanceRow> Groups { get; init; } = [];

    public decimal TotalOpeningDebit => Rows.Sum(r => r.OpeningDebit);
    public decimal TotalOpeningCredit => Rows.Sum(r => r.OpeningCredit);
    public decimal TotalPeriodDebit => Rows.Sum(r => r.PeriodDebit);
    public decimal TotalPeriodCredit => Rows.Sum(r => r.PeriodCredit);
    public decimal TotalClosingDebit => Rows.Sum(r => r.ClosingDebit);
    public decimal TotalClosingCredit => Rows.Sum(r => r.ClosingCredit);

    /// <summary>INV-09: every column pair balances.</summary>
    public bool IsBalanced => TotalOpeningDebit == TotalOpeningCredit && TotalPeriodDebit == TotalPeriodCredit && TotalClosingDebit == TotalClosingCredit;
}

public sealed record JournalBookRow(DateOnly PostingDate, string VoucherType, string VoucherNumber, Guid VoucherId, int LineNo,
    string AccountCode, string? PartnerCode, decimal Debit, decimal Credit, string? Description);

public sealed record AccountLedgerRow(DateOnly PostingDate, string VoucherType, string VoucherNumber, Guid VoucherId, string? Description,
    string CounterAccounts, decimal Debit, decimal Credit, decimal RunningBalance, string? ForeignCurrency = null, decimal? ForeignAmount = null);

/// <summary>Signed foreign-currency balance (debit positive) of the account at the end of the range.</summary>
public sealed record ForeignBalance(string Currency, decimal Closing);

public sealed record AccountLedger(Guid AccountId, string AccountCode, string AccountName, DateOnly From, DateOnly To,
    decimal OpeningBalance, IReadOnlyList<AccountLedgerRow> Rows, decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance)
{
    /// <summary>One row per foreign currency used on the account (empty for functional-currency accounts).</summary>
    public IReadOnlyList<ForeignBalance> ForeignClosing { get; init; } = [];
}

/// <summary>CalculateTrialBalance / CalculateAccountBalance / general journal (docs/03 §3).</summary>
public sealed class LedgerQueryService(HakDbContext db)
{
    /// <summary>Signed balance (debit positive) of an account up to and including <paramref name="asOf"/>.</summary>
    public async Task<decimal> AccountBalanceAsync(SessionContext ctx, Guid accountId, DateOnly asOf, Guid? partnerId = null)
    {
        ctx.Require(Permissions.ReportView);
        var q = db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId && l.PostingDate <= asOf);
        if (partnerId is not null) q = q.Where(l => l.PartnerId == partnerId);
        var sums = await SumBySideAsync(q);
        return sums.Sum(s => (int)s.Side * s.Total);
    }

    public async Task<TrialBalance> TrialBalanceAsync(SessionContext ctx, DateOnly from, DateOnly to, bool rollup = false)
    {
        var tb = await TrialBalanceCoreAsync(ctx, from, to);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Id);
        int Depth(Account a) { var d = 0; for (var p = a.ParentId; p is { } id && accounts.TryGetValue(id, out var parent); p = parent.ParentId) d++; return d; }
        var rows = tb.Rows.Select(r => r with { Level = Depth(accounts[r.AccountId]) }).ToList();
        if (!rollup) return tb with { Rows = rows };

        // Cộng dồn: every column of a parent account is the sum of the same column of its descendants (no netting).
        var sums = new Dictionary<Guid, decimal[]>();
        foreach (var r in rows)
            for (var p = accounts[r.AccountId].ParentId; p is { } id && accounts.TryGetValue(id, out var parent); p = parent.ParentId)
            {
                if (!sums.TryGetValue(id, out var s)) sums[id] = s = new decimal[6];
                s[0] += r.OpeningDebit; s[1] += r.OpeningCredit; s[2] += r.PeriodDebit; s[3] += r.PeriodCredit; s[4] += r.ClosingDebit; s[5] += r.ClosingCredit;
            }
        var groups = sums.Select(kv =>
        {
            var a = accounts[kv.Key];
            var s = kv.Value;
            return new TrialBalanceRow(a.Id, a.Code, a.Name, s[0], s[1], s[2], s[3], s[4], s[5], Depth(a), IsGroup: true);
        }).OrderBy(g => g.AccountCode, StringComparer.Ordinal).ToList();
        return tb with { Rows = rows, Groups = groups };
    }

    private async Task<TrialBalance> TrialBalanceCoreAsync(SessionContext ctx, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        if (to < from) throw new DomainException(ErrorCodes.ValidationFailed, "Khoảng ngày không hợp lệ.");

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Id);
        // Opening-balance vouchers (OPB) are "số dư đầu kỳ chuyển sang": always in the opening columns, never a movement.
        var baseQ = db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.PostingDate <= to);
        var opening = await SumBySideAsync(baseQ.Where(l => l.PostingDate < from || db.JournalEntries.Any(e => e.Id == l.JournalEntryId && e.VoucherType == VoucherTypes.OpeningBalance)));
        var period = await SumBySideAsync(baseQ.Where(l => l.PostingDate >= from && !db.JournalEntries.Any(e => e.Id == l.JournalEntryId && e.VoucherType == VoucherTypes.OpeningBalance)));

        // Indexed once by (account, partner, side): looking each total up in the lists is quadratic with thousands of partners.
        var openingBy = opening.ToDictionary(s => (s.AccountId, s.PartnerId, s.Side), s => s.Total);
        var periodBy = period.ToDictionary(s => (s.AccountId, s.PartnerId, s.Side), s => s.Total);
        var partnersOf = opening.Concat(period).GroupBy(s => s.AccountId).ToDictionary(g => g.Key, g => g.Select(s => s.PartnerId).Distinct().ToList());

        var rows = new List<TrialBalanceRow>();
        foreach (var (accId, partnerKeys) in partnersOf)
        {
            var acc = accounts[accId];

            decimal od = 0, oc = 0, pd = 0, pc = 0, cd = 0, cc = 0, netOpening = 0, netClosing = 0;
            foreach (var pk in partnerKeys)
            {
                var o = openingBy.GetValueOrDefault((accId, pk, Side.Debit)) - openingBy.GetValueOrDefault((accId, pk, Side.Credit));
                var d = periodBy.GetValueOrDefault((accId, pk, Side.Debit));
                var c = periodBy.GetValueOrDefault((accId, pk, Side.Credit));
                var close = BalanceMath.Closing(o, d, c);
                pd += d; pc += c;
                netOpening += o; netClosing += close;
                if (acc.BalanceDisplay == BalanceDisplay.GrossByPartner)
                {
                    var (a, b) = BalanceMath.ToColumns(o); od += a; oc += b;
                    var (x, y) = BalanceMath.ToColumns(close); cd += x; cc += y;
                }
            }
            if (acc.BalanceDisplay == BalanceDisplay.Net)
            {
                (od, oc) = BalanceMath.ToColumns(netOpening);
                (cd, cc) = BalanceMath.ToColumns(netClosing);
            }
            rows.Add(new TrialBalanceRow(acc.Id, acc.Code, acc.Name, od, oc, pd, pc, cd, cc));
        }
        return new TrialBalance(from, to, rows.OrderBy(r => r.AccountCode, StringComparer.Ordinal).ToList());
    }

    public async Task<IReadOnlyList<JournalBookRow>> GeneralJournalAsync(SessionContext ctx, DateOnly fromDate, DateOnly toDate)
    {
        ctx.Require(Permissions.ReportView);
        var rows = await (from l in db.JournalLines
                          join e in db.JournalEntries on l.JournalEntryId equals e.Id
                          join a in db.Accounts on l.AccountId equals a.Id
                          join p in db.BusinessPartners on l.PartnerId equals (Guid?)p.Id into pj
                          from p in pj.DefaultIfEmpty()
                          where l.CompanyId == ctx.CompanyId && l.PostingDate >= fromDate && l.PostingDate <= toDate
                          orderby l.PostingDate, e.VoucherNumber, l.LineNo
                          select new { l.PostingDate, e.VoucherType, e.VoucherNumber, e.VoucherId, l.LineNo, a.Code, PartnerCode = p == null ? null : p.Code, l.Side, l.Amount, l.Description })
            .AsNoTracking().ToListAsync();
        return rows.Select(r => new JournalBookRow(r.PostingDate, r.VoucherType, r.VoucherNumber, r.VoucherId, r.LineNo, r.Code, r.PartnerCode,
            r.Side == Side.Debit ? r.Amount : 0, r.Side == Side.Credit ? r.Amount : 0, r.Description)).ToList();
    }

    /// <summary>
    /// Account ledger (sổ chi tiết tài khoản; for a cash account it is the cash book): opening balance,
    /// each posted line with its counter-accounts and running balance, totals and closing balance.
    /// </summary>
    public async Task<AccountLedger> AccountLedgerAsync(SessionContext ctx, Guid accountId, DateOnly fromDate, DateOnly toDate, Guid? partnerId = null)
    {
        ctx.Require(Permissions.ReportView);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
        var opening = await AccountBalanceAsync(ctx, accountId, fromDate.AddDays(-1), partnerId);

        var q = from l in db.JournalLines
                join e in db.JournalEntries on l.JournalEntryId equals e.Id
                where l.CompanyId == ctx.CompanyId && l.AccountId == accountId && l.PostingDate >= fromDate && l.PostingDate <= toDate
                select new { l, e.VoucherId, e.VoucherNumber, e.VoucherType };
        if (partnerId is not null) q = q.Where(x => x.l.PartnerId == partnerId);
        var all = await q.AsNoTracking().ToListAsync();
        // Opening-balance vouchers inside the range are part of the opening balance, not rows of the book.
        opening += all.Where(x => x.VoucherType == VoucherTypes.OpeningBalance).Sum(x => (int)x.l.Side * x.l.Amount);
        var lines = all.Where(x => x.VoucherType != VoucherTypes.OpeningBalance).ToList();

        // Lines of the same entries, found by joining on the same filter (a large IN list of entry ids plans badly on SQLite).
        var mine = db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId && l.PostingDate >= fromDate && l.PostingDate <= toDate);
        if (partnerId is not null) mine = mine.Where(l => l.PartnerId == partnerId);
        var siblings = await (from m in mine.Select(m => m.JournalEntryId).Distinct()
                              join l in db.JournalLines on m equals l.JournalEntryId
                              join a in db.Accounts on l.AccountId equals a.Id
                              select new { l.JournalEntryId, l.Side, a.Code }).AsNoTracking().ToListAsync();
        // Counter-accounts per (entry, side): one pass instead of scanning every sibling for every row.
        var counterCodes = siblings.GroupBy(s => (s.JournalEntryId, s.Side))
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(s => s.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal)));

        var running = opening;
        var rows = new List<AccountLedgerRow>();
        foreach (var x in lines.OrderBy(x => x.l.PostingDate).ThenBy(x => x.VoucherNumber).ThenBy(x => x.l.LineNo))
        {
            running += (int)x.l.Side * x.l.Amount;
            var counters = counterCodes.GetValueOrDefault((x.l.JournalEntryId, x.l.Side == Side.Debit ? Side.Credit : Side.Debit), "");
            rows.Add(new AccountLedgerRow(x.l.PostingDate, x.VoucherType, x.VoucherNumber, x.VoucherId, x.l.Description, counters,
                x.l.Side == Side.Debit ? x.l.Amount : 0, x.l.Side == Side.Credit ? x.l.Amount : 0, running, x.l.ForeignCurrency, x.l.ForeignAmount));
        }

        // Foreign balance at the end of the range: revaluation lines carry the currency code without a foreign amount, so the
        // sum of foreign amounts is the foreign balance itself.
        var fq = db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accountId && l.PostingDate <= toDate && l.ForeignCurrency != null && l.ForeignAmount != null);
        if (partnerId is not null) fq = fq.Where(l => l.PartnerId == partnerId);
        var foreign = (await fq.Select(l => new { l.ForeignCurrency, l.Side, l.ForeignAmount }).ToListAsync())
            .GroupBy(x => x.ForeignCurrency!).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ForeignBalance(g.Key, g.Sum(x => (int)x.Side * x.ForeignAmount!.Value))).ToList();
        return new AccountLedger(account.Id, account.Code, account.Name, fromDate, toDate, opening, rows,
            rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), running) { ForeignClosing = foreign };
    }

    private sealed record SideSum(Guid AccountId, Guid? PartnerId, Side Side, decimal Total);

    /// <summary>
    /// Aggregates in the database: SUM over the stored column (INTEGER ×10⁴ on SQLite), so no
    /// floating point is involved. Each group's total comes back through the money converter.
    /// </summary>
    private static async Task<List<SideSum>> SumBySideAsync(IQueryable<JournalLine> q)
        => await q.GroupBy(k => new { k.AccountId, k.PartnerId, k.Side })
            .Select(g => new SideSum(g.Key.AccountId, g.Key.PartnerId, g.Key.Side, g.Sum(x => x.Amount)))
            .ToListAsync();

}
