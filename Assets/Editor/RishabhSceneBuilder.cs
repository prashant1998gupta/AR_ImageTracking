using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.Video;
using Imagine.WebAR;
using Imagine.WebAR.Samples;
using System.Collections.Generic;

/// <summary>
/// Rishabh AR Visiting-Card Scene Builder — Rionick Studios
///
/// One-click generator for the "Rishabh" AR visiting card:
///   • Clones the proven Demo-VisitingCard.unity template (ARCamera + CustomEventSystem
///     + screen Canvas/ScanArea + Directional Light come for free)
///   • Strips the template's demo target and demo UI (Button Menu → StopTracker,
///     LogoAndLink → hard-coded CTA)
///   • Registers "Rishabh_Card" + Target Image Rishabh.png in ImageTrackerGlobalSettings
///   • Builds the tracked subtree: tracking quad → video plane → World Canvas of
///     tappable UI quads, laid out from the CONFIG table below
///   • Wires every link through Imagine.WebAR.Samples.GoToUrl (the WebGL-safe opener)
///   • Repoints the on-screen scan hint at the new card art
///   • Saves as Assets/Scenes_1/Rishabh.unity and adds it to Build Settings
///
/// Re-running is safe: the scene is deleted and re-cloned, meshes/materials are
/// reused-or-updated in place, and the global-target entry is add-or-update.
/// Nothing outside Assets/Scenes_1/Rishabh.unity, Assets/AR_Assets/Materials and
/// Assets/AR_Assets/Planes/Generated is ever written.
/// </summary>
public static class RishabhSceneBuilder
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ██  C O N F I G  ██   — everything tunable lives here.  Nudge, rebuild.
    // ═══════════════════════════════════════════════════════════════════════

    // ─── Scene / target identity ────────────────────────────────────────────
    private static readonly string TemplatePath = "Assets/Scenes_1/Demo-VisitingCard.unity";
    private static readonly string NewScenePath = "Assets/Scenes_1/Rishabh.unity";

    // ─── Asset locations ────────────────────────────────────────────────────
    private static readonly string SrcFolder     = "Assets/AR_Assets/Rishabh";
    private static readonly string MatFolder     = "Assets/AR_Assets/Materials";
    private static readonly string MeshFolder    = "Assets/AR_Assets/Planes/Generated";
    private static readonly string VideoClipPath = SrcFolder + "/Ar Rishabh Video.mp4";

    /// <summary>One tracked image. Every entry gets its OWN complete copy of the
    /// card content (video plane + the full UI layout), so scanning EITHER side of
    /// the printed card gives the identical experience. Both faces are 650 × 1040,
    /// so the layout numbers below apply unchanged to both.</summary>
    private class TargetDef
    {
        public string id;      // tracker id — must be unique project-wide
        public string image;   // file name inside SrcFolder
    }

    private static readonly List<TargetDef> Targets = new List<TargetDef>
    {
        new TargetDef { id = "Rishabh_Card",      image = "Target Image Rishabh.png" },
        new TargetDef { id = "Rishabh_Card_Back", image = "Target Backside Image Rishabh.png" },
    };

    // ─── Physical size ──────────────────────────────────────────────────────
    // The tracker normalises the target's WIDTH to 1.0 world unit, so the card is
    // 1.00 wide × (imgH/imgW) tall = 1.00 × 1.60.  Leave PhysicalWidth at 1.0.
    private static readonly float PhysicalWidth = 1.0f;

    // ─── CONTENT SCALE ─── the knob for "the AR is bigger than the card" ────
    // Multiplies EVERYTHING that is drawn — the video plane AND the whole
    // surround (frame, badges, icons, name, buttons) — about the card centre.
    // The invisible tracking quad stays at the card's true size, so it remains
    // the honest reference for what "1.0" means.
    //
    //   1.00 = the video exactly fills the tracking quad (what shipped first)
    //   0.80 = tested on device — still ~20 % too large
    //   0.64 = 0.80 × 0.8 — closer, still a touch too large
    //   0.63 = settled here on device                    ← current
    //
    // Precedent for < 1.0: every other campaign in this project scales its video
    // down rather than filling the quad — MemeHunt FIFA 0.755, One8 0.57 — and
    // the Demo-VisitingCard template's own content box measures 0.98 × 0.56 world
    // units. Nothing here is authored at full quad size except this card was.
    //
    // HOW TO TUNE (one number, then re-run the builder — nothing else changes):
    // it is a pure multiplier, so to take another X % off, multiply by (1 - X/100).
    //   a touch smaller → 0.60             10 % smaller → 0.57
    //   a touch bigger  → 0.66             back to the start → 0.80
    private static readonly float ContentScale = 0.63f;

    // ─── Video ──────────────────────────────────────────────────────────────
    // UseLocalVideoClip = true  → VideoSource.VideoClip, plays instantly in the Editor.
    // UseLocalVideoClip = false → VideoSource.Url + CDNARVideoController.cdnVideoUrl,
    //                             the streaming path used by the WebGL builds.
    // Default = CDN, because every shipped campaign in this repo streams its video
    // by URL (see CDNARVideoController) — that is the path proven to work in the
    // WebGL build. The clip is already copied to <projectRoot>/videos/, so the URL
    // below goes live as soon as that folder is committed and pushed.
    // Flip to true only for offline Editor testing.
    private static readonly bool   EnableVideo       = true;
    private static readonly bool   UseLocalVideoClip = false;
    private static readonly string VideoCdnUrl =
        "https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/Ar%20Rishabh%20Video.mp4";
    // Measured 2026-08-11: 674 × 1080 (h/w = 1.6024), 10.05 s.
    // The target image is 650 × 1040 (h/w = 1.6000) — the two match to within
    // 0.15 %, so the video covers the card with no stretch and no crop.
    private static readonly int    VideoWidthPx  = 674;
    private static readonly int    VideoHeightPx = 1080;

    // ─── Save Contact (vCard) ───────────────────────────────────────────────
    private static readonly string ContactFullName  = "Rishabh Rustagi";
    private static readonly string ContactLastName  = "Rustagi";
    private static readonly string ContactFirstName = "Rishabh";
    private static readonly string ContactOrg       = "Rionick Studios";
    private static readonly string ContactPhone     = "+918377935429";
    private static readonly string ContactEmail     = "contact@rionick.com";
    private static readonly string ContactSite      = "https://www.rionick.com";

    // The wizard/prefab convention: Save Contact is an ordinary GoToUrl button that
    // points at an externally hosted .vcf.  jsDelivr serves .vcf as a download, so the
    // AR tab survives and iOS Safari shows the "Add Contact" sheet.
    // WriteVCardFile drops the generated card at <projectRoot>/videos/ (the folder this
    // repo already publishes through jsDelivr) so the URL below goes live on push.
    private static readonly bool   WriteVCardFile     = true;
    private static readonly string VCardProjectPath   = "videos/RishabhRustagi.vcf";
    private static readonly string SaveContactVCardUrl =
        "https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/RishabhRustagi.vcf";

    // Fallback for "no hosting available": embeds the whole vCard in a data: URI.
    // WARNING — Chrome blocks top-level navigation to data: URLs, and GoToUrl on WebGL
    // is window.location.assign(), so this only works in the Editor / on iOS Safari.
    // Leave false unless you know the target browser accepts it.
    private static readonly bool UseDataUriVCard = false;

    // ─── Template clean-up ──────────────────────────────────────────────────
    // "Button Menu" calls ImageTracker.StopTracker() and freezes all tracking → always removed.
    // "LogoAndLink" is the demo CTA; UIManager.Start() hard-codes rionick.com/contact-us onto
    // it with no null-check, so removing the button also removes the UIManager component.
    private static readonly bool RemoveTemplateCtaButton = true;
    // The template's on-screen "point your camera at the card" prompt
    // (Canvas ▸ ScanArea ▸ CardImage + ScanToStart). true = delete it entirely and
    // clear the tracker's OnImageFound/OnImageLost listeners that toggled it.
    private static readonly bool RemoveScanArea          = true;
    private static readonly bool UpdateScanHintArt       = true;   // ignored when RemoveScanArea
    private static readonly float ScanHintHeightPx       = 420f;   // portrait card inside the scan frame

    // Build Settings: false = just append this scene (non-destructive, the default).
    // true = MemeHunt behaviour — make it the ONLY enabled scene (disables every other
    // campaign scene, so only flip this for a single-campaign WebGL build).
    private static readonly bool MakeOnlyEnabledBuildScene = false;

    // ─── Z CONVENTION ───────────────────────────────────────────────────────
    // Quad normals are Vector3.back and the camera looks down +Z, so MORE NEGATIVE Z
    // = CLOSER TO THE CAMERA.  Layers on the target root, front to back:
    //
    //     z = -0.020   World Canvas plane  → every UI quad (name, icons, buttons)
    //     z = -0.010   video plane         → 1.00 × 1.75, covers the whole card
    //     z =  0.000   tracking-image quad → Renderer is disabled by ImageTracker.Start(),
    //                                        it exists only because Start() requires one
    //     z = +0.005   Lines.png decoration → BEHIND the opaque video plane, so it is
    //                                        depth-clipped to the halo around the card
    //
    // UI elements live on the canvas plane (localZ 0).  Lines is pushed back by
    // (LinesWorldZ − CanvasWorldZ) / CanvasScaleConst canvas-pixels.
    private static readonly float CanvasScaleConst = 0.00072f;  // the shipped prefab's exact value
    private static readonly float CardWorldZ       =  0.000f;
    private static readonly float VideoWorldZ      = -0.010f;
    private static readonly float CanvasWorldZ     = -0.020f;
    private static readonly float LinesWorldZ      =  0.005f;

    // ─── LAYOUT ─────────────────────────────────────────────────────────────
    // dx, dy and width are all in CARD-WIDTH UNITS (1.0 = the full width of the
    // tracking image).  Origin is the card centre, +Y up, +X right.  Height is never
    // authored — it is always width × aspect, where aspect is the PNG's own
    // measured height/width, so nothing can be stretched.
    private class Element
    {
        public string name;         // GameObject name inside "World Canvas"
        public string sprite;       // file name inside SrcFolder
        public float  dx, dy;       // centre offset, card-width units
        public float  width;        // width, card-width units
        public float  aspect;       // measured PNG height / width
        public string url;          // null/empty = pure decoration (no Button, no raycast)
        public bool   vCard;        // true = Save Contact; url is resolved at build time
        public float  worldZ = float.NaN;  // NaN = sit on the canvas plane (CanvasWorldZ)
    }

    // Every value below was MEASURED from the client's reference render
    // "Rishabh AR Smart Card.png" (1920 × 1080) by scanning its pixels, not by eye:
    //   card in the reference = x 723→1175, y 156→947  →  453 px wide, centre (949, 551.5)
    //   so 1 card-width unit = 453 px, and dx = (cx-949)/453, dy = (551.5-cy)/453.
    // The reference was rendered with the OLD 1.75 card; the real target is now
    // 1.60 (650 × 1040). Positions are deliberately NOT rescaled vertically —
    // the surround (frame, badges, icons, right column) keeps its exact designed
    // proportions, and only the card itself is slightly shorter inside it.
    private static readonly List<Element> Layout = new List<Element>
    {
        // decoration first so it draws behind its siblings.
        // Measured frame extent: x 577→1403, y 167→821 (the left rule sits at
        // x≈583 with the two icons centred on it, the right rule at x≈1397).
        new Element { name = "linesBg",       sprite = "Lines.png",           dx =  0.090f, dy =  0.127f, width = 1.823f, aspect = 0.8686f, worldZ = LinesWorldZ },

        new Element { name = "rionickTop",    sprite = "Rionick.png",         dx =  0.000f, dy =  1.001f, width = 0.929f, aspect = 0.2781f, url = "https://www.rionick.com" },
        new Element { name = "ghar360",       sprite = "ghar360 Icon.png",    dx = -0.807f, dy =  0.549f, width = 0.470f, aspect = 1.0094f, url = "https://rionick.com/ghar360" },
        new Element { name = "arrise",        sprite = "ARRISE Icon.png",     dx = -0.807f, dy =  0.052f, width = 0.470f, aspect = 1.0189f, url = "https://ar.rionick.com/" },

        new Element { name = "name",          sprite = "Name.png",            dx =  1.012f, dy =  0.490f, width = 0.914f, aspect = 0.5080f },
        new Element { name = "letsConnect",   sprite = "lets connect.png",    dx =  1.007f, dy =  0.153f, width = 0.532f, aspect = 0.1948f },

        new Element { name = "linkedin",      sprite = "Linkedin.png",        dx =  0.737f, dy = -0.053f, width = 0.254f, aspect = 1.0177f, url = "https://www.linkedin.com/company/rionick-studios/" },
        new Element { name = "instagram",     sprite = "Instagram.png",       dx =  1.003f, dy = -0.053f, width = 0.252f, aspect = 1.0088f, url = "https://www.instagram.com/rionick.studios/" },
        new Element { name = "whatsapp",      sprite = "Whatsapp.png",        dx =  1.276f, dy = -0.053f, width = 0.249f, aspect = 1.0000f, url = "https://wa.me/918377935429" },

        new Element { name = "saveContact",   sprite = "Save Contact.png",    dx =  1.008f, dy = -0.357f, width = 0.817f, aspect = 0.2919f, vCard = true },
        new Element { name = "rionickBottom", sprite = "Rionick.png",         dx =  0.018f, dy = -1.019f, width = 0.466f, aspect = 0.2781f, url = "https://www.rionick.com" },
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  ██  E N D   C O N F I G  ██
    // ═══════════════════════════════════════════════════════════════════════

    private const int UILayer = 5;   // Unity built-in "UI" layer

    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Step 2 — run this ONLY when you are about to build the Rishabh WebGL build.
    ///
    /// Step 1 appends Rishabh.unity to Build Settings without touching anything else,
    /// so the MemeHunt event build stays exactly as it is. But Unity boots the FIRST
    /// ENABLED scene, and that is still MemeHunt — so a WebGL build right after step 1
    /// would launch MemeHunt. This makes Rishabh the only enabled scene.
    ///
    /// To go back to another campaign afterwards, re-enable its scene in
    /// File ▸ Build Settings (or re-run that campaign's own builder).
    /// </summary>
    [MenuItem("Tools/Rishabh/2. Make Rishabh The Only Build Scene")]
    public static void MakeOnlyBuildSceneMenu()
    {
        if (!System.IO.File.Exists(NewScenePath))
        {
            EditorUtility.DisplayDialog("Rishabh AR Card",
                "Build the scene first:\nTools ▸ Rishabh ▸ Build Rishabh Scene", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Rishabh AR Card",
                "Make Rishabh.unity the ONLY enabled scene in Build Settings?\n\n" +
                "Every other campaign scene (including MemeHunt) will be disabled — " +
                "re-enable them in File ▸ Build Settings when you build those again.",
                "Yes, Rishabh only", "Cancel"))
            return;

        SetAsOnlyEnabledScene(NewScenePath);
        EditorUtility.DisplayDialog("Rishabh AR Card",
            "Rishabh.unity is now the only enabled build scene.\n\n" +
            "File ▸ Build Settings ▸ WebGL ▸ Build (template iTracker).", "OK");
    }

    [MenuItem("Tools/Rishabh/Build Rishabh Scene", priority = 0)]
    public static void BuildSceneMenu()
    {
        try
        {
            string result = BuildScene();
            EditorUtility.DisplayDialog("Rishabh AR Card", result, "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Rishabh AR Card — Error", e.Message, "OK");
            Debug.LogException(e);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>Batch-mode entry point:
    /// Unity.exe -batchmode -projectPath ... -executeMethod RishabhSceneBuilder.BuildSceneBatch -quit</summary>
    public static void BuildSceneBatch()
    {
        try
        {
            string result = BuildScene();
            Debug.Log("[Rishabh] " + result);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[Rishabh] FAILED: " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            throw;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    private static string BuildScene()
    {
        // 0. Safety: save whatever is open
        if (!Application.isBatchMode && EditorSceneManager.GetActiveScene().isDirty)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new System.Exception("Cancelled — current scene has unsaved changes.");
        }

        // 1. Validate every asset the layout references before touching anything
        Progress("Validating assets", 0.05f);
        EnsureFolder("Assets/AR_Assets");
        EnsureFolder(MatFolder);
        EnsureFolder("Assets/AR_Assets/Planes");
        EnsureFolder(MeshFolder);

        if (AssetDatabase.LoadAssetAtPath<Object>(TemplatePath) == null)
            throw new System.Exception("Template scene not found: " + TemplatePath);

        // The AR tracker feature-extracts the target in the browser; this project's
        // NOTE: Read/Write is deliberately NOT forced on the source image. The
        // exporter (Imagine/ImageTracker/Scripts/Editor/PostProcessBuild.cs) copies
        // the PNG file itself into <build>/targets/ and the browser extracts the
        // features, so the importer flag is irrelevant — and leaving it alone keeps
        // the promise that this builder writes nothing outside the folders above.
        if (Targets == null || Targets.Count == 0)
            throw new System.Exception("No entries in the Targets list.");

        var targetTextures = new Dictionary<string, Texture2D>();
        var seenIds = new HashSet<string>();
        foreach (var def in Targets)
        {
            if (!seenIds.Add(def.id))
                throw new System.Exception("Duplicate target id in the Targets list: " + def.id);
            string p = SrcFolder + "/" + def.image;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            if (tex == null)
                throw new System.Exception("Missing tracking image: " + p);
            targetTextures[def.id] = tex;
        }

        // Scan-hint art (only used when the prompt is kept)
        string firstImgPath  = SrcFolder + "/" + Targets[0].image;
        Texture2D targetTex  = targetTextures[Targets[0].id];
        Sprite targetSprite  = AssetDatabase.LoadAssetAtPath<Sprite>(firstImgPath);

        var sprites = new Dictionary<string, Sprite>();
        foreach (var e in Layout)
        {
            if (sprites.ContainsKey(e.sprite)) continue;
            string p = SrcFolder + "/" + e.sprite;
            var s = AssetDatabase.LoadAssetAtPath<Sprite>(p);
            if (s == null)
                throw new System.Exception("Missing sprite (is its Texture Type set to 'Sprite (2D and UI)'?): " + p);
            sprites[e.sprite] = s;
        }

        VideoClip clip = null;
        if (EnableVideo && UseLocalVideoClip)
        {
            clip = AssetDatabase.LoadAssetAtPath<VideoClip>(VideoClipPath);
            if (clip == null)
                throw new System.Exception("Missing video clip: " + VideoClipPath);
        }

        // Warn loudly if a PNG was swapped for one with a different aspect — the
        // CONFIG numbers are measured, the sprite is the ground truth.
        foreach (var e in Layout)
        {
            Rect r = sprites[e.sprite].rect;
            if (r.width <= 0f) continue;
            float actual = r.height / r.width;
            if (Mathf.Abs(actual - e.aspect) / e.aspect > 0.02f)
            {
                Debug.LogWarning($"[Rishabh] '{e.name}' aspect drift: CONFIG says {e.aspect:F4}, " +
                                 $"{e.sprite} is actually {actual:F4} ({r.width}×{r.height}). Using the sprite's value.");
                e.aspect = actual;
            }
        }

        // 2. Register every image target in the project-global settings (build-time export)
        Progress("Registering global image targets", 0.15f);
        foreach (var def in Targets) RegisterGlobalTarget(def.id, targetTextures[def.id]);

        // 3. Clone the template scene (never opened for writing — only CopyAsset reads it)
        Progress("Cloning template scene", 0.25f);
        // Never delete the scene that is currently open — step out of it first.
        if (EditorSceneManager.GetActiveScene().path == NewScenePath)
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (System.IO.File.Exists(NewScenePath))
            AssetDatabase.DeleteAsset(NewScenePath);
        if (!AssetDatabase.CopyAsset(TemplatePath, NewScenePath))
            throw new System.Exception("Could not copy template scene: " + TemplatePath);
        AssetDatabase.Refresh();
        Scene newScene = EditorSceneManager.OpenScene(NewScenePath, OpenSceneMode.Single);

        // 4. Find the ImageTracker + the ARCamera (needed for the world-space raycaster)
        Progress("Configuring ImageTracker", 0.35f);
        ImageTracker tracker = FindInScene<ImageTracker>(newScene);
        if (tracker == null)
            throw new System.Exception("No ImageTracker found in the template scene.");
        if (tracker.transform.parent != null)
            throw new System.Exception("ImageTracker must stay a root transform (JS bridge sends messages by name).");

        ARCamera arCamera = FindInScene<ARCamera>(newScene);
        if (arCamera == null)
            throw new System.Exception("No ARCamera found in the template scene.");
        Camera eventCamera = arCamera.GetComponent<Camera>();
        if (eventCamera == null)
            throw new System.Exception("ARCamera has no Camera component.");

        GameObject screenCanvas = FindScreenCanvas(newScene);

        // 5. Destroy the template's demo target(s)
        var trackerSo = new SerializedObject(tracker);
        var targetsProp = trackerSo.FindProperty("imageTargets");
        if (targetsProp == null)
            throw new System.Exception("ImageTracker.imageTargets not found — API changed?");
        for (int i = 0; i < targetsProp.arraySize; i++)
        {
            var t = targetsProp.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("transform").objectReferenceValue as Transform;
            if (t != null) Object.DestroyImmediate(t.gameObject);
        }

        // 5b. Strip demo UI inherited from the template.  Runs BEFORE the card is built
        //     so the leftover-GoToUrl sweep cannot touch our own link buttons.
        Progress("Removing demo UI", 0.40f);
        string strippedInfo = StripTemplateDemoUi(newScene, screenCanvas);

        // 6-8. Build one complete copy of the card content per tracked image, so
        //      either face of the printed card gives the same experience.
        Progress("Building tracked content", 0.45f);
        string saveContactUrl = ResolveSaveContactUrl();
        var linkReport = new List<string>();
        string videoInfo = "  • Video: disabled\n";
        var builtRoots = new List<GameObject>();
        float targetW = PhysicalWidth, targetH = PhysicalWidth;

        for (int ti = 0; ti < Targets.Count; ti++)
        {
            // Only the first pass fills the link report — every copy is identical.
            builtRoots.Add(BuildTargetContent(
                Targets[ti], targetTextures[Targets[ti].id], tracker, eventCamera,
                sprites, clip, saveContactUrl,
                ti == 0 ? linkReport : null,
                ref videoInfo, ref targetW, ref targetH));
        }

        // 9. Rewrite the tracker's target list with exactly these targets
        targetsProp.ClearArray();
        for (int i = 0; i < builtRoots.Count; i++)
        {
            targetsProp.InsertArrayElementAtIndex(i);
            var el = targetsProp.GetArrayElementAtIndex(i);
            el.FindPropertyRelative("id").stringValue = Targets[i].id;
            el.FindPropertyRelative("transform").objectReferenceValue = builtRoots[i].transform;
        }
        var originProp = trackerSo.FindProperty("trackerOrigin");
        if (originProp != null) originProp.enumValueIndex = 0;   // CAMERA_ORIGIN
        trackerSo.ApplyModifiedProperties();

        // 10. Scan prompt — removed by default (see RemoveScanArea)
        Progress("Finishing scene", 0.8f);
        string scanInfo;
        if (RemoveScanArea)
        {
            // Clear the template's listeners FIRST: they point at the ScanArea we are
            // about to destroy, and a persistent call to a dead object is dead weight.
            ClearImageEvent(trackerSo, "OnImageFound");
            ClearImageEvent(trackerSo, "OnImageLost");
            trackerSo.ApplyModifiedProperties();
            scanInfo = DestroyScanArea(screenCanvas);
        }
        else
        {
            WireScanAreaEvents(screenCanvas, trackerSo);
            if (UpdateScanHintArt) UpdateScanHint(screenCanvas, targetSprite, targetTex);
            scanInfo = "  • Scan prompt: kept\n";
        }

        // 10b. Declare this scene as a PLAIN AR campaign (not a hunt). HuntFlagPostBuild
        //      reads this at build time and writes enabled:false into index.html, so the
        //      Meme Hunt HUD can never appear over the card — even if a hunt_token is
        //      still sitting in this domain's localStorage from testing the hunt.
        var campaignGo = new GameObject("Campaign Settings");
        var campaign = campaignGo.AddComponent<CampaignSettings>();
        campaign.huntEnabled  = false;
        campaign.campaignName = "Rishabh Rustagi — AR Smart Card";

        // 11. vCard file + save + build settings
        string vcardInfo = "";
        if (WriteVCardFile && !UseDataUriVCard)
            vcardInfo = WriteVCard();

        return FinishScene(newScene, targetW, targetH, videoInfo, linkReport,
                           strippedInfo, scanInfo, vcardInfo);
    }

    /// <summary>Builds ONE tracked target: root (carrying the tracking-image mesh),
    /// its video plane and its World Canvas with the full layout. Returns the root.</summary>
    private static GameObject BuildTargetContent(
        TargetDef def, Texture2D targetTex, ImageTracker tracker, Camera eventCamera,
        Dictionary<string, Sprite> sprites, VideoClip clip, string saveContactUrl,
        List<string> linkReport, ref string videoInfo, ref float outW, ref float outH)
    {
        string TargetId = def.id;

        // Target root — mesh carries the physical size, transform stays identity.
        // ImageTracker.ParseData() hard-assigns localScale/position/rotation on this
        // object EVERY tracked frame, so nothing may be authored on it.
        float targetW = PhysicalWidth;
        float targetH = PhysicalWidth * ((float)targetTex.height / targetTex.width);
        outW = targetW; outH = targetH;

        var targetObj = new GameObject(TargetId);
        targetObj.layer = 0;
        targetObj.transform.SetParent(tracker.transform, false);
        targetObj.transform.localPosition = new Vector3(0, 0, CardWorldZ);
        targetObj.transform.localRotation = Quaternion.identity;
        targetObj.transform.localScale    = Vector3.one;

        targetObj.AddComponent<MeshFilter>().sharedMesh =
            GetOrCreateMesh(targetW, targetH, TargetId + "_TrackImg");
        // ImageTracker.Start() does GetComponent<Renderer>().enabled = false with no null
        // check — the MeshRenderer is mandatory even though the card art never renders.
        targetObj.AddComponent<MeshRenderer>().sharedMaterial =
            GetOrCreateUnlitMaterial($"{MatFolder}/{TargetId}_Mat.mat", TargetId + "_Mat", targetTex);

        // 7. Video plane — aspect from the source pixels, width pinned to the card width
        if (EnableVideo)
        {
            Progress("Creating video plane", 0.55f);
            // ContentScale shrinks the visible video; the tracking quad above keeps
            // the card's true size, so the two are deliberately allowed to differ.
            float vidW = PhysicalWidth * ContentScale;
            float vidH = PhysicalWidth * ContentScale * ((float)VideoHeightPx / VideoWidthPx);

            var vidObj = new GameObject(TargetId + " vid");
            vidObj.layer = 0;
            vidObj.transform.SetParent(targetObj.transform, false);
            vidObj.transform.localPosition = new Vector3(0, 0, VideoWorldZ);
            vidObj.transform.localRotation = Quaternion.identity;
            vidObj.transform.localScale    = Vector3.one;

            vidObj.AddComponent<MeshFilter>().sharedMesh =
                GetOrCreateMesh(vidW, vidH, TargetId + "_Vid");
            var vidRend = vidObj.AddComponent<MeshRenderer>();
            // Placeholder texture until the first video frame lands (kills the white flash)
            vidRend.sharedMaterial = GetOrCreateUnlitMaterial(
                $"{MatFolder}/{TargetId}_VidMat.mat", TargetId + "_VidMat", targetTex);

            var vp = vidObj.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.renderMode  = VideoRenderMode.MaterialOverride;
            vp.targetMaterialRenderer = vidRend;
            vp.targetMaterialProperty = "_MainTex";   // wizard leaves this <noninit>; CDN controller copies it verbatim
            vp.isLooping        = true;
            vp.waitForFirstFrame = true;
            vp.skipOnDrop       = true;
            vp.audioOutputMode  = VideoAudioOutputMode.Direct;   // default AudioSource mode = silent

            var cdn = vidObj.AddComponent<CDNARVideoController>();
            cdn.webGLSoundTargetKey = TargetId;   // must equal the tracker id for the WebGL sound unlock
            cdn.loopVideo = true;
            // Skip the 2-frame anti-flicker hold: while the video renderer is disabled
            // nothing opaque writes depth, and Lines.png (which relies on the video
            // plane's depth to stay behind the card) would flash over the card on
            // every acquisition.
            cdn.fastLoadMode = true;

            // CDNARVideoController.Awake() clones url → source → clip in that order, so the
            // in-scene VideoPlayer's `source` always wins.  Set it explicitly.
            if (UseLocalVideoClip)
            {
                vp.source = VideoSource.VideoClip;
                vp.clip   = clip;
                cdn.cdnVideoUrl = "";   // empty => Awake keeps originalVP.url/clip
                videoInfo = $"  • Video: local VideoClip, {vidW:F2} × {vidH:F2} units\n";
            }
            else
            {
                vp.source = VideoSource.Url;
                vp.url    = VideoCdnUrl;
                cdn.cdnVideoUrl = VideoCdnUrl;
                videoInfo = $"  • Video: CDN url, {vidW:F2} × {vidH:F2} units\n";
            }
        }

        // 8. World Canvas + the layout
        Progress("Laying out the card UI", 0.65f);
        var canvasObj = new GameObject("World Canvas", typeof(RectTransform));
        canvasObj.layer = UILayer;
        canvasObj.transform.SetParent(targetObj.transform, false);

        var worldCanvas = canvasObj.AddComponent<Canvas>();
        worldCanvas.renderMode = RenderMode.WorldSpace;
        // MANDATORY: a world-space GraphicRaycaster uses canvas.worldCamera as its event
        // camera.  The shipped wizard forgets this and its buttons are dead.
        worldCanvas.worldCamera = eventCamera;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        // 1 card-width unit == this many canvas pixels
        float pxPerUnit = 1f / CanvasScaleConst;                      // 1388.89

        // Anchors/pivot/size FIRST, then the transform — changing anchors after
        // positioning can silently re-solve localPosition.
        var canvasRt = canvasObj.GetComponent<RectTransform>();
        canvasRt.anchorMin = canvasRt.anchorMax = new Vector2(0.5f, 0.5f);
        canvasRt.pivot     = new Vector2(0.5f, 0.5f);
        canvasRt.sizeDelta = new Vector2(targetW * pxPerUnit, targetH * pxPerUnit);
        canvasRt.localScale    = Vector3.one * CanvasScaleConst;
        canvasRt.localRotation = Quaternion.identity;
        canvasRt.localPosition = new Vector3(0, 0, CanvasWorldZ);

        foreach (var e in Layout)
        {
            var go = new GameObject(e.name, typeof(RectTransform));
            go.layer = UILayer;
            go.transform.SetParent(canvasRt, false);

            var rt = (RectTransform)go.transform;
            rt.localRotation = Quaternion.identity;
            rt.localScale    = Vector3.one;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);

            // Same ContentScale as the video, so the surround stays locked to it
            float wPx = e.width * ContentScale * pxPerUnit;
            rt.sizeDelta = new Vector2(wPx, wPx * e.aspect);         // height is derived — never stretched

            // localZ converts a world-space z back into canvas pixels (see Z CONVENTION).
            float worldZ = float.IsNaN(e.worldZ) ? CanvasWorldZ : e.worldZ;
            float localZ = (worldZ - CanvasWorldZ) / CanvasScaleConst;
            rt.anchoredPosition3D = new Vector3(
                e.dx * ContentScale * pxPerUnit,
                e.dy * ContentScale * pxPerUnit,
                localZ);

            var img = go.AddComponent<Image>();
            img.sprite = sprites[e.sprite];
            img.preserveAspect = true;

            string url = e.vCard ? saveContactUrl : e.url;
            if (string.IsNullOrEmpty(url))
            {
                img.raycastTarget = false;    // decoration must never swallow a tap
                continue;
            }

            WireUrlButton(go, img, url);
            if (linkReport != null) linkReport.Add($"  • {e.name} → {Shorten(url)}");
        }

        // Template convention: target roots start INACTIVE — ImageTracker re-activates
        // on OnTrackingFound. Leaving it active makes CDNARVideoController.Awake/OnEnable
        // run at scene load, so the video would start (and its audio play) before the
        // card is ever scanned.
        targetObj.SetActive(false);
        return targetObj;
    }

    /// <summary>Saves the scene, updates Build Settings and composes the report.</summary>
    private static string FinishScene(Scene newScene, float targetW, float targetH,
                                      string videoInfo, List<string> linkReport,
                                      string strippedInfo, string scanInfo, string vcardInfo)
    {
        Progress("Saving scene", 0.95f);
        EditorSceneManager.MarkSceneDirty(newScene);
        if (!EditorSceneManager.SaveScene(newScene))
            throw new System.Exception("Failed to save " + NewScenePath);
        AssetDatabase.SaveAssets();

        if (MakeOnlyEnabledBuildScene) SetAsOnlyEnabledScene(NewScenePath);
        else                            AddToBuildSettings(NewScenePath);

        var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(NewScenePath);
        if (sceneAsset != null && !Application.isBatchMode)
        {
            EditorGUIUtility.PingObject(sceneAsset);
            Selection.activeObject = sceneAsset;
        }

        EditorUtility.ClearProgressBar();
        var ids = new List<string>();
        foreach (var d in Targets) ids.Add(d.id);

        return "Rishabh scene created at " + NewScenePath + "\n\n" +
               $"  • {Targets.Count} tracked image(s): {string.Join(", ", ids)}\n" +
               $"    each {targetW:F2} × {targetH:F2} units, with its own copy of the content\n" +
               videoInfo +
               $"  • {Layout.Count} UI quads per target, {linkReport.Count} tappable\n" +
               string.Join("\n", linkReport) + "\n" +
               strippedInfo + scanInfo + vcardInfo + "\n" +
               (MakeOnlyEnabledBuildScene
                    ? "It is now the only enabled scene in Build Settings.\n\n"
                    : "Added to Build Settings; every other scene is untouched — so the\n" +
                      "first ENABLED scene is still MemeHunt and a WebGL build would boot\n" +
                      "THAT. Before building Rishabh, run:\n" +
                      "   Tools ▸ Rishabh ▸ 2. Make Rishabh The Only Build Scene\n\n") +
               "Next: press ▶ to test, then File > Build Settings > WebGL > Build (template iTracker).";
    }

    // ─────────────────────────────────────────────────────────────────
    //  TRACKED-CONTENT HELPERS
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Image + Button + GoToUrl, matching the hand-made The Vice Streets prefab.
    /// Taps go through GraphicRaycaster + CustomEventSystem — there are no colliders
    /// anywhere in this architecture.</summary>
    private static void WireUrlButton(GameObject go, Image img, string url)
    {
        var btn = go.AddComponent<Button>();
        btn.transition    = Selectable.Transition.None;   // prefab uses m_Transition: 0
        btn.targetGraphic = img;
        var nav = btn.navigation;
        nav.mode = Navigation.Mode.None;                  // prefab uses m_Navigation.m_Mode: 3
        btn.navigation = nav;

        var goUrl = go.AddComponent<GoToUrl>();

        var methodInfo = UnityEvent.GetValidMethodInfo(goUrl, "GoTo", new System.Type[] { typeof(string) });
        if (methodInfo == null)
        {
            Debug.LogError("[Rishabh] GoToUrl.GoTo(string) not found — cannot wire " + go.name);
            return;
        }
        var action = System.Delegate.CreateDelegate(
            typeof(UnityAction<string>), goUrl, methodInfo, false) as UnityAction<string>;
        if (action == null)
        {
            Debug.LogError("[Rishabh] Could not bind GoToUrl.GoTo for " + go.name);
            return;
        }
        UnityEditor.Events.UnityEventTools.AddStringPersistentListener(btn.onClick, action, url);
    }

    private static string ResolveSaveContactUrl()
    {
        if (!UseDataUriVCard) return SaveContactVCardUrl;
        return "data:text/vcard;charset=utf-8," + System.Uri.EscapeDataString(BuildVCardBody());
    }

    private static string BuildVCardBody()
    {
        const string nl = "\r\n";   // vCard 3.0 requires CRLF
        return
            "BEGIN:VCARD" + nl +
            "VERSION:3.0" + nl +
            "N;CHARSET=UTF-8:" + ContactLastName + ";" + ContactFirstName + ";;;" + nl +
            "FN;CHARSET=UTF-8:" + ContactFullName + nl +
            "ORG;CHARSET=UTF-8:" + ContactOrg + nl +
            "TEL;TYPE=CELL:" + ContactPhone + nl +
            "EMAIL;CHARSET=UTF-8;type=WORK,INTERNET:" + ContactEmail + nl +
            "URL;type=WORK;CHARSET=UTF-8:" + ContactSite + nl +
            "URL;TYPE=linkedin:" + LayoutUrl("linkedin") + nl +
            "URL;TYPE=instagram:" + LayoutUrl("instagram") + nl +
            "END:VCARD" + nl;
    }

    /// <summary>Single-sources the social URLs from the LAYOUT table.</summary>
    private static string LayoutUrl(string elementName)
    {
        foreach (var e in Layout)
            if (e.name == elementName) return e.url ?? "";
        return "";
    }

    /// <summary>Writes the vCard next to the repo's other jsDelivr-published media
    /// (&lt;projectRoot&gt;/videos/). Outside Assets/, so Unity never imports it.</summary>
    private static string WriteVCard()
    {
        try
        {
            string root = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string full = System.IO.Path.Combine(root, VCardProjectPath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
            string dir  = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(full, BuildVCardBody());
            Debug.Log("[Rishabh] vCard written: " + full);
            return "  • vCard written to " + VCardProjectPath + " — commit & push to publish it on jsDelivr\n";
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[Rishabh] Could not write the vCard file: " + e.Message);
            return "  • vCard file could not be written (see Console)\n";
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  TEMPLATE / SCENE HELPERS
    // ─────────────────────────────────────────────────────────────────

    private static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            var c = go.GetComponentInChildren<T>(true);
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>The screen-space Canvas is the root Canvas whose name isn't the world one.</summary>
    private static GameObject FindScreenCanvas(Scene scene)
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            var canvas = go.GetComponent<Canvas>();
            if (canvas != null && !go.name.Contains("World")) return go;
        }
        return null;
    }

    /// <summary>Deletes Canvas ▸ ScanArea (CardImage + ScanToStart) — the template's
    /// "point your camera at the card" prompt. Call ClearImageEvent first: the
    /// template wires OnImageFound/OnImageLost to toggle this object.</summary>
    private static string DestroyScanArea(GameObject screenCanvas)
    {
        if (screenCanvas == null) return "  • Scan prompt: no screen Canvas found\n";
        Transform sa = screenCanvas.transform.Find("ScanArea");
        if (sa == null)
        {
            // Not fatal — an earlier run (or a template change) may have removed it
            Debug.Log("[Rishabh] No Canvas/ScanArea to remove.");
            return "  • Scan prompt: already absent\n";
        }
        Object.DestroyImmediate(sa.gameObject);
        return "  • Scan prompt: removed (ScanArea + CardImage + ScanToStart)\n";
    }

    /// <summary>Drops every persistent listener from one of the tracker's
    /// UnityEvents, so nothing points at an object we deleted.</summary>
    private static void ClearImageEvent(SerializedObject so, string eventFieldName)
    {
        var eventProp = so.FindProperty(eventFieldName);
        if (eventProp == null)
        {
            Debug.LogWarning("[Rishabh] Event not found on ImageTracker: " + eventFieldName);
            return;
        }
        var callsProp = eventProp.FindPropertyRelative("m_PersistentCalls.m_Calls");
        if (callsProp != null) callsProp.ClearArray();
    }

    private static void WireScanAreaEvents(GameObject screenCanvas, SerializedObject trackerSo)
    {
        Transform sa = screenCanvas != null ? screenCanvas.transform.Find("ScanArea") : null;
        if (sa == null)
        {
            Debug.LogWarning("[Rishabh] ScanArea not found — scan prompt left unwired.");
            return;
        }
        WireEventSetActive(trackerSo, "OnImageFound", sa.gameObject, false);
        WireEventSetActive(trackerSo, "OnImageLost",  sa.gameObject, true);
        trackerSo.ApplyModifiedProperties();
    }

    /// <summary>OnImageFound/OnImageLost are [SerializeField] private UnityEvent&lt;string&gt;,
    /// so their persistent calls have to be written raw.</summary>
    private static void WireEventSetActive(SerializedObject so, string eventFieldName,
                                           GameObject target, bool value)
    {
        var eventProp = so.FindProperty(eventFieldName);
        if (eventProp == null)
        {
            Debug.LogWarning("[Rishabh] Event not found on ImageTracker: " + eventFieldName);
            return;
        }
        var callsProp = eventProp.FindPropertyRelative("m_PersistentCalls.m_Calls");
        if (callsProp == null) return;

        callsProp.ClearArray();
        callsProp.InsertArrayElementAtIndex(0);
        var call = callsProp.GetArrayElementAtIndex(0);
        call.FindPropertyRelative("m_Target").objectReferenceValue = target;
        call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = "UnityEngine.GameObject, UnityEngine";
        call.FindPropertyRelative("m_MethodName").stringValue = "SetActive";
        call.FindPropertyRelative("m_Mode").intValue = 6;       // PersistentListenerMode.Bool
        call.FindPropertyRelative("m_CallState").intValue = 2;  // UnityEventCallState.RuntimeOnly
        var args = call.FindPropertyRelative("m_Arguments");
        if (args != null) args.FindPropertyRelative("m_BoolArgument").boolValue = value;
    }

    /// <summary>The wizard leaves the old demo card art in the on-screen "scan this" hint.
    /// Repoint it at the Rishabh target and reshape the ghost to the portrait aspect.</summary>
    private static void UpdateScanHint(GameObject screenCanvas, Sprite targetSprite, Texture2D targetTex)
    {
        if (screenCanvas == null) return;
        if (targetSprite == null)
        {
            // Silent no-op would be invisible if the PNG's Texture Type ever changes
            Debug.LogWarning("[Rishabh] Target image is not imported as a Sprite — " +
                             "scan hint left as the template's: " + SrcFolder + "/" + Targets[0].image);
            return;
        }
        Transform card = FindChildRecursive(screenCanvas.transform, "CardImage");
        if (card == null)
        {
            Debug.LogWarning("[Rishabh] Canvas/ScanArea/CardImage not found — scan hint left as-is.");
            return;
        }
        var img = card.GetComponent<Image>();
        if (img != null)
        {
            img.sprite = targetSprite;
            img.preserveAspect = true;
        }
        var rt = card.GetComponent<RectTransform>();
        if (rt != null && targetTex != null && targetTex.height > 0)
        {
            float w = ScanHintHeightPx * ((float)targetTex.width / targetTex.height);
            rt.sizeDelta = new Vector2(w, ScanHintHeightPx);
        }
    }

    /// <summary>Removes the template widgets that would break this card:
    /// "Button Menu" (calls ImageTracker.StopTracker → freezes all tracking), every
    /// leftover demo GoToUrl (window.location.assign navigates the AR tab away) and,
    /// optionally, "LogoAndLink" together with the UIManager that dereferences it
    /// without a null-check.  Must run BEFORE the card's own buttons exist.</summary>
    private static string StripTemplateDemoUi(Scene scene, GameObject screenCanvas)
    {
        var removed = new List<string>();
        var doomed = new List<GameObject>();

        foreach (var go in scene.GetRootGameObjects())
        {
            var menu = FindChildRecursive(go.transform, "Button Menu");
            if (menu == null) continue;
            doomed.Add(menu.gameObject);
            removed.Add("Button Menu (StopTracker)");
            break;
        }

        if (RemoveTemplateCtaButton)
        {
            Transform cta = screenCanvas != null ? FindChildRecursive(screenCanvas.transform, "LogoAndLink") : null;
            if (cta != null)
            {
                doomed.Add(cta.gameObject);
                removed.Add("LogoAndLink (hard-coded CTA)");
            }
        }

        // Any demo GoToUrl left over from the template would navigate the tab away.
        foreach (var go in scene.GetRootGameObjects())
            foreach (var g in go.GetComponentsInChildren<GoToUrl>(true))
            {
                if (doomed.Contains(g.gameObject)) continue;
                doomed.Add(g.gameObject);
                removed.Add("demo GoToUrl '" + g.gameObject.name + "'");
            }

        foreach (var go in doomed)
            if (go != null) Object.DestroyImmediate(go);

        if (RemoveTemplateCtaButton)
        {
            // UIManager.Start() does contectButton.onClick.AddListener(...) with no null
            // check — leaving it behind after deleting the button is a guaranteed NRE.
            var ui = FindInScene<UIManager>(scene);
            if (ui != null)
            {
                Object.DestroyImmediate(ui);
                removed.Add("UIManager component");
            }
        }

        return removed.Count == 0
            ? "  • No demo UI needed removing\n"
            : "  • Removed: " + string.Join(", ", removed) + "\n";
    }

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent == null) return null;
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            var found = FindChildRecursive(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    //  ASSET HELPERS
    // ─────────────────────────────────────────────────────────────────

    private static void RegisterGlobalTarget(string targetId, Texture2D tex)
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null)
            throw new System.Exception("ImageTrackerGlobalSettings.asset not found in Resources.");
        if (gs.imageTargetInfos == null)
            gs.imageTargetInfos = new List<ImageTargetInfo>();

        bool found = false;
        foreach (var info in gs.imageTargetInfos)
        {
            if (info.id != targetId) continue;
            info.texture = tex;
            found = true;
            break;
        }
        if (!found)
            gs.imageTargetInfos.Add(new ImageTargetInfo { id = targetId, texture = tex });

        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
    }

    /// <summary>Quad mesh, XY-vertical, centred on the origin, normals Vector3.back so it
    /// faces a camera looking down +Z. Cached per size — identical to ARCardSetupWindow.</summary>
    private static Mesh GetOrCreateMesh(float w, float h, string meshName)
    {
        // InvariantCulture: on a comma-decimal locale (de-DE, fr-FR…) "{w:F2}" would
        // emit "1,00", missing the cache and diverging from the existing
        // Generated/*_1.00x1.33.mesh names.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string path = MeshFolder + "/" + meshName + "_" +
                      w.ToString("F2", inv) + "x" + h.ToString("F2", inv) + ".mesh";
        Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) return existing;

        Mesh mesh = new Mesh { name = meshName };
        mesh.vertices = new Vector3[]
        {
            new Vector3(-w * 0.5f, -h * 0.5f, 0),
            new Vector3( w * 0.5f, -h * 0.5f, 0),
            new Vector3(-w * 0.5f,  h * 0.5f, 0),
            new Vector3( w * 0.5f,  h * 0.5f, 0),
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(0, 1), new Vector2(1, 1),
        };
        mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.normals   = new Vector3[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.RecalculateBounds();

        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();
        Debug.Log($"[Rishabh] Mesh generated: {path}  ({w} × {h} units)");
        return mesh;
    }

    /// <summary>Update-in-place rather than CreateAsset-over-the-top, so a rebuild never
    /// invalidates an existing material reference.</summary>
    private static Material GetOrCreateUnlitMaterial(string path, string matName, Texture tex)
    {
        Shader unlit = Shader.Find("Unlit/Texture");
        if (unlit == null) throw new System.Exception("Shader 'Unlit/Texture' not found.");

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(unlit) { name = matName };
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = unlit;
        mat.mainTexture = tex;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void AddToBuildSettings(string scenePath)
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].path != scenePath) continue;
            list[i] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = list.ToArray();
            return;
        }
        list.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    private static void SetAsOnlyEnabledScene(string scenePath)
    {
        var list = new List<EditorBuildSettingsScene>();
        bool present = false;
        foreach (var s in EditorBuildSettings.scenes)
        {
            if (s.path == scenePath)
            {
                present = true;
                list.Add(new EditorBuildSettingsScene(scenePath, true));
            }
            else
            {
                if (s.enabled)
                    Debug.Log("[Rishabh] Disabling scene in Build Settings: " + s.path);
                list.Add(new EditorBuildSettingsScene(s.path, false));
            }
        }
        if (!present)
            list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    /// <summary>Single-level folder creation — call it for each ancestor, in order.</summary>
    private static void EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        string parent = System.IO.Path.GetDirectoryName(assetPath).Replace("\\", "/");
        string folder = System.IO.Path.GetFileName(assetPath);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folder))
            AssetDatabase.CreateFolder(parent, folder);
    }

    private static string Shorten(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length <= 64) return url;
        return url.Substring(0, 61) + "…";
    }

    private static void Progress(string step, float t)
    {
        if (!Application.isBatchMode)
            EditorUtility.DisplayProgressBar("Rishabh AR Card Builder", step, t);
    }
}
