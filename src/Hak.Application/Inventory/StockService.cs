using System.Text.Json;
using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Inventory;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Inventory;

public sealed record StockLineInput(Guid ItemId, decimal Quantity, decimal? Amount = null, Guid? CounterAccountId = null, string? Description = null,
    Guid? OrderId = null, int? OrderLineNo = null, Guid? AnalysisCodeId = null);

public sealed record StockDocumentInput(StockDocumentType Type, Guid WarehouseId, DateOnly DocumentDate, DateOnly PostingDate, string Description,
    IReadOnlyList<StockLineInput> Lines, Guid? ToWarehouseId = null, Guid? PartnerId = null);

public sealed record InventorySummaryRow(Guid WarehouseId, string WarehouseCode, Guid ItemId, string ItemCode, string ItemName, string Unit,
    decimal OpeningQty, decimal OpeningValue, decimal InQty, decimal InValue, decimal OutQty, decimal OutValue,
    decimal ClosingQty, decimal ClosingValue, decimal PendingOutQty);

public sealed record StockCardRow(DateOnly Date, string DocumentNumber, string? Description, decimal InQty, decimal OutQty, decimal? Value,
    decimal BalanceQty, decimal? BalanceValue);

public sealed record InventoryReconcileRow(Guid AccountId, string AccountCode, decimal StockValue, decimal LedgerBalance, decimal Difference, decimal PendingIssueQty);

/// <summary>
/// Inventory (master prompt §15): stock documents, the immutable quantity ledger, issue costing by the warehouse's
/// method (FIFO / moving average at posting time; monthly average by a month-end run) and the GL entries.
/// Stock documents are posted in strict date order per warehouse/item, so FIFO layers and running averages are
/// never rewritten after the fact; a correction is a reversal or a new document.
/// </summary>
public sealed class StockService(HakDbContext db, AccountingEngine engine, AuditService audit, IClock clock)
{
    public const string StockSource = "HAK.STOCK";
    public const string CostingSource = "HAK.COSTING";

    // ------------------------------------------------------------------ catalog & policy

