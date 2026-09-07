using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>
/// Zero-copy view over the large, flat AniMatch cache arrays. The cache is opened with
/// FileShare.Delete so a later atomic cache replacement can coexist with an older live mapping.
/// </summary>
internal unsafe sealed class MappedAnimationIndex : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _basePointer;
    private bool _pointerAcquired;
    private bool _disposed;

    private readonly long _addressesOffset;
    private readonly long _descriptorsOffset;
    private readonly long _projectedOffset;
    private readonly long _treePointIndexOffset;
    private readonly long _treeThresholdOffset;
    private readonly long _treeNearOffset;
    private readonly long _treeFarOffset;

    public MappedAnimationIndex(
        string path,
        int sampleCount,
        int descriptorDimensions,
        int projectionDimensions,
        int treeRoot,
        long addressesOffset,
        long descriptorsOffset,
        long projectedOffset,
        long treePointIndexOffset,
        long treeThresholdOffset,
        long treeNearOffset,
        long treeFarOffset)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("AniMatch memory-mapped caches currently require a little-endian platform.");

        SampleCount = sampleCount;
        DescriptorDimensions = descriptorDimensions;
        ProjectionDimensions = projectionDimensions;
        TreeRoot = treeRoot;
        _addressesOffset = addressesOffset;
        _descriptorsOffset = descriptorsOffset;
        _projectedOffset = projectedOffset;
        _treePointIndexOffset = treePointIndexOffset;
        _treeThresholdOffset = treeThresholdOffset;
        _treeNearOffset = treeNearOffset;
        _treeFarOffset = treeFarOffset;

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            _mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            stream = null!;
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointerAcquired = true;
            _basePointer = pointer + _view.PointerOffset;
        }
        catch
        {
            stream?.Dispose();
            _view?.Dispose();
            _mapping?.Dispose();
            throw;
        }
    }

    ~MappedAnimationIndex() => Dispose(false);

    public int SampleCount { get; }
    public int DescriptorDimensions { get; }
    public int ProjectionDimensions { get; }
    public int TreeRoot { get; }

    public FrameAddress GetAddress(int sampleIndex)
    {
        ValidateSampleIndex(sampleIndex);
        var values = (int*)(_basePointer + _addressesOffset) + (long)sampleIndex * 2;
        return new FrameAddress(values[0], values[1]);
    }

    public void CopyDescriptor(int sampleIndex, Span<float> destination)
    {
        ValidateSampleIndex(sampleIndex);
        if (destination.Length < DescriptorDimensions)
            throw new ArgumentException("Destination is smaller than the descriptor.", nameof(destination));

        var source = (Half*)(_basePointer + _descriptorsOffset) + (long)sampleIndex * DescriptorDimensions;
        for (var i = 0; i < DescriptorDimensions; i++)
            destination[i] = (float)source[i];
    }

    public float GetDescriptorValue(int sampleIndex, int dimension)
    {
        ValidateSampleIndex(sampleIndex);
        if ((uint)dimension >= (uint)DescriptorDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        var source = (Half*)(_basePointer + _descriptorsOffset);
        return (float)source[(long)sampleIndex * DescriptorDimensions + dimension];
    }

    public float GetProjectedValue(int sampleIndex, int dimension)
    {
        ValidateSampleIndex(sampleIndex);
        if ((uint)dimension >= (uint)ProjectionDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimension));
        var source = (float*)(_basePointer + _projectedOffset);
        return source[(long)sampleIndex * ProjectionDimensions + dimension];
    }

    public int GetTreePointIndex(int node) => ReadTreeInt(_treePointIndexOffset, node);
    public float GetTreeThreshold(int node)
    {
        ValidateNode(node);
        return ((float*)(_basePointer + _treeThresholdOffset))[node];
    }
    public int GetTreeNear(int node) => ReadTreeInt(_treeNearOffset, node);
    public int GetTreeFar(int node) => ReadTreeInt(_treeFarOffset, node);

    private int ReadTreeInt(long offset, int node)
    {
        ValidateNode(node);
        return ((int*)(_basePointer + offset))[node];
    }

    private void ValidateSampleIndex(int sampleIndex)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MappedAnimationIndex));
        if ((uint)sampleIndex >= (uint)SampleCount)
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));
    }

    private void ValidateNode(int node)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MappedAnimationIndex));
        if ((uint)node >= (uint)SampleCount)
            throw new InvalidDataException("AniMatch VP-tree node index is outside the mapped cache.");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_pointerAcquired)
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); }
            catch { }
            _pointerAcquired = false;
            _basePointer = null;
        }

        if (disposing)
        {
            _view.Dispose();
            _mapping.Dispose();
        }
        else
        {
            try { _view.Dispose(); } catch { }
            try { _mapping.Dispose(); } catch { }
        }
    }
}
