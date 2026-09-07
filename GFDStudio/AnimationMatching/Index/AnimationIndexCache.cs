using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Features;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>
/// Persistent AniMatch cache. Version 3 is a flat, mmap-friendly layout: large descriptor and
/// projected-vector sections are never deserialized on reload. Descriptors are stored as FP16;
/// projected vectors stay FP32 so the persisted VP-tree thresholds describe exactly the same metric.
/// Every indexed frame is still present -- storage precision changed, not temporal sampling.
/// </summary>
public static class AnimationIndexCache
{
    private const int Magic = 0x414D4947; // "GIMA"
    private const int CurrentVersion = 3;
    private const int LegacyVersion1 = 1;
    private const int LegacyVersion2 = 2;
    private const int FlatHeaderSize = 128;
    private const int SectionAlignment = 64;
    private const int IoChunkElements = 256 * 1024;

    private readonly record struct FlatHeader(
        int DescriptorDimensions,
        int SampleCount,
        int ClipCount,
        int ProjectionDimensions,
        int TreeRoot,
        long MetadataOffset,
        long MetadataLength,
        long AddressesOffset,
        long MeanOffset,
        long InvStdOffset,
        long DescriptorsOffset,
        long ProjectedOffset,
        long TreePointIndexOffset,
        long TreeThresholdOffset,
        long TreeNearOffset,
        long TreeFarOffset,
        long FileLength);

    public static void Save(string path, AnimationSearchDatabase database, string corpusSignature)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("AniMatch memory-mapped caches currently require a little-endian platform.");
        if (database.InMemoryDescriptors is null || database.InMemoryProjected is null)
            throw new InvalidOperationException("A memory-mapped AniMatch database does not need to be serialized again.");

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
                // Reserve a fixed header. All heavyweight sections are raw aligned arrays so they can
                // later be addressed directly through a MemoryMappedViewAccessor.
                writer.Write(new byte[FlatHeaderSize]);

                var metadataOffset = stream.Position;
                writer.Write(corpusSignature ?? string.Empty);
                writer.Write(database.Options.GetIndexFingerprint());
                foreach (var clip in database.Corpus.Clips)
                    writer.Write(clip.Id);
                var metadataLength = stream.Position - metadataOffset;

                Align(writer);
                var addressesOffset = stream.Position;
                WriteAddresses(writer, database);

                Align(writer);
                var meanOffset = stream.Position;
                WriteFloatValues(writer.BaseStream, database.Mean);

                Align(writer);
                var invStdOffset = stream.Position;
                WriteFloatValues(writer.BaseStream, database.InvStd);

                Align(writer);
                var descriptorsOffset = stream.Position;
                WriteHalfValues(writer.BaseStream, database.InMemoryDescriptors);

                Align(writer);
                var projectedOffset = stream.Position;
                WriteFloatValues(writer.BaseStream, database.InMemoryProjected);

                Align(writer);
                var treePointIndexOffset = stream.Position;
                WriteIntValues(writer.BaseStream, database.Tree.Count, database.Tree.GetNodePointIndex);

                Align(writer);
                var treeThresholdOffset = stream.Position;
                WriteFloatValues(writer.BaseStream, database.Tree.Count, database.Tree.GetNodeThreshold);

                Align(writer);
                var treeNearOffset = stream.Position;
                WriteIntValues(writer.BaseStream, database.Tree.Count, database.Tree.GetNodeNear);

                Align(writer);
                var treeFarOffset = stream.Position;
                WriteIntValues(writer.BaseStream, database.Tree.Count, database.Tree.GetNodeFar);

                var fileLength = stream.Position;
                var header = new FlatHeader(
                    database.DescriptorDimensions,
                    database.SampleCount,
                    database.Corpus.Clips.Count,
                    database.Projection.OutputDimensions,
                    database.Tree.Root,
                    metadataOffset,
                    metadataLength,
                    addressesOffset,
                    meanOffset,
                    invStdOffset,
                    descriptorsOffset,
                    projectedOffset,
                    treePointIndexOffset,
                    treeThresholdOffset,
                    treeNearOffset,
                    treeFarOffset,
                    fileLength);

