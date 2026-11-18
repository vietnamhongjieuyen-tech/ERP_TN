using Hak.Application.Accounting;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Assets;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Organization;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Assets;

public sealed record AssetCostSourceInput(Guid JournalLineId, decimal Amount);

public sealed record AssetInput(string Code, string Name, AssetClass Class, Guid AssetAccountId, Guid? AccumulatedAccountId, Guid ExpenseAccountId,
    ProrationPolicy Proration, decimal ResidualValue, int UsefulLifeMonths, DateOnly InServiceDate, IReadOnlyList<AssetCostSourceInput> CostSources,
    decimal OpeningAccumulated = 0, int? RemainingLifeMonths = null, DateOnly? ScheduleStartDate = null, string? Note = null, string? UsedBy = null);

public sealed record AssetRegisterRow(Guid Id, string Code, string Name, AssetClass Class, AssetStatus Status, string AssetAccountCode,
    DateOnly InServiceDate, DateOnly ScheduleStartDate, int UsefulLifeMonths, decimal OriginalCost, decimal Accumulated, decimal NetBookValue, DateOnly? DisposalDate,
    decimal ResidualValue = 0, string? SourceVoucher = null, DateOnly? SourceDate = null, string? DisposalVoucher = null, string? DisposalReason = null, string? UsedBy = null);

public sealed record AssetScheduleRow(DateOnly PeriodEnd, decimal Planned, decimal Cumulative, decimal? Booked);

public sealed record AssetSourceRow(Guid JournalLineId, string VoucherNumber, DateOnly PostingDate, string AccountCode, decimal LineAmount, decimal Amount);

public sealed record AssetDetail(LongTermAsset Asset, IReadOnlyList<AssetSourceRow> Sources, IReadOnlyList<DepreciationEntry> Entries, IReadOnlyList<AssetScheduleRow> Schedule);

public sealed record AssetReconcileRow(Guid AccountId, string AccountCode, string Kind, decimal RegisterBalance, decimal LedgerBalance, decimal Difference);

/// <summary>Posted journal line on an asset account that is not yet (fully) assigned to an asset card.</summary>
public sealed record AssetCostCandidate(Guid JournalLineId, string VoucherNumber, DateOnly PostingDate, string AccountCode, string? Description, decimal LineAmount, decimal Assigned);

/// <summary>
/// Fixed assets and tools (master prompt: FA, TOOL). The register never books cost itself: an asset's cost is assigned from
/// posted debit lines on its asset account, so the register reconciles to the ledger by construction. Depreciation is a
/// company-wide run per accounting period, in period order; each run books (cumulative target) − (already booked) per asset.
/// Disposal books the last partial depreciation and removes cost and accumulated depreciation; the net book value goes to
/// the account the user chooses (proceeds are invoiced separately). Every run and disposal can be reversed, latest first.
/// Legal basis of methods and proration: [CẦN XÁC MINH].
/// </summary>
public sealed class AssetService(HakDbContext db, AccountingEngine engine, AuditService audit, IClock clock)
{
    public const string AssetSource = "HAK.ASSET";

    // ------------------------------------------------------------------ asset cards

