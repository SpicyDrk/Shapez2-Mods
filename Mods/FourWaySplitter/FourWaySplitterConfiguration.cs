namespace FourWaySplitter
{
    /// <summary>
    /// Concrete configuration for the FourWaySplitter simulation. Mirrors
    /// <c>DiagonalCutterConfiguration</c> but drops the ResearchSpeedId
    /// dependency — per CONSTRAINTS §5b + R6, FourWaySplitter ships without
    /// a research gate in v1.
    /// </summary>
    internal class FourWaySplitterConfiguration : IFourWaySplitterConfiguration
    {
        public BeltSpeed BeltSpeed => _Speed;
        public BeltDelay ProcessingDelay => _Delay;

        private readonly BuffableBeltSpeed _Speed;
        private readonly BuffableBeltDelay _Delay;

        public FourWaySplitterConfiguration(
            BuffableBeltSpeed.DiscreteSpeed beltSpeed,
            BuffableBeltDelay.DiscreteDuration processingDuration)
        {
            _Speed = new BuffableBeltSpeed
            {
                BaseSpeed = beltSpeed
            };

            _Delay = new BuffableBeltDelay
            {
                BaseDuration = processingDuration
            };

            _Speed.OnAfterDeserialize();
            _Delay.OnAfterDeserialize();
        }
    }
}

