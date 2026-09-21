using System;
using System.IO;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Conversion.AssimpNet;
using GFDLibrary.Conversion.FbxSdk;

namespace GFDStudio.FormatModules
{
    public static class ModelPackExportHelper
    {
        public static void ExportFile( ModelPack modelPack, string path, bool useUnrealBoneNames = false )
        {
            var ext = Path.GetExtension( path );
            if ( ext.Equals( ".fbx", StringComparison.OrdinalIgnoreCase ) )
            {
                // Plain FBX export is model-only. Animation baking is explicitly selected by the
                // caller so Save As can expose an "Include animations" option instead of silently
                // changing the contents of every FBX export.
                FbxSdkModelPackExporter.ExportFile(
                    modelPack,
                    path,
                    new FbxSdkModelPackExporterConfig { UseUnrealBoneNames = useUnrealBoneNames } );
            }
            else
            {
                AssimpNetModelPackExporter.ExportFile( modelPack, path );
            }
        }

        public static void ExportFile(
            ModelPack modelPack,
            AnimationPack animationPack,
            string path,
            bool useUnrealBoneNames = false )
        {
            if ( modelPack == null )
                throw new ArgumentNullException( nameof( modelPack ) );
            if ( modelPack.Model == null )
                throw new InvalidOperationException( "The model pack has no model to animate." );
            if ( animationPack == null )
                throw new ArgumentNullException( nameof( animationPack ) );
            if ( !Path.GetExtension( path ).Equals( ".fbx", StringComparison.OrdinalIgnoreCase ) )
                throw new NotSupportedException( "Combined model + animation export currently supports FBX only." );

            var config = new FbxSdkModelPackExporterConfig
            {
                UseUnrealBoneNames = useUnrealBoneNames
            };
            FbxSdkModelPackExporter.ExportFile( modelPack, path, config );
            FbxSdkAnimationExporter.AppendFile( modelPack.Model, animationPack, path, config );
        }
    }
}
