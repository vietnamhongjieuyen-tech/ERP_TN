using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Banking;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Banking;

public sealed record StatementLineInput(DateOnly TransactionDate, decimal Amount, string BankReference, string Description,
    string? CounterpartyName = null, string? CounterpartyAccount = null);

public sealed record StatementInput(Guid BankAccountId, DateOnly FromDate, DateOnly ToDate, decimal OpeningBalance, decimal ClosingBalance,
    string SourceName, string SourceContent, IReadOnlyList<StatementLineInput> Lines);

/// <summary><see cref="ReferenceMatched"/>: the bank reference appears on the voucher or the voucher number on the statement line.</summary>
public sealed record MatchCandidate(Guid JournalLineId, string VoucherNumber, DateOnly PostingDate, decimal Amount, int Score, string? Description,
    bool ReferenceMatched = false);

public sealed record AutoMatchResult(int Matched, int Pending, int Unmatched);

public sealed record UnmatchedStatementLine(Guid Id, DateOnly TransactionDate, decimal Amount, string BankReference, string Description,
    StatementLineStatus Status, IReadOnlyList<MatchCandidate> Candidates);

public sealed record UnmatchedLedgerLine(Guid JournalLineId, string VoucherNumber, DateOnly PostingDate, decimal SignedAmount, string? Description);

/// <summary>Bank balance per statement = book balance + items only on the statement − items only in the books (+ unexplained).</summary>
public sealed record BankReconciliationReport(Guid BankAccountId, DateOnly AsOf, decimal BookBalance, decimal? StatementBalance,
    IReadOnlyList<UnmatchedStatementLine> StatementOnly, IReadOnlyList<UnmatchedLedgerLine> BookOnly, decimal? UnexplainedDifference);

/// <summary>
/// Bank statement import and matching (master prompt §11). Matching only creates links in
/// reconciliation_matches; it never edits vouchers or journal lines. When several ledger lines fit a
/// statement line equally well, the line goes to the confirmation queue instead of being guessed.
/// </summary>
public sealed class BankReconciliationService(HakDbContext db, AuditService audit, IClock clock)
{
    public const int AutoMatchThreshold = 60;

