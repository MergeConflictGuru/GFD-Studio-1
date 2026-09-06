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

    /// <summary>Produces animated small same-model frames for a candidate result. May return null.</summary>
    Task<IReadOnlyList<Image>> RenderCandidateThumbnailAsync(IAnimationClip clip, int frame, int width, int height, CancellationToken cancellationToken);

    /// <summary>Exports a stitched/resampled clip using the normal GFD Studio animation export path.</summary>
    Task ExportAnimationAsync(IAnimationClip clip, CancellationToken cancellationToken);
}

/// <summary>Optional host capability enabling persistent animation descriptor caching.</summary>
public interface IAnimationMatchingCacheHost
{
    string AnimationMatchingCachePath { get; }
    string AnimationMatchingCorpusSignature { get; }
}

/// <summary>
/// Optional correctness-oriented corpus provider. Hosts with enough source-model context should
/// implement this instead of relying on the legacy raw Character Browser list. It lets the matcher
/// use a canonical source identity, a corpus that has been validated/retargeted for the active
/// target skeleton, and a context signature that also invalidates the in-memory index.
/// </summary>
public interface IAnimationMatchingCorpusHost
{
    IAnimationClip? CurrentAnimationForMatching { get; }
    IReadOnlyList<IAnimationClip> SearchableAnimationsForMatching { get; }
    string AnimationMatchingContextSignature { get; }
}
