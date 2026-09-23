using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NutHub.Services.Notifications;

/// <summary>How substituted values are escaped, depending on where the template ends up.</summary>
internal enum PlaceholderEscaping
{
    /// <summary>Verbatim (plain-text bodies, command arguments).</summary>
    None,

    /// <summary>Inside a JSON string literal: quotes, backslashes and control characters escaped.</summary>
    Json,

    /// <summary>Inside a URL or a form body: percent-encoded.</summary>
    Url,
}

/// <summary>
/// Replaces <c>{name}</c> placeholders in webhook templates and command arguments. Only known names are replaced,
/// so the braces of a JSON template and unknown words stay as they are.
/// </summary>
internal static class Placeholders
{
    public static string Apply(string template, IReadOnlyDictionary<string, string> values, PlaceholderEscaping escaping)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 64);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                int end = template.IndexOf('}', i + 1);
                if (end > i + 1 && end - i <= 32)
                {
                    string name = template.Substring(i + 1, end - i - 1);
                    if (IsName(name) && values.TryGetValue(name, out string? value))
                    {
                        sb.Append(Escape(value, escaping));
                        i = end + 1;
                        continue;
                    }
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    public static string Escape(string value, PlaceholderEscaping escaping) => escaping switch
    {
        PlaceholderEscaping.Json => JsonEncodedText.Encode(ValidText(value), JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
        PlaceholderEscaping.Url => Uri.EscapeDataString(value),
        _ => value,
    };

    /// <summary>
    /// The text with every half of a surrogate pair that lost its other half (text shortened in the middle of an
    /// emoji, e.g. a user name) replaced by U+FFFD, as the other encoders do: JsonEncodedText throws on it, and the
    /// notification would be lost.
    /// </summary>
    private static string ValidText(string value) =>
        value.Any(char.IsSurrogate) ? Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value)) : value;

    /// <summary>The escaping that fits a webhook body of this content type.</summary>
    public static PlaceholderEscaping ForContentType(string? contentType)
    {
        if (contentType is null)
        {
            return PlaceholderEscaping.None;
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return PlaceholderEscaping.Json;
        }

        return contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            ? PlaceholderEscaping.Url
            : PlaceholderEscaping.None;
    }

    private static bool IsName(string name)
    {
        foreach (char ch in name)
        {
            if (!char.IsAsciiLetter(ch))
            {
                return false;
            }
        }

        return true;
    }
}
