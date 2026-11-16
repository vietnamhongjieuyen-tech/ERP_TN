using System.Text;

namespace Hak.Application.Common;

/// <summary>Text folding for searches typed by users: case and Vietnamese accents are ignored, everything else is kept.</summary>
public static class SearchText
{
    /// <summary>"  HÓA ĐƠN số 12/A " → "hoa don so 12/a".</summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
        {
            var x = char.ToLowerInvariant(c);
            sb.Append(Map.TryGetValue(x, out var b) ? b : x);
        }
        return sb.ToString();
    }

    // The server runs with invariant globalization (no Unicode decomposition), so Vietnamese letters are folded by table.
    private static readonly Dictionary<char, char> Map = new[]
    {
        ("àáạảãâầấậẩẫăằắặẳẵ", 'a'), ("èéẹẻẽêềếệểễ", 'e'), ("ìíịỉĩ", 'i'), ("òóọỏõôồốộổỗơờớợởỡ", 'o'),
        ("ùúụủũưừứựửữ", 'u'), ("ỳýỵỷỹ", 'y'), ("đ", 'd'),
    }.SelectMany(p => p.Item1.Select(c => (c, p.Item2))).ToDictionary(p => p.c, p => p.Item2);
}
