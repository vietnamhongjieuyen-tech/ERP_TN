namespace Hak.Domain.Trade;

public enum InvoiceKind { Purchase = 1, Sales = 2 }

public enum InvoiceStatus { Draft = 1, Posted = 4, Reversed = 5, Cancelled = 9 }

/// <summary>Quantity the unit price applies to. Wood chips are bought and sold per BDMT while the stock ledger runs in MT.</summary>
public enum PricingBasis { Quantity = 1, Bdmt = 2 }

/// <summary>
/// Why the invoice exists. Return/Discount/Increase documents adjust a posted Normal invoice (AdjustsInvoiceId).
/// The legal form of adjustment e-invoices is to be verified against current invoice regulations [CẦN XÁC MINH].
/// </summary>
public enum InvoicePurpose
{
    Normal = 1,
    /// <summary>Hàng bán bị trả lại: goods come back into stock at their original cost; revenue reduced.</summary>
    Return = 2,
    /// <summary>Giảm giá / chiết khấu sau bán: revenue reduced, no stock movement.</summary>
    Discount = 3,
    /// <summary>Điều chỉnh tăng: additional amount on the original sale.</summary>
    Increase = 4,
}

/// <summary>
/// Purchase or sales invoice (master prompt §12–13). Three dates are kept apart: invoice date (on the invoice),
/// the receipt/delivery date (on the linked stock document) and the posting date (accounting period).
/// </summary>
public sealed class CommercialInvoice
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public InvoiceKind Kind { get; set; }
    public InvoicePurpose Purpose { get; set; } = InvoicePurpose.Normal;
    public Guid? AdjustsInvoiceId { get; set; }
    /// <summary>Internal document number (HDM/HDB…); the legal invoice identity is Series + InvoiceNo.</summary>
    public string Number { get; set; } = "";
    public string InvoiceSeries { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    public DateOnly InvoiceDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public Guid PartnerId { get; set; }
    /// <summary>ISO currency of the prices; functional currency when null.</summary>
    public string? Currency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public string Description { get; set; } = "";
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public Guid? VoucherId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? PostedBy { get; set; }
    public DateTime? PostedAt { get; set; }
    public long RowVersion { get; set; }
    public List<CommercialInvoiceLine> Lines { get; set; } = [];
}

public sealed class CommercialInvoiceLine
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid CompanyId { get; set; }
    public int LineNo { get; set; }
    /// <summary>Line of the original invoice this adjustment line refers to.</summary>
    public int? AdjustsLineNo { get; set; }
    public Guid? ItemId { get; set; }
    /// <summary>Linked stock document line (receipt for purchases, issue for sales).</summary>
    public Guid? StockDocumentId { get; set; }
    public int? StockLineNo { get; set; }
    /// <summary>Stock quantity (main unit) this line covers from the linked stock line.</summary>
    public decimal? StockQuantity { get; set; }
    /// <summary>Expense/revenue account for lines without stock (services) or to override the default.</summary>
    public Guid? AccountId { get; set; }
    /// <summary>Mã phân tích put on the revenue / expense line this invoice line generates.</summary>
    public Guid? AnalysisCodeId { get; set; }
    public PricingBasis Basis { get; set; } = PricingBasis.Quantity;
    /// <summary>Quantity × price base: stock quantity, or BDMT from the active measurement of the linked line.</summary>
    public decimal BasisQuantity { get; set; }
    /// <summary>Price in invoice currency per basis unit.</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>Amount in invoice currency (= round(BasisQuantity × UnitPrice)).</summary>
    public decimal Amount { get; set; }
    /// <summary>Amount in functional currency.</summary>
    public decimal FunctionalAmount { get; set; }
    /// <summary>VAT rate in percent; null = not subject to VAT. Rates are company configuration [CẦN XÁC MINH].</summary>
    public decimal? VatRate { get; set; }
    public decimal VatAmount { get; set; }
    public string? Description { get; set; }
    /// <summary>Order line covered (inherited from the stock line, or set directly for service lines).</summary>
    public Guid? OrderId { get; set; }
    public int? OrderLineNo { get; set; }
}
