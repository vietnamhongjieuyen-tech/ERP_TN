namespace Hak.Domain.Inventory;

public sealed class Warehouse
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

public sealed class Item
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Inventory (asset) account the item's value is carried on. Taken from the catalog, never hard-coded.</summary>
    public Guid InventoryAccountId { get; set; }
    /// <summary>Wood chips etc.: every receipt/issue line needs a dryness measurement to derive BDMT.</summary>
    public bool TracksDryness { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

/// <summary>The three issue-costing methods the business uses, each configured per warehouse.</summary>
public enum CostingMethod
{
    /// <summary>Nhập trước xuất trước.</summary>
    Fifo = 1,
    /// <summary>Bình quân gia quyền tức thời (sau mỗi lần nhập).</summary>
    MovingAverage = 2,
    /// <summary>Bình quân gia quyền cuối tháng: issues are valued by a month-end costing run.</summary>
    MonthlyAverage = 3,
}

/// <summary>Dated costing method of a warehouse. Changes take effect only from the start of a fiscal year.</summary>
public sealed class CostingPolicy
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WarehouseId { get; set; }
    public CostingMethod Method { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

public enum StockDocumentType { Receipt = 1, Issue = 2, Transfer = 3 }

public enum StockDocumentStatus { Draft = 1, Posted = 4, Reversed = 5, Cancelled = 9 }

public sealed class StockDocument
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Number { get; set; } = "";
    public StockDocumentType Type { get; set; }
    public Guid WarehouseId { get; set; }
    /// <summary>Destination warehouse for transfers.</summary>
    public Guid? ToWarehouseId { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Description { get; set; } = "";
    public Guid? PartnerId { get; set; }
    public StockDocumentStatus Status { get; set; } = StockDocumentStatus.Draft;
    public Guid? VoucherId { get; set; }
    public Guid? ReversalVoucherId { get; set; }
    public Guid? ReversesDocumentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public long RowVersion { get; set; }
    public List<StockDocumentLine> Lines { get; set; } = [];
}

public sealed class StockDocumentLine
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid CompanyId { get; set; }
    public int LineNo { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Receipt value (functional currency). Issues and transfers are valued by the costing engine.</summary>
    public decimal? Amount { get; set; }
    /// <summary>Counter account: supplier payable / goods-in-transit for receipts, expense / COGS for issues.</summary>
    public Guid? CounterAccountId { get; set; }
    public string? Description { get; set; }
    /// <summary>Purchase/sales order line this stock line fulfils.</summary>
    public Guid? OrderId { get; set; }
    public int? OrderLineNo { get; set; }
    /// <summary>Mã phân tích put on the counter-account line (cost of goods sold, expense) of the entry this line generates.</summary>
    public Guid? AnalysisCodeId { get; set; }
}

/// <summary>Immutable quantity ledger row. Quantity is signed: + in, − out. Value lives in <see cref="StockValuation"/>.</summary>
public sealed class StockMovement
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ItemId { get; set; }
    public Guid DocumentId { get; set; }
    public int LineNo { get; set; }
    public DateOnly MovementDate { get; set; }
    /// <summary>Strictly increasing per company: the processing order used by FIFO and moving average.</summary>
    public long Sequence { get; set; }
    public decimal Quantity { get; set; }
    public Guid? CounterAccountId { get; set; }
    public Guid? PartnerId { get; set; }
    /// <summary>Set on the mirror movement created when a stock document is reversed.</summary>
    public Guid? ReversesMovementId { get; set; }
}

/// <summary>
/// Value of a movement (signed like the quantity). Append-only; a monthly costing run that is reversed voids
/// its valuations so the month can be recosted. At most one active valuation per movement.
/// </summary>
public sealed class StockValuation
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid MovementId { get; set; }
    public decimal Value { get; set; }
    public Guid? CostingRunId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? VoidedAt { get; set; }
}

/// <summary>
/// Value-only change of a receipt already in stock (purchase price difference between the posted receipt and the
/// supplier invoice). Shares the movement sequence so costing replays it in order. Immutable.
/// </summary>
public sealed class StockValueAdjustment
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>The receipt movement whose value is adjusted.</summary>
    public Guid TargetMovementId { get; set; }
    public long Sequence { get; set; }
    public DateOnly AdjustmentDate { get; set; }
    public decimal Value { get; set; }
    public Guid? SourceInvoiceId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
}

public enum CostingRunStatus { Active = 1, Reversed = 2 }

/// <summary>Month-end weighted-average costing of one warehouse and period, with the GL voucher it produced.</summary>
public sealed class CostingRun
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid PeriodId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public CostingRunStatus Status { get; set; } = CostingRunStatus.Active;
    public Guid? VoucherId { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? ReversedAt { get; set; }
    public Guid? ReversedBy { get; set; }
}
