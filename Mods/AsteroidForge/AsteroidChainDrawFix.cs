using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Rendering;
using Game.Core.Rendering.Culling;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// PLAN-P04-001 (UAT fix, Test 4) — suppresses a vanilla NullReferenceException that fires every
    /// frame in the miner <b>draw</b> path when an extractor / boost-chain tile over a custom asteroid
    /// isn't on resource.
    ///
    /// <para><b>The vanilla bug.</b> <c>ChainedExtractionSystem&lt;T&gt;.ComputeChainConnectedChunks</c>
    /// dereferences <c>MapResourceAccessor.GetResourceAt_GC(position).ChunksLookup_G</c> without the
    /// null-check it uses elsewhere in the same class. For a vanilla asteroid that never bites (the
    /// patch is shaped/large enough that miner tiles are always on resource). Our raw
    /// <c>ShapeMapResourceSource</c> injection lets the player create the null case: a boost chain run
    /// off a thin/edge custom patch, or an extractor left orphaned after a custom patch is deleted /
    /// undone. The miner drawer then NREs every frame — flooding <c>Player.log</c> and (since the
    /// uncaught exception unwinds the whole map draw) interfering with rendering. Mining is unaffected.</para>
    ///
    /// <para><b>Why hook here.</b> The obvious fix — guarding <c>ComputeChainConnectedChunks</c> — is a
    /// method on a <i>generic</i> type, and this MonoMod build refuses to hook generic methods
    /// ("generic hooks are not supported"). So we hook the nearest <b>non-generic</b> seam:
    /// <c>IndependentMapSubDrawer.Draw</c> (the base method that calls each generic drawer's
    /// <c>DoDraw</c>), and wrap it in a try/catch — but ONLY for the miner drawer subtypes
    /// (<c>DynamicMinerDrawer&lt;&gt;</c> / <c>MinerExtensionDynamicDrawer&lt;&gt;</c>), so no other
    /// sub-drawer's exceptions are ever masked. Catching here is strictly better than the status quo:
    /// the rest of the map keeps drawing instead of the whole frame's map draw unwinding.</para>
    /// </summary>
    internal sealed class AsteroidChainDrawFix : IDisposable
    {
        // Only these sub-drawer subtypes get the try/catch — everything else draws untouched.
        private static readonly HashSet<Type> GuardedDrawerDefs = new HashSet<Type>
        {
            typeof(DynamicMinerDrawer<>),
            typeof(MinerExtensionDynamicDrawer<>),
        };

        private readonly Hook _hook;
        private readonly ILogger _logger;
        private bool _warned;

        public AsteroidChainDrawFix(ILogger logger)
        {
            _logger = logger;

            MethodInfo method = typeof(IndependentMapSubDrawer).GetMethod(
                "Draw",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(FrameDrawOptionsNoLOD), typeof(MapCullResult) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "AsteroidForge: failed to find IndependentMapSubDrawer.Draw(FrameDrawOptionsNoLOD, MapCullResult).");

            DrawDelegate detour = DrawPrefix;
            _hook = new Hook(method, detour);

            logger.Info?.Log(
                "[AsteroidForge:fix] miner-draw guard installed (suppresses the per-frame chained-extraction " +
                "NRE on thin/edge custom patches; other sub-drawers untouched).");
        }

        public void Dispose() => _hook.Dispose();

        // The live sub-drawer instances are concrete SUBCLASSES of the generic miner drawers (they
        // inherit DoDraw), so an exact generic-type-definition match misses them — walk the base chain.
        private static bool IsGuardedDrawer(Type t)
        {
            for (Type? cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (cur.IsGenericType && GuardedDrawerDefs.Contains(cur.GetGenericTypeDefinition()))
                    return true;
            }
            return false;
        }

        private delegate void DrawDelegate(
            Action<IndependentMapSubDrawer, FrameDrawOptionsNoLOD, MapCullResult> orig,
            IndependentMapSubDrawer self,
            FrameDrawOptionsNoLOD options,
            MapCullResult cullResult);

        private void DrawPrefix(
            Action<IndependentMapSubDrawer, FrameDrawOptionsNoLOD, MapCullResult> orig,
            IndependentMapSubDrawer self,
            FrameDrawOptionsNoLOD options,
            MapCullResult cullResult)
        {
            if (!IsGuardedDrawer(self.GetType()))
            {
                orig(self, options, cullResult);
                return;
            }

            try
            {
                orig(self, options, cullResult);
            }
            catch (NullReferenceException)
            {
                // Known vanilla NRE in ChainedExtractionSystem.ComputeChainConnectedChunks when a miner
                // / chain tile over a custom patch isn't on resource. Draw-path only; swallow so the
                // log doesn't flood and the rest of the map keeps rendering. Logged once.
                if (!_warned)
                {
                    _warned = true;
                    _logger.Warning?.Log(
                        "[AsteroidForge:fix] suppressed a chained-extraction miner-draw NRE over a custom patch " +
                        "(boost chain off a thin patch, or an extractor orphaned after delete). Logged once; mining unaffected.");
                }
            }
        }
    }
}
