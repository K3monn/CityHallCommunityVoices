using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering.Universal;
using TMPro;
using Unity.AI.Navigation;
using CityHall.Systems;
using CityHall.NPC;
using CityHall.Dialogue;
using CityHall.Environment;

namespace CityHall.EditorTools
{
    /// <summary>
    /// Procedurally builds Assets/Scenes/Town.unity from primitive geometry —
    /// this exists instead of a hand-authored .unity YAML file because Unity's
    /// scene serialization format (GUIDs, fileIDs, exact YAML structure) is
    /// fragile to hand-write and impossible to verify without a real Editor to
    /// open it in. Everything here goes through Unity's own scripting API
    /// (GameObject.CreatePrimitive, PrefabUtility, NavMeshSurface, ...), so
    /// whatever it produces is guaranteed to be a structurally valid scene —
    /// the *content* (layout, colors, text) still deserves a design pass once
    /// you're looking at it in the Editor, but it won't be corrupt.
    ///
    /// Geometry was originally primitive-only (cubes/cylinders/spheres/planes/
    /// capsules — Unity's built-in PrimitiveType set), and most of it still is.
    /// No custom mesh generation is used anywhere (e.g. no hand-rolled cone
    /// meshes for pitched roofs, which is why houses that fall back to the
    /// procedural path get flat cap roofs instead) because a hand-authored
    /// mesh's triangle winding can't be verified without opening it in Unity,
    /// and a mis-wound mesh renders invisible from outside.
    ///
    /// Single-family houses, citizens, and residential-zone trees now
    /// instantiate real prefabs from the Neighbourhood Pack / SimpleNaturePack
    /// (transferred into Assets/ from the user's other project) instead of
    /// primitives — see the "Asset-pack prefab helpers" region below. Every one
    /// of those swaps fails soft to the original primitive geometry if the
    /// expected prefab path isn't found, so a missing/renamed asset pack
    /// degrades the look rather than breaking the build. Mailboxes,
    /// streetlamps, fences, cars, shop/public buildings, and road/sidewalk
    /// tiles are still primitive-only — swapping those in is exactly the kind
    /// of pass this scaffold expects you to do next — see README section 4
    /// onward and the project notes doc's "Next steps" section.
    ///
    /// Usage: open the project in Unity, menu bar "City Hall > Build Town Scene".
    /// </summary>
    public static class TownSceneBuilder
    {
        // ------------------------------------------------------------
        // Shared state for a single build run
        // ------------------------------------------------------------
        private static readonly Dictionary<Color, Material> _materialCache = new Dictionary<Color, Material>();
        private static Light _sunLight;
        private static Material _defaultSkybox;
        private static GameObject _systemsRoot;
        private static Transform _playerRigTransform;
        private static Transform _mainCameraTransform;
        private static TimeOfDayManager _timeOfDayManager;
        private static PerformanceLODManager _performanceLODManager;
        private static GameObject _citizenPrefab;
        private static GameObject _plazaPortalMarker;   // for wiring PortalTravel after both areas exist
        private static GameObject _interiorPortalMarker;
        // 2026-07-23 Town Hall interior redesign: the Final Reflection Hall's "Return to
        // Neighborhood" half of its exit choice — same two-area show/hide mechanism as
        // _plazaPortalMarker (see WirePortals), just a second doorway back to the same
        // destination from deeper in the building.
        private static GameObject _finalHallExitPortalMarker;
        private static GameObject _townRoot;
        private static GameObject _interiorRoot;

        private static readonly List<NPCSpawner.SpawnEntry> _residentialSpawns = new List<NPCSpawner.SpawnEntry>();
        private static readonly List<NPCSpawner.SpawnEntry> _commercialSpawns = new List<NPCSpawner.SpawnEntry>();
        private static readonly List<NPCSpawner.SpawnEntry> _publicRowSpawns = new List<NPCSpawner.SpawnEntry>();
        private static readonly List<NPCSpawner.SpawnEntry> _plazaSpawns = new List<NPCSpawner.SpawnEntry>();
        // 2026-07-23 Town Hall interior redesign: ambient City Hall staff in the main lobby, and
        // the two Volunteer Debrief Lounge NPCs — mirrors the outdoor zone lists above exactly.
        private static readonly List<NPCSpawner.SpawnEntry> _lobbySpawns = new List<NPCSpawner.SpawnEntry>();
        private static readonly List<NPCSpawner.SpawnEntry> _volunteerLoungeSpawns = new List<NPCSpawner.SpawnEntry>();
        private static readonly List<NPCSpawner.SpawnEntry> _mayorOfficeSpawns = new List<NPCSpawner.SpawnEntry>();

        // ------------------------------------------------------------
        // Real asset-pack integration (added after the Neighbourhood Pack
        // and SimpleNaturePack were transferred into Assets/ from the
        // user's other project). See class-level comment update below and
        // the "Asset-pack prefab helpers" region for how this is used.
        // ------------------------------------------------------------
        private const string kHousePrefabDir = "Assets/Neighbourhood Pack/Prefabs/Props/";
        private const string kCharacterPrefabDir = "Assets/Neighbourhood Pack/Prefabs/Characters/";
        private const string kNaturePrefabDir = "Assets/SimpleNaturePack/Prefabs/";
        private static readonly List<string> _missingAssetPaths = new List<string>();
        private static readonly List<GameObject> _citizenPrefabVariants = new List<GameObject>();
        // Fixed seed (not readonly — reset each BuildScene() call, see below) so repeated builds in
        // the same Editor session place the same greenery in the same spots instead of shuffling
        // every time, matching NPCSpawner's existing deterministicAppearance convention.
        private static System.Random _greeneryRng = new System.Random(20260722);

        // ------------------------------------------------------------
        // Warm/muted suburban-downtown palette (Phase 1 of the town
        // redesign request — environment & streets). Named so every call
        // site reads as an intentional design choice ("Palette.SageGreen")
        // rather than another magic-number Color literal, and so future
        // passes (Main Street, City Hall interior) can pull from the same
        // set instead of drifting into new ad hoc colors.
        //
        // These are my own best-effort match to the "warm, muted,
        // Craftsman/Farmhouse/Modern-Civic, no neon/no cartoon" brief — the
        // exact hex list from that message didn't survive into this pass's
        // context, so if specific hex values were in mind, send them and
        // this table swaps in directly (every call site below references
        // Palette.*, not raw Color literals, so a repaint is a one-file
        // edit, not a hunt-and-replace).
        // ------------------------------------------------------------
        private static class Palette
        {
            // Primary — large structural surfaces (walls, roads, roofs, hardscape)
            public static readonly Color CreamStucco = new Color(0.925f, 0.894f, 0.827f);
            public static readonly Color WarmSand = new Color(0.855f, 0.792f, 0.686f);
            public static readonly Color SageGreen = new Color(0.616f, 0.663f, 0.549f);
            public static readonly Color SlateBlueGray = new Color(0.494f, 0.549f, 0.573f);
            public static readonly Color TerracottaRoof = new Color(0.702f, 0.396f, 0.290f);
            public static readonly Color CharcoalTrim = new Color(0.267f, 0.263f, 0.251f);
            public static readonly Color SoftWhite = new Color(0.945f, 0.933f, 0.902f);
            public static readonly Color WarmGray = new Color(0.686f, 0.667f, 0.631f);
            public static readonly Color Concrete = new Color(0.784f, 0.769f, 0.729f);
            public static readonly Color AsphaltGray = new Color(0.302f, 0.314f, 0.322f);
            public static readonly Color ForestGreen = new Color(0.325f, 0.412f, 0.298f);

            // Accent — awnings, signage, trim, doors, street furniture
            public static readonly Color BurntOrange = new Color(0.792f, 0.443f, 0.180f);
            public static readonly Color MustardGold = new Color(0.796f, 0.635f, 0.208f);
            public static readonly Color DustyRose = new Color(0.741f, 0.494f, 0.478f);
            public static readonly Color DenimBlue = new Color(0.294f, 0.408f, 0.522f);
            public static readonly Color Plum = new Color(0.475f, 0.337f, 0.408f);
            public static readonly Color Brick = new Color(0.639f, 0.278f, 0.235f);
            public static readonly Color TealAccent = new Color(0.243f, 0.522f, 0.514f);
            public static readonly Color GoldenYellow = new Color(0.878f, 0.706f, 0.271f);
        }

        [MenuItem("City Hall/Build Town Scene")]
        public static void BuildScene()
        {
            _materialCache.Clear();
            _residentialSpawns.Clear();
            _commercialSpawns.Clear();
            _publicRowSpawns.Clear();
            _plazaSpawns.Clear();
            _lobbySpawns.Clear();
            _volunteerLoungeSpawns.Clear();
            _mayorOfficeSpawns.Clear();
            _missingAssetPaths.Clear();
            _citizenPrefabVariants.Clear();
            _houseVariantCounter = 0;
            _greeneryRng = new System.Random(20260722);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();
            BuildCitizenPrefab();

            var systemsGO = new GameObject("Systems");
            _systemsRoot = systemsGO;
            systemsGO.AddComponent<CivicDataLoader>();
            _timeOfDayManager = systemsGO.AddComponent<TimeOfDayManager>();
            ConfigureTimeOfDayManager(_timeOfDayManager);
            _performanceLODManager = systemsGO.AddComponent<PerformanceLODManager>();
            systemsGO.AddComponent<AccessibilitySettings>(); // 2026-07-22 UI brief — see that file's doc comment
            systemsGO.AddComponent<CityMoraleSystem>(); // 2026-07-22 governor/canvassing decision system — see that file's doc comment
            systemsGO.AddComponent<MoraleAmbientReflector>();

            _townRoot = new GameObject("Town_Root");
            _interiorRoot = new GameObject("Interior_Root");
            _interiorRoot.transform.position = new Vector3(300f, 0f, 0f);
            _interiorRoot.SetActive(false);

            BuildGround(_townRoot.transform);
            BuildResidential(_townRoot.transform);
            BuildNeighborhoodPerimeterTrees(_townRoot.transform);
            BuildCommercial(_townRoot.transform);
            BuildPublicRow(_townRoot.transform);
            BuildCityHallExterior(_townRoot.transform);
            BuildWorldBoundaries(_townRoot.transform);
            BuildInterior(_interiorRoot.transform);
            BuildTownHallWings(_interiorRoot.transform); // 2026-07-23 Town Hall interior redesign

            BuildNPCSpawners();
            BuildPlayerRig();
            WirePortals();
            BuildNavMesh();

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/Town.unity");

            // 2026-07-23 Town Hall interior redesign: MainMenuReturnTrigger's "Return to Main
            // Menu" kiosk calls SceneManager.LoadScene(buildIndex) at runtime, which silently
            // no-ops (logs an error, does nothing) unless the target scene is actually registered
            // in File > Build Settings — an unregistered scene's buildIndex is -1. Nothing in this
            // project registered Town.unity there before now (there was no runtime scene-reload
            // feature that needed it), so that registration is done here, once, right after the
            // scene is saved, rather than leaving it as a manual step the user could easily miss
            // and only discover by walking all the way to the Final Reflection Hall to test it.
            var buildScenes = EditorBuildSettings.scenes;
            bool alreadyRegistered = false;
            foreach (var s in buildScenes)
                if (s.path == "Assets/Scenes/Town.unity") { alreadyRegistered = true; break; }
            if (!alreadyRegistered)
            {
                var updated = new EditorBuildSettingsScene[buildScenes.Length + 1];
                System.Array.Copy(buildScenes, updated, buildScenes.Length);
                updated[buildScenes.Length] = new EditorBuildSettingsScene("Assets/Scenes/Town.unity", true);
                EditorBuildSettings.scenes = updated;
            }

            Debug.Log("City Hall & Community Voices: Assets/Scenes/Town.unity built. Press Play to smoke-test with mouse+keyboard " +
                       "(left-click to interact), or see README sections 4-9 to layer in the real asset pack, XR locomotion, and lighting.");

            if (_missingAssetPaths.Count > 0)
            {
                Debug.LogWarning("City Hall & Community Voices: " + _missingAssetPaths.Count + " expected asset-pack prefab(s) were not " +
                    "found at their expected path, so this build fell back to primitive placeholder geometry for those spots instead. " +
                    "This usually means the Neighbourhood Pack / SimpleNaturePack folders moved or were renamed after import. Missing paths:\n" +
                    string.Join("\n", _missingAssetPaths));
            }
        }

        // ==============================================================
        // Primitive helpers — everything below is Unity's own built-in
        // PrimitiveType geometry, never a hand-authored mesh.
        // ==============================================================

        private static Material GetMaterial(Color color)
        {
            if (_materialCache.TryGetValue(color, out var cached)) return cached;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard"); // Built-in RP fallback
            var mat = new Material(shader) { name = "AutoMat_" + ColorUtility.ToHtmlStringRGB(color) };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            _materialCache[color] = mat;
            return mat;
        }

        private static GameObject CreateBox(Transform parent, string name, Vector3 localPos, Vector3 size, Color color, Vector3? eulerRot = null, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            if (eulerRot.HasValue) go.transform.localRotation = Quaternion.Euler(eulerRot.Value);
            go.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);
            if (markStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return go;
        }

        private static GameObject CreateCylinder(Transform parent, string name, Vector3 localPos, float radius, float height, Color color, Vector3? eulerRot = null, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            // Unity's cylinder primitive: default radius 0.5, height 2 (centered on pivot).
            go.transform.localScale = new Vector3(radius * 2f, height / 2f, radius * 2f);
            if (eulerRot.HasValue) go.transform.localRotation = Quaternion.Euler(eulerRot.Value);
            go.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);

            // BUG FOUND 2026-07-22 (from "still physically stopped" at the City Hall archway after
            // fixing the portal's own trigger collider): CreatePrimitive(Cylinder)'s default collider
            // is a CapsuleCollider, and Unity clamps a capsule's height to at least 2x its radius —
            // so any "flat disc" cylinder (radius bigger than half its height, e.g. the archway's
            // Threshold welcome-mat marker: radius 1.2, height 0.02) silently gets an invisible
            // collider inflated up to 2x its radius tall, WAY past what's visible. For the Threshold
            // specifically that's a ~2.4m-tall invisible pillar standing in the middle of the
            // doorway — same bug, independently, on several tabletop/porthole props added this
            // session (café/bistro table tops, the laundromat porthole, flower/bread-basket rims,
            // even the barber pole's color bands). Swap to a BoxCollider sized to the actual
            // unscaled bounding box whenever this clamping would kick in, instead of auditing every
            // call site by hand — box.size (1,2,1) combined with the localScale already set above
            // reproduces the cylinder's true (diameter x height x diameter) footprint exactly, never
            // ballooning past what's drawn.
            if (height < radius * 2f)
            {
                Object.DestroyImmediate(go.GetComponent<Collider>());
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(1f, 2f, 1f);
            }

            if (markStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return go;
        }

        private static GameObject CreateSphere(Transform parent, string name, Vector3 localPos, float radius, Color color, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one * (radius * 2f);
            go.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);
            if (markStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return go;
        }

        /// <summary>Unity's Plane primitive already lies flat in the XZ plane (10x10 default), so unlike
        /// A-Frame/three.js no rotation is needed — widthX/depthZ map directly to a /10 scale.</summary>
        private static GameObject CreateGroundPlane(Transform parent, string name, Vector3 localPos, float widthX, float depthZ, Color color, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(widthX / 10f, 1f, depthZ / 10f);
            go.GetComponent<Renderer>().sharedMaterial = GetMaterial(color);
            if (markStatic) GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return go;
        }

        // 2026-07-23 Town Hall interior redesign: general-purpose wall builders that optionally
        // leave a gap (a doorway/corridor opening) partway along the wall, splitting it into up to
        // two segments around that gap instead of one solid box. Written as reusable helpers
        // rather than hand-computing each wing's segment coordinates individually — every new
        // room below (and BuildInterior's existing Lobby, retrofitted to use these for its new
        // wing doorways) goes through one of these two, which keeps the split math in one place
        // and tested once instead of duplicated per wall.
        //
        // "Along X" = a wall running east-west at a fixed Z (a north or south wall); "Along Z" = a
        // wall running north-south at a fixed X (an east or west wall). Gap bounds are in the
        // wall's own running axis (X for an Along-X wall, Z for an Along-Z wall).
        private static void BuildWallAlongX(Transform zone, string name, float centerX, float z, float fullWidth, float height, float thickness, Color color, float? gapMin = null, float? gapMax = null)
        {
            float xMin = centerX - fullWidth / 2f;
            float xMax = centerX + fullWidth / 2f;
            if (gapMin.HasValue && gapMax.HasValue && gapMax.Value > xMin && gapMin.Value < xMax)
            {
                float gMin = Mathf.Clamp(gapMin.Value, xMin, xMax);
                float gMax = Mathf.Clamp(gapMax.Value, xMin, xMax);
                if (gMin > xMin)
                {
                    float segWidth = gMin - xMin;
                    CreateBox(zone, name + "_A", new Vector3(xMin + segWidth / 2f, height / 2f, z), new Vector3(segWidth, height, thickness), color);
                }
                if (gMax < xMax)
                {
                    float segWidth = xMax - gMax;
                    CreateBox(zone, name + "_B", new Vector3(gMax + segWidth / 2f, height / 2f, z), new Vector3(segWidth, height, thickness), color);
                }
            }
            else
            {
                CreateBox(zone, name, new Vector3(centerX, height / 2f, z), new Vector3(fullWidth, height, thickness), color);
            }
        }

        private static void BuildWallAlongZ(Transform zone, string name, float x, float centerZ, float fullDepth, float height, float thickness, Color color, float? gapMin = null, float? gapMax = null)
        {
            float zMin = centerZ - fullDepth / 2f;
            float zMax = centerZ + fullDepth / 2f;
            if (gapMin.HasValue && gapMax.HasValue && gapMax.Value > zMin && gapMin.Value < zMax)
            {
                float gMin = Mathf.Clamp(gapMin.Value, zMin, zMax);
                float gMax = Mathf.Clamp(gapMax.Value, zMin, zMax);
                if (gMin > zMin)
                {
                    float segDepth = gMin - zMin;
                    CreateBox(zone, name + "_A", new Vector3(x, height / 2f, zMin + segDepth / 2f), new Vector3(thickness, height, segDepth), color);
                }
                if (gMax < zMax)
                {
                    float segDepth = zMax - gMax;
                    CreateBox(zone, name + "_B", new Vector3(x, height / 2f, gMax + segDepth / 2f), new Vector3(thickness, height, segDepth), color);
                }
            }
            else
            {
                CreateBox(zone, name, new Vector3(x, height / 2f, centerZ), new Vector3(thickness, height, fullDepth), color);
            }
        }

        // 2026-07-23 addition, right after the Town Hall interior redesign, per direct user
        // feedback once they'd seen it in-editor ("it looks great, but could you add just
        // simple posters on the walls?"): a small flat poster — frame + colored inset panel +
        // a two-line label — mounted flush to a wall. Children are siblings parented under the
        // unscaled `poster` anchor, NOT under the "Frame" CreateBox result — CreateBox stretches
        // its own transform.localScale to the box's size, so anything parented under it would
        // inherit that non-uniform scale (the exact bug documented on the curb bulb-out
        // planters earlier this session; same fix applied here preemptively).
        //
        // Deliberately used only on the interior's North/South walls (the ones built via
        // BuildWallAlongX) at zero Y-rotation — every existing wall-mounted label/sign in this
        // file (CITY HALL — MAIN LOBBY, TOWN HALL, COMMUNITY ISSUES BOARD, etc.) already uses
        // zero rotation successfully on exactly this kind of wall, so mirroring that convention
        // is guaranteed correct instead of guessing at an untested rotation for a Z-running wall.
        private static void BuildWallPoster(Transform zone, string name, Vector3 localPos, string headline, string caption, Color accentColor)
        {
            var poster = new GameObject(name).transform;
            poster.SetParent(zone, false);
            poster.localPosition = localPos;

            CreateBox(poster, "Frame", Vector3.zero, new Vector3(1.1f, 1.5f, 0.06f), new Color(0.42f, 0.388f, 0.333f));
            CreateBox(poster, "Panel", new Vector3(0f, 0f, 0.035f), new Vector3(0.94f, 1.32f, 0.02f), accentColor);
            CreateLabel(poster, headline, new Vector3(0f, 0.32f, 0.05f), Vector3.zero, 0.16f, Color.white, 0.85f);
            CreateLabel(poster, caption, new Vector3(0f, -0.28f, 0.05f), Vector3.zero, 0.105f, new Color(0.96f, 0.96f, 0.94f), 0.85f);
        }

        private static Sprite _roundedPanelSprite;

        /// <summary>Procedurally generates (once, cached) a single reusable rounded-rect Sprite —
        /// anti-aliased and 9-sliced so any panel size stays crisp — entirely in code, no
        /// Editor-authored texture asset needed. Used by CreateGlassPanel below for the "rounded
        /// corners, soft drop shadow, frosted-glass-style semi-transparent panel" look from the
        /// 2026-07-22 UI brief. True background blur (real frosted glass) needs a URP renderer
        /// feature / grab-pass shader that can't be authored blind without the Editor to verify
        /// it compiles and renders — this approximates the same "soft, modern, not-a-flat-
        /// rectangle" read with tinted semi-transparency and a drop shadow instead, which is
        /// honest about what's actually achievable from static analysis alone.</summary>
        private static Sprite GetRoundedPanelSprite()
        {
            if (_roundedPanelSprite != null) return _roundedPanelSprite;
            const int size = 64;
            const int radius = 18;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float dx = Mathf.Max(0f, Mathf.Max(radius - px, px - (size - radius)));
                    float dy = Mathf.Max(0f, Mathf.Max(radius - py, py - (size - radius)));
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - dist + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            _roundedPanelSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f,
                0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            return _roundedPanelSprite;
        }

        /// <summary>Builds a glassmorphism-style panel background (soft drop shadow + rounded,
        /// semi-transparent tinted panel) as children of `parent`, matching how the old flat `bg`
        /// Image was used everywhere — callers still add their own label children the same way
        /// afterward, just against a nicer background. Returns the main panel's Image so callers
        /// can wire it into a controller's `background` field for live accessibility recoloring.</summary>
        private static Image CreateGlassPanel(Transform parent, Color tint)
        {
            var shadowGO = new GameObject("Shadow");
            shadowGO.transform.SetParent(parent, false);
            var shadowImage = shadowGO.AddComponent<Image>();
            shadowImage.sprite = GetRoundedPanelSprite();
            shadowImage.type = Image.Type.Sliced;
            shadowImage.color = new Color(0f, 0f, 0f, 0.35f);
            var shadowRT = shadowGO.GetComponent<RectTransform>();
            shadowRT.anchorMin = Vector2.zero; shadowRT.anchorMax = Vector2.one;
            shadowRT.offsetMin = new Vector2(-4f, -14f); shadowRT.offsetMax = new Vector2(6f, -4f);

            var bg = new GameObject("Background");
            bg.transform.SetParent(parent, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.sprite = GetRoundedPanelSprite();
            bgImage.type = Image.Type.Sliced;
            bgImage.color = tint;
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;

            return bgImage;
        }

        private static Texture2D _flagPictureTexture;

        /// <summary>Procedurally generates (once, cached) a simple stars-and-stripes-style flag
        /// texture — 2026-07-22/23 "make City Hall patriotic... add pictures" brief. Real
        /// photographic images can't be sourced or imported without the Editor (no internet asset
        /// fetch, no way to author/verify a texture import pipeline blind), so this uses the same
        /// per-pixel procedural-texture technique already established in
        /// DialogueUIController.GetAvatarSprite (deterministic pixel painting via SetPixel/Apply)
        /// rather than leaving "pictures" undone. Used both for the two entrance picture panels
        /// and the flagpole banner, via CreateTexturedBox below.</summary>
        private static Texture2D GetFlagPictureTexture()
        {
            if (_flagPictureTexture != null) return _flagPictureTexture;
            const int w = 96, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            Color red = new Color(0.698f, 0.132f, 0.203f);
            Color white = Color.white;
            Color blue = new Color(0.176f, 0.216f, 0.412f);
            int stripeH = Mathf.Max(1, h / 13);
            int cantonW = (int)(w * 0.4f);
            int cantonH = stripeH * 7;
            for (int y = 0; y < h; y++)
            {
                int stripeIndex = y / stripeH;
                Color stripeColor = (stripeIndex % 2 == 0) ? red : white;
                for (int x = 0; x < w; x++)
                {
                    if (x < cantonW && y >= h - cantonH)
                    {
                        // Inside the canton: blue field with a light grid of small white squares
                        // standing in for stars — an actual 5-pointed star needs more resolution
                        // than a 96x64 procedural texture comfortably gives.
                        bool star = (x % 8 >= 3 && x % 8 <= 5) && (y % 8 >= 3 && y % 8 <= 5);
                        tex.SetPixel(x, y, star ? white : blue);
                    }
                    else
                    {
                        tex.SetPixel(x, y, stripeColor);
                    }
                }
            }
            tex.Apply();
            _flagPictureTexture = tex;
            return tex;
        }

        private static Texture2D _civicSealTexture;

        /// <summary>Procedurally generates (once, cached) a circular civic-seal-style texture
        /// (gold ring + center emblem on a navy field, cream background outside the circle) — the
        /// second of the two "pictures" from the patriotic City Hall brief, reusing this file's
        /// existing civic blue/gold palette (see EntranceBanner colors above) so it visually
        /// matches the rest of the entrance dressing rather than introducing a new palette.</summary>
        private static Texture2D GetCivicSealTexture()
        {
            if (_civicSealTexture != null) return _civicSealTexture;
            const int size = 96;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            Color cream = new Color(0.910f, 0.894f, 0.847f);
            Color navyField = new Color(0.122f, 0.227f, 0.302f);
            Color gold = new Color(0.796f, 0.635f, 0.208f);
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float outerR = size * 0.46f;
            float ringInnerR = size * 0.38f;
            float starHalfDiagonal = size * 0.14f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float dist = Vector2.Distance(new Vector2(px, py), center);
                    Color c = cream;
                    if (dist <= outerR)
                    {
                        c = dist >= ringInnerR ? gold : navyField;
                        // Simple diamond "star" stand-in at the seal's center — a real 5-pointed
                        // star needs more resolution than a 96x96 procedural texture comfortably
                        // gives, same reasoning as the flag texture's square-dot stars above.
                        float diag = Mathf.Abs(px - center.x) + Mathf.Abs(py - center.y);
                        if (diag <= starHalfDiagonal) c = gold;
                    }
                    tex.SetPixel(x, y, c);
                }
            }
            tex.Apply();
            _civicSealTexture = tex;
            return tex;
        }

