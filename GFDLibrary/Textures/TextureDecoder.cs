using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using GFDLibrary.Textures.DDS;
using GFDLibrary.Textures.GNF;
using GFDLibrary.Textures.Swizzle;
using BCnEncoder.Decoder;
using BCnEncoder.ImageSharp;
using BCnEncoder.Shared;
using Microsoft.Toolkit.HighPerformance;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;

namespace GFDLibrary.Textures
{
    public static class TextureDecoder
    {
        public static Bitmap Decode( Texture texture )
        {
            return Decode( texture.Data, texture.Format );
        }

        public static Bitmap Decode( FieldTexturePS3 texture )
        {
            var ddsBytes = DecodeToDDS( texture );
            return DecodeDDS( ddsBytes );
        }

        public static Bitmap Decode( GNFTexture texture )
        {
            var ddsBytes = DecodeToDDS( texture );
            return DecodeDDS( ddsBytes );
        }

        public static byte[] DecodeToDDS( FieldTexturePS3 texture )
        {
            var surfaceFormat = DDSPixelFormatFourCC.DXT1;
            if ( texture.Flags.HasFlag( FieldTextureFlags.DXT3 ) )
            {
                surfaceFormat = DDSPixelFormatFourCC.DXT3;
            }
            else if ( texture.Flags.HasFlag( FieldTextureFlags.DXT5 ) )
            {
                surfaceFormat = DDSPixelFormatFourCC.DXT5;
            }

            var ddsHeader = new DDSHeader( texture.Width, texture.Height, surfaceFormat )
            {
                MipMapCount = texture.MipMapCount
            };
            if ( texture.MipMapCount > 1 )
                ddsHeader.Flags |= DDSHeaderFlags.MipMapCount;

            using var ddsStream = ddsHeader.Save();
            ddsStream.Write( texture.Data, 0, texture.DataLength );
            return ddsStream.ToArray();
        }

        public static byte[] DecodeToDDS( GNFTexture texture )
        {
            var imageFormat = DDSPixelFormatFourCC.DXT5;
            var dx10ImageFormat = DDSDxgiFormat.UNKNOWN;
            switch ( texture.SurfaceFormat )
            {
                case GNF.SurfaceFormat.BC1:
                    imageFormat = DDSPixelFormatFourCC.DXT1;
                    break;
                case GNF.SurfaceFormat.BC2:
                    imageFormat = DDSPixelFormatFourCC.DXT2;
                    break;
                case GNF.SurfaceFormat.BC3:
                    imageFormat = DDSPixelFormatFourCC.DXT5;
                    break;
                case GNF.SurfaceFormat.BC4:
                    imageFormat = DDSPixelFormatFourCC.ATI1;
                    break;
                case GNF.SurfaceFormat.BC5:
                    imageFormat = DDSPixelFormatFourCC.ATI2N_3Dc;
                    break;
                case GNF.SurfaceFormat.BC6:
                    imageFormat = DDSPixelFormatFourCC.DX10;
                    dx10ImageFormat = DDSDxgiFormat.BC6H_UF16;
                    break;
                case GNF.SurfaceFormat.BC7:
                    imageFormat = DDSPixelFormatFourCC.DX10;

                    switch ( texture.ChannelType )
                    {
                        case ChannelType.Srgb:
                            dx10ImageFormat = DDSDxgiFormat.BC7_UNORM_SRGB;
                            break;
                        default:
                            dx10ImageFormat = DDSDxgiFormat.BC7_UNORM;
                            break;
                    }
                    break;
            }

            var ddsHeader = new DDSHeader( texture.Width, texture.Height, imageFormat )
            {
                MipMapCount = texture.LastMipLevel,
                Depth = texture.Depth,
                DxgiFormat = dx10ImageFormat,
                D3D10ResourceDimension = DDSD3D10ResourceDimension.TEXTURE2D,
                ArraySize = 1
            };
            if ( texture.LastMipLevel > 1 )
                ddsHeader.Flags |= DDSHeaderFlags.MipMapCount;
            if ( dx10ImageFormat != DDSDxgiFormat.UNKNOWN )
                ddsHeader.Size += sizeof( uint ) * 5;

            // unswizzle
            var data = Swizzler.UnSwizzle( texture.Data, texture.Width, texture.Height, imageFormat == DDSPixelFormatFourCC.DXT1 ? 8 : 16, SwizzleType.PS4 );

            using var ddsStream = ddsHeader.Save();
            ddsStream.Write( data, 0, data.Length );
            return ddsStream.ToArray();
        }