    public async Task<BankStatement> ImportAsync(SessionContext ctx, StatementInput input)
    {
        ctx.Require(Permissions.BankReconcile);
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Sao kê không có giao dịch.");
        if (input.ToDate < input.FromDate) throw new DomainException(ErrorCodes.ValidationFailed, "Khoảng ngày sao kê không hợp lệ.");
        var computed = input.OpeningBalance + input.Lines.Sum(l => l.Amount);
        if (computed != input.ClosingBalance)
            throw new DomainException(ErrorCodes.StatementUnbalanced,
                $"Số dư đầu {input.OpeningBalance} + phát sinh {input.Lines.Sum(l => l.Amount)} = {computed}, khác số dư cuối {input.ClosingBalance} trên sao kê.");
        if (input.Lines.Any(l => l.Amount == 0 || string.IsNullOrWhiteSpace(l.BankReference) || l.TransactionDate < input.FromDate || l.TransactionDate > input.ToDate))
            throw new DomainException(ErrorCodes.ValidationFailed, "Mỗi giao dịch phải có số tiền khác 0, mã giao dịch ngân hàng và ngày trong kỳ sao kê.");
        var dupInFile = input.Lines.GroupBy(l => l.BankReference.Trim()).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupInFile.Count > 0) throw new DomainException(ErrorCodes.StatementDuplicate, $"Mã giao dịch lặp trong file: {string.Join(", ", dupInFile)}.");

        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.SourceContent)));
        return await db.InTransactionAsync(async () =>
        {
            var bank = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == input.BankAccountId && a.CompanyId == ctx.CompanyId);
            if (bank is null || bank.CashKind != CashKind.Bank) throw new DomainException(ErrorCodes.ValidationFailed, "Tài khoản không phải tài khoản tiền gửi ngân hàng của công ty.");
            if (await db.BankStatements.AnyAsync(s => s.CompanyId == ctx.CompanyId && s.BankAccountId == bank.Id && s.SourceSha256 == sha))
                throw new DomainException(ErrorCodes.StatementDuplicate, "File sao kê này đã được nhập.");
            var refs = input.Lines.Select(l => l.BankReference.Trim()).ToList();
            var existing = await db.BankStatementLines.Where(l => l.CompanyId == ctx.CompanyId && l.BankAccountId == bank.Id && refs.Contains(l.BankReference))
                .Select(l => l.BankReference).ToListAsync();
            if (existing.Count > 0) throw new DomainException(ErrorCodes.StatementDuplicate, $"Giao dịch đã có trong sao kê nhập trước: {string.Join(", ", existing)}.");

            var previous = await db.BankStatements.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.BankAccountId == bank.Id && s.ToDate < input.FromDate)
                .OrderByDescending(s => s.ToDate).FirstOrDefaultAsync();
            if (previous is not null && previous.ToDate.AddDays(1) == input.FromDate && previous.ClosingBalance != input.OpeningBalance)
                throw new DomainException(ErrorCodes.StatementUnbalanced,
                    $"Số dư đầu {input.OpeningBalance} không khớp số dư cuối {previous.ClosingBalance} của sao kê liền trước.");

            var st = new BankStatement
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, BankAccountId = bank.Id, FromDate = input.FromDate, ToDate = input.ToDate,
                OpeningBalance = input.OpeningBalance, ClosingBalance = input.ClosingBalance, SourceName = input.SourceName, SourceSha256 = sha,
                ImportedAt = clock.UtcNow, ImportedBy = ctx.UserId,
            };
            st.Lines = input.Lines.Select((l, i) => new BankStatementLine
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, StatementId = st.Id, BankAccountId = bank.Id, LineNo = i + 1,
                TransactionDate = l.TransactionDate, Amount = l.Amount, BankReference = l.BankReference.Trim(), Description = l.Description,
                CounterpartyName = l.CounterpartyName, CounterpartyAccount = l.CounterpartyAccount,
            }).ToList();
            db.BankStatements.Add(st);
            await audit.AppendAsync(ctx.CompanyId, ctx, "bank_statement.import", "bank_statement", st.Id.ToString(),
                new { bank = bank.Code, input.FromDate, input.ToDate, lines = st.Lines.Count, sha, input.SourceName });
            await db.SaveChangesAsync();
            return st;
        });
    }

    /// <summary>
    /// Scores candidates (same bank account, same amount and direction, posting date within ± tolerance, not yet matched).
    /// A unique top candidate with score ≥ threshold is matched only when a reference ties them together (bank reference on
    /// the voucher, or voucher number in the statement text). Same amount and date alone — even when unique — only go to
    /// PendingConfirmation for a person to confirm (company decision 16/10/2026); ties or weak evidence likewise.
    /// </summary>
    public async Task<AutoMatchResult> AutoMatchAsync(SessionContext ctx, Guid statementId, int toleranceDays = 3)
    {
        ctx.Require(Permissions.BankReconcile);
        if (!await db.BankStatements.AnyAsync(s => s.Id == statementId && s.CompanyId == ctx.CompanyId))
            throw new DomainException(ErrorCodes.NotFound, "Sao kê không tồn tại.");
        return await db.InTransactionAsync(async () =>
        {
            var lines = await db.BankStatementLines.Where(l => l.CompanyId == ctx.CompanyId && l.StatementId == statementId && l.Status != StatementLineStatus.Matched)
                .OrderBy(l => l.LineNo).ToListAsync();
            var taken = new HashSet<Guid>();
            int matched = 0, pending = 0, unmatched = 0;
            foreach (var sl in lines)
            {
                var candidates = (await CandidatesAsync(ctx.CompanyId, sl, toleranceDays)).Where(c => !taken.Contains(c.JournalLineId)).ToList();
                var top = candidates.FirstOrDefault();
                var unique = top is not null && (candidates.Count == 1 || candidates[1].Score < top.Score);
                if (top is not null && unique && top.ReferenceMatched && top.Score >= AutoMatchThreshold)
                {
                    await MatchCoreAsync(ctx, sl, top.JournalLineId, MatchOrigin.Auto, top.Score);
                    taken.Add(top.JournalLineId);
                    matched++;
                }
                else if (candidates.Count > 0)
                {
                    sl.Status = StatementLineStatus.PendingConfirmation;
                    sl.CandidatesJson = JsonSerializer.Serialize(candidates.Select(c => c.JournalLineId));
                    pending++;
                }
                else
                {
                    sl.Status = StatementLineStatus.Unmatched;
                    sl.CandidatesJson = null;
                    unmatched++;
                }
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "bank_reconcile.auto", "bank_statement", statementId.ToString(), new { matched, pending, unmatched, toleranceDays });
            await db.SaveChangesAsync();
            return new AutoMatchResult(matched, pending, unmatched);
        });
    }

    public async Task<ReconciliationMatch> ConfirmAsync(SessionContext ctx, Guid statementLineId, Guid journalLineId)
    {
        ctx.Require(Permissions.BankReconcile);
        return await db.InTransactionAsync(async () =>
        {
            var sl = await db.BankStatementLines.FirstOrDefaultAsync(l => l.Id == statementLineId && l.CompanyId == ctx.CompanyId)
                     ?? throw new DomainException(ErrorCodes.NotFound, "Dòng sao kê không tồn tại.");
            if (sl.Status == StatementLineStatus.Matched) throw new DomainException(ErrorCodes.MatchInvalid, "Dòng sao kê đã được đối chiếu.");
            var m = await MatchCoreAsync(ctx, sl, journalLineId, MatchOrigin.Manual, 0);
            await audit.AppendAsync(ctx.CompanyId, ctx, "bank_reconcile.confirm", "bank_statement_line", sl.Id.ToString(), new { journalLineId });
            await db.SaveChangesAsync();
            return m;
        });
    }

    public async Task UnmatchAsync(SessionContext ctx, Guid matchId, string reason)
    {
        ctx.Require(Permissions.BankReconcile);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy đối chiếu.");
        await db.InTransactionAsync(async () =>
        {
            var m = await db.ReconciliationMatches.FirstOrDefaultAsync(x => x.Id == matchId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Kết quả đối chiếu không tồn tại.");
            if (m.VoidedAt is not null) throw new DomainException(ErrorCodes.InvalidStateTransition, "Kết quả đối chiếu đã bị hủy.");
            m.VoidedAt = clock.UtcNow;
            m.VoidedBy = ctx.UserId;
            m.VoidReason = reason;
            var sl = await db.BankStatementLines.FirstAsync(l => l.Id == m.StatementLineId);
            sl.Status = StatementLineStatus.Unmatched;
            var po = await PaymentOrderForLineAsync(m.JournalLineId);
            if (po is { Status: PaymentOrderStatus.Reconciled }) { po.UndoReconciliation(); po.RowVersion++; }
            await audit.AppendAsync(ctx.CompanyId, ctx, "bank_reconcile.unmatch", "reconciliation_match", m.Id.ToString(), new { reason });
            await db.SaveChangesAsync();
        });
    }

    public async Task<BankReconciliationReport> ReportAsync(SessionContext ctx, Guid bankAccountId, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var lines = await db.JournalLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == bankAccountId && l.PostingDate <= asOf)
            .Select(l => new { l.Id, l.Side, l.Amount, l.PostingDate, l.Description, l.JournalEntryId }).ToListAsync();
        var book = lines.Sum(l => (int)l.Side * l.Amount);
        var activeMatches = await db.ReconciliationMatches.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.VoidedAt == null)
            .Select(m => new { m.JournalLineId, m.StatementLineId }).ToListAsync();
        var matchedLedger = activeMatches.Select(m => m.JournalLineId).ToHashSet();

        var entryNumbers = await db.JournalEntries.AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId).ToDictionaryAsync(e => e.Id, e => e.VoucherNumber);
        var bookOnly = lines.Where(l => !matchedLedger.Contains(l.Id))
            .Select(l => new UnmatchedLedgerLine(l.Id, entryNumbers[l.JournalEntryId], l.PostingDate, (int)l.Side * l.Amount, l.Description))
            .OrderBy(l => l.PostingDate).ToList();

        var statementLines = await db.BankStatementLines.AsNoTracking()
            .Where(l => l.CompanyId == ctx.CompanyId && l.BankAccountId == bankAccountId && l.TransactionDate <= asOf && l.Status != StatementLineStatus.Matched)
            .OrderBy(l => l.TransactionDate).ThenBy(l => l.LineNo).ToListAsync();
        var statementOnly = new List<UnmatchedStatementLine>();
        foreach (var sl in statementLines)
            statementOnly.Add(new UnmatchedStatementLine(sl.Id, sl.TransactionDate, sl.Amount, sl.BankReference, sl.Description, sl.Status,
                sl.Status == StatementLineStatus.PendingConfirmation ? await CandidatesAsync(ctx.CompanyId, sl, 3) : []));

        var latest = await db.BankStatements.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.BankAccountId == bankAccountId && s.ToDate <= asOf)
            .OrderByDescending(s => s.ToDate).FirstOrDefaultAsync();
        decimal? bank = latest?.ClosingBalance;
        decimal? unexplained = bank is null ? null : bank - (book + statementOnly.Sum(s => s.Amount) - bookOnly.Sum(b => b.SignedAmount));
        return new BankReconciliationReport(bankAccountId, asOf, book, bank, statementOnly, bookOnly, unexplained);
    }

    // ------------------------------------------------------------------

    private async Task<ReconciliationMatch> MatchCoreAsync(SessionContext ctx, BankStatementLine sl, Guid journalLineId, MatchOrigin origin, int score)
    {
        var jl = await db.JournalLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == journalLineId && l.CompanyId == ctx.CompanyId)
                 ?? throw new DomainException(ErrorCodes.NotFound, "Dòng sổ cái không tồn tại.");
        if (jl.AccountId != sl.BankAccountId || (int)jl.Side * jl.Amount != sl.Amount)
            throw new DomainException(ErrorCodes.MatchInvalid, "Dòng sổ phải cùng tài khoản ngân hàng, cùng số tiền và cùng chiều tiền vào/ra với dòng sao kê.");
        if (await db.ReconciliationMatches.AnyAsync(m => m.JournalLineId == jl.Id && m.VoidedAt == null)
            || db.ReconciliationMatches.Local.Any(m => m.JournalLineId == jl.Id && m.VoidedAt == null))
            throw new DomainException(ErrorCodes.MatchInvalid, "Dòng sổ này đã khớp với một giao dịch sao kê khác.");
        var m = new ReconciliationMatch
        {
            Id = Ids.New(), CompanyId = ctx.CompanyId, StatementLineId = sl.Id, JournalLineId = jl.Id, Origin = origin, Score = score,
            CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
        };
        db.ReconciliationMatches.Add(m);
        sl.Status = StatementLineStatus.Matched;
        sl.CandidatesJson = null;
        var po = await PaymentOrderForLineAsync(jl.Id);
        if (po is { Status: PaymentOrderStatus.Posted }) { po.MarkReconciled(); po.RowVersion++; }
        return m;
    }

    private async Task<PaymentOrder?> PaymentOrderForLineAsync(Guid journalLineId)
    {
        var voucherId = await (from l in db.JournalLines join e in db.JournalEntries on l.JournalEntryId equals e.Id
                               where l.Id == journalLineId select e.VoucherId).FirstAsync();
        return await db.PaymentOrders.FirstOrDefaultAsync(p => p.VoucherId == voucherId);
    }

    private async Task<List<MatchCandidate>> CandidatesAsync(Guid companyId, BankStatementLine sl, int toleranceDays)
    {
        var side = sl.Amount > 0 ? Side.Debit : Side.Credit;
        var abs = Math.Abs(sl.Amount);
        var fromDate = sl.TransactionDate.AddDays(-toleranceDays);
        var toDate = sl.TransactionDate.AddDays(toleranceDays);
        var rows = await (from l in db.JournalLines
                          join e in db.JournalEntries on l.JournalEntryId equals e.Id
                          join v in db.Vouchers on e.VoucherId equals v.Id
                          where l.CompanyId == companyId && l.AccountId == sl.BankAccountId && l.Side == side && l.Amount == abs
                                && l.PostingDate >= fromDate && l.PostingDate <= toDate
                                && !db.ReconciliationMatches.Any(m => m.JournalLineId == l.Id && m.VoidedAt == null)
                          select new { l.Id, e.VoucherNumber, l.PostingDate, l.Amount, l.Description, VDesc = v.Description, v.SourceId })
            .AsNoTracking().ToListAsync();

        var reference = sl.BankReference.Trim();
        var result = rows.Select(r =>
        {
            var score = 50 - 5 * Math.Abs(r.PostingDate.DayNumber - sl.TransactionDate.DayNumber);
            if (r.PostingDate == sl.TransactionDate) score += 10;
            var byBankRef = reference.Length > 0 && ((r.Description?.Contains(reference, StringComparison.OrdinalIgnoreCase) ?? false)
                                                     || r.VDesc.Contains(reference, StringComparison.OrdinalIgnoreCase));
            var byVoucherNo = r.VoucherNumber.Length > 0 && sl.Description.Contains(r.VoucherNumber, StringComparison.OrdinalIgnoreCase);
            if (byBankRef) score += 40;
            if (byVoucherNo) score += 40;
            return new MatchCandidate(r.Id, r.VoucherNumber, r.PostingDate, (int)side * r.Amount, score, r.Description, byBankRef || byVoucherNo);
        }).OrderByDescending(c => c.Score).ThenBy(c => c.PostingDate).ToList();
        return result;
    }
}
