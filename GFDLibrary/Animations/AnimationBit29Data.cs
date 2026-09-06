using System.Collections.Generic;
using GFDLibrary.IO;
using GFDLibrary.Models;

namespace GFDLibrary.Animations
{
    public sealed class AnimationBit29Data : Resource
    {
        public override ResourceType ResourceType => ResourceType.AnimationBit29Data;

        public Animation Field00 { get; set; }

        public float Field10 { get; set; }

        public Animation Field04 { get; set; }

        public float Field14 { get; set; }

        public Animation Field08 { get; set; }

        public float Field18 { get; set; }

        public Animation Field0C { get; set; }

        public float Field1C { get; set; }

        public AnimationBit29Data()
        {
        }

        public AnimationBit29Data(uint version) : base(version)
        {
            
        }

        protected override void ReadCore( ResourceReader reader )
        {
            Field00 = reader.ReadResource<Animation>( Version );
            Field10 = reader.ReadSingle();
            Field04 = reader.ReadResource<Animation>( Version );
            Field14 = reader.ReadSingle();
            Field08 = reader.ReadResource<Animation>( Version );
            Field18 = reader.ReadSingle();
            Field0C = reader.ReadResource<Animation>( Version );
            Field1C = reader.ReadSingle();
        }

        protected override void WriteCore( ResourceWriter writer )
        {
            writer.WriteResource( Field00 );
            writer.WriteSingle( Field10 );
            writer.WriteResource( Field04 );
            writer.WriteSingle( Field14 );
            writer.WriteResource( Field08 );
            writer.WriteSingle( Field18 );
            writer.WriteResource( Field0C );
            writer.WriteSingle( Field1C );
        }

        public void FixTargetIds( Model model )
        {
            Field00.FixTargetIds( model );
            Field04.FixTargetIds( model );
            Field08.FixTargetIds( model );
            Field0C.FixTargetIds( model );
        }

        public void Retarget( Model originalModel, Model newModel, bool fixArms )
        {
            Field00.Retarget( originalModel, newModel, fixArms );
            Field04.Retarget( originalModel, newModel, fixArms );
            Field08.Retarget( originalModel, newModel, fixArms );
            Field0C.Retarget( originalModel, newModel, fixArms );
        }

        internal void Retarget( AnimationRetargetMap retargetMap, bool fixArms )
        {
            Field00.Retarget( retargetMap, fixArms );
            Field04.Retarget( retargetMap, fixArms );
            Field08.Retarget( retargetMap, fixArms );
            Field0C.Retarget( retargetMap, fixArms );
        }

        internal void SetVersion( uint version )
        {
            Version = version;
            Field00.SetVersion( version );
            Field04.SetVersion( version );
            Field08.SetVersion( version );
            Field0C.SetVersion( version );
        }
    }
}
