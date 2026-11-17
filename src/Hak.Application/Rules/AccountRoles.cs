namespace Hak.Application.Rules;

/// <summary>Optional roles fall back to another role when unmapped (payroll expense groups → PAYROLL_EXPENSE_GENERAL).</summary>
public sealed record AccountRoleInfo(string Code, string Module, string Description, bool Optional = false);

/// <summary>
/// Every account role the modules book through. Roles keep legal account codes out of the program: each company maps a role
/// to an account of its own chart (verified against the regime by the chief accountant). Used by the setup screen to show
/// what still has to be mapped; a module refuses to post with RULE_ROLE_UNMAPPED when a role it needs is missing.
/// </summary>
public static class AccountRoles
{
    public static readonly IReadOnlyList<AccountRoleInfo> Known =
    [
        new("AR_TRADE", "Bán hàng / công nợ", "Phải thu khách hàng (hóa đơn bán, thu tiền)"),
        new("AP_TRADE", "Mua hàng / công nợ", "Phải trả người bán (hóa đơn mua, trả tiền)"),
        new("CASH_DEFAULT", "Thu chi", "Tài khoản tiền mặc định của quy tắc định khoản thu/chi"),
        new("EMPLOYEE_ADVANCE", "Thu chi", "Tạm ứng cho nhân viên (quy tắc chi tạm ứng / thu hoàn ứng, theo từng người)", Optional: true),
        new("GRNI", "Mua hàng / kho", "Hàng về chưa có hóa đơn (đối ứng phiếu nhập chờ hóa đơn, xuất trả nhà cung cấp)"),
        new("VAT_INPUT", "Thuế GTGT", "Thuế GTGT đầu vào được khấu trừ"),
        new("VAT_OUTPUT", "Thuế GTGT", "Thuế GTGT đầu ra phải nộp"),
        new("SALES_REVENUE", "Bán hàng", "Doanh thu bán hàng (mặc định khi dòng hóa đơn không chọn TK)"),
        new("SALES_RETURNS", "Bán hàng", "Hàng bán bị trả lại"),
        new("SALES_DISCOUNT", "Bán hàng", "Giảm giá / chiết khấu hàng bán"),
        new("COGS", "Kho / giá thành", "Giá vốn: chênh lệch giá mua phần đã xuất, chênh lệch trả hàng mua, chênh lệch giá thành phần đã xuất"),
        new("BANK_FEE", "Ngân hàng", "Chi phí phí ngân hàng — chỉ dùng để GỢI Ý TK khi tạo chứng từ từ dòng sao kê", Optional: true),
        new("BANK_INTEREST_INCOME", "Ngân hàng", "Lãi tiền gửi — chỉ dùng để GỢI Ý TK khi tạo chứng từ từ dòng sao kê", Optional: true),
        new("FX_REVALUATION", "Ngoại tệ", "Chênh lệch tỷ giá do đánh giá lại cuối kỳ"),
        new("FX_REALIZED_GAIN", "Ngoại tệ", "Lãi chênh lệch tỷ giá đã thực hiện (thu/chi ngoại tệ theo hóa đơn)"),
        new("FX_REALIZED_LOSS", "Ngoại tệ", "Lỗ chênh lệch tỷ giá đã thực hiện"),
        new("PAYROLL_EXPENSE_GENERAL", "Lương", "Chi phí lương — mặc định cho mọi nhóm chưa có vai trò riêng"),
        new("PAYROLL_EXPENSE_PRODUCTION", "Lương", "Chi phí lương nhóm sản xuất (không ánh xạ thì dùng GENERAL)", Optional: true),
        new("PAYROLL_EXPENSE_SELLING", "Lương", "Chi phí lương nhóm bán hàng (không ánh xạ thì dùng GENERAL)", Optional: true),
        new("PAYROLL_EXPENSE_ADMIN", "Lương", "Chi phí lương nhóm quản lý (không ánh xạ thì dùng GENERAL)", Optional: true),
        new("PAYROLL_PAYABLE", "Lương", "Phải trả người lao động (thực lĩnh, theo từng người)"),
        new("PAYROLL_SI_PAYABLE", "Lương", "Bảo hiểm xã hội phải nộp (phần NLĐ + DN)"),
        new("PAYROLL_HI_PAYABLE", "Lương", "Bảo hiểm y tế phải nộp"),
        new("PAYROLL_UI_PAYABLE", "Lương", "Bảo hiểm thất nghiệp phải nộp"),
        new("PAYROLL_PIT_PAYABLE", "Lương", "Thuế TNCN khấu trừ phải nộp"),
        new("PAYROLL_UNION_PAYABLE", "Lương", "Kinh phí công đoàn phải nộp"),
        new("PAYROLL_OTHER_DEDUCTION", "Lương", "Các khoản khấu trừ khác từ lương"),
    ];
}
