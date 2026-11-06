using System.Diagnostics;
using System.Text.RegularExpressions;
using SplitPathsIntoChunks;
using PM = PatternMatching;
using SC = SearchContent;
using VR = ValidateRequest;

namespace IntelliSearch;

/// <summary>
/// IntelliSearch orchestration (pseudocode.txt). Combines the ValidateRequest, TraverseDirectory,
/// SplitPathsIntoChunks, PatternMatching and SearchContent nanoservice DLLs; it coordinates them
/// and never re-implements their logic. Never throws: every failure becomes a structured
/// <see cref="SearchError"/> in the returned <see cref="SearchResponse"/>.
/// </summary>
public class IntelliSearchService : IIntelliSearchService
{
    public const string OperationSearch = "Search";
    public const string OperationParsePattern = "ParsePattern";
    public const string OperationPatternMatching = "PatternMatching";
    public const string OperationSearchContent = "SearchContent";
    public const string OperationReportProgress = "ReportProgress";

    private readonly ISearchProgressReporter? _progressReporter;
    private readonly VR.ValidateRequestService _validator = new();
    private readonly TraverseDirectoryService _traverser = new();
    private readonly PM.PatternMatchingService _patternMatcher = new();
    private readonly SC.SearchContentService _contentSearcher = new();

