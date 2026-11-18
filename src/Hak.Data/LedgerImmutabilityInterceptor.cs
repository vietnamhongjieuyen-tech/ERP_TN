using Hak.Domain.Common;
using Hak.Domain.Ledger;
using Hak.Domain.Rules;
using Hak.Domain.Settlement;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hak.Data;

/// <summary>
/// First layer of ledger immutability (docs/02 ADR-07). The second layer is DB triggers,
/// which also catch writes that bypass EF.
/// </summary>
public sealed class LedgerImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbContext ctx)
    {
        foreach (var entry in ctx.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case JournalEntry or JournalLine or AuditLog or VoucherHistory
                    when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw Immutable($"{entry.Entity.GetType().Name} không được sửa hoặc xóa.");

                case Voucher when entry.State == EntityState.Deleted:
                    throw Immutable("Chứng từ không được xóa vật lý; dùng chức năng Hủy (nháp) hoặc Đảo (đã ghi sổ).");

                case Voucher v when entry.State == EntityState.Modified:
                    CheckPostedVoucher(entry, v);
                    break;

                case VoucherLine line when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                    var status = OriginalVoucherStatus(ctx, line.VoucherId);
                    if (status is not null && status != VoucherStatus.Draft)
                        throw Immutable($"Không được thay đổi dòng của chứng từ đang ở trạng thái {status}.");
                    break;

                case SettlementAllocation when entry.State == EntityState.Deleted:
                    throw Immutable("Phân bổ thanh toán không được xóa; dùng chức năng hủy phân bổ.");

                case SettlementAllocation when entry.State == EntityState.Modified:
                    // Only a single void (VoidedAt/By/Reason set once) is allowed.
                    if (entry.OriginalValues[nameof(SettlementAllocation.VoidedAt)] is not null
                        || entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)
                            && p.Metadata.Name is not (nameof(SettlementAllocation.VoidedAt) or nameof(SettlementAllocation.VoidedBy) or nameof(SettlementAllocation.VoidReason))))
                        throw Immutable("Phân bổ thanh toán chỉ được hủy một lần, không được sửa.");
                    break;

                case Domain.Banking.BankStatement when entry.State is EntityState.Modified or EntityState.Deleted:
                case Domain.Banking.BankStatementLine when entry.State == EntityState.Deleted:
                case Domain.Banking.ReconciliationMatch when entry.State == EntityState.Deleted:
                    throw Immutable("Sao kê đã nhập và kết quả đối chiếu không được sửa hoặc xóa.");

                case Domain.Banking.BankStatementLine when entry.State == EntityState.Modified:
                    if (entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)
                            && p.Metadata.Name is not (nameof(Domain.Banking.BankStatementLine.Status) or nameof(Domain.Banking.BankStatementLine.CandidatesJson))))
                        throw Immutable("Dữ liệu dòng sao kê là chứng cứ gốc, không được sửa.");
                    break;

                case Domain.Banking.ReconciliationMatch when entry.State == EntityState.Modified:
                    if (entry.OriginalValues[nameof(Domain.Banking.ReconciliationMatch.VoidedAt)] is not null)
                        throw Immutable("Kết quả đối chiếu đã hủy, không sửa tiếp.");
                    break;

                case Domain.Inventory.StockMovement or Domain.Inventory.StockValueAdjustment when entry.State is EntityState.Modified or EntityState.Deleted:
                case Domain.Inventory.StockValuation when entry.State == EntityState.Deleted:
                case Domain.Inventory.CostingRun when entry.State == EntityState.Deleted:
                    throw Immutable("Sổ kho (số lượng, giá trị, lần tính giá) không được sửa hoặc xóa.");

                case Domain.Inventory.StockValuation when entry.State == EntityState.Modified:
                    if (entry.OriginalValues[nameof(Domain.Inventory.StockValuation.VoidedAt)] is not null
                        || entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name != nameof(Domain.Inventory.StockValuation.VoidedAt)))
                        throw Immutable("Giá trị kho chỉ được hủy một lần khi hủy lần tính giá, không được sửa.");
                    break;

                case Domain.Inventory.StockMeasurement when entry.State == EntityState.Deleted:
                    throw Immutable("Kết quả đo độ khô không được xóa; dùng chức năng thay thế có ghi lý do.");

                case Domain.Inventory.StockMeasurement when entry.State == EntityState.Modified:
                    if (entry.OriginalValues[nameof(Domain.Inventory.StockMeasurement.SupersededAt)] is not null
                        || entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name is not (
                            nameof(Domain.Inventory.StockMeasurement.SupersededAt) or nameof(Domain.Inventory.StockMeasurement.SupersededBy) or nameof(Domain.Inventory.StockMeasurement.SupersedeReason))))
                        throw Immutable("Kết quả đo độ khô không được sửa; lập kết quả đo mới thay thế.");
                    break;

                case Domain.Trade.CommercialInvoice when entry.State == EntityState.Deleted:
                    throw Immutable("Hóa đơn không được xóa; hủy (nháp) hoặc đảo (đã ghi sổ).");

                case Domain.Trade.CommercialInvoice inv when entry.State == EntityState.Modified
                                                            && (Domain.Trade.InvoiceStatus)entry.OriginalValues[nameof(Domain.Trade.CommercialInvoice.Status)]! != Domain.Trade.InvoiceStatus.Draft:
                    if (entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)
                            && p.Metadata.Name is not (nameof(Domain.Trade.CommercialInvoice.Status) or nameof(Domain.Trade.CommercialInvoice.RowVersion))))
                        throw Immutable($"Hóa đơn {inv.Number} đã ghi sổ, không được sửa.");
                    break;

                case Domain.Trade.CommercialInvoiceLine il when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted:
                {
                    var head = ctx.ChangeTracker.Entries<Domain.Trade.CommercialInvoice>().FirstOrDefault(e => e.Entity.Id == il.InvoiceId);
                    var st = head is null
                        ? ctx.Set<Domain.Trade.CommercialInvoice>().AsNoTracking().Where(x => x.Id == il.InvoiceId).Select(x => (Domain.Trade.InvoiceStatus?)x.Status).FirstOrDefault()
                        : head.State == EntityState.Added ? null : (Domain.Trade.InvoiceStatus)head.OriginalValues[nameof(Domain.Trade.CommercialInvoice.Status)]!;
                    if (st is not null && st != Domain.Trade.InvoiceStatus.Draft)
                        throw Immutable("Không được thay đổi dòng của hóa đơn đã ghi sổ.");
                    break;
                }

                case Domain.Tax.EInvoiceRecord or Domain.Tax.EInvoiceRateLine or Domain.Tax.EInvoiceImportBatch or Domain.Tax.EInvoiceStatusChange
                    when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw Immutable("Dữ liệu hóa đơn điện tử đã nhập là chứng cứ gốc, không được sửa/xóa.");

                case Domain.Tax.TaxDeclaration when entry.State == EntityState.Deleted:
                case Domain.Tax.TaxDeclaration when entry.State == EntityState.Modified
                                                    && (Domain.Tax.DeclarationStatus)entry.OriginalValues[nameof(Domain.Tax.TaxDeclaration.Status)]! == Domain.Tax.DeclarationStatus.Filed:
                    throw Immutable("Tờ khai đã nộp không được sửa/xóa; lập tờ khai bổ sung theo quy định.");

                case Domain.Assets.LongTermAsset when entry.State == EntityState.Deleted:
                case Domain.Assets.AssetCostSource when entry.State is EntityState.Modified or EntityState.Deleted:
                case Domain.Assets.DepreciationRun or Domain.Assets.DepreciationEntry or Domain.Fx.FxRevaluationRun when entry.State == EntityState.Deleted:
                    throw Immutable("Thẻ tài sản, nguồn nguyên giá và số khấu hao đã ghi không được sửa hoặc xóa.");

                case Domain.Assets.LongTermAsset a when entry.State == EntityState.Modified
                                                       && (Domain.Assets.AssetStatus)entry.OriginalValues[nameof(Domain.Assets.LongTermAsset.Status)]! != Domain.Assets.AssetStatus.Draft:
                    if (entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name is not (
                            nameof(Domain.Assets.LongTermAsset.Status) or nameof(Domain.Assets.LongTermAsset.DisposalDate)
                            or nameof(Domain.Assets.LongTermAsset.DisposalVoucherId) or nameof(Domain.Assets.LongTermAsset.RowVersion)
                            or nameof(Domain.Assets.LongTermAsset.UsedBy))))   // nơi sử dụng: informational, no effect on the figures
                        throw Immutable($"Tài sản {a.Code} đã ghi tăng, thông số khấu hao không được sửa.");
                    break;

                case Domain.Assets.DepreciationEntry when entry.State == EntityState.Modified:
                    if (entry.OriginalValues[nameof(Domain.Assets.DepreciationEntry.VoidedAt)] is not null
                        || entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name != nameof(Domain.Assets.DepreciationEntry.VoidedAt)))
                        throw Immutable("Số khấu hao chỉ được hủy một lần khi hủy lần chạy / hủy ghi giảm, không được sửa.");
                    break;

                case Domain.Production.ProductionCostRun when entry.State == EntityState.Deleted:
                    throw Immutable("Lần tính giá thành không được xóa; dùng chức năng hủy.");

                case Domain.Payroll.PayrollSheet or Domain.Payroll.PayrollSheetLine when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw Immutable("Bảng lương đã nhận là chứng cứ gốc, không được sửa hoặc xóa; bảng lương tính lại gửi phiên bản mới.");

                case Domain.Loans.LoanContract or Domain.Loans.LoanInterestRun or Domain.Loans.LoanInterestEntry when entry.State == EntityState.Deleted:
                case Domain.Loans.LoanRate or Domain.Loans.LoanLink when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw Immutable("Khế ước vay, lãi suất, liên kết giải ngân/trả nợ và số lãi đã trích không được sửa hoặc xóa.");

                case Domain.Loans.LoanContract loan when entry.State == EntityState.Modified
                                                         && (Domain.Loans.LoanStatus)entry.OriginalValues[nameof(Domain.Loans.LoanContract.Status)]! != Domain.Loans.LoanStatus.Draft:
                    if (entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)
                            && p.Metadata.Name is not (nameof(Domain.Loans.LoanContract.Status) or nameof(Domain.Loans.LoanContract.RowVersion))))
                        throw Immutable($"Khế ước {loan.Code} đã hiệu lực, không được sửa; lãi suất mới thêm bằng dòng lãi suất có ngày hiệu lực.");
                    break;

                case Domain.Loans.LoanInterestEntry when entry.State == EntityState.Modified:
                    if (entry.OriginalValues[nameof(Domain.Loans.LoanInterestEntry.VoidedAt)] is not null
                        || entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue) && p.Metadata.Name != nameof(Domain.Loans.LoanInterestEntry.VoidedAt)))
                        throw Immutable("Số lãi đã trích chỉ được hủy một lần khi hủy lần trích lãi.");
                    break;

                case PostingRule when entry.State == EntityState.Deleted:
                    throw Immutable("Quy tắc định khoản không được xóa; dùng trạng thái Retired.");

                case PostingRule when entry.State == EntityState.Modified
                                      && (RuleStatus)entry.OriginalValues[nameof(PostingRule.Status)]! != RuleStatus.Draft:
                    if (entry.Properties.Any(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)
                            && p.Metadata.Name is not (nameof(PostingRule.Status) or nameof(PostingRule.RowVersion))))
                        throw Immutable("Quy tắc đã kích hoạt không được sửa; hãy tạo phiên bản mới.");
                    break;
            }
        }
    }

    private static void CheckPostedVoucher(EntityEntry entry, Voucher v)
    {
        var original = (VoucherStatus)entry.OriginalValues[nameof(Voucher.Status)]!;
        if (original is not (VoucherStatus.Posted or VoucherStatus.Reversed)) return;

        var allowed = new HashSet<string> { nameof(Voucher.Status), nameof(Voucher.RowVersion) };
        var changed = entry.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).Select(p => p.Metadata.Name);
        if (changed.Any(n => !allowed.Contains(n)) || !(original == VoucherStatus.Posted && v.Status is VoucherStatus.Posted or VoucherStatus.Reversed))
            throw Immutable($"Chứng từ {v.Number} đã ghi sổ, không được sửa trực tiếp.");
    }

    /// <summary>Status as stored in the database (null when the voucher is being inserted in this unit of work).</summary>
    private static VoucherStatus? OriginalVoucherStatus(DbContext ctx, Guid voucherId)
    {
        var tracked = ctx.ChangeTracker.Entries<Voucher>().FirstOrDefault(e => e.Entity.Id == voucherId);
        if (tracked is not null)
            return tracked.State == EntityState.Added ? null : (VoucherStatus)tracked.OriginalValues[nameof(Voucher.Status)]!;
        return ctx.Set<Voucher>().AsNoTracking().Where(x => x.Id == voucherId).Select(x => (VoucherStatus?)x.Status).FirstOrDefault();
    }

    private static DomainException Immutable(string msg) => new(ErrorCodes.LedgerImmutable, msg);
}
