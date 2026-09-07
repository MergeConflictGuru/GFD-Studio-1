using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Index;
using GFDStudio.AnimationMatching.Search;
using GFDStudio.AnimationMatching.Stitching;
using GFDStudio.AnimationMatching.UI;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// UI-agnostic controller for the animation matching mode. The host bridges GFD Studio's
/// currently retargeted animation/model representation into IAnimationClip.
/// </summary>
public sealed class AnimationMatchingModeController : IDisposable
{
    private readonly IGfdAnimationMatchingHost _host;
    private readonly IAnimationMatchingModeView _view;
    private readonly AnimationMatchOptions _options;
    private AnimationSearchDatabase? _database;
    private string? _databaseContextSignature;
    private IAnimationClip? _sourceForResults;
    private StitchedAnimation? _stitched;
    private CancellationTokenSource? _work;
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    public AnimationMatchingModeController(
        IGfdAnimationMatchingHost host,
        IAnimationMatchingModeView view,
        AnimationMatchOptions? options = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _options = options ?? new AnimationMatchOptions();
        _options.Validate();

        _view.SearchRequested += OnSearchRequested;
        _view.ReindexRequested += OnReindexRequested;
        _view.CandidateActivated += OnCandidateActivated;
        _view.ExportRequested += OnExportRequested;
        _view.ThumbnailRequested += OnThumbnailRequested;
    }

    private IAnimationClip? CurrentSource => _host.CurrentAnimation;
    private IReadOnlyList<IAnimationClip> CurrentCorpus => _host.SearchableAnimations;
    private string? CurrentContextSignature =>
        _host is IAnimationMatchingCacheHost cacheHost ? cacheHost.AnimationMatchingCorpusSignature : null;

    public void SyncSourceFromHost()
    {
        var source = CurrentSource;
        if (source is null) return;
        _sourceForResults = source;
        _stitched = null;
        _view.SetSource(source.DisplayName, source.FrameCount, source.FramesPerSecond);
    }

    private async void OnSearchRequested(object? sender, EventArgs e)
    {
        var source = _sourceForResults ?? CurrentSource;
        if (source is null)
        {
            _view.SetStatus("Load an animation first.");
            return;
        }

        RestartWork();
        _view.SetBusy(true, "Preparing animation match…");
        try
        {
            await BuildIndexAsync(force: false);
            if (_database is null || _work?.IsCancellationRequested != false) return;

            _view.SetStatus("Searching…");
            var matcher = new AnimationMatcher(_database);
            var selection = _view.Selection;
            var results = await Task.Run(() => matcher.Search(
                source,
                selection?.start,
                selection?.end,
                _work.Token), _work.Token);

            _sourceForResults = source;
            _stitched = null;
            _view.SetResults(results);
            _view.SetStatus(results.Count == 0 ? "No matches found" : $"{results.Count:N0} matches");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.SetStatus(ex.Message); }
        finally { _view.SetBusy(false); }
    }

    private async void OnReindexRequested(object? sender, EventArgs e)
    {
        RestartWork();
        try { await BuildIndexAsync(force: true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.SetStatus(ex.Message); }
    }

    private async Task BuildIndexAsync(bool force)
    {
        var contextSignature = CurrentContextSignature;
        if (_database is not null && !force &&
            string.Equals(_databaseContextSignature, contextSignature, StringComparison.Ordinal))
            return;

        // Do not synchronously dispose an older mapped database here: a just-cancelled search task
        // may still be unwinding on another thread. The mapping is opened FileShare.Delete and has a
        // finalizer, so an atomic cache replacement is safe and the old view can die with its reader.
        _database = null;
        _databaseContextSignature = null;

        RestartWork();
        _view.SetBusy(true, "Indexing animations…");
        try
        {
            var corpus = new AnimationCorpus(CurrentCorpus);
            if (corpus.Clips.Count == 0)
                throw new InvalidOperationException("No animations with a resolvable source model are available for matching.");

            if (!force && _host is IAnimationMatchingCacheHost cacheHost)
            {
                var cacheSignature = contextSignature ?? cacheHost.AnimationMatchingCorpusSignature;
                _view.SetStatus("Loading animation match index…");
                var cacheProgress = new Progress<string>(message => _view.SetStatus(message));
                _database = await Task.Run(() => AnimationIndexCache.TryLoad(
                    cacheHost.AnimationMatchingCachePath,
                    corpus,
                    _options,
                    cacheSignature,
                    cacheProgress), _work!.Token);
                if (_database is not null)
                {
                    _databaseContextSignature = contextSignature;
                    _view.SetStatus($"Loaded {_database.SampleCount:N0} indexed poses from cache");
                    return;
                }
            }

            var progress = new Progress<(int done, int total)>(p =>
                _view.SetStatus($"Indexing {p.done:N0}/{p.total:N0} poses…"));
            _database = await AnimationSearchDatabase.BuildAsync(corpus, _options, progress, _work!.Token);
            _databaseContextSignature = contextSignature;

            if (_host is IAnimationMatchingCacheHost writeCacheHost)
            {
                try
                {
                    var builtDatabase = _database;
                    var cacheSignature = contextSignature ?? writeCacheHost.AnimationMatchingCorpusSignature;
                    _view.SetStatus("Saving memory-mapped animation match index…");
                    await Task.Run(() => AnimationIndexCache.Save(
                        writeCacheHost.AnimationMatchingCachePath,
                        builtDatabase,
                        cacheSignature), _work.Token);

                    // Search from the same representation future launches use. This also lets the
                    // huge temporary FP32 build arrays become collectible immediately after saving.
                    _view.SetStatus("Opening memory-mapped animation match index…");
                    var cacheProgress = new Progress<string>(message => _view.SetStatus(message));
                    var mappedDatabase = await Task.Run(() => AnimationIndexCache.TryLoad(
                        writeCacheHost.AnimationMatchingCachePath,
                        corpus,
                        _options,
                        cacheSignature,
                        cacheProgress), _work.Token);
                    if (mappedDatabase is not null)
                        _database = mappedDatabase;
                }
                catch
                {
                    // Cache failure must never make matching fail; keep the freshly built DB.
                }
            }
            _view.SetStatus($"Indexed {_database.SampleCount:N0} poses from {corpus.Clips.Count:N0} animations");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _view.SetStatus(ex.Message);
            _database = null;
            _databaseContextSignature = null;
        }
        finally { _view.SetBusy(false); }
    }

    private void OnCandidateActivated(object? sender, AnimationMatchResult result)
    {
        var source = _sourceForResults ?? CurrentSource;
        if (source is null) return;
        var blend = _view.BlendingEnabled ? _view.BlendSeconds : 0f;
        _stitched = new StitchedAnimation(source, result.SourceFrame, result.Candidate, result.CandidateFrame, blend);
        _view.SetCombinedTimeline(_stitched.FrameCount, result.SourceFrame);
        _host.PreviewAnimation(_stitched, result.SourceFrame);
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (_stitched is null)
        {
            _view.SetStatus("Choose a candidate before exporting.");
            return;
        }
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
        var entered = false;
        try
        {
            await _thumbnailGate.WaitAsync();
            entered = true;
            var frames = await _host.RenderCandidateThumbnailAsync(
                request.Result.Candidate,
                request.Result.CandidateFrame,
                request.Width,
                request.Height,
                CancellationToken.None);
            request.Complete(frames);
        }
        catch { request.Complete(null); }
        finally
        {
            if (entered)
                _thumbnailGate.Release();
        }
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
