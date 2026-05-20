namespace SmartCutter
{
    internal sealed class SmartCutterMirroring : IBuildingMirroringDefinition
    {
        public IBuildingDefinition MirroredDefinition { get; }
        public bool IsMirrored { get; }

        public SmartCutterMirroring(IBuildingDefinition mirroredDefinition, bool isMirrored)
        {
            MirroredDefinition = mirroredDefinition;
            IsMirrored = isMirrored;
        }
    }
}
