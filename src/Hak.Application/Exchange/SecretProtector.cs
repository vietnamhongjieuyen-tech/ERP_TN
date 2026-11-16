using System.Security.Cryptography;
using Hak.Domain.Common;

namespace Hak.Application.Exchange;

/// <summary>
/// Exchange-key secrets at rest. On Windows the secret is wrapped with DPAPI (machine scope), so a copied database file does
/// not reveal it; the price is that a database moved or restored to another machine cannot read its keys — they are issued
/// again there (docs/08). Values without the prefix are legacy plain Base64 and still readable. Other platforms store Base64.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "HAK.ExchangePartnerKey.v1"u8.ToArray();

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(byte[] secret)
        => OperatingSystem.IsWindows()
            ? Prefix + Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.LocalMachine))
            : Convert.ToBase64String(secret);

    public static byte[] Unprotect(string stored)
    {
        if (!IsProtected(stored)) return Convert.FromBase64String(stored);
        if (!OperatingSystem.IsWindows())
            throw new DomainException(ErrorCodes.NotSupported, "Khóa trao đổi được bảo vệ bằng Windows DPAPI; không đọc được trên hệ điều hành này. Cấp khóa mới.");
        try { return ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine); }
        catch (CryptographicException)
        {
            throw new DomainException(ErrorCodes.ValidationFailed,
                "Không giải mã được khóa trao đổi: cơ sở dữ liệu đã được chuyển sang máy khác. Ngừng dùng khóa này và cấp khóa mới (Lương → Khóa trao đổi).");
        }
    }
}
