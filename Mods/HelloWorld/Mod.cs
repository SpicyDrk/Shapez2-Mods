using Game.Core.Modding;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

namespace HelloWorld
{
    /// <summary>
    /// Minimal Shapez 2 mod. Implements IMod and does nothing observable in
    /// the game beyond appearing in the loaded-mod list. Serves as the first
    /// real proof that the Shapez2-Mods workspace's shared build config
    /// compiles cleanly against Shifter and the game assemblies.
    ///
    /// When you copy this mod as the starting point for a real mod:
    /// - Rename the folder and .csproj to match your mod's name.
    /// - Update manifest.json (Title/Description/Author/Assemblies[0]).
    /// - Add real behavior in the constructor using Shifter's fluent builders.
    /// </summary>
    [UsedImplicitly]
    public class HelloWorldMod : IMod
    {
        public HelloWorldMod(ILogger logger)
        {
            // Intentionally empty. The logger parameter satisfies Shifter's
            // DI convention; we don't call it here because the logging API
            // surface isn't part of this workspace's documented contract yet.
        }

        public void Dispose()
        {
        }
    }
}
