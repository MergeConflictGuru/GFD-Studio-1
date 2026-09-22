using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// The GFD-specific bridge used by animation matching.
/// </summary>
public interface IGfdAnimationMatchingHost
{
    IAnimationClip? CurrentAnimation { get; }
    IReadOnlyList<IAnimationClip> SearchableAnimations { get; }

    /// <summary>Displays a clip in the existing left model viewer and seeks to frame zero.</summary>
    void PreviewAnimation(IAnimationClip clip, int transitionFrame = -1);

    /// <summary>Loads a clip as the new main animation without stitching it to the previous source.</summary>
    Task OpenAnimationAsync(IAnimationClip clip, CancellationToken cancellationToken);

    /// <summary>Produces animated small same-model frames for a candidate result. May return null.</summary>
    Task<IReadOnlyList<Image>> RenderCandidateThumbnailAsync(IAnimationClip clip, int frame, int width, int height, CancellationToken cancellationToken);

    /// <summary>Exports a stitched/resampled clip using the normal GFD Studio animation export path.</summary>
    Task ExportAnimationAsync(IAnimationClip clip, CancellationToken cancellationToken);

    /// <summary>Exports the aligned/cut source and candidate sides of a stitched clip separately.</summary>
    Task ExportAnimationPartsAsync(IAnimationClip clip, CancellationToken cancellationToken);
}

/// <summary>Optional host capability enabling persistent animation descriptor caching.</summary>
public interface IAnimationMatchingCacheHost
{
    string AnimationMatchingCachePath { get; }
    string AnimationMatchingCorpusSignature { get; }
}

/// <summary>
/// Source-model corpus provider for the global matcher. Implementations must expose clips in the
/// canonical skeleton space; the active showroom target must not affect these members.
/// </summary>
public interface IAnimationMatchingCorpusHost
{
    bool AnimationMatchingCorpusReady { get; }
    IAnimationClip? CurrentAnimationForMatching { get; }
    IReadOnlyList<IAnimationClip> SearchableAnimationsForMatching { get; }
    string AnimationMatchingContextSignature { get; }
}
