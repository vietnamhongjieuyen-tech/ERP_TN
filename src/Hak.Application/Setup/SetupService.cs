using System.Text.RegularExpressions;
using Hak.Application.Audit;
using Hak.Application.Common;
using Hak.Application.Security;
using Hak.Data;
using Hak.Domain.Catalog;
using Hak.Domain.Common;
using Hak.Domain.Organization;
using Hak.Domain.Regime;
using Hak.Domain.Security;
using Hak.Domain.Vouchers;
using Microsoft.EntityFrameworkCore;

namespace Hak.Application.Setup;

public sealed record LegalSourceInput(string RuleGroup, string DocumentNo, DateOnly? IssuedDate, string? Article, string? Url);

/// <summary>System administration (docs/01 §5): companies, fiscal years, periods, users, regimes, catalogs.</summary>
public sealed partial class SetupService(HakDbContext db, AuditService audit, IClock clock, HakRuntimeOptions options)
{
    [GeneratedRegex("^[A-Z0-9_.-]{1,30}$")]
    private static partial Regex CodePattern();

    /// <summary>Idempotent seeding of ISO currencies and the default role catalogue.</summary>
    public async Task EnsureReferenceDataAsync()
    {
        var currencies = new[] { ("VND", "Đồng Việt Nam", 0), ("USD", "Đô la Mỹ", 2), ("EUR", "Euro", 2), ("CNY", "Nhân dân tệ", 2), ("JPY", "Yên Nhật", 0) };
        foreach (var (code, name, dp) in currencies)
            if (!await db.Currencies.AnyAsync(c => c.Code == code))
                db.Currencies.Add(new Currency { Code = code, Name = name, DecimalPlaces = dp });

        // Default roles: created on first run; on upgrades the permissions added to the catalogue since (asset.manage,
        // loan.manage…) are granted to the existing default roles. Nothing is ever removed automatically.
        var added = new List<object>();
        foreach (var (code, (name, perms)) in RoleCatalog.Defaults)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code);
            if (role is null)
            {
                role = new Role { Id = Ids.New(), Code = code, Name = name };
                role.Permissions = perms.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }).ToList();
                db.Roles.Add(role);
                continue;
            }
            var missing = perms.Distinct().Except(role.Permissions.Select(p => p.Permission)).ToList();
            foreach (var p in missing) role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            if (missing.Count > 0) added.Add(new { role = code, permissions = missing });
        }
        if (added.Count > 0) await audit.AppendAsync(Guid.Empty, null, "role.sync_defaults", "role", null, new { added });
        await db.SaveChangesAsync();
    }

    /// <summary>Creates the first administrator. Refused once any user exists.</summary>
    /// <summary>First run only: creates the first system administrator while the user table is empty (checked inside the
    /// writer transaction, so two first-run screens cannot both succeed).</summary>
    public async Task<User> BootstrapAdminAsync(string userName, string fullName, string password) =>
        await db.InTransactionAsync(async () =>
        {
            if (await db.Users.AnyAsync())
                throw new DomainException(ErrorCodes.Forbidden, "Hệ thống đã có người dùng; không thể khởi tạo lại quản trị viên.");
            return await CreateUserCoreAsync(null, userName, fullName, password, isSystemAdmin: true);
        });

    public async Task<bool> NeedsBootstrapAsync() => !await db.Users.AnyAsync();

    public async Task<User> CreateUserAsync(Guid adminId, string userName, string fullName, string password, bool isSystemAdmin = false)
    {
        await RequireSystemAdminAsync(adminId);
        return await CreateUserCoreAsync(adminId, userName, fullName, password, isSystemAdmin);
    }

    private async Task<User> CreateUserCoreAsync(Guid? adminId, string userName, string fullName, string password, bool isSystemAdmin)
    {
        if (password.Length < PasswordHasher.MinPasswordLength)
            throw new DomainException(ErrorCodes.ValidationFailed, $"Mật khẩu phải có ít nhất {PasswordHasher.MinPasswordLength} ký tự.");
        var norm = userName.Trim().ToLowerInvariant();
        if (norm.Length == 0) throw new DomainException(ErrorCodes.ValidationFailed, "Tên đăng nhập bắt buộc.");
        var user = new User
        {
            Id = Ids.New(), UserName = userName.Trim(), NormalizedUserName = norm, FullName = fullName,
            PasswordHash = PasswordHasher.Hash(password, options.PasswordIterations), IsSystemAdmin = isSystemAdmin, CreatedAt = clock.UtcNow,
        };
        await db.InTransactionAsync(async () =>
        {
            if (await db.Users.AnyAsync(u => u.NormalizedUserName == norm))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Tên đăng nhập '{userName}' đã tồn tại.");
            db.Users.Add(user);
            await audit.AppendAsync(Guid.Empty, null, "user.create", "user", user.Id.ToString(), new { user.UserName, isSystemAdmin }, userIdOverride: adminId ?? user.Id);
            await db.SaveChangesAsync();
        });
        return user;
    }

    public async Task GrantRoleAsync(Guid adminId, Guid userId, Guid companyId, string roleCode)
    {
        await RequireSystemAdminAsync(adminId);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Code == roleCode)
                   ?? throw new DomainException(ErrorCodes.NotFound, $"Vai trò {roleCode} không tồn tại.");
        await db.InTransactionAsync(async () =>
        {
            if (!await db.UserCompanyRoles.AnyAsync(x => x.UserId == userId && x.CompanyId == companyId && x.RoleId == role.Id))
                db.UserCompanyRoles.Add(new UserCompanyRole { UserId = userId, CompanyId = companyId, RoleId = role.Id });
            await audit.AppendAsync(companyId, null, "role.grant", "user", userId.ToString(), new { roleCode }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
    }

    // ---------------- Accounting regimes (docs/03 §8) ----------------

    public async Task<AccountingRegimeVersion> CreateRegimeVersionAsync(Guid adminId, string regimeCode, string regimeName,
        string versionCode, DateOnly effectiveFrom, DateOnly? effectiveTo, IEnumerable<LegalSourceInput> sources)
    {
        await RequireSystemAdminAsync(adminId);
        regimeCode = regimeCode.Trim().ToUpperInvariant();
        versionCode = versionCode.Trim();
        var sourceList = sources.ToList();
        if (regimeCode.Length == 0 || versionCode.Length == 0 || string.IsNullOrWhiteSpace(regimeName))
            throw new DomainException(ErrorCodes.ValidationFailed, "Mã chế độ, tên chế độ và mã phiên bản là bắt buộc.");
        if (effectiveTo is not null && effectiveTo < effectiveFrom)
            throw new DomainException(ErrorCodes.ValidationFailed, "Ngày hết hiệu lực trước ngày hiệu lực.");
        if (sourceList.Count == 0 || sourceList.Any(s => string.IsNullOrWhiteSpace(s.RuleGroup) || string.IsNullOrWhiteSpace(s.DocumentNo)))
            throw new DomainException(ErrorCodes.ValidationFailed, "Phải khai báo ít nhất một căn cứ pháp lý (nhóm quy định + số văn bản).");
        sources = sourceList;
        return await db.InTransactionAsync(async () =>
        {
            var regime = await db.AccountingRegimes.FirstOrDefaultAsync(r => r.Code == regimeCode);
            if (regime is not null && await db.AccountingRegimeVersions.AnyAsync(v => v.RegimeId == regime.Id && v.VersionCode == versionCode))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Phiên bản {versionCode} của chế độ {regimeCode} đã tồn tại.");
            if (regime is null)
            {
                regime = new AccountingRegime { Id = Ids.New(), Code = regimeCode, Name = regimeName };
                db.AccountingRegimes.Add(regime);
            }
            var version = new AccountingRegimeVersion
            {
                Id = Ids.New(), RegimeId = regime.Id, VersionCode = versionCode,
                EffectiveFrom = effectiveFrom, EffectiveTo = effectiveTo, VerificationStatus = VerificationStatus.Unverified,
            };
            version.LegalSources = sources.Select(s => new LegalSource
            {
                Id = Ids.New(), RegimeVersionId = version.Id, RuleGroup = s.RuleGroup, DocumentNo = s.DocumentNo,
                IssuedDate = s.IssuedDate, Article = s.Article, Url = s.Url,
            }).ToList();
            db.AccountingRegimeVersions.Add(version);
            await audit.AppendAsync(Guid.Empty, null, "regime.version.create", "accounting_regime_version", version.Id.ToString(),
                new { regimeCode, versionCode, effectiveFrom, effectiveTo }, userIdOverride: adminId);
            await db.SaveChangesAsync();
            return version;
        });
    }

    /// <summary>
    /// Marks a regime version as checked against the original legal texts. Requires at least one
    /// legal source and a note naming who checked what (e.g. the chief accountant's sign-off).
    /// </summary>
    public async Task VerifyRegimeVersionAsync(Guid adminId, Guid versionId, string note)
    {
        await RequireSystemAdminAsync(adminId);
        if (string.IsNullOrWhiteSpace(note)) throw new DomainException(ErrorCodes.ReasonRequired, "Phải ghi rõ nội dung xác minh.");
        await db.InTransactionAsync(async () =>
        {
            var v = await db.AccountingRegimeVersions.Include(x => x.LegalSources).FirstOrDefaultAsync(x => x.Id == versionId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Phiên bản chế độ kế toán không tồn tại.");
            if (v.LegalSources.Count == 0)
                throw new DomainException(ErrorCodes.ValidationFailed, "Phải khai báo nguồn văn bản pháp luật trước khi xác minh.");
            v.VerificationStatus = VerificationStatus.Verified;
            v.VerifiedBy = adminId;
            v.VerifiedAt = clock.UtcNow;
            v.VerificationNote = note;
            await audit.AppendAsync(Guid.Empty, null, "regime.version.verify", "accounting_regime_version", v.Id.ToString(), new { note }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
    }

    // ---------------- Companies, fiscal years, periods ----------------

    public async Task<Company> CreateCompanyAsync(Guid adminId, string code, string name, string? taxCode, string functionalCurrency, Guid regimeVersionId)
    {
        await RequireSystemAdminAsync(adminId);
        RequireCode(code);
        var company = new Company
        {
            Id = Ids.New(), Code = code, Name = name, TaxCode = taxCode, FunctionalCurrency = functionalCurrency,
            RegimeVersionId = regimeVersionId, CreatedAt = clock.UtcNow, CreatedBy = adminId,
        };
        await db.InTransactionAsync(async () =>
        {
            if (!await db.Currencies.AnyAsync(c => c.Code == functionalCurrency))
                throw new DomainException(ErrorCodes.ValidationFailed, $"Đồng tiền {functionalCurrency} chưa được khai báo.");
            if (!await db.AccountingRegimeVersions.AnyAsync(v => v.Id == regimeVersionId))
                throw new DomainException(ErrorCodes.NotFound, "Phiên bản chế độ kế toán không tồn tại.");
            if (await db.Companies.AnyAsync(c => c.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã công ty {code} đã tồn tại.");
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            await audit.AppendAsync(company.Id, null, "company.create", "company", company.Id.ToString(),
                new { code, name, taxCode, functionalCurrency, regimeVersionId }, userIdOverride: adminId);
            await db.SaveChangesAsync();
        });
        return company;
    }

    /// <summary>Updates name/tax code. Functional currency and regime are deliberately not editable here (see docs/01 §5.9).</summary>
    public async Task<Company> UpdateCompanyProfileAsync(Guid adminId, Guid companyId, string name, string? taxCode)
    {
        await RequireSystemAdminAsync(adminId);
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException(ErrorCodes.ValidationFailed, "Tên công ty bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == companyId) ?? throw new DomainException(ErrorCodes.NotFound, "Công ty không tồn tại.");
            var old = new { c.Name, c.TaxCode };
            c.Name = name.Trim();
            c.TaxCode = string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim();
            c.RowVersion++;
            await audit.AppendAsync(companyId, null, "company.update", "company", c.Id.ToString(), new { old, c.Name, c.TaxCode }, c.RowVersion, userIdOverride: adminId);
            await db.SaveChangesAsync();
            return c;
        });
    }

    public async Task<FiscalYear> CreateFiscalYearAsync(SessionContext ctx, string code, DateOnly start, DateOnly end)
    {
        ctx.Require(Permissions.ConfigManage);
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException(ErrorCodes.ValidationFailed, "Nhập mã năm tài chính.");
        if (start.Year < 1900) throw new DomainException(ErrorCodes.ValidationFailed, "Nhập ngày bắt đầu năm tài chính.");
        if (end < start) throw new DomainException(ErrorCodes.ValidationFailed, "Ngày kết thúc phải sau ngày bắt đầu.");
        return await db.InTransactionAsync(async () =>
        {
            var existing = await db.FiscalYears.Where(f => f.CompanyId == ctx.CompanyId).ToListAsync();
            if (existing.Any(f => f.Overlaps(start, end)))
                throw new DomainException(ErrorCodes.FiscalYearOverlap, "Năm tài chính chồng lấn với năm đã có.");
            if (existing.Any(f => f.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã năm tài chính {code} đã tồn tại.");
            var fy = new FiscalYear { Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, StartDate = start, EndDate = end, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            fy.Periods = AccountingPeriod.MonthlyFor(fy);
            db.FiscalYears.Add(fy);
            await audit.AppendAsync(ctx.CompanyId, ctx, "fiscal_year.create", "fiscal_year", fy.Id.ToString(), new { code, start, end, periods = fy.Periods.Count });
            await db.SaveChangesAsync();
            return fy;
        });
    }

    public Task LockPeriodAsync(SessionContext ctx, Guid periodId) => SetPeriodStatusAsync(ctx, periodId, PeriodStatus.Locked, null);

    public Task ReopenPeriodAsync(SessionContext ctx, Guid periodId, string reason)
    {
        ctx.Require(Permissions.PeriodReopen);   // permission first: a user without it is told so, not asked for a reason
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(ErrorCodes.ReasonRequired, "Mở lại kỳ phải ghi rõ lý do.");
        return SetPeriodStatusAsync(ctx, periodId, PeriodStatus.Open, reason);
    }

    private async Task SetPeriodStatusAsync(SessionContext ctx, Guid periodId, PeriodStatus target, string? reason)
    {
        ctx.Require(target == PeriodStatus.Locked ? Permissions.PeriodLock : Permissions.PeriodReopen);
        await db.InTransactionAsync(async () =>
        {
            var p = await db.AccountingPeriods.FirstOrDefaultAsync(x => x.Id == periodId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.PeriodNotFound, "Kỳ kế toán không tồn tại trong công ty.");
            if (p.Status == target) return;
            if (target == PeriodStatus.Locked)
            {
                var pending = await db.Vouchers.CountAsync(v => v.CompanyId == ctx.CompanyId && v.PostingDate >= p.StartDate && v.PostingDate <= p.EndDate
                                                               && (v.Status == VoucherStatus.Submitted || v.Status == VoucherStatus.Approved));
                if (pending > 0)
                    throw new DomainException(ErrorCodes.PeriodHasPending, $"Còn {pending} chứng từ đã gửi duyệt/đã duyệt chưa ghi sổ trong kỳ; xử lý trước khi khóa sổ.");
            }
            p.Status = target;
            p.LockedAt = target == PeriodStatus.Locked ? clock.UtcNow : null;
            p.LockedBy = target == PeriodStatus.Locked ? ctx.UserId : null;
            p.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, target == PeriodStatus.Locked ? "period.lock" : "period.reopen",
                "accounting_period", p.Id.ToString(), new { p.StartDate, p.EndDate, reason }, p.RowVersion);
            await db.SaveChangesAsync();
        });
    }

    // ---------------- Catalog ----------------

    public async Task<Account> CreateAccountAsync(SessionContext ctx, string code, string name, string? parentCode = null,
        bool isPostable = true, bool requiresPartner = false, BalanceDisplay display = BalanceDisplay.Net,
        OpenItemNature openItemNature = OpenItemNature.None, CashKind cashKind = CashKind.None, NegativeBalancePolicy negativeBalancePolicy = NegativeBalancePolicy.None)
    {
        ctx.Require(Permissions.CatalogManage);
        RequireCode(code);
        if (openItemNature != OpenItemNature.None && !requiresPartner)
            throw new DomainException(ErrorCodes.ValidationFailed, "Tài khoản theo dõi công nợ theo chứng từ phải bắt buộc đối tượng.");
        if (openItemNature != OpenItemNature.None && cashKind != CashKind.None)
            throw new DomainException(ErrorCodes.ValidationFailed, "Tài khoản tiền không thể đồng thời là tài khoản công nợ.");
        return await db.InTransactionAsync(async () =>
        {
            if (await db.Accounts.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Tài khoản {code} đã tồn tại.");
            Guid? parentId = null;
            if (parentCode is not null)
            {
                var parent = await db.Accounts.FirstOrDefaultAsync(a => a.CompanyId == ctx.CompanyId && a.Code == parentCode)
                             ?? throw new DomainException(ErrorCodes.AccountNotFound, $"Tài khoản cha {parentCode} không tồn tại.");
                if (parent.IsPostable)
                    throw new DomainException(ErrorCodes.ValidationFailed, $"Tài khoản cha {parentCode} đang là tài khoản chi tiết; phải chuyển thành tổng hợp trước.");
                parentId = parent.Id;
            }
            var acc = new Account
            {
                Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Name = name, ParentId = parentId, IsPostable = isPostable,
                RequiresPartner = requiresPartner, BalanceDisplay = display, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId,
                OpenItemNature = openItemNature, CashKind = cashKind, NegativeBalancePolicy = negativeBalancePolicy,
            };
            db.Accounts.Add(acc);
            await audit.AppendAsync(ctx.CompanyId, ctx, "account.create", "account", acc.Id.ToString(), new { code, name, parentCode, isPostable, requiresPartner, openItemNature, cashKind, negativeBalancePolicy });
            await db.SaveChangesAsync();
            return acc;
        });
    }

    public async Task<BusinessPartner> CreatePartnerAsync(SessionContext ctx, string code, string name, PartnerRoles roles, string? taxCode = null)
    {
        ctx.Require(Permissions.CatalogManage);
        RequireCode(code);
        return await db.InTransactionAsync(async () =>
        {
            if (await db.BusinessPartners.AnyAsync(p => p.CompanyId == ctx.CompanyId && p.Code == code))
                throw new DomainException(ErrorCodes.DuplicateCode, $"Mã đối tượng {code} đã tồn tại.");
            var p = new BusinessPartner { Id = Ids.New(), CompanyId = ctx.CompanyId, Code = code, Name = name, Roles = roles, TaxCode = taxCode, CreatedAt = clock.UtcNow, CreatedBy = ctx.UserId };
            db.BusinessPartners.Add(p);
            await audit.AppendAsync(ctx.CompanyId, ctx, "partner.create", "business_partner", p.Id.ToString(), new { code, name, taxCode });
            await db.SaveChangesAsync();
            return p;
        });
    }

    /// <summary>Updates a partner's name, tax code and active flag (code is the stable key and is not editable).</summary>
    public async Task<BusinessPartner> UpdatePartnerAsync(SessionContext ctx, Guid partnerId, string name, string? taxCode, bool isActive)
    {
        ctx.Require(Permissions.CatalogManage);
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException(ErrorCodes.ValidationFailed, "Tên đối tượng bắt buộc.");
        return await db.InTransactionAsync(async () =>
        {
            var p = await db.BusinessPartners.FirstOrDefaultAsync(x => x.Id == partnerId && x.CompanyId == ctx.CompanyId)
                    ?? throw new DomainException(ErrorCodes.NotFound, "Đối tượng không tồn tại.");
            var old = new { p.Name, p.TaxCode, p.IsActive };
            p.Name = name.Trim();
            p.TaxCode = string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim();
            p.IsActive = isActive;
            p.RowVersion++;
            await audit.AppendAsync(ctx.CompanyId, ctx, "partner.update", "business_partner", p.Id.ToString(), new { old, p.Name, p.TaxCode, p.IsActive }, p.RowVersion);
            await db.SaveChangesAsync();
            return p;
        });
    }

    private async Task RequireSystemAdminAsync(Guid userId)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.IsSystemAdmin))
            throw new DomainException(ErrorCodes.Forbidden, "Chỉ quản trị hệ thống được thực hiện thao tác này.");
    }

    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    private static void RequireCode(string code)
    {
        if (!CodePattern().IsMatch(code))
            throw new DomainException(ErrorCodes.ValidationFailed, $"Mã '{code}' không hợp lệ (chỉ A–Z, 0–9, _ . -; tối đa 30 ký tự).");
    }
}
