using System.Text.RegularExpressions;

namespace PlakaTakip;

/// <summary>Türk plakası doğrulama / OCR hatalarını düzeltme: İL(01-81) + 1-3 HARF + 2-4 RAKAM</summary>
public static class TurkishPlate
{
    private static readonly Regex Pattern =
        new(@"^(0[1-9]|[1-7][0-9]|8[01])([A-Z]{1,3})([0-9]{2,4})$", RegexOptions.Compiled);

    // Rakam olması gereken yerde harf okunduysa
    private static readonly Dictionary<char, char> ToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['I'] = '1', ['L'] = '1',
        ['Z'] = '2', ['S'] = '5', ['G'] = '6', ['B'] = '8'
    };

    // Harf olması gereken yerde rakam okunduysa
    private static readonly Dictionary<char, char> ToLetter = new()
    {
        ['0'] = 'O', ['1'] = 'I', ['2'] = 'Z', ['5'] = 'S', ['6'] = 'G', ['8'] = 'B'
    };

    public static bool TryNormalize(string? raw, out string plate)
    {
        plate = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = new string(raw.ToUpperInvariant()
            .Where(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            .ToArray());

        string? best = null;
        var bestCost = int.MaxValue;

        // Soldaki mavi "TR" şeridi 1-2 karakter olarak okunabilir; baştan 0-2 karakter atlayarak dene
        for (var skip = 0; skip <= 2 && skip < cleaned.Length; skip++)
        {
            var s = cleaned[skip..];
            for (var letters = 1; letters <= 3; letters++)
            {
                var digits = s.Length - 2 - letters;
                if (digits is < 2 or > 4) continue;

                if (TryFix(s, letters, out var fixedText, out var corrections))
                {
                    var cost = corrections + skip;
                    if (cost < bestCost)
                    {
                        best = fixedText;
                        bestCost = cost;
                    }
                }
            }
        }

        if (best is null) return false;
        plate = best;
        return true;
    }

    /// <summary>"34ABC123" → "34 ABC 123"</summary>
    public static string Format(string plate)
    {
        var m = Pattern.Match(plate);
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}" : plate;
    }

    private static bool TryFix(string s, int letterCount, out string result, out int corrections)
    {
        result = string.Empty;
        corrections = 0;
        var chars = new char[s.Length];

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var shouldBeLetter = i >= 2 && i < 2 + letterCount;

            if (shouldBeLetter && char.IsDigit(c))
            {
                if (!ToLetter.TryGetValue(c, out var mapped)) return false;
                c = mapped;
                corrections++;
            }
            else if (!shouldBeLetter && char.IsLetter(c))
            {
                if (!ToDigit.TryGetValue(c, out var mapped)) return false;
                c = mapped;
                corrections++;
            }

            chars[i] = c;
        }

        result = new string(chars);
        return Pattern.IsMatch(result);
    }
}
