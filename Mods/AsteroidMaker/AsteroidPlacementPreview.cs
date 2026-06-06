using System;
using System.Reflection;
using Drawing;
using Game.Core.Coordinates;
using Game.Core.Rendering;
using MonoMod.RuntimeDetour;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace AsteroidMaker
{
    /// <summary>
    /// PLAN-P04-001 (drag preview) — draws a translucent rectangle over the tiles a box-drag would
    /// fill, so the player sees the asteroid footprint <b>before</b> releasing the mouse (mimicking the
    /// way vanilla shows a platform/selection preview while dragging).
    ///
    /// <para><b>Why a render hook.</b> Drawing needs a per-frame <see cref="FrameDrawOptionsNoLOD"/>
    /// (it carries the renderers + the in-game debug draw builder); an <c>ITickRewirer</c> has none.
    /// The map-draw pipeline calls <c>MapDrawer.Draw(FrameDrawOptionsNoLOD)</c> exactly once per frame,
    /// so we hook that non-generic seam: run the original map draw, then — only while a drag is live
    /// (<see cref="AsteroidUiState.DragPreviewActive"/>) — paint the box on top.</para>
    ///
    /// <para><b>How it draws.</b> We mirror vanilla's own debug-chunk highlight
    /// (<c>DebugViewChunkIO</c>: <c>options.GetDebugDrawer().SolidPlane(chunk.ToCenter_W(), up, 20, color)</c>)
    /// — one chunk-sized translucent plane per tile in the dragged rectangle. Reusing that exact call
    /// inherits the correct space-map orientation without us having to reason about world axes. The whole
    /// thing is wrapped in try/catch so a preview hiccup can never break the map render; the placement
    /// itself is unaffected (the controller computes the real footprint independently at release).</para>
    /// </summary>
    internal sealed class AsteroidPlacementPreview : IDisposable
    {
        // Match the controller's MaxDragDim so the preview can never iterate more tiles than a drag
        // could place (defensive cap; real drags are tiny — a full space belt is 9×4).
        private const int MaxDim = 64;

        // Chunk edge length in world units (a SolidPlane size of 20 covers exactly one chunk tile, so
        // adjacent tiles tile edge-to-edge into a continuous filled rectangle).
        private const float ChunkWorld = 20f;

        // Translucent cyan fill — reads as a placement highlight without hiding the map underneath.
        private static readonly Color FillColor = new Color(0.28f, 0.72f, 1f, 0.35f);

        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;
        private readonly Hook _hook;
        private bool _warned;

        public AsteroidPlacementPreview(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;

            MethodInfo method = typeof(MapDrawer).GetMethod(
                "Draw",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(FrameDrawOptionsNoLOD) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "AsteroidMaker: failed to find MapDrawer.Draw(FrameDrawOptionsNoLOD).");

            DrawDelegate detour = DrawPrefix;
            _hook = new Hook(method, detour);

            logger.Info?.Log(
                "[AsteroidMaker:preview] drag-preview drawer installed (highlights the box footprint while dragging).");
        }

        public void Dispose() => _hook.Dispose();

        private delegate void DrawDelegate(
            Action<MapDrawer, FrameDrawOptionsNoLOD> orig,
            MapDrawer self,
            FrameDrawOptionsNoLOD options);

        private void DrawPrefix(
            Action<MapDrawer, FrameDrawOptionsNoLOD> orig,
            MapDrawer self,
            FrameDrawOptionsNoLOD options)
        {
            // Always draw the real map first; the preview goes on top.
            orig(self, options);

            if (!_ui.DragPreviewActive) return;

            try
            {
                DrawDragBox(options);
            }
            catch (Exception ex)
            {
                // Never let a preview problem disturb the map render. Log once.
                if (!_warned)
                {
                    _warned = true;
                    _logger.Warning?.Log($"[AsteroidMaker:preview] drag-preview draw threw (non-fatal, suppressed): {ex}");
                }
            }
        }

        private void DrawDragBox(FrameDrawOptionsNoLOD options)
        {
            GlobalChunkCoordinate anchor = _ui.DragAnchor;
            GlobalChunkCoordinate current = _ui.DragCurrent;

            // Same rectangle the placer builds at release: grow from the anchor toward the cursor,
            // clamped per-axis. (0,0) — the anchor itself — is always included.
            int dirX = current.x >= anchor.x ? 1 : -1;
            int dirY = current.y >= anchor.y ? 1 : -1;
            int spanX = Math.Min(Math.Abs(current.x - anchor.x) + 1, MaxDim);
            int spanY = Math.Min(Math.Abs(current.y - anchor.y) + 1, MaxDim);

            using CommandBuilder builder = options.GetDebugDrawer();
            float3 up = new float3(0f, 1f, 0f);
            for (int i = 0; i < spanX; i++)
            {
                for (int j = 0; j < spanY; j++)
                {
                    GlobalChunkCoordinate tile = anchor + new ChunkVector(dirX * i, dirY * j, 0);
                    builder.SolidPlane(tile.ToCenter_W(), up, ChunkWorld, FillColor);
                }
            }
        }
    }
}
