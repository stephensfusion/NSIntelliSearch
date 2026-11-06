using System.Globalization;
using PM = PatternMatching;
using SC = SearchContent;
using VR = ValidateRequest;

namespace IntelliSearch;

/// <summary>
/// Pure data mapping between the public IntelliSearch contract and the request / result / error
/// types each nanoservice DLL defines for itself. No search behaviour lives here.
/// </summary>
internal static class NanoserviceMapper
{
    public static VR.SearchRequest? ToValidateRequest(SearchRequest? r) => r is null ? null : new VR.SearchRequest
    {
        RootPath = r.RootPath,
        SearchText = r.SearchText,
        Recursive = r.Recursive,
        // The nanoservice uses a non-nullable int; "not set" maps to 0 (accepted), negatives stay negative (rejected).
        MaximumDepth = r.MaximumDepth ?? 0,
        MatchCase = r.MatchCase,
        WholeWord = r.WholeWord,
        UseRegularExpression = r.UseRegularExpression,
        PlainText = !r.UseRegularExpression,
        SearchFiles = r.SearchFiles,
        SearchFolders = r.SearchFolders,
        SearchContents = r.SearchContents,
        IncludePatterns = r.IncludePatterns,
        ExcludePatterns = r.ExcludePatterns,
        EncodingPreference = r.EncodingPreference,
        TimeOut = r.Timeout,
    };

    /// <summary>Request used to test an entry *name* against the search text (no include/exclude rules).</summary>
    public static PM.SearchRequest ToNameMatchRequest(SearchRequest r) => new()
    {
        RootPath = r.RootPath,
        SearchText = r.SearchText,
        MatchCase = r.MatchCase,
        WholeWord = r.WholeWord,
        UseRegularExpression = r.UseRegularExpression,
        PlainText = !r.UseRegularExpression,
        TimeOut = r.Timeout,
    };

    /// <summary>Request used to test a relative path against a set of glob patterns only (no search text).</summary>
    public static PM.SearchRequest ToGlobRequest(SearchRequest r, List<string> patterns) => new()
    {
        RootPath = r.RootPath,
        SearchText = null,
        MatchCase = r.MatchCase,
        IncludePatterns = patterns,
        TimeOut = r.Timeout,
    };

    public static SC.SearchRequest ToContentRequest(SearchRequest r) => new()
    {
        RootPath = r.RootPath ?? string.Empty,
        SearchText = r.SearchText ?? string.Empty,
        Recursive = r.Recursive,
        MaximumDepth = r.MaximumDepth ?? 0,
        MatchCase = r.MatchCase,
        WholeWord = r.WholeWord,
        UseRegularExpression = r.UseRegularExpression,
        PlainText = !r.UseRegularExpression,
        SearchFiles = r.SearchFiles,
        SearchFolders = r.SearchFolders,
        SearchContents = r.SearchContents,
        EncodingPreference = r.EncodingPreference ?? string.Empty,
        TimeOut = r.Timeout,
    };

    public static SearchResult FromContentResult(SC.SearchResult m) => new()
    {
        FilePath = m.FilePath,
        FileName = m.FileName,
        LineNumber = ParseInt(m.LineNumber),
        ColumnNumber = ParseInt(m.ColumnNumber),
        CharacterIndex = ParseInt(m.CharacterIndex),
        MatchedText = m.MatchedText,
        // No nanoservice returns line context; the matched text is the only snippet available.
        PreviewSnippet = m.MatchedText,
    };

    public static SearchResult NameResult(string fullPath, string name) => new()
    {
        FilePath = fullPath,
        FileName = name,
        MatchedText = name,
        PreviewSnippet = name,
    };

    public static SearchError FromError(VR.SearchError e) => Create(e.Path, e.Operation, e.ErrorCode, e.Message, e.ExceptionType);

    public static SearchError FromError(global::SearchError e) => Create(e.Path, e.Operation, e.ErrorCode, e.Message, e.ExceptionType);

    public static SearchError FromError(SC.SearchError e) => Create(e.Path, e.Operation, e.ErrorCode, e.Message, e.ExceptionType);

    public static SearchError Create(string? path, string operation, string code, string message, string exceptionType) => new()
    {
        Path = path ?? string.Empty,
        Operation = operation,
        ErrorCode = code,
        Message = message,
        ExceptionType = exceptionType,
    };

    // SearchContent reports positions as invariant-culture strings (they can exceed int for huge files).
    private static int ParseInt(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? (int)Math.Clamp(n, int.MinValue, int.MaxValue)
            : 0;
}
