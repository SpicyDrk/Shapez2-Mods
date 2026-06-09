using System;
using System.Linq;
using System.Reflection;
using Core.Localization;
using ShapezShifter.Flow.Toolbar;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace AsteroidForge
{
    /// <summary>
    /// PLAN-P02-001 Task 1 — inserts a "Asteroid Forge" entry into the space-map build
    /// toolbar, bound to the <c>PlacementInitiatorId</c> registered by
    /// <see cref="AsteroidIslandPlacementRewirer"/>.
    ///
    /// <para>Implements <see cref="IToolbarDataRewirer"/>. Shifter's
    /// <c>ToolbarInterceptor</c> prefix-hooks <c>ToolbarBuilder.BuildToolbar</c> and calls
    /// <see cref="ModifyToolbarData"/> for each toolbar it builds. We can't use Shifter's
    /// public <c>ToolbarRewirer</c> directly because it takes the
    /// <c>PlacementInitiatorId</c> at construction, but that id only exists after the
    /// placer-registration pass runs in-session — so we read it lazily from
    /// <see cref="AsteroidUiState"/>.</para>
    ///
    /// <para>The entry is appended to the <b>Space Platforms</b> category (matched by its
    /// title key, <c>island-toolbar.category-RegularPlatform</c>), after the platform
    /// shapes. Matching by title — rather than a hardcoded index — keeps placement stable
    /// across unlock state and other mods, and means we only touch the space-map toolbar
    /// (building bars and other contexts lack that category and are left untouched).</para>
    /// </summary>
    internal sealed class AsteroidToolbarRewirer : IToolbarDataRewirer
    {
        // Title-key substring of the space-map "Space Platforms" category, as seen in the
        // live toolbar tree: LazyText[island-toolbar.category-RegularPlatform.title].
        private const string SpacePlatformsCategoryKey = "category-RegularPlatform";
        private const string EntryTitle = "Asteroid Forge";
        private const string RemoveEntryTitle = "Remove Asteroid";

        private readonly AsteroidUiState _ui;
        private readonly ILogger _logger;
        private bool _loggedAdd;
        private bool _loggedMissing;

        public AsteroidToolbarRewirer(AsteroidUiState ui, ILogger logger)
        {
            _ui = ui;
            _logger = logger;
        }

        public ToolbarData ModifyToolbarData(ToolbarData toolbarData)
        {
            try
            {
                if (!_ui.InitiatorRegistered)
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        _logger.Warning?.Log(
                            "[AsteroidForge:ui] toolbar built before the initiator was registered; " +
                            "entry skipped this pass (will appear once placers are registered).");
                    }
                    return toolbarData;
                }

                // Find the Space Platforms category. Returning early when it's absent both
                // targets the right spot and ensures we never touch unrelated toolbars.
                if (toolbarData.RootToolbarElement is not IParentToolbarElementData root)
                {
                    return toolbarData;
                }

                IParentToolbarElementData? platforms = FindChildParentByTitleKey(root, SpacePlatformsCategoryKey);
                if (platforms == null)
                {
                    return toolbarData; // not the space-map toolbar
                }

                // Append after the platform shapes = end of the Space Platforms category.
                bool addedPlace = TryInsertEntry(
                    platforms, EntryTitle,
                    "Place a custom-shape mineable asteroid (author it via a shape code).",
                    _ui.InitiatorId, true, _ui.PlaceIcon);
                bool addedRemove = _ui.RemoveInitiatorRegistered && TryInsertEntry(
                    platforms, RemoveEntryTitle,
                    "Remove a custom asteroid you placed (click it on the space map).",
                    _ui.RemoveInitiatorId, true, _ui.RemoveIcon);

                if ((addedPlace || addedRemove) && !_loggedAdd)
                {
                    _loggedAdd = true;
                    _logger.Info?.Log("[AsteroidForge:ui] inserted 'Asteroid Forge' + 'Remove Asteroid' entries at the end of the Space Platforms category.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error?.Log($"[AsteroidForge:ui] ModifyToolbarData threw (non-fatal): {ex}");
            }

            return toolbarData;
        }

        public bool Equals(IRewirer other) => ReferenceEquals(this, other);

        /// <summary>
        /// Find a direct child of <paramref name="root"/> whose Title resolves to a string
        /// containing <paramref name="titleKey"/> and which is itself a parent (has Children).
        /// </summary>
        private static IParentToolbarElementData? FindChildParentByTitleKey(IParentToolbarElementData root, string titleKey)
        {
            foreach (IToolbarElementData child in root.Children)
            {
                object? title = GetMember(child, "Title");
                if (title != null
                    && (title.ToString() ?? "").Contains(titleKey)
                    && child is IParentToolbarElementData parent)
                {
                    return parent;
                }
            }
            return null;
        }

        /// <summary>
        /// Insert one <c>PlacementToolbarElementData</c> entry at the end of the category, bound to
        /// <paramref name="id"/> — unless <paramref name="registered"/> is false or an entry with
        /// that title already exists (the toolbar is rebuilt per open). Returns true if inserted.
        /// </summary>
        private bool TryInsertEntry(
            IParentToolbarElementData category, string entryTitle, string entryDescription,
            PlacementInitiatorId id, bool registered, Sprite? icon)
        {
            if (!registered) return false;
            if (CategoryHasEntry(category, entryTitle)) return false;

            IText title = new RawText(entryTitle);
            IText description = new RawText(entryDescription);
            var entry = new PlacementToolbarElementData(title, description, id, icon);
            category.InsertAtIndex((IToolbarElementData)entry, category.Children.Count());
            return true;
        }

        /// <summary>True if the category already contains an entry whose title contains <paramref name="entryTitle"/>.</summary>
        private static bool CategoryHasEntry(IParentToolbarElementData category, string entryTitle)
        {
            foreach (IToolbarElementData child in category.Children)
            {
                object? title = GetMember(child, "Title");
                if (title != null && (title.ToString() ?? "").Contains(entryTitle))
                {
                    return true;
                }
            }
            return false;
        }

        // The toolbar element-data types live in the DLL-only Game.Orchestration assembly;
        // Title is exposed differently across element types, so read it reflectively.
        private static object? GetMember(object node, string name)
        {
            Type t = node.GetType();
            PropertyInfo? p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(node);
            FieldInfo? f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(node);
            return null;
        }
    }
}
