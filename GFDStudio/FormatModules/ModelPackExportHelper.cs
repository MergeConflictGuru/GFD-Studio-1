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
        public static void ExportFile( ModelPack modelPack, string path )
        {
            var ext = Path.GetExtension( path );
            if ( ext.Equals( ".fbx", StringComparison.OrdinalIgnoreCase ) )
            {
                FbxSdkModelPackExporter.ExportFile( modelPack, path, new FbxSdkModelPackExporterConfig() );
                if ( modelPack.Model != null && modelPack.AnimationPack?.Animations?.Count > 0 )
                    FbxSdkAnimationExporter.AppendFile( modelPack.Model, modelPack.AnimationPack, path );
            }
            else
            {
                AssimpNetModelPackExporter.ExportFile( modelPack, path );
            }
        }

        public static void ExportFile( ModelPack modelPack, AnimationPack animationPack, string path )
        {
            if ( modelPack == null )
                throw new ArgumentNullException( nameof( modelPack ) );
            if ( modelPack.Model == null )
                throw new InvalidOperationException( "The model pack has no model to animate." );
            if ( animationPack == null )
                throw new ArgumentNullException( nameof( animationPack ) );
            if ( !Path.GetExtension( path ).Equals( ".fbx", StringComparison.OrdinalIgnoreCase ) )
                throw new NotSupportedException( "Combined model + animation export currently supports FBX only." );

            FbxSdkModelPackExporter.ExportFile( modelPack, path, new FbxSdkModelPackExporterConfig() );
            FbxSdkAnimationExporter.AppendFile( modelPack.Model, animationPack, path );
        }
    }
}
