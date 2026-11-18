using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Common;
using Hak.Domain.Inventory;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Inventory;

public sealed record MeasurementInput(Guid DocumentId, int LineNo, decimal DrynessPercent, EvidenceType EvidenceType, string EvidenceNo,
    DateOnly EvidenceDate, string? EvidenceIssuer = null, decimal? MeasuredMt = null, string? Note = null);

public sealed record DrynessSummaryRow(Guid WarehouseId, string WarehouseCode, Guid ItemId, string ItemCode,
    decimal OpeningMt, decimal OpeningBdmt, decimal InMt, decimal InBdmt, decimal OutMt, decimal OutBdmt, decimal ClosingMt, decimal ClosingBdmt,
    decimal UnmeasuredInMt, decimal UnmeasuredOutMt);

public sealed record PendingMeasurement(Guid DocumentId, string DocumentNumber, StockDocumentType Type, DateOnly PostingDate, int LineNo,
    Guid ItemId, string ItemCode, decimal Quantity, MeasurementKind ExpectedKind);

public sealed record MeasurementView(Guid Id, string DocumentNumber, int LineNo, MeasurementKind Kind, decimal MeasuredMt, decimal LineMt,
    decimal DrynessPercent, decimal Bdmt, string MethodCode, EvidenceType EvidenceType, string EvidenceNo, string? EvidenceIssuer, DateOnly EvidenceDate,
    DateTime CreatedAt, DateTime? SupersededAt, string? SupersedeReason);

