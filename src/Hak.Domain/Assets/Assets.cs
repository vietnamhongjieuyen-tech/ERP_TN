using Hak.Domain.Common;

namespace Hak.Domain.Assets;

/// <summary>TSCĐ (depreciated, with an accumulated-depreciation account) or CCDC / prepaid cost (allocated straight off its own account).</summary>
public enum AssetClass { FixedAsset = 1, Tool = 2 }

/// <summary>Only straight-line is implemented. Other methods (declining balance, units of production) are DESIGNED, [CẦN XÁC MINH] legal basis.</summary>
public enum DepreciationMethod { StraightLine = 1 }

/// <summary>
/// How the first and last month are counted. [CẦN XÁC MINH] which rule the company applies under the current regulation.
/// FullMonth: the in-service month counts fully, the disposal month does not count.
/// Daily: by days used (start day and the day before disposal inclusive) over the days in the month.
/// </summary>
public enum ProrationPolicy { FullMonth = 1, Daily = 2 }

public enum AssetStatus { Draft = 1, Active = 2, Disposed = 3, Cancelled = 9 }

/// <summary>
/// Asset card. Its cost is never booked by this module: it is the sum of already-posted journal lines on the asset account
/// (<see cref="AssetCostSource"/>), so the register and the ledger cannot count the same cost twice.
/// Parameters are frozen once Active; corrections go through disposal reversal or (later) a change document.
/// </summary>
public sealed class LongTermAsset
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AssetClass Class { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.Draft;
    /// <summary>Cost account (TSCĐ) or prepaid/tool account (CCDC). Taken from the catalog, never hard-coded.</summary>
    public Guid AssetAccountId { get; set; }
    /// <summary>Accumulated depreciation account; required for fixed assets, null for tools (credited straight to the asset account).</summary>
    public Guid? AccumulatedAccountId { get; set; }
    public Guid ExpenseAccountId { get; set; }
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;
    public ProrationPolicy Proration { get; set; } = ProrationPolicy.FullMonth;
    /// <summary>Frozen at activation: Σ cost sources.</summary>
    public decimal OriginalCost { get; set; }
    public decimal ResidualValue { get; set; }
    public int UsefulLifeMonths { get; set; }
    /// <summary>Ngày đưa vào sử dụng / bắt đầu phân bổ.</summary>
    public DateOnly InServiceDate { get; set; }
    /// <summary>
    /// Assets taken over from another system: depreciation already accumulated before <see cref="ScheduleStartDate"/>
    /// (it sits in the opening-balance voucher) and the life still remaining from that date.
    /// </summary>
    public decimal OpeningAccumulated { get; set; }
    public int? RemainingLifeMonths { get; set; }
    public DateOnly ScheduleStartDate { get; set; }
    public DateOnly? DisposalDate { get; set; }
    public Guid? DisposalVoucherId { get; set; }
    public string? Note { get; set; }
    /// <summary>Nơi sử dụng: department, room or person using the asset (S22-DN); informational, can change while the asset is in use.</summary>
    public string? UsedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public Guid? ActivatedBy { get; set; }
    public long RowVersion { get; set; }
    public List<AssetCostSource> CostSources { get; set; } = [];

    /// <summary>Amount the schedule still has to depreciate from <see cref="ScheduleStartDate"/>.</summary>
    public decimal ScheduleBase => OriginalCost - ResidualValue - OpeningAccumulated;
    public int ScheduleMonths => RemainingLifeMonths ?? UsefulLifeMonths;
}

/// <summary>Part of a posted journal line (debit on the asset account) that makes up an asset's cost.</summary>
public sealed class AssetCostSource
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public Guid JournalLineId { get; set; }
    public decimal Amount { get; set; }
}

public enum DepreciationRunStatus { Active = 1, Reversed = 2 }

/// <summary>Depreciation / allocation of all active assets for one accounting period, with the GL voucher it produced.</summary>
public sealed class DepreciationRun
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid PeriodId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DepreciationRunStatus Status { get; set; } = DepreciationRunStatus.Active;
    public Guid? VoucherId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? ReversedAt { get; set; }
    public Guid? ReversedBy { get; set; }
}

/// <summary>
/// One asset's depreciation for a run, or its final part booked by a disposal (RunId null, IsDisposal).
/// Append-only; voided once when the run or disposal is reversed.
/// </summary>
public sealed class DepreciationEntry
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? RunId { get; set; }
    public bool IsDisposal { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? VoidedAt { get; set; }
}

/// <summary>
/// Pure straight-line schedule. Each period books (cumulative target at period end) − (already booked), so rounding never
/// accumulates and the last month always closes exactly at the schedule base. Rounding: MidpointRounding.AwayFromZero.
/// </summary>
public static class DepreciationCalculator
{
    /// <summary>
    /// Depreciation that should have been booked from <paramref name="start"/> up to and including <paramref name="through"/>.
    /// </summary>
    public static decimal CumulativeTarget(decimal scheduleBase, int months, DateOnly start, DateOnly through, ProrationPolicy proration, int decimals)
    {
        if (months <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Thời gian khấu hao phải lớn hơn 0 tháng.");
        if (scheduleBase <= 0 || through < start) return 0;
        var (num, den) = Elapsed(start, through, proration);
        // elapsed = num / den months; target = base × elapsed / months, computed as one exact division before rounding.
        if (num >= (long)months * den) return scheduleBase;
        return MoneyMath.Round(scheduleBase * num / (den * (decimal)months), decimals);
    }

    /// <summary>Elapsed months as an exact fraction num/den.</summary>
    internal static (long Num, long Den) Elapsed(DateOnly start, DateOnly through, ProrationPolicy proration)
    {
        var monthIndex = (through.Year * 12 + through.Month) - (start.Year * 12 + start.Month);
        if (proration == ProrationPolicy.FullMonth) return (monthIndex + 1, 1);

        long d1 = DateTime.DaysInMonth(start.Year, start.Month);
        if (monthIndex == 0) return (through.Day - start.Day + 1, d1);
        long d2 = DateTime.DaysInMonth(through.Year, through.Month);
        long first = d1 - start.Day + 1;       // days used in the first month, over d1
        long last = through.Day;               // days used in the last month, over d2
        long full = monthIndex - 1;            // whole months in between
        return (first * d2 + full * d1 * d2 + last * d1, d1 * d2);
    }

    /// <summary>Last day the asset depreciates when disposed on <paramref name="disposalDate"/>.</summary>
    public static DateOnly LastDepreciationDay(DateOnly disposalDate, ProrationPolicy proration)
        => proration == ProrationPolicy.FullMonth ? new DateOnly(disposalDate.Year, disposalDate.Month, 1).AddDays(-1) : disposalDate.AddDays(-1);
}
