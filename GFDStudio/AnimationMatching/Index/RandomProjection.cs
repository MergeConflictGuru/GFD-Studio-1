using System;

namespace GFDStudio.AnimationMatching.Index;

/// <summary>
/// Deterministic sparse random projection used as a cheap first-stage embedding.
/// It avoids a heavyweight numeric dependency while preserving distances well enough
/// for candidate generation; full descriptors are always used for final reranking.
/// </summary>
public sealed class RandomProjection
{
    private readonly float[] _matrix;

    public RandomProjection(int inputDimensions, int outputDimensions, int seed)
    {
        InputDimensions = inputDimensions;
        OutputDimensions = Math.Min(outputDimensions, inputDimensions);
        _matrix = new float[InputDimensions * OutputDimensions];
        var random = new Random(seed);
        var scale = MathF.Sqrt(3f / OutputDimensions);

        for (var o = 0; o < OutputDimensions; o++)
        {
            for (var i = 0; i < InputDimensions; i++)
            {
                var r = random.NextDouble();
                // Achlioptas-style sparse projection: {-sqrt(3/k), 0, +sqrt(3/k)}.
                _matrix[o * InputDimensions + i] = r < 1.0 / 6.0 ? scale : r > 5.0 / 6.0 ? -scale : 0f;
            }
        }
    }

    public int InputDimensions { get; }
    public int OutputDimensions { get; }

    public void Project(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != InputDimensions || output.Length < OutputDimensions)
            throw new ArgumentException("Projection vector dimensions do not match.");

        for (var o = 0; o < OutputDimensions; o++)
        {
            var sum = 0f;
            var row = o * InputDimensions;
            for (var i = 0; i < InputDimensions; i++)
                sum += input[i] * _matrix[row + i];
            output[o] = sum;
        }
    }
}
