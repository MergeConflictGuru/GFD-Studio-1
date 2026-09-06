using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>
/// Persistent cache for the expensive descriptor extraction and ANN search-index stages.
/// Version 2 also stores projected vectors and the VP-tree, so a valid cache does not spend
/// minutes rebuilding a search structure every time GFD Studio starts.
/// </summary>
public static class AnimationIndexCache
{
    private const int Magic = 0x414D4947; // "GIMA"
    private const int CurrentVersion = 2;
    private const int LegacyVersion = 1;

    public static void Save(string path, AnimationSearchDatabase database, string corpusSignature)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(CurrentVersion);
                writer.Write(corpusSignature ?? string.Empty);
                writer.Write(database.Options.GetIndexFingerprint());
                writer.Write(database.DescriptorDimensions);
                writer.Write(database.SampleCount);
                writer.Write(database.Corpus.Clips.Count);
                foreach (var clip in database.Corpus.Clips) writer.Write(clip.Id);
                foreach (var address in database.Addresses)
                {
                    writer.Write(address.ClipIndex);
                    writer.Write(address.FrameIndex);
                }

                WriteFloatArray(writer, database.Mean);
                WriteFloatArray(writer, database.InvStd);
                WriteFloatArray(writer, database.Descriptors);
                WriteFloatArray(writer, database.Projected);
                database.Tree.WriteTo(writer);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Cache cleanup must not affect matching.
            }
        }
    }

    /// <summary>
    /// Returns null when the cache does not exactly match the current corpus/options.
    /// Version-1 caches are accepted and upgraded in place after their search tree is rebuilt once.
    /// </summary>
    public static AnimationSearchDatabase? TryLoad(
        string path,
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        string corpusSignature,
        IProgress<string>? progress = null)
    {
        if (!File.Exists(path) || corpus.Clips.Count == 0) return null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadInt32() != Magic) return null;
            var version = reader.ReadInt32();
            if (version != LegacyVersion && version != CurrentVersion) return null;
            if (!string.Equals(reader.ReadString(), corpusSignature ?? string.Empty, StringComparison.Ordinal)) return null;
            if (!string.Equals(reader.ReadString(), options.GetIndexFingerprint(), StringComparison.Ordinal)) return null;

            var dimensions = reader.ReadInt32();
            var sampleCount = reader.ReadInt32();
            var clipCount = reader.ReadInt32();
            if (clipCount != corpus.Clips.Count || sampleCount < 0 || dimensions < 1) return null;
            for (var i = 0; i < clipCount; i++)
                if (!string.Equals(reader.ReadString(), corpus.Clips[i].Id, StringComparison.Ordinal)) return null;

            progress?.Report("Loading cached pose descriptors…");
            var addresses = new FrameAddress[sampleCount];
            for (var i = 0; i < sampleCount; i++)
                addresses[i] = new FrameAddress(reader.ReadInt32(), reader.ReadInt32());
            var mean = ReadFloatArray(reader, dimensions);
            var invStd = ReadFloatArray(reader, dimensions);
            var descriptors = ReadFloatArray(reader, checked(sampleCount * dimensions));

            options.Validate();
            var extractor = new PoseFeatureExtractor(options);
            var clipFeatureBones = new int[clipCount][];
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
            if (dimensions != extractor.GetDescriptorLength(commonBoneCount)) return null;
            var dimensionWeights = extractor.GetPostNormalizationWeights(commonBoneCount);

            var projection = new RandomProjection(dimensions, options.ProjectionDimensions, options.ProjectionSeed);
            float[] projected;
            VpTree tree;

            if (version >= CurrentVersion)
            {
                progress?.Report("Loading cached search index…");
                projected = ReadFloatArray(reader, checked(sampleCount * projection.OutputDimensions));
                tree = VpTree.ReadFrom(reader, projected, projection.OutputDimensions);
            }
            else
            {
                // Preserve existing v1 caches: do not throw away a 15-minute descriptor build.
                // Rebuild only the cheap representation once, then rewrite as v2 for subsequent runs.
                progress?.Report("Upgrading old AniMatch cache: projecting poses…");
                projected = new float[sampleCount * projection.OutputDimensions];
                Parallel.For(0, sampleCount, i =>
                {
                    projection.Project(
                        descriptors.AsSpan(i * dimensions, dimensions),
                        projected.AsSpan(i * projection.OutputDimensions, projection.OutputDimensions));
                });

                progress?.Report("Upgrading old AniMatch cache: building search tree…");
                tree = new VpTree(projected, projection.OutputDimensions);
            }

            var database = AnimationSearchDatabase.FromCache(
                corpus,
                options,
                extractor,
                clipFeatureBones,
                addresses,
                descriptors,
                mean,
                invStd,
                dimensionWeights,
                projection,
                projected,
                tree);

            if (version == LegacyVersion)
            {
                progress?.Report("Saving upgraded AniMatch cache…");
                try { Save(path, database, corpusSignature); }
                catch { /* A failed upgrade must not discard the usable in-memory index. */ }
            }

            return database;
        }
        catch (EndOfStreamException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static void WriteFloatArray(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        writer.Flush();
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static float[] ReadFloatArray(BinaryReader reader, int expectedLength)
    {
        var length = reader.ReadInt32();
        if (length != expectedLength)
            throw new InvalidDataException("Animation matching cache dimensions do not match.");
        var values = new float[length];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
