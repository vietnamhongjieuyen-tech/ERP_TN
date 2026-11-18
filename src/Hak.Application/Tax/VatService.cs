using System.Security.Cryptography;
using System.Text;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Hak.Domain.Tax;
using Hak.Domain.Trade;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Tax;

public sealed record EInvoiceRateInput(decimal? VatRate, decimal AmountBeforeVat, decimal VatAmount);

public sealed record EInvoiceInput(TaxDirection Direction, string SellerTaxCode, string SellerName, string Series, string Number, DateOnly IssueDate,
    decimal AmountBeforeVat, decimal VatAmount, decimal TotalPayment, EInvoiceStatus Status, IReadOnlyList<EInvoiceRateInput> Rates,
    string? BuyerTaxCode = null, string? BuyerName = null, string? Currency = null, decimal? ExchangeRate = null,
    string? RelatedSeries = null, string? RelatedNumber = null, string? LookupCode = null);

public sealed record EInvoiceImportResult(Guid BatchId, int Imported, int Duplicates, int StatusChanges, IReadOnlyList<string> Rejected);

/// <summary>One invoice as recorded in the books (source 2), signed: returns/discounts are negative.</summary>
public sealed record LedgerVatInvoice(Guid InvoiceId, string Number, TaxDirection Direction, string? CounterpartyTaxCode, string CounterpartyName,
    string Series, string InvoiceNo, DateOnly InvoiceDate, DateOnly PostingDate, InvoicePurpose Purpose,
    decimal AmountBeforeVat, decimal VatAmount, IReadOnlyDictionary<string, (decimal Amount, decimal Vat)> ByRate);

public enum TaxDiffType
{
    MissingInBooks = 1,
    MissingEInvoice = 2,
    AmountDifference = 3,
    VatDifference = 4,
    PeriodDifference = 5,
    CancelledButRecorded = 6,
    PartnerTaxCodeMissing = 7,
    DeclarationDifference = 8,
}

public sealed record TaxDifference(TaxDiffType Type, TaxDirection? Direction, string Key, decimal? EInvoiceAmount, decimal? BooksAmount,
    decimal? EInvoiceVat, decimal? BooksVat, decimal Difference, string Suggestion, Guid? InvoiceId = null, Guid? EInvoiceId = null);

public sealed record VatReconciliation(DateOnly From, DateOnly To, int Matched, IReadOnlyList<TaxDifference> Differences)
{
    public bool IsClean => Differences.Count == 0;
}

public sealed record DeclarationLineInput(string IndicatorCode, string Label, decimal DeclaredAmount, TaxMeasure Measure = TaxMeasure.None, decimal? VatRate = null);

public sealed record VatPayableSummary(DateOnly From, DateOnly To, decimal OutputVat, decimal InputVat, decimal NetPayable, string Note);