/// <summary>
/// Wood-chip MT/BDMT tracking (master prompt §15). BDMT = MT × dryness% / 100 under an approved method.
/// Measurements are recorded per stock-document line, may arrive after posting (ship certificates come later),
/// are never edited (superseded with a reason) and have no automatic GL effect: BDMT differences are reported,
/// not booked as exchange differences or gains.
/// </summary>
public sealed class DrynessService(HakDbContext db, AuditService audit, IClock clock)
{
    public async Task<DrynessMethod> CreateMethodAsync(SessionContext ctx, string code, string description, int drynessDecimals, int bdmtDecimals,
        DateOnly effectiveFrom, string approvalNote)
    {
        ctx.Require(Permissions.ConfigManage);
        if (string.IsNullOrWhiteSpace(approvalNote)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi căn cứ phê duyệt phương pháp quy đổi.");
        if (drynessDecimals is < 0 or > 4 || bdmtDecimals is < 0 or > 4) throw new DomainException(ErrorCodes.ValidationFailed, "Số chữ số thập phân từ 0 đến 4.");
        return await db.InTransactionAsync(async () =>
        {
            if (await db.DrynessMethods.AnyAsync(m => m.CompanyId == ctx.CompanyId && m.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Phương pháp {code} đã tồn tại.");
            var m = new DrynessMethod
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Description = description, DrynessDecimals = drynessDecimals, BdmtDecimals = bdmtDecimals,
                EffectiveFrom = effectiveFrom, ApprovalNote = approvalNote, ApprovedBy = ctx.UserId, ApprovedAt = clock.UtcNow,
            };
            db.DrynessMethods.Add(m);
            await audit.AppendAsync(ctx.CompanyId, ctx, "dryness_method.create", "dryness_method", m.Id.ToString(),
                new { code, description, drynessDecimals, bdmtDecimals, effectiveFrom, approvalNote, formula = "BDMT = MT × độ khô% / 100" });
            await db.SaveChangesAsync();
            return m;
        });
    }

    public async Task SetItemTracksDrynessAsync(SessionContext ctx, Guid itemId, bool tracks)
    {
        ctx.Require(Permissions.CatalogManage);
        await db.InTransactionAsync(async () =>
        {
            var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.CompanyId == ctx.CompanyId)
                       ?? throw new DomainException(ErrorCodes.NotFound, "Vật tư không tồn tại.");
            if (!tracks && await db.StockMeasurements.AnyAsync(m => m.ItemId == itemId))
                throw new DomainException(ErrorCodes.ValidationFailed, "Vật tư đã có kết quả đo độ khô; không tắt theo dõi BDMT được.");
            item.TracksDryness = tracks;
            await audit.AppendAsync(ctx.CompanyId, ctx, "item.tracks_dryness", "item", item.Id.ToString(), new { tracks });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Records the dryness of one line. Issue lines are shipments: the evidence must be a certificate (chứng thư).
    /// Allowed on Draft or Posted documents (a certificate usually arrives after loading).
    /// </summary>
    public Task<StockMeasurement> RecordAsync(SessionContext ctx, MeasurementInput input)
        => RecordCoreAsync(ctx, input, supersedeId: null, reason: null);

    /// <summary>Replaces the active measurement of a line (e.g. corrected certificate). The old row stays in history.</summary>
    public Task<StockMeasurement> SupersedeAsync(SessionContext ctx, Guid measurementId, MeasurementInput input, string reason)
    {
        ctx.Require(Permissions.StockDocument);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi lý do thay thế kết quả đo.");
        return RecordCoreAsync(ctx, input, measurementId, reason);
    }

    private async Task<StockMeasurement> RecordCoreAsync(SessionContext ctx, MeasurementInput input, Guid? supersedeId, string? reason)
    {
        ctx.Require(Permissions.StockDocument);
        if (string.IsNullOrWhiteSpace(input.EvidenceNo)) throw new DomainException(ErrorCodes.ValidationFailed, "Phải nhập số chứng thư / phiếu đo.");
        return await db.InTransactionAsync(async () =>
        {
            var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == input.DocumentId && d.CompanyId == ctx.CompanyId)
                      ?? throw new DomainException(ErrorCodes.NotFound, "Phiếu kho không tồn tại.");
            if (doc.Status is not (StockDocumentStatus.Draft or StockDocumentStatus.Posted) || doc.ReversesDocumentId is not null)
                throw new DomainException(ErrorCodes.InvalidStateTransition, $"Phiếu {doc.Number} ở trạng thái {doc.Status}; không ghi nhận kết quả đo.");
            var line = doc.Lines.FirstOrDefault(l => l.LineNo == input.LineNo)
                       ?? throw new DomainException(ErrorCodes.NotFound, $"Phiếu {doc.Number} không có dòng {input.LineNo}.");
            var item = await db.Items.AsNoTracking().FirstAsync(i => i.Id == line.ItemId);
            if (!item.TracksDryness) throw new DomainException(ErrorCodes.ValidationFailed, $"Vật tư {item.Code} không theo dõi độ khô/BDMT.");

            var kind = doc.Type switch { StockDocumentType.Receipt => MeasurementKind.Receipt, StockDocumentType.Issue => MeasurementKind.Shipment, _ => MeasurementKind.Other };
            if (kind == MeasurementKind.Shipment && input.EvidenceType != EvidenceType.Certificate)
                throw new DomainException(ErrorCodes.ValidationFailed, "Độ khô khi xuất hàng lấy theo đo ở tàu: chứng từ phải là chứng thư.");

            var date = doc.PostingDate;
            var method = await db.DrynessMethods.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && m.EffectiveFrom <= date)
                             .OrderByDescending(m => m.EffectiveFrom).FirstOrDefaultAsync()
                         ?? throw new DomainException(ErrorCodes.CostingPolicyMissing, $"Chưa có phương pháp quy đổi MT→BDMT được phê duyệt hiệu lực ngày {date:dd/MM/yyyy}.");
            var mt = input.MeasuredMt ?? line.Quantity;
            var bdmt = DrynessCalculator.Bdmt(mt, input.DrynessPercent, method);

            var active = await db.StockMeasurements.FirstOrDefaultAsync(m => m.CompanyId == ctx.CompanyId && m.DocumentId == doc.Id && m.LineNo == line.LineNo && m.SupersededAt == null);
            if (supersedeId is null && active is not null)
                throw new DomainException(ErrorCodes.DuplicateSource, $"Dòng {line.LineNo} của phiếu {doc.Number} đã có kết quả đo; dùng chức năng thay thế có ghi lý do.");
            if (supersedeId is not null)
            {
                if (active is null || active.Id != supersedeId) throw new DomainException(ErrorCodes.NotFound, "Kết quả đo cần thay thế không phải kết quả đang hiệu lực của dòng này.");
                active.SupersededAt = clock.UtcNow;
                active.SupersededBy = ctx.UserId;
                active.SupersedeReason = reason;
                await db.SaveChangesAsync(); // free the "one active per line" slot before inserting the replacement
            }

            var m = new StockMeasurement
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, DocumentId = doc.Id, LineNo = line.LineNo, ItemId = item.Id, Kind = kind, MeasuredMt = mt,
                DrynessPercent = input.DrynessPercent, Bdmt = bdmt, MethodId = method.Id, EvidenceType = input.EvidenceType, EvidenceNo = input.EvidenceNo.Trim(),
                EvidenceIssuer = input.EvidenceIssuer, EvidenceDate = input.EvidenceDate, Note = input.Note, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
            };
            db.StockMeasurements.Add(m);
            await audit.AppendAsync(ctx.CompanyId, ctx, supersedeId is null ? "dryness.record" : "dryness.supersede", "stock_measurement", m.Id.ToString(),
                new { doc.Number, line.LineNo, mt, input.DrynessPercent, bdmt, method = method.Code, input.EvidenceType, input.EvidenceNo, replaced = supersedeId, reason });
            await db.SaveChangesAsync();
            return m;
        });
    }

    /// <summary>MT and BDMT in/out/closing per warehouse and item; lines without a measurement are reported, never guessed.</summary>
    public async Task<IReadOnlyList<DrynessSummaryRow>> SummaryAsync(SessionContext ctx, DateOnly from, DateOnly to, Guid? warehouseId = null)
    {
        ctx.Require(Permissions.ReportView);
        var tracked = await db.Items.AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId && i.TracksDryness).ToDictionaryAsync(i => i.Id);
        var ids = tracked.Keys.ToList();
        var q = db.StockMovements.AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId && ids.Contains(m.ItemId) && m.MovementDate <= to);
        if (warehouseId is not null) q = q.Where(m => m.WarehouseId == warehouseId);
        var moves = await q.ToListAsync();
        var bdmt = await BdmtByMovementAsync(ctx.CompanyId, moves);
        var whs = await db.Warehouses.AsNoTracking().Where(w => w.CompanyId == ctx.CompanyId).ToDictionaryAsync(w => w.Id);

