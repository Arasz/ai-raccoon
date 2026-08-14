namespace AiRaccoon.Core.Memory.QueryGuard.Structural;

/// <summary>
///     Scores a <see cref="StructuralNoiseFeatures" /> vector with the trained structural-noise
///     ensemble (docs/adr/0041): 200 depth-4 trees, compiled as data in
///     <c>StructuralNoiseModel.Trees.g.cs</c> — no model file, no embedding, no I/O (F12: "30
///     numbers and a table of split points, not a model file"). Traversal matches
///     <c>HistGradientBoostingClassifier</c>'s own semantics exactly: at each internal node, go
///     left when the feature value is less than or equal to the split threshold, otherwise right;
///     sum every tree's leaf value plus the baseline, then sigmoid.
/// </summary>
public static partial class StructuralNoiseModel
{
    public static double Score(double[] features)
    {
        var sum = Baseline;
        for (var t = 0; t < TreeCount; t++)
        {
            var start = TreeStart[t];
            var node = 0;
            while (IsLeaf[start + node] == 0)
            {
                var offset = start + node;
                var goLeft = features[FeatureIdx[offset]] <= Threshold[offset];
                node = goLeft ? Left[offset] : Right[offset];
            }

            sum += Value[start + node];
        }

        return 1.0 / (1.0 + Math.Exp(-sum));
    }
}