    public async Task<LongTermAsset> CreateAsync(SessionContext ctx, AssetInput input)
    {
        ctx.Require(Permissions.AssetManage);
        if (string.IsNullOrWhiteSpace(input.Code) || string.IsNullOrWhiteSpace(input.Name))
            throw new DomainException(ErrorCodes.ValidationFailed, "Mã và tên tài sản bắt buộc.");
        if (input.UsefulLifeMonths <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Thời gian sử dụng phải lớn hơn 0 tháng.");
        if (input.ResidualValue < 0 || input.OpeningAccumulated < 0) throw new DomainException(ErrorCodes.AmountNotPositive, "Giá trị thu hồi và hao mòn lũy kế đầu kỳ không được âm.");
        if (input.CostSources.Count == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Phải chọn bút toán đã ghi sổ hình thành nguyên giá.");
        if (input.CostSources.Select(s => s.JournalLineId).Distinct().Count() != input.CostSources.Count)
            throw new DomainException(ErrorCodes.ValidationFailed, "Một dòng bút toán chỉ chọn một lần cho mỗi tài sản.");
        var takenOver = input.OpeningAccumulated > 0 || input.RemainingLifeMonths is not null;
        if (takenOver && (input.RemainingLifeMonths is not > 0 || input.ScheduleStartDate is null))
            throw new DomainException(ErrorCodes.ValidationFailed, "Tài sản chuyển từ hệ thống cũ phải có số tháng còn lại và ngày bắt đầu khấu hao trên hệ thống mới.");
        var scheduleStart = input.ScheduleStartDate ?? input.InServiceDate;
        if (scheduleStart < input.InServiceDate) throw new DomainException(ErrorCodes.ValidationFailed, "Ngày bắt đầu khấu hao không được trước ngày đưa vào sử dụng.");
        if (input.Class == AssetClass.FixedAsset && (input.AccumulatedAccountId is null || input.AccumulatedAccountId == input.AssetAccountId))
            throw new DomainException(ErrorCodes.ValidationFailed, "TSCĐ phải có tài khoản hao mòn riêng.");
        if (input.Class == AssetClass.Tool && input.AccumulatedAccountId is not null)
            throw new DomainException(ErrorCodes.ValidationFailed, "CCDC / chi phí trả trước phân bổ thẳng vào tài khoản của nó, không dùng tài khoản hao mòn.");

        return await db.InTransactionAsync(async () =>
        {
            var code = input.Code.Trim();
            if (await db.LongTermAssets.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã tài sản {code} đã tồn tại.");
            var dp = await DecimalsAsync(ctx.CompanyId);
            if (input.CostSources.Any(s => s.Amount <= 0 || !MoneyMath.HasAtMostDecimals(s.Amount, dp))
                || !MoneyMath.HasAtMostDecimals(input.ResidualValue, dp) || !MoneyMath.HasAtMostDecimals(input.OpeningAccumulated, dp))
                throw new DomainException(ErrorCodes.AmountPrecision, $"Số tiền phải dương và tối đa {dp} chữ số thập phân.");
            var assetAcc = await PostableAccountAsync(ctx, input.AssetAccountId);
            if (assetAcc.CashKind != CashKind.None || assetAcc.OpenItemNature != OpenItemNature.None || assetAcc.RequiresPartner)
                throw new DomainException(ErrorCodes.ValidationFailed, $"Tài khoản {assetAcc.Code} không dùng làm tài khoản tài sản (không phải tiền/công nợ).");
            if (input.AccumulatedAccountId is { } accId) await PostableAccountAsync(ctx, accId);
            await PostableAccountAsync(ctx, input.ExpenseAccountId);

            var asset = new LongTermAsset
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Name = input.Name.Trim(), Class = input.Class,
                AssetAccountId = input.AssetAccountId, AccumulatedAccountId = input.AccumulatedAccountId, ExpenseAccountId = input.ExpenseAccountId,
                Proration = input.Proration, ResidualValue = input.ResidualValue, UsefulLifeMonths = input.UsefulLifeMonths,
                InServiceDate = input.InServiceDate, OpeningAccumulated = input.OpeningAccumulated, RemainingLifeMonths = input.RemainingLifeMonths,
                ScheduleStartDate = scheduleStart, Note = input.Note, UsedBy = string.IsNullOrWhiteSpace(input.UsedBy) ? null : input.UsedBy.Trim(), CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId, RowVersion = 1,
            };
            asset.CostSources = input.CostSources.Select(s => new AssetCostSource
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, AssetId = asset.Id, JournalLineId = s.JournalLineId, Amount = s.Amount,
            }).ToList();
            asset.OriginalCost = asset.CostSources.Sum(s => s.Amount);
            await CheckSourcesAsync(ctx, asset);
            db.LongTermAssets.Add(asset);
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.create", "asset", asset.Id.ToString(), input, asset.RowVersion);
            await db.SaveChangesAsync();
            return asset;
        });
    }

    /// <summary>Ghi tăng: freezes the card and starts depreciation. Activator must differ from the creator when the company requires it.</summary>
    public async Task<LongTermAsset> ActivateAsync(SessionContext ctx, Guid assetId)
    {
        ctx.Require(Permissions.AssetManage);
        return await db.InTransactionAsync(async () =>
        {
            var asset = await LoadAsync(ctx, assetId, tracking: true);
            if (asset.Status != AssetStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Tài sản đang ở trạng thái {asset.Status}.");
            var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == ctx.CompanyId);
            if (company.RequireDistinctApprover && asset.CreatedBy == ctx.UserId)
                throw new DomainException(ErrorCodes.SodViolation, "Người ghi tăng tài sản phải khác người lập thẻ.");
            await CheckSourcesAsync(ctx, asset);
            if (asset.ScheduleBase < 0)
                throw new DomainException(ErrorCodes.AssetCostMismatch, "Giá trị thu hồi + hao mòn lũy kế đầu kỳ vượt nguyên giá.");
            var last = await LastActiveRunAsync(ctx.CompanyId);
            if (last is not null && asset.ScheduleStartDate <= last.PeriodEnd)
                throw new DomainException(ErrorCodes.PeriodDepreciated,
                    $"Đã chạy khấu hao đến {last.PeriodEnd:dd/MM/yyyy}; ngày bắt đầu khấu hao phải sau ngày đó, hoặc hủy các lần chạy khấu hao từ kỳ của tài sản.");
            asset.Status = AssetStatus.Active;
            asset.ActivatedAt = clock.UtcNow;
            asset.ActivatedBy = ctx.UserId;
            asset.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.activate", "asset", asset.Id.ToString(), new { asset.OriginalCost, asset.ScheduleStartDate }, asset.RowVersion);
            await db.SaveChangesAsync();
            return asset;
        });
    }

    /// <summary>Changes where the asset is used (department / person, S22-DN). Allowed at any status except cancelled; logged.</summary>
    public async Task SetUsedByAsync(SessionContext ctx, Guid assetId, string? usedBy)
    {
        ctx.Require(Permissions.AssetManage);
        var value = string.IsNullOrWhiteSpace(usedBy) ? null : usedBy.Trim();
        if (value is { Length: > 200 }) throw new DomainException(ErrorCodes.ValidationFailed, "Nơi sử dụng tối đa 200 ký tự.");
        await db.InTransactionAsync(async () =>
        {
            var asset = await LoadAsync(ctx, assetId, tracking: true);
            if (asset.Status == AssetStatus.Cancelled) throw new DomainException(ErrorCodes.InvalidStateTransition, "Thẻ tài sản đã hủy.");
            if (asset.UsedBy == value) return 0;
            var before = asset.UsedBy;
            asset.UsedBy = value;
            asset.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.used_by", "asset", asset.Id.ToString(), new { before, after = value }, asset.RowVersion);
            await db.SaveChangesAsync();
            return 0;
        });
    }

    public async Task CancelDraftAsync(SessionContext ctx, Guid assetId, string reason)
    {
        ctx.Require(Permissions.AssetManage);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy thẻ tài sản.");
        await db.InTransactionAsync(async () =>
        {
            var asset = await LoadAsync(ctx, assetId, tracking: true);
            if (asset.Status != AssetStatus.Draft) throw new DomainException(ErrorCodes.InvalidStateTransition, "Chỉ hủy được thẻ tài sản chưa ghi tăng.");
            asset.Status = AssetStatus.Cancelled;
            asset.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.cancel", "asset", asset.Id.ToString(), new { reason }, asset.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Each source must be a posted (not reversed) debit on the asset account, and the amounts assigned to all registered
    /// assets may not exceed the line. Drafts do not reserve a line.
    /// </summary>
    private async Task CheckSourcesAsync(SessionContext ctx, LongTermAsset asset)
    {
        var ids = asset.CostSources.Select(s => s.JournalLineId).ToList();
        var lines = await (from l in db.JournalLines
                           join e in db.JournalEntries on l.JournalEntryId equals e.Id
                           join v in db.Vouchers on e.VoucherId equals v.Id
                           where ids.Contains(l.Id) && l.CompanyId == ctx.CompanyId
                           select new { l.Id, l.AccountId, l.Side, l.Amount, v.Status, v.Number }).AsNoTracking().ToListAsync();
        var used = await (from s in db.AssetCostSources
                          join a in db.LongTermAssets on s.AssetId equals a.Id
                          where ids.Contains(s.JournalLineId) && a.Id != asset.Id && (a.Status == AssetStatus.Active || a.Status == AssetStatus.Disposed)
                          select new { s.JournalLineId, s.Amount }).AsNoTracking().ToListAsync();
        foreach (var s in asset.CostSources)
        {
            var l = lines.FirstOrDefault(x => x.Id == s.JournalLineId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Dòng bút toán nguồn nguyên giá không tồn tại.");
            if (l.Status != VoucherStatus.Posted)
                throw new DomainException(ErrorCodes.AssetCostMismatch, $"Chứng từ {l.Number} không còn hiệu lực (đã đảo); không dùng làm nguồn nguyên giá.");
            if (l.AccountId != asset.AssetAccountId || l.Side != Side.Debit)
                throw new DomainException(ErrorCodes.AssetCostMismatch, $"Dòng của chứng từ {l.Number} không phải phát sinh Nợ tài khoản tài sản đã chọn.");
            var taken = used.Where(u => u.JournalLineId == l.Id).Sum(u => u.Amount);
            if (taken + s.Amount > l.Amount)
                throw new DomainException(ErrorCodes.AssetCostMismatch,
                    $"Dòng của chứng từ {l.Number}: {l.Amount:N0}, đã gán cho tài sản khác {taken:N0}, không đủ để gán thêm {s.Amount:N0}.");
        }
    }

    // ------------------------------------------------------------------ depreciation runs

    public async Task<DepreciationRun> RunAsync(SessionContext ctx, Guid periodId)
    {
        ctx.Require(Permissions.VoucherPost);
        return await db.InTransactionAsync(async () =>
        {
            var period = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId && p.CompanyId == ctx.CompanyId)
                         ?? throw new DomainException(ErrorCodes.PeriodNotFound, "Kỳ kế toán không tồn tại.");
            if (period.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, $"Kỳ {period.StartDate:MM/yyyy} đã khóa sổ.");
            var last = await LastActiveRunAsync(ctx.CompanyId);
            if (last is not null && last.PeriodStart >= period.StartDate)
                throw new DomainException(ErrorCodes.PeriodDepreciated, $"Đã chạy khấu hao đến {last.PeriodEnd:dd/MM/yyyy}; hủy từ kỳ gần nhất nếu cần chạy lại.");
            if (last is not null && last.PeriodEnd.AddDays(1) != period.StartDate)
                throw new DomainException(ErrorCodes.DepreciationPending, $"Chưa chạy khấu hao kỳ trước (đã chạy đến {last.PeriodEnd:dd/MM/yyyy}); chạy theo thứ tự kỳ.");

            var assets = await db.LongTermAssets.AsNoTracking()
                .Where(a => a.CompanyId == ctx.CompanyId && a.Status == AssetStatus.Active && a.ScheduleStartDate <= period.EndDate)
                .OrderBy(a => a.Code).ToListAsync();
            if (last is null && assets.FirstOrDefault(a => a.ScheduleStartDate < period.StartDate) is { } early)
                throw new DomainException(ErrorCodes.DepreciationPending,
                    $"Tài sản {early.Code} bắt đầu khấu hao từ {early.ScheduleStartDate:dd/MM/yyyy}; lần chạy đầu tiên phải từ kỳ chứa ngày đó.");

            var booked = await BookedAsync(ctx.CompanyId, assets.Select(a => a.Id).ToList());
            var dp = await DecimalsAsync(ctx.CompanyId);
            var run = new DepreciationRun
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, PeriodId = period.Id, PeriodStart = period.StartDate, PeriodEnd = period.EndDate,
                CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            };
            db.DepreciationRuns.Add(run);
            var gl = new List<VoucherLineInput>();
            var result = new List<object>();
            foreach (var a in assets)
            {
                var target = DepreciationCalculator.CumulativeTarget(a.ScheduleBase, a.ScheduleMonths, a.ScheduleStartDate, period.EndDate, a.Proration, dp);
                var amount = target - booked.GetValueOrDefault(a.Id);
                if (amount < 0) throw new DomainException(ErrorCodes.AssetCostMismatch, $"Số khấu hao đã ghi của {a.Code} vượt kế hoạch; kiểm tra dữ liệu.");
                if (amount == 0) continue;
                db.DepreciationEntries.Add(new DepreciationEntry
                {
                    Id = Ids.New(), CompanyId = ctx.CompanyId, AssetId = a.Id, RunId = run.Id, PeriodStart = period.StartDate, PeriodEnd = period.EndDate,
                    Amount = amount, CreatedAt = clock.UtcNow,
                });
                var label = $"{(a.Class == AssetClass.FixedAsset ? "Khấu hao" : "Phân bổ")} {a.Code} {period.StartDate:MM/yyyy}";
                gl.Add(new VoucherLineInput(a.ExpenseAccountId, Side.Debit, amount, Description: label));
                gl.Add(new VoucherLineInput(a.AccumulatedAccountId ?? a.AssetAccountId, Side.Credit, amount, Description: label));
                result.Add(new { asset = a.Code, amount });
            }
            if (gl.Count > 0)
            {
                var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(VoucherTypes.Depreciation, period.EndDate, period.EndDate,
                    $"Khấu hao TSCĐ / phân bổ CCDC kỳ {period.StartDate:MM/yyyy}", gl, SourceSystem: AssetSource, SourceId: run.Id.ToString()));
                run.VoucherId = v.Id;
            }
            await audit.AppendAsync(ctx.CompanyId, ctx, "depreciation.run", "depreciation_run", run.Id.ToString(), new { period = period.StartDate, result });
            await db.SaveChangesAsync();
            return run;
        });
    }

    public async Task ReverseRunAsync(SessionContext ctx, Guid runId, string reason)
    {
        ctx.Require(Permissions.VoucherReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy lần chạy khấu hao.");
        await db.InTransactionAsync(async () =>
        {
            var run = await db.DepreciationRuns.FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Lần chạy khấu hao không tồn tại.");
            if (run.Status != DepreciationRunStatus.Active) throw new DomainException(ErrorCodes.InvalidStateTransition, "Lần chạy khấu hao đã bị hủy.");
            if (await db.DepreciationRuns.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.Status == DepreciationRunStatus.Active && r.PeriodStart > run.PeriodStart))
                throw new DomainException(ErrorCodes.PeriodDepreciated, "Các kỳ sau đã chạy khấu hao; hủy từ kỳ gần nhất trở về trước.");
            if (await db.LongTermAssets.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.Status == AssetStatus.Disposed && a.DisposalDate > run.PeriodEnd))
                throw new DomainException(ErrorCodes.InvalidStateTransition, "Có tài sản ghi giảm sau kỳ này (số khấu hao cuối dựa trên kỳ này); hủy ghi giảm trước.");
            await RequireOpenPeriodAsync(ctx.CompanyId, run.PeriodEnd);
            foreach (var e in await db.DepreciationEntries.Where(e => e.RunId == run.Id && e.VoidedAt == null).ToListAsync())
                e.VoidedAt = clock.UtcNow;
            if (run.VoucherId is { } vid) await engine.ReverseCoreAsync(ctx, vid, run.PeriodEnd, $"Hủy khấu hao: {reason}", allowModuleOwned: true);
            run.Status = DepreciationRunStatus.Reversed;
            run.ReversedAt = clock.UtcNow;
            run.ReversedBy = ctx.UserId;
            await audit.AppendAsync(ctx.CompanyId, ctx, "depreciation.reverse", "depreciation_run", run.Id.ToString(), new { reason });
            await db.SaveChangesAsync();
        });
    }

    public async Task<IReadOnlyList<DepreciationRun>> RunsAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.ReportView);
        return await db.DepreciationRuns.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId)
            .OrderByDescending(r => r.PeriodStart).ThenByDescending(r => r.CreatedAt).ToListAsync();
    }

    // ------------------------------------------------------------------ disposal

    /// <summary>
    /// Ghi giảm (thanh lý, nhượng bán, hư hỏng…) on <paramref name="date"/>: books the depreciation of the disposal month that the
    /// proration policy allows, then removes cost and accumulated depreciation; the remaining value goes to <paramref name="residualAccountId"/>.
    /// </summary>
    public async Task<LongTermAsset> DisposeAsync(SessionContext ctx, Guid assetId, DateOnly date, Guid residualAccountId, string description)
    {
        ctx.Require(Permissions.AssetManage);
        ctx.Require(Permissions.VoucherPost);
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException(ErrorCodes.ValidationFailed, "Diễn giải / căn cứ ghi giảm bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            var asset = await LoadAsync(ctx, assetId, tracking: true);
            if (asset.Status != AssetStatus.Active) throw new DomainException(ErrorCodes.InvalidStateTransition, $"Tài sản đang ở trạng thái {asset.Status}.");
            if (date < asset.ScheduleStartDate) throw new DomainException(ErrorCodes.ValidationFailed, "Ngày ghi giảm không được trước ngày bắt đầu khấu hao.");
            var period = await RequireOpenPeriodAsync(ctx.CompanyId, date);
            var last = await LastActiveRunAsync(ctx.CompanyId);
            if (last is not null && last.PeriodEnd >= period.StartDate)
                throw new DomainException(ErrorCodes.PeriodDepreciated, $"Kỳ {period.StartDate:MM/yyyy} đã chạy khấu hao; hủy lần chạy kỳ đó trước khi ghi giảm.");
            if (asset.ScheduleStartDate < period.StartDate && last?.PeriodEnd != period.StartDate.AddDays(-1))
                throw new DomainException(ErrorCodes.DepreciationPending, "Chưa chạy khấu hao đến hết kỳ trước kỳ ghi giảm.");
            await PostableAccountAsync(ctx, residualAccountId);

            var dp = await DecimalsAsync(ctx.CompanyId);
            var booked = (await BookedAsync(ctx.CompanyId, [asset.Id])).GetValueOrDefault(asset.Id);
            var through = DepreciationCalculator.LastDepreciationDay(date, asset.Proration);
            var final = DepreciationCalculator.CumulativeTarget(asset.ScheduleBase, asset.ScheduleMonths, asset.ScheduleStartDate, through, asset.Proration, dp) - booked;
            if (final < 0) throw new DomainException(ErrorCodes.AssetCostMismatch, $"Số khấu hao đã ghi của {asset.Code} vượt kế hoạch; kiểm tra dữ liệu.");
            var accumulated = asset.OpeningAccumulated + booked + final;
            var remaining = asset.OriginalCost - accumulated;

            var disposalId = Ids.New();
            if (final > 0)
                db.DepreciationEntries.Add(new DepreciationEntry
                {
                    Id = disposalId, CompanyId = ctx.CompanyId, AssetId = asset.Id, IsDisposal = true, PeriodStart = period.StartDate, PeriodEnd = through,
                    Amount = final, CreatedAt = clock.UtcNow,
                });
            var gl = new List<VoucherLineInput>();
            var credit = asset.AccumulatedAccountId ?? asset.AssetAccountId;
            if (final > 0)
            {
                gl.Add(new VoucherLineInput(asset.ExpenseAccountId, Side.Debit, final, Description: $"Khấu hao tháng ghi giảm {asset.Code}"));
                gl.Add(new VoucherLineInput(credit, Side.Credit, final, Description: $"Khấu hao tháng ghi giảm {asset.Code}"));
            }
            if (asset.Class == AssetClass.FixedAsset)
            {
                if (accumulated > 0) gl.Add(new VoucherLineInput(credit, Side.Debit, accumulated, Description: $"Xóa hao mòn lũy kế {asset.Code}"));
                if (remaining > 0) gl.Add(new VoucherLineInput(residualAccountId, Side.Debit, remaining, Description: $"Giá trị còn lại {asset.Code}"));
                if (asset.OriginalCost > 0) gl.Add(new VoucherLineInput(asset.AssetAccountId, Side.Credit, asset.OriginalCost, Description: $"Ghi giảm nguyên giá {asset.Code}"));
            }
            else if (remaining > 0)
            {
                gl.Add(new VoucherLineInput(residualAccountId, Side.Debit, remaining, Description: $"Giá trị chưa phân bổ {asset.Code}"));
                gl.Add(new VoucherLineInput(asset.AssetAccountId, Side.Credit, remaining, Description: $"Giá trị chưa phân bổ {asset.Code}"));
            }
            if (gl.Count > 0)
            {
                var v = await engine.PostSystemVoucherAsync(ctx, new VoucherInput(VoucherTypes.AssetDisposal, date, date,
                    $"Ghi giảm {asset.Code} – {description.Trim()}", gl, SourceSystem: AssetSource, SourceId: disposalId.ToString()));
                asset.DisposalVoucherId = v.Id;
            }
            asset.Status = AssetStatus.Disposed;
            asset.DisposalDate = date;
            asset.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.dispose", "asset", asset.Id.ToString(),
                new { date, description, finalDepreciation = final, accumulated, remaining, residualAccountId }, asset.RowVersion);
            await db.SaveChangesAsync();
            return asset;
        });
    }

    public async Task<LongTermAsset> ReverseDisposalAsync(SessionContext ctx, Guid assetId, string reason)
    {
        ctx.Require(Permissions.AssetManage);
        ctx.Require(Permissions.VoucherReverse);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do hủy ghi giảm.");
        return await db.InTransactionAsync(async () =>
        {
            var asset = await LoadAsync(ctx, assetId, tracking: true);
            if (asset.Status != AssetStatus.Disposed || asset.DisposalDate is not { } date)
                throw new DomainException(ErrorCodes.InvalidStateTransition, "Tài sản chưa ghi giảm.");
            var period = await RequireOpenPeriodAsync(ctx.CompanyId, date);
            if (await db.DepreciationRuns.AnyAsync(r => r.CompanyId == ctx.CompanyId && r.Status == DepreciationRunStatus.Active && r.PeriodEnd >= period.StartDate))
                throw new DomainException(ErrorCodes.PeriodDepreciated, "Đã chạy khấu hao từ kỳ ghi giảm trở đi; hủy các lần chạy đó trước.");
            foreach (var e in await db.DepreciationEntries.Where(e => e.AssetId == asset.Id && e.IsDisposal && e.VoidedAt == null).ToListAsync())
                e.VoidedAt = clock.UtcNow;
            if (asset.DisposalVoucherId is { } vid) await engine.ReverseCoreAsync(ctx, vid, date, $"Hủy ghi giảm {asset.Code}: {reason}", allowModuleOwned: true);
            asset.Status = AssetStatus.Active;
            asset.DisposalDate = null;
            asset.DisposalVoucherId = null;
            asset.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "asset.dispose_reverse", "asset", asset.Id.ToString(), new { reason, date }, asset.RowVersion);
            await db.SaveChangesAsync();
            return asset;
        });
    }

    // ------------------------------------------------------------------ queries

    public async Task<IReadOnlyList<AssetRegisterRow>> RegisterAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var assets = await db.LongTermAssets.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Status != AssetStatus.Cancelled)
            .OrderBy(a => a.Code).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).ToDictionaryAsync(a => a.Id, a => a.Code);
        var entries = await ActiveEntriesAsync(ctx.CompanyId);
        // Ghi tăng: the voucher of the first cost line; ghi giảm: the disposal voucher (S21-DN).
        var assetIds = assets.Select(a => a.Id).ToList();
        var firstSources = (await (from s in db.AssetCostSources
                                   join l in db.JournalLines on s.JournalLineId equals l.Id
                                   join e in db.JournalEntries on l.JournalEntryId equals e.Id
                                   where assetIds.Contains(s.AssetId)
                                   select new { s.AssetId, e.VoucherNumber, l.PostingDate }).AsNoTracking().ToListAsync())
            .GroupBy(x => x.AssetId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.PostingDate).First());
        var disposalIds = assets.Where(a => a.DisposalVoucherId != null).Select(a => a.DisposalVoucherId!.Value).ToList();
        var disposals = await db.Vouchers.AsNoTracking().Where(v => disposalIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => new { v.Number, v.Description });
        return assets.Select(a =>
        {
            var acc = a.Status == AssetStatus.Draft ? 0 : a.OpeningAccumulated + entries.Where(e => e.AssetId == a.Id && e.PeriodEnd <= asOf).Sum(e => e.Amount);
            var src = firstSources.GetValueOrDefault(a.Id);
            var disposal = a.DisposalVoucherId is { } dv ? disposals.GetValueOrDefault(dv) : null;
            return new AssetRegisterRow(a.Id, a.Code, a.Name, a.Class, a.Status, accounts[a.AssetAccountId], a.InServiceDate, a.ScheduleStartDate, a.UsefulLifeMonths,
                a.OriginalCost, acc, a.OriginalCost - acc, a.DisposalDate, a.ResidualValue,
                src?.VoucherNumber, src?.PostingDate, disposal?.Number,
                disposal?.Description is { } text && text.StartsWith($"Ghi giảm {a.Code} – ", StringComparison.Ordinal) ? text[($"Ghi giảm {a.Code} – ".Length)..] : disposal?.Description,
                a.UsedBy);
        }).ToList();
    }

    public async Task<AssetDetail> DetailAsync(SessionContext ctx, Guid assetId)
    {
        ctx.Require(Permissions.ReportView);
        var asset = await LoadAsync(ctx, assetId, tracking: false);
        var ids = asset.CostSources.Select(s => s.JournalLineId).ToList();
        var lines = await (from l in db.JournalLines
                           join e in db.JournalEntries on l.JournalEntryId equals e.Id
                           join acc in db.Accounts on l.AccountId equals acc.Id
                           where ids.Contains(l.Id)
                           select new { l.Id, e.VoucherNumber, l.PostingDate, acc.Code, l.Amount }).AsNoTracking().ToListAsync();
        var sources = asset.CostSources.Select(s =>
        {
            var l = lines.First(x => x.Id == s.JournalLineId);
            return new AssetSourceRow(s.JournalLineId, l.VoucherNumber, l.PostingDate, l.Code, l.Amount, s.Amount);
        }).ToList();
        var entries = await db.DepreciationEntries.AsNoTracking().Where(e => e.AssetId == asset.Id).OrderBy(e => e.PeriodEnd).ThenBy(e => e.CreatedAt).ToListAsync();
        return new AssetDetail(asset, sources, entries, Schedule(asset, entries.Where(e => e.VoidedAt == null).ToList(), await DecimalsAsync(ctx.CompanyId)));
    }

    /// <summary>Monthly plan from the schedule start to the end of life, next to what has actually been booked.</summary>
    private static List<AssetScheduleRow> Schedule(LongTermAsset a, List<DepreciationEntry> active, int dp)
    {
        var rows = new List<AssetScheduleRow>();
        if (a.ScheduleBase <= 0) return rows;
        var monthStart = new DateOnly(a.ScheduleStartDate.Year, a.ScheduleStartDate.Month, 1);
        decimal previous = 0;
        for (var i = 0; i <= a.ScheduleMonths && previous < a.ScheduleBase; i++)
        {
            var start = monthStart.AddMonths(i);
            var end = start.AddMonths(1).AddDays(-1);
            var cumulative = DepreciationCalculator.CumulativeTarget(a.ScheduleBase, a.ScheduleMonths, a.ScheduleStartDate, end, a.Proration, dp);
            var booked = active.Where(e => e.PeriodEnd >= start && e.PeriodEnd <= end).ToList();
            rows.Add(new AssetScheduleRow(end, cumulative - previous, cumulative, booked.Count == 0 ? null : booked.Sum(e => e.Amount)));
            previous = cumulative;
        }
        return rows;
    }

    /// <summary>Posted debit lines on asset accounts still (partly) free to assign to an asset card.</summary>
    public async Task<IReadOnlyList<AssetCostCandidate>> CostCandidatesAsync(SessionContext ctx, Guid assetAccountId)
    {
        ctx.Require(Permissions.ReportView);
        var lines = await (from l in db.JournalLines
                           join e in db.JournalEntries on l.JournalEntryId equals e.Id
                           join v in db.Vouchers on e.VoucherId equals v.Id
                           join acc in db.Accounts on l.AccountId equals acc.Id
                           where l.CompanyId == ctx.CompanyId && l.AccountId == assetAccountId && l.Side == Side.Debit && v.Status == VoucherStatus.Posted
                           orderby l.PostingDate, e.VoucherNumber, l.LineNo
                           select new { l.Id, e.VoucherNumber, l.PostingDate, acc.Code, l.Description, l.Amount }).AsNoTracking().ToListAsync();
        var ids = lines.Select(l => l.Id).ToList();
        var used = await (from s in db.AssetCostSources
                          join a in db.LongTermAssets on s.AssetId equals a.Id
                          where ids.Contains(s.JournalLineId) && (a.Status == AssetStatus.Active || a.Status == AssetStatus.Disposed)
                          select new { s.JournalLineId, s.Amount }).AsNoTracking().ToListAsync();
        return lines.Select(l => new AssetCostCandidate(l.Id, l.VoucherNumber, l.PostingDate, l.Code, l.Description, l.Amount,
                used.Where(u => u.JournalLineId == l.Id).Sum(u => u.Amount)))
            .Where(c => c.Assigned < c.LineAmount).ToList();
    }

    /// <summary>
    /// Register ↔ ledger at <paramref name="asOf"/>, per account (signed, debit positive): cost and remaining tool value on asset
    /// accounts, accumulated depreciation (negative) on accumulated accounts. A difference means postings on those accounts that
    /// no asset card explains (or the opening-balance voucher does not match the cards' opening accumulated depreciation).
    /// </summary>
    public async Task<IReadOnlyList<AssetReconcileRow>> ReconcileAsync(SessionContext ctx, DateOnly asOf)
    {
        ctx.Require(Permissions.ReportView);
        var assets = await db.LongTermAssets.AsNoTracking().Include(a => a.CostSources)
            .Where(a => a.CompanyId == ctx.CompanyId && (a.Status == AssetStatus.Active || a.Status == AssetStatus.Disposed)).ToListAsync();
        var sourceIds = assets.SelectMany(a => a.CostSources.Select(s => s.JournalLineId)).ToList();
        var sourceDates = await db.JournalLines.AsNoTracking().Where(l => sourceIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.PostingDate);
        var entries = await ActiveEntriesAsync(ctx.CompanyId);

        var register = new Dictionary<Guid, decimal>();
        void Add(Guid account, decimal amount) => register[account] = register.GetValueOrDefault(account) + amount;
        foreach (var a in assets)
        {
            Add(a.AssetAccountId, 0);
            if (a.AccumulatedAccountId is { } accId) Add(accId, 0);
            if (a.DisposalDate is { } d && d <= asOf) continue;
            var cost = a.CostSources.Where(s => sourceDates[s.JournalLineId] <= asOf).Sum(s => s.Amount);
            var depreciated = (a.ScheduleStartDate <= asOf.AddDays(1) ? a.OpeningAccumulated : 0)
                              + entries.Where(e => e.AssetId == a.Id && !e.IsDisposal && e.PeriodEnd <= asOf).Sum(e => e.Amount);
            if (a.AccumulatedAccountId is { } acc)
            {
                Add(a.AssetAccountId, cost);
                Add(acc, -depreciated);
            }
            else Add(a.AssetAccountId, cost - depreciated);
        }

        var accountIds = register.Keys.ToList();
        var sums = await db.JournalLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && accountIds.Contains(l.AccountId) && l.PostingDate <= asOf)
            .GroupBy(l => new { l.AccountId, l.Side }).Select(g => new { g.Key.AccountId, g.Key.Side, Total = g.Sum(x => x.Amount) }).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code);
        var accumulatedAccounts = assets.Where(a => a.AccumulatedAccountId is not null).Select(a => a.AccumulatedAccountId!.Value).ToHashSet();
        return register.Select(r =>
        {
            var ledger = sums.Where(s => s.AccountId == r.Key).Sum(s => s.Side == Side.Debit ? s.Total : -s.Total);
            var kind = accumulatedAccounts.Contains(r.Key) ? "Hao mòn lũy kế"
                : assets.Where(a => a.AssetAccountId == r.Key).All(a => a.Class == AssetClass.Tool) ? "Giá trị CCDC còn lại" : "Nguyên giá";
            return new AssetReconcileRow(r.Key, accounts[r.Key], kind, r.Value, ledger, ledger - r.Value);
        }).OrderBy(r => r.AccountCode, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<LongTermAsset> LoadAsync(SessionContext ctx, Guid id, bool tracking)
    {
        var q = db.LongTermAssets.Include(a => a.CostSources).Where(a => a.Id == id && a.CompanyId == ctx.CompanyId);
        if (!tracking) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync() ?? throw new DomainException(ErrorCodes.NotFound, "Tài sản không tồn tại.");
    }

    private async Task<Account> PostableAccountAsync(SessionContext ctx, Guid id)
    {
        var acc = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id)
                  ?? throw new DomainException(ErrorCodes.AccountNotFound, "Tài khoản không tồn tại.");
        if (acc.CompanyId != ctx.CompanyId) throw new DomainException(ErrorCodes.AccountWrongCompany, $"Tài khoản {acc.Code} không thuộc công ty.");
        if (!acc.IsPostable) throw new DomainException(ErrorCodes.AccountNotPostable, $"Tài khoản {acc.Code} không phải tài khoản chi tiết.");
        if (!acc.IsActive) throw new DomainException(ErrorCodes.AccountInactive, $"Tài khoản {acc.Code} đã ngừng sử dụng.");
        return acc;
    }

    private Task<DepreciationRun?> LastActiveRunAsync(Guid companyId)
        => db.DepreciationRuns.AsNoTracking().Where(r => r.CompanyId == companyId && r.Status == DepreciationRunStatus.Active)
            .OrderByDescending(r => r.PeriodStart).FirstOrDefaultAsync();

    private async Task<Dictionary<Guid, decimal>> BookedAsync(Guid companyId, List<Guid> assetIds)
        => (await db.DepreciationEntries.AsNoTracking().Where(e => e.CompanyId == companyId && assetIds.Contains(e.AssetId) && e.VoidedAt == null)
                .Select(e => new { e.AssetId, e.Amount }).ToListAsync())
            .GroupBy(e => e.AssetId).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

    private Task<List<DepreciationEntry>> ActiveEntriesAsync(Guid companyId)
        => db.DepreciationEntries.AsNoTracking().Where(e => e.CompanyId == companyId && e.VoidedAt == null).ToListAsync();

    private async Task<int> DecimalsAsync(Guid companyId)
    {
        var currency = await db.Companies.Where(c => c.Id == companyId).Select(c => c.FunctionalCurrency).FirstAsync();
        return await db.Currencies.Where(c => c.Code == currency).Select(c => c.DecimalPlaces).FirstAsync();
    }

    private async Task<AccountingPeriod> RequireOpenPeriodAsync(Guid companyId, DateOnly date)
    {
        var p = await db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.StartDate <= date && x.EndDate >= date)
                ?? throw new DomainException(ErrorCodes.PeriodNotFound, $"Không có kỳ kế toán chứa ngày {date:dd/MM/yyyy}.");
        if (p.Status == PeriodStatus.Locked) throw new DomainException(ErrorCodes.PeriodLocked, $"Kỳ {p.StartDate:MM/yyyy} đã khóa sổ.");
        return p;
    }
}
