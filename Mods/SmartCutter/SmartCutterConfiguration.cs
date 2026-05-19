namespace SmartCutter
{
    /// <summary>
    /// Concrete configuration for the SmartCutter simulation. Mirrors
    /// FourWaySplitterConfiguration's shape — buffable belt speed + processing
    /// delay, no research-speed dependency (no research gate in v1).
    /// </summary>
    internal class SmartCutterConfiguration : ISmartCutterConfiguration
    {
        public BeltSpeed BeltSpeed => _Speed;
        public BeltDelay ProcessingDelay => _Delay;

        private readonly BuffableBeltSpeed _Speed;
        private readonly BuffableBeltDelay _Delay;

        public SmartCutterConfiguration(
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
