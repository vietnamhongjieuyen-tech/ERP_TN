using System.Text;
using Hak.Application.Accounting;
using Hak.Application.Security;
using Hak.Domain.Common;
using Hak.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hak.Integration.Tests;

public class AttachmentTests
{
    private static async Task<string> CodeOf(Func<Task> f) => (await Assert.ThrowsAsync<DomainException>(f)).Code;

    [Fact]
    public async Task Attachments_AddListDownloadRemove_NeverDeleted_AndTypeChecked()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var files = new VoucherAttachmentService(s.Db, s.Audit, h.Clock);
        var voucher = await h.PostedAsync(s, h.Sale(1_000_000));
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 hoa don thu nghiem");

        // Refusals: no create permission, unknown voucher, active or unknown content, empty, too large.
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => files.AddAsync(h.Auditor, voucher.Id, "hd.pdf", pdf)));
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => files.AddAsync(h.Preparer, Guid.NewGuid(), "hd.pdf", pdf)));
        foreach (var name in new[] { "trang.html", "anh.svg", "chay.exe", "khong-duoi", "" })
            Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => files.AddAsync(h.Preparer, voucher.Id, name, pdf)));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => files.AddAsync(h.Preparer, voucher.Id, "hd.pdf", [])));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => files.AddAsync(h.Preparer, voucher.Id, "hd.pdf", new byte[VoucherAttachmentService.MaxBytes + 1])));

        // A posted voucher still takes attachments; a path in the name is dropped; the same content twice is refused.
        var added = await files.AddAsync(h.Preparer, voucher.Id, @"C:\Scan\Hóa đơn 0001.PDF", pdf);
        Assert.Equal(("Hóa đơn 0001.PDF", (long)pdf.Length), (added.FileName, added.Size));
        Assert.Equal(ErrorCodes.DuplicateCode, await CodeOf(() => files.AddAsync(h.Preparer, voucher.Id, "ban-sao.pdf", pdf)));
        await files.AddAsync(h.Approver, voucher.Id, "hop-dong.jpg", [0xFF, 0xD8, 0xFF, 1, 2, 3]);
        Assert.Equal(2, (await files.ListAsync(h.Auditor, voucher.Id)).Count);

        var file = await files.DownloadAsync(h.Auditor, added.Id);
        Assert.Equal(("Hóa đơn 0001.PDF", "application/pdf"), (file.FileName, file.ContentType));
        Assert.Equal(pdf, file.Content);

        // Removing needs a reason, keeps the bytes and the row; afterwards only audit viewers can open it.
        Assert.Equal(ErrorCodes.ReasonRequired, await CodeOf(() => files.RemoveAsync(h.Preparer, added.Id, " ")));
        await files.RemoveAsync(h.Preparer, added.Id, "Đính nhầm chứng từ");
        Assert.Equal(ErrorCodes.InvalidStateTransition, await CodeOf(() => files.RemoveAsync(h.Preparer, added.Id, "lần 2")));
        var removed = (await files.ListAsync(h.Auditor, voucher.Id)).Single(a => a.Id == added.Id);
        Assert.True(removed.IsRemoved);
        Assert.Equal("Đính nhầm chứng từ", removed.RemoveReason);
        Assert.Equal(pdf, (await s.Db.VoucherAttachments.AsNoTracking().SingleAsync(a => a.Id == added.Id)).Content);
        Assert.Equal(pdf, (await files.DownloadAsync(h.Auditor, added.Id)).Content);                        // internal auditor: audit.view
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => files.DownloadAsync(h.Preparer, added.Id)));
        await files.AddAsync(h.Preparer, voucher.Id, "hd-dung.pdf", pdf);                                   // the same file may be attached again after removal

        // Another company's session sees nothing.
        var other = new SessionContext(h.PreparerId, Guid.NewGuid(), new HashSet<string> { Permissions.VoucherView, Permissions.VoucherCreate });
        Assert.Empty(await files.ListAsync(other, voucher.Id));
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => files.DownloadAsync(other, added.Id)));

        var actions = await s.Db.AuditLogs.Select(a => a.Action).ToListAsync();
        Assert.Contains("voucher.attach", actions);
        Assert.Contains("voucher.detach", actions);
        Assert.Null(await s.Audit.VerifyChainAsync(h.CompanyId));
    }
}