                stream.Position = 0;
                WriteFlatHeader(writer, header);
                if (stream.Position != FlatHeaderSize)
                    throw new InvalidDataException("AniMatch flat-cache header size changed unexpectedly.");

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

    public static AnimationSearchDatabase? TryLoad(
        string path,
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        string corpusSignature,
        IProgress<string>? progress = null)
    {
        if (!File.Exists(path) || corpus.Clips.Count == 0) return null;

        AnimationSearchDatabase? legacyDatabase = null;
        try
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (reader.ReadInt32() != Magic) return null;
                var version = reader.ReadInt32();

                if (version == CurrentVersion)
                {
                    var header = ReadFlatHeader(reader);
                    if (!ValidateFlatLayout(header, stream.Length)) return null;
                    if (!ValidateMetadata(reader, header, corpus, options, corpusSignature)) return null;

                    var prepared = PrepareFeatureLayout(corpus, options, header.DescriptorDimensions);
                    if (prepared.projection.OutputDimensions != header.ProjectionDimensions) return null;

                    // Only tiny normalization vectors are copied. The GB-scale sections remain on SSD
                    // and are paged by the OS when searches actually touch them.
                    var mean = ReadFloatSection(stream, header.MeanOffset, header.DescriptorDimensions);
                    var invStd = ReadFloatSection(stream, header.InvStdOffset, header.DescriptorDimensions);

                    progress?.Report("Memory-mapping cached animation index…");
                    var mapped = new MappedAnimationIndex(
                        path,
                        header.SampleCount,
                        header.DescriptorDimensions,
                        header.ProjectionDimensions,
                        header.TreeRoot,
                        header.AddressesOffset,
                        header.DescriptorsOffset,
                        header.ProjectedOffset,
                        header.TreePointIndexOffset,
                        header.TreeThresholdOffset,
                        header.TreeNearOffset,
                        header.TreeFarOffset);
                    try
                    {
                        var tree = new VpTree(mapped, header.ProjectionDimensions, header.TreeRoot);
                        return AnimationSearchDatabase.FromMappedCache(
                            corpus,
                            options,
                            prepared.extractor,
                            prepared.clipFeatureBones,
                            mapped,
                            mean,
                            invStd,
                            prepared.dimensionWeights,
                            prepared.projection,
                            tree);
                    }
                    catch
                    {
                        mapped.Dispose();
                        throw;
                    }
                }

                if (version != LegacyVersion1 && version != LegacyVersion2)
                    return null;

                legacyDatabase = ReadLegacyCache(reader, version, corpus, options, corpusSignature, progress);
            }

            if (legacyDatabase is null)
                return null;

            // Upgrade without re-extracting poses. v2 already contains its projection/tree; v1 pays
            // that upgrade once. Closing the old file before File.Move is important on Windows.
            progress?.Report("Converting AniMatch cache to memory-mapped FP16 format…");
            try
            {
                Save(path, legacyDatabase, corpusSignature);
                var mappedDatabase = TryLoad(path, corpus, options, corpusSignature, progress);
                if (mappedDatabase is not null)
                {
                    legacyDatabase.Dispose();
                    return mappedDatabase;
                }
            }
            catch
            {
                // A failed conversion must not discard the usable legacy in-memory database.
            }

