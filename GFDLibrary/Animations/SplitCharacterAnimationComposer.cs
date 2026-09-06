using System;
using System.Collections.Generic;
using System.Linq;

namespace GFDLibrary.Animations
{
    public static class SplitCharacterAnimationComposer
    {
        /// <summary>
        /// Adds split face/hair tracks to a body animation while keeping the
        /// body's shared skeleton tracks authoritative. Face and hair packs
        /// repeat shared RootNode/head tracks because they can also be played
        /// on their standalone models.
        /// </summary>
        public static Animation AddComponentTracks(Animation body, params Animation[] components)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));

            var bodyTargets = new HashSet<string>(
                body.Controllers
                    .Where(controller => controller.TargetKind == TargetKind.Node &&
                                         !string.IsNullOrWhiteSpace(controller.TargetName))
                    .Select(controller => controller.TargetName),
                StringComparer.OrdinalIgnoreCase);

            foreach (var component in components ?? Array.Empty<Animation>())
            {
                if (component == null)
                    continue;

                foreach (var controller in component.Controllers)
                {
                    if (controller.TargetKind == TargetKind.Node &&
                        bodyTargets.Contains(controller.TargetName))
                        continue;

                    body.Controllers.Add(controller);
                }
            }

            return body;
        }
    }
}
