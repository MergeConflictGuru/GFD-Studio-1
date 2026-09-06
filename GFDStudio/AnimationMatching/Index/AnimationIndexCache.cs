using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>
/// Compact persistent cache for the expensive full descriptor extraction stage.
/// The host supplies a corpusSignature made from animation paths/sizes/mtimes (or hashes),
/// selected model/skeleton signature and retargeting settings. Projection/tree rebuilds from
/// cached descriptors are cheap and deterministic.
/// </summary>
public static class AnimationIndexCache
{
    private const int Magic = 0x414D4947; // "GIMA"
    private const int Version = 1;

    public static void Save(string path, AnimationSearchDatabase database, string corpusSignature)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(corpusSignature ?? string.Empty);
        writer.Write(database.Options.GetIndexFingerprint());
        writer.Write(database.DescriptorDimensions);
        writer.Write(database.SampleCount);
        writer.Write(database.Corpus.Clips.Count);
        foreach (var clip in database.Corpus.Clips) writer.Write(clip.Id);
        foreach (var address in database.Addresses) { writer.Write(address.ClipIndex); writer.Write(address.FrameIndex); }
        WriteFloatArray(writer, database.Mean);
        WriteFloatArray(writer, database.InvStd);
        WriteFloatArray(writer, database.Descriptors);
    }

    /// <summary>
    /// Returns null when the cache does not exactly match the current corpus/options.
    /// Descriptors in cache are already z-score normalized.
    /// </summary>
    public static AnimationSearchDatabase? TryLoad(
        string path,
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        string corpusSignature)
    {
        if (!File.Exists(path) || corpus.Clips.Count == 0) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return null;
            if (!string.Equals(reader.ReadString(), corpusSignature ?? string.Empty, StringComparison.Ordinal)) return null;
            if (!string.Equals(reader.ReadString(), options.GetIndexFingerprint(), StringComparison.Ordinal)) return null;
            var dimensions = reader.ReadInt32();
            var sampleCount = reader.ReadInt32();
            var clipCount = reader.ReadInt32();
            if (clipCount != corpus.Clips.Count || sampleCount < 0 || dimensions < 1) return null;
            for (var i = 0; i < clipCount; i++)
                if (!string.Equals(reader.ReadString(), corpus.Clips[i].Id, StringComparison.Ordinal)) return null;

            var addresses = new FrameAddress[sampleCount];
            for (var i = 0; i < sampleCount; i++) addresses[i] = new FrameAddress(reader.ReadInt32(), reader.ReadInt32());
            var mean = ReadFloatArray(reader, dimensions);
            var invStd = ReadFloatArray(reader, dimensions);
            var descriptors = ReadFloatArray(reader, checked(sampleCount * dimensions));

            options.Validate();
            var extractor = new PoseFeatureExtractor(options);
            var clipFeatureBones = new int[clipCount][];
            var expectedDimensions = -1;
            var commonBoneCount = int.MaxValue;
            for (var i = 0; i < clipCount; i++)
            {
                clipFeatureBones[i] = extractor.SelectFeatureBones(corpus.Clips[i].Skeleton);
                commonBoneCount = Math.Min(commonBoneCount, clipFeatureBones[i].Length);
            }
            if (commonBoneCount < 1) return null;
            for (var i = 0; i < clipCount; i++)
                if (clipFeatureBones[i].Length != commonBoneCount)
                    clipFeatureBones[i] = clipFeatureBones[i][..commonBoneCount];
            expectedDimensions = extractor.GetDescriptorLength(commonBoneCount);
            if (dimensions != expectedDimensions) return null;
            var dimensionWeights = extractor.GetPostNormalizationWeights(commonBoneCount);

            var projection = new RandomProjection(dimensions, options.ProjectionDimensions, options.ProjectionSeed);
            var projected = new float[sampleCount * projection.OutputDimensions];
            for (var i = 0; i < sampleCount; i++)
                projection.Project(descriptors.AsSpan(i * dimensions, dimensions), projected.AsSpan(i * projection.OutputDimensions, projection.OutputDimensions));
            var tree = new VpTree(projected, projection.OutputDimensions);

            return AnimationSearchDatabase.FromCache(corpus, options, extractor, clipFeatureBones, addresses, descriptors, mean, invStd, dimensionWeights, projection, projected, tree);
        }
        catch (EndOfStreamException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static void WriteFloatArray(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values) writer.Write(value);
    }

    private static float[] ReadFloatArray(BinaryReader reader, int expectedLength)
    {
        var length = reader.ReadInt32();
        if (length != expectedLength) throw new InvalidDataException("Animation matching cache dimensions do not match.");
        var values = new float[length];
        for (var i = 0; i < length; i++) values[i] = reader.ReadSingle();
        return values;
    }
}
