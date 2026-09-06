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

        private AnimationRetargetMap( Model originalModel, Model targetModel )
        {
            mOriginalNodes = CreateNodeLookup( originalModel.Nodes );
            mTargetNodes = CreateNodeLookup( targetModel.Nodes );
            mTargetNodeIds = targetModel.Nodes
                .Select( ( node, index ) => ( node, index ) )
                .GroupBy( x => x.node )
                .ToDictionary( x => x.Key, x => x.First().index );

            mTargetNodesByRole = new Dictionary<string, Node>( StringComparer.OrdinalIgnoreCase );
            foreach ( var node in targetModel.Nodes )
            {
                var role = GetSkeletonRole( node.Name );
                if ( role != null && !mTargetNodesByRole.ContainsKey( role ) )
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
                return "root";

            var sourceName = normalized;
            if ( sourceName.StartsWith( "Bip01 ", StringComparison.OrdinalIgnoreCase ) )
                sourceName = sourceName.Substring( 6 );

            if ( sourceName.Equals( "Bip01", StringComparison.OrdinalIgnoreCase ) )
                return "root";
            if ( sourceName.Equals( "Pelvis", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Hips", StringComparison.OrdinalIgnoreCase ) )
                return "hips";
            if ( sourceName.Equals( "Spine", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Spine1", StringComparison.OrdinalIgnoreCase ) ||
                 sourceName.Equals( "Spine2", StringComparison.OrdinalIgnoreCase ) )
                return sourceName.ToLowerInvariant();
            if ( sourceName.Equals( "Neck0", StringComparison.OrdinalIgnoreCase ) )
                return "spine2";
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
            if ( sideName.StartsWith( "ThighTwist", StringComparison.OrdinalIgnoreCase ) )
                return side + "uplegroll" + GetSuffixNumber( sideName, "ThighTwist" );
            if ( sideName.StartsWith( "ForeTwist", StringComparison.OrdinalIgnoreCase ) )
                return side + "forearmroll" + GetSuffixNumber( sideName, "ForeTwist" );

            if ( sideName.StartsWith( "Finger", StringComparison.OrdinalIgnoreCase ) )
                return GetFingerRole( side, sideName );

            // Dance models use these names for the same roll bones. Supporting
            // them here also allows a Dance skeleton to be used as the source.
            if ( sideName.StartsWith( "UpLeg_Roll_", StringComparison.OrdinalIgnoreCase ) )
                return side + "uplegroll" + GetTrailingNumber( sideName );
            if ( sideName.StartsWith( "ForeArm_Roll_", StringComparison.OrdinalIgnoreCase ) )
                return side + "forearmroll" + GetTrailingNumber( sideName );

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

        private static string GetFingerRole( string side, string name )
        {
            var digits = new string( name.Substring( 6 ).TakeWhile( char.IsDigit ).ToArray() );
            if ( digits.Length == 0 )
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

        private static int GetSuffixNumber( string name, string prefix )
        {
            var suffix = name.Substring( prefix.Length );
            return string.IsNullOrEmpty( suffix ) ? 1 : int.TryParse( suffix, out var number ) ? number + 1 : -1;
        }

        private static int GetTrailingNumber( string name )
        {
            var digits = new string( name.Reverse().TakeWhile( char.IsDigit ).Reverse().ToArray() );
            return int.TryParse( digits, out var number ) ? number : -1;
        }
    }
}