        /// <summary>A thin textured box rather than a Quad primitive, deliberately: a Quad only
        /// renders from one face-normal direction, and which local axis that normal points down
        /// isn't confirmable without opening the Editor (see BuildCityHallExterior's own
        /// model-orientation caveat for the same class of risk). A box's Cube primitive renders
        /// the same material on all 6 faces, so the texture is guaranteed visible from the front
        /// regardless of which way "front" actually turns out to face once placed.</summary>
        private static GameObject CreateTexturedBox(Transform parent, string name, Vector3 localPos, Vector3 size, Texture2D tex, Vector3? eulerRot = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            if (eulerRot.HasValue) go.transform.localRotation = Quaternion.Euler(eulerRot.Value);
            Object.DestroyImmediate(go.GetComponent<Collider>()); // decorative wall/pole art, never meant to block movement
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { name = "PictureMat_" + name };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            else if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        /// <summary>See CityHall.Systems.UILayer's doc comment for the full occlusion-proof-UI
        /// reasoning. Call once per rig (desktop + XR) right after that rig's own camera is
        /// created — adds a second stacked camera that renders only the UI layer with its depth
        /// buffer pre-cleared, and excludes that layer from the main camera so nothing draws it
        /// twice.</summary>
        private static void BuildOcclusionProofUICamera(Camera mainCam)
        {
            // BUG FOUND 2026-07-22 (user screenshot: Game view rendering solid flat blue, zero
            // geometry visible): this project uses URP (see Packages/manifest.json), which does
            // NOT composite multiple plain Camera components the way the legacy Built-in
            // pipeline did via depth ordering + clearFlags=Depth alone — every URP camera is
            // implicitly a "Base" camera unless explicitly told otherwise. Two independent Base
            // cameras both targeting the screen do not reliably layer on top of each other the
            // way the original version of this method assumed; the second (overlay) camera was
            // instead fully clearing the screen with URP's own default background before
            // drawing only its own (mostly-empty, UI-layer-only) content — wiping out everything
            // the main camera had just drawn, which is exactly the flat blue screen reported.
            //
            // Fix: URP's actual mechanism for "render this camera's output on top of that one"
            // is Camera Stacking — mark the overlay camera's
            // UniversalAdditionalCameraData.renderType = Overlay and add it to the base camera's
            // own cameraStack list. That's the URP-specific setup this method was missing; the
            // rest of the approach (depth-cleared overlay camera rendering only the UI layer) is
            // unchanged and still the right technique, it just needed URP's own wiring for it.
            mainCam.cullingMask &= ~(1 << UILayer.Layer);

            var uiCamGO = new GameObject("UIOcclusionProof_Camera");
            uiCamGO.transform.SetParent(mainCam.transform, false); // rides along with the main camera exactly, every frame, for free
            var uiCam = uiCamGO.AddComponent<Camera>();
            uiCam.clearFlags = CameraClearFlags.Depth;
            uiCam.cullingMask = 1 << UILayer.Layer;
            uiCam.depth = mainCam.depth + 1f;
            uiCam.nearClipPlane = mainCam.nearClipPlane;
            uiCam.farClipPlane = mainCam.farClipPlane;
            uiCam.fieldOfView = mainCam.fieldOfView;

            var mainCamData = mainCam.GetComponent<UniversalAdditionalCameraData>();
            if (mainCamData == null) mainCamData = mainCam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            mainCamData.renderType = CameraRenderType.Base;

            var uiCamData = uiCamGO.AddComponent<UniversalAdditionalCameraData>();
            uiCamData.renderType = CameraRenderType.Overlay;

            if (!mainCamData.cameraStack.Contains(uiCam))
                mainCamData.cameraStack.Add(uiCam);
        }

        private static TextMeshPro CreateLabel(Transform parent, string text, Vector3 localPos, Vector3 eulerRot, float fontSize, Color color, float width = 4f)
        {
            var go = new GameObject("Label_" + text.Replace("\n", " "));
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(eulerRot);
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            var rt = tmp.rectTransform;
            rt.sizeDelta = new Vector2(width, 1.5f);
            return tmp;
        }

        private static NPCSpawner.SpawnEntry MakeSpawnPoint(Transform parent, string rosterId, float x, float z, float yRotation)
        {
            var marker = new GameObject("Spawn_" + rosterId);
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = new Vector3(x, 0f, z);
            marker.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            return new NPCSpawner.SpawnEntry { rosterId = rosterId, point = marker.transform, prefabOverride = PickCitizenVariant(rosterId) };
        }

        // ==============================================================
        // Asset-pack prefab helpers (Neighbourhood Pack / SimpleNaturePack)
        //
        // These packs were transferred in from the user's own vrd.2 project
        // (real houses, a rigged character model, and nature dressing) to
        // replace some of the primitive placeholder geometry above. This
        // scaffold still can't open a real Editor to verify exact prefab
        // scale/pivot, so every swap here is deliberately narrow (houses,
        // citizens, residential-zone trees) and fails soft: if a prefab is
        // missing at its expected path, LoadPrefab() records it and the
        // caller falls back to the original primitive geometry instead of
        // breaking the build. Mailboxes, streetlamps, fences, cars, and
        // road/sidewalk tiles were deliberately NOT swapped in this pass —
        // streetlamps because TimeOfDayManager's night-lighting hook finds
        // its emissive bulbs by searching for renderers named "Bulb" (see
        // BuildPlayerRig below), which a real street_light prefab won't
        // have without extra wiring; tiles/fences because their real-world
        // module size/pivot needs eyeballing in the Editor first to avoid
        // visible gaps or overlaps. See the project notes doc for the full
        // list of what's left for a manual follow-up pass.
        // ==============================================================

        private static GameObject LoadPrefab(string assetPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) _missingAssetPaths.Add(assetPath);
            return prefab;
        }

        /// <summary>Instantiates a real asset-pack prefab as a nested-prefab child (preserving the
        /// link to its source asset), matching the position/rotation/static-flag conventions the
        /// CreateBox/CreateCylinder/etc. helpers above use. If the source has no Collider anywhere
        /// in its hierarchy (true for every prefab checked in both packs), a BoxCollider sized to
        /// the combined renderer bounds is added so it still blocks the player and registers for
        /// static batching/navigation the same way the primitive geometry does.</summary>
        private static GameObject InstantiateAssetPrefab(Transform parent, GameObject prefab, string name, Vector3 localPos, Vector3? eulerRot = null, bool markStatic = true, bool ensureCollider = true)
        {
            if (prefab == null) return null;
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null) instance = Object.Instantiate(prefab); // defensive fallback, shouldn't trigger for a real asset prefab
            instance.name = name;
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = localPos;
            if (eulerRot.HasValue) instance.transform.localRotation = Quaternion.Euler(eulerRot.Value);