        public static Bitmap Decode( byte[] data, TextureFormat format )
        {
            switch ( format )
            {
                case TextureFormat.DDS:
                case TextureFormat.EPT:
                    return DecodeDDS( data );
                case TextureFormat.TMX:
                    return DecodeTMX( data );
                case TextureFormat.TGA:
                    return DecodeTGA( data );
                case TextureFormat.GXT:
                    return DecodeGXT( data );
                case TextureFormat.GNF:
                    return DecodeGNF(data);
                default:
                    throw new NotSupportedException();
            }
        }

        private static Bitmap DecodeDDS( byte[] data )
        {
            try
            {
                BcDecoder decoder = new BcDecoder();
                MemoryStream texturestream = new MemoryStream( data );
                Image<Rgba32> rgba32image = decoder.DecodeToImageRgba32( texturestream );

                // Create the Bitmap with the correct size and pixel format
                var bitmap = new Bitmap( rgba32image.Width, rgba32image.Height, PixelFormat.Format32bppArgb );

                // Lock the bitmap data for direct access
                BitmapData bmpData = bitmap.LockBits( new System.Drawing.Rectangle( 0, 0, bitmap.Width, bitmap.Height ), ImageLockMode.WriteOnly, bitmap.PixelFormat );

                unsafe
                {
                    // Iterate through each row of the image
                    for ( int y = 0; y < rgba32image.Height; y++ )
                    {
                        // Get the starting address of the current row
                        byte* ptr = (byte*)bmpData.Scan0 + y * bmpData.Stride;

                        // Iterate through each pixel in the row
                        for ( int x = 0; x < rgba32image.Width; x++ )
                        {
                            // Get the pixel value from the image
                            var pixel = rgba32image[x, y];

                            // Set the pixel values directly in the bitmap data
                            ptr[0] = pixel.B; // Blue
                            ptr[1] = pixel.G; // Green
                            ptr[2] = pixel.R; // Red
                            ptr[3] = pixel.A; // Alpha

                            // Move to the next pixel
                            ptr += 4;
                        }
                    }
                }

                // Unlock the bitmap data to release resources
                bitmap.UnlockBits( bmpData );

                return bitmap;
            }
            catch ( Exception )
            {
                // :02Shrug:
            }

            try
            {
                return DDSCodec.DecompressImage( data );
            }
            catch ( Exception )
            {
            }

            // RIP
            Trace.WriteLine( "Failed to decode DDS texture" );
            return new Bitmap( 32, 32, PixelFormat.Format32bppArgb );
        }

        private static Bitmap DecodeTMX( byte[] data )
        {
            var tmx = new Scarlet.IO.ImageFormats.TMX();
            tmx.Open( new MemoryStream( data ), Scarlet.IO.Endian.LittleEndian );
            return tmx.GetBitmap();
        }
        private static Bitmap DecodeTGA( byte[] data )
        {
            return TgaDecoderTest.TgaDecoder.FromBinary( data );
        }
        private static Bitmap DecodeGXT( byte[] data )
        {
            var gxt = new Scarlet.IO.ImageFormats.GXT();
            gxt.Open( new MemoryStream( data ), Scarlet.IO.Endian.LittleEndian );
            return gxt.GetBitmap();
        }
        private static Bitmap DecodeGNF(byte[] data)
        {
            var gnf = new Scarlet.IO.ImageFormats.GNF();
            gnf.Open(new MemoryStream(data), Scarlet.IO.Endian.LittleEndian);
            return gnf.GetBitmap();
        }
    }
}
