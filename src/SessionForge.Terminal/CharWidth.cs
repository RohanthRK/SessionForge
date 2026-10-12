using System.Globalization;

namespace SessionForge.Terminal;

/// <summary>
/// Terminal column width of a Unicode scalar (wcwidth semantics):
/// 0 for combining/format characters, 2 for East Asian wide/fullwidth and emoji presentation, 1 otherwise.
/// </summary>
public static class CharWidth
{
    // Sorted, non-overlapping inclusive ranges of double-width code points.
    // Derived from Unicode EastAsianWidth (W/F) plus Emoji_Presentation blocks.
    private static readonly (int Lo, int Hi)[] Wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0),
        (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F),
        (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5),
        (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728),
        (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2E80, 0x303E), (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF),
        (0xA960, 0xA97F), (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F),
        (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4), (0x17000, 0x18CFF), (0x1AFF0, 0x1B2FF),
        (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A),
        (0x1F200, 0x1F202), (0x1F210, 0x1F23B), (0x1F240, 0x1F248), (0x1F250, 0x1F251),
        (0x1F260, 0x1F265), (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0),
        (0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC),
        (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A),
        (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5),
        (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6DC, 0x1F6DF),
        (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0),
        (0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FAFF),
        (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    private static readonly sbyte[] AsciiAndLatin = BuildLow();

    private static sbyte[] BuildLow()
    {
        var t = new sbyte[0x300];
        for (int i = 0; i < t.Length; i++) t[i] = (sbyte)Compute(i);
        return t;
    }

    /// <summary>Returns 0, 1 or 2. Control characters return 0 (they are never printed).</summary>
    public static int Of(int codePoint)
    {
        if ((uint)codePoint < (uint)AsciiAndLatin.Length) return AsciiAndLatin[codePoint];
        return Compute(codePoint);
    }

    private static int Compute(int cp)
    {
        if (cp < 0x20 || (cp >= 0x7F && cp < 0xA0)) return 0;
        if (cp < 0x300) return 1;
        if (cp == 0x200B || (cp >= 0x200C && cp <= 0x200F) || (cp >= 0x2028 && cp <= 0x202E) || (cp >= 0x2060 && cp <= 0x2064)) return 0;
        if (cp >= 0xFE00 && cp <= 0xFE0F) return 0; // variation selectors
        if (cp >= 0xE0100 && cp <= 0xE01EF) return 0;
        if (cp >= 0x1160 && cp <= 0x11FF) return 0; // Hangul medial vowels/final consonants combine
        if (cp > 0x10FFFF) return 1;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsWide(int cp)
    {
        int lo = 0, hi = Wide.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var r = Wide[mid];
            if (cp < r.Lo) hi = mid - 1;
            else if (cp > r.Hi) lo = mid + 1;
            else return true;
        }
        return false;
    }
}
