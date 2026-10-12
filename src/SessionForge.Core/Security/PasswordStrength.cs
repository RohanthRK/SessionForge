namespace SessionForge.Core.Security;

public enum StrengthLevel { VeryWeak, Weak, Fair, Strong, VeryStrong }

/// <summary>Conservative password strength estimate (character-class entropy with penalties for repetition and common words).</summary>
public static class PasswordStrength
{
    private static readonly string[] Common =
    [
        "password", "123456", "qwerty", "letmein", "admin", "welcome", "monkey", "dragon", "iloveyou", "abc123",
        "master", "login", "princess", "sunshine", "football", "baseball", "shadow", "superman", "trustno1", "passw0rd",
    ];

    public static (StrengthLevel Level, double Bits) Evaluate(string? password)
    {
        if (string.IsNullOrEmpty(password)) return (StrengthLevel.VeryWeak, 0);
        int pool = 0;
        if (password.Any(char.IsLower)) pool += 26;
        if (password.Any(char.IsUpper)) pool += 26;
        if (password.Any(char.IsDigit)) pool += 10;
        if (password.Any(c => !char.IsLetterOrDigit(c) && c < 128)) pool += 33;
        if (password.Any(c => c >= 128)) pool += 100;

        int distinct = password.Distinct().Count();
        double effectiveLength = Math.Min(password.Length, distinct * 2.0);
        double bits = effectiveLength * Math.Log2(Math.Max(pool, 1));

        var lower = password.ToLowerInvariant();
        if (Common.Any(lower.Contains)) bits *= 0.4;
        if (IsSequential(lower)) bits *= 0.6;

        var level = bits switch
        {
            < 28 => StrengthLevel.VeryWeak,
            < 40 => StrengthLevel.Weak,
            < 60 => StrengthLevel.Fair,
            < 80 => StrengthLevel.Strong,
            _ => StrengthLevel.VeryStrong,
        };
        return (level, bits);
    }

    private static bool IsSequential(string s)
    {
        if (s.Length < 4) return false;
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] - s[i - 1] is 1 or -1 or 0 ? run + 1 : 1;
            if (run >= 4) return true;
        }
        return false;
    }
}
