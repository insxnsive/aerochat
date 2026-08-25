namespace Aerochat.Server.Gateway;

public static class GatewayCursor
{
    public static bool TryParse(string value, out string instanceId, out long sequence)
    {
        instanceId = string.Empty;
        sequence = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1
            || value.IndexOf(':') != separator
            || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        ReadOnlySpan<char> digits = value.AsSpan(separator + 1);
        if (digits.Length > 1 && digits[0] == '0')
        {
            return false;
        }

        foreach (char digit in digits)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sequence)
            || sequence < 0)
        {
            return false;
        }

        instanceId = value[..separator];
        return true;
    }
}