/// <summary>
/// VAT (master prompt §20): e-invoice registry (source 1), VAT listings from the books (source 2), declared figures
/// (source 3) and the 3-way reconciliation that lists every discrepancy with a suggested action. Nothing here files a
/// return: declarations are records of what the company filed elsewhere.
/// </summary>
public sealed class VatService(HakDbContext db, AuditService audit, IClock clock)
{
    public async Task<EInvoiceImportResult> ImportEInvoicesAsync(SessionContext ctx, string sourceName, string sourceContent, IReadOnlyList<EInvoiceInput> invoices)
    {
        ctx.Require(Permissions.DataImport);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceContent)));
        return await db.InTransactionAsync(async () =>
        {
            if (await db.EInvoiceImportBatches.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.SourceSha256 == sha))
                throw new DomainException(ErrorCodes.PackageReplayed, "File hóa đơn điện tử này đã được nhập.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var own = Norm(company.TaxCode);
            if (own.Length == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Công ty chưa khai báo mã số thuế; không đối chiếu được hóa đơn điện tử.");

            var batch = new EInvoiceImportBatch { Id = Ids.New(), CompanyId = ctx.CompanyId, SourceName = sourceName, SourceSha256 = sha, ImportedAt = clock.UtcNow, ImportedBy = ctx.UserId };
            db.EInvoiceImportBatches.Add(batch);
            var rejected = new List<string>();
            int statusChanges = 0;
            foreach (var x in invoices)
            {
                var key = $"{x.Direction}/{x.SellerTaxCode}/{x.Series}/{x.Number}";
                string? problem = null;
                if (string.IsNullOrWhiteSpace(x.Series) || string.IsNullOrWhiteSpace(x.Number) || string.IsNullOrWhiteSpace(x.SellerTaxCode)) problem = "thiếu MST người bán, ký hiệu hoặc số";
                else if (x.AmountBeforeVat + x.VatAmount != x.TotalPayment) problem = "tiền hàng + thuế ≠ tổng thanh toán";
                else if (x.Rates.Count > 0 && (x.Rates.Sum(r => r.AmountBeforeVat) != x.AmountBeforeVat || x.Rates.Sum(r => r.VatAmount) != x.VatAmount)) problem = "chi tiết theo thuế suất không khớp tổng";
                else if (x.Direction == TaxDirection.Output && Norm(x.SellerTaxCode) != own) problem = "hóa đơn đầu ra nhưng người bán không phải công ty";
                else if (x.Direction == TaxDirection.Input && x.BuyerTaxCode is { } b && Norm(b) != own) problem = "hóa đơn đầu vào nhưng người mua không phải công ty";
                if (problem is not null) { rejected.Add($"{key}: {problem}"); continue; }

                var seller = Norm(x.SellerTaxCode);
                var existing = await db.EInvoiceRecords.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.Direction == x.Direction
                    && r.SellerTaxCode == seller && r.Series == x.Series.Trim() && r.Number == x.Number.Trim())
                    ?? db.EInvoiceRecords.Local.FirstOrDefault(r => r.Direction == x.Direction && r.SellerTaxCode == seller && r.Series == x.Series.Trim() && r.Number == x.Number.Trim());
                if (existing is not null)
                {
                    if (await CurrentStatusAsync(existing) != x.Status)
                    {
                        db.EInvoiceStatusChanges.Add(new EInvoiceStatusChange { Id = Ids.New(), EInvoiceId = existing.Id, NewStatus = x.Status, ImportBatchId = batch.Id, At = clock.UtcNow });
                        statusChanges++;
                    }
                    else batch.Duplicates++;
                    continue;
                }
                var rec = new EInvoiceRecord
                {
                    Id = Ids.New(), CompanyId = ctx.CompanyId, Direction = x.Direction, SellerTaxCode = seller, SellerName = x.SellerName, BuyerTaxCode = x.BuyerTaxCode is null ? null : Norm(x.BuyerTaxCode),
                    BuyerName = x.BuyerName, Series = x.Series.Trim(), Number = x.Number.Trim(), IssueDate = x.IssueDate, Currency = x.Currency, ExchangeRate = x.ExchangeRate,
                    AmountBeforeVat = x.AmountBeforeVat, VatAmount = x.VatAmount, TotalPayment = x.TotalPayment, Status = x.Status, RelatedSeries = x.RelatedSeries,
                    RelatedNumber = x.RelatedNumber, LookupCode = x.LookupCode, ImportBatchId = batch.Id, ImportedAt = clock.UtcNow, ImportedBy = ctx.UserId,
                };
                rec.RateLines = (x.Rates.Count > 0 ? x.Rates : [new EInvoiceRateInput(null, x.AmountBeforeVat, x.VatAmount)])
                    .Select(r => new EInvoiceRateLine { Id = Ids.New(), EInvoiceId = rec.Id, VatRate = r.VatRate, AmountBeforeVat = r.AmountBeforeVat, VatAmount = r.VatAmount }).ToList();
                db.EInvoiceRecords.Add(rec);
                batch.Imported++;
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "einvoice.import", "einvoice_batch", batch.Id.ToString(),
                new { sourceName, sha, batch.Imported, batch.Duplicates, statusChanges, rejected = rejected.Count });
            await db.SaveChangesAsync();
            return new EInvoiceImportResult(batch.Id, batch.Imported, batch.Duplicates, statusChanges, rejected);
        });
    }

    /// <summary>VAT listing from posted invoices whose posting date falls in the period (bảng kê từ sổ).</summary>
    public async Task<IReadOnlyList<LedgerVatInvoice>> LedgerListingAsync(SessionContext ctx, TaxDirection direction, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        return (await LedgerInvoicesAsync(ctx.CompanyId, from, to)).Where(x => x.Direction == direction).ToList();
    }

    /// <summary>
    /// 3-way reconciliation for a period: e-invoices issued in the period ↔ invoices posted in the books (matched on seller tax
    /// code + series + number) ↔ the declaration covering exactly this period, if recorded.
    /// </summary>
    public async Task<VatReconciliation> ReconcileAsync(SessionContext ctx, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
        var own = Norm(company.TaxCode);
        var einvoices = await db.EInvoiceRecords.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).ToListAsync();
        var statuses = new Dictionary<Guid, EInvoiceStatus>();
        foreach (var e in einvoices) statuses[e.Id] = await CurrentStatusAsync(e);

        // Books: everything posted up to "to" so an invoice booked in a later/earlier period is reported as a period difference, not as missing.
        var books = await LedgerInvoicesAsync(ctx.CompanyId, DateOnly.MinValue, DateOnly.MaxValue);
        string Key(TaxDirection d, string? seller, string series, string no) => $"{d}|{Norm(seller)}|{series.Trim()}|{no.Trim()}";
        var bookByKey = books.GroupBy(b => Key(b.Direction, b.Direction == TaxDirection.Output ? own : b.CounterpartyTaxCode, b.Series, b.InvoiceNo))
            .ToDictionary(g => g.Key, g => g.ToList());

        var diffs = new List<TaxDifference>();
        var matched = 0;
        var seenBookIds = new HashSet<Guid>();
        foreach (var e in einvoices.Where(e => e.IssueDate >= from && e.IssueDate <= to))
        {
            var key = Key(e.Direction, e.SellerTaxCode, e.Series, e.Number);
            var label = $"{(e.Direction == TaxDirection.Input ? "Đầu vào" : "Đầu ra")} {e.SellerTaxCode} {e.Series}/{e.Number}";
            var st = statuses[e.Id];
            bookByKey.TryGetValue(key, out var recs);
            var normal = recs?.Where(r => r.Purpose == InvoicePurpose.Normal).ToList() ?? [];
            foreach (var r in normal) seenBookIds.Add(r.InvoiceId);

            if (st is EInvoiceStatus.Cancelled or EInvoiceStatus.Replaced)
            {
                if (normal.Count > 0)
                    diffs.Add(new(TaxDiffType.CancelledButRecorded, e.Direction, label, e.AmountBeforeVat, normal.Sum(n => n.AmountBeforeVat), e.VatAmount, normal.Sum(n => n.VatAmount),
                        normal.Sum(n => n.VatAmount), $"Hóa đơn đã {(st == EInvoiceStatus.Cancelled ? "bị hủy" : "bị thay thế")} trên hệ thống HĐĐT nhưng vẫn ghi sổ: đảo hóa đơn trong sổ và ghi nhận hóa đơn thay thế (nếu có).",
                        normal[0].InvoiceId, e.Id));
                continue;
            }
            if (normal.Count == 0)
            {
                diffs.Add(new(TaxDiffType.MissingInBooks, e.Direction, label, e.AmountBeforeVat, null, e.VatAmount, null, e.VatAmount,
                    e.Direction == TaxDirection.Input ? "Có HĐĐT đầu vào nhưng chưa ghi sổ: kiểm tra hàng/dịch vụ đã nhận, ghi nhận nếu hợp lệ hoặc liên hệ người bán nếu không đúng."
                                                      : "Có HĐĐT đầu ra nhưng chưa ghi doanh thu: ghi nhận hóa đơn bán hoặc kiểm tra hóa đơn xuất nhầm.", EInvoiceId: e.Id));
                continue;
            }
            var b = normal[0];
            matched++;
            if (b.AmountBeforeVat != e.AmountBeforeVat)
                diffs.Add(new(TaxDiffType.AmountDifference, e.Direction, label, e.AmountBeforeVat, b.AmountBeforeVat, e.VatAmount, b.VatAmount, b.AmountBeforeVat - e.AmountBeforeVat,
                    "Tiền hàng trong sổ khác HĐĐT: kiểm tra đơn giá/số lượng/tỷ giá; điều chỉnh sổ hoặc yêu cầu hóa đơn điều chỉnh.", b.InvoiceId, e.Id));
            if (b.VatAmount != e.VatAmount)
                diffs.Add(new(TaxDiffType.VatDifference, e.Direction, label, e.AmountBeforeVat, b.AmountBeforeVat, e.VatAmount, b.VatAmount, b.VatAmount - e.VatAmount,
                    "Thuế GTGT trong sổ khác HĐĐT: kiểm tra thuế suất từng dòng.", b.InvoiceId, e.Id));
            if (b.PostingDate < from || b.PostingDate > to)
                diffs.Add(new(TaxDiffType.PeriodDifference, e.Direction, label, e.AmountBeforeVat, b.AmountBeforeVat, e.VatAmount, b.VatAmount, 0,
                    $"Hóa đơn lập ngày {e.IssueDate:dd/MM/yyyy} nhưng hạch toán ngày {b.PostingDate:dd/MM/yyyy}: xác định đúng kỳ kê khai [CẦN XÁC MINH quy định kê khai bổ sung].", b.InvoiceId, e.Id));
        }

        foreach (var b in books.Where(b => b.Purpose == InvoicePurpose.Normal && b.PostingDate >= from && b.PostingDate <= to && !seenBookIds.Contains(b.InvoiceId)))
        {
            var label = $"{(b.Direction == TaxDirection.Input ? "Đầu vào" : "Đầu ra")} {b.Number} {b.Series}/{b.InvoiceNo}";
            if (b.Direction == TaxDirection.Input && string.IsNullOrWhiteSpace(b.CounterpartyTaxCode))
            {
                diffs.Add(new(TaxDiffType.PartnerTaxCodeMissing, b.Direction, label, null, b.AmountBeforeVat, null, b.VatAmount, b.VatAmount,
                    $"Nhà cung cấp {b.CounterpartyName} chưa có mã số thuế trong danh mục: bổ sung để đối chiếu.", b.InvoiceId));
                continue;
            }
            var key = Key(b.Direction, b.Direction == TaxDirection.Output ? own : b.CounterpartyTaxCode, b.Series, b.InvoiceNo);
            if (einvoices.Any(e => Key(e.Direction, e.SellerTaxCode, e.Series, e.Number) == key)) continue; // issued in another period: reported above as period difference
            diffs.Add(new(TaxDiffType.MissingEInvoice, b.Direction, label, null, b.AmountBeforeVat, null, b.VatAmount, b.VatAmount,
                b.Direction == TaxDirection.Input ? "Sổ có hóa đơn đầu vào nhưng không có trong dữ liệu HĐĐT: tra cứu hóa đơn trên hệ thống; không khấu trừ khi chưa xác thực."
                                                  : "Sổ có doanh thu nhưng không có HĐĐT tương ứng: kiểm tra việc xuất hóa đơn.", b.InvoiceId));
        }

        var declaration = await db.TaxDeclarations.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.CompanyId == ctx.CompanyId && d.TaxType == "GTGT" && d.PeriodFrom == from && d.PeriodTo == to);
        if (declaration is not null)
        {
            var inPeriod = books.Where(b => b.PostingDate >= from && b.PostingDate <= to).ToList();
            foreach (var l in declaration.Lines.Where(l => l.Measure != TaxMeasure.None))
            {
                var computed = Measure(inPeriod, l.Measure, l.VatRate);
                if (computed != l.DeclaredAmount)
                    diffs.Add(new(TaxDiffType.DeclarationDifference, null, $"Chỉ tiêu {l.IndicatorCode} – {l.Label}", null, computed, null, null, computed - l.DeclaredAmount,
                        "Số đã kê khai khác số trên sổ: kiểm tra kỳ kê khai, hóa đơn bổ sung; lập tờ khai bổ sung nếu cần [CẦN XÁC MINH]."));
            }
        }
        return new VatReconciliation(from, to, matched, diffs.OrderBy(d => d.Type).ThenBy(d => d.Key, StringComparer.Ordinal).ToList());
    }

    public async Task<TaxDeclaration> SaveDeclarationAsync(SessionContext ctx, DateOnly from, DateOnly to, IReadOnlyList<DeclarationLineInput> lines)
    {
        ctx.Require(Permissions.ConfigManage);
        if (to < from || lines.Count == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Kỳ hoặc chỉ tiêu tờ khai không hợp lệ.");
        if (lines.GroupBy(l => l.IndicatorCode).Any(g => g.Count() > 1)) throw new DomainException(ErrorCodes.ValidationFailed, "Mã chỉ tiêu bị trùng.");
        return await db.InTransactionAsync(async () =>
        {
            var d = await db.TaxDeclarations.Include(x => x.Lines).FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.TaxType == "GTGT" && x.PeriodFrom == from && x.PeriodTo == to);
            if (d is null)
            {
                d = new TaxDeclaration { Id = Ids.New(), CompanyId = ctx.CompanyId, PeriodFrom = from, PeriodTo = to, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
                db.TaxDeclarations.Add(d);
            }
            else if (d.Status == DeclarationStatus.Filed) throw new DomainException(ErrorCodes.LedgerImmutable, "Tờ khai đã đánh dấu đã nộp; không sửa.");
            else db.TaxDeclarationLines.RemoveRange(d.Lines);
            d.Lines = lines.Select(l => new TaxDeclarationLine { Id = Ids.New(), DeclarationId = d.Id, IndicatorCode = l.IndicatorCode.Trim(), Label = l.Label, DeclaredAmount = l.DeclaredAmount, Measure = l.Measure, VatRate = l.VatRate }).ToList();
            db.TaxDeclarationLines.AddRange(d.Lines);
            d.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "tax_declaration.save", "tax_declaration", d.Id.ToString(), new { from, to, lines });
            await db.SaveChangesAsync();
            return d;
        });
    }

    /// <summary>Records that the declaration was filed (outside this system) and freezes it.</summary>
    public async Task MarkFiledAsync(SessionContext ctx, Guid declarationId, string filingReference, DateOnly filedDate)
    {
        ctx.Require(Permissions.ConfigManage);
        if (string.IsNullOrWhiteSpace(filingReference)) throw new DomainException(ErrorCodes.ValidationFailed, "Phải nhập mã giao dịch/số tham chiếu nộp tờ khai.");
        await db.InTransactionAsync(async () =>
        {
            var d = await db.TaxDeclarations.FirstOrDefaultAsync(x => x.Id == declarationId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Tờ khai không tồn tại.");
            if (d.Status == DeclarationStatus.Filed) throw new DomainException(ErrorCodes.InvalidStateTransition, "Tờ khai đã đánh dấu đã nộp.");
            d.Status = DeclarationStatus.Filed;
            d.FilingReference = filingReference;
            d.FiledDate = filedDate;
            d.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "tax_declaration.filed", "tax_declaration", d.Id.ToString(), new { filingReference, filedDate });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>Draft VAT position from the VAT accounts in the books (not an official computation).</summary>
    public async Task<VatPayableSummary> PayableSummaryAsync(SessionContext ctx, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        var roles = await db.AccountRoleMappings.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && (m.RoleCode == "VAT_INPUT" || m.RoleCode == "VAT_OUTPUT"))
            .ToDictionaryAsync(m => m.RoleCode, m => m.AccountId);
        async Task<decimal> Movement(string role, Domain.Vouchers.Side side)
        {
            if (!roles.TryGetValue(role, out var acc)) return 0;
            var sums = await db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == acc && l.PostingDate >= from && l.PostingDate <= to)
                .GroupBy(l => l.Side).Select(g => new { g.Key, T = g.Sum(x => x.Amount) }).ToListAsync();
            return sums.Where(s => s.Key == side).Sum(s => s.T) - sums.Where(s => s.Key != side).Sum(s => s.T);
        }
        var output = await Movement("VAT_OUTPUT", Domain.Vouchers.Side.Credit);
        var input = await Movement("VAT_INPUT", Domain.Vouchers.Side.Debit);
        return new VatPayableSummary(from, to, output, input, output - input,
            "Số liệu dự thảo từ sổ (phát sinh TK thuế GTGT trong kỳ); chưa tính số thuế còn được khấu trừ kỳ trước và các điều chỉnh. Không phải tờ khai chính thức.");
    }

    // ------------------------------------------------------------------

    private async Task<List<LedgerVatInvoice>> LedgerInvoicesAsync(Guid companyId, DateOnly fromDate, DateOnly toDate)
    {
        var rows = await (from i in db.CommercialInvoices
                          join p in db.BusinessPartners on i.PartnerId equals p.Id
                          where i.CompanyId == companyId && i.Status == InvoiceStatus.Posted && i.PostingDate >= fromDate && i.PostingDate <= toDate
                          select new { i, p.TaxCode, p.Name }).AsNoTracking().ToListAsync();
        var ids = rows.Select(r => r.i.Id).ToList();
        var lines = await db.CommercialInvoiceLines.AsNoTracking().Where(l => ids.Contains(l.InvoiceId)).ToListAsync();
        return rows.Select(r =>
        {
            var sign = r.i.Purpose is InvoicePurpose.Return or InvoicePurpose.Discount ? -1 : 1;
            var ls = lines.Where(l => l.InvoiceId == r.i.Id).ToList();
            var byRate = ls.GroupBy(l => l.VatRate is { } v ? v.ToString("0.####") : "KCT")
                .ToDictionary(g => g.Key, g => (sign * g.Sum(x => x.FunctionalAmount), sign * g.Sum(x => x.VatAmount)));
            return new LedgerVatInvoice(r.i.Id, r.i.Number, r.i.Kind == InvoiceKind.Purchase ? TaxDirection.Input : TaxDirection.Output,
                r.TaxCode is null ? null : Norm(r.TaxCode), r.Name, r.i.InvoiceSeries, r.i.InvoiceNo, r.i.InvoiceDate, r.i.PostingDate, r.i.Purpose,
                sign * ls.Sum(x => x.FunctionalAmount), sign * ls.Sum(x => x.VatAmount), byRate);
        }).OrderBy(x => x.PostingDate).ThenBy(x => x.Number).ToList();
    }

    private static decimal Measure(IEnumerable<LedgerVatInvoice> books, TaxMeasure m, decimal? rate)
    {
        var dir = m is TaxMeasure.InputAmountBeforeVat or TaxMeasure.InputVat ? TaxDirection.Input : TaxDirection.Output;
        var key = rate is { } r ? r.ToString("0.####") : null;
        decimal total = 0;
        foreach (var b in books.Where(b => b.Direction == dir))
            foreach (var (k, v) in b.ByRate.Where(kv => key is null || kv.Key == key))
                total += m is TaxMeasure.InputVat or TaxMeasure.OutputVat ? v.Vat : v.Amount;
        return total;
    }

    private async Task<EInvoiceStatus> CurrentStatusAsync(EInvoiceRecord e)
        => await db.EInvoiceStatusChanges.AsNoTracking().Where(s => s.EInvoiceId == e.Id).OrderByDescending(s => s.At).Select(s => (EInvoiceStatus?)s.NewStatus).FirstOrDefaultAsync()
           ?? db.EInvoiceStatusChanges.Local.Where(s => s.EInvoiceId == e.Id).OrderByDescending(s => s.At).Select(s => (EInvoiceStatus?)s.NewStatus).FirstOrDefault()
           ?? e.Status;

    private static string Norm(string? taxCode) => (taxCode ?? "").Replace(" ", "").Trim();
}
