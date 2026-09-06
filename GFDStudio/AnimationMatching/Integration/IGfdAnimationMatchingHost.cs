using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Integration;

/// <summary>
/// The only GFD-specific bridge the mode needs. The showroom branch can implement this with its
/// existing GAP loader, in-memory retargeter, OpenGL model viewer and animation exporter.
/// </summary>
public interface IGfdAnimationMatchingHost
{
    IAnimationClip? CurrentAnimation { get; }
    IReadOnlyList<IAnimationClip> SearchableAnimations { get; }

    /// <summary>Displays a clip in the existing left model viewer and seeks to frame zero.</summary>
    void PreviewAnimation(IAnimationClip clip, int transitionFrame = -1);

    /// <summary>Produces a small same-model preview for a candidate result. May return null.</summary>
    Task<Image?> RenderCandidateThumbnailAsync(IAnimationClip clip, int frame, int width, int height, CancellationToken cancellationToken);

    /// <summary>Exports a stitched/resampled clip using the normal GFD Studio animation export path.</summary>
    Task ExportAnimationAsync(IAnimationClip clip, CancellationToken cancellationToken);
}

/// <summary>Optional host capability enabling persistent animation descriptor caching.</summary>
public interface IAnimationMatchingCacheHost
{
    string AnimationMatchingCachePath { get; }
    string AnimationMatchingCorpusSignature { get; }
}