            return legacyDatabase;
        }
        catch (EndOfStreamException) { legacyDatabase?.Dispose(); return null; }
        catch (InvalidDataException) { legacyDatabase?.Dispose(); return null; }
        catch (IOException) { legacyDatabase?.Dispose(); return null; }
        catch (OverflowException) { legacyDatabase?.Dispose(); return null; }
        catch (ArgumentException) { legacyDatabase?.Dispose(); return null; }
    }

    private static AnimationSearchDatabase? ReadLegacyCache(
        BinaryReader reader,
        int version,
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        string corpusSignature,
        IProgress<string>? progress)
    {
        if (!string.Equals(reader.ReadString(), corpusSignature ?? string.Empty, StringComparison.Ordinal)) return null;
        if (!string.Equals(reader.ReadString(), options.GetIndexFingerprint(), StringComparison.Ordinal)) return null;

        var dimensions = reader.ReadInt32();
        var sampleCount = reader.ReadInt32();
        var clipCount = reader.ReadInt32();
        if (clipCount != corpus.Clips.Count || sampleCount < 0 || dimensions < 1) return null;
        for (var i = 0; i < clipCount; i++)
            if (!string.Equals(reader.ReadString(), corpus.Clips[i].Id, StringComparison.Ordinal)) return null;

        progress?.Report("Loading legacy cached pose descriptors…");
        var addresses = new FrameAddress[sampleCount];
        for (var i = 0; i < sampleCount; i++)
            addresses[i] = new FrameAddress(reader.ReadInt32(), reader.ReadInt32());
        var mean = ReadLegacyFloatArray(reader, dimensions);
        var invStd = ReadLegacyFloatArray(reader, dimensions);
        var descriptors = ReadLegacyFloatArray(reader, checked(sampleCount * dimensions));

        var prepared = PrepareFeatureLayout(corpus, options, dimensions);
        float[] projected;
        VpTree tree;

        if (version >= LegacyVersion2)
        {
            progress?.Report("Loading legacy cached search index…");
            projected = ReadLegacyFloatArray(reader, checked(sampleCount * prepared.projection.OutputDimensions));
            tree = VpTree.ReadFrom(reader, projected, prepared.projection.OutputDimensions);
        }
        else
        {
            progress?.Report("Upgrading old AniMatch cache: projecting poses…");
            projected = new float[checked(sampleCount * prepared.projection.OutputDimensions)];
            Parallel.For(0, sampleCount, i =>
            {
                prepared.projection.Project(
                    descriptors.AsSpan(i * dimensions, dimensions),
                    projected.AsSpan(i * prepared.projection.OutputDimensions, prepared.projection.OutputDimensions));
            });

            progress?.Report("Upgrading old AniMatch cache: building search tree…");
            tree = new VpTree(projected, prepared.projection.OutputDimensions);
        }

        return AnimationSearchDatabase.FromCache(
            corpus,
            options,
            prepared.extractor,
            prepared.clipFeatureBones,
            addresses,
            descriptors,
            mean,
            invStd,
            prepared.dimensionWeights,
            prepared.projection,
            projected,
            tree);
    }

    private static (PoseFeatureExtractor extractor, int[][] clipFeatureBones, float[] dimensionWeights, RandomProjection projection)
        PrepareFeatureLayout(AnimationCorpus corpus, AnimationMatchOptions options, int dimensions)
    {
        options.Validate();
        var extractor = new PoseFeatureExtractor(options);
        var clipFeatureBones = new int[corpus.Clips.Count][];
        var commonBoneCount = int.MaxValue;
        for (var i = 0; i < corpus.Clips.Count; i++)
        {
            clipFeatureBones[i] = extractor.SelectFeatureBones(corpus.Clips[i].Skeleton);
            commonBoneCount = Math.Min(commonBoneCount, clipFeatureBones[i].Length);
        }
        if (commonBoneCount < 1)
            throw new InvalidDataException("No feature bones were found while opening the AniMatch cache.");
        for (var i = 0; i < clipFeatureBones.Length; i++)
            if (clipFeatureBones[i].Length != commonBoneCount)
                clipFeatureBones[i] = clipFeatureBones[i][..commonBoneCount];
        if (dimensions != extractor.GetDescriptorLength(commonBoneCount))
            throw new InvalidDataException("AniMatch descriptor dimensions do not match the current skeleton/features.");

        var dimensionWeights = extractor.GetPostNormalizationWeights(commonBoneCount);
        var projection = new RandomProjection(dimensions, options.ProjectionDimensions, options.ProjectionSeed);
        return (extractor, clipFeatureBones, dimensionWeights, projection);
    }

    private static void WriteFlatHeader(BinaryWriter writer, FlatHeader header)
    {
        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write(FlatHeaderSize);
        writer.Write(header.DescriptorDimensions);
        writer.Write(header.SampleCount);
        writer.Write(header.ClipCount);
        writer.Write(header.ProjectionDimensions);
        writer.Write(header.TreeRoot);
        writer.Write(header.MetadataOffset);
        writer.Write(header.MetadataLength);
        writer.Write(header.AddressesOffset);
        writer.Write(header.MeanOffset);
        writer.Write(header.InvStdOffset);
        writer.Write(header.DescriptorsOffset);
        writer.Write(header.ProjectedOffset);
        writer.Write(header.TreePointIndexOffset);
        writer.Write(header.TreeThresholdOffset);
        writer.Write(header.TreeNearOffset);
        writer.Write(header.TreeFarOffset);
        writer.Write(header.FileLength);
    }

    private static FlatHeader ReadFlatHeader(BinaryReader reader)
    {
        if (reader.ReadInt32() != FlatHeaderSize)
            throw new InvalidDataException("Unsupported AniMatch flat-cache header.");
        return new FlatHeader(
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt64());
    }

    private static bool ValidateFlatLayout(FlatHeader header, long actualFileLength)
    {
        if (header.FileLength != actualFileLength || header.FileLength < FlatHeaderSize)
            return false;
        if (header.DescriptorDimensions < 1 || header.ProjectionDimensions < 1 || header.SampleCount < 0 || header.ClipCount < 0)
            return false;
        if (header.SampleCount == 0 ? header.TreeRoot != -1 : header.TreeRoot < 0 || header.TreeRoot >= header.SampleCount)
            return false;

        var addressesBytes = checked((long)header.SampleCount * sizeof(int) * 2);
        var statsBytes = checked((long)header.DescriptorDimensions * sizeof(float));
        var descriptorBytes = checked((long)header.SampleCount * header.DescriptorDimensions * sizeof(ushort));
        var projectedBytes = checked((long)header.SampleCount * header.ProjectionDimensions * sizeof(float));
        var treeBytes = checked((long)header.SampleCount * sizeof(int));

        if (!SectionFits(header.MetadataOffset, header.MetadataLength, header.FileLength)) return false;
        if (!SectionFits(header.AddressesOffset, addressesBytes, header.FileLength)) return false;
        if (!SectionFits(header.MeanOffset, statsBytes, header.FileLength)) return false;
        if (!SectionFits(header.InvStdOffset, statsBytes, header.FileLength)) return false;
        if (!SectionFits(header.DescriptorsOffset, descriptorBytes, header.FileLength)) return false;
        if (!SectionFits(header.ProjectedOffset, projectedBytes, header.FileLength)) return false;
        if (!SectionFits(header.TreePointIndexOffset, treeBytes, header.FileLength)) return false;
        if (!SectionFits(header.TreeThresholdOffset, treeBytes, header.FileLength)) return false;
        if (!SectionFits(header.TreeNearOffset, treeBytes, header.FileLength)) return false;
        if (!SectionFits(header.TreeFarOffset, treeBytes, header.FileLength)) return false;

        return header.MetadataOffset >= FlatHeaderSize &&
               header.MetadataOffset + header.MetadataLength <= header.AddressesOffset &&
               header.AddressesOffset + addressesBytes <= header.MeanOffset &&
               header.MeanOffset + statsBytes <= header.InvStdOffset &&
               header.InvStdOffset + statsBytes <= header.DescriptorsOffset &&
               header.DescriptorsOffset + descriptorBytes <= header.ProjectedOffset &&
               header.ProjectedOffset + projectedBytes <= header.TreePointIndexOffset &&
               header.TreePointIndexOffset + treeBytes <= header.TreeThresholdOffset &&
               header.TreeThresholdOffset + treeBytes <= header.TreeNearOffset &&
               header.TreeNearOffset + treeBytes <= header.TreeFarOffset &&
               header.TreeFarOffset + treeBytes <= header.FileLength;
    }

    private static bool ValidateMetadata(
        BinaryReader reader,
        FlatHeader header,
        AnimationCorpus corpus,
        AnimationMatchOptions options,
        string corpusSignature)
    {
        if (header.ClipCount != corpus.Clips.Count) return false;
        reader.BaseStream.Position = header.MetadataOffset;
        var metadataEnd = checked(header.MetadataOffset + header.MetadataLength);

        if (!string.Equals(reader.ReadString(), corpusSignature ?? string.Empty, StringComparison.Ordinal)) return false;
        if (!string.Equals(reader.ReadString(), options.GetIndexFingerprint(), StringComparison.Ordinal)) return false;
        for (var i = 0; i < header.ClipCount; i++)
            if (!string.Equals(reader.ReadString(), corpus.Clips[i].Id, StringComparison.Ordinal)) return false;

        return reader.BaseStream.Position <= metadataEnd;
    }

    private static bool SectionFits(long offset, long length, long fileLength)
        => offset >= 0 && length >= 0 && offset <= fileLength && length <= fileLength - offset;

    private static void Align(BinaryWriter writer)
    {
        var remainder = writer.BaseStream.Position % SectionAlignment;
        if (remainder == 0) return;
        writer.Write(new byte[(int)(SectionAlignment - remainder)]);
    }

    private static void WriteAddresses(BinaryWriter writer, AnimationSearchDatabase database)
    {
        const int SamplesPerChunk = 64 * 1024;
        var buffer = new int[SamplesPerChunk * 2];
        for (var offset = 0; offset < database.SampleCount; offset += SamplesPerChunk)
        {
            var count = Math.Min(SamplesPerChunk, database.SampleCount - offset);
            for (var i = 0; i < count; i++)
            {
                var address = database.GetAddress(offset + i);
                buffer[i * 2] = address.ClipIndex;
                buffer[i * 2 + 1] = address.FrameIndex;
            }
            writer.BaseStream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, count * 2)));
        }
    }

    private static void WriteHalfValues(Stream stream, float[] values)
    {
        var buffer = new Half[Math.Min(IoChunkElements, Math.Max(1, values.Length))];
        for (var offset = 0; offset < values.Length; offset += buffer.Length)
        {
            var count = Math.Min(buffer.Length, values.Length - offset);
            for (var i = 0; i < count; i++) buffer[i] = (Half)values[offset + i];
            stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, count)));
        }
    }

    private static void WriteFloatValues(Stream stream, float[] values)
    {
        for (var offset = 0; offset < values.Length; offset += IoChunkElements)
        {
            var count = Math.Min(IoChunkElements, values.Length - offset);
            stream.Write(MemoryMarshal.AsBytes(values.AsSpan(offset, count)));
        }
    }

    private static void WriteFloatValues(Stream stream, int count, Func<int, float> getter)
    {
        var buffer = new float[Math.Min(IoChunkElements, Math.Max(1, count))];
        for (var offset = 0; offset < count; offset += buffer.Length)
        {
            var length = Math.Min(buffer.Length, count - offset);
            for (var i = 0; i < length; i++) buffer[i] = getter(offset + i);
            stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, length)));
        }
    }

    private static void WriteIntValues(Stream stream, int count, Func<int, int> getter)
    {
        var buffer = new int[Math.Min(IoChunkElements, Math.Max(1, count))];
        for (var offset = 0; offset < count; offset += buffer.Length)
        {
            var length = Math.Min(buffer.Length, count - offset);
            for (var i = 0; i < length; i++) buffer[i] = getter(offset + i);
            stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, length)));
        }
    }

    private static float[] ReadFloatSection(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var values = new float[count];
        ReadRawInChunks(stream, values);
        return values;
    }

    private static float[] ReadLegacyFloatArray(BinaryReader reader, int expectedLength)
    {
        var length = reader.ReadInt32();
        if (length != expectedLength)
            throw new InvalidDataException("Animation matching cache dimensions do not match.");
        var values = new float[length];
        ReadRawInChunks(reader.BaseStream, values);
        return values;
    }

    private static void ReadRawInChunks<T>(Stream stream, T[] values) where T : unmanaged
    {
        for (var offset = 0; offset < values.Length; offset += IoChunkElements)
        {
            var count = Math.Min(IoChunkElements, values.Length - offset);
            stream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan(offset, count)));
        }
    }
}
