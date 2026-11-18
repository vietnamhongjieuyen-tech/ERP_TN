using System.Text;
using Hak.Domain.Assets;
using Hak.Domain.Banking;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Fx;
using Hak.Domain.Integration;
using Hak.Domain.Inventory;
using Hak.Domain.Ledger;
using Hak.Domain.Loans;
using Hak.Domain.Organization;
using Hak.Domain.Payroll;
using Hak.Domain.Production;
using Hak.Domain.Regime;
using Hak.Domain.Rules;
using Hak.Domain.Settlement;
using Hak.Domain.Tax;
using Hak.Domain.Trade;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hak.Data;

public sealed class HakDbContext(DbContextOptions<HakDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<AccountingRegime> AccountingRegimes => Set<AccountingRegime>();
    public DbSet<AccountingRegimeVersion> AccountingRegimeVersions => Set<AccountingRegimeVersion>();
    public DbSet<LegalSource> LegalSources => Set<LegalSource>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<BusinessPartner> BusinessPartners => Set<BusinessPartner>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherLine> VoucherLines => Set<VoucherLine>();
    public DbSet<VoucherHistory> VoucherHistory => Set<VoucherHistory>();
    public DbSet<VoucherNumberSequence> VoucherNumberSequences => Set<VoucherNumberSequence>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserCompanyRole> UserCompanyRoles => Set<UserCompanyRole>();
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ExchangePartnerKey> ExchangePartnerKeys => Set<ExchangePartnerKey>();
    public DbSet<SettlementAllocation> SettlementAllocations => Set<SettlementAllocation>();
    public DbSet<AccountRoleMapping> AccountRoleMappings => Set<AccountRoleMapping>();
    public DbSet<PostingRule> PostingRules => Set<PostingRule>();
    public DbSet<ClosingRuleSet> ClosingRuleSets => Set<ClosingRuleSet>();
    public DbSet<ClosingStep> ClosingSteps => Set<ClosingStep>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<BankStatement> BankStatements => Set<BankStatement>();
    public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
    public DbSet<ReconciliationMatch> ReconciliationMatches => Set<ReconciliationMatch>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<CostingPolicy> CostingPolicies => Set<CostingPolicy>();
    public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
    public DbSet<StockDocumentLine> StockDocumentLines => Set<StockDocumentLine>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockValuation> StockValuations => Set<StockValuation>();
    public DbSet<CostingRun> CostingRuns => Set<CostingRun>();
    public DbSet<StockValueAdjustment> StockValueAdjustments => Set<StockValueAdjustment>();
    public DbSet<DrynessMethod> DrynessMethods => Set<DrynessMethod>();
    public DbSet<StockMeasurement> StockMeasurements => Set<StockMeasurement>();
    public DbSet<CommercialInvoice> CommercialInvoices => Set<CommercialInvoice>();
    public DbSet<CommercialInvoiceLine> CommercialInvoiceLines => Set<CommercialInvoiceLine>();
    public DbSet<TradeOrder> TradeOrders => Set<TradeOrder>();
    public DbSet<TradeOrderLine> TradeOrderLines => Set<TradeOrderLine>();
    public DbSet<EInvoiceRecord> EInvoiceRecords => Set<EInvoiceRecord>();
    public DbSet<EInvoiceRateLine> EInvoiceRateLines => Set<EInvoiceRateLine>();
    public DbSet<EInvoiceImportBatch> EInvoiceImportBatches => Set<EInvoiceImportBatch>();
    public DbSet<EInvoiceStatusChange> EInvoiceStatusChanges => Set<EInvoiceStatusChange>();
    public DbSet<TaxDeclaration> TaxDeclarations => Set<TaxDeclaration>();
    public DbSet<TaxDeclarationLine> TaxDeclarationLines => Set<TaxDeclarationLine>();
    public DbSet<LongTermAsset> LongTermAssets => Set<LongTermAsset>();
    public DbSet<AssetCostSource> AssetCostSources => Set<AssetCostSource>();
    public DbSet<DepreciationRun> DepreciationRuns => Set<DepreciationRun>();
    public DbSet<DepreciationEntry> DepreciationEntries => Set<DepreciationEntry>();
    public DbSet<FxRevaluationRun> FxRevaluationRuns => Set<FxRevaluationRun>();
    public DbSet<LoanContract> LoanContracts => Set<LoanContract>();
    public DbSet<LoanRate> LoanRates => Set<LoanRate>();
    public DbSet<LoanLink> LoanLinks => Set<LoanLink>();
    public DbSet<LoanInterestRun> LoanInterestRuns => Set<LoanInterestRun>();
    public DbSet<LoanInterestEntry> LoanInterestEntries => Set<LoanInterestEntry>();
    public DbSet<PayrollSheet> PayrollSheets => Set<PayrollSheet>();
    public DbSet<PayrollSheetLine> PayrollSheetLines => Set<PayrollSheetLine>();
    public DbSet<CostObject> CostObjects => Set<CostObject>();
    public DbSet<VoucherTemplate> VoucherTemplates => Set<VoucherTemplate>();
    public DbSet<AnalysisCode> AnalysisCodes => Set<AnalysisCode>();
    public DbSet<VoucherAttachment> VoucherAttachments => Set<VoucherAttachment>();
    public DbSet<Hak.Domain.Security.IdempotencyRecord> IdempotencyRecords => Set<Hak.Domain.Security.IdempotencyRecord>();
    public DbSet<ProductionCostRun> ProductionCostRuns => Set<ProductionCostRun>();
    public DbSet<Hak.Domain.Reports.ReportDefinition> ReportDefinitions => Set<Hak.Domain.Reports.ReportDefinition>();
    public DbSet<Hak.Domain.Reports.ReportSnapshot> ReportSnapshots => Set<Hak.Domain.Reports.ReportSnapshot>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite stores decimals as INTEGER × 10^4 (see ApplySqliteMoneyStorage), so literal bounds in check constraints scale too.
        var scale = Database.IsSqlite() ? MoneyMath.StorageFactor : 1m;
        string Bound(decimal v) => (v * scale).ToString(System.Globalization.CultureInfo.InvariantCulture);
        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(250);
            e.Property(x => x.FunctionalCurrency).HasMaxLength(3);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasOne<AccountingRegimeVersion>().WithMany().HasForeignKey(x => x.RegimeVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => x.FunctionalCurrency).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Branch>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<Currency>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(3);
            e.ToTable(t => t.HasCheckConstraint("ck_currencies_decimal_places", "decimal_places BETWEEN 0 AND 4"));
        });
        b.Entity<FiscalYear>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Periods).WithOne().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_fiscal_years_dates", "end_date >= start_date"));
        });
        b.Entity<AccountingPeriod>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.FiscalYearId, x.PeriodNo }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.StartDate });
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<AccountingRegime>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.RegimeId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccountingRegimeVersion>(e =>
        {
            e.HasIndex(x => new { x.RegimeId, x.VersionCode }).IsUnique();
            e.HasMany(x => x.LegalSources).WithOne().HasForeignKey(x => x.RegimeVersionId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Account>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.ReportTags).HasMaxLength(200);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<BusinessPartner>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.TaxCode });
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<Voucher>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.VoucherType, x.FiscalYearId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.SourceSystem, x.SourceId }).IsUnique().HasFilter("source_id IS NOT NULL AND status <> 9"); // cancelled drafts do not block re-creation
            e.HasIndex(x => new { x.CompanyId, x.ReversesVoucherId }).IsUnique().HasFilter("reverses_voucher_id IS NOT NULL");
            e.HasIndex(x => new { x.CompanyId, x.Status, x.PostingDate });
            e.Property(x => x.Number).HasMaxLength(30);
            e.Property(x => x.VoucherType).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FiscalYear>().WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.ReversesVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.AdjustsVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.TotalDebit);
            e.Ignore(x => x.TotalCredit);
        });
        b.Entity<VoucherLine>(e =>
        {
            e.Property(x => x.CashFlowCode).HasMaxLength(4);
            e.HasIndex(x => new { x.VoucherId, x.LineNo }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_voucher_lines_amount", "amount > 0");
                t.HasCheckConstraint("ck_voucher_lines_side", "side IN (1, -1)");
            });
        });
        // analysis_code_id on voucher / journal lines has no database foreign key: adding one would make SQLite rebuild the
        // immutable ledger tables (dropping their hand-written triggers). The engine validates the code; codes are never deleted.
        b.Entity<VoucherTemplate>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.VoucherType).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Hak.Domain.Security.IdempotencyRecord>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Method).HasMaxLength(10);
            e.Property(x => x.Path).HasMaxLength(300);
            e.Property(x => x.RequestHash).HasMaxLength(64);
        });
        b.Entity<VoucherAttachment>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.VoucherId });
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.Extension).HasMaxLength(10);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.RemoveReason).HasMaxLength(500);
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AnalysisCode>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(50);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Name).HasMaxLength(250);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<VoucherHistory>(e => e.HasIndex(x => new { x.CompanyId, x.VoucherId, x.At }));
        b.Entity<VoucherNumberSequence>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.FiscalYearId, x.VoucherType }).IsUnique();
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<JournalEntry>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.VoucherId }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.PostingDate });
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => x.PeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountingRegimeVersion>().WithMany().HasForeignKey(x => x.RegimeVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<JournalLine>(e =>
        {
            e.Property(x => x.CashFlowCode).HasMaxLength(4);
            e.HasIndex(x => new { x.CompanyId, x.AccountId, x.PostingDate });
            e.HasIndex(x => new { x.CompanyId, x.PartnerId, x.AccountId });
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_journal_lines_amount", "amount > 0");
                t.HasCheckConstraint("ck_journal_lines_side", "side IN (1, -1)");
            });
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedUserName).IsUnique();
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RolePermission>(e => e.HasKey(x => new { x.RoleId, x.Permission }));
        b.Entity<UserCompanyRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.CompanyId, x.RoleId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
        });
        b.Entity<LoginSession>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
        b.Entity<AuditLog>(e => e.HasIndex(x => new { x.CompanyId, x.Seq }).IsUnique());
        b.Entity<ImportBatch>(e => e.HasIndex(x => new { x.CompanyId, x.PackageId }).IsUnique());
        b.Entity<ExchangePartnerKey>(e => e.HasIndex(x => new { x.CompanyId, x.SourceSystem, x.KeyId }).IsUnique());
        b.Entity<SettlementAllocation>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.DebitLineId });
            e.HasIndex(x => new { x.CompanyId, x.CreditLineId });
            e.HasIndex(x => new { x.CompanyId, x.AccountId, x.PartnerId });
            e.HasOne<JournalLine>().WithMany().HasForeignKey(x => x.DebitLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalLine>().WithMany().HasForeignKey(x => x.CreditLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsActive);
            e.ToTable(t => t.HasCheckConstraint("ck_settlement_allocations_amount", "amount > 0"));
        });
        b.Entity<AccountRoleMapping>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.RoleCode }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<PostingRule>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code, x.Version }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.DocumentType, x.BusinessEvent, x.Status });
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<ClosingRuleSet>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code, x.Version }).IsUnique();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.RuleSetId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ClosingStep>(e => e.HasOne<Account>().WithMany().HasForeignKey(x => x.TargetAccountId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<Warehouse>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Item>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.InventoryAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CostingPolicy>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.EffectiveFrom }).IsUnique();
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockDocument>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.ReversesDocumentId }).IsUnique().HasFilter("reverses_document_id IS NOT NULL");
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<StockDocumentLine>(e =>
        {
            e.HasIndex(x => new { x.DocumentId, x.LineNo }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.OrderId, x.OrderLineNo });
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_stock_document_lines_quantity", "quantity > 0"));
        });
        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.ItemId, x.Sequence });
            e.HasIndex(x => new { x.CompanyId, x.DocumentId });
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_stock_movements_quantity", "quantity <> 0"));
        });
        b.Entity<StockValuation>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.MovementId }).IsUnique().HasFilter("voided_at IS NULL");
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockValueAdjustment>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.ItemId, x.Sequence });
            e.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.TargetMovementId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_stock_value_adjustments_value", "value <> 0"));
        });
        b.Entity<CostingRun>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.WarehouseId, x.PeriodId }).IsUnique().HasFilter("status = 1");
        });
        b.Entity<DrynessMethod>(e => e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique());
        b.Entity<StockMeasurement>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.DocumentId, x.LineNo }).IsUnique().HasFilter("superseded_at IS NULL");
            e.HasIndex(x => new { x.CompanyId, x.EvidenceNo });
            e.HasOne<StockDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DrynessMethod>().WithMany().HasForeignKey(x => x.MethodId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_stock_measurements_dryness", $"dryness_percent > 0 AND dryness_percent <= {Bound(100)}");
                t.HasCheckConstraint("ck_stock_measurements_mt", "measured_mt > 0");
            });
        });
        b.Entity<CommercialInvoice>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Kind, x.PartnerId, x.InvoiceSeries, x.InvoiceNo });
            e.HasIndex(x => new { x.CompanyId, x.AdjustsInvoiceId });
            e.HasOne<CommercialInvoice>().WithMany().HasForeignKey(x => x.AdjustsInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<CommercialInvoiceLine>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.LineNo }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.StockDocumentId, x.StockLineNo });
            e.ToTable(t => t.HasCheckConstraint("ck_commercial_invoice_lines_amount", "amount >= 0 AND vat_amount >= 0"));
        });
        b.Entity<TradeOrder>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Kind, x.Status });
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("ck_trade_orders_tolerance", $"over_delivery_tolerance_percent >= 0 AND over_delivery_tolerance_percent <= {Bound(100)}"));
        });
        b.Entity<TradeOrderLine>(e =>
        {
            e.HasIndex(x => new { x.OrderId, x.LineNo }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_trade_order_lines_quantity", "quantity > 0 AND unit_price >= 0"));
        });
        b.Entity<EInvoiceRecord>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Direction, x.SellerTaxCode, x.Series, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.IssueDate });
            e.HasMany(x => x.RateLines).WithOne().HasForeignKey(x => x.EInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EInvoiceImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<EInvoiceImportBatch>(e => e.HasIndex(x => new { x.CompanyId, x.SourceSha256 }).IsUnique());
        b.Entity<EInvoiceStatusChange>(e =>
        {
            e.HasIndex(x => new { x.EInvoiceId, x.At });
            e.HasOne<EInvoiceRecord>().WithMany().HasForeignKey(x => x.EInvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<TaxDeclaration>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.TaxType, x.PeriodFrom, x.PeriodTo }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DeclarationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<TaxDeclarationLine>(e => e.HasIndex(x => new { x.DeclarationId, x.IndicatorCode }).IsUnique());
        b.Entity<PaymentOrder>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status });
            e.HasIndex(x => new { x.CompanyId, x.VoucherId });
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("ck_payment_orders_amount", "amount > 0"));
        });
        b.Entity<BankStatement>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.SourceSha256 }).IsUnique();
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<BankStatementLine>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.BankReference }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.BankAccountId, x.Status });
            e.ToTable(t => t.HasCheckConstraint("ck_bank_statement_lines_amount", "amount <> 0"));
        });
        b.Entity<ReconciliationMatch>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.StatementLineId }).IsUnique().HasFilter("voided_at IS NULL");
            e.HasIndex(x => new { x.CompanyId, x.JournalLineId }).IsUnique().HasFilter("voided_at IS NULL");
            e.HasOne<BankStatementLine>().WithMany().HasForeignKey(x => x.StatementLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalLine>().WithMany().HasForeignKey(x => x.JournalLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LongTermAsset>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status });
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AssetAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccumulatedAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.CostSources).WithOne().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Name).HasMaxLength(250);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_long_term_assets_amounts", "original_cost >= 0 AND residual_value >= 0 AND opening_accumulated >= 0");
                t.HasCheckConstraint("ck_long_term_assets_life", "useful_life_months > 0 AND (remaining_life_months IS NULL OR remaining_life_months > 0)");
            });
        });
        b.Entity<AssetCostSource>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.JournalLineId });
            e.HasIndex(x => new { x.AssetId, x.JournalLineId }).IsUnique();
            e.HasOne<JournalLine>().WithMany().HasForeignKey(x => x.JournalLineId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_asset_cost_sources_amount", "amount > 0"));
        });
        b.Entity<DepreciationRun>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.PeriodId }).IsUnique().HasFilter("status = 1");
            e.HasIndex(x => new { x.CompanyId, x.PeriodStart });
        });
        b.Entity<DepreciationEntry>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.AssetId });
            e.HasIndex(x => new { x.RunId });
            e.HasOne<LongTermAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DepreciationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_depreciation_entries_amount", "amount > 0"));
        });

        b.Entity<FxRevaluationRun>(e => e.HasIndex(x => new { x.CompanyId, x.RevaluationDate }));
        b.Entity<LoanContract>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.LenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.PrincipalAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.InterestExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccruedInterestAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Rates).WithOne().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("ck_loan_contracts_dates", "maturity_date >= start_date AND contract_amount > 0"));
        });
        b.Entity<LoanRate>(e =>
        {
            e.HasIndex(x => new { x.LoanId, x.EffectiveFrom }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_loan_rates_rate", $"annual_rate_percent >= 0 AND annual_rate_percent <= {Bound(100)}"));
        });
        b.Entity<LoanLink>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.JournalLineId }).IsUnique();
            e.HasIndex(x => x.LoanId);
            e.HasOne<LoanContract>().WithMany().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalLine>().WithMany().HasForeignKey(x => x.JournalLineId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LoanInterestRun>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.PeriodId }).IsUnique().HasFilter("status = 1");
            e.HasIndex(x => new { x.CompanyId, x.PeriodStart });
        });
        b.Entity<CostObject>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.WipAccountId }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.WipAccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Code).HasMaxLength(30);
        });
        b.Entity<ProductionCostRun>(e =>
        {
            e.HasIndex(x => new { x.CostObjectId, x.PeriodId }).IsUnique().HasFilter("status = 1");
            e.HasOne<CostObject>().WithMany().HasForeignKey(x => x.CostObjectId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Hak.Domain.Reports.ReportDefinition>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code, x.Version }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Checks).WithOne().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Code).HasMaxLength(30);
            e.Property(x => x.Columns).HasMaxLength(600);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
        });
        b.Entity<Hak.Domain.Reports.ReportLine>(e =>
        {
            e.ToTable("report_lines");
            e.HasIndex(x => new { x.DefinitionId, x.LineNo }).IsUnique();
            e.HasIndex(x => new { x.DefinitionId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(30);
        });
        b.Entity<Hak.Domain.Reports.ReportCheck>(e => { e.ToTable("report_checks"); e.HasIndex(x => new { x.DefinitionId, x.LineNo }).IsUnique(); });
        b.Entity<Hak.Domain.Reports.ReportSnapshot>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.DefinitionCode, x.ToDate });
            e.HasOne<Hak.Domain.Reports.ReportDefinition>().WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DefinitionCode).HasMaxLength(30);
        });
        b.Entity<PayrollSheet>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.SourceSystem, x.SourceId, x.SourceVersion }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.PostingDate });
            e.HasOne<Voucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SheetId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceSystem).HasMaxLength(30);
            e.Property(x => x.SourceId).HasMaxLength(100);
            e.Property(x => x.SourceVersion).HasMaxLength(50);
        });
        b.Entity<PayrollSheetLine>(e =>
        {
            e.HasIndex(x => new { x.SheetId, x.LineNo }).IsUnique();
            e.HasIndex(x => new { x.SheetId, x.EmployeeId }).IsUnique();
            e.HasOne<BusinessPartner>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.EmployeeDeductions);
            e.Ignore(x => x.EmployerContributions);
            e.ToTable(t => t.HasCheckConstraint("ck_payroll_sheet_lines_balance",
                "gross - social_insurance - health_insurance - unemployment_insurance - personal_income_tax - other_deduction = net"));
        });
        b.Entity<LoanInterestEntry>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.LoanId });
            e.HasIndex(x => x.RunId);
            e.HasOne<LoanContract>().WithMany().HasForeignKey(x => x.LoanId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LoanInterestRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_loan_interest_entries_amount", "amount > 0"));
        });

        if (Database.IsSqlite()) ApplySqliteMoneyStorage(b);
        else ApplyExactDecimalPrecision(b);
        ApplySnakeCaseNames(b);
    }

    /// <summary>
    /// SQLite has no decimal type; EF would store TEXT, which breaks SUM/ORDER BY.
    /// Money is stored as INTEGER scaled by 10^4 (docs/02 ADR-05). Exchange rates are not
    /// aggregated and stay as invariant TEXT (EF default for decimal on SQLite).
    /// </summary>
    private static void ApplySqliteMoneyStorage(ModelBuilder b)
    {
        var money = new ValueConverter<decimal, long>(v => MoneyMath.ToStorage(v), v => MoneyMath.FromStorage(v));
        var moneyNullable = new ValueConverter<decimal?, long?>(
            v => v.HasValue ? MoneyMath.ToStorage(v.Value) : null,
            v => v.HasValue ? MoneyMath.FromStorage(v.Value) : null);

        b.Entity<Company>().Property(x => x.ApprovalLimit).HasConversion(moneyNullable);
        b.Entity<VoucherLine>().Property(x => x.Amount).HasConversion(money);
        b.Entity<VoucherLine>().Property(x => x.ForeignAmount).HasConversion(moneyNullable);
        b.Entity<JournalLine>().Property(x => x.Amount).HasConversion(money);
        b.Entity<JournalLine>().Property(x => x.ForeignAmount).HasConversion(moneyNullable);
        b.Entity<SettlementAllocation>().Property(x => x.Amount).HasConversion(money);
        b.Entity<SettlementAllocation>().Property(x => x.ForeignAmount).HasConversion(moneyNullable);
        b.Entity<PaymentOrder>().Property(x => x.Amount).HasConversion(money);
        b.Entity<BankStatement>().Property(x => x.OpeningBalance).HasConversion(money);
        b.Entity<BankStatement>().Property(x => x.ClosingBalance).HasConversion(money);
        b.Entity<BankStatementLine>().Property(x => x.Amount).HasConversion(money);
        b.Entity<StockDocumentLine>().Property(x => x.Quantity).HasConversion(money);
        b.Entity<StockDocumentLine>().Property(x => x.Amount).HasConversion(moneyNullable);
        b.Entity<StockMovement>().Property(x => x.Quantity).HasConversion(money);
        b.Entity<StockValuation>().Property(x => x.Value).HasConversion(money);
        b.Entity<StockValueAdjustment>().Property(x => x.Value).HasConversion(money);
        b.Entity<StockMeasurement>().Property(x => x.MeasuredMt).HasConversion(money);
        b.Entity<StockMeasurement>().Property(x => x.DrynessPercent).HasConversion(money);
        b.Entity<StockMeasurement>().Property(x => x.Bdmt).HasConversion(money);
        b.Entity<CommercialInvoiceLine>().Property(x => x.StockQuantity).HasConversion(moneyNullable);
        b.Entity<CommercialInvoiceLine>().Property(x => x.BasisQuantity).HasConversion(money);
        b.Entity<CommercialInvoiceLine>().Property(x => x.Amount).HasConversion(money);
        b.Entity<CommercialInvoiceLine>().Property(x => x.FunctionalAmount).HasConversion(money);
        b.Entity<CommercialInvoiceLine>().Property(x => x.VatAmount).HasConversion(money);
        b.Entity<TradeOrder>().Property(x => x.OverDeliveryTolerancePercent).HasConversion(money);
        b.Entity<TradeOrderLine>().Property(x => x.Quantity).HasConversion(money);
        b.Entity<TradeOrderLine>().Property(x => x.UnitPrice).HasConversion(money);
        b.Entity<EInvoiceRecord>().Property(x => x.AmountBeforeVat).HasConversion(money);
        b.Entity<EInvoiceRecord>().Property(x => x.VatAmount).HasConversion(money);
        b.Entity<EInvoiceRecord>().Property(x => x.TotalPayment).HasConversion(money);
        b.Entity<EInvoiceRateLine>().Property(x => x.AmountBeforeVat).HasConversion(money);
        b.Entity<EInvoiceRateLine>().Property(x => x.VatAmount).HasConversion(money);
        b.Entity<TaxDeclarationLine>().Property(x => x.DeclaredAmount).HasConversion(money);
        b.Entity<LongTermAsset>().Property(x => x.OriginalCost).HasConversion(money);
        b.Entity<LongTermAsset>().Property(x => x.ResidualValue).HasConversion(money);
        b.Entity<LongTermAsset>().Property(x => x.OpeningAccumulated).HasConversion(money);
        b.Entity<AssetCostSource>().Property(x => x.Amount).HasConversion(money);
        b.Entity<DepreciationEntry>().Property(x => x.Amount).HasConversion(money);
        b.Entity<LoanContract>().Property(x => x.ContractAmount).HasConversion(money);
        b.Entity<LoanRate>().Property(x => x.AnnualRatePercent).HasConversion(money);
        b.Entity<LoanInterestEntry>().Property(x => x.Amount).HasConversion(money);
        b.Entity<LoanInterestEntry>().Property(x => x.ForeignAmount).HasConversion(moneyNullable);
        b.Entity<PayrollSheetLine>().Property(x => x.Gross).HasConversion(money);
        b.Entity<CostObject>().Property(x => x.PlannedUnitCost).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.WipBalanceBefore).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.ProvisionalValue).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.ClosingWip).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.OutputQuantity).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.ActualCost).HasConversion(money);
        b.Entity<ProductionCostRun>().Property(x => x.Difference).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.SocialInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.HealthInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.UnemploymentInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.PersonalIncomeTax).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.OtherDeduction).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.Net).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.EmployerSocialInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.EmployerHealthInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.EmployerUnemploymentInsurance).HasConversion(money);
        b.Entity<PayrollSheetLine>().Property(x => x.EmployerUnionFee).HasConversion(money);
    }

    /// <summary>
    /// Server databases (PostgreSQL): exact numeric columns — money and quantities numeric(19,4), rates numeric(18,6)
    /// (docs/02 ADR-05). Applied to every decimal property so a new entity cannot silently get a float type.
    /// </summary>
    private static void ApplyExactDecimalPrecision(ModelBuilder b)
    {
        foreach (var p in b.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            if (p.Name.EndsWith("Rate", StringComparison.Ordinal) && p.Name != nameof(Domain.Trade.CommercialInvoiceLine.VatRate)) p.SetPrecision(18);
            else p.SetPrecision(19);
            p.SetScale(p.Name.EndsWith("Rate", StringComparison.Ordinal) && p.Name != nameof(Domain.Trade.CommercialInvoiceLine.VatRate) ? 6 : 4);
        }
    }

    private static void ApplySnakeCaseNames(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnake(entity.GetTableName() ?? entity.ClrType.Name));
            foreach (var p in entity.GetProperties()) p.SetColumnName(ToSnake(p.Name));
            foreach (var k in entity.GetKeys()) k.SetName(ToSnake(k.GetName() ?? ""));
            foreach (var fk in entity.GetForeignKeys()) fk.SetConstraintName(ToSnake(fk.GetConstraintName() ?? ""));
            foreach (var ix in entity.GetIndexes()) ix.SetDatabaseName(ToSnake(ix.GetDatabaseName() ?? ""));
        }
    }

    public static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && name[i - 1] != '_' && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
