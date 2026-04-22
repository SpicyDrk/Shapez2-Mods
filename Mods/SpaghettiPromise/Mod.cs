using Game.Core.Modding;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

namespace SpaghettiPromise
{
    /// <summary>
    /// Flips the game's opening "I will not build any spaghetti factories."
    /// contract line via a translations.json override. Uses no runtime API
    /// surface — Shifter merges this mod's translations over the base game's,
    /// so the new string takes effect without a single IL detour.
    /// </summary>
    [UsedImplicitly]
    public class SpaghettiPromiseMod : IMod
    {
        public SpaghettiPromiseMod(ILogger logger)
        {
        }

        public void Dispose()
        {
        }
    }
}
