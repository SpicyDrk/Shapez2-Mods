namespace SmartCutter
{
    /// <summary>
    /// Prediction adapter for the SmartCutter. Phase P01 stub — returns the
    /// input shape unchanged (the belt-placement preview will show shapes
    /// flowing through untouched). The real simulation still applies the
    /// keep-mask at runtime.
    ///
    /// WHY THIS EXISTS — Shifter v1.0.0 has an NRE at AtomicBuildingExtender:158
    /// when a building chain uses <c>.WithoutPrediction()</c> (see
    /// FourWaySplitter's Operation1In4OutPredictionFactoryBuilder docstring).
    /// We supply this non-null adapter to sidestep that bug. Real-mask
    /// prediction would require wire-signal visibility in the prediction
    /// pipeline, which isn't a thing — the prediction sim is item-only by
    /// design — so a true masked preview isn't reachable. Identity is an
    /// honest compromise: the player sees the input shape on the preview, and
    /// the masked output appears at runtime once a real wire signal is wired
    /// in.
    /// </summary>
    public class ShapeOperationSmartCutPredictionAdapter : IItemOperation1In1Out
    {
        public bool TryExecute(IItem input, out IItem output)
        {
            if (input is not ShapeItem)
            {
                output = null!;
                return false;
            }

            output = input;
            return true;
        }
    }
}
