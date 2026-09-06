using System;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Index;
using GFDStudio.AnimationMatching.Search;
using GFDStudio.AnimationMatching.Stitching;
using GFDStudio.AnimationMatching.UI;

namespace GFDStudio.AnimationMatching.Integration;

public sealed class AnimationMatchingModeController : IDisposable
{
    private readonly IGfdAnimationMatchingHost _host;
    private readonly AnimationMatchingModeControl _view;
    private readonly AnimationMatchOptions _options;
    private AnimationSearchDatabase? _database;
    private CancellationTokenSource? _work;
    private StitchedAnimation? _stitched;
    private IAnimationClip? _sourceForResults;

    public AnimationMatchingModeController(IGfdAnimationMatchingHost host, AnimationMatchingModeControl view, AnimationMatchOptions? options = null)
    {
        _host = host;
        _view = view;
        _options = options ?? new AnimationMatchOptions();
        _view.SearchRequested += OnSearchRequested;
        _view.ReindexRequested += OnReindexRequested;
        _view.CandidateActivated += OnCandidateActivated;
        _view.ExportRequested += OnExportRequested;
        _view.ThumbnailRequested += OnThumbnailRequested;
    }

    public void SyncSourceFromHost()
    {
        var source = _host.CurrentAnimation;
        _sourceForResults = source;
        _stitched = null;
        _view.SetSource(source?.DisplayName ?? "No animation", source?.FrameCount ?? 1, source?.FramesPerSecond ?? 30f);
    }

    private async void OnReindexRequested(object? sender, EventArgs e) => await BuildIndexAsync(force: true);

    private async void OnSearchRequested(object? sender, EventArgs e)
    {
        try
        {
            var source = _host.CurrentAnimation;
            if (source is null) { _view.SetStatus("Open a source animation first."); return; }
            _sourceForResults = source;
            if (_database is null) await BuildIndexAsync(force: false);
            if (_database is null) return;

            RestartWork();
            _view.SetBusy(true, "Searching…");
            var matcher = new AnimationMatcher(_database);
            var selection = _view.Selection;
            var results = await Task.Run(() => selection.HasValue
                ? matcher.Search(source, selection.Value.start, selection.Value.end, _work!.Token)
                : matcher.Search(source, cancellationToken: _work!.Token), _work!.Token);
            _view.SetResults(results);
            _view.SetStatus($"{results.Count} matches");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.SetStatus(ex.Message); }
        finally { _view.SetBusy(false); }
    }

    private async Task BuildIndexAsync(bool force)
    {
        if (_database is not null && !force) return;
        RestartWork();
        _view.SetBusy(true, "Indexing animations…");
        try
        {
            var corpus = new AnimationCorpus(_host.SearchableAnimations);
            if (!force && _host is IAnimationMatchingCacheHost cacheHost)
            {
                _view.SetStatus("Loading animation match index…");
                _database = await Task.Run(() => AnimationIndexCache.TryLoad(
                    cacheHost.AnimationMatchingCachePath, corpus, _options, cacheHost.AnimationMatchingCorpusSignature), _work!.Token);
                if (_database is not null)
                {
                    _view.SetStatus($"Loaded {_database.SampleCount:N0} indexed poses from cache");
                    return;
                }
            }

            var progress = new Progress<(int done, int total)>(p => _view.SetStatus($"Indexing {p.done:N0}/{p.total:N0} poses…"));
            _database = await AnimationSearchDatabase.BuildAsync(corpus, _options, progress, _work!.Token);
            if (_host is IAnimationMatchingCacheHost writeCacheHost)
            {
                try
                {
                    AnimationIndexCache.Save(writeCacheHost.AnimationMatchingCachePath, _database, writeCacheHost.AnimationMatchingCorpusSignature);
                }
                catch { /* cache failure must never make matching fail */ }
            }
            _view.SetStatus($"Indexed {_database.SampleCount:N0} poses from {corpus.Clips.Count:N0} animations");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.SetStatus(ex.Message); _database = null; }
        finally { _view.SetBusy(false); }
    }

    private void OnCandidateActivated(object? sender, AnimationMatchResult result)
    {
        var source = _sourceForResults ?? _host.CurrentAnimation;
        if (source is null) return;
        var blend = _view.BlendingEnabled ? _view.BlendSeconds : 0f;
        _stitched = new StitchedAnimation(source, result.SourceFrame, result.Candidate, result.CandidateFrame, blend);
        _view.SetCombinedTimeline(_stitched.FrameCount, result.SourceFrame);
        _host.PreviewAnimation(_stitched, result.SourceFrame);
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (_stitched is null) { _view.SetStatus("Choose a candidate before exporting."); return; }
        RestartWork();
        try
        {
            _view.SetBusy(true, "Exporting…");
            await _host.ExportAnimationAsync(_stitched, _work!.Token);
            _view.SetStatus("Export complete");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.SetStatus(ex.Message); }
        finally { _view.SetBusy(false); }
    }

    private async void OnThumbnailRequested(object? sender, ThumbnailRequest request)
    {
        try
        {
            var image = await _host.RenderCandidateThumbnailAsync(request.Result.Candidate, request.Result.CandidateFrame, request.Width, request.Height, CancellationToken.None);
            request.Complete(image);
        }
        catch { request.Complete(null); }
    }

    private void RestartWork()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = new CancellationTokenSource();
    }

    public void Dispose()
    {
        _work?.Cancel();
        _work?.Dispose();
        _view.SearchRequested -= OnSearchRequested;
        _view.ReindexRequested -= OnReindexRequested;
        _view.CandidateActivated -= OnCandidateActivated;
        _view.ExportRequested -= OnExportRequested;
        _view.ThumbnailRequested -= OnThumbnailRequested;
    }
}