    public IntelliSearchService(ISearchProgressReporter? progressReporter = null)
    {
        _progressReporter = progressReporter;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        // Setup structural variables to track searchStatistics, SearchResponse and SearchProgress.
        var state = new SearchState(_progressReporter);

        // Start Timer
        var timer = Stopwatch.StartNew();
        try
        {
            await RunAsync(request, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Response.Cancelled = true;
        }
        catch (Exception ex)
        {
            // Unrecoverable failure: report it instead of terminating unexpectedly.
            state.AddError(NanoserviceMapper.Create(request?.RootPath, OperationSearch, "UnexpectedError",
                "The search failed unexpectedly.", ex.GetType().Name));
            state.Fatal = true;
        }

        // Stop Timer, update ElapsedTime in SearchResponse.
        timer.Stop();
        return state.Complete(timer.Elapsed, cancellationToken.IsCancellationRequested);
    }

    private async Task RunAsync(SearchRequest request, SearchState state, CancellationToken cancellationToken)
    {
        // Validate the searchRequest using the ValidateRequest nanoservice.
        var (isValid, validationError) = _validator.ValidateSearchRequest(
            NanoserviceMapper.ToValidateRequest(request)!, cancellationToken);
        if (!isValid)
        {
            if (validationError?.ErrorCode == VR.ValidateRequestService.ErrorCodes.OperationCanceled)
            {
                state.Response.Cancelled = true;
            }
            else
            {
                state.AddError(validationError is null
                    ? NanoserviceMapper.Create(request?.RootPath, VR.ValidateRequestService.OperationName, "ValidationFailed", "The search request is invalid.", string.Empty)
                    : NanoserviceMapper.FromError(validationError));
                state.Fatal = true;
            }
            return;
        }

        // Requirement AT-04 / VAL-01: an invalid regex is a structured error raised before any file is read.
        // No nanoservice validates the pattern up front (PatternMatching silently returns false), so the
        // orchestrator checks it once here.
        if (request.UseRegularExpression && !IsValidRegex(request.SearchText!, out var regexException))
        {
            state.AddError(NanoserviceMapper.Create(request.RootPath, OperationParsePattern, "InvalidPattern",
                "The search pattern is not a valid regular expression.", regexException!.GetType().Name));
            state.Fatal = true;
            return;
        }

        // Request-scoped timeout, linked to the caller's token.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout > TimeSpan.Zero)
        {
            timeoutSource.CancelAfter(request.Timeout > MaxTimer ? MaxTimer : request.Timeout);
        }
        var token = timeoutSource.Token;

        try
        {
            await SearchTreeAsync(request, state, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Handled below: caller cancellation vs timeout.
        }

        if (token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            state.AddError(NanoserviceMapper.Create(request.RootPath, OperationSearch, "Timeout",
                "The search did not complete within the requested timeout.", nameof(TimeoutException)));
            state.Fatal = true;
        }
    }

    private async Task SearchTreeAsync(SearchRequest request, SearchState state, CancellationToken token)
    {
        var root = Path.GetFullPath(request.RootPath!);

        // Traverse directory using the TraverseDirectory nanoservice. "MaximumDepth not set" with
        // Recursive = true means unlimited depth.
        var maxDepth = request.Recursive ? request.MaximumDepth ?? int.MaxValue : 0;
        var (paths, traversalErrors) = await Task.Run(
            () => _traverser.TraverseDirectory(root, maxDepth, request.Recursive, token), token).ConfigureAwait(false);

        // Update the SearchResponse errors with the returned errors.
        foreach (var error in traversalErrors)
        {
            state.AddError(NanoserviceMapper.FromError(error));
            if (error.Operation == TraverseDirectoryService.OperationValidate)
            {
                state.Fatal = true; // the root itself could not be traversed
            }
        }

        token.ThrowIfCancellationRequested();
        state.BeginProcessing(paths.Count);

        // Use the SplitIntoChunks nanoservice to split the collection of paths into chunks.
        var chunkSize = Math.Max(1, (int)Math.Ceiling(paths.Count / (double)(Environment.ProcessorCount * ChunksPerCore)));
        var chunks = SplitPathsIntoChunksService.SplitIntoChunks(paths, chunkSize, token);

        var context = new PathContext(
            request,
            root,
            NanoserviceMapper.ToNameMatchRequest(request),
            request.IncludePatterns is { Count: > 0 } inc ? NanoserviceMapper.ToGlobRequest(request, inc) : null,
            request.ExcludePatterns is { Count: > 0 } exc ? NanoserviceMapper.ToGlobRequest(request, exc) : null,
            NanoserviceMapper.ToContentRequest(request));

        // Use concurrency to process each chunk.
        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };
        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Count), options, (chunkIndex, ct) =>
        {
            var chunk = chunks[chunkIndex];
            for (var i = 0; i < chunk.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ProcessPath(context, state, chunkIndex * chunkSize + i, chunk[i], ct);
            }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void ProcessPath(PathContext ctx, SearchState state, int index, string path, CancellationToken token)
    {
        var request = ctx.Request;
        var name = Path.GetFileName(path);
        var relative = Path.GetRelativePath(ctx.Root, path);

        if (Directory.Exists(path))
        {
            state.DirectoryProcessed(path);
            if (!request.SearchFolders)
            {
                return;
            }

            // Check match using the PatternMatching nanoservice; on error update the response errors.
            if (PassesFilters(ctx, state, path, relative, token) && NameMatches(ctx, state, path, name, token))
            {
                state.AddFolderMatch(index, NanoserviceMapper.NameResult(path, name));
            }
            return;
        }

        state.FileProcessed(path);

        // Check match using the PatternMatching nanoservice (include / exclude rules).
        if (!PassesFilters(ctx, state, path, relative, token))
        {
            return;
        }

        if (request.SearchFiles && NameMatches(ctx, state, path, name, token))
        {
            state.AddFileMatch(index, NanoserviceMapper.NameResult(path, name));
        }

        if (request.SearchContents)
        {
            SearchContents(ctx, state, index, path, token);
        }
    }

    private void SearchContents(PathContext ctx, SearchState state, int index, string path, CancellationToken token)
    {
        try
        {
            var (matches, errors) = _contentSearcher.FindMatches(path, ctx.ContentRequest, token);
            try
            {
                foreach (var match in matches)
                {
                    state.AddContentMatch(index, NanoserviceMapper.FromContentResult(match));
                }
            }
            finally
            {
                // SearchContent appends read-time errors to the same list during enumeration.
                foreach (var error in errors)
                {
                    state.AddError(NanoserviceMapper.FromError(error));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.AddError(NanoserviceMapper.Create(path, OperationSearchContent, "UnexpectedError",
                "Content search failed for this file.", ex.GetType().Name));
        }
    }

    /// <summary>
    /// Include / exclude rules evaluated by the PatternMatching nanoservice against the path relative to
    /// the root. Each rule set is tested against "rel" and "/rel" so that a leading "**/" (which the
    /// nanoservice collapses to "*/") also matches entries directly under the root.
    /// </summary>
    private bool PassesFilters(PathContext ctx, SearchState state, string path, string relative, CancellationToken token)
    {
        if (ctx.IncludeRequest is null && ctx.ExcludeRequest is null)
        {
            return true;
        }

        var normalized = relative.Replace('\\', '/');
        var anchored = "/" + normalized;

        if (ctx.IncludeRequest is { } include
            && !(Match(state, path, normalized, include, token) || Match(state, path, anchored, include, token)))
        {
            return false;
        }

        return ctx.ExcludeRequest is not { } exclude
            || !(Match(state, path, normalized, exclude, token) || Match(state, path, anchored, exclude, token));
    }

    private bool NameMatches(PathContext ctx, SearchState state, string path, string name, CancellationToken token)
        => Match(state, path, name, ctx.NameRequest, token);

    private bool Match(SearchState state, string path, string value, PM.SearchRequest request, CancellationToken token)
    {
        try
        {
            return _patternMatcher.IsPatternMatching(value, request, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.AddError(NanoserviceMapper.Create(path, OperationPatternMatching, "UnexpectedError",
                "Pattern matching failed for this entry.", ex.GetType().Name));
            return false;
        }
    }

    private static bool IsValidRegex(string pattern, out Exception? exception)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant);
            exception = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            exception = ex;
            return false;
        }
    }

    private const int ChunksPerCore = 4;
    private static readonly TimeSpan MaxTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private sealed record PathContext(
        SearchRequest Request,
        string Root,
        PM.SearchRequest NameRequest,
        PM.SearchRequest? IncludeRequest,
        PM.SearchRequest? ExcludeRequest,
        SC.SearchRequest ContentRequest);

    /// <summary>
    /// Thread-safe holder for searchStatistics, SearchResponse and SearchProgress. Results are kept
    /// per traversal index so the final order is deterministic despite concurrent processing.
    /// </summary>
    private sealed class SearchState
    {
        private readonly object _gate = new();
        private readonly ISearchProgressReporter? _reporter;
        private readonly SearchProgress _progress = new();
        private List<SearchResult>?[] _results = Array.Empty<List<SearchResult>?>();
        private int _totalPaths;
        private int _processedPaths;
        private bool _reporterFailed;

        public SearchState(ISearchProgressReporter? reporter) => _reporter = reporter;

        public SearchResponse Response { get; } = new();
        public SearchStatistics Statistics { get; } = new();
        public bool Fatal { get; set; }

        public void BeginProcessing(int totalPaths)
        {
            lock (_gate)
            {
                _totalPaths = totalPaths;
                _results = new List<SearchResult>?[totalPaths];
                Statistics.DirectoriesVisited = 1; // the root
            }
        }

        public void AddError(SearchError error)
        {
            lock (_gate) Response.Errors.Add(error);
        }

        public void DirectoryProcessed(string path)
        {
            lock (_gate)
            {
                _processedPaths++;
                Statistics.DirectoriesVisited++;
                _progress.DirectoriesProcessed++;
                _progress.CurrentDirectory = path;
            }
        }

        public void FileProcessed(string path)
        {
            lock (_gate)
            {
                _processedPaths++;
                Statistics.FilesVisited++;
                _progress.FilesProcessed++;
                _progress.CurrentFile = path;
                _progress.CurrentDirectory = Path.GetDirectoryName(path);
            }
        }

        public void AddFolderMatch(int index, SearchResult result) => Add(index, result, isFolder: true, isContent: false);

        public void AddFileMatch(int index, SearchResult result) => Add(index, result, isFolder: false, isContent: false);

        public void AddContentMatch(int index, SearchResult result) => Add(index, result, isFolder: false, isContent: true);

        private void Add(int index, SearchResult result, bool isFolder, bool isContent)
        {
            lock (_gate)
            {
                var firstResultForPath = _results[index] is null;
                (_results[index] ??= new List<SearchResult>()).Add(result);

                if (isFolder)
                {
                    Statistics.FoldersMatched++;
                }
                else if (firstResultForPath)
                {
                    // A file counts once in FilesMatched, whether it matched by name, content, or both.
                    Statistics.FilesMatched++;
                }

                if (isContent)
                {
                    Statistics.ContentMatches++;
                }

                Report();
            }
        }

        /// <summary>Reports a snapshot of SearchProgress. Called under the lock, so percentages never go backwards.</summary>
        private void Report()
        {
            if (_reporter is null || _reporterFailed)
            {
                return;
            }

            _progress.ProgressPercentage = _totalPaths == 0 ? 100 : Math.Min(100, _processedPaths * 100.0 / _totalPaths);
            try
            {
                _reporter.Report(new SearchProgress
                {
                    CurrentDirectory = _progress.CurrentDirectory,
                    CurrentFile = _progress.CurrentFile,
                    DirectoriesProcessed = _progress.DirectoriesProcessed,
                    FilesProcessed = _progress.FilesProcessed,
                    ProgressPercentage = _progress.ProgressPercentage,
                });
            }
            catch (Exception ex)
            {
                // A faulty reporter must not stop the search; report it once and stop calling it.
                _reporterFailed = true;
                Response.Errors.Add(NanoserviceMapper.Create(string.Empty, OperationReportProgress, "ProgressReporterFailed",
                    "The progress reporter threw an exception.", ex.GetType().Name));
            }
        }

        public SearchResponse Complete(TimeSpan elapsed, bool callerCancelled)
        {
            lock (_gate)
            {
                Response.Cancelled |= callerCancelled;
                Response.Succeeded = !Response.Cancelled && !Fatal;
                Response.ElapsedTime = elapsed;
                Statistics.ElapsedTime = elapsed;
                Response.Statistics = Statistics;
                foreach (var list in _results)
                {
                    if (list is not null)
                    {
                        Response.SearchResults.AddRange(list);
                    }
                }
                return Response;
            }
        }
    }
}