    public async Task<Warehouse> CreateWarehouseAsync(SessionContext ctx, string code, string name)
    {
        ctx.Require(Permissions.CatalogManage);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) throw new DomainException(ErrorCodes.ValidationFailed, "Nhập mã và tên kho.");
        code = code.Trim(); name = name.Trim();
        return await db.InTransactionAsync(async () =>
        {
            if (await db.Warehouses.AnyAsync(w => w.CompanyId == ctx.CompanyId && w.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã kho {code} đã tồn tại.");
            var w = new Warehouse { Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Name = name, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            db.Warehouses.Add(w);
            await audit.AppendAsync(ctx.CompanyId, ctx, "warehouse.create", "warehouse", w.Id.ToString(), new { code, name });
            await db.SaveChangesAsync();
            return w;
        });
    }

    public async Task<Item> CreateItemAsync(SessionContext ctx, string code, string name, string unit, string inventoryAccountCode)
    {
        ctx.Require(Permissions.CatalogManage);
        return await db.InTransactionAsync(async () =>
        {
            if (await db.Items.AnyAsync(i => i.CompanyId == ctx.CompanyId && i.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã vật tư {code} đã tồn tại.");
            var acc = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId && a.Code == inventoryAccountCode)
                      ?? throw new DomainException(ErrorCodes.AccountNotFound, $"Tài khoản {inventoryAccountCode} không tồn tại.");
            if (!acc.IsPostable || acc.CashKind != CashKind.None || acc.OpenItemNature != OpenItemNature.None || acc.RequiresPartner)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Tài khoản {acc.Code} không dùng làm tài khoản hàng tồn kho (phải là TK chi tiết, không phải tiền/công nợ).");
            var item = new Item { Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Name = name, Unit = unit, InventoryAccountId = acc.Id, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            db.Items.Add(item);
            await audit.AppendAsync(ctx.CompanyId, ctx, "item.create", "item", item.Id.ToString(), new { code, name, unit, inventoryAccountCode });
            await db.SaveChangesAsync();
            return item;
        });
    }

    /// <summary>
    /// Sets the warehouse costing method from <paramref name="effectiveFrom"/>, which must be the first day of a fiscal
    /// year with no stock movement on or after it (consistency principle: no retroactive change of method).
    /// </summary>
    public async Task<CostingPolicy> SetCostingPolicyAsync(SessionContext ctx, Guid warehouseId, CostingMethod method, DateOnly effectiveFrom, string note)
    {
        ctx.Require(Permissions.ConfigManage);
        if (string.IsNullOrWhiteSpace(note)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi căn cứ/quyết định áp dụng phương pháp tính giá.");
        return await db.InTransactionAsync(async () =>
        {
            await LoadWarehouseAsync(ctx, warehouseId);
            if (!await db.FiscalYears.AnyAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate == effectiveFrom))
                throw new DomainException(ErrorCodes.ValidationFailed, "Phương pháp tính giá chỉ được áp dụng/thay đổi từ ngày đầu một năm tài chính.");
            if (await db.StockMovements.AnyAsync(m => m.CompanyId == ctx.CompanyId && m.WarehouseId == warehouseId && m.MovementDate >= effectiveFrom))
                throw new DomainException(ErrorCodes.ValidationFailed, "Kho đã có nghiệp vụ từ ngày hiệu lực; không được đổi phương pháp hồi tố.");
            if (await db.CostingPolicies.AnyAsync(p => p.CompanyId == ctx.CompanyId && p.WarehouseId == warehouseId && p.EffectiveFrom == effectiveFrom))
                throw new DomainException(ErrorCodes.DuplicateCode, "Đã có phương pháp tính giá cho kho từ ngày này.");
            var p = new CostingPolicy { Id = Ids.New(), CompanyId = ctx.CompanyId, WarehouseId = warehouseId, Method = method, EffectiveFrom = effectiveFrom, Note = note, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            db.CostingPolicies.Add(p);
            await audit.AppendAsync(ctx.CompanyId, ctx, "costing_policy.set", "warehouse", warehouseId.ToString(), new { method, effectiveFrom, note });
            await db.SaveChangesAsync();
            return p;
        });
    }

    public async Task<CostingMethod> MethodForAsync(Guid companyId, Guid warehouseId, DateOnly date)
        => (await db.CostingPolicies.AsNoTracking().Where(p => p.CompanyId == companyId && p.WarehouseId == warehouseId && p.EffectiveFrom <= date)
               .OrderByDescending(p => p.EffectiveFrom).FirstOrDefaultAsync())?.Method
           ?? throw new DomainException(ErrorCodes.CostingPolicyMissing, $"Kho chưa được thiết lập phương pháp tính giá xuất kho tại ngày {date:dd/MM/yyyy}.");

    // ------------------------------------------------------------------ documents

    public async Task<StockDocument> CreateDocumentAsync(SessionContext ctx, StockDocumentInput input)
    {
        ctx.Require(Permissions.StockDocument);
        if (input.Lines.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Phiếu kho chưa có dòng.");
        if (string.IsNullOrWhiteSpace(input.Description)) throw new DomainException(ErrorCodes.ValidationFailed, "Diễn giải bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            await LoadWarehouseAsync(ctx, input.WarehouseId);
            if (input.Type == StockDocumentType.Transfer)
            {
                if (input.ToWarehouseId is not { } to || to == input.WarehouseId)
                    throw new DomainException(ErrorCodes.ValidationFailed, "Phiếu điều chuyển phải có kho nhận khác kho xuất.");
                await LoadWarehouseAsync(ctx, to);
            }
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var dp = await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();
            var itemIds = input.Lines.Select(l => l.ItemId).Distinct().ToList();
            var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
            foreach (var (l, n) in input.Lines.Select((l, i) => (l, i + 1)))
            {
                if (!items.TryGetValue(l.ItemId, out var item) || !item.IsActive)
                    throw new DomainException(ErrorCodes.NotFound, $"Dòng {n}: vật tư không tồn tại hoặc ngừng sử dụng.");
                if (l.Quantity <= 0 || !MoneyMath.HasAtMostDecimals(l.Quantity, MoneyMath.StorageScale))
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: số lượng phải lớn hơn 0, tối đa 4 chữ số thập phân.");
                if (input.Type == StockDocumentType.Receipt && (l.Amount is not { } a || a < 0 || !MoneyMath.HasAtMostDecimals(a, dp)))
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: phiếu nhập phải có giá trị ≥ 0, tối đa {dp} chữ số thập phân.");
                if (input.Type != StockDocumentType.Receipt && l.Amount is not null)
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: giá trị xuất kho do hệ thống tính theo phương pháp của kho, không nhập tay.");
                if (input.Type != StockDocumentType.Transfer)
                {
                    var counter = l.CounterAccountId is { } cid ? await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == cid && a.CompanyId == ctx.CompanyId) : null;
                    if (counter is null || !counter.IsPostable || !counter.IsActive)
                        throw new DomainException(ErrorCodes.AccountNotFound, $"Dòng {n}: thiếu tài khoản đối ứng hợp lệ.");
                    if (counter.RequiresPartner && input.PartnerId is null)
                        throw new DomainException(ErrorCodes.PartnerRequired, $"Dòng {n}: tài khoản đối ứng {counter.Code} yêu cầu đối tượng.");
                }
                if ((l.OrderId is null) != (l.OrderLineNo is null))
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Dòng {n}: liên kết đơn hàng phải có cả đơn và dòng đơn.");
                if (l.OrderId is { } oid)
                {
                    var earlier = input.Lines.Take(n - 1).Where(x => x.OrderId == oid && x.OrderLineNo == l.OrderLineNo).Sum(x => x.Quantity);
                    await Trade.OrderService.CheckStockAgainstOrderAsync(db, ctx.CompanyId, input.Type, input.PartnerId, oid, l.OrderLineNo!.Value, l.ItemId, l.Quantity, null, earlier);
                }
            }

            var fy = await db.FiscalYears.AsNoTracking().FirstOrDefaultAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= input.PostingDate && f.EndDate >= input.PostingDate)
                     ?? throw new DomainException(ErrorCodes.FiscalYearNotFound, $"Không có năm tài chính chứa ngày {input.PostingDate:dd/MM/yyyy}.");
            var prefix = input.Type switch { StockDocumentType.Receipt => "PN", StockDocumentType.Issue => "PX", _ => "DC" };
            var doc = new StockDocument
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Number = await NextNumberAsync(ctx.CompanyId, fy, prefix), Type = input.Type,
                WarehouseId = input.WarehouseId, ToWarehouseId = input.ToWarehouseId, DocumentDate = input.DocumentDate, PostingDate = input.PostingDate,
                Description = input.Description.Trim(), PartnerId = input.PartnerId, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
            };
            doc.Lines = input.Lines.Select((l, i) => new StockDocumentLine
            {
                Id = Ids.New(), DocumentId = doc.Id, CompanyId = ctx.CompanyId, LineNo = i + 1, ItemId = l.ItemId, Quantity = l.Quantity,
                Amount = l.Amount, CounterAccountId = l.CounterAccountId, Description = l.Description, OrderId = l.OrderId, OrderLineNo = l.OrderLineNo, AnalysisCodeId = l.AnalysisCodeId,
            }).ToList();
            db.StockDocuments.Add(doc);
            await audit.AppendAsync(ctx.CompanyId, ctx, "stock.create", "stock_document", doc.Id.ToString(), new { doc.Number, doc.Type, lines = doc.Lines.Count });
            await db.SaveChangesAsync();
            return doc;
        });
    }

    /// <summary>
    /// Posts a stock document: quantity ledger + valuation (FIFO / moving average immediately, monthly average later)
    /// + the GL voucher for whatever value is known now. One transaction; any failure leaves the document a Draft.
    /// </summary>
    public async Task<StockDocument> PostDocumentAsync(SessionContext ctx, Guid documentId)
    {
        ctx.Require(Permissions.VoucherPost);
        return await db.InTransactionAsync(async () =>
        {
            var doc = await db.StockDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Phiếu kho không tồn tại.");
            if (doc.Status != StockDocumentStatus.Draft)
                throw new DomainException(doc.Status == StockDocumentStatus.Posted ? ErrorCodes.AlreadyPosted : ErrorCodes.InvalidStateTransition, $"Phiếu {doc.Number} ở trạng thái {doc.Status}.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            if (company.RequireDistinctApprover && doc.CreatedBy == ctx.UserId)
                throw new DomainException(ErrorCodes.SodViolation, "Người ghi sổ phiếu kho không được trùng người lập.");
            await RequireOpenPeriodAsync(ctx.CompanyId, doc.PostingDate);
            foreach (var ol in doc.Lines.Where(l => l.OrderId is not null))
            {
                var order = await db.TradeOrders.AsNoTracking().FirstAsync(o => o.Id == ol.OrderId);
                if (order.Status != Domain.Trade.OrderStatus.Confirmed)
                    throw new DomainException(ErrorCodes.OrderMismatch, $"Đơn {order.Number} ở trạng thái {order.Status}; không ghi sổ phiếu theo đơn này.");
            }
            var dp = await DecimalsAsync(company);
            var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
            var seq = await MaxSequenceAsync(ctx.CompanyId);

            var glLines = new List<VoucherLineInput>();
            foreach (var line in doc.Lines.OrderBy(l => l.LineNo))
            {
                var item = items[line.ItemId];
                switch (doc.Type)
                {
                    case StockDocumentType.Receipt:
                        await GuardOrderAsync(ctx.CompanyId, doc.WarehouseId, item.Id, doc.PostingDate);
                        await AddMovementAsync(doc, line, doc.WarehouseId, line.Quantity, line.Amount!.Value, ++seq, line.CounterAccountId, doc.PartnerId);
                        AddGl(glLines, item.InventoryAccountId, line.CounterAccountId!.Value, doc.PartnerId, line.Amount.Value, line.Description, counterCode: line.AnalysisCodeId);
                        break;

                    case StockDocumentType.Issue:
                    {
                        var method = await GuardOrderAsync(ctx.CompanyId, doc.WarehouseId, item.Id, doc.PostingDate);
                        var value = await IssueValueAsync(ctx.CompanyId, doc.WarehouseId, item.Id, line.Quantity, method, dp);
                        await AddMovementAsync(doc, line, doc.WarehouseId, -line.Quantity, value is null ? null : -value.Value, ++seq, line.CounterAccountId, doc.PartnerId);
                        if (value is { } v) AddGl(glLines, line.CounterAccountId!.Value, item.InventoryAccountId, doc.PartnerId, v, line.Description, debitPartner: true, counterCode: line.AnalysisCodeId);
                        break;
                    }

                    case StockDocumentType.Transfer:
                    {
                        var method = await GuardOrderAsync(ctx.CompanyId, doc.WarehouseId, item.Id, doc.PostingDate);
                        await GuardOrderAsync(ctx.CompanyId, doc.ToWarehouseId!.Value, item.Id, doc.PostingDate);
                        if (method == CostingMethod.MonthlyAverage)
                            throw new DomainException(ErrorCodes.ValidationFailed,
                                "Chưa hỗ trợ điều chuyển từ kho tính giá bình quân cuối tháng (giá xuất chỉ có khi tính giá cuối kỳ).");
                        var value = (await IssueValueAsync(ctx.CompanyId, doc.WarehouseId, item.Id, line.Quantity, method, dp))!.Value;
                        await AddMovementAsync(doc, line, doc.WarehouseId, -line.Quantity, -value, ++seq, null, null);
                        await AddMovementAsync(doc, line, doc.ToWarehouseId!.Value, line.Quantity, value, ++seq, null, null);
                        // Same item ⇒ same inventory account on both sides: no GL entry needed.
                        break;
                    }
                }
            }

            if (glLines.Count > 0)
            {
                var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(
                    doc.Type == StockDocumentType.Receipt ? VoucherTypes.InventoryReceipt : VoucherTypes.InventoryIssue,
                    doc.DocumentDate, doc.PostingDate, $"{doc.Description} ({doc.Number})", glLines, SourceSystem: StockSource, SourceId: doc.Id.ToString()));
                doc.VoucherId = v.Id;
            }
            doc.Status = StockDocumentStatus.Posted;
            doc.PostedBy = ctx.UserId;
            doc.PostedAt = clock.UtcNow;
            doc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "stock.post", "stock_document", doc.Id.ToString(), new { doc.Number, doc.VoucherId }, doc.RowVersion);
            await db.SaveChangesAsync();
            return doc;
        });
    }

    /// <summary>
    /// Reverses a posted stock document with mirror movements and the reversal of its GL voucher. Refused when the
    /// item has later movements in the warehouse (their FIFO/average cost depended on this document) or when the
    /// month has been costed.
    /// </summary>
    public async Task<StockDocument> ReverseDocumentAsync(SessionContext ctx, Guid documentId, DateOnly reversalDate, string reason)
    {
        ctx.Require(Permissions.VoucherReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do đảo phiếu kho.");
        return await db.InTransactionAsync(async () =>
        {
            var doc = await db.StockDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Phiếu kho không tồn tại.");
            if (doc.Status != StockDocumentStatus.Posted) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Phiếu {doc.Number} chưa ghi sổ hoặc đã đảo.");
            if (reversalDate < doc.PostingDate) throw new DomainException(ErrorCodes.ValidationFailed, "Ngày đảo không được trước ngày ghi sổ phiếu.");
            await RequireOpenPeriodAsync(ctx.CompanyId, reversalDate);

            var movements = await db.StockMovements.Where(m => m.CompanyId == ctx.CompanyId && m.DocumentId == doc.Id).OrderBy(m => m.Sequence).ToListAsync();
            foreach (var m in movements)
            {
                if (await db.StockMovements.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.WarehouseId == m.WarehouseId && x.ItemId == m.ItemId
                                                          && x.Sequence > m.Sequence && x.DocumentId != doc.Id)
                    || await db.StockValueAdjustments.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.WarehouseId == m.WarehouseId && a.ItemId == m.ItemId && a.Sequence > m.Sequence))
                    throw new DomainException(ErrorCodes.StockBackdated,
                        $"Vật tư trong phiếu {doc.Number} đã có nghiệp vụ kho phát sinh sau; không đảo được (giá xuất các phiếu sau phụ thuộc phiếu này). Lập phiếu điều chỉnh.");
                await GuardCostedAsync(ctx.CompanyId, m.WarehouseId, m.MovementDate);
                await GuardCostedAsync(ctx.CompanyId, m.WarehouseId, reversalDate);
            }
            var values = await ActiveValuesAsync(movements.Select(m => m.Id).ToList());

            var fy = await db.FiscalYears.AsNoTracking().FirstAsync(f => f.CompanyId == ctx.CompanyId && f.StartDate <= reversalDate && f.EndDate >= reversalDate);
            var rev = new StockDocument
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Number = await NextNumberAsync(ctx.CompanyId, fy, "DP"), Type = doc.Type, WarehouseId = doc.WarehouseId,
                ToWarehouseId = doc.ToWarehouseId, DocumentDate = reversalDate, PostingDate = reversalDate, Description = $"Đảo phiếu {doc.Number}: {reason.Trim()}",
                PartnerId = doc.PartnerId, Status = StockDocumentStatus.Posted, ReversesDocumentId = doc.Id, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
                PostedBy = ctx.UserId, PostedAt = clock.UtcNow, RowVersion = 1,
            };
            db.StockDocuments.Add(rev);
            var seq = await MaxSequenceAsync(ctx.CompanyId);
            foreach (var m in movements)
            {
                var mirror = new StockMovement
                {
                    Id = Ids.New(), CompanyId = ctx.CompanyId, WarehouseId = m.WarehouseId, ItemId = m.ItemId, DocumentId = rev.Id, LineNo = m.LineNo,
                    MovementDate = reversalDate, Sequence = ++seq, Quantity = -m.Quantity, CounterAccountId = m.CounterAccountId, PartnerId = m.PartnerId,
                    ReversesMovementId = m.Id,
                };
                db.StockMovements.Add(mirror);
                if (values.TryGetValue(m.Id, out var val))
                    db.StockValuations.Add(new StockValuation { Id = Ids.New(), CompanyId = ctx.CompanyId, MovementId = mirror.Id, Value = -val, CreatedAt = clock.UtcNow });
            }
            if (doc.VoucherId is { } vid)
                rev.ReversalVoucherId = (await engine.ReverseCoreAsync(ctx, vid, reversalDate, $"Đảo phiếu kho {doc.Number}: {reason}", allowModuleOwned: true)).Id;
            doc.Status = StockDocumentStatus.Reversed;
            doc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "stock.reverse", "stock_document", doc.Id.ToString(), new { doc.Number, reversal = rev.Number, reason }, doc.RowVersion);
            await db.SaveChangesAsync();
            return rev;
        });
    }

    /// <summary>
    /// Purchase-invoice hook: values a Draft receipt line from the invoice (BDMT × price) and points it at the
    /// goods-received-not-invoiced account, so stock value and invoice value are identical by construction.
    /// </summary>
    internal async Task SetDraftReceiptValueAsync(Guid companyId, Guid documentId, int lineNo, decimal amount, Guid counterAccountId)
    {
        var doc = await db.StockDocuments.Include(d => d.Lines).FirstAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc.Status != StockDocumentStatus.Draft || doc.Type != StockDocumentType.Receipt)
            throw new DomainException(ErrorCodes.InvalidStateTransition, $"Phiếu {doc.Number} không còn là phiếu nhập nháp.");
        var line = doc.Lines.Single(l => l.LineNo == lineNo);
        line.Amount = amount;
        line.CounterAccountId = counterAccountId;
        doc.RowVersion++;
        await db.SaveChangesAsync();
    }

    /// <summary>Cancels a Draft stock document (frees its order quantity). Refused while a draft invoice still references it.</summary>
    public async Task<StockDocument> CancelDocumentAsync(SessionContext ctx, Guid documentId, string reason)
    {
        ctx.Require(Permissions.StockDocument);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy phiếu.");
        return await db.InTransactionAsync(async () =>
        {
            var doc = await db.StockDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Phiếu kho không tồn tại.");
            if (doc.Status != StockDocumentStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ hủy được phiếu kho nháp; phiếu đã ghi sổ thì đảo.");
            if (await (from l in db.CommercialInvoiceLines join i in db.CommercialInvoices on l.InvoiceId equals i.Id
                       where l.StockDocumentId == doc.Id && i.Status == Domain.Trade.InvoiceStatus.Draft select l.Id).AnyAsync())
                throw new DomainException(ErrorCodes.InvalidStateTransition, $"Phiếu {doc.Number} đang gắn với hóa đơn nháp; hủy hóa đơn trước.");
            doc.Status = StockDocumentStatus.Cancelled;
            doc.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "stock.cancel", "stock_document", doc.Id.ToString(), new { doc.Number, reason }, doc.RowVersion);
            await db.SaveChangesAsync();
            return doc;
        });
    }

    /// <summary>How a purchase price difference on a posted receipt line splits between stock still on hand and goods already issued.</summary>
    public sealed record PriceDifferenceSplit(Guid ReceiptMovementId, decimal ReceiptQuantity, decimal QuantityInStock, decimal InStockPart, decimal IssuedPart, CostingMethod Method);

    /// <summary>
    /// Splits <paramref name="difference"/> (invoice value − receipt value) by the quantity of this receipt still in stock:
    /// FIFO uses the receipt's own remaining layer; average methods use min(receipt qty, quantity on hand) since the receipt
    /// has merged into the pool. Read-only: used for the warning shown before posting and by the posting itself.
    /// </summary>
    public async Task<PriceDifferenceSplit> PriceDifferenceSplitAsync(Guid companyId, Guid receiptDocumentId, int lineNo, decimal difference)
    {
        var mv = await db.StockMovements.AsNoTracking().SingleAsync(m => m.CompanyId == companyId && m.DocumentId == receiptDocumentId && m.LineNo == lineNo && m.Quantity > 0);
        var receiptDate = mv.MovementDate;
        var method = await MethodForAsync(companyId, mv.WarehouseId, receiptDate);
        var history = await db.StockMovements.AsNoTracking().Where(m => m.CompanyId == companyId && m.WarehouseId == mv.WarehouseId && m.ItemId == mv.ItemId)
            .OrderBy(m => m.Sequence).ToListAsync();
        var values = await ActiveValuesAsync(history.Select(m => m.Id).ToList());
        decimal inStock;
        if (method == CostingMethod.Fifo)
            inStock = CostingCalculator.FifoRemaining(await CostedHistoryAsync(companyId, mv.WarehouseId, mv.ItemId, history, values), mv.Sequence,
                await DecimalsAsync(await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId)));
        else
            inStock = Math.Max(0, Math.Min(mv.Quantity, history.Sum(m => m.Quantity)));
        var dp = await DecimalsAsync(await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId));
        var part = inStock == mv.Quantity ? difference : MoneyMath.Round(difference * inStock / mv.Quantity, dp);
        return new PriceDifferenceSplit(mv.Id, mv.Quantity, inStock, part, difference - part, method);
    }

    /// <summary>
    /// Writes the inventory part of a purchase price difference as a value-only adjustment of the receipt layer, dated
    /// <paramref name="date"/>. Called inside the invoice-posting transaction; the GL entry is booked by the invoice.
    /// </summary>
    internal async Task<PriceDifferenceSplit> AdjustReceiptValueAsync(SessionContext ctx, Guid receiptDocumentId, int lineNo, decimal difference, DateOnly date,
        Guid sourceInvoiceId, string reason)
    {
        var split = await PriceDifferenceSplitAsync(ctx.CompanyId, receiptDocumentId, lineNo, difference);
        if (split.InStockPart == 0) return split;
        var mv = await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == split.ReceiptMovementId);
        await GuardCostedAsync(ctx.CompanyId, mv.WarehouseId, date);
        await RequireOpenPeriodAsync(ctx.CompanyId, date);
        db.StockValueAdjustments.Add(new StockValueAdjustment
        {
            Id = Ids.New(), CompanyId = ctx.CompanyId, WarehouseId = mv.WarehouseId, ItemId = mv.ItemId, TargetMovementId = mv.Id,
            Sequence = await MaxSequenceAsync(ctx.CompanyId) + 1, AdjustmentDate = date, Value = split.InStockPart, SourceInvoiceId = sourceInvoiceId,
            Reason = reason, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
        });
        return split;
    }

    /// <summary>Offsets every value-only adjustment booked by an invoice that is being reversed (inside the reversal transaction).</summary>
    internal async Task ReverseValueAdjustmentsAsync(SessionContext ctx, Guid sourceInvoiceId, DateOnly date, string reason)
    {
        var originals = await db.StockValueAdjustments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.SourceInvoiceId == sourceInvoiceId).ToListAsync();
        if (originals.Count == 0 || originals.Sum(a => a.Value) == 0) return;
        await RequireOpenPeriodAsync(ctx.CompanyId, date);
        var seq = await MaxSequenceAsync(ctx.CompanyId);
        foreach (var g in originals.GroupBy(a => (a.WarehouseId, a.ItemId, a.TargetMovementId)))
        {
            var value = -g.Sum(a => a.Value);
            if (value == 0) continue;
            await GuardCostedAsync(ctx.CompanyId, g.Key.WarehouseId, date);
            db.StockValueAdjustments.Add(new StockValueAdjustment
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, WarehouseId = g.Key.WarehouseId, ItemId = g.Key.ItemId, TargetMovementId = g.Key.TargetMovementId,
                Sequence = ++seq, AdjustmentDate = date, Value = value, SourceInvoiceId = sourceInvoiceId, Reason = reason, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            });
        }
    }

    /// <summary>Highest sequence used by movements or value adjustments (they share one processing order per company).</summary>
    private async Task<long> MaxSequenceAsync(Guid companyId)
    {
        var m = await db.StockMovements.Where(x => x.CompanyId == companyId).MaxAsync(x => (long?)x.Sequence) ?? 0;
        var a = await db.StockValueAdjustments.Where(x => x.CompanyId == companyId).MaxAsync(x => (long?)x.Sequence) ?? 0;
        var local = db.StockValueAdjustments.Local.Select(x => x.Sequence).DefaultIfEmpty(0).Max();
        return Math.Max(Math.Max(m, a), local);
    }

    /// <summary>Movement history merged with value-only adjustments, in processing order, for the costing calculators.</summary>
    private async Task<List<CostedMovement>> CostedHistoryAsync(Guid companyId, Guid warehouseId, Guid itemId, IReadOnlyList<StockMovement> history,
        IReadOnlyDictionary<Guid, decimal> values)
    {
        var seqById = history.ToDictionary(m => m.Id, m => m.Sequence);
        var adjs = await db.StockValueAdjustments.AsNoTracking().Where(a => a.CompanyId == companyId && a.WarehouseId == warehouseId && a.ItemId == itemId).ToListAsync();
        return history.Select(m => new CostedMovement(m.Sequence, m.Quantity, values.TryGetValue(m.Id, out var v) ? v : null))
            .Concat(adjs.Select(a => new CostedMovement(a.Sequence, 0, a.Value, seqById.GetValueOrDefault(a.TargetMovementId))))
            .OrderBy(x => x.Sequence).ToList();
    }

    // ------------------------------------------------------------------ monthly average costing

    /// <summary>
    /// Values every pending issue of a monthly-average warehouse for one period and posts one IC voucher
    /// (Dr counter accounts / Cr inventory). Periods must be costed in order; a costed period refuses new postings.
    /// </summary>
    public async Task<CostingRun> RunMonthlyCostingAsync(SessionContext ctx, Guid warehouseId, Guid periodId)
    {
        ctx.Require(Permissions.VoucherPost);
        return await db.InTransactionAsync(async () =>
        {
            await LoadWarehouseAsync(ctx, warehouseId);
            var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == ctx.CompanyId)
                         ?? throw new DomainException(ErrorCodes.PeriodNotFound, "Kỳ kế toán không tồn tại.");
            if (period.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, "Kỳ đã khóa sổ.");
            if (await MethodForAsync(ctx.CompanyId, warehouseId, period.EndDate) != CostingMethod.MonthlyAverage
                || await MethodForAsync(ctx.CompanyId, warehouseId, period.StartDate) != CostingMethod.MonthlyAverage)
                throw new DomainException(ErrorCodes.ValidationFailed, "Kho không áp dụng bình quân cuối tháng trong kỳ này.");
            if (await db.CostingRuns.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.WarehouseId == warehouseId && r.PeriodId == periodId && r.Status == CostingRunStatus.Active))
                throw new DomainException(ErrorCodes.PeriodCosted, "Kỳ này đã được tính giá; hủy lần tính giá trước khi tính lại.");

            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            var dp = await DecimalsAsync(company);
            var all = await db.StockMovements.AsNoTracking()
                .Where(m => m.CompanyId == ctx.CompanyId && m.WarehouseId == warehouseId && m.MovementDate <= period.EndDate).OrderBy(m => m.Sequence).ToListAsync();
            var values = await ActiveValuesAsync(all.Select(m => m.Id).ToList());
            if (all.Any(m => m.MovementDate < period.StartDate && !values.ContainsKey(m.Id)))
                throw new DomainException(ErrorCodes.CostingPending, "Còn nghiệp vụ xuất kho của kỳ trước chưa tính giá; tính giá các kỳ trước theo thứ tự.");
            var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
            var adjustments = await db.StockValueAdjustments.AsNoTracking()
                .Where(a => a.CompanyId == ctx.CompanyId && a.WarehouseId == warehouseId && a.AdjustmentDate <= period.EndDate).ToListAsync();

            // A pending issue reversed within the same month cancels out and is not valued.
            var inPeriod = all.Where(m => m.MovementDate >= period.StartDate).ToList();
            var cancelled = inPeriod.Where(m => m.ReversesMovementId is not null && !values.ContainsKey(m.Id)).SelectMany(m => new[] { m.Id, m.ReversesMovementId!.Value }).ToHashSet();

            var run = new CostingRun
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, WarehouseId = warehouseId, PeriodId = periodId, PeriodStart = period.StartDate,
                PeriodEnd = period.EndDate, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            };
            var glLines = new List<VoucherLineInput>();
            var result = new List<object>();
            // Mã phân tích of the stock lines whose issues are valued by this run (the movement itself does not carry it).
            var periodDocs = inPeriod.Select(m => m.DocumentId).Distinct().ToList();
            var lineCodes = (await db.StockDocumentLines.AsNoTracking().Where(l => periodDocs.Contains(l.DocumentId) && l.AnalysisCodeId != null)
                .Select(l => new { l.DocumentId, l.LineNo, l.AnalysisCodeId }).ToListAsync()).ToDictionary(l => (l.DocumentId, l.LineNo), l => l.AnalysisCodeId);
            foreach (var g in all.GroupBy(m => m.ItemId))
            {
                var opening = g.Where(m => m.MovementDate < period.StartDate).ToList();
                var valued = g.Where(m => m.MovementDate >= period.StartDate && values.ContainsKey(m.Id)).ToList();
                var pending = g.Where(m => m.MovementDate >= period.StartDate && !values.ContainsKey(m.Id) && !cancelled.Contains(m.Id)).ToList();
                if (pending.Count == 0) continue;
                if (pending.Any(m => m.Quantity > 0))
                    throw new DomainException(ErrorCodes.CostingPending, "Có nghiệp vụ nhập chưa có giá trị; kiểm tra dữ liệu kho.");

                // Purchase price adjustments are value-only: before the period they are part of the opening value, inside it of the receipts.
                var adjBefore = adjustments.Where(a => a.ItemId == g.Key && a.AdjustmentDate < period.StartDate).Sum(a => a.Value);
                var adjInside = adjustments.Where(a => a.ItemId == g.Key && a.AdjustmentDate >= period.StartDate).Sum(a => a.Value);
                var issueValues = CostingCalculator.MonthlyAverage(opening.Sum(m => m.Quantity), opening.Sum(m => values[m.Id]) + adjBefore,
                    valued.Sum(m => m.Quantity), valued.Sum(m => values[m.Id]) + adjInside, pending.Select(m => -m.Quantity).ToList(), dp);
                var item = items[g.Key];
                for (var i = 0; i < pending.Count; i++)
                {
                    db.StockValuations.Add(new StockValuation { Id = Ids.New(), CompanyId = ctx.CompanyId, MovementId = pending[i].Id, Value = -issueValues[i], CostingRunId = run.Id, CreatedAt = clock.UtcNow });
                    if (issueValues[i] > 0)
                        AddGl(glLines, pending[i].CounterAccountId!.Value, item.InventoryAccountId, pending[i].PartnerId, issueValues[i], $"Giá vốn xuất {item.Code}", debitPartner: true,
                            counterCode: lineCodes.GetValueOrDefault((pending[i].DocumentId, pending[i].LineNo)));
                }
                var totalQty = opening.Sum(m => m.Quantity) + valued.Sum(m => m.Quantity);
                var totalValue = opening.Sum(m => values[m.Id]) + valued.Sum(m => values[m.Id]) + adjBefore + adjInside;
                result.Add(new { item = item.Code, totalQty, totalValue, issuedQty = -pending.Sum(m => m.Quantity), issuedValue = issueValues.Sum() });
            }
            if (result.Count == 0) throw new DomainException(ErrorCodes.VoucherNoLines, "Không có phiếu xuất nào chờ tính giá trong kỳ.");

            db.CostingRuns.Add(run);
            run.ResultJson = JsonSerializer.Serialize(result);
            if (glLines.Count > 0)
            {
                var wh = await db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == warehouseId);
                var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(VoucherTypes.InventoryCosting, period.EndDate, period.EndDate,
                    $"Tính giá xuất kho bình quân tháng {period.StartDate:MM/yyyy} kho {wh.Code}", glLines, SourceSystem: CostingSource, SourceId: run.Id.ToString()));
                run.VoucherId = v.Id;
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "costing.run", "costing_run", run.Id.ToString(), new { warehouseId, period = period.StartDate, result });
            await db.SaveChangesAsync();
            return run;
        });
    }

    public async Task ReverseCostingRunAsync(SessionContext ctx, Guid runId, string reason)
    {
        ctx.Require(Permissions.VoucherReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy lần tính giá.");
        await db.InTransactionAsync(async () =>
        {
            var run = await db.CostingRuns.FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Lần tính giá không tồn tại.");
            if (run.Status != CostingRunStatus.Active) throw new DomainException(ErrorCodes.InvalidStateTransition, "Lần tính giá đã bị hủy.");
            if (await db.CostingRuns.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.WarehouseId == run.WarehouseId && r.Status == CostingRunStatus.Active && r.PeriodStart > run.PeriodStart))
                throw new DomainException(ErrorCodes.PeriodCosted, "Các kỳ sau đã tính giá; hủy từ kỳ gần nhất trở về trước.");
            await RequireOpenPeriodAsync(ctx.CompanyId, run.PeriodEnd);
            foreach (var v in await db.StockValuations.Where(v => v.CostingRunId == run.Id && v.VoidedAt == null).ToListAsync())
                v.VoidedAt = clock.UtcNow;
            if (run.VoucherId is { } vid) await engine.ReverseCoreAsync(ctx, vid, run.PeriodEnd, $"Hủy tính giá: {reason}", allowModuleOwned: true);
            run.Status = CostingRunStatus.Reversed;
            run.ReversedAt = clock.UtcNow;
            run.ReversedBy = ctx.UserId;
            await audit.AppendAsync(ctx.CompanyId, ctx, "costing.reverse", "costing_run", run.Id.ToString(), new { reason });
            await db.SaveChangesAsync();
        });
    }

    // ------------------------------------------------------------------ reports

    /// <summary>Nhập – xuất – tồn per warehouse/item. Issues not yet costed are counted in PendingOutQty and carry no value yet.</summary>
    public async Task<IReadOnlyList<InventorySummaryRow>> SummaryAsync(SessionContext ctx, DateOnly from, DateOnly to, Guid? warehouseId = null)
    {
        ctx.Require(Permissions.ReportView);
        var q = db.StockMovements.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.MovementDate <= to);
        if (warehouseId is not null) q = q.Where(m => m.WarehouseId == warehouseId);
        var moves = await q.ToListAsync();
        var values = await ActiveValuesAsync(moves.Select(m => m.Id).ToList());
        var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
        var whs = await db.Warehouses.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).ToDictionaryAsync(w => w.Id);
        var adjQ = db.StockValueAdjustments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AdjustmentDate <= to);
        if (warehouseId is not null) adjQ = adjQ.Where(a => a.WarehouseId == warehouseId);
        var adjs = await adjQ.ToListAsync();
        return moves.GroupBy(m => (m.WarehouseId, m.ItemId)).Select(g =>
        {
            decimal V(StockMovement m) => values.GetValueOrDefault(m.Id);
            var before = g.Where(m => m.MovementDate < from).ToList();
            var inside = g.Where(m => m.MovementDate >= from).ToList();
            var ins = inside.Where(m => m.Quantity > 0).ToList();
            var outs = inside.Where(m => m.Quantity < 0).ToList();
            var it = items[g.Key.ItemId];
            var adj = adjs.Where(a => a.WarehouseId == g.Key.WarehouseId && a.ItemId == g.Key.ItemId).ToList();
            var oq = before.Sum(m => m.Quantity); var ov = before.Sum(V) + adj.Where(a => a.AdjustmentDate < from).Sum(a => a.Value);
            var iq = ins.Sum(m => m.Quantity); var iv = ins.Sum(V) + adj.Where(a => a.AdjustmentDate >= from).Sum(a => a.Value);
            var xq = -outs.Sum(m => m.Quantity); var xv = -outs.Sum(V);
            return new InventorySummaryRow(g.Key.WarehouseId, whs[g.Key.WarehouseId].Code, it.Id, it.Code, it.Name, it.Unit, oq, ov, iq, iv, xq, xv,
                oq + iq - xq, ov + iv - xv, -g.Where(m => !values.ContainsKey(m.Id) && m.Quantity < 0).Sum(m => m.Quantity));
        }).OrderBy(r => r.WarehouseCode, StringComparer.Ordinal).ThenBy(r => r.ItemCode, StringComparer.Ordinal).ToList();
    }

    /// <summary>Thẻ kho: movements with running quantity; running value is shown only while every movement so far is valued.</summary>
    public async Task<IReadOnlyList<StockCardRow>> StockCardAsync(SessionContext ctx, Guid warehouseId, Guid itemId, DateOnly from, DateOnly to)
    {
        ctx.Require(Permissions.ReportView);
        var moves = await (from m in db.StockMovements
                           join d in db.StockDocuments on m.DocumentId equals d.Id
                           where m.CompanyId == ctx.CompanyId && m.WarehouseId == warehouseId && m.ItemId == itemId && m.MovementDate <= to
                           orderby m.Sequence
                           select new { m, d.Number, d.Description }).AsNoTracking().ToListAsync();
        var values = await ActiveValuesAsync(moves.Select(x => x.m.Id).ToList());
        var adjs = await db.StockValueAdjustments.AsNoTracking()
            .Where(a => a.CompanyId == ctx.CompanyId && a.WarehouseId == warehouseId && a.ItemId == itemId && a.AdjustmentDate <= to).ToListAsync();
        decimal qty = 0; decimal? val = 0;
        var rows = new List<StockCardRow>();
        var events = moves.Select((x, i) => (Seq: x.m.Sequence, Index: i, Adj: (StockValueAdjustment?)null))
            .Concat(adjs.Select(a => (Seq: a.Sequence, Index: -1, Adj: (StockValueAdjustment?)a))).OrderBy(e => e.Seq).ToList();
        foreach (var e in events)
        {
            if (e.Adj is { } a)
            {
                val = val is null ? null : val + a.Value;
                if (a.AdjustmentDate >= from) rows.Add(new StockCardRow(a.AdjustmentDate, "ĐCGT", a.Reason, 0, 0, a.Value, qty, val));
                continue;
            }
            var x = moves[e.Index];
            qty += x.m.Quantity;
            var has = values.TryGetValue(x.m.Id, out var v);
            val = val is null || !has ? null : val + v;
            if (x.m.MovementDate >= from)
                rows.Add(new StockCardRow(x.m.MovementDate, x.Number, x.Description, x.m.Quantity > 0 ? x.m.Quantity : 0, x.m.Quantity < 0 ? -x.m.Quantity : 0,
                    has ? Math.Abs(v) : null, qty, val));
        }
        return rows;
    }

    /// <summary>Inventory subledger (Σ valuations) vs GL balance of each inventory account.</summary>
    public async Task<IReadOnlyList<InventoryReconcileRow>> ReconcileAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var items = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId).ToDictionaryAsync(i => i.Id);
        var moves = await db.StockMovements.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.MovementDate <= asOf).ToListAsync();
        var values = await ActiveValuesAsync(moves.Select(m => m.Id).ToList());
        var adjs = await db.StockValueAdjustments.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.AdjustmentDate <= asOf).ToListAsync();
        var rows = new List<InventoryReconcileRow>();
        foreach (var accId in items.Values.Select(i => i.InventoryAccountId).Distinct())
        {
            var acc = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accId);
            var accMoves = moves.Where(m => items[m.ItemId].InventoryAccountId == accId).ToList();
            var stock = accMoves.Sum(m => values.GetValueOrDefault(m.Id)) + adjs.Where(a => items[a.ItemId].InventoryAccountId == accId).Sum(a => a.Value);
            var ledger = (await db.JournalLines.Where(l => l.CompanyId == ctx.CompanyId && l.AccountId == accId && l.PostingDate <= asOf)
                .GroupBy(l => l.Side).Select(g => new { g.Key, T = g.Sum(x => x.Amount) }).ToListAsync()).Sum(x => (int)x.Key * x.T);
            rows.Add(new InventoryReconcileRow(accId, acc.Code, stock, ledger, stock - ledger, -accMoves.Where(m => m.Quantity < 0 && !values.ContainsKey(m.Id)).Sum(m => m.Quantity)));
        }
        return rows;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Documents must be posted in date order per warehouse/item (no backdating before a later movement) and never
    /// into a month already costed. Returns the warehouse's costing method on that date.
    /// </summary>
    private async Task<CostingMethod> GuardOrderAsync(Guid companyId, Guid warehouseId, Guid itemId, DateOnly date)
    {
        var method = await MethodForAsync(companyId, warehouseId, date);
        var last = await db.StockMovements.Where(m => m.CompanyId == companyId && m.WarehouseId == warehouseId && m.ItemId == itemId)
            .MaxAsync(m => (DateOnly?)m.MovementDate);
        var lastAdj = await db.StockValueAdjustments.Where(a => a.CompanyId == companyId && a.WarehouseId == warehouseId && a.ItemId == itemId)
            .MaxAsync(a => (DateOnly?)a.AdjustmentDate);
        if (lastAdj is { } la && (last is null || la > last)) last = la;
        var local = db.StockMovements.Local.Where(m => m.WarehouseId == warehouseId && m.ItemId == itemId).Select(m => (DateOnly?)m.MovementDate).Max();
        if ((last is { } l && l > date) || (local is { } lc && lc > date))
            throw new DomainException(ErrorCodes.StockBackdated,
                $"Kho đã có nghiệp vụ của vật tư này sau ngày {date:dd/MM/yyyy}; không ghi sổ lùi ngày để không làm sai giá xuất đã tính.");
        await GuardCostedAsync(companyId, warehouseId, date);
        return method;
    }

    private async Task GuardCostedAsync(Guid companyId, Guid warehouseId, DateOnly date)
    {
        if (await db.CostingRuns.AnyAsync(r => r.CompanyId == companyId && r.WarehouseId == warehouseId && r.Status == CostingRunStatus.Active && r.PeriodEnd >= date))
            throw new DomainException(ErrorCodes.PeriodCosted, $"Kho đã tính giá bình quân cho kỳ chứa hoặc sau ngày {date:dd/MM/yyyy}; hủy lần tính giá trước.");
    }

    private async Task<decimal?> IssueValueAsync(Guid companyId, Guid warehouseId, Guid itemId, decimal qty, CostingMethod method, int dp)
    {
        var stored = await db.StockMovements.AsNoTracking().Where(m => m.CompanyId == companyId && m.WarehouseId == warehouseId && m.ItemId == itemId)
            .OrderBy(m => m.Sequence).ToListAsync();
        var history = stored.Concat(db.StockMovements.Local.Where(m => m.WarehouseId == warehouseId && m.ItemId == itemId && db.Entry(m).State == EntityState.Added))
            .OrderBy(m => m.Sequence).ToList();
        var values = await ActiveValuesAsync(history.Select(m => m.Id).ToList());

        if (method == CostingMethod.MonthlyAverage)
        {
            var onHand = history.Sum(m => m.Quantity);
            if (qty > onHand) throw new DomainException(ErrorCodes.InsufficientStock, $"Không đủ tồn kho: cần {qty}, hiện có {onHand}.");
            return null; // valued by the month-end run
        }
        var costed = await CostedHistoryAsync(companyId, warehouseId, itemId, history, values);
        return method == CostingMethod.Fifo
            ? CostingCalculator.Fifo(costed, qty, dp).Value
            : CostingCalculator.MovingAverage(costed, qty, dp).Value;
    }

    private async Task AddMovementAsync(StockDocument doc, StockDocumentLine line, Guid warehouseId, decimal qty, decimal? value, long seq, Guid? counter, Guid? partner)
    {
        var m = new StockMovement
        {
            Id = Ids.New(), CompanyId = doc.CompanyId, WarehouseId = warehouseId, ItemId = line.ItemId, DocumentId = doc.Id, LineNo = line.LineNo,
            MovementDate = doc.PostingDate, Sequence = seq, Quantity = qty, CounterAccountId = counter, PartnerId = partner,
        };
        db.StockMovements.Add(m);
        if (value is { } v)
            db.StockValuations.Add(new StockValuation { Id = Ids.New(), CompanyId = doc.CompanyId, MovementId = m.Id, Value = v, CreatedAt = clock.UtcNow });
        await Task.CompletedTask;
    }

    /// <summary>Active valuation per movement, merging rows pending in this unit of work.</summary>
    private async Task<Dictionary<Guid, decimal>> ActiveValuesAsync(IReadOnlyCollection<Guid> movementIds)
    {
        var result = new Dictionary<Guid, decimal>();
        foreach (var chunk in movementIds.Chunk(500))
            foreach (var v in await db.StockValuations.AsNoTracking().Where(v => chunk.Contains(v.MovementId) && v.VoidedAt == null).ToListAsync())
                result[v.MovementId] = v.Value;
        foreach (var e in db.ChangeTracker.Entries<StockValuation>())
        {
            if (!movementIds.Contains(e.Entity.MovementId)) continue;
            if (e.Entity.VoidedAt is null) result[e.Entity.MovementId] = e.Entity.Value;
            else result.Remove(e.Entity.MovementId);
        }
        return result;
    }

    /// <summary>Adds one Dr/Cr pair; the partner goes on whichever side's account requires it.</summary>
    /// <remarks>counterCode (mã phân tích) goes on the counter-account line — the side that carries the partner — never on the inventory account.</remarks>
    private static void AddGl(List<VoucherLineInput> lines, Guid debitAccount, Guid creditAccount, Guid? partner, decimal amount, string? description, bool debitPartner = false,
        Guid? counterCode = null)
    {
        if (amount == 0) return;
        lines.Add(new VoucherLineInput(debitAccount, Side.Debit, amount, debitPartner ? partner : null, Description: description, AnalysisCodeId: debitPartner ? counterCode : null));
        lines.Add(new VoucherLineInput(creditAccount, Side.Credit, amount, debitPartner ? null : partner, Description: description, AnalysisCodeId: debitPartner ? null : counterCode));
    }

    private async Task<int> DecimalsAsync(Company company)
        => await db.Currencies.Where(c => c.Code == company.FunctionalCurrency).Select(c => c.DecimalPlaces).FirstAsync();

    private async Task<Warehouse> LoadWarehouseAsync(SessionContext ctx, Guid id)
        => await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.CompanyId == ctx.CompanyId && w.IsActive)
           ?? throw new DomainException(ErrorCodes.NotFound, "Kho không tồn tại hoặc ngừng sử dụng.");

    private async Task RequireOpenPeriodAsync(Guid companyId, DateOnly date)
    {
        var p = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.StartDate <= date && x.EndDate >= date)
                ?? throw new DomainException(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {date:dd/MM/yyyy}.");
        if (p.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, $"Kỳ {p.StartDate:MM/yyyy} đã khóa sổ.");
    }

    private async Task<string> NextNumberAsync(Guid companyId, FiscalYear fy, string prefix)
    {
        var key = "STK-" + prefix;
        var seq = await db.VoucherNumberSequences.FirstOrDefaultAsync(s => s.CompanyId == companyId && s.FiscalYearId == fy.Id && s.VoucherType == key);
        if (seq is null) db.VoucherNumberSequences.Add(seq = new VoucherNumberSequence { Id = Ids.New(), CompanyId = companyId, FiscalYearId = fy.Id, VoucherType = key });
        seq.LastNumber++;
        seq.RowVersion++;
        return $"{prefix}{fy.Code}-{seq.LastNumber:D6}";
    }
}
