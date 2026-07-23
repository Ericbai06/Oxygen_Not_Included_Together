namespace ONI_Together.HeadlessTests;

internal static class SyncExecutionOwnerSymbolMatcher
{
    internal static bool Matches(
        string symbol,
        string typeName,
        string methodName)
    {
        string owner = $"{typeName}.{methodName}";
        int start = 0;
        while ((start = symbol.IndexOf(
                   owner, start, StringComparison.Ordinal)) >= 0)
        {
            if (IsTypeBoundary(symbol, start) &&
                HasMethodSuffix(symbol, start + owner.Length))
                return true;
            start++;
        }
        return false;
    }

    private static bool IsTypeBoundary(string symbol, int start) =>
        start == 0 || symbol[start - 1] is '.' or '+';

    private static bool HasMethodSuffix(string symbol, int suffix)
    {
        if (suffix >= symbol.Length)
            return false;
        if (symbol[suffix] == '(')
            return true;
        if (symbol[suffix] != '<')
            return false;
        int depth = 0;
        for (int index = suffix; index < symbol.Length; index++)
        {
            if (symbol[index] == '<')
                depth++;
            else if (symbol[index] == '>')
                depth--;
            if (depth == 0)
                return index + 1 < symbol.Length &&
                    symbol[index + 1] == '(';
        }
        return false;
    }
}
