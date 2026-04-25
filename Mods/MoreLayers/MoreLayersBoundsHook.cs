using System;
using System.Reflection;
using Game.Core.Coordinates;
using Game.Core.Rendering.Buildings;
using Game.Core.Rendering.Islands.Notches;
using Game.Core.Rendering.Islands.PlayingField;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace MoreLayers
{
    /// <summary>
    /// Camera-distance culling fix for layer-4+ rendering. Surfaced by UAT-P02
    /// re-test 3 (2026-04-25) after PLAN-P02-004 made layer-4+ meshes visible:
    /// when the camera is close (or at certain angles), all meshes on the
    /// affected platform vanish — the per-frame frustum-vs-AABB cull test sees
    /// the cached AABB entirely outside the frustum and skips drawing.
    ///
    /// <para>
    /// Two drawers cache a per-chunk AABB sized for <c>maxBuildingLayer + 1</c>:
    /// <list type="bullet">
    /// <item><c>IslandChunkStaticBuildingsDrawer.ContentBounds</c> (decompiled
    /// line 38-40) — used for the static-mesh combined draw.</item>
    /// <item><c>BuildingSimpleAnimationDrawer.MaxZ</c> +
    /// <c>ChunkCullingDimensions</c> (decompiled line 88-91) — used for the
    /// dynamic per-building animation draw.</item>
    /// </list>
    /// Both default to Z-size 4 with vanilla cap=3. We extend both to Z-size 7
    /// (matches our cap=6) so the cull-AABB always at least intersects the
    /// frustum when meshes might be visible.
    /// </para>
    ///
    /// <para>
    /// PLAN-P02-002 implemented the chunk-bounds patch but it was prematurely
    /// removed in PLAN-P02-003 when the IOORE root cause was discovered. Both
    /// fixes are needed — IOORE was the visible-bug cause; bounds is the
    /// camera-distance issue exposed once IOORE was fixed.
    /// </para>
    /// </summary>
    internal sealed class MoreLayersBoundsHook : IDisposable
    {
        private const float TargetCapBoundsZ = 7f; // matches MoreLayersScenarioRewirer.TargetCap (6) + 1f

        // --- IslandChunkStaticBuildingsDrawer.ContentBounds (private readonly Bounds) ---
        private static readonly FieldInfo ContentBoundsField =
            typeof(IslandChunkStaticBuildingsDrawer).GetField(
                "ContentBounds",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect IslandChunkStaticBuildingsDrawer.ContentBounds.");

        // --- BuildingSimpleAnimationDrawer private fields ---
        private static readonly FieldInfo MaxZField =
            typeof(BuildingSimpleAnimationDrawer).GetField(
                "MaxZ",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect BuildingSimpleAnimationDrawer.MaxZ.");

        private static readonly FieldInfo ChunkCullingDimensionsField =
            typeof(BuildingSimpleAnimationDrawer).GetField(
                "ChunkCullingDimensions",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect BuildingSimpleAnimationDrawer.ChunkCullingDimensions.");

        // --- IslandPlayingFieldLayersDrawer.MaxLayer (private readonly short) ---
        private static readonly FieldInfo PlayingFieldMaxLayerField =
            typeof(IslandPlayingFieldLayersDrawer).GetField(
                "MaxLayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect IslandPlayingFieldLayersDrawer.MaxLayer.");

        // --- IslandNotchDrawer.TileDrawCache.Roles (private NotchTileBuildingRole[]) ---
        private static readonly FieldInfo NotchTileDrawCacheRolesField =
            typeof(IslandNotchDrawer.TileDrawCache).GetField(
                "Roles",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MoreLayers: failed to reflect IslandNotchDrawer.TileDrawCache.Roles.");

        private readonly Hook _staticChunkHook;
        private readonly Hook _animationDrawerHook;
        private readonly Hook _playingFieldHook;
        private readonly Hook _notchTileCacheHook;

        public MoreLayersBoundsHook(ILogger logger)
        {
            // Hook 1: IslandChunkStaticBuildingsDrawer ctor (6 params)
            ConstructorInfo staticChunkCtor = typeof(IslandChunkStaticBuildingsDrawer)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[]
                    {
                        typeof(IMapModel),
                        typeof(GlobalChunkCoordinate),
                        typeof(VisualThemeBaseResources),
                        typeof(short),
                        typeof(IResourceLifetime),
                        typeof(ILogger),
                    },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find IslandChunkStaticBuildingsDrawer ctor.");

            StaticChunkCtorDelegate staticDetour = StaticChunkCtorPostfix;
            _staticChunkHook = new Hook(staticChunkCtor, staticDetour);

            // Hook 2: BuildingSimpleAnimationDrawer ctor (1 param)
            ConstructorInfo animationCtor = typeof(BuildingSimpleAnimationDrawer)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(short) },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find BuildingSimpleAnimationDrawer ctor.");

            AnimationCtorDelegate animDetour = AnimationCtorPostfix;
            _animationDrawerHook = new Hook(animationCtor, animDetour);

            // Hook 3: IslandPlayingFieldLayersDrawer ctor (2 params).
            // MaxLayer caches the cap and is used in `for (int i = 1; i <= MaxLayer; i++)`
            // to draw per-layer playing-field planes. With cap=3, planes for layers 4-6
            // are never drawn → "all meshes including platforms vanish" at higher layers.
            ConstructorInfo playingFieldCtor = typeof(IslandPlayingFieldLayersDrawer)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(IslandPlayingfieldLayoutCache), typeof(short) },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find IslandPlayingFieldLayersDrawer ctor.");

            PlayingFieldCtorDelegate pfDetour = PlayingFieldCtorPostfix;
            _playingFieldHook = new Hook(playingFieldCtor, pfDetour);

            // Hook 4: IslandNotchDrawer.TileDrawCache ctor (1 param) — defensive.
            // Roles array is sized maxBuildingLayer + 1 (= 4 vanilla); SetRole(int, ...)
            // would IOORE on cross-island layer-4+ notch interactions.
            ConstructorInfo notchCacheCtor = typeof(IslandNotchDrawer.TileDrawCache)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(int) },
                    modifiers: null)
                ?? throw new InvalidOperationException(
                    "MoreLayers: failed to find IslandNotchDrawer.TileDrawCache ctor.");

            NotchTileCacheCtorDelegate ntDetour = NotchTileCacheCtorPostfix;
            _notchTileCacheHook = new Hook(notchCacheCtor, ntDetour);

            logger.Info?.Log("MoreLayers: bounds hooks installed (chunk-static + building-animation + playing-field + notch-tile-cache; Z=7, MaxLayer=6, Roles[7]).");
        }

        public void Dispose()
        {
            _notchTileCacheHook.Dispose();
            _playingFieldHook.Dispose();
            _animationDrawerHook.Dispose();
            _staticChunkHook.Dispose();
        }

        // ---- IslandChunkStaticBuildingsDrawer ----

        private delegate void StaticChunkCtorDelegate(
            Action<IslandChunkStaticBuildingsDrawer, IMapModel, GlobalChunkCoordinate, VisualThemeBaseResources, short, IResourceLifetime, ILogger> orig,
            IslandChunkStaticBuildingsDrawer self,
            IMapModel map,
            GlobalChunkCoordinate origin_GC,
            VisualThemeBaseResources resources,
            short maxBuildingLayer,
            IResourceLifetime resourceLifetime,
            ILogger logger);

        private static void StaticChunkCtorPostfix(
            Action<IslandChunkStaticBuildingsDrawer, IMapModel, GlobalChunkCoordinate, VisualThemeBaseResources, short, IResourceLifetime, ILogger> orig,
            IslandChunkStaticBuildingsDrawer self,
            IMapModel map,
            GlobalChunkCoordinate origin_GC,
            VisualThemeBaseResources resources,
            short maxBuildingLayer,
            IResourceLifetime resourceLifetime,
            ILogger logger)
        {
            orig(self, map, origin_GC, resources, maxBuildingLayer, resourceLifetime, logger);

            Bounds original = (Bounds)ContentBoundsField.GetValue(self);
            Vector3 newCenter = new Vector3(original.center.x, original.center.y, TargetCapBoundsZ / 2f);
            Vector3 newSize = new Vector3(original.size.x, original.size.y, TargetCapBoundsZ);
            ContentBoundsField.SetValue(self, new Bounds(newCenter, newSize));
        }

        // ---- BuildingSimpleAnimationDrawer ----

        private delegate void AnimationCtorDelegate(
            Action<BuildingSimpleAnimationDrawer, short> orig,
            BuildingSimpleAnimationDrawer self,
            short maxBuildingLayer);

        private static void AnimationCtorPostfix(
            Action<BuildingSimpleAnimationDrawer, short> orig,
            BuildingSimpleAnimationDrawer self,
            short maxBuildingLayer)
        {
            orig(self, maxBuildingLayer);

            // MaxZ is float; ChunkCullingDimensions is WorldDimension (struct, ~Vector3-shaped).
            MaxZField.SetValue(self, TargetCapBoundsZ);
            ChunkCullingDimensionsField.SetValue(self, new WorldDimension(22f, 22f, TargetCapBoundsZ));
        }

        // ---- IslandPlayingFieldLayersDrawer ----

        private delegate void PlayingFieldCtorDelegate(
            Action<IslandPlayingFieldLayersDrawer, IslandPlayingfieldLayoutCache, short> orig,
            IslandPlayingFieldLayersDrawer self,
            IslandPlayingfieldLayoutCache layoutCache,
            short maxLayer);

        private static void PlayingFieldCtorPostfix(
            Action<IslandPlayingFieldLayersDrawer, IslandPlayingfieldLayoutCache, short> orig,
            IslandPlayingFieldLayersDrawer self,
            IslandPlayingfieldLayoutCache layoutCache,
            short maxLayer)
        {
            orig(self, layoutCache, maxLayer);
            // Override the cached cap so the per-layer playingfield-plane loop draws all 6 layers.
            PlayingFieldMaxLayerField.SetValue(self, (short)6);
        }

        // ---- IslandNotchDrawer.TileDrawCache (defensive) ----

        private delegate void NotchTileCacheCtorDelegate(
            Action<IslandNotchDrawer.TileDrawCache, int> orig,
            IslandNotchDrawer.TileDrawCache self,
            int maxBuildingLayer);

        private static void NotchTileCacheCtorPostfix(
            Action<IslandNotchDrawer.TileDrawCache, int> orig,
            IslandNotchDrawer.TileDrawCache self,
            int maxBuildingLayer)
        {
            orig(self, maxBuildingLayer);

            // Replace Roles with a size-7 array initialized to Empty.
            var roles = new NotchTileBuildingRole[7];
            for (int i = 0; i < roles.Length; i++) roles[i] = NotchTileBuildingRole.Empty;
            NotchTileDrawCacheRolesField.SetValue(self, roles);
        }
    }
}
