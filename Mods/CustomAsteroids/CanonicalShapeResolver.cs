using System;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

namespace CustomAsteroids
{
    /// <summary>
    /// Resolves a shape code into a <see cref="ShapeDefinition"/> via the CANONICAL
    /// <c>ShapeRegistry</c> (<c>GameHelper.Core.ShapeRegistry</c>) + its paired
    /// <c>ShapeIdManager</c> — the same registry the miner, HUD inspector and serializer
    /// use. Resolving here (rather than via a captured rewirer dependency) is the
    /// Phase-1 fix for the shape-identity bug: per-instance <c>ShapeId</c>s are sequential,
    /// so a definition built from a different manager mines as the wrong shape.
    ///
    /// <para>Doubles as validation: an invalid code yields <c>TryGetDefinition == false</c>
    /// (the factory can't parse the hash), so the dialog can reject it.</para>
    /// </summary>
    internal static class CanonicalShapeResolver
    {
        public static bool TryResolve(string? code, out ShapeDefinition shape, out string diag)
        {
            shape = null!;

            if (string.IsNullOrWhiteSpace(code))
            {
                diag = "empty shape code";
                return false;
            }
            code = code!.Trim();

#pragma warning disable CS0618 // IGameSessionManagers is [Obsolete] — a mod here has no DI seam.
            IGameSessionManagers? sessions = GameHelper.Core;
#pragma warning restore CS0618
            if (sessions == null) { diag = "no active game session"; return false; }

            ShapeRegistry registry = sessions.ShapeRegistry;
            if (registry == null) { diag = "canonical ShapeRegistry is null"; return false; }

            IShapeIdManager idMgr = registry.ShapeIdManager; // publicized private field
            if (idMgr == null) { diag = "canonical ShapeIdManager is null"; return false; }

            ShapeId id;
            try
            {
                id = idMgr.Resolve(code);
            }
            catch (Exception ex)
            {
                diag = $"resolve threw: {ex.Message}";
                return false;
            }

            if (!registry.TryGetDefinition(id, out shape))
            {
                diag = $"not a valid shape code (no definition for id #{id.Uid})";
                return false;
            }

            diag = $"id=#{id.Uid}, layers={shape.Layers.Length}, parts={shape.PartCount}";
            return true;
        }
    }
}
