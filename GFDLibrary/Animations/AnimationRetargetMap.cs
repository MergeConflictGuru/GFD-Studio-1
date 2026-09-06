using System;
using System.Collections.Generic;
using System.Linq;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    /// <summary>
    /// Resolves animation node names between the skeleton naming conventions used
    /// by the different Persona games. Exact names always win; the aliases below
    /// are only used when the source and target models use different conventions.
    /// </summary>
    internal sealed class AnimationRetargetMap
    {
        private readonly Dictionary<string, Node> mOriginalNodes;
        private readonly Dictionary<string, Node> mTargetNodes;
        private readonly Dictionary<string, Node> mTargetNodesByRole;
        private readonly Dictionary<Node, int> mTargetNodeIds;
        private Node mSyntheticRootNode;
        internal Model SourceModel { get; }
        internal Model TargetModel { get; }
        internal bool UsesDifferentHumanoidHierarchy { get; }

        private AnimationRetargetMap( Model originalModel, Model targetModel )
        {
            SourceModel = originalModel;
            TargetModel = targetModel;
            mOriginalNodes = CreateNodeLookup( originalModel.Nodes );
            mTargetNodes = CreateNodeLookup( targetModel.Nodes );
            UsesDifferentHumanoidHierarchy =
                (mOriginalNodes.ContainsKey("Bip01 Pelvis") && mTargetNodes.ContainsKey("Hips")) ||
                (mOriginalNodes.ContainsKey("Hips") && mTargetNodes.ContainsKey("Bip01 Pelvis"));
            mTargetNodeIds = targetModel.Nodes
                .Select( ( node, index ) => ( node, index ) )
                .GroupBy( x => x.node )
                .ToDictionary( x => x.Key, x => x.First().index );

            mTargetNodesByRole = new Dictionary<string, Node>( StringComparer.OrdinalIgnoreCase );
            foreach ( var node in targetModel.Nodes )
            {
                var roles = GetHairRoles( node.Name ).ToList();
                if ( roles.Count == 0 )
                {
                    var role = GetSkeletonRole( node.Name );
                    if ( role != null )
                        roles.Add( role );
                }

                foreach ( var role in roles.Distinct( StringComparer.OrdinalIgnoreCase ) )
                    if ( !mTargetNodesByRole.ContainsKey( role ) )
                        mTargetNodesByRole.Add( role, node );
            }
        }

        public static AnimationRetargetMap Create( Model originalModel, Model targetModel )
        {
            if ( originalModel == null )
                throw new ArgumentNullException( nameof( originalModel ) );
            if ( targetModel == null )
                throw new ArgumentNullException( nameof( targetModel ) );
            if ( originalModel.RootNode == null )
                throw new ArgumentException( "The original model has no root node.", nameof( originalModel ) );
            if ( targetModel.RootNode == null )
                throw new ArgumentException( "The target model has no root node.", nameof( targetModel ) );

            return new AnimationRetargetMap( originalModel, targetModel );
        }

        public bool TryGetTarget( string sourceName, out Node sourceNode, out Node targetNode )
        {
            sourceNode = null;
            targetNode = null;

            if ( string.IsNullOrEmpty( sourceName ) )
                return false;

            // The Dance motion root corresponds to Bip01, not P5's axis
            // conversion node also named root. Their extra ancestors are
            // evaluated by the pose baker, never assigned duplicate controllers.
            if ( UsesDifferentHumanoidHierarchy )
            {
                if (mOriginalNodes.ContainsKey("Bip01 Pelvis"))
                {
                    if (sourceName == "root" || sourceName == "rot" || sourceName == "RootNode")
                        return false;
                    if (sourceName == "Bip01")
                    {
                        sourceNode = mOriginalNodes[sourceName];
                        return mTargetNodes.TryGetValue("root", out targetNode);
                    }
                }
                else if (sourceName == "root")
                {
                    sourceNode = mOriginalNodes[sourceName];
                    return mTargetNodes.TryGetValue("Bip01", out targetNode);
                }
                else if (sourceName == "RootNode")
                    return false;
            }

            if ( !mOriginalNodes.TryGetValue( sourceName, out sourceNode ) )
            {
                // Older Persona 5 models do not contain the file-level RootNode,
                // although their animation packs can still contain a controller
                // for it. Its bind transform is the identity transform.
                if ( string.Equals( sourceName, "RootNode", StringComparison.OrdinalIgnoreCase ) )
                {
                    mSyntheticRootNode ??= new Node( "RootNode" );
                    sourceNode = mSyntheticRootNode;
                }
                else
                {
                    return false;
                }
            }

            if ( mTargetNodes.TryGetValue( sourceName, out targetNode ) )
                return true;

            foreach ( var hairRole in GetHairRoles( sourceName ) )
            {
                if ( mTargetNodesByRole.TryGetValue( hairRole, out targetNode ) )
                    return true;
            }

            var role = GetSkeletonRole( sourceName );
            return role != null && mTargetNodesByRole.TryGetValue( role, out targetNode );
        }

        public bool TryGetTargetId( Node targetNode, out int targetId )
        {
            return mTargetNodeIds.TryGetValue( targetNode, out targetId );
        }

        private static Dictionary<string, Node> CreateNodeLookup( IEnumerable<Node> nodes )
        {
            var lookup = new Dictionary<string, Node>( StringComparer.OrdinalIgnoreCase );
            foreach ( var node in nodes )
            {
                if ( !lookup.ContainsKey( node.Name ) )
                    lookup.Add( node.Name, node );
            }

            return lookup;
        }

        private static string GetSkeletonRole( string name )
        {
            if ( string.IsNullOrWhiteSpace( name ) )
                return null;

            var normalized = name.Trim();
            if ( normalized.Equals( "RootNode", StringComparison.OrdinalIgnoreCase ) )
                return "rootnode";
            if ( normalized.Equals( "root", StringComparison.OrdinalIgnoreCase ) )
                return "root";
            if ( normalized.Equals( "rot", StringComparison.OrdinalIgnoreCase ) )
                return null;

            var hairRole = GetHairRoles( normalized ).FirstOrDefault();
            if ( hairRole != null )
                return hairRole;

            var sourceName = normalized;
            if ( sourceName.StartsWith( "Bip01 ", StringComparison.OrdinalIgnoreCase ) )
                sourceName = sourceName.Substring( 6 );

            if ( sourceName.Equals( "Bip01", StringComparison.OrdinalIgnoreCase ) )
                return "motionroot";
            if ( sourceName.Equals( "Pelvis", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Hips", StringComparison.OrdinalIgnoreCase ) )
                return "hips";
            if ( sourceName.Equals( "Spine", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Spine1", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Spine2", StringComparison.OrdinalIgnoreCase ) )
                return sourceName.ToLowerInvariant();
            if ( sourceName.Equals( "Neck0", StringComparison.OrdinalIgnoreCase ) )
                return null;
            if ( sourceName.Equals( "Neck", StringComparison.OrdinalIgnoreCase ) )
                return "neck";
            if ( sourceName.Equals( "Head", StringComparison.OrdinalIgnoreCase ) )
                return "head";

            var side = GetSide( sourceName, out var sideName );
            if ( side == null )
                return null;

            if ( sideName.Equals( "Clavicle", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "Shoulder", StringComparison.OrdinalIgnoreCase ) )
                return side + "shoulder";
            if ( sideName.Equals( "UpperArm", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "Arm", StringComparison.OrdinalIgnoreCase ) )
                return side + "arm";
            if ( sideName.Equals( "Forearm", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "ForeArm", StringComparison.OrdinalIgnoreCase ) )
                return side + "forearm";
            if ( sideName.Equals( "Hand", StringComparison.OrdinalIgnoreCase ) )
                return side + "hand";
            if ( sideName.StartsWith( "HandIndex", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "HandMiddle", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "HandRing", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "HandPinky", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "HandThumb", StringComparison.OrdinalIgnoreCase ) )
            {
                var namedFinger = sideName.Substring( 4 );
                var fingerEnd = namedFinger.TakeWhile( char.IsLetter ).Count();
                var fingerName = namedFinger.Substring( 0, fingerEnd ).ToLowerInvariant();
                var segment = GetTrailingNumber( namedFinger );
                return segment >= 1 && segment <= 3 ? side + fingerName + segment : null;
            }
            if ( sideName.Equals( "Thigh", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "UpLeg", StringComparison.OrdinalIgnoreCase ) )
                return side + "upleg";
            if ( sideName.Equals( "Calf", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "Leg", StringComparison.OrdinalIgnoreCase ) )
                return side + "leg";
            if ( sideName.Equals( "Foot", StringComparison.OrdinalIgnoreCase ) )
                return side + "foot";
            if ( sideName.Equals( "Toe0", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.Equals( "Toe2", StringComparison.OrdinalIgnoreCase ) )
                return side + "toe";

            if ( sideName.StartsWith( "Finger", StringComparison.OrdinalIgnoreCase ) )
                return GetFingerRole( side, sideName );


            return null;
        }

        private static string GetSide( string name, out string sideName )
        {
            sideName = name;

            if ( sideName.StartsWith( "Left", StringComparison.OrdinalIgnoreCase ) )
            {
                sideName = sideName.Substring( 4 );
                return "left";
            }
            if ( sideName.StartsWith( "Right", StringComparison.OrdinalIgnoreCase ) )
            {
                sideName = sideName.Substring( 5 );
                return "right";
            }
            if ( sideName.StartsWith( "L ", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "L_", StringComparison.OrdinalIgnoreCase ) )
            {
                sideName = sideName.Substring( 2 );
                return "left";
            }
            if ( sideName.StartsWith( "R ", StringComparison.OrdinalIgnoreCase ) ||
                 sideName.StartsWith( "R_", StringComparison.OrdinalIgnoreCase ) )
            {
                sideName = sideName.Substring( 2 );
                return "right";
            }

            return null;
        }

        private static IEnumerable<string> GetHairRoles( string name )
        {
            var roles = new List<string>();
            var danceParts = name.Split( '_', StringSplitOptions.RemoveEmptyEntries );
            if ( danceParts.Length == 3 && danceParts[0].Length == 1 && IsHairGroup( danceParts[0] ) )
            {
                var group = danceParts[0].ToLowerInvariant();
                if ( danceParts[1].Equals( "hair", StringComparison.OrdinalIgnoreCase ) &&
                     int.TryParse( danceParts[2], out var mainIndex ) )
                {
                    AddHairMainRoles( roles, group, null, mainIndex );
                    return roles;
                }

                if ( danceParts[1].StartsWith( "hair", StringComparison.OrdinalIgnoreCase ) &&
                     int.TryParse( danceParts[1].Substring( 4 ), out var chainIndex ) &&
                     int.TryParse( danceParts[2], out var danceBranchIndex ) )
                {
                    AddHairBranchRoles( roles, group, null, chainIndex, danceBranchIndex );
                    return roles;
                }
            }

            // Dance uses a family marker for the two long-hair chains, for
            // example L_B_longhair_00 and L_S_longhair_00. Keep the generic
            // role as a fallback for older hair files named L_hair_00.
            if ( danceParts.Length == 4 && danceParts[0].Length == 1 &&
                 IsHairGroup( danceParts[0] ) &&
                 (danceParts[1].Equals( "B", StringComparison.OrdinalIgnoreCase ) ||
                  danceParts[1].Equals( "S", StringComparison.OrdinalIgnoreCase )) &&
                 danceParts[2].Equals( "longhair", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse( danceParts[3], out var longHairIndex ) )
            {
                var group = danceParts[0].ToLowerInvariant();
                var family = danceParts[1].ToLowerInvariant();
                AddHairMainRoles( roles, group, family, longHairIndex );
                return roles;
            }

            if ( danceParts.Length == 3 && danceParts[0].Equals( "B", StringComparison.OrdinalIgnoreCase) &&
                 danceParts[1].Equals( "longhair", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse( danceParts[2], out var backHairIndex ) )
            {
                AddHairMainRoles( roles, "b", null, backHairIndex );
                return roles;
            }

            var parts = name.Replace( '_', ' ' ).Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            if ( parts.Length < 3 || !parts[0].Equals( "b", StringComparison.OrdinalIgnoreCase ) )
                return roles;

            var groups = new List<string>();
            var hairPart = -1;
            if ( IsHairGroup( parts[1] ) && parts[2].StartsWith( "hair", StringComparison.OrdinalIgnoreCase ) )
            {
                groups.Add( parts[1].ToLowerInvariant() );
                hairPart = 2;
            }
            else if ( IsHairGroup( parts[1] ) && parts.Length >= 4 &&
                      IsHairGroup( parts[2] ) && parts[3].StartsWith( "hair", StringComparison.OrdinalIgnoreCase ) )
            {
                // P5R also uses b l f hair01 / b r b hair01. Prefer the
                // side-specific Dance chain, then fall back to the generic
                // front/back chain when that is the only shape present.
                groups.Add( parts[1].ToLowerInvariant() );
                groups.Add( parts[2].ToLowerInvariant() );
                hairPart = 3;
            }

            if ( hairPart < 0 )
                return roles;

            var digits = new string( parts[hairPart].Substring( 4 ).TakeWhile( char.IsDigit ).ToArray() );
            if ( !int.TryParse( digits, out var sourceIndex ) || sourceIndex < 1 )
                return roles;

            var branchIndex = 0;
            var hasExplicitBranch = hairPart + 1 < parts.Length && int.TryParse( parts[hairPart + 1], out branchIndex );
            foreach ( var group in groups.Distinct( StringComparer.OrdinalIgnoreCase ) )
            {
                var family = group.Equals( "l", StringComparison.OrdinalIgnoreCase ) ||
                             group.Equals( "r", StringComparison.OrdinalIgnoreCase )
                    ? "b"
                    : null;
                if ( hasExplicitBranch )
                    AddHairBranchRoles( roles, group, family, sourceIndex, branchIndex );
                else if ( sourceIndex <= 4 )
                    AddHairMainRoles( roles, group, family, sourceIndex - 1 );
                else if ( sourceIndex == 5 )
                {
                    // In the P5R hair rig hair05 is the side lock branching
                    // from hair01. Dance calls that slot hair02_01.
                    AddHairBranchRoles( roles, group, family, 2, 1 );
                }
            }

            return roles;
        }

        private static void AddHairMainRoles( List<string> roles, string group, string family, int index )
        {
            if ( !string.IsNullOrWhiteSpace( family ) )
                roles.Add( $"hair:{group}:{family}:main:{index}" );
            roles.Add( $"hair:{group}:main:{index}" );
        }

        private static void AddHairBranchRoles(
            List<string> roles, string group, string family, int chainIndex, int branchIndex )
        {
            if ( !string.IsNullOrWhiteSpace( family ) )
                roles.Add( $"hair:{group}:{family}:branch:{chainIndex}:{branchIndex}" );
            roles.Add( $"hair:{group}:branch:{chainIndex}:{branchIndex}" );
        }

        private static bool IsHairGroup( string value )
        {
            return value.Equals( "l", StringComparison.OrdinalIgnoreCase ) ||
                   value.Equals( "r", StringComparison.OrdinalIgnoreCase ) ||
                   value.Equals( "f", StringComparison.OrdinalIgnoreCase ) ||
                   value.Equals( "b", StringComparison.OrdinalIgnoreCase );
        }

        private static string GetFingerRole( string side, string name )
        {
            var digits = new string( name.Substring( 6 ).TakeWhile( char.IsDigit ).ToArray() );
            if ( digits.Length == 0 || digits.Length > 2 )
                return null;
            if ( name.Length != 6 + digits.Length )
                return null;

            var finger = digits[ 0 ] - '0';
            if ( finger < 0 || finger > 4 )
                return null;

            var fingerName = finger switch
            {
                0 => "thumb",
                1 => "index",
                2 => "middle",
                3 => "ring",
                4 => "pinky",
                _ => null
            };
            if ( fingerName == null )
                return null;

            var segment = digits.Length == 1 ? 1 : digits[ 1 ] - '0' + 1;
            return segment >= 1 && segment <= 3 ? side + fingerName + segment : null;
        }

        private static int GetTrailingNumber( string name )
        {
            var digits = new string( name.Reverse().TakeWhile( char.IsDigit ).Reverse().ToArray() );
            return int.TryParse( digits, out var number ) ? number : -1;
        }
    }
}