            if (ensureCollider && instance.GetComponentInChildren<Collider>() == null)
            {
                var renderers = instance.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                    var scale = instance.transform.lossyScale;
                    var box = instance.AddComponent<BoxCollider>();
                    box.center = instance.transform.InverseTransformPoint(bounds.center);
                    box.size = new Vector3(
                        bounds.size.x / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
                        bounds.size.y / Mathf.Max(Mathf.Abs(scale.y), 0.0001f),
                        bounds.size.z / Mathf.Max(Mathf.Abs(scale.z), 0.0001f));
                }
            }

            if (markStatic) GameObjectUtility.SetStaticEditorFlags(instance, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return instance;
        }

        /// <summary>Deterministic pick from whichever real character prefabs successfully loaded
        /// (see BuildCitizenPrefab). Same rosterId always yields the same variant, matching
        /// NPCSpawner's existing deterministicAppearance convention. Returns null (defaulting to
        /// the always-available capsule prefab) if no character variants are available.</summary>
        private static GameObject PickCitizenVariant(string rosterId)
        {
            if (_citizenPrefabVariants.Count == 0) return null;
            int index = Mathf.Abs(rosterId.GetHashCode()) % _citizenPrefabVariants.Count;
            return _citizenPrefabVariants[index];
        }

        /// <summary>Picks a random SimpleNaturePack variant from the given name pool (e.g. "Bush",
        /// "Rock", "Flowers", "Grass", "Mushroom") and instantiates it. Purely decorative — unlike
        /// houses/citizens/trees, a missing prefab here just means that one scatter point silently
        /// contributes nothing (still recorded in _missingAssetPaths by LoadPrefab), no primitive
        /// fallback needed for a single bush or flower clump.</summary>
        private static void ScatterNaturePrefab(Transform parent, string[] namePool, Vector3 localPos)
        {
            string variant = namePool[_greeneryRng.Next(namePool.Length)];
            var prefab = LoadPrefab(kNaturePrefabDir + variant + ".prefab");
            float yRot = (float)(_greeneryRng.NextDouble() * 360.0);
            InstantiateAssetPrefab(parent, prefab, "Greenery_" + variant, localPos, new Vector3(0f, yRot, 0f));
        }

        private static readonly string[] kBushPool = { "Bush_01", "Bush_02", "Bush_03" };
        private static readonly string[] kYardPool = { "Bush_01", "Bush_02", "Bush_03", "Flowers_01", "Flowers_02" };
        private static readonly string[] kGroundClutterPool = { "Grass_01", "Grass_02", "Mushroom_01", "Mushroom_02" };
        private static readonly string[] kRockPool = { "Rock_01", "Rock_02", "Rock_03", "Rock_04", "Rock_05" };

        // ==============================================================
        // Lighting
        // ==============================================================
        private static void BuildLighting()
        {
            var lightGO = new GameObject("Directional Light (Sun)");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.91f, 0.73f);
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGO.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            _sunLight = light;

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.82f, 0.88f, 0.93f);
            RenderSettings.ambientEquatorColor = new Color(0.6f, 0.63f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.33f, 0.32f, 0.28f);

            // BuildScene() creates the scene via NewSceneSetup.EmptyScene (no
            // default GameObjects, no default Lighting-settings skybox), so
            // RenderSettings.skybox is null until we assign one — Unity's fallback
            // for a null skybox is a flat dark camera background color, which is
            // what "the sky is dark" in Play mode was. GetBuiltinExtraResource
            // pulls the same Default-Skybox.mat every fresh Unity project ships
            // with (Skybox/Procedural shader, renders fine under URP), so this is
            // the same sky a scene created via File > New Scene would already have.
            var defaultSkybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            RenderSettings.skybox = defaultSkybox;
            _defaultSkybox = defaultSkybox;
        }

        /// <summary>
        /// TimeOfDayManager.sunColorOverDay (a Gradient) has no field initializer,
        /// so AddComponent<TimeOfDayManager>() leaves it null — Start() calls
        /// Apply() immediately, which reads sunColorOverDay.Evaluate(t) and throws
        /// a NullReferenceException every frame, aborting the rest of Apply() (sun
        /// intensity, night-light emissive toggling, skybox swap) before it runs.
        /// Also wires daySkybox/eveningSkybox to the same Default-Skybox material
        /// BuildLighting() assigns to RenderSettings.skybox, so the evening
        /// transition doesn't null the skybox back out later in a play session
        /// (both fields were previously left unassigned).
        /// </summary>
        private static void ConfigureTimeOfDayManager(TimeOfDayManager manager)
        {
            var sunGradient = new Gradient();
            sunGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.55f, 0.55f, 0.68f), 0f),   // dawn — cool, dim
                    new GradientColorKey(new Color(1f, 0.95f, 0.85f), 0.25f),   // morning — warm bright
                    new GradientColorKey(new Color(1f, 0.98f, 0.94f), 0.55f),   // midday — bright white
                    new GradientColorKey(new Color(1f, 0.68f, 0.42f), 0.82f),   // evening — warm orange
                    new GradientColorKey(new Color(0.32f, 0.34f, 0.48f), 1f)    // night — cool, dim
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
            manager.sunColorOverDay = sunGradient;
            manager.daySkybox = _defaultSkybox;
            manager.eveningSkybox = _defaultSkybox;
        }

        // ==============================================================
        // Citizen prefabs. Always builds the original capsule-body prefab
        // as a guaranteed-available fallback (_citizenPrefab, wired as
        // NPCSpawner.defaultCitizenPrefab), then tries to additionally
        // build real-character variants from the Neighbourhood Pack's
        // "character boy"/"character girl" prefabs. Those two source
        // prefabs are genuinely modular (verified: each has a distinct
        // SkinnedMeshRenderer per body part — "body", "hair boy"/"hair_girl",
        // "shirt"/"dress", "pants" (boy only), "shoes"), which is exactly
        // the pattern NPCVariationController.skinRenderers expects for its
        // MaterialPropertyBlock-based skin-tone tinting, so that part wires
        // up directly. Neither source prefab ships with an Animator or a
        // Collider, so this only replaces the *visual* — animation and
        // collision are handled the same way as everything else in this
        // scaffold (NavMeshAgent-driven translation; BoxCollider sized to
        // the model's actual bounds, computed at build time rather than
        // guessed, since the real height isn't knowable without opening
        // the Editor).
        // ==============================================================
        private static void BuildCitizenPrefab()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs"))
                AssetDatabase.CreateFolder("Assets", "Prefabs");

            _citizenPrefab = BuildCitizenBase("Citizen_Base", null, requireVisualSource: false, new Color(0.25f, 0.35f, 0.55f));

            var boySource = LoadPrefab(kCharacterPrefabDir + "character boy.prefab");
            var girlSource = LoadPrefab(kCharacterPrefabDir + "character girl.prefab");
            var boyVariant = BuildCitizenBase("Citizen_Base_Boy", boySource, requireVisualSource: true, Color.clear);
            var girlVariant = BuildCitizenBase("Citizen_Base_Girl", girlSource, requireVisualSource: true, Color.clear);
            if (boyVariant != null) _citizenPrefabVariants.Add(boyVariant);
            if (girlVariant != null) _citizenPrefabVariants.Add(girlVariant);
            // If neither real character prefab was found, _citizenPrefabVariants stays empty and
            // PickCitizenVariant() returns null for every NPC, so every spawn just uses the
            // capsule-based _citizenPrefab as before — no behavior change if the assets are missing.
        }

        /// <summary>Shared citizen-root builder. Pass a null visualSource with requireVisualSource
        /// false to get the original primitive capsule body (capsuleColor used); pass a real
        /// character prefab (with requireVisualSource true) to get that model as the visual
        /// instead. Returns null when a visual source was required but LoadPrefab() couldn't find
        /// it (already recorded in _missingAssetPaths by that point).</summary>
        private static GameObject BuildCitizenBase(string prefabName, GameObject visualSource, bool requireVisualSource, Color capsuleColor)
        {
            if (requireVisualSource && visualSource == null) return null;

            var root = new GameObject("Citizen_Base");
            root.AddComponent<NPCIdentity>();
            var variation = root.AddComponent<NPCVariationController>();
            root.AddComponent<NPCRoutineController>(); // RequireComponent adds a NavMeshAgent
            var prompt = root.AddComponent<InteractionPrompt>();

            float headHeight; // used to place the IconRoot just above the visual, whatever it turns out to be
            if (visualSource == null)
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(root.transform, false);
                body.transform.localPosition = new Vector3(0f, 1f, 0f);
                body.GetComponent<Renderer>().sharedMaterial = GetMaterial(capsuleColor);
                headHeight = 2.3f; // matches the original scaffold's capsule proportions
            }
            else
            {
                // BUG FOUND 2026-07-22: this used to pass ensureCollider: false on the
                // reasoning that InteractionPrompt doesn't need a Collider (true — see
                // InteractionPrompt.cs's own class comment, it's Vector3.Distance-based)
                // but that missed that PlayerInteractor's raycast absolutely does need
                // SOMETHING to hit. The real "character boy/girl.prefab" source models
                // ship with zero Collider anywhere in their hierarchy (confirmed by
                // grepping the .prefab YAML directly — no "Collider" component at all),
                // and PickCitizenVariant() hands out these real-model variants to every
                // NPC once both were found, so with no collider added here, literally no
                // NPC in the whole town could ever be raycast-hit — every dialogue
                // interact silently failed. Fixed by adding a CapsuleCollider directly to
                // root, sized from the visual's actual renderer bounds, below.
                var visual = InstantiateAssetPrefab(root.transform, visualSource, "Body", Vector3.zero, markStatic: false, ensureCollider: false);
                var renderers = visual.GetComponentsInChildren<Renderer>();
                float maxY = 1.8f; // sane default if bounds computation finds nothing
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                    maxY = bounds.max.y - root.transform.position.y;
                }
                headHeight = maxY + 0.3f;

                var bodyCollider = root.AddComponent<CapsuleCollider>();
                bodyCollider.height = Mathf.Max(maxY, 0.5f);
                bodyCollider.radius = 0.32f;
                bodyCollider.center = new Vector3(0f, bodyCollider.height * 0.5f, 0f);

                var skinRenderer = System.Array.Find(renderers, r => r.gameObject.name == "body") as SkinnedMeshRenderer;
                if (skinRenderer != null) variation.skinRenderers = new Renderer[] { skinRenderer };
            }

            var iconRoot = new GameObject("IconRoot");
            iconRoot.transform.SetParent(root.transform, false);
            iconRoot.transform.localPosition = new Vector3(0f, headHeight, 0f);
            var iconMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            iconMarker.name = "IconMarker";
            iconMarker.transform.SetParent(iconRoot.transform, false);
            iconMarker.transform.localScale = Vector3.one * 0.22f;
            iconMarker.GetComponent<Renderer>().sharedMaterial = GetMaterial(new Color(1f, 0.85f, 0.3f));
            Object.DestroyImmediate(iconMarker.GetComponent<Collider>()); // decorative only, must not block raycasts
            iconRoot.SetActive(false);
            prompt.iconRoot = iconRoot.transform;

            var agent = root.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.radius = 0.3f;
                agent.height = 2f;
                agent.speed = 1.35f;
            }

            string path = "Assets/Prefabs/" + prefabName + ".prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        // ==============================================================
        // Ground, road, sidewalks, streetlamps (shared spine of the town)
        // ==============================================================
        private static void BuildGround(Transform root)
        {
            CreateGroundPlane(root, "Grass", Vector3.zero, 90f, 200f, Palette.SageGreen);
            CreateGroundPlane(root, "MainStreet_Road", new Vector3(0f, 0.01f, 0f), 8f, 190f, Palette.AsphaltGray);
            CreateGroundPlane(root, "Sidewalk_West", new Vector3(-5.2f, 0.015f, 0f), 2.2f, 190f, Palette.Concrete);
            CreateGroundPlane(root, "Sidewalk_East", new Vector3(5.2f, 0.015f, 0f), 2.2f, 190f, Palette.Concrete);

            var dashesParent = new GameObject("RoadDashes").transform;
            dashesParent.SetParent(root, false);
            for (float z = -90f; z <= 90f; z += 6f)
                CreateGroundPlane(dashesParent, "Dash", new Vector3(0f, 0.02f, z), 0.25f, 2f, Palette.SoftWhite);

            var lampsParent = new GameObject("Streetlamps").transform;
            lampsParent.SetParent(root, false);
            for (float z = -80f; z <= 80f; z += 16f)
            {
                foreach (var x in new[] { -6.5f, 6.5f })
                {
                    var lamp = new GameObject("Streetlamp").transform;
                    lamp.SetParent(lampsParent, false);
                    lamp.localPosition = new Vector3(x, 0f, z);
                    CreateCylinder(lamp, "Post", new Vector3(0f, 1.8f, 0f), 0.06f, 3.6f, new Color(0.361f, 0.373f, 0.373f));
                    var bulb = CreateSphere(lamp, "Bulb", new Vector3(0f, 3.6f, 0f), 0.18f, new Color(0.949f, 0.902f, 0.690f));
                    bulb.tag = "Untagged";
                    bulb.name = "Bulb"; // TimeOfDayManager.emissiveAtNight is wired to these renderers in WireSystemsReferences()
                }
            }
        }

        /// <summary>Crosswalk striping plus lightweight traffic-calming detail — a stop line set
        /// back on each approach (so cars/the player's sense of the street reads "yield here" before
        /// the stripes) and a curb bulb-out planter on each of the four sidewalk corners. Bulb-outs
        /// are real streets' way of narrowing the visual road width at a crossing and giving
        /// pedestrians a shorter, more visible crossing distance — here they're simple corner
        /// planters (CreateBox base + a real nature-pack flower/bush) since there's no curb-geometry
        /// asset to pull in, but they read the same way: a crossing that's clearly a *place*, not
        /// just paint on asphalt.</summary>
        private static void BuildCrosswalk(Transform root, float z)
        {
            var parent = new GameObject("Crosswalk_z" + z).transform;
            parent.SetParent(root, false);
            for (float x = -4.5f; x <= 4.5f; x += 1f)
                CreateGroundPlane(parent, "Stripe", new Vector3(x, 0.02f, z), 0.5f, 3f, Palette.SoftWhite);

            // Stop lines: one solid bar per direction of travel, set back 2.5m from the near edge
            // of the crossing (road runs along Z, lanes split at x=0).
            CreateGroundPlane(parent, "StopLine_South", new Vector3(-2f, 0.02f, z - 4.5f), 3.6f, 0.4f, Palette.SoftWhite);
            CreateGroundPlane(parent, "StopLine_North", new Vector3(2f, 0.02f, z + 4.5f), 3.6f, 0.4f, Palette.SoftWhite);

            // Curb bulb-out planters at all four sidewalk corners of this crossing. Each corner gets
            // its own (unscaled) anchor transform rather than parenting the flower prefab straight
            // under the CreateBox result — CreateBox sets its own transform.localScale to the box's
            // *size* (see CreateBox above), so a child placed under it would inherit that squashed
            // (1.1, 0.24, 1.1) scale and its local position would be scaled too, not just offset.
            var cornerSpots = new[] { new Vector2(-6.4f, -3.6f), new Vector2(-6.4f, 3.6f), new Vector2(6.4f, -3.6f), new Vector2(6.4f, 3.6f) };
            var cornerPool = new[] { kYardPool, kBushPool };
            for (int i = 0; i < cornerSpots.Length; i++)
            {
                var spot = cornerSpots[i];
                var anchor = new GameObject("CurbBulbOut").transform;
                anchor.SetParent(parent, false);
                anchor.localPosition = new Vector3(spot.x, 0f, z + spot.y);
                CreateBox(anchor, "Planter", new Vector3(0f, 0.12f, 0f), new Vector3(1.1f, 0.24f, 1.1f), Palette.WarmGray);
                ScatterNaturePrefab(anchor, cornerPool[i % cornerPool.Length], new Vector3(0f, 0.24f, 0f));
            }
        }

        // ==============================================================
        // Residential zone
        // ==============================================================
        // Cycles through the Neighbourhood Pack's 5 single-family house prefabs so
        // consecutive "house"-kind calls don't all look identical. "duplex" and
        // "apartment" kinds keep the procedural box massing below — the pack only
        // ships single-family homes, and guessing a multi-unit substitute without
        // being able to see its actual footprint in the Editor risked a house that
        // reads as the wrong building type, so that swap was left for a manual pass.
        private static int _houseVariantCounter = 0;

        private static GameObject BuildHouse(Transform root, float x, float z, string kind, Color color)
        {
            float facing = x < 0f ? 0f : 180f;
            var house = new GameObject("House_" + kind).transform;
            house.SetParent(root, false);
            house.localPosition = new Vector3(x, 0f, z);
            house.localRotation = Quaternion.Euler(0f, facing, 0f);

            if (kind == "house")
            {
                // Only cycles house_01..house_04 — house_05 is reserved exclusively for
                // BuildCityHallExterior()'s building below, so City Hall doesn't end up
                // looking like an oversized duplicate of a model also sitting on a lot
                // down the street.
                int variant = (_houseVariantCounter++ % 4) + 1;
                var housePrefab = LoadPrefab(kHousePrefabDir + "house_0" + variant + ".prefab");
                var instance = InstantiateAssetPrefab(house, housePrefab, "HouseModel", Vector3.zero);
                if (instance != null)
                {
                    AddDoorKnockInteraction(house);
                    return house.gameObject; // real prefab placed; skip the procedural fallback below
                }
            }

            // NOTE: flat cap roofs everywhere (not the pitched cone roofs in the
            // browser mockup) — see the class-level comment on why this scaffold
            // avoids hand-authored mesh geometry. This procedural path is also the
            // fallback if house_0N.prefab wasn't found above.
            if (kind == "duplex")
            {
                CreateBox(house, "UnitA", new Vector3(-1.1f, 1.25f, 0f), new Vector3(2.2f, 2.5f, 4f), color);
                CreateBox(house, "UnitB", new Vector3(1.1f, 1.25f, 0f), new Vector3(2.2f, 2.5f, 4f), color);
                CreateBox(house, "Roof", new Vector3(0f, 2.6f, 0f), new Vector3(4.6f, 0.3f, 4.4f), new Color(0.42f, 0.35f, 0.28f));
            }
            else if (kind == "apartment")
            {
                CreateBox(house, "Block", new Vector3(0f, 3f, 0f), new Vector3(5f, 6f, 4.5f), color);
                for (float fy = 1.2f; fy < 5.8f; fy += 1.4f)
                    foreach (var fx in new[] { -1.6f, 0f, 1.6f })
                        CreateBox(house, "Window", new Vector3(fx, fy, 2.26f), new Vector3(0.5f, 0.6f, 0.02f), new Color(0.812f, 0.902f, 0.918f));
            }
            else
            {
                CreateBox(house, "Body", new Vector3(0f, 1.25f, 0f), new Vector3(4f, 2.5f, 3.8f), color);
                CreateBox(house, "Roof", new Vector3(0f, 2.65f, 0f), new Vector3(4.3f, 0.3f, 4.1f), new Color(0.478f, 0.275f, 0.196f));
            }

            float doorX = kind == "apartment" ? 2.8f : 2.3f;
            CreateBox(house, "Door", new Vector3(doorX, 0.75f, 1.95f), new Vector3(0.7f, 1.5f, 0.05f), new Color(0.169f, 0.208f, 0.145f));
            AddDoorKnockInteraction(house);
            return house.gameObject;
        }

        private static void AddDoorKnockInteraction(Transform house)
        {
            house.gameObject.AddComponent<DoorKnockTrigger>();
            var prompt = house.gameObject.AddComponent<InteractionPrompt>();
            prompt.showDistance = 3.5f;

            var iconRoot = new GameObject("DoorKnockPrompt").transform;
            iconRoot.SetParent(house, false);
            iconRoot.localPosition = new Vector3(0f, 2.3f, 0f);
            var icon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            icon.name = "KnockIcon";
            icon.transform.SetParent(iconRoot, false);
            icon.transform.localScale = Vector3.one * 0.22f;
            icon.GetComponent<Renderer>().sharedMaterial = GetMaterial(new Color(0.878f, 0.706f, 0.271f));
            Object.DestroyImmediate(icon.GetComponent<Collider>());
            iconRoot.gameObject.SetActive(false);
            prompt.iconRoot = iconRoot;
        }

        private static void BuildPlayground(Transform root, float x, float z)
        {
            var pg = new GameObject("Playground").transform;
            pg.SetParent(root, false);
            pg.localPosition = new Vector3(x, 0f, z);
            CreateBox(pg, "SwingPostA", new Vector3(-1.4f, 1.1f, 0f), new Vector3(0.1f, 2.2f, 0.1f), new Color(0.788f, 0.471f, 0.184f));
            CreateBox(pg, "SwingPostB", new Vector3(1.4f, 1.1f, 0f), new Vector3(0.1f, 2.2f, 0.1f), new Color(0.788f, 0.471f, 0.184f));
            CreateBox(pg, "SwingBar", new Vector3(0f, 2.2f, 0f), new Vector3(3f, 0.08f, 0.08f), new Color(0.788f, 0.471f, 0.184f));
            CreateBox(pg, "Slide", new Vector3(3.2f, 0.75f, 0f), new Vector3(1.5f, 0.1f, 0.8f), new Color(0.788f, 0.761f, 0.659f), new Vector3(0f, 0f, 20f));

            // Landscaping around the swing/slide footprint (which spans roughly x=-1.5..4.7,
            // z=-1..1) — these three spots sit clear of it.
            var pool = new[] { kBushPool, kRockPool };
            var perimeterSpots = new[] { new Vector2(-3.5f, 2f), new Vector2(4.5f, -1.8f), new Vector2(-2f, -2.5f) };
            for (int i = 0; i < perimeterSpots.Length; i++)
                ScatterNaturePrefab(pg, pool[i % pool.Length], new Vector3(perimeterSpots[i].x, 0f, perimeterSpots[i].y));
        }

        private static void BuildCommunityGarden(Transform root, float x, float z)
        {
            var garden = new GameObject("CommunityGarden").transform;
            garden.SetParent(root, false);
            garden.localPosition = new Vector3(x, 0f, z);
            Color[] plantColors = { new Color(0.294f, 0.431f, 0.294f), new Color(0.42f, 0.58f, 0.31f), new Color(0.56f, 0.68f, 0.32f) };
            var plantPool = new[] { "Flowers_01", "Flowers_02", "Bush_01", "Bush_02", "Bush_03" };
            int idx = 0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    float px = c * 1.1f - 1.1f, pz = r * 1.1f - 1.1f;
                    CreateBox(garden, "Bed", new Vector3(px, 0.15f, pz), new Vector3(0.9f, 0.3f, 0.9f), new Color(0.361f, 0.247f, 0.165f));

                    // Real plant instead of a bare sphere. Beds are tightly spaced (1.1m) so a real
                    // bush/flower model may look denser/more overgrown than the old neat spheres —
                    // reads as a "lush community garden" rather than a bug, and falls back to the
                    // original sphere if the prefab path doesn't resolve.
                    string variant = plantPool[idx % plantPool.Length];
                    var plantPrefab = LoadPrefab(kNaturePrefabDir + variant + ".prefab");
                    float yRot = (float)(_greeneryRng.NextDouble() * 360.0);
                    var instance = InstantiateAssetPrefab(garden, plantPrefab, "Plant_" + variant, new Vector3(px, 0.3f, pz), new Vector3(0f, yRot, 0f));
                    if (instance == null)
                        CreateSphere(garden, "Plant", new Vector3(px, 0.35f, pz), 0.28f, plantColors[idx % 3]);
                    idx++;
                }
            }
        }

        private static void BuildBusStop(Transform root, float x, float z)
        {
            var stop = new GameObject("BusStop").transform;
            stop.SetParent(root, false);
            stop.localPosition = new Vector3(x, 0f, z);
            CreateBox(stop, "PostA", new Vector3(-0.9f, 1.1f, 0f), new Vector3(0.08f, 2.2f, 0.08f), new Color(0.361f, 0.373f, 0.373f));
            CreateBox(stop, "PostB", new Vector3(0.9f, 1.1f, 0f), new Vector3(0.08f, 2.2f, 0.08f), new Color(0.361f, 0.373f, 0.373f));
            CreateBox(stop, "Roof", new Vector3(0f, 2.2f, 0f), new Vector3(2.2f, 0.08f, 1f), new Color(0.361f, 0.373f, 0.373f));
            CreateBox(stop, "Bench", new Vector3(0f, 0.5f, 0.3f), new Vector3(2f, 0.5f, 0.15f), new Color(0.541f, 0.541f, 0.541f));
        }

        private static void BuildBikeRack(Transform root, float x, float z)
        {
            var rack = new GameObject("BikeRack").transform;
            rack.SetParent(root, false);
            rack.localPosition = new Vector3(x, 0f, z);
            for (int i = 0; i < 3; i++)
                CreateCylinder(rack, "Loop", new Vector3(i * 0.5f - 0.5f, 0.35f, 0f), 0.03f, 0.7f, new Color(0.541f, 0.541f, 0.541f), new Vector3(90f, 0f, 0f));
        }

        // --------------------------------------------------------------
        // Sidewalk furniture (Phase 1 town redesign — additive detail that
        // doesn't touch any existing coordinate, so it's safe to lay down
        // over both the commercial and residential sidewalks without
        // re-checking clearance against houses/trees/NPC spawns).
        // --------------------------------------------------------------
        private static void BuildBench(Transform root, float x, float z, float yRotation)
        {
            var bench = new GameObject("Bench").transform;
            bench.SetParent(root, false);
            bench.localPosition = new Vector3(x, 0f, z);
            bench.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            CreateBox(bench, "Seat", new Vector3(0f, 0.45f, 0f), new Vector3(1.4f, 0.08f, 0.5f), Palette.CharcoalTrim);
            CreateBox(bench, "Back", new Vector3(0f, 0.75f, -0.22f), new Vector3(1.4f, 0.5f, 0.06f), Palette.CharcoalTrim);
            foreach (var legX in new[] { -0.55f, 0.55f })
            {
                CreateBox(bench, "LegFront", new Vector3(legX, 0.22f, 0.18f), new Vector3(0.08f, 0.44f, 0.08f), Palette.WarmGray);
                CreateBox(bench, "LegBack", new Vector3(legX, 0.22f, -0.18f), new Vector3(0.08f, 0.44f, 0.08f), Palette.WarmGray);
            }
        }

        private static void BuildTrashBin(Transform root, float x, float z)
        {
            var bin = new GameObject("TrashBin").transform;
            bin.SetParent(root, false);
            bin.localPosition = new Vector3(x, 0f, z);
            CreateCylinder(bin, "Body", new Vector3(0f, 0.4f, 0f), 0.28f, 0.8f, Palette.SlateBlueGray);
            CreateCylinder(bin, "Lid", new Vector3(0f, 0.81f, 0f), 0.3f, 0.05f, Palette.CharcoalTrim);
        }

        /// <summary>Standalone sidewalk planter (distinct from BuildCrosswalk's corner bulb-outs) —
        /// a low box with a real nature-pack flower/bush so street furniture runs don't read as
        /// pure hardscape.</summary>
        private static void BuildStreetPlanter(Transform root, float x, float z)
        {
            var anchor = new GameObject("Planter").transform;
            anchor.SetParent(root, false);
            anchor.localPosition = new Vector3(x, 0f, z);
            CreateBox(anchor, "Box", new Vector3(0f, 0.22f, 0f), new Vector3(0.7f, 0.44f, 0.7f), Palette.TerracottaRoof);
            ScatterNaturePrefab(anchor, kYardPool, new Vector3(0f, 0.44f, 0f));
        }

        /// <summary>Lays down a repeating bench / planter / trash-bin pattern along a straight
        /// sidewalk run at a fixed x (road runs along Z), cycling the three pieces so the same prop
        /// doesn't repeat back-to-back. facingInward points furniture toward the road (benches face
        /// the street, matching how real sidewalk seating is oriented) — true for the west sidewalk
        /// (faces +X), false for the east sidewalk (faces -X).</summary>
        private static void BuildStreetFurnitureRun(Transform root, float x, float zStart, float zEnd, float interval, bool facingInward)
        {
            float benchYaw = facingInward ? 90f : 270f;
            int i = 0;
            for (float z = zStart; z <= zEnd; z += interval)
            {
                switch (i % 3)
                {
                    case 0: BuildBench(root, x, z, benchYaw); break;
                    case 1: BuildStreetPlanter(root, x, z); break;
                    default: BuildTrashBin(root, x, z); break;
                }
                i++;
            }
        }

        /// <summary>Info sign / board with a collider (from CreatePrimitive) doubling as the
        /// interactable's raycast target. sourceType/entryId must match Boards.json or CivicExhibits.json.</summary>
        private static GameObject BuildInfoSign(Transform root, string name, float x, float z, float yRotation, string title, CivicExhibitTrigger.SourceType sourceType, string entryId, Color signColor)
        {
            var post = new GameObject(name).transform;
            post.SetParent(root, false);
            post.localPosition = new Vector3(x, 0f, z);
            post.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            CreateBox(post, "Post", new Vector3(0f, 0.75f, 0f), new Vector3(0.08f, 1.5f, 0.08f), new Color(0.42f, 0.388f, 0.333f));
            var board = CreateBox(post, "Board", new Vector3(0f, 1.5f, 0f), new Vector3(1.3f, 0.9f, 0.06f), signColor);
            CreateLabel(board.transform, title, new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.35f, new Color(0.137f, 0.141f, 0.122f), 1.2f);

            var trigger = board.AddComponent<CivicExhibitTrigger>();
            trigger.sourceType = sourceType;
            trigger.entryId = entryId;
            var prompt = board.AddComponent<InteractionPrompt>();
            var icon = CreateSphere(board.transform, "IconMarker", new Vector3(0f, 0.7f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
            Object.DestroyImmediate(icon.GetComponent<Collider>());
            icon.SetActive(false);
            prompt.iconRoot = icon.transform;
            return post.gameObject;
        }

        // 2026-07-22 governor/canvassing decision system: same physical shape as BuildInfoSign,
        // but wired to CityMoraleBoardTrigger (live CityMoraleSystem text) instead of a static
        // CivicExhibitTrigger JSON lookup — see that component's doc comment.
        private static GameObject BuildMoraleBoard(Transform root, string name, float x, float z, float yRotation)
        {
            var post = new GameObject(name).transform;
            post.SetParent(root, false);
            post.localPosition = new Vector3(x, 0f, z);
            post.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            CreateBox(post, "Post", new Vector3(0f, 0.75f, 0f), new Vector3(0.08f, 1.5f, 0.08f), new Color(0.42f, 0.388f, 0.333f));
            var board = CreateBox(post, "Board", new Vector3(0f, 1.5f, 0f), new Vector3(1.3f, 0.9f, 0.06f), new Color(0.263f, 0.412f, 0.384f));
            CreateLabel(board.transform, "COMMUNITY\nPULSE", new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.32f, Color.white, 1.2f);

            board.AddComponent<CityMoraleBoardTrigger>();
            var prompt = board.AddComponent<InteractionPrompt>();
            var icon = CreateSphere(board.transform, "IconMarker", new Vector3(0f, 0.7f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
            Object.DestroyImmediate(icon.GetComponent<Collider>());
            icon.SetActive(false);
            prompt.iconRoot = icon.transform;
            return post.gameObject;
        }

        private static void BuildResidential(Transform root)
        {
            var zone = new GameObject("Residential").transform;
            zone.SetParent(root, false);

            // The original 9 lots (unchanged positions/colors/kinds) plus a THIRD row at x=±35 and
            // 4 more lots pushing the existing rows further north (up to z=94) — all 10 new
            // positions were checked programmatically against every existing house/tree/structure
            // position (min. 3m clearance beyond each object's approximate footprint radius, plus
            // road-corridor and grass-plane-bounds exclusion) rather than eyeballed, since the scene
            // is dense enough now that manual arithmetic risked a silent overlap. Still not
            // Editor-verified — that's the one thing only the user can confirm.
            var lots = new (float x, float z, string kind, Color color)[]
            {
                (-13f, 66f, "house", new Color(0.847f, 0.788f, 0.639f)),
                (-13f, 76f, "house", new Color(0.780f, 0.698f, 0.6f)),
                (13f, 70f, "duplex", new Color(0.878f, 0.827f, 0.722f)),
                (13f, 56f, "apartment", new Color(0.718f, 0.788f, 0.761f)),
                (-13f, 54f, "house", new Color(0.847f, 0.788f, 0.639f)),
                (-24f, 68f, "house", new Color(0.827f, 0.749f, 0.612f)),
                (-24f, 82f, "house", new Color(0.706f, 0.749f, 0.694f)),
                (24f, 58f, "house", new Color(0.859f, 0.804f, 0.671f)),
                (24f, 74f, "house", new Color(0.749f, 0.678f, 0.588f)),
                // Third row
                (-35f, 56f, "house", new Color(0.831f, 0.769f, 0.686f)),
                (-35f, 80f, "house", new Color(0.757f, 0.706f, 0.647f)),
                (-35f, 92f, "house", new Color(0.867f, 0.816f, 0.741f)),
                (35f, 50f, "house", new Color(0.694f, 0.741f, 0.686f)),
                (35f, 74f, "house", new Color(0.792f, 0.749f, 0.596f)),
                (35f, 86f, "house", new Color(0.729f, 0.667f, 0.612f)),
                // North extension of rows 1 & 2
                (-13f, 90f, "house", new Color(0.847f, 0.788f, 0.639f)),
                (13f, 88f, "house", new Color(0.878f, 0.827f, 0.722f)),
                (-24f, 94f, "house", new Color(0.706f, 0.749f, 0.694f)),
                (24f, 90f, "house", new Color(0.859f, 0.804f, 0.671f)),
            };

            var mailboxPool = new[] { "mailbox", "mailbox red", "mailbox yellow" };
            int mailboxIdx = 0;
            foreach (var lot in lots)
            {
                BuildHouse(zone, lot.x, lot.z, lot.kind, lot.color);
                if (lot.kind == "house")
                {
                    string mbVariant = mailboxPool[mailboxIdx++ % mailboxPool.Length];
                    var mbPrefab = LoadPrefab(kHousePrefabDir + mbVariant + ".prefab");
                    var mbPos = new Vector3(lot.x + 1.6f, 0f, lot.z + 3.5f);
                    if (InstantiateAssetPrefab(zone, mbPrefab, "Mailbox_" + mbVariant, mbPos) == null)
                        CreateBox(zone, "Mailbox", mbPos + new Vector3(0f, 0.55f, 0f), new Vector3(0.15f, 0.25f, 0.35f), new Color(0.227f, 0.353f, 0.478f));
                }
                // One bush/flower clump per yard, tucked in the corner opposite the mailbox so the
                // two never overlap. Purely decorative (see ScatterNaturePrefab) — this is most of
                // what makes the block feel lived-in/full rather than just a row of empty lots.
                ScatterNaturePrefab(zone, kYardPool, new Vector3(lot.x - 2.2f, 0f, lot.z - 2.8f));
                // A single fence accent in the corner opposite both the mailbox and the yard
                // greenery — not a continuous fence run (the real module's exact width isn't known
                // without the Editor, so tiling a full property line risked visible gaps), just a
                // detail piece framing the yard.
                var fencePrefab = LoadPrefab(kHousePrefabDir + "fence_0" + ((mailboxIdx % 4) + 1) + ".prefab");
                InstantiateAssetPrefab(zone, fencePrefab, "FenceAccent", new Vector3(lot.x + 2.4f, 0f, lot.z - 3.0f), new Vector3(0f, lot.x < 0f ? 0f : 180f, 0f));
            }

            Color[] leafColors = { new Color(0.294f, 0.431f, 0.294f), new Color(0.361f, 0.541f, 0.290f), new Color(0.42f, 0.58f, 0.31f) };
            // Original 10 spots unchanged, plus 3 more filling gaps opened up by the third house
            // row and north extension — same programmatic clearance-check as the lots above.
            var treeSpots = new[] {
                new Vector2(-19f, 60f), new Vector2(19f, 64f), new Vector2(-8f, 78f), new Vector2(8f, 50f), new Vector2(-18f, 44f), new Vector2(17f, 40f),
                new Vector2(-31f, 72f), new Vector2(31f, 66f), new Vector2(-30f, 50f), new Vector2(0f, 84f),
                new Vector2(-40f, 64f), new Vector2(40f, 60f), new Vector2(24f, 44f)
            };
            for (int i = 0; i < treeSpots.Length; i++)
            {
                var t = new GameObject("Tree").transform;
                t.SetParent(zone, false);
                t.localPosition = new Vector3(treeSpots[i].x, 0f, treeSpots[i].y);

                // SimpleNaturePack has 5 tree variants; these spots are spaced 10m+ apart so a
                // real model being a bit larger/smaller than the old 2.6m-tall primitive tree
                // won't visibly clash with anything, which is why this swap (unlike tiles) was
                // low-risk enough to make without seeing it in the Editor first.
                var treePrefab = LoadPrefab(kNaturePrefabDir + "Tree_0" + ((i % 5) + 1) + ".prefab");
                var instance = InstantiateAssetPrefab(t, treePrefab, "TreeModel", Vector3.zero);
                if (instance == null)
                {
                    CreateCylinder(t, "Trunk", new Vector3(0f, 1.2f, 0f), 0.3f, 2.4f, new Color(0.42f, 0.29f, 0.19f));
                    CreateSphere(t, "Canopy", new Vector3(0f, 2.8f, 0f), 1.3f, leafColors[i % 3]);
                }
            }

            // Low ground-clutter (grass tufts/mushrooms) along both sidewalk verges, extended to
            // z=90 to match the block's new northern extent (sidewalks sit at x=±5.2 with 2.2m
            // width, so ±7.8 is clear of them) — was otherwise bare the whole length of the street.
            var clutterParent = new GameObject("SidewalkClutter").transform;
            clutterParent.SetParent(zone, false);
            for (float z = 44f; z <= 90f; z += 8f)
                foreach (var x in new[] { -7.8f, 7.8f })
                    ScatterNaturePrefab(clutterParent, kGroundClutterPool, new Vector3(x, 0f, z));

            // Looser open-lawn scatter (bush/rock/flower/mushroom/stump) filling the wider gaps
            // between the three house rows and around the new north end — the same
            // clearance-checked-in-Python positions as the lots/trees above, just with a smaller
            // footprint requirement since these are small props, not houses or full trees.
            var detailPool = new[] { "Bush_01", "Bush_02", "Bush_03", "Rock_01", "Rock_02", "Rock_03", "Rock_04", "Rock_05", "Flowers_01", "Flowers_02", "Mushroom_01", "Mushroom_02", "Stump_01" };
            var detailSpots = new[] {
                new Vector2(-40f, 82f), new Vector2(40f, 78f), new Vector2(35f, 94f), new Vector2(-24f, 50f),
                new Vector2(-19f, 86f), new Vector2(19f, 82f), new Vector2(-28f, 60f), new Vector2(-35f, 64f), new Vector2(35f, 58f)
            };
            var detailParent = new GameObject("OpenLawnDetail").transform;
            detailParent.SetParent(zone, false);
            foreach (var spot in detailSpots)
                ScatterNaturePrefab(detailParent, detailPool, new Vector3(spot.x, 0f, spot.y));

            BuildPlayground(zone, -10f, 44f);
            CreateGroundPlane(zone, "PlaygroundSand", new Vector3(-10f, 0.015f, 44f), 8f, 8f, new Color(0.788f, 0.690f, 0.471f));
            BuildCommunityGarden(zone, 12f, 44f);

            BuildCrosswalk(zone, 58f);
            BuildBusStop(zone, 6f, 58f);
            BuildInfoSign(zone, "BusStopPoster", 4.7f, 58f, 90f, "Public Transit\nExpansion Survey", CivicExhibitTrigger.SourceType.EnvironmentalBoard, "bus_stop_transit_survey", new Color(0.863f, 0.906f, 0.933f));
            BuildBikeRack(zone, -9f, 46f);
            BuildInfoSign(zone, "BulletinBoard", -10f, 40f, 20f, "Neighborhood\nBulletin Board", CivicExhibitTrigger.SourceType.EnvironmentalBoard, "bulletin_board_upcoming", new Color(0.937f, 0.902f, 0.816f));

            // Sidewalk furniture along both sides of the residential stretch of Main Street.
            // Z-ranges/intervals were picked to sidestep the InfoSign boards already on this block
            // (BusStopPoster at x=4.7,z=58 and CanvassingSignupBoard at x=4.7,z=76 — both close to
            // the east sidewalk band at x=5.2) rather than checked pointwise, since these are small
            // props (<=1.4m) with several meters of clearance at every step below.
            BuildStreetFurnitureRun(zone, -5.2f, 44f, 90f, 7f, facingInward: true);
            BuildStreetFurnitureRun(zone, 5.2f, 44f, 90f, 9f, facingInward: false);
            // Ties the neighborhood back to the project's actual stated purpose —
            // see the ExhibitIds comment above and canvasser_01 below.
            BuildInfoSign(zone, "CanvassingSignupBoard", 4.7f, 76f, 90f, "Join the\nOff-Season Canvass", CivicExhibitTrigger.SourceType.EnvironmentalBoard, "canvassing_volunteer_signup", new Color(0.902f, 0.859f, 0.796f));

            _residentialSpawns.Add(MakeSpawnPoint(zone, "parent_01", -5f, 62f, 100f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "neighbor_01", -6.6f, 61.6f, 260f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "cyclist_01", 4f, 52f, 200f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "dog_walker_01", -3f, 46f, 30f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "mail_carrier_01", 0f, 70f, 90f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "new_resident_01", -17f, 60f, 45f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "park_volunteer_01", -9f, 41f, 0f));
            _residentialSpawns.Add(MakeSpawnPoint(zone, "jogger_01", 8f, 66f, 180f));
            // The one NPC in town who exists specifically to voice this project's
            // actual concept (see NPCRoster.json / Conversations.json additions).
            _residentialSpawns.Add(MakeSpawnPoint(zone, "canvasser_01", 5f, 78f, 300f));
        }

        /// <summary>"is it possible to add trees surrounding the perimiter of the neighbourhood" —
        /// a tree line ringing the outer edge of the residential block built above, left open at
        /// the south end where Main Street actually enters the block (the playground, community
        /// garden, and bulletin board already occupy that z=40-46 stretch — see the end of
        /// BuildResidential) so this doesn't wall off the road. Same clearance-checked-placement
        /// discipline as BuildResidential's own lots/treeSpots/detailSpots arrays: every candidate
        /// ring point is checked against ALL of those existing coordinates (plus every other
        /// perimeter tree already placed) before anything is instantiated, so the now-wide block
        /// (houses run out to x=+/-35, z up to 94) can't silently swallow a spot into an overlap.</summary>
        private static void BuildNeighborhoodPerimeterTrees(Transform root)
        {
            var zone = new GameObject("NeighborhoodPerimeterTrees").transform;
            zone.SetParent(root, false);

            // Every coordinate BuildResidential placed something at, paired with a clearance
            // radius roughly matching that object's real footprint plus margin — houses/duplexes/
            // apartments are the biggest (~5-6m across), existing trees are mid-sized, small
            // props/signs/furniture are the smallest. The perimeter ring below sits well outside
            // all of these (x=+/-47 vs. the outermost lots at x=+/-35), but every point is still
            // checked rather than assumed clear, matching this file's established discipline.
            var occupied = new List<(float x, float z, float r)>
            {
                // Lots (BuildHouse footprint + mailbox/yard-greenery/fence corner props)
                (-13f, 66f, 6f), (-13f, 76f, 6f), (13f, 70f, 6f), (13f, 56f, 6f), (-13f, 54f, 6f),
                (-24f, 68f, 6f), (-24f, 82f, 6f), (24f, 58f, 6f), (24f, 74f, 6f),
                (-35f, 56f, 6f), (-35f, 80f, 6f), (-35f, 92f, 6f), (35f, 50f, 6f), (35f, 74f, 6f), (35f, 86f, 6f),
                (-13f, 90f, 6f), (13f, 88f, 6f), (-24f, 94f, 6f), (24f, 90f, 6f),
                // Existing street trees
                (-19f, 60f, 3.5f), (19f, 64f, 3.5f), (-8f, 78f, 3.5f), (8f, 50f, 3.5f), (-18f, 44f, 3.5f), (17f, 40f, 3.5f),
                (-31f, 72f, 3.5f), (31f, 66f, 3.5f), (-30f, 50f, 3.5f), (0f, 84f, 3.5f),
                (-40f, 64f, 3.5f), (40f, 60f, 3.5f), (24f, 44f, 3.5f),
                // Open-lawn detail clumps
                (-40f, 82f, 3f), (40f, 78f, 3f), (35f, 94f, 3f), (-24f, 50f, 3f),
                (-19f, 86f, 3f), (19f, 82f, 3f), (-28f, 60f, 3f), (-35f, 64f, 3f), (35f, 58f, 3f),
                // Playground / community garden / bulletin board / bus stop / bike rack — the busy
                // south stretch where Main Street enters the block
                (-10f, 44f, 5f), (12f, 44f, 5f), (6f, 58f, 3f), (-9f, 46f, 3f), (-10f, 40f, 3f), (4.7f, 76f, 3f)
            };

            const float treeClearance = 4f; // min extra gap beyond an occupied point's own radius
            const float treeSpacing = 4.8f; // dense woodland spacing; canopy overlap makes a natural tree line
            const float innerBeltSpacing = 6f;
            const float roadHalfWidth = 8.5f; // keep the south cap clear of the Main Street corridor
            const float xEdge = 47f;
            const float zSouth = -64f;
            const float zNorth = 103f; // 9m past the northernmost lot (z=94) — the block dead-ends here

            var placed = new List<Vector2>();
            int leafIdx = 0;

            void TryPlace(float x, float z)
            {
                foreach (var o in occupied)
                {
                    float dx = x - o.x, dz = z - o.z;
                    if (dx * dx + dz * dz < (o.r + treeClearance) * (o.r + treeClearance)) return;
                }
                foreach (var p in placed)
                {
                    float dx = x - p.x, dz = z - p.y;
                    if (dx * dx + dz * dz < treeSpacing * treeSpacing) return;
                }

                placed.Add(new Vector2(x, z));
                var t = new GameObject("PerimeterTree").transform;
                t.SetParent(zone, false);
                t.localPosition = new Vector3(x, 0f, z);

                var treePrefab = LoadPrefab(kNaturePrefabDir + "Tree_0" + ((leafIdx % 5) + 1) + ".prefab");
                leafIdx++;
                var instance = InstantiateAssetPrefab(t, treePrefab, "TreeModel", Vector3.zero);
                if (instance == null)
                {
                    Color[] leafColors = { new Color(0.294f, 0.431f, 0.294f), new Color(0.361f, 0.541f, 0.290f), new Color(0.42f, 0.58f, 0.31f) };
                    CreateCylinder(t, "Trunk", new Vector3(0f, 1.2f, 0f), 0.3f, 2.4f, new Color(0.42f, 0.29f, 0.19f));
                    CreateSphere(t, "Canopy", new Vector3(0f, 2.8f, 0f), 1.3f, leafColors[leafIdx % 3]);
                }
            }

            // West & east edges — full north/south run of the block.
            for (float z = zSouth; z <= zNorth; z += treeSpacing)
            {
                TryPlace(-xEdge, z);
                TryPlace(xEdge, z);
            }

            // North cap — the block dead-ends here, no road to leave clear.
            for (float x = -xEdge; x <= xEdge; x += treeSpacing)
                TryPlace(x, zNorth);

            // South cap — Main Street actually enters here, so leave the road corridor open and
            // only flank it either side.
            for (float x = -xEdge; x <= xEdge; x += treeSpacing)
                if (Mathf.Abs(x) > roadHalfWidth)
                    TryPlace(x, zSouth);

            // A staggered second belt fills the broad open strip just inside the perimeter.
            // TryPlace checks every home, street tree, busy public space, and earlier tree so
            // the added canopy stays in available lawn instead of blocking paths or buildings.
            for (float z = zSouth + 3f; z <= zNorth - 3f; z += innerBeltSpacing)
            {
                TryPlace(-42f, z + 2.5f);
                TryPlace(42f, z + 2.5f);
            }

            for (float x = -42f; x <= 42f; x += innerBeltSpacing)
            {
                float staggeredX = x + 3f;
                TryPlace(staggeredX, zNorth - 5f);
                if (Mathf.Abs(staggeredX) > roadHalfWidth)
                    TryPlace(staggeredX, zSouth + 5f);
            }
        }

        // ==============================================================
        private static void BuildWorldBoundaries(Transform root)
        {
            // Invisible collision barriers keep the player inside the finished town while
            // leaving the full street, City Hall plaza, and forest ring available to explore.
            const float sideX = 55f;
            const float southZ = -70f;
            const float northZ = 109f;
            const float height = 8f;
            const float thickness = 0.6f;
            float spanZ = northZ - southZ;
            float centerZ = (northZ + southZ) * 0.5f;

            var west = CreateBox(root, "WorldBoundary_West", new Vector3(-sideX, height * 0.5f, centerZ), new Vector3(thickness, height, spanZ), Palette.CharcoalTrim, markStatic: false);
            var east = CreateBox(root, "WorldBoundary_East", new Vector3(sideX, height * 0.5f, centerZ), new Vector3(thickness, height, spanZ), Palette.CharcoalTrim, markStatic: false);
            var south = CreateBox(root, "WorldBoundary_South", new Vector3(0f, height * 0.5f, southZ), new Vector3(sideX * 2f + thickness, height, thickness), Palette.CharcoalTrim, markStatic: false);
            var north = CreateBox(root, "WorldBoundary_North", new Vector3(0f, height * 0.5f, northZ), new Vector3(sideX * 2f + thickness, height, thickness), Palette.CharcoalTrim, markStatic: false);
            west.GetComponent<Renderer>().enabled = false;
            east.GetComponent<Renderer>().enabled = false;
            south.GetComponent<Renderer>().enabled = false;
            north.GetComponent<Renderer>().enabled = false;
        }

        // Commercial zone (Main Street)
        // ==============================================================
        private struct ShopDef { public string Name, BoardId, Kind; public float X, Z; public Color Color; }

        /// <summary>Awning above the storefront windows — a single slanted panel plus a thin white
        /// valance stripe along its lower edge (the classic two-tone fabric-awning look), instead of
        /// the old bare Trim band. Distinguishes storefronts from houses/public buildings at a glance
        /// even before reading a sign, which the flat Trim-band-only version didn't.</summary>
        private static void BuildAwning(Transform shop, Color color)
        {
            CreateBox(shop, "Awning", new Vector3(0f, 2.55f, 1.85f), new Vector3(3.9f, 0.35f, 1.1f), color, new Vector3(22f, 0f, 0f));
            CreateBox(shop, "AwningValance", new Vector3(0f, 2.1f, 2.35f), new Vector3(3.9f, 0.18f, 0.06f), Palette.SoftWhite);
        }

        /// <summary>Small distinguishing prop cluster on the sidewalk/wall out front, unique per shop
        /// Kind, so nine storefronts read as nine different businesses rather than nine identical
        /// boxes with different-colored trim and a text label. All primitive geometry (see class-level
        /// comment on why this scaffold avoids hand-authored meshes) — none of these need a real
        /// asset, they're built from the same CreateBox/CreateCylinder/CreateSphere vocabulary as
        /// everything else here.</summary>
        private static void BuildShopIdentityProp(Transform shop, string kind, Color accent)
        {
            switch (kind)
            {
                case "coffee":
                    // Round sidewalk table + two stools.
                    var tableAnchor = new GameObject("CafeTable").transform;
                    tableAnchor.SetParent(shop, false);
                    tableAnchor.localPosition = new Vector3(1.6f, 0f, 2.6f);
                    CreateCylinder(tableAnchor, "Pole", new Vector3(0f, 0.35f, 0f), 0.05f, 0.7f, Palette.CharcoalTrim);
                    CreateCylinder(tableAnchor, "Top", new Vector3(0f, 0.72f, 0f), 0.35f, 0.05f, Palette.SoftWhite);
                    CreateCylinder(tableAnchor, "StoolA", new Vector3(0.55f, 0.25f, 0.2f), 0.16f, 0.5f, accent);
                    CreateCylinder(tableAnchor, "StoolB", new Vector3(-0.2f, 0.25f, 0.55f), 0.16f, 0.5f, accent);
                    break;

                case "grocery":
                    // Wooden crate of produce by the entrance.
                    var crate = new GameObject("ProduceCrate").transform;
                    crate.SetParent(shop, false);
                    crate.localPosition = new Vector3(-1.6f, 0f, 2.4f);
                    CreateBox(crate, "Crate", new Vector3(0f, 0.25f, 0f), new Vector3(0.7f, 0.5f, 0.5f), new Color(0.541f, 0.42f, 0.271f));
                    var produceColors = new[] { Palette.Brick, Palette.MustardGold, Palette.ForestGreen };
                    for (int i = 0; i < 5; i++)
                        CreateSphere(crate, "Produce", new Vector3((i % 3 - 1) * 0.2f, 0.55f, (i / 3) * 0.2f - 0.05f), 0.13f, produceColors[i % produceColors.Length]);
                    break;

                case "pharmacy":
                    // Green cross above the entrance.
                    var cross = new GameObject("CrossSign").transform;
                    cross.SetParent(shop, false);
                    cross.localPosition = new Vector3(0f, 3.35f, 1.85f);
                    CreateBox(cross, "Vertical", Vector3.zero, new Vector3(0.22f, 0.7f, 0.1f), Palette.ForestGreen);
                    CreateBox(cross, "Horizontal", Vector3.zero, new Vector3(0.7f, 0.22f, 0.1f), Palette.ForestGreen);
                    break;

                case "barber":
                    // Classic barber pole — three stacked color bands.
                    var pole = new GameObject("BarberPole").transform;
                    pole.SetParent(shop, false);
                    pole.localPosition = new Vector3(-1.9f, 0f, 1.9f);
                    CreateCylinder(pole, "Base", new Vector3(0f, 0.6f, 0f), 0.09f, 1.2f, Palette.SoftWhite);
                    CreateCylinder(pole, "BandRed", new Vector3(0f, 1.0f, 0f), 0.095f, 0.15f, Palette.Brick);
                    CreateCylinder(pole, "BandBlue", new Vector3(0f, 0.8f, 0f), 0.095f, 0.15f, Palette.DenimBlue);
                    CreateSphere(pole, "Cap", new Vector3(0f, 1.22f, 0f), 0.1f, Palette.SoftWhite);
                    break;

                case "laundromat":
                    // A row of washer drums along the front wall.
                    var washers = new GameObject("Washers").transform;
                    washers.SetParent(shop, false);
                    washers.localPosition = new Vector3(0f, 0f, 1.85f);
                    for (int i = 0; i < 3; i++)
                    {
                        float wx = (i - 1) * 0.9f;
                        CreateBox(washers, "Unit", new Vector3(wx, 0.45f, 0f), new Vector3(0.7f, 0.9f, 0.15f), Palette.SoftWhite);
                        CreateCylinder(washers, "Porthole", new Vector3(wx, 0.5f, 0.08f), 0.22f, 0.05f, Palette.CharcoalTrim, new Vector3(90f, 0f, 0f));
                    }
                    break;

                case "restaurant":
                    // Bistro table with an umbrella.
                    var bistro = new GameObject("BistroTable").transform;
                    bistro.SetParent(shop, false);
                    bistro.localPosition = new Vector3(1.7f, 0f, 2.5f);
                    CreateCylinder(bistro, "Pole", new Vector3(0f, 1.1f, 0f), 0.04f, 2.2f, Palette.CharcoalTrim);
                    CreateCylinder(bistro, "TableTop", new Vector3(0f, 0.7f, 0f), 0.32f, 0.05f, Palette.SoftWhite);
                    CreateCylinder(bistro, "Umbrella", new Vector3(0f, 2.05f, 0f), 0.9f, 0.1f, accent);
                    break;

                case "bookstore":
                    // Sidewalk book cart with a few angled "spines."
                    var cart = new GameObject("BookCart").transform;
                    cart.SetParent(shop, false);
                    cart.localPosition = new Vector3(-1.6f, 0f, 2.5f);
                    CreateBox(cart, "CartBox", new Vector3(0f, 0.35f, 0f), new Vector3(0.9f, 0.5f, 0.5f), Palette.CharcoalTrim);
                    var bookColors = new[] { Palette.Brick, Palette.DenimBlue, Palette.ForestGreen, Palette.MustardGold };
                    for (int i = 0; i < 4; i++)
                        CreateBox(cart, "Book", new Vector3(i * 0.18f - 0.27f, 0.68f, 0f), new Vector3(0.14f, 0.28f, 0.4f), bookColors[i], new Vector3(0f, 0f, i % 2 == 0 ? 8f : -8f));
                    break;

                case "bakery":
                    // Basket of loaves out front.
                    var basket = new GameObject("BreadBasket").transform;
                    basket.SetParent(shop, false);
                    basket.localPosition = new Vector3(1.6f, 0f, 2.3f);
                    CreateCylinder(basket, "Basket", new Vector3(0f, 0.2f, 0f), 0.35f, 0.4f, new Color(0.541f, 0.42f, 0.271f));
                    var loafColors = new[] { Palette.MustardGold, Palette.BurntOrange };
                    for (int i = 0; i < 3; i++)
                        CreateCylinder(basket, "Loaf", new Vector3((i - 1) * 0.16f, 0.45f, 0f), 0.09f, 0.32f, loafColors[i % 2], new Vector3(0f, 0f, 90f));
                    break;

                case "flower":
                    // A row of flower buckets — reuses the same real nature-pack flowers as
                    // residential yards, just clustered densely for a "flower shop" feel.
                    var buckets = new GameObject("FlowerBuckets").transform;
                    buckets.SetParent(shop, false);
                    buckets.localPosition = new Vector3(0f, 0f, 2.2f);
                    for (int i = 0; i < 3; i++)
                    {
                        float bx = (i - 1) * 0.8f;
                        CreateCylinder(buckets, "Bucket", new Vector3(bx, 0.18f, 0f), 0.22f, 0.36f, accent);
                        ScatterNaturePrefab(buckets, kYardPool, new Vector3(bx, 0.38f, 0f));
                    }
                    break;
            }
        }

        private static void BuildCommercial(Transform root)
        {
            var zone = new GameObject("Commercial").transform;
            zone.SetParent(root, false);

            var shops = new[]
            {
                new ShopDef { Name = "Coffee Shop", BoardId = "sign_coffee_shop", Kind = "coffee", X = -10f, Z = 32f, Color = Palette.BurntOrange },
                new ShopDef { Name = "Grocery Store", BoardId = "sign_grocery_store", Kind = "grocery", X = -10f, Z = 24f, Color = Palette.ForestGreen },
                new ShopDef { Name = "Pharmacy", BoardId = "sign_pharmacy", Kind = "pharmacy", X = -10f, Z = 16f, Color = Palette.DenimBlue },
                new ShopDef { Name = "Barber Shop", BoardId = "sign_barber_shop", Kind = "barber", X = -10f, Z = 8f, Color = Palette.Brick },
                new ShopDef { Name = "Laundromat", BoardId = "sign_laundromat", Kind = "laundromat", X = -10f, Z = 0f, Color = Palette.SlateBlueGray },
                new ShopDef { Name = "Restaurant", BoardId = "sign_restaurant", Kind = "restaurant", X = 10f, Z = 30f, Color = Palette.BurntOrange },
                new ShopDef { Name = "Bookstore", BoardId = "sign_bookstore", Kind = "bookstore", X = 10f, Z = 22f, Color = Palette.Plum },
                new ShopDef { Name = "Bakery", BoardId = "sign_bakery", Kind = "bakery", X = 10f, Z = 14f, Color = Palette.MustardGold },
                new ShopDef { Name = "Flower Shop", BoardId = "sign_flower_shop", Kind = "flower", X = 10f, Z = 6f, Color = Palette.DustyRose },
            };

            foreach (var shop in shops)
            {
                float facing = shop.X < 0f ? 0f : 180f;
                var s = new GameObject("Shop_" + shop.Name.Replace(" ", "")).transform;
                s.SetParent(zone, false);
                s.localPosition = new Vector3(shop.X, 0f, shop.Z);
                s.localRotation = Quaternion.Euler(0f, facing, 0f);

                CreateBox(s, "Body", new Vector3(0f, 1.6f, 0f), new Vector3(3.6f, 3.2f, 3.4f), Palette.CreamStucco);
                CreateBox(s, "Trim", new Vector3(0f, 3.3f, 0f), new Vector3(3.9f, 0.4f, 3.6f), shop.Color);
                CreateBox(s, "WindowL", new Vector3(-1f, 1.3f, 1.72f), new Vector3(1f, 1.4f, 0.05f), new Color(0.737f, 0.843f, 0.878f));
                CreateBox(s, "WindowR", new Vector3(1f, 1.3f, 1.72f), new Vector3(1f, 1.4f, 0.05f), new Color(0.737f, 0.843f, 0.878f));
                BuildAwning(s, shop.Color);
                BuildShopIdentityProp(s, shop.Kind, shop.Color);

                var signPost = new GameObject("SignAnchor").transform;
                signPost.SetParent(s, false);
                signPost.localPosition = new Vector3(0f, 2.7f, 1.75f);
                var sign = CreateBox(signPost, "Sign", Vector3.zero, new Vector3(3f, 0.6f, 0.06f), shop.Color);
                CreateLabel(sign.transform, shop.Name, new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.35f, Color.white, 2.8f);
                var trigger = sign.AddComponent<CivicExhibitTrigger>();
                trigger.sourceType = CivicExhibitTrigger.SourceType.EnvironmentalBoard;
                trigger.entryId = shop.BoardId;
                var prompt = sign.AddComponent<InteractionPrompt>();
                var icon = CreateSphere(sign.transform, "IconMarker", new Vector3(0f, 0.5f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
                Object.DestroyImmediate(icon.GetComponent<Collider>());
                icon.SetActive(false);
                prompt.iconRoot = icon.transform;
            }

            BuildCrosswalk(zone, 18f);

            // Sidewalk furniture along the commercial block, offset from the shop entrance props
            // above (z=0..34) and the z=18 crosswalk's own corner bulb-outs (different x band —
            // furniture sits at x=5.2/-5.2, bulb-outs at x=6.4/-6.4 — and the z=26 construction
            // barrier on the east side).
            BuildStreetFurnitureRun(zone, -5.2f, 2f, 34f, 8f, facingInward: true);
            BuildStreetFurnitureRun(zone, 5.2f, 2f, 34f, 8f, facingInward: false);

            var barrier = new GameObject("ConstructionBarrier").transform;
            barrier.SetParent(zone, false);
            barrier.localPosition = new Vector3(7f, 0f, 26f);
            CreateBox(barrier, "Panel", new Vector3(0f, 0.4f, 0f), new Vector3(1.2f, 0.8f, 0.15f), new Color(0.878f, 0.635f, 0.2f));
            CreateBox(barrier, "Stripe", new Vector3(0f, 0.4f, 0f), new Vector3(1.2f, 0.15f, 0.16f), new Color(0.137f, 0.141f, 0.122f));

            _commercialSpawns.Add(MakeSpawnPoint(zone, "business_owner_01", 8.5f, 28f, 200f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "customer_01", 7.2f, 27f, 20f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "student_01", 9f, 20f, 210f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "friend_01", 10.6f, 19.6f, 30f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "restaurant_owner_01", 11.5f, 31.5f, 190f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "delivery_rider_01", -8f, 12f, 90f));
            _commercialSpawns.Add(MakeSpawnPoint(zone, "street_musician_01", -5.5f, 18f, 0f));
        }

        // ==============================================================
        // Public buildings row
        // ==============================================================
        private struct PublicBuildingDef { public string Name, SignBoardId, PosterId, PosterTitle; public float X, Z; public Color Color; }

        private static void BuildPublicRow(Transform root)
        {
            var zone = new GameObject("PublicRow").transform;
            zone.SetParent(root, false);

            var buildings = new[]
            {
                new PublicBuildingDef { Name = "Public Library", SignBoardId = "sign_public_library", PosterId = "library_poster_local_gov", PosterTitle = "Learn How Local\nGovernment Works", X = -12f, Z = -6f, Color = new Color(0.294f, 0.431f, 0.294f) },
                new PublicBuildingDef { Name = "Police Station", SignBoardId = "sign_police_station", PosterId = null, PosterTitle = null, X = -12f, Z = -14f, Color = new Color(0.227f, 0.353f, 0.478f) },
                new PublicBuildingDef { Name = "Fire Department", SignBoardId = "sign_fire_department", PosterId = null, PosterTitle = null, X = -12f, Z = -22f, Color = new Color(0.788f, 0.216f, 0.216f) },
                new PublicBuildingDef { Name = "Community Center", SignBoardId = "sign_community_center", PosterId = "community_center_planning_workshop", PosterTitle = "Neighborhood\nPlanning Workshop", X = 12f, Z = -6f, Color = new Color(0.788f, 0.635f, 0.2f) },
                new PublicBuildingDef { Name = "Public School", SignBoardId = "sign_public_school", PosterId = null, PosterTitle = null, X = 12f, Z = -14f, Color = new Color(0.478f, 0.373f, 0.686f) },
                new PublicBuildingDef { Name = "Health Clinic", SignBoardId = "sign_health_clinic", PosterId = null, PosterTitle = null, X = 12f, Z = -22f, Color = new Color(0.176f, 0.541f, 0.541f) },
            };

            foreach (var b in buildings)
            {
                float facing = b.X < 0f ? 0f : 180f;
                var g = new GameObject("Public_" + b.Name.Replace(" ", "")).transform;
                g.SetParent(zone, false);
                g.localPosition = new Vector3(b.X, 0f, b.Z);
                g.localRotation = Quaternion.Euler(0f, facing, 0f);
                CreateBox(g, "Body", new Vector3(0f, 2f, 0f), new Vector3(4.4f, 4f, 4f), new Color(0.902f, 0.867f, 0.784f));
                CreateBox(g, "Trim", new Vector3(0f, 4.15f, 0f), new Vector3(4.7f, 0.3f, 4.3f), b.Color);

                var sign = CreateBox(g, "Sign", new Vector3(0f, 3.3f, 2.05f), new Vector3(3.6f, 0.5f, 0.06f), b.Color);
                CreateLabel(sign.transform, b.Name, new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.3f, Color.white, 3.4f);
                var trigger = sign.AddComponent<CivicExhibitTrigger>();
                trigger.sourceType = CivicExhibitTrigger.SourceType.EnvironmentalBoard;
                trigger.entryId = b.SignBoardId;
                var prompt = sign.AddComponent<InteractionPrompt>();
                var icon = CreateSphere(sign.transform, "IconMarker", new Vector3(0f, 0.4f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
                Object.DestroyImmediate(icon.GetComponent<Collider>());
                icon.SetActive(false);
                prompt.iconRoot = icon.transform;

                if (b.PosterId != null)
                {
                    float posterX = b.X + (b.X < 0f ? 2.6f : -2.6f);
                    float posterRot = b.X < 0f ? 90f : -90f;
                    BuildInfoSign(zone, b.Name.Replace(" ", "") + "_Poster", posterX, b.Z, posterRot, b.PosterTitle, CivicExhibitTrigger.SourceType.EnvironmentalBoard, b.PosterId, new Color(0.863f, 0.906f, 0.933f));
                }
            }

            var ts = new GameObject("TransitStation").transform;
            ts.SetParent(zone, false);
            ts.localPosition = new Vector3(11f, 0f, -30f);
            CreateBox(ts, "Body", new Vector3(0f, 1.3f, 0f), new Vector3(3f, 2.6f, 2.4f), new Color(0.361f, 0.384f, 0.4f));
            var tsSign = CreateBox(ts, "Sign", new Vector3(0f, 2.2f, 1.25f), new Vector3(2.6f, 0.5f, 0.06f), new Color(0.176f, 0.29f, 0.369f));
            CreateLabel(tsSign.transform, "Transit Station", new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.3f, Color.white, 2.4f);
            var tsTrigger = tsSign.AddComponent<CivicExhibitTrigger>();
            tsTrigger.sourceType = CivicExhibitTrigger.SourceType.EnvironmentalBoard;
            tsTrigger.entryId = "sign_transit_station";
            var tsPrompt = tsSign.AddComponent<InteractionPrompt>();
            var tsIcon = CreateSphere(tsSign.transform, "IconMarker", new Vector3(0f, 0.4f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
            Object.DestroyImmediate(tsIcon.GetComponent<Collider>());
            tsIcon.SetActive(false);
            tsPrompt.iconRoot = tsIcon.transform;

            _publicRowSpawns.Add(MakeSpawnPoint(zone, "transit_rider_01", 9f, -28f, 90f));
            _publicRowSpawns.Add(MakeSpawnPoint(zone, "young_parent_01", -10f, -3f, 100f));
            _publicRowSpawns.Add(MakeSpawnPoint(zone, "maintenance_worker_01", 10f, -18f, 250f));
            _publicRowSpawns.Add(MakeSpawnPoint(zone, "engineer_01", 10f, -22.5f, 90f));
            _publicRowSpawns.Add(MakeSpawnPoint(zone, "passerby_01", 11f, -21.5f, 270f));
            _publicRowSpawns.Add(MakeSpawnPoint(zone, "library_employee_01", -11.5f, -7.5f, 0f));
        }

        // ==============================================================
        // City Hall plaza (exterior)
        // ==============================================================
        private static void BuildCityHallExterior(Transform root)
        {
            var zone = new GameObject("CityHallPlaza").transform;
            zone.SetParent(root, false);

            BuildCrosswalk(zone, -34f);

            var hall = new GameObject("CityHallBuilding").transform;
            hall.SetParent(zone, false);
            hall.localPosition = new Vector3(0f, 0f, -50f);

            // The Neighbourhood Pack has no dedicated civic/town-hall model — only
            // single-family house_01..house_05 — so house_05 (reserved exclusively
            // for this building; see the exclusion note in BuildHouse()'s variant
            // cycling) stands in as the core structure, scaled up and dressed with
            // procedural civic elements (portico columns, pediment, sign) below so
            // it reads as a landmark rather than an oversized copy of a house down
            // the street. Real bounds are measured from the instantiated model
            // itself (same technique BuildCitizenBase uses for headHeight) rather
            // than guessed, so the add-on elements land correctly regardless of the
            // model's actual real-world size, which isn't knowable without the
            // Editor. Falls back to the original procedural box massing if
            // house_05.prefab isn't found.
            var hallPrefab = LoadPrefab(kHousePrefabDir + "house_05.prefab");
            var hallInstance = InstantiateAssetPrefab(hall, hallPrefab, "HallModel", Vector3.zero);

            Bounds hallBounds;
            if (hallInstance != null)
            {
                hallInstance.transform.localScale = Vector3.one * 1.85f;

                // BUG FOUND 2026-07-22 (real root cause of "still not allowing me to enter the town
                // hall" after the TriggerVolume/EntranceGlass/Rigidbody/Threshold fixes below):
                // InstantiateAssetPrefab auto-adds a SOLID BoxCollider sized to a prefab's full
                // combined renderer bounds whenever the source has no collider of its own — true for
                // every prefab in both asset packs, including house_05 (see that method's doc
                // comment). That box covers the model's entire footprint, front face included, at
                // whatever real depth the scaled model turns out to have — not knowable without the
                // Editor, and near-certainly NOT aligned with the archway's fixed world position
                // (z=-46.5, originally tuned to the old procedural fallback box's 8m depth). Even
                // with every doorway-specific collider fixed, this auto-generated whole-body box —
                // never touched by any of those fixes because it isn't part of the doorway at all —
                // was very likely still standing solid at or behind the archway. City Hall already
                // has its own purpose-built, correctly-positioned walkable doorway (TriggerVolume/
                // Threshold below), so the real model only needs to be visual dressing here, same
                // reasoning already applied to EntranceGlass a few lines down.
                foreach (var hallCollider in hallInstance.GetComponentsInChildren<Collider>())
                    Object.DestroyImmediate(hallCollider);

                // BUG FOUND 2026-07-22 (candidate cause of "large floating platform above the town
                // hall"): GetComponentsInChildren<Renderer> includes renderers on active-but-disabled
                // components too (a hidden LOD mesh, an interior-only proxy, etc. — common in asset
                // packs). A single such outlier with an inflated or oddly-placed bounds can blow up
                // this Encapsulate loop's result, which is exactly what roofY/frontZ/localCenter below
                // are built from — so the Trim cornice band (a large flat slab sized to the building's
                // footprint) would get placed at whatever wrong height that produced, i.e. a "large
                // floating platform." Filtering to enabled renderers only keeps this measurement to
                // what's actually visibly part of the model.
                var hallRenderers = hallInstance.GetComponentsInChildren<Renderer>();
                var enabledHallRenderers = System.Array.FindAll(hallRenderers, r => r.enabled);
                if (enabledHallRenderers.Length == 0) enabledHallRenderers = hallRenderers; // don't end up with zero geometry to measure
                hallBounds = enabledHallRenderers.Length > 0
                    ? enabledHallRenderers[0].bounds
                    : new Bounds(hall.position + Vector3.up * 3f, new Vector3(12f, 6f, 8f));
                foreach (var r in enabledHallRenderers) hallBounds.Encapsulate(r.bounds);
            }
            else
            {
                CreateBox(hall, "Body", new Vector3(0f, 3f, 0f), new Vector3(12f, 6f, 8f), new Color(0.910f, 0.894f, 0.847f));
                hallBounds = new Bounds(hall.TransformPoint(new Vector3(0f, 3f, 0f)), new Vector3(12f, 6f, 8f));
            }

            // Everything below is positioned from hallBounds (world space) converted
            // to a local offset under `hall`, so it lands correctly whether hall is
            // the real scaled model above or the procedural fallback box. Front is
            // +Z locally (hall itself carries no rotation) — the same convention the
            // original primitive massing and the interior portal marker already use,
            // matching the direction the player actually approaches City Hall from.
            // KNOWN RISK: this guarantees the *dressing* (columns/sign/ramp/glass)
            // faces the plaza correctly; it does NOT guarantee the real house
            // mesh's own modeled front door/porch detail also faces +Z — that's the
            // model's own authored orientation, which can't be previewed without the
            // Editor. If City Hall's real building looks like it's facing away from
            // the plaza, rotate the "HallModel" child 180° on Y in the Inspector.
            Vector3 localCenter = hallBounds.center - hall.position;
            float halfWidth = hallBounds.extents.x;
            float roofY = hallBounds.max.y - hall.position.y;
            float frontZ = localCenter.z + hallBounds.extents.z;

            // Cornice trim tracks the real roofline, whatever height that turns out to be.
            CreateBox(hall, "Trim", new Vector3(localCenter.x, roofY + 0.2f, localCenter.z),
                new Vector3(halfWidth * 2f + 0.4f, 0.4f, hallBounds.size.z + 0.4f), new Color(0.176f, 0.29f, 0.369f));

            // Give the civic landmark a clearly readable, full-footprint pitched roof.
            // The two thick roof planes overlap at the ridge and extend beyond the
            // measured walls to form eaves, so this remains reliable for both the
            // imported house prefab and the procedural fallback.
            float roofHalfDepth = hallBounds.extents.z + 0.75f;
            float roofRise = Mathf.Clamp(roofHalfDepth * 0.35f, 1.25f, 2.1f);
            float roofSlopeLength = Mathf.Sqrt(roofHalfDepth * roofHalfDepth + roofRise * roofRise);
            float roofSlopeAngle = Mathf.Atan2(roofRise, roofHalfDepth) * Mathf.Rad2Deg;
            float roofWidth = halfWidth * 2f + 1f;
            float roofCenterY = roofY + roofRise * 0.5f;
            float roofCenterOffsetZ = roofHalfDepth * 0.5f;
            var civicRoof = new Color(0.25f, 0.34f, 0.42f);
            var roofSize = new Vector3(roofWidth, 0.32f, roofSlopeLength + 0.12f);
            CreateBox(hall, "CivicRoof_FrontSlope",
                new Vector3(localCenter.x, roofCenterY, localCenter.z + roofCenterOffsetZ),
                roofSize, civicRoof, new Vector3(roofSlopeAngle, 0f, 0f));
            CreateBox(hall, "CivicRoof_BackSlope",
                new Vector3(localCenter.x, roofCenterY, localCenter.z - roofCenterOffsetZ),
                roofSize, civicRoof, new Vector3(-roofSlopeAngle, 0f, 0f));
            CreateBox(hall, "CivicRoof_Ridge",
                new Vector3(localCenter.x, roofY + roofRise, localCenter.z),
                new Vector3(roofWidth + 0.15f, 0.3f, 0.38f), new Color(0.18f, 0.25f, 0.32f));
            CreateBox(hall, "CivicRoof_FrontEave",
                new Vector3(localCenter.x, roofY + 0.08f, localCenter.z + roofHalfDepth),
                new Vector3(roofWidth + 0.25f, 0.24f, 0.2f), new Color(0.18f, 0.25f, 0.32f));
            CreateBox(hall, "CivicRoof_BackEave",
                new Vector3(localCenter.x, roofY + 0.08f, localCenter.z - roofHalfDepth),
                new Vector3(roofWidth + 0.25f, 0.24f, 0.2f), new Color(0.18f, 0.25f, 0.32f));

            // Portico: fixed, human/entrance-scaled civic dressing (deliberately
            // doesn't try to reach the real roofline, whatever height that turns out
            // to be) — two columns + a pediment in front of the entrance, the
            // cheapest way to make a reused residential model read as a civic
            // building without needing to touch its material/texture.
            const float porchY = 4.2f;
            CreateBox(hall, "Pediment", new Vector3(localCenter.x, porchY + 0.3f, frontZ + 0.3f),
                new Vector3(5.6f, 0.7f, 0.4f), new Color(0.910f, 0.894f, 0.847f));
            foreach (var cx in new[] { -2.2f, 2.2f })
                CreateCylinder(hall, "Column", new Vector3(localCenter.x + cx, porchY * 0.5f, frontZ + 0.3f),
                    0.22f, porchY, new Color(0.933f, 0.925f, 0.898f));

            // Solid color rather than true transparency: getting URP's Transparent
            // surface-type keywords/render-queue set correctly from script isn't
            // verifiable without a real Editor, so this stays opaque for now — swap
            // the material's Surface Type to Transparent in the Inspector for real glass.
            var entranceGlass = CreateBox(hall, "EntranceGlass", new Vector3(localCenter.x, 2.2f, frontZ + 0.05f),
                new Vector3(4f, 3.2f, 0.05f), new Color(0.737f, 0.843f, 0.878f));
            // BUG FOUND 2026-07-22: this is purely cosmetic "reads as glass" dressing, positioned at
            // the building's own measured frontZ — which, depending on the real house_05 prefab's
            // actual depth, can land within a meter or two of the separate "Portal_ToInterior"
            // archway at world z=-46.5. Left solid, it could physically block the player's approach
            // to the archway even after fixing that archway's own trigger collider (see WirePortals/
            // BuildInterior notes) — same "unable to enter the building" symptom, different object.
            // Since nothing is meant to walk through actual glass anyway, just drop the collider
            // entirely, same as the decorative IconMarker spheres elsewhere in this file.
            Object.DestroyImmediate(entranceGlass.GetComponent<Collider>());
            CreateLabel(hall, "CITY HALL", new Vector3(localCenter.x, porchY + 0.75f, frontZ + 0.5f),
                Vector3.zero, 0.6f, new Color(0.122f, 0.227f, 0.302f), 8f);
            CreateBox(hall, "ADARamp", new Vector3(localCenter.x - 3.5f, 0.25f, frontZ + 1.8f),
                new Vector3(3f, 0.15f, 2.5f), new Color(0.788f, 0.761f, 0.659f), new Vector3(0f, 0f, -6f));

            // 2026-07-22 "decorate the city hall": hung directly on the two existing columns
            // (reusing their proven-safe local x-offsets, just a bit further forward in Z) rather
            // than placing new standalone posts near the entrance — the ADARamp/EntranceGlass area
            // is already tightly packed and can't be re-checked visually, so new geometry there is
            // exactly where a collision risk would land. Bulb-named spheres are picked up
            // automatically by TimeOfDayManager.emissiveAtNight (see BuildPlayerRig, which scans
            // _townRoot for any renderer literally named "Bulb") — same mechanism as every other
            // streetlamp in town, so these glow warm at dusk for free with no extra wiring.
            var civicBlue = new Color(0.176f, 0.29f, 0.369f);
            var civicGold = new Color(0.796f, 0.635f, 0.208f);
            foreach (var cx in new[] { -2.2f, 2.2f })
            {
                CreateBox(hall, "EntranceBanner", new Vector3(localCenter.x + cx, 2.6f, frontZ + 0.42f),
                    new Vector3(0.55f, 1.5f, 0.03f), civicBlue);
                CreateBox(hall, "EntranceBannerTrim", new Vector3(localCenter.x + cx, 3.32f, frontZ + 0.43f),
                    new Vector3(0.57f, 0.08f, 0.04f), civicGold);
                CreateSphere(hall, "Bulb", new Vector3(localCenter.x + cx, 0.15f, frontZ + 0.35f), 0.12f, new Color(1f, 0.9f, 0.7f));
            }

            // 2026-07-23 "make city hall patriotic, and add pictures":
            //
            // Bunting along the cornice: alternating red/white/blue pennants. A thin box
            // rotated 45 degrees reads as a simple diamond/pennant shape — the same "stylized
            // stand-in" reasoning as the procedurally-generated avatar circles elsewhere in this
            // project, since a real triangular pennant mesh isn't one of Unity's built-in
            // primitives and can't be hand-authored/verified without the Editor. Count and span
            // are derived from the measured halfWidth so this scales with whatever the real
            // house_05 model's actual width turns out to be, same defensive approach the rest of
            // this method already uses for hallBounds-derived placement.
            var buntingColors = new[] { new Color(0.698f, 0.132f, 0.203f), Color.white, civicBlue };
            int buntingCount = 9;
            float buntingSpan = Mathf.Max(1f, halfWidth * 2f - 0.6f);
            for (int i = 0; i < buntingCount; i++)
            {
                float bx = localCenter.x - buntingSpan / 2f + buntingSpan * i / (buntingCount - 1);
                CreateBox(hall, "Bunting_" + i, new Vector3(bx, roofY - 0.05f, frontZ + 0.32f),
                    new Vector3(0.28f, 0.28f, 0.03f), buntingColors[i % 3], new Vector3(0f, 0f, 45f));
            }

            // Two framed "pictures" flanking the entrance — a procedurally-generated flag image
            // and a civic-seal image (see GetFlagPictureTexture/GetCivicSealTexture), standing in
            // for real photographic art the same way the procedural avatar portraits already
            // stand in for real portrait art. pictureOffsetX is clamped to halfWidth so the
            // frames always land on the building itself rather than floating past its edge on a
            // narrower-than-expected real model.
            var frameColor = new Color(0.361f, 0.247f, 0.165f);
            float pictureOffsetX = Mathf.Min(3.6f, Mathf.Max(0.9f, halfWidth - 0.9f));
            CreateBox(hall, "PictureFrame_Flag", new Vector3(localCenter.x - pictureOffsetX, 2.3f, frontZ + 0.05f),
                new Vector3(1.3f, 0.95f, 0.05f), frameColor);
            CreateTexturedBox(hall, "Picture_Flag", new Vector3(localCenter.x - pictureOffsetX, 2.3f, frontZ + 0.09f),
                new Vector3(1.1f, 0.75f, 0.02f), GetFlagPictureTexture());

            CreateBox(hall, "PictureFrame_Seal", new Vector3(localCenter.x + pictureOffsetX, 2.3f, frontZ + 0.05f),
                new Vector3(1.3f, 0.95f, 0.05f), frameColor);
            CreateTexturedBox(hall, "Picture_Seal", new Vector3(localCenter.x + pictureOffsetX, 2.3f, frontZ + 0.09f),
                new Vector3(1.1f, 0.75f, 0.02f), GetCivicSealTexture());

            var flag = new GameObject("FlagPole").transform;
            flag.SetParent(zone, false);
            flag.localPosition = new Vector3(5f, 0f, -45f);
            CreateCylinder(flag, "Pole", new Vector3(0f, 3f, 0f), 0.06f, 6f, new Color(0.541f, 0.541f, 0.541f));
            // 2026-07-23: swapped from a flat civic-blue box to the same procedural flag texture
            // used on the entrance picture panel, so the pole actually flies a flag now instead of
            // a plain colored rectangle.
            CreateTexturedBox(flag, "Banner", new Vector3(0.6f, 5.4f, 0f), new Vector3(1.2f, 0.7f, 0.02f), GetFlagPictureTexture());

            foreach (var p in new[] { new Vector2(-6f, -46f), new Vector2(6f, -46f) })
                CreateBox(zone, "Bench", new Vector3(p.x, 0.35f, p.y), new Vector3(1.4f, 0.1f, 0.5f), new Color(0.478f, 0.357f, 0.235f));

            // 2026-07-22 "decorate the city hall": added two more flower beds near the plaza
            // walkway (same proven pattern as the original two, just at new coordinates checked
            // against every other plaza prop/spawn point's position — clear of all of them).
            foreach (var p in new[] { new Vector2(-4f, -43f), new Vector2(4f, -43f), new Vector2(-2f, -39.5f), new Vector2(2f, -39.5f) })
            {
                CreateCylinder(zone, "FlowerBed", new Vector3(p.x, 0.15f, p.y), 0.8f, 0.3f, new Color(0.361f, 0.247f, 0.165f));

                // A small cluster of 3 real flower models arranged around the bed's center instead
                // of one bare sphere, falling back to the original sphere only if neither Flowers
                // prefab resolved (rather than per-flower, so a partial cluster doesn't leave an
                // odd mix with a placeholder sphere sitting among real flowers).
                bool anyPlaced = false;
                for (int k = 0; k < 3; k++)
                {
                    float ang = k * 120f * Mathf.Deg2Rad;
                    var pos = new Vector3(p.x + Mathf.Cos(ang) * 0.4f, 0.32f, p.y + Mathf.Sin(ang) * 0.4f);
                    string variant = k % 2 == 0 ? "Flowers_01" : "Flowers_02";
                    var flowerPrefab = LoadPrefab(kNaturePrefabDir + variant + ".prefab");
                    float yRot = (float)(_greeneryRng.NextDouble() * 360.0);
                    if (InstantiateAssetPrefab(zone, flowerPrefab, "Flowers_" + variant + "_" + k, pos, new Vector3(0f, yRot, 0f)) != null)
                        anyPlaced = true;
                }
                if (!anyPlaced)
                    CreateSphere(zone, "Flowers", new Vector3(p.x, 0.4f, p.y), 0.5f, new Color(0.788f, 0.322f, 0.478f));
            }

            var art = new GameObject("PublicArt").transform;
            art.SetParent(zone, false);
            art.localPosition = new Vector3(-8f, 0f, -44f);
            CreateCylinder(art, "Sculpture", new Vector3(0f, 0.9f, 0f), 0.4f, 1.8f, new Color(0.541f, 0.604f, 0.627f), new Vector3(0f, 0f, 12f));
            ScatterNaturePrefab(art, kRockPool, new Vector3(0.9f, 0f, 0.6f));
            ScatterNaturePrefab(art, kRockPool, new Vector3(-0.7f, 0f, -0.8f));

            CreateBox(zone, "DonationBox", new Vector3(3f, 0.5f, -41f), new Vector3(0.6f, 1f, 0.5f), new Color(0.294f, 0.431f, 0.294f));

            var memorial = new GameObject("VeteransMemorial").transform;
            memorial.SetParent(zone, false);
            memorial.localPosition = new Vector3(8f, 0f, -44f);
            CreateBox(memorial, "Monument", new Vector3(0f, 0.9f, 0f), new Vector3(0.8f, 1.8f, 0.8f), new Color(0.541f, 0.541f, 0.541f));
            CreateLabel(memorial, "IN HONOR OF\nTHOSE WHO SERVED", new Vector3(0f, 1.3f, 0.42f), Vector3.zero, 0.22f, new Color(0.137f, 0.141f, 0.122f), 1.8f);

            BuildInfoSign(zone, "RegisterToVoteBoard", -9f, -38f, 20f, "Register to Vote", CivicExhibitTrigger.SourceType.EnvironmentalBoard, "election_register_to_vote", new Color(0.863f, 0.906f, 0.933f));

            _plazaSpawns.Add(MakeSpawnPoint(zone, "elder_01", -6f, -44.5f, 300f));
            _plazaSpawns.Add(MakeSpawnPoint(zone, "city_employee_01", 3f, -41f, 180f));
            _plazaSpawns.Add(MakeSpawnPoint(zone, "teenager_01", -3f, -40f, 150f));
            _plazaSpawns.Add(MakeSpawnPoint(zone, "teen_friend_01", -4.3f, -40.6f, 300f));
            _plazaSpawns.Add(MakeSpawnPoint(zone, "city_planner_01", 7f, -42f, 200f));

            // 2026-07-22 governor/canvassing decision system: the Governor's post (a small podium,
            // not a full building — the physical Town Hall wings are a separate, not-yet-built
            // milestone) plus the "Community Pulse" board that reads CityMoraleSystem live. Placed
            // well clear of the portal's 3.5m keep-clear radius and the other plaza NPCs above.
            var govPost = new GameObject("GovernorPost").transform;
            govPost.SetParent(zone, false);
            govPost.localPosition = new Vector3(10f, 0f, -44.3f);
            CreateBox(govPost, "Podium", new Vector3(0f, 0.55f, 0f), new Vector3(0.9f, 1.1f, 0.6f), new Color(0.176f, 0.29f, 0.369f));
            CreateLabel(govPost, "OFFICE OF\nTHE GOVERNOR", new Vector3(0f, 1.3f, 0.32f), Vector3.zero, 0.18f, new Color(0.122f, 0.227f, 0.302f), 1.6f);
            _plazaSpawns.Add(MakeSpawnPoint(zone, "governor_01", 10f, -43.2f, 210f));

            BuildMoraleBoard(zone, "CommunityPulseBoard", 12f, -40.5f, 250f);

            // Portal marker into City Hall interior
            var portal = new GameObject("Portal_ToInterior").transform;
            portal.SetParent(zone, false);
            portal.localPosition = new Vector3(0f, 0f, -46.5f);
            CreateBox(portal, "FrameL", new Vector3(-1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(0.624f, 0.847f, 1f), markStatic: false);
            CreateBox(portal, "FrameR", new Vector3(1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(0.624f, 0.847f, 1f), markStatic: false);
            CreateBox(portal, "Lintel", new Vector3(0f, 2.4f, 0f), new Vector3(2.55f, 0.15f, 0.15f), new Color(0.624f, 0.847f, 1f), markStatic: false);
            CreateCylinder(portal, "Threshold", new Vector3(0f, 0.02f, 0f), 1.2f, 0.02f, new Color(0.624f, 0.847f, 1f), markStatic: false);
            CreateLabel(portal, "ENTER CITY HALL", new Vector3(0f, 2.9f, 0f), Vector3.zero, 0.3f, new Color(0.122f, 0.227f, 0.302f), 3f);
            var portalCollider = CreateBox(portal, "TriggerVolume", new Vector3(0f, 1.2f, 0f), new Vector3(2.4f, 2.4f, 0.3f), new Color(0.624f, 0.847f, 1f), markStatic: false);
            portalCollider.GetComponent<Renderer>().enabled = false;
            // BUG FOUND 2026-07-22: this box exactly fills the archway's doorway gap between
            // FrameL/FrameR, and CreatePrimitive's default BoxCollider is solid (isTrigger=false) —
            // so despite being invisible, it physically blocked the player from ever walking through
            // the archway (reported as "unable to enter the building," screenshot showed the archway
            // looking like a solid wall). isTrigger=true lets PortalTravel's new OnTriggerEnter fire
            // on walk-through while no longer blocking movement; SphereCast/Raycast still hit trigger
            // colliders by default, so click-to-interact keeps working too.
            portalCollider.GetComponent<Collider>().isTrigger = true;
            _interiorPortalMarker = portalCollider; // PortalTravel added in WirePortals() once both areas exist
        }

        // ==============================================================
        // City Hall interior (centerpiece)
        // ==============================================================
        // exhibit_offseason_canvassing (8th entry, added 2026-07-22) is the one
        // piece of content in this whole scaffold that directly carries this
        // project's actual stated purpose ("advocates canvassing during off
        // seasons") — the original 7 exhibits are solid general how-local-
        // government-works content, but none of them ever mentioned canvassing at
        // all, which is part of why the experience read as unclear on its own
        // goal. See also canvassing_volunteer_signup in Boards.json and the new
        // canvasser_01 NPC below.
        private static readonly string[] ExhibitIds =
        {
            "exhibit_budget", "exhibit_taxes", "exhibit_council_vote",
            "exhibit_road_repair", "exhibit_parks_funding", "exhibit_transit_approval", "exhibit_submit_concerns",
            "exhibit_offseason_canvassing"
        };
        private static readonly string[] ExhibitTitles =
        {
            "How does a city\nbudget work?", "Where do\ntaxes go?", "How does a city\ncouncil vote?",
            "How is a road\nrepaired?", "How are parks\nfunded?", "How does public\ntransportation\nget approved?", "How do residents\nsubmit concerns?",
            "Why canvass\nbetween elections?"
        };

        private static void BuildInteriorCeiling(Transform parent, string name, Vector3 center, float width, float depth)
        {
            // The interior was previously open above the five-metre walls, so the player saw
            // the skybox wherever they looked. A warm finished ceiling with teal cross-beams
            // makes the town hall read as a complete civic building from inside the room.
            var warmPlaster = new Color(0.89f, 0.85f, 0.75f);
            var deepTeal = new Color(0.10f, 0.25f, 0.29f);
            CreateBox(parent, name + "_Plaster", center + new Vector3(0f, 5.06f, 0f),
                new Vector3(width, 0.18f, depth), warmPlaster);

            const float beamHeight = 0.16f;
            const float beamWidth = 0.22f;
            float insideWidth = Mathf.Max(0.5f, width - 0.2f);
            for (float z = -depth * 0.5f + 2f; z < depth * 0.5f; z += 4f)
            {
                CreateBox(parent, name + "_Beam_" + Mathf.Abs(z).ToString("0.0"),
                    center + new Vector3(0f, 4.91f, z), new Vector3(insideWidth, beamHeight, beamWidth), deepTeal);
            }
        }

        private static void BuildPottedPlant(Transform parent, string name, Vector3 position, float scale = 1f)
        {
            var plant = new GameObject(name).transform;
            plant.SetParent(parent, false);
            plant.localPosition = position;
            CreateCylinder(plant, "TerracottaPot", new Vector3(0f, 0.22f * scale, 0f), 0.32f * scale, 0.44f * scale, Palette.TerracottaRoof);
            CreateCylinder(plant, "Soil", new Vector3(0f, 0.44f * scale, 0f), 0.24f * scale, 0.035f * scale, new Color(0.25f, 0.18f, 0.12f));
            CreateCylinder(plant, "Stem", new Vector3(0f, 0.84f * scale, 0f), 0.035f * scale, 0.8f * scale, Palette.ForestGreen);
            CreateSphere(plant, "LeafCenter", new Vector3(0f, 1.2f * scale, 0f), 0.33f * scale, new Color(0.37f, 0.52f, 0.34f));
            CreateSphere(plant, "LeafLeft", new Vector3(-0.25f * scale, 0.94f * scale, 0f), 0.28f * scale, new Color(0.48f, 0.61f, 0.40f));
            CreateSphere(plant, "LeafRight", new Vector3(0.25f * scale, 0.94f * scale, 0f), 0.28f * scale, new Color(0.42f, 0.57f, 0.37f));
            CreateSphere(plant, "LeafTop", new Vector3(0f, 1.48f * scale, 0f), 0.25f * scale, new Color(0.56f, 0.66f, 0.44f));
        }

        private static void BuildInteriorLight(Transform parent, string name, Vector3 position, float range = 14f)
        {
            CreateBox(parent, name + "_Fixture", position + Vector3.up * 0.22f, new Vector3(0.7f, 0.08f, 0.7f), Palette.CharcoalTrim);
            CreateSphere(parent, name + "_Bulb", position, 0.16f, new Color(1f, 0.84f, 0.62f));
            var lightObject = new GameObject(name + "_WarmLight");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = position;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.79f, 0.58f);
            light.intensity = 0.75f;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        private static void BuildCivicArtwork(Transform parent, string name, Vector3 position, string title, Color matColor, float width = 1.8f)
        {
            CreateBox(parent, name + "_Frame", position, new Vector3(width, 1.25f, 0.12f), new Color(0.35f, 0.24f, 0.16f));
            CreateBox(parent, name + "_Canvas", position + new Vector3(0f, 0f, -0.075f), new Vector3(width - 0.16f, 1.08f, 0.035f), matColor);
            CreateBox(parent, name + "_Accent", position + new Vector3(0f, -0.22f, -0.052f), new Vector3(width - 0.4f, 0.07f, 0.02f), Palette.SoftWhite);
            CreateLabel(parent, title, position + new Vector3(0f, -0.42f, -0.025f), Vector3.zero, 0.13f, new Color(0.12f, 0.18f, 0.20f), width - 0.28f);
        }

        private static void BuildMonitor(Transform parent, string name, Vector3 position)
        {
            CreateBox(parent, name + "_Screen", position + new Vector3(0f, 0.28f, 0f), new Vector3(0.82f, 0.56f, 0.08f), new Color(0.12f, 0.22f, 0.27f));
            CreateBox(parent, name + "_Display", position + new Vector3(0f, 0.3f, -0.046f), new Vector3(0.7f, 0.42f, 0.018f), new Color(0.66f, 0.82f, 0.82f));
            CreateBox(parent, name + "_Stand", position + new Vector3(0f, -0.05f, 0f), new Vector3(0.12f, 0.14f, 0.12f), Palette.CharcoalTrim);
            CreateBox(parent, name + "_Base", position + new Vector3(0f, -0.13f, 0f), new Vector3(0.36f, 0.04f, 0.2f), Palette.CharcoalTrim);
            CreateBox(parent, name + "_Keyboard", position + new Vector3(0f, -0.22f, -0.22f), new Vector3(0.52f, 0.025f, 0.18f), Palette.WarmGray);
        }

        private static void BuildInterior(Transform root)
        {
            var zone = new GameObject("Lobby").transform;
            zone.SetParent(root, false);

            CreateGroundPlane(zone, "Floor", Vector3.zero, 26f, 20f, new Color(0.902f, 0.886f, 0.831f));
            BuildInteriorCeiling(zone, "Lobby", Vector3.zero, 26f, 20f);
            BuildInteriorLight(zone, "LobbyPendant", new Vector3(0f, 4.65f, 0f), 18f);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            // 2026-07-23 Town Hall interior redesign: Wall_Back/West/East were each one solid box
            // before — now built with BuildWallAlongX/Z (see those methods' doc comment) so they
            // can leave a gap for the new wing doorways without hand-splitting coordinates. Wall_
            // FrontW/FrontE are untouched (still one solid box each) since the existing plaza
            // entrance/door-row/reception layout there isn't changing.
            BuildWallAlongX(zone, "Wall_Back", 0f, -10f, 26f, 5f, 0.3f, wallColor, -3f, 3f);   // gap -> central corridor -> Rotunda
            BuildWallAlongZ(zone, "Wall_West", -13f, 0f, 20f, 5f, 0.3f, wallColor, -3f, 3f);   // gap -> Community Issues Board wing
            BuildWallAlongZ(zone, "Wall_East", 13f, 0f, 20f, 5f, 0.3f, wallColor, -3f, 3f);    // gap -> Community Resources Center wing
            CreateBox(zone, "Wall_FrontW", new Vector3(-9f, 2.5f, 10f), new Vector3(8f, 5f, 0.3f), wallColor);
            CreateBox(zone, "Wall_FrontE", new Vector3(9f, 2.5f, 10f), new Vector3(8f, 5f, 0.3f), wallColor);
            CreateLabel(zone, "CITY HALL — MAIN LOBBY", new Vector3(0f, 4.4f, -9.8f), Vector3.zero, 0.5f, new Color(0.122f, 0.227f, 0.302f), 9f);

            // Simple wall poster (2026-07-23, see BuildWallPoster's own comment) — mirrors the
            // DirectoryBoard's spot on the opposite side of the same wall, clear of the Meeting
            // Kiosk at x=6.
            BuildWallPoster(zone, "Poster_GetInvolved", new Vector3(10f, 1.8f, -9.8f),
                "GET INVOLVED\nYEAR-ROUND", "Democracy doesn't stop after election day.", Palette.SlateBlueGray);

            // 2026-07-23 Town Hall interior redesign brief: "a welcoming atrium/lobby... ambient
            // NPCs with routines." Positioned away from the reception desk (z=-6), exhibit kiosks
            // (z=4/z=-2), waiting chairs, and both new side-wall gaps (z in -3..3) so they don't
            // spawn-collide with any existing lobby geometry or block the new corridor openings.
            _lobbySpawns.Add(MakeSpawnPoint(zone, "city_greeter_01", -8f, -7.2f, 60f));
            _lobbySpawns.Add(MakeSpawnPoint(zone, "records_clerk_01", 7f, -6.6f, 280f));

            var desk = new GameObject("ReceptionDesk").transform;
            desk.SetParent(zone, false);
            desk.localPosition = new Vector3(0f, 0f, -6f);
            CreateBox(desk, "Desk", new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 1f), new Color(0.541f, 0.42f, 0.271f));
            CreateLabel(desk, "RECEPTION", new Vector3(0f, 1.3f, 0.51f), Vector3.zero, 0.3f, new Color(0.137f, 0.141f, 0.122f), 3.5f);
            BuildMonitor(desk, "ReceptionComputer", new Vector3(0f, 1.18f, 0.05f));

            var directory = CreateBox(zone, "DirectoryBoard", new Vector3(-8f, 2f, -9.8f), new Vector3(4f, 3f, 0.05f), new Color(0.176f, 0.29f, 0.369f));
            CreateLabel(directory.transform, "DIRECTORY\n\nPlanning - Public Works\nFinance - Parks\nTransportation - Housing\nPermits - Public Records", new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.22f, Color.white, 3.6f);

            var welcomeMap = CreateBox(zone, "WelcomeMap", new Vector3(2.2f, 2.4f, -9.78f), new Vector3(3.2f, 2.5f, 0.08f), new Color(0.23f, 0.36f, 0.39f));
            CreateBox(welcomeMap.transform, "MapLand", new Vector3(0f, 0f, 0.055f), new Vector3(2.72f, 1.85f, 0.02f), new Color(0.77f, 0.79f, 0.68f));
            CreateBox(welcomeMap.transform, "MapPark", new Vector3(-0.75f, 0.22f, 0.075f), new Vector3(0.58f, 0.5f, 0.02f), new Color(0.42f, 0.58f, 0.39f));
            CreateBox(welcomeMap.transform, "MapRoadNorthSouth", new Vector3(0.1f, 0f, 0.075f), new Vector3(0.16f, 1.8f, 0.02f), new Color(0.86f, 0.84f, 0.77f));
            CreateBox(welcomeMap.transform, "MapRoadEastWest", new Vector3(0f, -0.2f, 0.095f), new Vector3(2.65f, 0.15f, 0.02f), new Color(0.86f, 0.84f, 0.77f));
            CreateLabel(welcomeMap.transform, "WELCOME • CITY DIRECTORY", new Vector3(0f, 0.86f, 0.07f), Vector3.zero, 0.17f, Color.white, 2.85f);
            CreateLabel(welcomeMap.transform, "TOWN HALL", new Vector3(0.62f, 0.12f, 0.1f), Vector3.zero, 0.11f, new Color(0.12f, 0.23f, 0.30f), 0.95f);

            var kiosk = new GameObject("MeetingKiosk").transform;
            kiosk.SetParent(zone, false);
            kiosk.localPosition = new Vector3(6f, 0f, -8f);
            CreateBox(kiosk, "Stand", new Vector3(0f, 0.9f, 0f), new Vector3(0.8f, 1.8f, 0.5f), new Color(0.227f, 0.239f, 0.239f));
            var kioskScreen = CreateBox(kiosk, "Screen", new Vector3(0f, 1.3f, 0.26f), new Vector3(0.6f, 0.9f, 0.05f), new Color(0.122f, 0.227f, 0.302f));
            CreateLabel(kioskScreen.transform, "MEETING\nSCHEDULE", new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.16f, new Color(0.624f, 0.847f, 1f), 0.55f);
            var kioskTrigger = kioskScreen.AddComponent<CivicExhibitTrigger>();
            kioskTrigger.sourceType = CivicExhibitTrigger.SourceType.EnvironmentalBoard;
            kioskTrigger.entryId = "bulletin_board_upcoming";
            kioskTrigger.promptText = "Read the meeting schedule";
            var kioskPrompt = kioskScreen.AddComponent<InteractionPrompt>();
            var kioskIcon = CreateSphere(kioskScreen.transform, "IconMarker", new Vector3(0f, 0.6f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
            Object.DestroyImmediate(kioskIcon.GetComponent<Collider>());
            kioskIcon.SetActive(false);
            kioskPrompt.iconRoot = kioskIcon.transform;

            CreateBox(zone, "BrochureRack", new Vector3(9f, 0.75f, -4f), new Vector3(0.6f, 1.5f, 0.6f), new Color(0.478f, 0.357f, 0.235f));
            foreach (var p in new[] { new Vector2(-0.18f, -0.05f), new Vector2(0f, 0f), new Vector2(0.18f, 0.05f) })
                CreateBox(zone, "VisitorBrochure", new Vector3(9f + p.x, 1.5f, -4f + p.y), new Vector3(0.17f, 0.38f, 0.025f), Palette.SoftWhite, new Vector3(-12f, 0f, p.x * 50f));
            foreach (var p in new[] { new Vector2(-4f, -1f), new Vector2(-2.6f, -1f), new Vector2(-1.2f, -1f) })
                CreateBox(zone, "WaitingChair", new Vector3(p.x, 0.35f, p.y), new Vector3(1f, 0.1f, 1f), new Color(0.541f, 0.42f, 0.271f));
            foreach (var p in new[] { new Vector3(-11f, 0f, 7.2f), new Vector3(11f, 0f, 7.2f), new Vector3(-11f, 0f, -6.7f), new Vector3(11f, 0f, -6.7f) })
                BuildPottedPlant(zone, "LobbyPlant", p, 0.9f);
            BuildCivicArtwork(zone, "LobbyPainting_Park", new Vector3(-10.4f, 2.8f, 9.72f), "NEIGHBORHOOD PARK", new Color(0.49f, 0.63f, 0.45f), 1.8f);
            BuildCivicArtwork(zone, "LobbyPainting_City", new Vector3(10.4f, 2.8f, 9.72f), "A CITY FOR EVERYONE", new Color(0.42f, 0.58f, 0.68f), 1.8f);

            var exhibitPositions = new[]
            {
                new Vector2(-10f, 4f), new Vector2(-6f, 4f), new Vector2(-2f, 4f),
                new Vector2(2f, 4f), new Vector2(6f, 4f), new Vector2(10f, 4f), new Vector2(11f, -2f),
                new Vector2(-11f, -2f)
            };
            for (int i = 0; i < ExhibitIds.Length; i++)
            {
                var pos = exhibitPositions[i];
                var g = new GameObject("Exhibit_" + ExhibitIds[i]).transform;
                g.SetParent(zone, false);
                g.localPosition = new Vector3(pos.x, 0f, pos.y);
                CreateBox(g, "Kiosk", new Vector3(0f, 1f, 0f), new Vector3(1.6f, 2f, 0.15f), new Color(0.176f, 0.29f, 0.369f));
                var screenFace = CreateBox(g, "Screen", new Vector3(0f, 1.2f, 0.09f), new Vector3(1.3f, 0.9f, 0.03f), new Color(0.957f, 0.957f, 0.941f));
                CreateLabel(screenFace.transform, ExhibitTitles[i], new Vector3(0f, 0f, 0.03f), Vector3.zero, 0.18f, new Color(0.122f, 0.227f, 0.302f), 1.2f);
                var trigger = screenFace.AddComponent<CivicExhibitTrigger>();
                trigger.sourceType = CivicExhibitTrigger.SourceType.CivicExhibit;
                trigger.entryId = ExhibitIds[i];
                trigger.promptText = "Explore " + ExhibitTitles[i].Replace('\n', ' ');
                var prompt = screenFace.AddComponent<InteractionPrompt>();
                var icon = CreateSphere(screenFace.transform, "IconMarker", new Vector3(0f, 0.6f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
                Object.DestroyImmediate(icon.GetComponent<Collider>());
                icon.SetActive(false);
                prompt.iconRoot = icon.transform;
            }

            CreateBox(zone, "LobbyWayfinding", new Vector3(0f, 3.2f, 9.72f), new Vector3(7.6f, 0.85f, 0.08f), Palette.SoftWhite);
            CreateLabel(zone, "COUNCIL CHAMBER  ←     •     MAYOR'S OFFICE  →", new Vector3(0f, 3.2f, 9.78f), Vector3.zero, 0.23f, new Color(0.12f, 0.23f, 0.30f), 7.2f);

            // Portal marker back to plaza
            var portal = new GameObject("Portal_ToPlaza").transform;
            portal.SetParent(zone, false);
            portal.localPosition = new Vector3(0f, 0f, 9.5f);
            CreateBox(portal, "FrameL", new Vector3(-1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateBox(portal, "FrameR", new Vector3(1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateBox(portal, "Lintel", new Vector3(0f, 2.4f, 0f), new Vector3(2.55f, 0.15f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateLabel(portal, "BACK TO PLAZA", new Vector3(0f, 2.9f, 0f), Vector3.zero, 0.3f, new Color(0.122f, 0.227f, 0.302f), 3f);
            var portalCollider = CreateBox(portal, "TriggerVolume", new Vector3(0f, 1.2f, 0f), new Vector3(2.4f, 2.4f, 0.3f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            portalCollider.GetComponent<Renderer>().enabled = false;
            portalCollider.GetComponent<Collider>().isTrigger = true; // see matching note on Portal_ToInterior above
            _plazaPortalMarker = portalCollider;
        }

        // ==============================================================
        // Town Hall interior redesign (2026-07-23) — "fix the design of the city hall" ->
        // clarified via AskUserQuestion as: build the full scoped-but-not-built interior redesign
        // from the project notes (lobby/atrium already exists above; this adds the four wings, a
        // connecting central corridor + Rotunda junction, and the Final Reflection Hall + exit
        // choice). Per that earlier scoping decision, everything here is real, walkable, sample
        // content — not a placeholder — with the real canvassing/save backend still deferred.
        //
        // Layout (all coordinates local to _interiorRoot, same space BuildInterior's Lobby uses):
        // the Lobby's three previously-solid walls (Back/West/East) now each carry one gap (see
        // BuildWallAlongX/Z above) leading to: a central corridor south to a 10x10 Rotunda
        // junction (which itself branches further south to the Final Reflection Hall, west to the
        // Reflection Wall wing, and east to the Volunteer Debrief Lounge); and directly west/east
        // to the Community Issues Board and Community Resources Center wings respectively. Every
        // room follows the exact same floor+four-walls-with-optional-gap shape, so the two wall
        // helpers above do all the real geometry work — these methods just call them with the
        // right numbers.
        // ==============================================================
        private static void BuildTownHallWings(Transform root)
        {
            BuildCentralCorridorAndRotunda(root);
            BuildCommunityIssuesBoardWing(root);
            BuildCommunityResourcesCenterWing(root);
            BuildReflectionWallWing(root);
            BuildVolunteerDebriefLoungeWing(root);
            BuildFinalReflectionHall(root);
            BuildCouncilChamberWing(root);
            BuildMayorOfficeWing(root);
        }

        private static void BuildCentralCorridorAndRotunda(Transform root)
        {
            var zone = new GameObject("CentralCorridor").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            // Corridor: Lobby's back-wall gap (x -3..3 at z=-10) to the Rotunda's north gap (z=-14).
            CreateGroundPlane(zone, "CorridorFloor", new Vector3(0f, 0f, -12f), 6f, 4f, floorColor);
            BuildInteriorCeiling(zone, "CentralCorridor", new Vector3(0f, 0f, -12f), 6f, 4f);
            BuildWallAlongZ(zone, "CorridorWall_W", -3f, -12f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "CorridorWall_E", 3f, -12f, 4f, 5f, 0.3f, wallColor);

            // Rotunda: a 10x10 junction with four gaps — Lobby (north), Final Reflection Hall
            // (south), Reflection Wall wing (west), Volunteer Debrief Lounge (east).
            CreateGroundPlane(zone, "RotundaFloor", new Vector3(0f, 0f, -19f), 10f, 10f, floorColor);
            BuildInteriorCeiling(zone, "Rotunda", new Vector3(0f, 0f, -19f), 10f, 10f);
            BuildInteriorLight(zone, "RotundaPendant", new Vector3(0f, 4.6f, -19f), 13f);
            BuildWallAlongX(zone, "RotundaWall_North", 0f, -14f, 10f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongX(zone, "RotundaWall_South", 0f, -24f, 10f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongZ(zone, "RotundaWall_West", -5f, -19f, 10f, 5f, 0.3f, wallColor, -22f, -16f);
            BuildWallAlongZ(zone, "RotundaWall_East", 5f, -19f, 10f, 5f, 0.3f, wallColor, -22f, -16f);

            CreateLabel(zone, "TOWN HALL", new Vector3(0f, 4.3f, -19f), Vector3.zero, 0.45f, new Color(0.122f, 0.227f, 0.302f), 6f);

            // Simple wall poster (2026-07-23) on the Rotunda's South wall segment, well clear of
            // the four gapped walls' door openings.
            BuildWallPoster(zone, "Poster_EveryVoice", new Vector3(-4f, 1.8f, -23.7f),
                "EVERY VOICE\nCOUNTS", "This hall exists so more of them are heard.", Palette.TerracottaRoof);
        }

        private static void BuildCommunityIssuesBoardWing(Transform root)
        {
            var zone = new GameObject("CommunityIssuesBoardWing").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            CreateGroundPlane(zone, "CorridorFloor", new Vector3(-15f, 0f, 0f), 4f, 6f, floorColor);
            BuildInteriorCeiling(zone, "IssuesCorridor", new Vector3(-15f, 0f, 0f), 4f, 6f);
            BuildWallAlongX(zone, "CorridorWall_N", -15f, 3f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "CorridorWall_S", -15f, -3f, 4f, 5f, 0.3f, wallColor);

            CreateGroundPlane(zone, "Floor", new Vector3(-24f, 0f, 0f), 14f, 12f, floorColor);
            BuildInteriorCeiling(zone, "IssuesWing", new Vector3(-24f, 0f, 0f), 14f, 12f);
            BuildInteriorLight(zone, "IssuesGalleryLight", new Vector3(-24f, 4.6f, 0f), 14f);
            BuildWallAlongX(zone, "Wall_North", -24f, 6f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "Wall_South", -24f, -6f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_West", -31f, 0f, 12f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongZ(zone, "Wall_East", -17f, 0f, 12f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildPottedPlant(zone, "IssuesPlant", new Vector3(-29.8f, 0f, 4.3f));
            BuildPottedPlant(zone, "IssuesPlant", new Vector3(-18.2f, 0f, 4.3f));
            BuildCivicArtwork(zone, "IssuesGalleryArt", new Vector3(-24f, 2.7f, -5.72f), "LISTEN FIRST", Palette.MustardGold, 2.1f);

            CreateLabel(zone, "COMMUNITY ISSUES BOARD", new Vector3(-24f, 4.3f, 5.7f), Vector3.zero, 0.3f, new Color(0.122f, 0.227f, 0.302f), 7f);
            CreateLabel(zone, "Live morale, gathered from every canvassing conversation across town.", new Vector3(-24f, 3.6f, 5.7f), Vector3.zero, 0.15f, new Color(0.35f, 0.35f, 0.32f), 7f);

            // Simple wall poster (2026-07-23) on the Wall_South, opposite the title/subtitle and
            // the bar-chart pedestals so it doesn't crowd either.
            BuildWallPoster(zone, "Poster_TrackWhatMatters", new Vector3(-24f, 1.8f, -5.7f),
                "TRACK WHAT\nMATTERS", "Morale reflects real conversations, not guesses.", Palette.MustardGold);

            // Five animated bars (four CityMoraleSystem-tracked zones + one city-wide average),
            // each a pedestal + a growing/shrinking "Fill" cube driven live by
            // MoraleBarChartDisplay (see that file) — the actual "animated-bar data gallery" from
            // the brief, reading the exact data source CityMoraleSystem was already flagged as a
            // candidate for back when it was built for the governor/canvassing system.
            var barSpecs = new (string zone, string label, float x)[]
            {
                ("residential", "Residential", -29f),
                ("commercial", "Commercial", -26.5f),
                ("public_row", "Public Row", -24f),
                ("city_hall_plaza", "City Hall Plaza", -21.5f),
                ("", "City-Wide", -19f),
            };
            var bars = new MoraleBarChartDisplay.Bar[barSpecs.Length];
            for (int i = 0; i < barSpecs.Length; i++)
            {
                var spec = barSpecs[i];
                var pedestalBase = new Vector3(spec.x, 0f, 2f);
                CreateBox(zone, "Pedestal_" + i, pedestalBase + new Vector3(0f, 0.1f, 0f), new Vector3(0.9f, 0.2f, 0.9f), new Color(0.176f, 0.29f, 0.369f));

                var barBaseGO = new GameObject("BarBase_" + i);
                barBaseGO.transform.SetParent(zone, false);
                barBaseGO.transform.localPosition = pedestalBase + new Vector3(0f, 0.2f, 0f); // pedestal's top surface = this bar's ground point

                var fill = CreateBox(barBaseGO.transform, "Fill", Vector3.zero, new Vector3(0.7f, 1f, 0.7f), new Color(0.796f, 0.635f, 0.208f), markStatic: false);
                Object.DestroyImmediate(fill.GetComponent<Collider>()); // purely visual; a live-scaling collider would read as a shrinking wall to the player

                // Label sits above the tallest possible bar (maxHeight 3) so it's never swallowed
                // even at 100 morale.
                var label = CreateLabel(barBaseGO.transform, spec.label + "\n0", new Vector3(0f, 3.4f, 0f), Vector3.zero, 0.16f, new Color(0.122f, 0.227f, 0.302f), 1.4f);

                bars[i] = new MoraleBarChartDisplay.Bar { zone = spec.zone, fillTransform = fill.transform, label = label, maxHeight = 3f };
            }

            var displayGO = new GameObject("MoraleBarChartDisplay");
            displayGO.transform.SetParent(zone, false);
            displayGO.AddComponent<MoraleBarChartDisplay>().bars = bars;
        }

        private static void BuildCommunityResourcesCenterWing(Transform root)
        {
            var zone = new GameObject("CommunityResourcesCenterWing").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            CreateGroundPlane(zone, "CorridorFloor", new Vector3(15f, 0f, 0f), 4f, 6f, floorColor);
            BuildInteriorCeiling(zone, "ResourcesCorridor", new Vector3(15f, 0f, 0f), 4f, 6f);
            BuildWallAlongX(zone, "CorridorWall_N", 15f, 3f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "CorridorWall_S", 15f, -3f, 4f, 5f, 0.3f, wallColor);

            CreateGroundPlane(zone, "Floor", new Vector3(24f, 0f, 0f), 14f, 12f, floorColor);
            BuildInteriorCeiling(zone, "ResourcesWing", new Vector3(24f, 0f, 0f), 14f, 12f);
            BuildInteriorLight(zone, "ResourcesGalleryLight", new Vector3(24f, 4.6f, 0f), 14f);
            BuildWallAlongX(zone, "Wall_North", 24f, 6f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "Wall_South", 24f, -6f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_East", 31f, 0f, 12f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongZ(zone, "Wall_West", 17f, 0f, 12f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildPottedPlant(zone, "ResourcesPlant", new Vector3(18.2f, 0f, 4.2f));
            BuildPottedPlant(zone, "ResourcesPlant", new Vector3(29.8f, 0f, 4.2f));
            BuildCivicArtwork(zone, "ResourcesGalleryArt", new Vector3(24f, 2.7f, -5.72f), "WAYS TO GET INVOLVED", Palette.TealAccent, 2.4f);

            CreateLabel(zone, "COMMUNITY RESOURCES CENTER", new Vector3(24f, 4.3f, 5.7f), Vector3.zero, 0.28f, new Color(0.122f, 0.227f, 0.302f), 8f);

            // Simple wall poster (2026-07-23) on the Wall_South, clear of the Info Desk (z=-4)
            // and the three exhibit kiosks (z=3).
            BuildWallPoster(zone, "Poster_FindYourNextStep", new Vector3(24f, 1.8f, -5.7f),
                "FIND YOUR\nNEXT STEP", "Registration, local orgs, and volunteer paths — all here.", Palette.TealAccent);

            var deskT = new GameObject("InfoDesk").transform;
            deskT.SetParent(zone, false);
            deskT.localPosition = new Vector3(24f, 0f, -4f);
            CreateBox(deskT, "Desk", new Vector3(0f, 0.6f, 0f), new Vector3(4f, 1.2f, 1f), new Color(0.541f, 0.42f, 0.271f));
            CreateLabel(deskT, "VISITOR INFO", new Vector3(0f, 1.3f, 0.51f), Vector3.zero, 0.24f, new Color(0.137f, 0.141f, 0.122f), 3.2f);

            // Three kiosks reusing the exact CivicExhibitTrigger + InteractionPrompt + hidden
            // IconMarker pattern already established for the Lobby's 8 exhibits — new content in
            // CivicExhibits.json (exhibit_voter_registration_howto/local_orgs_directory/volunteer_pathways).
            var resourceExhibits = new[]
            {
                ("exhibit_voter_registration_howto", new Vector2(19f, 3f)),
                ("exhibit_local_orgs_directory", new Vector2(24f, 3f)),
                ("exhibit_volunteer_pathways", new Vector2(29f, 3f)),
            };
            foreach (var (id, pos) in resourceExhibits)
            {
                var g = new GameObject("Kiosk_" + id).transform;
                g.SetParent(zone, false);
                g.localPosition = new Vector3(pos.x, 0f, pos.y);
                CreateBox(g, "Stand", new Vector3(0f, 1f, 0f), new Vector3(1.6f, 2f, 0.15f), new Color(0.176f, 0.29f, 0.369f));
                var screenFace = CreateBox(g, "Screen", new Vector3(0f, 1.2f, 0.09f), new Vector3(1.3f, 0.9f, 0.03f), new Color(0.957f, 0.957f, 0.941f));
                var trigger = screenFace.AddComponent<CivicExhibitTrigger>();
                trigger.sourceType = CivicExhibitTrigger.SourceType.CivicExhibit;
                trigger.entryId = id;
                var prompt = screenFace.AddComponent<InteractionPrompt>();
                var icon = CreateSphere(screenFace.transform, "IconMarker", new Vector3(0f, 0.6f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
                Object.DestroyImmediate(icon.GetComponent<Collider>());
                icon.SetActive(false);
                prompt.iconRoot = icon.transform;
            }
        }

        private static void BuildCouncilChamberWing(Transform root)
        {
            var zone = new GameObject("CouncilChamberWing").transform;
            zone.SetParent(root, false);
            var wall = new Color(0.949f, 0.937f, 0.894f);
            var floor = new Color(0.86f, 0.82f, 0.73f);
            CreateGroundPlane(zone, "CouncilCorridorFloor", new Vector3(-35f, 0f, 0f), 8f, 6f, floor);
            BuildInteriorCeiling(zone, "CouncilCorridor", new Vector3(-35f, 0f, 0f), 8f, 6f);
            BuildWallAlongX(zone, "CouncilCorridorWall_N", -35f, 3f, 8f, 5f, 0.3f, wall);
            BuildWallAlongX(zone, "CouncilCorridorWall_S", -35f, -3f, 8f, 5f, 0.3f, wall);

            CreateGroundPlane(zone, "CouncilChamberFloor", new Vector3(-46f, 0f, 0f), 14f, 12f, floor);
            BuildInteriorCeiling(zone, "CouncilChamber", new Vector3(-46f, 0f, 0f), 14f, 12f);
            BuildInteriorLight(zone, "CouncilChamberLight", new Vector3(-46f, 4.6f, 0f), 14f);
            BuildWallAlongX(zone, "CouncilWall_North", -46f, 6f, 14f, 5f, 0.3f, wall);
            BuildWallAlongX(zone, "CouncilWall_South", -46f, -6f, 14f, 5f, 0.3f, wall);
            BuildWallAlongZ(zone, "CouncilWall_West", -53f, 0f, 12f, 5f, 0.3f, wall);
            BuildWallAlongZ(zone, "CouncilWall_East", -39f, 0f, 12f, 5f, 0.3f, wall, -3f, 3f);
            CreateLabel(zone, "COUNCIL CHAMBER", new Vector3(-46f, 4.25f, 5.7f), Vector3.zero, 0.35f, new Color(0.12f, 0.23f, 0.30f), 7f);
            CreateLabel(zone, "PUBLIC MEETINGS • COMMUNITY VOICE • SHARED DECISIONS", new Vector3(-46f, 3.65f, 5.7f), Vector3.zero, 0.15f, new Color(0.35f, 0.35f, 0.32f), 9f);

            CreateBox(zone, "CouncilDais", new Vector3(-46f, 0.17f, 3.5f), new Vector3(7.2f, 0.34f, 2.4f), Palette.DenimBlue);
            var podium = CreateBox(zone, "PublicCommentPodium", new Vector3(-46f, 0.92f, 3.55f), new Vector3(0.9f, 1.3f, 0.65f), Palette.WarmSand);
            CreateLabel(podium.transform, "PUBLIC\nCOMMENT", new Vector3(0f, 0.13f, 0.34f), Vector3.zero, 0.13f, new Color(0.12f, 0.23f, 0.30f), 0.8f);
            CreateBox(zone, "CouncilPresentationScreen", new Vector3(-46f, 3.25f, 5.72f), new Vector3(5.4f, 2.6f, 0.08f), new Color(0.15f, 0.26f, 0.31f));
            CreateBox(zone, "CouncilPresentation", new Vector3(-46f, 3.25f, 5.66f), new Vector3(4.9f, 2.1f, 0.025f), new Color(0.83f, 0.87f, 0.83f));
            CreateLabel(zone, "TODAY'S COMMUNITY PLAN", new Vector3(-46f, 3.45f, 5.62f), Vector3.zero, 0.19f, new Color(0.12f, 0.23f, 0.30f), 4.5f);

            for (int i = 0; i < 5; i++)
            {
                float x = -50f + i * 2f;
                CreateBox(zone, "CouncilDesk", new Vector3(x, 0.8f, 1.05f), new Vector3(1.7f, 0.16f, 0.72f), new Color(0.48f, 0.34f, 0.23f));
                CreateBox(zone, "CouncilNameplate", new Vector3(x, 0.93f, 0.7f), new Vector3(0.9f, 0.18f, 0.04f), Palette.SoftWhite);
                CreateLabel(zone, i == 2 ? "CHAIR" : "COUNCIL", new Vector3(x, 0.94f, 0.67f), Vector3.zero, 0.09f, new Color(0.12f, 0.23f, 0.30f), 0.8f);
                CreateBox(zone, "CouncilChairSeat", new Vector3(x, 0.48f, 0.05f), new Vector3(0.72f, 0.12f, 0.72f), Palette.CharcoalTrim);
                CreateBox(zone, "CouncilChairBack", new Vector3(x, 0.9f, -0.25f), new Vector3(0.72f, 0.82f, 0.12f), Palette.CharcoalTrim);
            }

            foreach (float z in new[] { -2.4f, -4.1f })
            foreach (float x in new[] { -50f, -47.3f, -44.7f, -42f })
            {
                CreateBox(zone, "AudienceSeat", new Vector3(x, 0.42f, z), new Vector3(0.95f, 0.12f, 0.82f), Palette.WarmSand);
                CreateBox(zone, "AudienceSeatBack", new Vector3(x, 0.78f, z - 0.32f), new Vector3(0.95f, 0.62f, 0.12f), Palette.WarmSand);
            }

            var planBoard = CreateBox(zone, "CommunityPlanBoard", new Vector3(-52.72f, 2.25f, -1.5f), new Vector3(0.08f, 2.4f, 4.2f), Palette.SlateBlueGray);
            CreateLabel(zone, "COMMUNITY PLAN\n\nSafe routes to school\nAccessible sidewalks\nParks for every neighborhood\nRegular public updates", new Vector3(-52.64f, 2.25f, -1.5f), new Vector3(0f, 90f, 0f), 0.19f, Color.white, 3.8f);
            var planTrigger = planBoard.AddComponent<CivicExhibitTrigger>();
            planTrigger.sourceType = CivicExhibitTrigger.SourceType.CivicExhibit;
            planTrigger.entryId = "council_community_plan";
            AddInteractionMarker(planBoard.transform);

            BuildPottedPlant(zone, "CouncilPlant", new Vector3(-40.5f, 0f, 4.4f));
            BuildPottedPlant(zone, "CouncilPlant", new Vector3(-51.5f, 0f, 4.4f));
            BuildCivicArtwork(zone, "CouncilArtwork", new Vector3(-46f, 2.65f, -5.72f), "A PUBLIC TRUST", Palette.TealAccent, 2.8f);
        }

        private static void BuildMayorOfficeWing(Transform root)
        {
            var zone = new GameObject("MayorOfficeWing").transform;
            zone.SetParent(root, false);
            var wall = new Color(0.949f, 0.937f, 0.894f);
            var floor = new Color(0.86f, 0.82f, 0.73f);
            CreateGroundPlane(zone, "MayorCorridorFloor", new Vector3(35f, 0f, 0f), 8f, 6f, floor);
            BuildInteriorCeiling(zone, "MayorCorridor", new Vector3(35f, 0f, 0f), 8f, 6f);
            BuildWallAlongX(zone, "MayorCorridorWall_N", 35f, 3f, 8f, 5f, 0.3f, wall);
            BuildWallAlongX(zone, "MayorCorridorWall_S", 35f, -3f, 8f, 5f, 0.3f, wall);

            CreateGroundPlane(zone, "MayorOfficeFloor", new Vector3(46f, 0f, 0f), 14f, 12f, floor);
            BuildInteriorCeiling(zone, "MayorOffice", new Vector3(46f, 0f, 0f), 14f, 12f);
            BuildInteriorLight(zone, "MayorOfficeLight", new Vector3(46f, 4.6f, 0f), 14f);
            BuildWallAlongX(zone, "MayorOfficeWall_N", 46f, 6f, 14f, 5f, 0.3f, wall);
            BuildWallAlongX(zone, "MayorOfficeWall_S", 46f, -6f, 14f, 5f, 0.3f, wall);
            BuildWallAlongZ(zone, "MayorOfficeWall_E", 53f, 0f, 12f, 5f, 0.3f, wall);
            BuildWallAlongZ(zone, "MayorOfficeWall_W", 39f, 0f, 12f, 5f, 0.3f, wall, -3f, 3f);
            CreateLabel(zone, "MAYOR'S OFFICE", new Vector3(46f, 4.25f, 5.7f), Vector3.zero, 0.38f, new Color(0.12f, 0.23f, 0.30f), 6f);
            CreateLabel(zone, "A PLACE TO LISTEN, PLAN, AND FOLLOW THROUGH", new Vector3(46f, 3.65f, 5.7f), Vector3.zero, 0.15f, new Color(0.35f, 0.35f, 0.32f), 8f);

            CreateBox(zone, "MayorDesk", new Vector3(47f, 0.66f, 2.5f), new Vector3(4.2f, 1.32f, 1.25f), new Color(0.48f, 0.34f, 0.23f));
            CreateBox(zone, "MayorDeskNameplate", new Vector3(47f, 1.36f, 1.82f), new Vector3(1.55f, 0.25f, 0.06f), Palette.SoftWhite);
            CreateLabel(zone, "MAYOR", new Vector3(47f, 1.38f, 1.77f), Vector3.zero, 0.14f, new Color(0.12f, 0.23f, 0.30f), 1.3f);
            var mayorMonitor = new GameObject("MayorComputer").transform;
            mayorMonitor.SetParent(zone, false);
            mayorMonitor.localPosition = new Vector3(47f, 1.38f, 2.35f);
            BuildMonitor(mayorMonitor, "MayorMonitor", Vector3.zero);
            CreateBox(zone, "MayorChairSeat", new Vector3(47f, 0.48f, 3.65f), new Vector3(0.85f, 0.14f, 0.85f), Palette.DenimBlue);
            CreateBox(zone, "MayorChairBack", new Vector3(47f, 1f, 3.98f), new Vector3(0.85f, 1f, 0.14f), Palette.DenimBlue);
            foreach (float x in new[] { 44.8f, 49.2f })
            {
                CreateBox(zone, "ResidentVisitorChair", new Vector3(x, 0.42f, 0.6f), new Vector3(0.95f, 0.12f, 0.86f), Palette.WarmSand);
                CreateBox(zone, "ResidentVisitorChairBack", new Vector3(x, 0.78f, 0.93f), new Vector3(0.95f, 0.64f, 0.12f), Palette.WarmSand);
            }

            CreateBox(zone, "MayorBookcase", new Vector3(51.8f, 1.15f, -1.5f), new Vector3(1.35f, 2.3f, 3.2f), new Color(0.42f, 0.29f, 0.19f));
            for (int shelf = 0; shelf < 3; shelf++)
            {
                float y = 0.55f + shelf * 0.62f;
                CreateBox(zone, "BookcaseShelf", new Vector3(51.1f, y, -1.5f), new Vector3(0.08f, 0.08f, 3.05f), Palette.WarmSand);
                for (int book = 0; book < 5; book++)
                    CreateBox(zone, "CivicReferenceBook", new Vector3(51.0f, y + 0.24f, -2.65f + book * 0.52f), new Vector3(0.25f, 0.42f, 0.34f), book % 2 == 0 ? Palette.DenimBlue : Palette.BurntOrange);
            }
            CreateBox(zone, "FilingCabinet", new Vector3(40.5f, 0.76f, -4.5f), new Vector3(1.15f, 1.52f, 0.75f), Palette.SlateBlueGray);
            for (int drawer = 0; drawer < 3; drawer++)
            {
                float y = 0.35f + drawer * 0.42f;
                CreateBox(zone, "FilingDrawer", new Vector3(40.5f, y, -4.1f), new Vector3(0.95f, 0.32f, 0.04f), Palette.SoftWhite);
                CreateBox(zone, "DrawerHandle", new Vector3(40.5f, y, -4.04f), new Vector3(0.22f, 0.035f, 0.025f), Palette.CharcoalTrim);
            }

            BuildCivicArtwork(zone, "MayorPortrait", new Vector3(43f, 2.75f, 5.72f), "MAYOR • COMMUNITY LISTENER", Palette.TealAccent, 2.5f);
            BuildCivicArtwork(zone, "MayorCertificate", new Vector3(49.5f, 2.75f, 5.72f), "PUBLIC SERVICE", Palette.MustardGold, 2f);
            BuildPottedPlant(zone, "MayorOfficePlant", new Vector3(40.5f, 0f, 4.5f));
            BuildPottedPlant(zone, "MayorOfficePlant", new Vector3(51.5f, 0f, 4.5f));
            _mayorOfficeSpawns.Add(MakeSpawnPoint(zone, "mayor_01", 47f, 4.55f, 180f));
        }

        private static void AddInteractionMarker(Transform target)
        {
            var prompt = target.gameObject.AddComponent<InteractionPrompt>();
            var icon = CreateSphere(target, "IconMarker", new Vector3(0f, 1.55f, -0.1f), 0.09f, Palette.GoldenYellow, markStatic: false);
            Object.DestroyImmediate(icon.GetComponent<Collider>());
            icon.SetActive(false);
            prompt.iconRoot = icon.transform;
        }

        private static readonly (string id, string text)[] ReflectionQuestions =
        {
            ("reflect_between_elections", "What does \"democracy\" mean to you between elections, not just on voting day?"),
            ("reflect_listened_to", "Think of a time someone in your community actually listened before offering an opinion. What did that feel like?"),
            ("reflect_your_role", "What is one small, ongoing way you could stay involved in your own neighborhood this year?"),
            ("reflect_trust", "Trust between neighbors and local government is built slowly, over many small moments. What builds it — or breaks it — where you live?"),
        };

        private static void BuildReflectionWallWing(Transform root)
        {
            var zone = new GameObject("ReflectionWallWing").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            CreateGroundPlane(zone, "CorridorFloor", new Vector3(-7f, 0f, -19f), 4f, 6f, floorColor);
            BuildInteriorCeiling(zone, "ReflectionCorridor", new Vector3(-7f, 0f, -19f), 4f, 6f);
            BuildWallAlongX(zone, "CorridorWall_N", -7f, -16f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "CorridorWall_S", -7f, -22f, 4f, 5f, 0.3f, wallColor);

            CreateGroundPlane(zone, "Floor", new Vector3(-16f, 0f, -19f), 14f, 12f, floorColor);
            BuildInteriorCeiling(zone, "ReflectionWing", new Vector3(-16f, 0f, -19f), 14f, 12f);
            BuildInteriorLight(zone, "ReflectionGalleryLight", new Vector3(-16f, 4.6f, -19f), 14f);
            BuildWallAlongX(zone, "Wall_North", -16f, -13f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "Wall_South", -16f, -25f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_West", -23f, -19f, 12f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_East", -9f, -19f, 12f, 5f, 0.3f, wallColor, -22f, -16f);

            CreateLabel(zone, "REFLECTION WALL", new Vector3(-16f, 4.3f, -13.4f), Vector3.zero, 0.32f, new Color(0.122f, 0.227f, 0.302f), 6f);
            CreateLabel(zone, "Take a moment with each question before moving on.", new Vector3(-16f, 3.6f, -13.4f), Vector3.zero, 0.16f, new Color(0.35f, 0.35f, 0.32f), 6f);

            // Simple wall poster (2026-07-23) on the Wall_South, ~1.5m clear of the four
            // reflection stations along z=-23.
            BuildWallPoster(zone, "Poster_PauseReflect", new Vector3(-16f, 1.8f, -24.7f),
                "PAUSE &\nREFLECT", "Take a moment before you move on.", Palette.DenimBlue);

            // One ReflectionStation per question (see that file) — read-at-your-pace like a civic
            // exhibit, but marks itself reflected-on (PlayerPrefs-persisted) the first time it's read.
            float[] xs = { -20.5f, -17.5f, -14.5f, -11.5f };
            for (int i = 0; i < ReflectionQuestions.Length; i++)
            {
                var (id, text) = ReflectionQuestions[i];
                var g = new GameObject("Station_" + id).transform;
                g.SetParent(zone, false);
                g.localPosition = new Vector3(xs[i], 0f, -23f);

                var plinth = CreateBox(g, "Plinth", new Vector3(0f, 0.5f, 0f), new Vector3(0.9f, 1f, 0.5f), new Color(0.176f, 0.29f, 0.369f));
                var checkIconGO = CreateSphere(g, "CheckIcon", new Vector3(0f, 1.15f, 0.26f), 0.12f, new Color(0.55f, 0.55f, 0.58f), markStatic: false);
                Object.DestroyImmediate(checkIconGO.GetComponent<Collider>());

                // Attached to `plinth` (not the empty `g` root) specifically so ReflectionStation's
                // [RequireComponent(typeof(BoxCollider))] resolves to the properly-sized collider
                // CreateBox already gave it, instead of Unity silently auto-adding a default 1x1x1
                // box centered on empty `g`'s own origin (half of which would sit underground) —
                // same established pattern as every exhibit kiosk's trigger living on its `screenFace`.
                var station = plinth.AddComponent<ReflectionStation>();
                station.questionId = id;
                station.questionText = text;
                station.checkIcon = checkIconGO.GetComponent<MeshRenderer>();

                var prompt = plinth.AddComponent<InteractionPrompt>();
                var promptIcon = CreateSphere(g, "IconMarker", new Vector3(0f, 1.8f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
                Object.DestroyImmediate(promptIcon.GetComponent<Collider>());
                promptIcon.SetActive(false);
                prompt.iconRoot = promptIcon.transform;
            }

            BuildPottedPlant(zone, "ReflectionPlant", new Vector3(-21.5f, 0f, -14.2f));
            BuildPottedPlant(zone, "ReflectionPlant", new Vector3(-10.5f, 0f, -14.2f));
            BuildCivicArtwork(zone, "ReflectionArt", new Vector3(-16f, 2.7f, -24.72f), "A COMMUNITY GROWS BY LISTENING", Palette.DenimBlue, 2.4f);
        }

        private static void BuildVolunteerDebriefLoungeWing(Transform root)
        {
            var zone = new GameObject("VolunteerDebriefLoungeWing").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            CreateGroundPlane(zone, "CorridorFloor", new Vector3(7f, 0f, -19f), 4f, 6f, floorColor);
            BuildInteriorCeiling(zone, "VolunteerCorridor", new Vector3(7f, 0f, -19f), 4f, 6f);
            BuildWallAlongX(zone, "CorridorWall_N", 7f, -16f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "CorridorWall_S", 7f, -22f, 4f, 5f, 0.3f, wallColor);

            CreateGroundPlane(zone, "Floor", new Vector3(16f, 0f, -19f), 14f, 12f, floorColor);
            BuildInteriorCeiling(zone, "VolunteerLounge", new Vector3(16f, 0f, -19f), 14f, 12f);
            BuildInteriorLight(zone, "VolunteerLoungeLight", new Vector3(16f, 4.6f, -19f), 14f);
            BuildWallAlongX(zone, "Wall_North", 16f, -13f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongX(zone, "Wall_South", 16f, -25f, 14f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_East", 23f, -19f, 12f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_West", 9f, -19f, 12f, 5f, 0.3f, wallColor, -22f, -16f);

            CreateLabel(zone, "VOLUNTEER DEBRIEF LOUNGE", new Vector3(16f, 4.3f, -13.4f), Vector3.zero, 0.28f, new Color(0.122f, 0.227f, 0.302f), 8f);

            // Simple wall poster (2026-07-23) on the Wall_South, clear of the lounge
            // chairs/coffee table clustered around z=-18..-21.5.
            BuildWallPoster(zone, "Poster_ThankAVolunteer", new Vector3(16f, 1.8f, -24.7f),
                "THANK A\nVOLUNTEER", "Off-season canvassing keeps neighborhoods connected.", Palette.BurntOrange);

            // Casual seating, reusing the Lobby's WaitingChair box-prop pattern, plus the two
            // volunteer NPCs (new roster entries volunteer_maria_01/volunteer_sam_01, each with a
            // canvassing-debrief conversation carrying its own educational choices — same
            // DialogueChoice mechanism as the outdoor zone NPCs).
            foreach (var p in new[] { new Vector2(13f, -20f), new Vector2(15f, -21.5f), new Vector2(18f, -21f), new Vector2(19.5f, -18.5f) })
                CreateBox(zone, "LoungeChair", new Vector3(p.x, 0.35f, p.y), new Vector3(1f, 0.1f, 1f), new Color(0.541f, 0.42f, 0.271f));
            CreateBox(zone, "CoffeeTable", new Vector3(16f, 0.25f, -20f), new Vector3(1.4f, 0.5f, 1.4f), new Color(0.478f, 0.357f, 0.235f));
            CreateBox(zone, "LoungeRug", new Vector3(16f, 0.015f, -20f), new Vector3(9f, 0.025f, 7f), new Color(0.72f, 0.67f, 0.56f));
            BuildPottedPlant(zone, "LoungePlant", new Vector3(10.5f, 0f, -14.5f));
            BuildPottedPlant(zone, "LoungePlant", new Vector3(21.5f, 0f, -14.5f));
            BuildCivicArtwork(zone, "LoungeArt", new Vector3(16f, 2.7f, -24.72f), "THANK YOU, VOLUNTEERS", Palette.BurntOrange, 2.4f);

            _volunteerLoungeSpawns.Add(MakeSpawnPoint(zone, "volunteer_maria_01", 13.5f, -18.5f, 220f));
            _volunteerLoungeSpawns.Add(MakeSpawnPoint(zone, "volunteer_sam_01", 19f, -22.5f, 40f));
        }

        private static void BuildFinalReflectionHall(Transform root)
        {
            var zone = new GameObject("FinalReflectionHall").transform;
            zone.SetParent(root, false);
            var wallColor = new Color(0.949f, 0.937f, 0.894f);
            var floorColor = new Color(0.902f, 0.886f, 0.831f);

            CreateGroundPlane(zone, "CorridorFloor", new Vector3(0f, 0f, -26f), 6f, 4f, floorColor);
            BuildInteriorCeiling(zone, "ReflectionHallCorridor", new Vector3(0f, 0f, -26f), 6f, 4f);
            BuildWallAlongZ(zone, "CorridorWall_W", -3f, -26f, 4f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "CorridorWall_E", 3f, -26f, 4f, 5f, 0.3f, wallColor);

            CreateGroundPlane(zone, "Floor", new Vector3(0f, 0f, -36f), 18f, 16f, floorColor);
            BuildInteriorCeiling(zone, "FinalReflectionHall", new Vector3(0f, 0f, -36f), 18f, 16f);
            BuildInteriorLight(zone, "FinalHallLight", new Vector3(0f, 4.6f, -36f), 18f);
            BuildWallAlongX(zone, "Wall_North", 0f, -28f, 18f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongX(zone, "Wall_South", 0f, -44f, 18f, 5f, 0.3f, wallColor, -3f, 3f);
            BuildWallAlongZ(zone, "Wall_West", -9f, -36f, 16f, 5f, 0.3f, wallColor);
            BuildWallAlongZ(zone, "Wall_East", 9f, -36f, 16f, 5f, 0.3f, wallColor);
            BuildPottedPlant(zone, "ReflectionHallPlant", new Vector3(-7f, 0f, -31f));
            BuildPottedPlant(zone, "ReflectionHallPlant", new Vector3(7f, 0f, -31f));
            BuildCivicArtwork(zone, "ReflectionHallArt", new Vector3(0f, 2.7f, -43.72f), "LISTEN • LEARN • PARTICIPATE", Palette.SageGreen, 3f);

            // The guiding message the whole Town Hall interior redesign brief was built around.
            CreateLabel(zone,
                "\"Democracy is more than elections.\nIt is built through listening,\nparticipation, and community involvement.\"",
                new Vector3(0f, 3.6f, -30f), Vector3.zero, 0.3f, new Color(0.122f, 0.227f, 0.302f), 12f);

            // Simple wall poster (2026-07-23) on the Wall_South segment, well clear of the exit
            // portal (x=-2) and Return-to-Main-Menu kiosk (x=2).
            BuildWallPoster(zone, "Poster_KeepShowingUp", new Vector3(-6f, 1.8f, -43.7f),
                "KEEP SHOWING\nUP", "Off-season canvassing is how community stays strong.", Palette.SageGreen);

            // Exit choice: "Return to Neighborhood" (a walk-through portal, the exact same
            // PortalTravel mechanism as the Lobby's own Portal_ToPlaza, wired in WirePortals below)
            // and "Return to Main Menu" (a deliberate click-to-interact kiosk, NOT a walk-through
            // trigger — see MainMenuReturnTrigger's own doc comment for why a full scene reload
            // shouldn't fire just from walking near it, and for why it reloads the scene rather
            // than loading a real title-screen scene that doesn't exist in this project).
            var exitPortal = new GameObject("Portal_ToNeighborhood").transform;
            exitPortal.SetParent(zone, false);
            exitPortal.localPosition = new Vector3(-2f, 0f, -43.5f);
            CreateBox(exitPortal, "FrameL", new Vector3(-1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateBox(exitPortal, "FrameR", new Vector3(1.2f, 1.2f, 0f), new Vector3(0.15f, 2.4f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateBox(exitPortal, "Lintel", new Vector3(0f, 2.4f, 0f), new Vector3(2.55f, 0.15f, 0.15f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            CreateLabel(exitPortal, "RETURN TO\nNEIGHBORHOOD", new Vector3(0f, 2.9f, 0f), Vector3.zero, 0.24f, new Color(0.122f, 0.227f, 0.302f), 3f);
            var exitPortalCollider = CreateBox(exitPortal, "TriggerVolume", new Vector3(0f, 1.2f, 0f), new Vector3(2.4f, 2.4f, 0.3f), new Color(1f, 0.847f, 0.451f), markStatic: false);
            exitPortalCollider.GetComponent<Renderer>().enabled = false;
            exitPortalCollider.GetComponent<Collider>().isTrigger = true; // see matching note on Portal_ToInterior/Portal_ToPlaza above
            _finalHallExitPortalMarker = exitPortalCollider;

            var menuKiosk = new GameObject("Kiosk_ReturnToMainMenu").transform;
            menuKiosk.SetParent(zone, false);
            menuKiosk.localPosition = new Vector3(2f, 0f, -43.5f);
            CreateBox(menuKiosk, "Stand", new Vector3(0f, 0.9f, 0f), new Vector3(0.8f, 1.8f, 0.5f), new Color(0.227f, 0.239f, 0.239f));
            var menuScreen = CreateBox(menuKiosk, "Screen", new Vector3(0f, 1.3f, 0.26f), new Vector3(0.6f, 0.9f, 0.05f), new Color(0.122f, 0.227f, 0.302f));
            CreateLabel(menuScreen.transform, "RETURN TO\nMAIN MENU", new Vector3(0f, 0f, 0.04f), Vector3.zero, 0.16f, new Color(0.624f, 0.847f, 1f), 0.55f);
            menuScreen.AddComponent<MainMenuReturnTrigger>();
            var menuPrompt = menuScreen.AddComponent<InteractionPrompt>();
            var menuIcon = CreateSphere(menuScreen.transform, "IconMarker", new Vector3(0f, 0.6f, 0f), 0.08f, new Color(1f, 0.85f, 0.3f), markStatic: false);
            Object.DestroyImmediate(menuIcon.GetComponent<Collider>());
            menuIcon.SetActive(false);
            menuPrompt.iconRoot = menuIcon.transform;
        }

        // ==============================================================
        // NPC spawners — one per zone, wired to the spawn point lists
        // built while constructing that zone's geometry above.
        // ==============================================================
        private static void BuildNPCSpawners()
        {
            var spawnersRoot = new GameObject("NPCSpawners").transform;
            spawnersRoot.SetParent(_townRoot.transform, false);

            CreateSpawner(spawnersRoot, "ResidentialSpawner", _residentialSpawns);
            CreateSpawner(spawnersRoot, "CommercialSpawner", _commercialSpawns);
            CreateSpawner(spawnersRoot, "PublicRowSpawner", _publicRowSpawns);
            // PlazaSpawner gets a keep-clear anchor at the City Hall entrance portal: every one of
            // the 5 plaza NPCs' spawn points sits within their own 9m wander radius of the portal
            // (closest is elder_01 at ~6m away — all five can reach it), so with no exclusion,
            // ambient wander could park any of them right in the doorway, which reads exactly like
            // "something is blocking the entrance." keepClearRadius=3.5 keeps the threshold itself
            // clear while leaving the rest of the plaza's 9m wander area untouched.
            CreateSpawner(spawnersRoot, "PlazaSpawner", _plazaSpawns,
                keepClearAnchor: _interiorPortalMarker != null ? _interiorPortalMarker.transform : null, keepClearRadius: 3.5f);

            // 2026-07-23 Town Hall interior redesign: parented under _interiorRoot, NOT the
            // spawnersRoot above (which lives under _townRoot) — this NPCSpawner GameObject itself
            // needs to be deactivated/reactivated along with the rest of the interior via
            // PortalTravel's SetActive toggling, the same reason every other piece of interior
            // geometry is parented under _interiorRoot instead of _townRoot. Parenting these under
            // the town's always-active spawnersRoot would leave the Lobby/Lounge NPCs permanently
            // active (running AI/NavMesh work) even while the player is out in the plaza.
            var interiorSpawnersRoot = new GameObject("NPCSpawners").transform;
            interiorSpawnersRoot.SetParent(_interiorRoot.transform, false);
            CreateSpawner(interiorSpawnersRoot, "LobbySpawner", _lobbySpawns);
            CreateSpawner(interiorSpawnersRoot, "VolunteerLoungeSpawner", _volunteerLoungeSpawns);
            CreateSpawner(interiorSpawnersRoot, "MayorOfficeSpawner", _mayorOfficeSpawns);
        }

        private static void CreateSpawner(Transform parent, string name, List<NPCSpawner.SpawnEntry> entries, Transform keepClearAnchor = null, float keepClearRadius = 3f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var spawner = go.AddComponent<NPCSpawner>();
            spawner.defaultCitizenPrefab = _citizenPrefab;
            spawner.spawnPoints = new List<NPCSpawner.SpawnEntry>(entries);
            spawner.keepClearAnchor = keepClearAnchor;
            spawner.keepClearRadius = keepClearRadius;
        }

        // ==============================================================
        // Player rig + dialogue/exhibit UI
        // ==============================================================
        private static void BuildPlayerRig()
        {
            var rig = new GameObject("PlayerRig");
            rig.tag = "Player"; // one of Unity's built-in default tags
            rig.transform.position = new Vector3(0f, 0f, 46f);
            rig.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // face south, down the street toward City Hall

            var characterController = rig.AddComponent<CharacterController>();
            characterController.height = 1.8f;
            characterController.radius = 0.4f;
            characterController.center = new Vector3(0f, 0.9f, 0f);
            characterController.skinWidth = 0.04f;

            // BUG FOUND 2026-07-22, after the portal-trigger fix still didn't work: Unity's physics
            // engine only processes trigger overlaps for a collider pair where at least one side has
            // a Rigidbody attached — a bare CharacterController with no Rigidbody on the same object
            // can fail to raise OnTriggerEnter on a static trigger volume at all, even though the
            // trigger itself is correctly configured. Kinematic + no gravity so this doesn't add any
            // physics-driven movement of its own (CharacterController.Move() already does all actual
            // movement) — it exists purely so the physics engine has a Rigidbody to pair against.
            var rigidbody = rig.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;

            var camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            camGO.transform.SetParent(rig.transform, false);
            camGO.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var mainCam = camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();
            BuildOcclusionProofUICamera(mainCam); // see UILayer.cs — fixes UI hidden behind world geometry
            var interactor = camGO.AddComponent<PlayerInteractor>();
            interactor.rayOrigin = camGO.transform;
            var interactionHint = camGO.AddComponent<InteractionFocusUI>();
            interactionHint.controlHint = "CLICK / SPACE / A / TAP";

            // WASD + mouselook so Play mode is actually walkable in the Editor —
            // BuildPlayerRig previously only wired up the camera + click-to-interact,
            // with nothing that translated the rig.
            var movement = rig.AddComponent<PlayerMovement>();
            movement.cameraTransform = camGO.transform;

            _playerRigTransform = rig.transform;
            _mainCameraTransform = camGO.transform;

            if (_performanceLODManager != null) _performanceLODManager.player = camGO.transform;
            if (_timeOfDayManager != null)
            {
                _timeOfDayManager.sunLight = _sunLight;
                var bulbs = new List<Renderer>();
                foreach (var r in _townRoot.GetComponentsInChildren<Renderer>(true))
                    if (r.gameObject.name == "Bulb") bulbs.Add(r);
                _timeOfDayManager.emissiveAtNight = bulbs;
            }

            BuildDialogueUI(camGO.transform);
            BuildInfoPanelUI(camGO.transform);
            BuildIntroBriefing();
            BuildCrosshair(rig.transform, interactor);

            var xrRig = BuildXRRig(rig.transform.position, rig.transform.rotation);
            if (_systemsRoot != null)
            {
                var rigSelector = _systemsRoot.AddComponent<RigModeSelector>();
                rigSelector.desktopRig = rig;
                rigSelector.xrRig = xrRig;
            }
        }

        // ==============================================================
        // Quest / OpenXR rig. Built alongside (not instead of) the desktop rig
        // above — RigModeSelector (on the Systems object) enables whichever one
        // is appropriate at runtime, since the same Town.unity scene is used for
        // both Editor desktop play-testing and the eventual Quest build.
        //
        // Deliberately does not use XR Interaction Toolkit's XR Origin / Action-
        // Based Controller / locomotion-provider components: those classes'
        // exact fields have shifted across XRI 2.x point releases, and the
        // precise installed version couldn't be confirmed without the Editor.
        // Everything below instead uses TrackedPoseDriver bound to generic
        // <XRHMD>/<XRController> device paths (stable since Input System 1.5),
        // plus the project's own already-verified CharacterController-based
        // movement pattern (see PlayerMovement.cs) and PlayerInteractor's
        // existing raycast-interact logic — smaller surface, and any binding-
        // path mistake fails soft (reads as zero / never true) instead of
        // crashing or silently mistracking.
        // ==============================================================
        private static GameObject BuildXRRig(Vector3 spawnPosition, Quaternion spawnRotation)
        {
            var xrRig = new GameObject("XRRig");
            // Tagged "Player" to match the desktop rig — PortalTravel's new OnTriggerEnter checks
            // for this tag (see PortalTravel.cs), and both rigs safely share the tag the same way
            // both cameras already share "MainCamera" (only one rig is ever active at a time).
            xrRig.tag = "Player";
            xrRig.transform.position = spawnPosition;
            xrRig.transform.rotation = spawnRotation;

            var xrController = xrRig.AddComponent<CharacterController>();
            xrController.height = 1.8f;
            xrController.radius = 0.4f;
            xrController.center = new Vector3(0f, 0.9f, 0f);
            xrController.skinWidth = 0.04f;

            // See matching note on the desktop rig's CharacterController above — same fix, same reason.
            var xrRigidbody = xrRig.AddComponent<Rigidbody>();
            xrRigidbody.isKinematic = true;
            xrRigidbody.useGravity = false;

            var headGO = new GameObject("Head");
            headGO.transform.SetParent(xrRig.transform, false);
            // BUG FOUND 2026-07-22 (while adding the intro briefing below): nothing
            // tagged this camera "MainCamera", so Camera.main resolves to null
            // whenever the XR rig is the active one (RigModeSelector disables the
            // desktop rig's camera, which is the only one carrying this tag) —
            // silently breaking anything that reads Camera.main (InteractionPrompt's
            // icon billboarding, PlayerInteractor's Reset() fallback, the new
            // IntroBriefingController below). Safe to tag both cameras the same way
            // since only one is ever active+enabled at a time — Camera.main just
            // resolves to whichever that is.
            headGO.tag = "MainCamera";
            var headCam = headGO.AddComponent<Camera>();
            headGO.AddComponent<AudioListener>();
            BuildOcclusionProofUICamera(headCam); // see UILayer.cs — fixes UI hidden behind world geometry
            var headPose = headGO.AddComponent<TrackedPoseDriver>();
            headPose.positionInput = new InputActionProperty(new InputAction(
                "XRHeadPosition", InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            headPose.rotationInput = new InputActionProperty(new InputAction(
                "XRHeadRotation", InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));

            var movement = xrRig.AddComponent<XRLocomotion>();
            movement.headTransform = headGO.transform;

            BuildXRController(xrRig.transform, isLeftHand: true);
            BuildXRController(xrRig.transform, isLeftHand: false);

            xrRig.SetActive(false); // RigModeSelector turns this on if an XR loader is actually active at runtime
            return xrRig;
        }

        private static void BuildXRController(Transform parent, bool isLeftHand)
        {
            string hand = isLeftHand ? "LeftHand" : "RightHand";
            var handGO = new GameObject(hand + "Controller");
            handGO.transform.SetParent(parent, false);

            var pose = handGO.AddComponent<TrackedPoseDriver>();
            pose.positionInput = new InputActionProperty(new InputAction(
                "XR" + hand + "Position", InputActionType.Value, "<XRController>{" + hand + "}/devicePosition", expectedControlType: "Vector3"));
            pose.rotationInput = new InputActionProperty(new InputAction(
                "XR" + hand + "Rotation", InputActionType.Value, "<XRController>{" + hand + "}/deviceRotation", expectedControlType: "Quaternion"));

            var interactor = handGO.AddComponent<PlayerInteractor>();
            interactor.rayOrigin = handGO.transform;
            var interactionHint = handGO.AddComponent<InteractionFocusUI>();
            interactionHint.controlHint = "PRESS CONTROLLER TRIGGER";

            var controllerInput = handGO.AddComponent<XRControllerInput>();
            controllerInput.interactor = interactor;
            controllerInput.isLeftHand = isLeftHand;
        }

        private static void BuildDialogueUI(Transform playerCamera)
        {
            var canvasGO = new GameObject("DialogueUI_Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            var cg = canvasGO.AddComponent<CanvasGroup>();
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            // 2026-07-22 governor/canvassing decision system, ROUND 2: originally the choice rows
            // lived on a completely separate canvas (DialogueChoicePanelController) positioned
            // independently below this one, specifically to avoid touching this panel's
            // already-tuned, user-confirmed layout. User confirmed the underlying choice/morale
            // logic works fine (keyboard 1/2/3 selection works) but could never SEE the separate
            // panel — rather than keep guessing at what's wrong with an entirely separate,
            // untested camera-relative canvas, the rows now live directly on THIS canvas instead,
            // reusing the exact rendering/occlusion/camera-tracking pipeline already proven
            // visible. Height grown 260->440 to fit them; offsetFromCamera compensated below so
            // the header/portrait/speaker/subtitle block's on-screen position is UNCHANGED — see
            // the math note next to that assignment.
            canvasRT.sizeDelta = new Vector2(800f, 440f);
            canvasGO.transform.localScale = Vector3.one * 0.0016f; // world-space canvas ~= 1.3m wide panel

            var bgImage = CreateGlassPanel(canvasGO.transform, new Color(0.06f, 0.07f, 0.06f, 0.78f));

            // Speaker portrait: a procedurally-colored circle + initial letter (real portrait art
            // would need the Editor to author/import), plus a small "current speaker" indicator
            // dot and an occupation line beneath the name — see DialogueUIController.ApplyLine.
            var portraitGO = new GameObject("Portrait");
            portraitGO.transform.SetParent(canvasGO.transform, false);
            var portraitImage = portraitGO.AddComponent<Image>();
            var portraitRT = portraitGO.GetComponent<RectTransform>();
            portraitRT.anchorMin = portraitRT.anchorMax = new Vector2(0f, 1f);
            portraitRT.pivot = new Vector2(0f, 1f);
            portraitRT.anchoredPosition = new Vector2(20f, -16f);
            portraitRT.sizeDelta = new Vector2(64f, 64f);

            var initialGO = new GameObject("PortraitInitial");
            initialGO.transform.SetParent(portraitGO.transform, false);
            var initialText = initialGO.AddComponent<TextMeshProUGUI>();
            initialText.fontSize = 28f;
            initialText.fontStyle = FontStyles.Bold;
            initialText.color = Color.white;
            initialText.alignment = TextAlignmentOptions.Center;
            var initialRT = initialGO.GetComponent<RectTransform>();
            initialRT.anchorMin = Vector2.zero; initialRT.anchorMax = Vector2.one; initialRT.offsetMin = Vector2.zero; initialRT.offsetMax = Vector2.zero;

            var dotGO = new GameObject("SpeakerIndicatorDot");
            dotGO.transform.SetParent(canvasGO.transform, false);
            var dotImage = dotGO.AddComponent<Image>();
            dotImage.color = Color.white;
            var dotRT = dotGO.GetComponent<RectTransform>();
            dotRT.anchorMin = dotRT.anchorMax = new Vector2(0f, 1f);
            dotRT.pivot = new Vector2(0.5f, 0.5f);
            dotRT.anchoredPosition = new Vector2(96f, -20f);
            dotRT.sizeDelta = new Vector2(12f, 12f);

            var speakerGO = new GameObject("SpeakerLabel");
            speakerGO.transform.SetParent(canvasGO.transform, false);
            var speakerText = speakerGO.AddComponent<TextMeshProUGUI>();
            speakerText.fontSize = 34f;
            speakerText.fontStyle = FontStyles.Bold;
            speakerText.color = new Color(0.9f, 0.85f, 0.6f);
            speakerText.alignment = TextAlignmentOptions.TopLeft;
            var speakerRT = speakerGO.GetComponent<RectTransform>();
            speakerRT.anchorMin = new Vector2(0f, 1f); speakerRT.anchorMax = new Vector2(1f, 1f);
            speakerRT.pivot = new Vector2(0.5f, 1f);
            speakerRT.anchoredPosition = new Vector2(112f, -14f);
            speakerRT.sizeDelta = new Vector2(-132f, 38f);

            var occupationGO = new GameObject("OccupationLabel");
            occupationGO.transform.SetParent(canvasGO.transform, false);
            var occupationText = occupationGO.AddComponent<TextMeshProUGUI>();
            occupationText.fontSize = 20f;
            occupationText.color = new Color(0.75f, 0.75f, 0.7f);
            occupationText.alignment = TextAlignmentOptions.TopLeft;
            var occupationRT = occupationGO.GetComponent<RectTransform>();
            occupationRT.anchorMin = new Vector2(0f, 1f); occupationRT.anchorMax = new Vector2(1f, 1f);
            occupationRT.pivot = new Vector2(0.5f, 1f);
            occupationRT.anchoredPosition = new Vector2(112f, -50f);
            occupationRT.sizeDelta = new Vector2(-132f, 26f);

            var subtitleGO = new GameObject("SubtitleLabel");
            subtitleGO.transform.SetParent(canvasGO.transform, false);
            var subtitleText = subtitleGO.AddComponent<TextMeshProUGUI>();
            subtitleText.fontSize = 30f;
            subtitleText.color = Color.white;
            subtitleText.alignment = TextAlignmentOptions.TopLeft;
            subtitleText.lineSpacing = 12f;
            var subtitleRT = subtitleGO.GetComponent<RectTransform>();
            subtitleRT.anchorMin = Vector2.zero; subtitleRT.anchorMax = Vector2.one;
            // Bottom inset grown 20->210 to make room for the choice rows below (see the block
            // right after this one) — offsetMax (top inset) is UNCHANGED, so the header/portrait/
            // speaker/occupation block above stays exactly where it already was.
            subtitleRT.offsetMin = new Vector2(24f, 210f); subtitleRT.offsetMax = new Vector2(-24f, -92f);

            // 2026-07-22 governor/canvassing decision system, ROUND 2 (embedded directly in this
            // canvas — see the sizeDelta comment above for why). Three selectable rows in the
            // bottom 190 units of the now-440-tall canvas, hidden by default and only shown by
            // DialogueUIController.ShowChoiceRows() when the current line actually has choices.
            // Bottom-anchored (pivot 0,0 origin) rather than the top-anchored scheme used
            // elsewhere in this method — simpler to reason about "N units up from the bottom
            // edge" for a group that's meant to sit flush with the panel's bottom, and it avoids
            // a real bug the previous (separate-canvas) version had: a top-pivoted rect with its
            // BoxCollider centered at local (0,0,0) has HALF the collider sitting above the
            // visible row instead of centered on it. Bottom pivot + center.y = sizeDelta.y/2 gets
            // the collider genuinely centered on what's drawn.
            var choiceSlots = new DialogueChoiceSlot[3];
            for (int i = 0; i < 3; i++)
            {
                var rowGO = new GameObject("ChoiceRow_" + i);
                rowGO.transform.SetParent(canvasGO.transform, false);
                var rowRT = rowGO.AddComponent<RectTransform>();
                rowRT.anchorMin = new Vector2(0f, 0f); rowRT.anchorMax = new Vector2(1f, 0f);
                rowRT.pivot = new Vector2(0.5f, 0f);
                rowRT.anchoredPosition = new Vector2(0f, 20f + (2 - i) * 62f);
                rowRT.sizeDelta = new Vector2(-48f, 54f);

                var rowBg = rowGO.AddComponent<Image>();
                rowBg.sprite = GetRoundedPanelSprite();
                rowBg.type = Image.Type.Sliced;
                rowBg.color = new Color(1f, 1f, 1f, 0.10f);

                var labelGO = new GameObject("Label");
                labelGO.transform.SetParent(rowGO.transform, false);
                var label = labelGO.AddComponent<TextMeshProUGUI>();
                label.fontSize = 22f;
                label.color = new Color(0.88f, 0.94f, 1f);
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.enableWordWrapping = true;
                var labelRT = labelGO.GetComponent<RectTransform>();
                labelRT.anchorMin = Vector2.zero; labelRT.anchorMax = Vector2.one;
                labelRT.offsetMin = new Vector2(16f, 4f); labelRT.offsetMax = new Vector2(-16f, -4f);

                var boxCollider = rowGO.AddComponent<BoxCollider>();
                boxCollider.size = new Vector3(752f, 54f, 20f);
                boxCollider.center = new Vector3(0f, 27f, 0f); // rect's actual visual center given pivot (0.5,0)

                var slot = rowGO.AddComponent<DialogueChoiceSlot>();
                slot.index = i;
                slot.label = label;
                rowGO.SetActive(false);
                choiceSlots[i] = slot;
            }

            var controller = canvasGO.AddComponent<DialogueUIController>();
            controller.canvasGroup = cg;
            controller.speakerLabel = speakerText;
            controller.occupationLabel = occupationText;
            controller.subtitleLabel = subtitleText;
            controller.portraitImage = portraitImage;
            controller.portraitInitialLabel = initialText;
            controller.speakerIndicatorDot = dotImage;
            controller.background = bgImage;
            controller.playerCamera = playerCamera;
            controller.choiceSlots = choiceSlots;
            // Panel grew from 260 to 440 units tall (180 more) with a CENTERED pivot (Unity's
            // default, never explicitly changed) — left alone, that growth would expand equally
            // in both directions and shift the header UP by half the added height (90 units *
            // 0.0016 world scale = 0.144m) relative to camera. Shifting offsetFromCamera.y down
            // by that exact amount keeps the panel's TOP edge — and therefore the header/
            // portrait/speaker block — at the identical on-screen position it was already
            // confirmed working at; only the BOTTOM extends further to fit the new choice rows.
            controller.offsetFromCamera = new Vector3(0f, -0.15f - 0.144f, 1.1f);
        }

        /// <summary>Desktop-only aim reticle — see CrosshairUI.cs's class comment for why this was
        /// added (PlayerInteractor's raycast had no visual aim indicator at all). Parented under the
        /// desktop rig root (not camGO) purely so RigModeSelector's SetActive(false) on the whole
        /// desktop rig in XR mode also hides this without any extra wiring.</summary>
        private static void BuildCrosshair(Transform desktopRigRoot, PlayerInteractor interactor)
        {
            var canvasGO = new GameObject("Crosshair_Canvas");
            canvasGO.transform.SetParent(desktopRigRoot, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();

            var dotGO = new GameObject("Dot");
            dotGO.transform.SetParent(canvasGO.transform, false);
            var image = dotGO.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.55f);
            var rt = dotGO.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(6f, 6f);

            var crosshair = canvasGO.AddComponent<CrosshairUI>();
            crosshair.interactor = interactor;
            crosshair.dot = image;
        }

        private static void BuildInfoPanelUI(Transform playerCamera)
        {
            var canvasGO = new GameObject("InfoPanel_Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            var cg = canvasGO.AddComponent<CanvasGroup>();
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(900f, 500f);
            canvasGO.transform.localScale = Vector3.one * 0.0018f;

            var bgImage = CreateGlassPanel(canvasGO.transform, new Color(0.98f, 0.97f, 0.95f, 0.92f));

            var kickerGO = new GameObject("KickerLabel");
            kickerGO.transform.SetParent(canvasGO.transform, false);
            var kickerText = kickerGO.AddComponent<TextMeshProUGUI>();
            kickerText.fontSize = 20f;
            kickerText.color = new Color(0.42f, 0.48f, 0.53f);
            kickerText.alignment = TextAlignmentOptions.TopLeft;
            var kickerRT = kickerGO.GetComponent<RectTransform>();
            kickerRT.anchorMin = new Vector2(0f, 1f); kickerRT.anchorMax = new Vector2(1f, 1f);
            kickerRT.pivot = new Vector2(0.5f, 1f);
            kickerRT.anchoredPosition = new Vector2(0f, -24f);
            kickerRT.sizeDelta = new Vector2(-48f, 30f);

            var titleGO = new GameObject("TitleLabel");
            titleGO.transform.SetParent(canvasGO.transform, false);
            var titleText = titleGO.AddComponent<TextMeshProUGUI>();
            titleText.fontSize = 36f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = new Color(0.12f, 0.23f, 0.3f);
            titleText.alignment = TextAlignmentOptions.TopLeft;
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f); titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f);
            titleRT.anchoredPosition = new Vector2(0f, -58f);
            titleRT.sizeDelta = new Vector2(-48f, 60f);

            var bodyGO = new GameObject("BodyLabel");
            bodyGO.transform.SetParent(canvasGO.transform, false);
            var bodyText = bodyGO.AddComponent<TextMeshProUGUI>();
            bodyText.fontSize = 26f;
            bodyText.color = new Color(0.14f, 0.14f, 0.11f);
            bodyText.alignment = TextAlignmentOptions.TopLeft;
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = Vector2.zero; bodyRT.anchorMax = Vector2.one;
            bodyRT.offsetMin = new Vector2(24f, 24f); bodyRT.offsetMax = new Vector2(-24f, -130f);

            var controller = canvasGO.AddComponent<InfoPanelController>();
            controller.canvasGroup = cg;
            controller.kickerLabel = kickerText;
            controller.titleLabel = titleText;
            controller.bodyLabel = bodyText;
            controller.background = bgImage;
            controller.playerCamera = playerCamera;
        }

        /// <summary>
        /// One-time "what is this and what am I supposed to do" briefing —
        /// added in response to user feedback that the experience's goal wasn't
        /// clear on its own. States it plainly, and is also the first place in
        /// the whole scaffold that names this project's actual stated purpose
        /// (canvassing during the off-season) — see IntroBriefingController.cs
        /// for why this doesn't take a playerCamera reference the way
        /// DialogueUI/InfoPanel above do (it reads Camera.main directly each
        /// frame instead, so it works correctly whichever rig — desktop or XR —
        /// ends up active).
        /// </summary>
        private static void BuildIntroBriefing()
        {
            var canvasGO = new GameObject("IntroBriefing_Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGO.AddComponent<CanvasScaler>();
            var canvasRT = canvasGO.GetComponent<RectTransform>();
            // 2026-07-22: enlarged from 560 to 700 to fit the fuller walkthrough below (now covers
            // the Governor/canvassing decision system too) without the text overflowing the panel.
            canvasRT.sizeDelta = new Vector2(900f, 700f);
            canvasGO.transform.localScale = Vector3.one * 0.0018f;

            var bg = new GameObject("Background");
            bg.transform.SetParent(canvasGO.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.08f, 0.1f, 0.09f, 0.9f);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;

            var titleGO = new GameObject("TitleLabel");
            titleGO.transform.SetParent(canvasGO.transform, false);
            var titleText = titleGO.AddComponent<TextMeshProUGUI>();
            titleText.text = "City Hall & Community Voices";
            titleText.fontSize = 34f;
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = new Color(0.9f, 0.85f, 0.6f);
            titleText.alignment = TextAlignmentOptions.Top;
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f); titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f);
            titleRT.anchoredPosition = new Vector2(0f, -28f);
            titleRT.sizeDelta = new Vector2(-48f, 56f);

            var bodyGO = new GameObject("BodyLabel");
            bodyGO.transform.SetParent(canvasGO.transform, false);
            var bodyText = bodyGO.AddComponent<TextMeshProUGUI>();
            // 2026-07-22: rewritten as a fuller scene walkthrough (was a 3-paragraph summary) —
            // now covers the Governor/canvassing decision system alongside the original resident/
            // City Hall guidance, with short bolded lead-ins (TMP rich text, richText defaults on)
            // so it reads as a scannable checklist rather than a wall of text. Accent color
            // (#E5D999) matches the title above for visual consistency.
            bodyText.text =
                "This town shows how local government stays responsive — not just on election day.\n\n" +
                "<b><color=#E5D999>TALK TO RESIDENTS</color></b> — aim at a person or exhibit to see one clear interaction hint. Use the controller trigger, mouse click, Space, gamepad A, or tap. Your choices affect neighborhood morale.\n\n" +
                "<b><color=#E5D999>KNOCK RESPECTFULLY</color></b> — approach a marked home, choose whether to knock, introduce yourself, and accept a resident's choice not to talk.\n\n" +
                "<b><color=#E5D999>VISIT THE GOVERNOR</color></b> — their podium is in the City Hall plaza. Get a briefing, choose where canvassing should focus next, and check the Community Pulse board nearby to see how it's going.\n\n" +
                "<b><color=#E5D999>EXPLORE CITY HALL</color></b> — meet the Mayor, review the community plan in the Council Chamber, explore civic exhibits, and save reflections in the Reflection Hall.\n\n" +
                "Canvassing volunteers knock on doors during the off-season too, not just campaign season — it's one of the best ways residents stay connected to city government.\n\n" +
                "This message will disappear shortly, or move to continue.";
            bodyText.fontSize = 21f;
            bodyText.color = Color.white;
            bodyText.alignment = TextAlignmentOptions.TopLeft;
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = Vector2.zero; bodyRT.anchorMax = Vector2.one;
            bodyRT.offsetMin = new Vector2(28f, 24f); bodyRT.offsetMax = new Vector2(-28f, -96f);

            var briefing = canvasGO.AddComponent<IntroBriefingController>();
            // Bumped from the component's 16s default — the walkthrough is longer now, so anyone
            // standing still to actually read it gets more time before it auto-hides. Moving still
            // dismisses it immediately either way (see IntroBriefingController.Update()).
            briefing.autoHideSeconds = 22f;
        }

        // ==============================================================
        // Portals — wired last since both PortalTravel instances need
        // references to each other's area GameObject and the player rig.
        // ==============================================================
        private static void WirePortals()
        {
            if (_interiorPortalMarker != null)
            {
                var toInterior = _interiorPortalMarker.AddComponent<PortalTravel>();
                toInterior.areaToHide = _townRoot;
                toInterior.areaToShow = _interiorRoot;
                toInterior.playerRig = _playerRigTransform;
                toInterior.arrivalPosition = new Vector3(300f, 0f, 6f);
                toInterior.arrivalEulerRotation = new Vector3(0f, 180f, 0f);
            }
            if (_plazaPortalMarker != null)
            {
                var toPlaza = _plazaPortalMarker.AddComponent<PortalTravel>();
                toPlaza.areaToHide = _interiorRoot;
                toPlaza.areaToShow = _townRoot;
                toPlaza.playerRig = _playerRigTransform;
                toPlaza.arrivalPosition = new Vector3(0f, 0f, -42f);
                toPlaza.arrivalEulerRotation = Vector3.zero;
            }
            // 2026-07-23 Town Hall interior redesign: the Final Reflection Hall's "Return to
            // Neighborhood" exit — same destination/arrival as the Lobby's own toPlaza above
            // (walking out through either doorway lands you back at the same plaza spot), just a
            // second doorway reachable from much deeper in the building.
            if (_finalHallExitPortalMarker != null)
            {
                var toPlazaFromHall = _finalHallExitPortalMarker.AddComponent<PortalTravel>();
                toPlazaFromHall.areaToHide = _interiorRoot;
                toPlazaFromHall.areaToShow = _townRoot;
                toPlazaFromHall.playerRig = _playerRigTransform;
                toPlazaFromHall.arrivalPosition = new Vector3(0f, 0f, -42f);
                toPlazaFromHall.arrivalEulerRotation = Vector3.zero;
            }
        }

        // ==============================================================
        // NavMesh — one surface for the outdoor town, one for the lobby.
        // KNOWN RISK: the `Unity.AI.Navigation` namespace / NavMeshSurface
        // API matches the com.unity.ai.navigation package pinned in
        // Packages/manifest.json (1.1.5). If your installed Editor resolves
        // a different package version with a different namespace, this is
        // the method most likely to need a one-line fix.
        // ==============================================================
        private static void BuildNavMesh()
        {
            // NavMeshSurface.collectObjects = CollectObjects.Children walks the
            // children of whatever GameObject the NavMeshSurface component itself
            // is attached to — NOT the children of that GameObject's parent. The
            // previous version put the component on a brand-new leaf GameObject
            // parented alongside the actual town geometry (a sibling of the
            // ground/houses/sidewalks, not their parent), so it had zero children
            // of its own and the bake silently produced an empty NavMesh — every
            // NavMeshAgent then fails "not placed on a NavMesh" the moment
            // anything calls SetDestination/remainingDistance on it (previously
            // masked because NPCRoutineController never called either, until the
            // ambient-wander fallback started actually driving the agents). Fix:
            // add the component directly on _townRoot / _interiorRoot themselves
            // — the actual GameObjects every piece of town/interior geometry is
            // parented under — so Children mode has something to find.
            // useGeometry = PhysicsColliders (instead of the default RenderMeshes) — the console
            // was flooding with "Source mesh TextMeshPro Mesh has invalid vertex data and will be
            // skipped" (one per sign/label in town: shop signs, exhibit boards, the City Hall
            // sign, etc.) because RenderMeshes mode pulls in every MeshRenderer under Children,
            // including TextMeshPro's dynamically-generated (and NavMesh-unreadable) label meshes.
            // Every piece of geometry that should actually block/support pathing already has a
            // Collider — CreatePrimitive's own default collider for every CreateBox/CreateCylinder/
            // CreateSphere/CreateGroundPlane, the BoxCollider InstantiateAssetPrefab adds to real
            // asset-pack prefabs, and the CapsuleCollider added to citizens — while the label
            // GameObjects from CreateLabel() and the decorative IconMarker spheres deliberately have
            // no Collider (IconMarker even explicitly destroys the one CreatePrimitive gives it).
            // Switching to PhysicsColliders means NavMesh collection walks Colliders instead of
            // Renderers, so those non-colliding decorative objects are skipped by construction
            // instead of being pulled in and then rejected mesh-by-mesh.
            var townSurface = _townRoot.AddComponent<NavMeshSurface>();
            townSurface.collectObjects = CollectObjects.Children;
            townSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            townSurface.BuildNavMesh();

            var interiorSurface = _interiorRoot.AddComponent<NavMeshSurface>();
            interiorSurface.collectObjects = CollectObjects.Children;
            interiorSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            // Interior_Root starts inactive; NavMeshSurface.BuildNavMesh() only
            // walks active-in-hierarchy renderers, so bake with it active, then
            // restore the inactive state the rest of the scaffold expects.
            _interiorRoot.SetActive(true);
            interiorSurface.BuildNavMesh();
            _interiorRoot.SetActive(false);
        }
    }
}