        return moves.GroupBy(m => (m.WarehouseId, m.ItemId)).Select(g =>
        {
            decimal B(StockMovement m) => bdmt.TryGetValue(m.Id, out var b) ? b : 0;
            var before = g.Where(m => m.MovementDate < from).ToList();
            var ins = g.Where(m => m.MovementDate >= from && m.Quantity > 0).ToList();
            var outs = g.Where(m => m.MovementDate >= from && m.Quantity < 0).ToList();
            var om = before.Sum(m => m.Quantity); var ob = before.Sum(B);
            var im = ins.Sum(m => m.Quantity); var ib = ins.Sum(B);
            var xm = -outs.Sum(m => m.Quantity); var xb = -outs.Sum(B);
            return new DrynessSummaryRow(g.Key.WarehouseId, whs[g.Key.WarehouseId].Code, g.Key.ItemId, tracked[g.Key.ItemId].Code,
                om, ob, im, ib, xm, xb, om + im - xm, ob + ib - xb,
                g.Where(m => m.Quantity > 0 && !bdmt.ContainsKey(m.Id)).Sum(m => m.Quantity),
                -g.Where(m => m.Quantity < 0 && !bdmt.ContainsKey(m.Id)).Sum(m => m.Quantity));
        }).OrderBy(r => r.WarehouseCode, StringComparer.Ordinal).ThenBy(r => r.ItemCode, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Draft or posted lines of dryness-tracked items still waiting for a measurement: receipts measured before posting
    /// (purchases priced per BDMT, customer returns) and shipments awaiting the certificate.
    /// </summary>
    public async Task<IReadOnlyList<PendingMeasurement>> PendingAsync(SessionContext ctx)
    {
        ctx.Require(Permissions.ReportView);
        var rows = await (from d in db.StockDocuments
                          join l in db.StockDocumentLines on d.Id equals l.DocumentId
                          join i in db.Items on l.ItemId equals i.Id
                          where d.CompanyId == ctx.CompanyId && (d.Status == StockDocumentStatus.Posted || d.Status == StockDocumentStatus.Draft) && d.ReversesDocumentId == null && i.TracksDryness
                                && !db.StockMeasurements.Any(m => m.DocumentId == d.Id && m.LineNo == l.LineNo && m.SupersededAt == null)
                          orderby d.PostingDate, d.Number, l.LineNo
                          select new { DocId = d.Id, d.Number, d.Type, d.PostingDate, l.LineNo, ItemId = i.Id, ItemCode = i.Code, l.Quantity })
            .AsNoTracking().ToListAsync();
        return rows.Select(r => new PendingMeasurement(r.DocId, r.Number, r.Type, r.PostingDate, r.LineNo, r.ItemId, r.ItemCode, r.Quantity,
            r.Type switch { StockDocumentType.Receipt => MeasurementKind.Receipt, StockDocumentType.Issue => MeasurementKind.Shipment, _ => MeasurementKind.Other })).ToList();
    }

    /// <summary>All measurements (active and superseded) for audit, newest first.</summary>
    public async Task<IReadOnlyList<MeasurementView>> HistoryAsync(SessionContext ctx, Guid? documentId = null)
    {
        ctx.Require(Permissions.ReportView);
        var q = from m in db.StockMeasurements
                join d in db.StockDocuments on m.DocumentId equals d.Id
                join l in db.StockDocumentLines on new { m.DocumentId, m.LineNo } equals new { l.DocumentId, l.LineNo }
                join me in db.DrynessMethods on m.MethodId equals me.Id
                where m.CompanyId == ctx.CompanyId
                select new { m, d.Number, LineMt = l.Quantity, MethodCode = me.Code };
        if (documentId is not null) q = q.Where(x => x.m.DocumentId == documentId);
        var rows = await q.AsNoTracking().ToListAsync();
        return rows.OrderByDescending(x => x.m.CreatedAt).Select(x => new MeasurementView(x.m.Id, x.Number, x.m.LineNo, x.m.Kind, x.m.MeasuredMt, x.LineMt,
            x.m.DrynessPercent, x.m.Bdmt, x.MethodCode, x.m.EvidenceType, x.m.EvidenceNo, x.m.EvidenceIssuer, x.m.EvidenceDate, x.m.CreatedAt, x.m.SupersededAt, x.m.SupersedeReason)).ToList();
    }

    /// <summary>
    /// Signed BDMT per movement from the active measurement of its document line. Transfers carry the same BDMT
    /// out of one warehouse and into the other; reversal movements mirror the original line's measurement.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> BdmtByMovementAsync(Guid companyId, IReadOnlyList<StockMovement> moves)
    {
        var originals = moves.Where(m => m.ReversesMovementId is not null).Select(m => m.ReversesMovementId!.Value).ToList();
        var originalMoves = await db.StockMovements.AsNoTracking().Where(m => originals.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        var docIds = moves.Select(m => m.DocumentId).Concat(originalMoves.Values.Select(m => m.DocumentId)).Distinct().ToList();
        var measures = await db.StockMeasurements.AsNoTracking().Where(m => m.CompanyId == companyId && docIds.Contains(m.DocumentId) && m.SupersededAt == null)
            .ToDictionaryAsync(m => (m.DocumentId, m.LineNo), m => m.Bdmt);
        var result = new Dictionary<Guid, decimal>();
        foreach (var mv in moves)
        {
            var key = mv.ReversesMovementId is { } o && originalMoves.TryGetValue(o, out var orig) ? (orig.DocumentId, orig.LineNo) : (mv.DocumentId, mv.LineNo);
            if (measures.TryGetValue(key, out var b)) result[mv.Id] = Math.Sign(mv.Quantity) * b;
        }
        return result;
    }
}
