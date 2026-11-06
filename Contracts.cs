namespace IntelliSearch;

// Public contract transcribed from Requirement.pdf. No behaviour lives here.

public interface IIntelliSearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

public interface ISearchProgressReporter
{
    void Report(SearchProgress progress);
}

public interface ISearchEngine
{
    Task<IEnumerable<SearchMatch>> ExecuteAsync(SearchRequest request, CancellationToken cancellationToken);
}

public interface IPatternMatcher
{
    bool IsMatch(string value);
}

public interface IContentMatcher
{
    IEnumerable<SearchMatch> FindMatches(string content, SearchRequest request);
}

public class SearchRequest
{
    public string? RootPath { get; set; }
    public string? SearchText { get; set; }
    public List<string>? IncludePatterns { get; set; }
    public List<string>? ExcludePatterns { get; set; }
    public bool Recursive { get; set; }
    public int? MaximumDepth { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool UseRegularExpression { get; set; }
    public bool SearchFiles { get; set; }
    public bool SearchFolders { get; set; }
    public bool SearchContents { get; set; }
    public string? EncodingPreference { get; set; }
    public TimeSpan Timeout { get; set; }
}

public class SearchResponse
{
    public bool Succeeded { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public List<SearchResult> SearchResults { get; set; } = new();
    public List<SearchError> Errors { get; set; } = new();

    /// <summary>
    /// Counters tracked by the orchestration (pseudocode "searchStatistics"). Additive property;
    /// exposes the SearchStatistics model required by Requirement.pdf without changing existing members.
    /// </summary>
    public SearchStatistics Statistics { get; set; } = new();
}

public class SearchResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public int CharacterIndex { get; set; }
    public string MatchedText { get; set; } = string.Empty;
    public string PreviewSnippet { get; set; } = string.Empty;
}

/// <summary>Match produced by the engine / content matcher (shape not fixed by the requirement).</summary>
public class SearchMatch
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public int CharacterIndex { get; set; }
    public string MatchedText { get; set; } = string.Empty;
    public string PreviewSnippet { get; set; } = string.Empty;
}

public class SearchStatistics
{
    public int DirectoriesVisited { get; set; }
    public int FilesVisited { get; set; }
    public int FilesMatched { get; set; }
    public int FoldersMatched { get; set; }
    public int ContentMatches { get; set; }
    public TimeSpan ElapsedTime { get; set; }
}

public class SearchProgress
{
    public string? CurrentDirectory { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int FilesProcessed { get; set; }
    public string? CurrentFile { get; set; }
    public double ProgressPercentage { get; set; }
}

public class SearchError
{
    public string Path { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
}
