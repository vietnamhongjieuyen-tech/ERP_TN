using Hak.Application.Accounting;
using Hak.Application.Rules;
using Hak.Domain.Common;
using Hak.Domain.Rules;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;
using static Hak.Integration.Tests.Phase2Fixture;

namespace Hak.Integration.Tests;

public class RulesCashClosingTests
{
    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<DomainException>(act)).Code;
    private static readonly DateOnly Mar10 = new(2026, 3, 10);

    [Fact]
    public async Task RuleActivation_RequiresPassingTests_AndVersioningRetiresPrevious()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await s.Rules.SetRoleMappingAsync(h.Admin, "AR_TRADE", "AR");
        await s.Rules.SetRoleMappingAsync(h.Admin, "CASH_DEFAULT", "CASH");

        var noTests = await s.Rules.CreateDraftAsync(h.Admin, "CR-X", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, new(2026, 1, 1), null, CustomerReceiptDef, "[]");
        Assert.False((await s.Rules.ActivateAsync(h.Admin, noTests.Id)).Passed);

        var wrong = await s.Rules.CreateDraftAsync(h.Admin, "CR-X", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, new(2026, 1, 1), null, CustomerReceiptDef,
            CustomerReceiptTests.Replace("\"account_code\":\"AR\",\"amount\":\"1000000\"", "\"account_code\":\"REV\",\"amount\":\"1000000\""));
        var failed = await s.Rules.ActivateAsync(h.Admin, wrong.Id);
        Assert.False(failed.Passed);
        Assert.Equal(RuleStatus.Draft, (await s.Db.PostingRules.AsNoTracking().SingleAsync(r => r.Id == wrong.Id)).Status);
        Assert.Equal(ErrorCodes.RuleMissing, await CodeOf(() => s.Rules.ResolveAsync(h.CompanyId, VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", Mar10)));

        var v3 = await s.Rules.CreateDraftAsync(h.Admin, "CR-X", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, new(2026, 1, 1), null, CustomerReceiptDef, CustomerReceiptTests);
        Assert.True((await s.Rules.ActivateAsync(h.Admin, v3.Id)).Passed);
        var v4 = await s.Rules.CreateDraftAsync(h.Admin, "CR-X", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, new(2026, 1, 1), null, CustomerReceiptDef, CustomerReceiptTests);
        Assert.True((await s.Rules.ActivateAsync(h.Admin, v4.Id)).Passed);
        Assert.Equal(RuleStatus.Retired, (await s.Db.PostingRules.AsNoTracking().SingleAsync(r => r.Id == v3.Id)).Status);
        Assert.Equal(4, (await s.Rules.ResolveAsync(h.CompanyId, VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", Mar10)).Version);

        var active = await s.Db.PostingRules.SingleAsync(r => r.Id == v4.Id);
        active.DefinitionJson = ExpenseDef;
        Assert.Equal(ErrorCodes.LedgerImmutable, (await Assert.ThrowsAsync<DomainException>(() => s.Db.SaveChangesAsync())).Code);
        s.Db.ChangeTracker.Clear();
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Rules.CreateDraftAsync(h.Preparer, "Z", VoucherTypes.CashReceipt, "E", 1, new(2026, 1, 1), null, CustomerReceiptDef, "[]")));
    }

    [Theory]
    [InlineData("""{"lines":[{"side":"D","account":"ROLE:X"}]}""")]                                   // unbalanced templates
    [InlineData("""{"lines":[{"side":"D","account":"SQL:DROP"},{"side":"C","account":"ROLE:X"}]}""")] // unknown expression
    [InlineData("""{"lines":[{"side":"X","account":"ROLE:X"},{"side":"C","account":"ROLE:X"}]}""")]
    [InlineData("not json")]
    public async Task InvalidRuleDefinitions_AreRejected(string def)
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        Assert.Equal(ErrorCodes.RuleInvalid, await CodeOf(() => s.Rules.CreateDraftAsync(h.Admin, "BAD", VoucherTypes.CashPayment, "E", 1, new(2026, 1, 1), null, def, "[]")));
    }

    [Fact]
    public async Task UnmappedRole_And_AmbiguousRules_AreReported()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var r = await s.Rules.CreateDraftAsync(h.Admin, "CR-1", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, new(2026, 1, 1), null, CustomerReceiptDef,
            """[{"name":"vai trò chưa ánh xạ","document":{"partner_code":"KH01","main_account_code":"CASH","lines":[{"amount":"1"}]},"expect_error":"RULE_ROLE_UNMAPPED"}]""");
        Assert.True((await s.Rules.ActivateAsync(h.Admin, r.Id)).Passed); // a negative test case passing is a valid test

        Assert.Equal(ErrorCodes.RuleRoleUnmapped, await CodeOf(() => s.Documents.CreateAsync(h.Preparer, Receipt(h, 1, Mar10))));

        await ConfigureAsync(h, s); // adds CR-CUST with the same priority 10; CR-1 has priority 1
        var other = await s.Rules.CreateDraftAsync(h.Admin, "CR-2", VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 10, new(2026, 1, 1), null, CustomerReceiptDef, CustomerReceiptTests);
        Assert.True((await s.Rules.ActivateAsync(h.Admin, other.Id)).Passed);
        Assert.Equal(ErrorCodes.RuleAmbiguous, await CodeOf(() => s.Documents.CreateAsync(h.Preparer, Receipt(h, 1, Mar10))));
    }

    [Fact]
    public async Task CashDocument_RecordsRuleAndFollowsWorkflow()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await ConfigureAsync(h, s);
        var v = await s.Documents.CreateAsync(h.Preparer, Receipt(h, 2_000_000, Mar10));
        Assert.Equal((VoucherTypes.CashReceipt, "CUSTOMER_RECEIPT", 1, VoucherStatus.Draft), (v.VoucherType, v.BusinessEvent, v.PostingRuleVersion, v.Status));
        Assert.StartsWith("CR2026-", v.Number);

        // Rule-driven vouchers cannot be edited as free-form GL entries, nor created as such.
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Engine.UpdateDraftAsync(h.Preparer, v.Id, v.RowVersion, h.Sale(1) with { VoucherType = VoucherTypes.CashReceipt })));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Engine.CreateDraftAsync(h.Preparer, h.Sale(1) with { VoucherType = VoucherTypes.CashReceipt })));
        var updated = await s.Documents.UpdateAsync(h.Preparer, v.Id, v.RowVersion, Receipt(h, 2_500_000, Mar10));
        Assert.Equal(2_500_000m, updated.TotalDebit);

        // Mã phân tích of a document line lands on the non-cash side of the generated entry, never on the cash account.
        var code = await new Hak.Application.Setup.WorkflowSetupService(s.Db, s.Audit, h.Clock).CreateAnalysisCodeAsync(h.Preparer, "Bộ phận", "VP", "Văn phòng");
        var doc = Expense(h, 100_000, Mar10);
        var tagged = await s.Documents.CreateAsync(h.Preparer, doc with { Lines = [doc.Lines[0] with { AnalysisCodeId = code.Id }] });
        Assert.Equal(code.Id, tagged.Lines.Single(l => l.AccountId == h.Acc["EXP"]).AnalysisCodeId);
        Assert.Null(tagged.Lines.Single(l => l.AccountId == h.Acc["CASH"]).AnalysisCodeId);

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => s.Documents.CreateAsync(h.Auditor, Receipt(h, 1, Mar10))));
        Assert.Equal(ErrorCodes.ValidationFailed, await CodeOf(() => s.Documents.CreateAsync(h.Preparer, Receipt(h, 1, Mar10) with { MainAccountId = h.Acc["BANK"] })));
    }

    [Fact]
    public async Task NegativeCash_IsBlocked_IncludingBackdatedPayments()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await ConfigureAsync(h, s);
        var early = await s.Documents.CreateAsync(h.Preparer, Expense(h, 1_000_000, Mar10));
        Assert.Equal(ErrorCodes.NegativeBalance, await CodeOf(() => s.Engine.SubmitAsync(h.Preparer, early.Id)));

        await PostDocAsync(h, s, Receipt(h, 2_000_000, Mar10));
        await PostDocAsync(h, s, Expense(h, 1_500_000, new(2026, 3, 15)));
        // A payment dated before the receipt would make the fund negative on 05/03.
        var backdated = await s.Documents.CreateAsync(h.Preparer, Expense(h, 400_000, new(2026, 3, 5)));
        Assert.Equal(ErrorCodes.NegativeBalance, await CodeOf(() => s.Engine.SubmitAsync(h.Preparer, backdated.Id)));
        // 500,000 left after 15/03: a later payment of 500,000 is fine, 500,001 is not.
        await PostDocAsync(h, s, Expense(h, 500_000, new(2026, 3, 20)));
        Assert.Equal(0m, await s.Ledger.AccountBalanceAsync(h.Auditor, h.Acc["CASH"], new(2026, 3, 31)));
    }

    [Fact]
    public async Task NegativeBank_WithWarnPolicy_WarnsButDoesNotBlock()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await ConfigureAsync(h, s);
        var pay = await s.Documents.CreateAsync(h.Preparer, new BusinessDocument(VoucherTypes.BankPayment, "SUPPLIER_PAYMENT", Mar10, Mar10, "Trả NCC",
            h.Partner["NCC01"], h.Acc["BANK"], [new BusinessDocumentLine(700)]));
        Assert.Contains(await s.Engine.ValidateAsync(h.Preparer, pay.Id), e => e.Code == ErrorCodes.NegativeBalanceWarning);
        await s.Engine.SubmitAsync(h.Preparer, pay.Id);
        await s.Engine.ApproveAsync(h.Approver, pay.Id);
        await s.Engine.PostAsync(h.Approver, pay.Id);
    }

    [Fact]
    public async Task InternalTransfer_NeverTouchesRevenueOrExpense()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        await ConfigureAsync(h, s);
        await PostDocAsync(h, s, Receipt(h, 1_000_000, Mar10, main: "BANK"));
        await PostDocAsync(h, s, new BusinessDocument(VoucherTypes.BankPayment, "INTERNAL_TRANSFER", Mar10, Mar10, "Chuyển tiền nội bộ",
            null, h.Acc["BANK"], [new BusinessDocumentLine(400_000, h.Acc["BANK2"])]));
        Assert.Equal(ErrorCodes.RuleConstraint, await CodeOf(() => s.Documents.CreateAsync(h.Preparer, new BusinessDocument(VoucherTypes.BankPayment,
            "INTERNAL_TRANSFER", Mar10, Mar10, "Sai", null, h.Acc["BANK"], [new BusinessDocumentLine(1, h.Acc["REV"])]))));

        var tb = await s.Ledger.TrialBalanceAsync(h.Auditor, new(2026, 3, 1), new(2026, 3, 31));
        Assert.DoesNotContain(tb.Rows, r => r.AccountCode is "REV" or "EXP");
        Assert.Equal(400_000m, tb.Rows.Single(r => r.AccountCode == "BANK2").ClosingDebit);
    }

    [Fact]
    public async Task Closing_MovesResultAccounts_IsIdempotent_AndBlockedByPendingVouchers()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var set = await s.Closing.CreateRuleSetAsync(h.Admin, "CLOSE-TEST", new(2026, 1, 1), null,
        [
            new ClosingStepInput("Kết chuyển doanh thu", "REV", "RESULT"),
            new ClosingStepInput("Kết chuyển chi phí", "EXP", "RESULT"),
            new ClosingStepInput("Kết chuyển lãi lỗ", "RESULT", "RETAINED"),
        ]);
        var march = await s.Db.AccountingPeriods.SingleAsync(p => p.PeriodNo == 3);
        Assert.Equal(ErrorCodes.RuleMissing, await CodeOf(() => s.Closing.GenerateAsync(h.Preparer, march.Id)));
        await s.Closing.ActivateRuleSetAsync(h.Admin, set.Id);

        await h.PostedAsync(s, h.Sale(10_000_000));
        await h.PostedAsync(s, new VoucherInput(VoucherTypes.General, Mar10, Mar10, "Chi phí dịch vụ",
            [new(h.Acc["EXP"], Side.Debit, 3_000_000), new(h.Acc["AP"], Side.Credit, 3_000_000, h.Partner["NCC01"])]));

        var pending = await s.Engine.CreateDraftAsync(h.Preparer, h.Sale(1));
        await s.Engine.SubmitAsync(h.Preparer, pending.Id);
        Assert.Equal(ErrorCodes.PeriodHasPending, await CodeOf(() => s.Closing.GenerateAsync(h.Preparer, march.Id)));
        await s.Engine.RejectAsync(h.Approver, pending.Id, "Chưa đủ hồ sơ");

        var cls = await s.Closing.GenerateAsync(h.Preparer, march.Id);
        Assert.Equal(VoucherTypes.Closing, cls.VoucherType);
        Assert.Equal(ErrorCodes.DuplicateSource, await CodeOf(() => s.Closing.GenerateAsync(h.Preparer, march.Id)));
        await s.Engine.SubmitAsync(h.Preparer, cls.Id);
        await s.Engine.ApproveAsync(h.Approver, cls.Id);
        await s.Engine.PostAsync(h.Approver, cls.Id);
        Assert.Equal(ErrorCodes.AlreadyClosed, await CodeOf(() => s.Closing.GenerateAsync(h.Preparer, march.Id)));

        var end = new DateOnly(2026, 3, 31);
        foreach (var code in new[] { "REV", "EXP", "RESULT" })
            Assert.Equal(0m, await s.Ledger.AccountBalanceAsync(h.Auditor, h.Acc[code], end));
        Assert.Equal(-7_000_000m, await s.Ledger.AccountBalanceAsync(h.Auditor, h.Acc["RETAINED"], end));
        Assert.True((await s.Ledger.TrialBalanceAsync(h.Auditor, new(2026, 3, 1), end)).IsBalanced);
    }

    [Fact]
    public async Task Closing_CancelledDraftCanBeRegenerated()
    {
        using var h = await TestHost.CreateAsync();
        using var s = h.NewScope();
        var set = await s.Closing.CreateRuleSetAsync(h.Admin, "C", new(2026, 1, 1), null, [new ClosingStepInput("DT", "REV", "RESULT")]);
        await s.Closing.ActivateRuleSetAsync(h.Admin, set.Id);
        await h.PostedAsync(s, h.Sale(500));
        var march = await s.Db.AccountingPeriods.SingleAsync(p => p.PeriodNo == 3);
        var first = await s.Closing.GenerateAsync(h.Preparer, march.Id);
        await s.Engine.CancelAsync(h.Preparer, first.Id, "Tạo lại sau khi bổ sung chứng từ");
        await h.PostedAsync(s, h.Sale(250));
        var second = await s.Closing.GenerateAsync(h.Preparer, march.Id);
        Assert.Equal(750m, second.TotalDebit);
        Assert.Equal(ErrorCodes.RuleInvalid, await CodeOf(() => s.Closing.CreateRuleSetAsync(h.Admin, "BAD", new(2026, 1, 1), null, [new ClosingStepInput("x", "RES", "RESULT")])));
    }
}
