using System;
using System.Text.RegularExpressions;

namespace GFDStudio.GUI.Controls;

internal static class FilterTextMatcher
{
    public static bool Matches(string? value, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        if (filter.Length >= 2 && filter[0] == '/' && filter[^1] == '/')
        {
            var pattern = filter.Substring(1, filter.Length - 2);
            try
            {
                return Regex.IsMatch(
                    value ?? string.Empty,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return (value ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
