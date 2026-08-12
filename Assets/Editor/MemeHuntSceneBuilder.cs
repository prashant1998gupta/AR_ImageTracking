using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Imagine.WebAR;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Meme Hunt Scene Builder — Bharatiya Vyapar Mahotsav 2026
///
/// DROP A POSTER IN THE FOLDER AND REBUILD. Nothing else.
///
/// The poster set is DISCOVERED, never authored: every image in PosterFolder whose
/// file name starts with its hunt-order number becomes a target, and the number is
/// the hunt order. Adding a sixth poster is copying a sixth file in. Removing one is
/// deleting it. No C# edit, no per-poster mesh, no per-poster material, no
/// keepFromTemplate, no reuse of the Demo-Video template's baked targets — every
/// poster is built the same way from its own image, so poster #6 is exactly as
/// trustworthy as poster #1.
///
/// What one run does:
///   • Discovers the posters, sorts them by their leading number (= the hunt route)
///   • Registers every one in ImageTrackerGlobalSettings (the build-time export source)
///   • Clones Demo-Video.unity, destroys ALL of its demo targets and its demo UI
///   • Builds each poster procedurally: tracking quad → Content (ARContentScale) →
///     video plane (VideoPlayer + CDNARVideoController streaming from the CDN)
///   • Rewires the ImageTracker to exactly those posters, in that order
///   • Declares the scene a HUNT campaign (CampaignSettings) so the overlay ships ON
///   • Makes MemeHunt the only enabled scene in Build Settings
///   • Exports the poster list as JSON so the SERVER can learn the ids
///
/// Re-running is safe and idempotent: the scene is deleted and re-cloned, meshes and
/// materials are reused-or-updated in place, and the global-target entry is
/// add-or-update. Nothing outside Assets/Scenes_1/MemeHunt.unity,
/// Assets/AR_Assets/Materials, Assets/AR_Assets/Planes/Generated and
/// &lt;projectRoot&gt;/hunt-posters.json is ever written.
///
/// The hunt UI itself lives in the WebGL template (hunt-overlay.js) — no scene UI
/// changes are needed; ImageTracker's analytics bridge feeds the overlay.
///
/// ─── EXPORTING THE POSTER LIST ──────────────────────────────────────────
/// The target ids are baked into the WebGL build and cannot be changed after it
/// ships, so UNITY OWNS THEM and the server is told what they are. Building the
/// scene (or Tools ▸ Meme Hunt ▸ 3.) writes &lt;projectRoot&gt;/hunt-posters.json and
/// logs the same JSON to the Console; paste it into hunt/admin.html ▸ Settings ▸
/// Poster list. With that setting empty the server stays on its own built-in
/// defaults — and then every scan of a poster it has never heard of is rejected
/// with 400 while the video still plays, which looks like nothing at all going
/// wrong. Exporting is therefore part of EVERY build, not a thing to remember.
/// </summary>
public static class MemeHuntSceneBuilder
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ██  C O N F I G  ██   — everything tunable lives here.  Nudge, rebuild.
    // ═══════════════════════════════════════════════════════════════════════

    // ─── Scene identity ─────────────────────────────────────────────────────
    private static readonly string TemplatePath = "Assets/Scenes_1/Demo-Video.unity";
    private static readonly string NewScenePath = "Assets/Scenes_1/MemeHunt.unity";

    // ─── THE POSTER FOLDER — the only thing you touch to change the hunt ────
    // Every image directly inside this folder whose NAME MATCHES PosterFilePattern
    // is a poster. Sub-folders are ignored on purpose: AssetDatabase.FindAssets
    // recurses, and a stray screenshot two levels down must never silently become
    // poster number 7.
    private static readonly string PosterFolder = "Assets/AR_Assets/AR Meme Hunt";

    // The file name (without extension) must CONTAIN the poster's HUNT ORDER as its
    // first run of digits. Group 1 is that number — it drives the id, the label, the
    // video URL, and the order that sequential mode and the "next hint" pointer walk.
    //
    // Deliberately loose about everything except the number, because the poster files
    // have already been renamed twice mid-project and a builder that breaks on a
    // rename is not generic. All of these give 1:
    //   "Target-1.jpg"   "Target 1.jpg"   "Target_1.png"   "1 Target.jpg"   "meme-1.jpg"
    // and "Target-10" gives 10, which sorts after 9 rather than next to 1.
    // Files with no digit at all are skipped with a per-file warning, never silently.
    private static readonly string PosterFilePattern = @"^\D*(\d+)";
    private static readonly string[] PosterExtensions = { ".jpg", ".jpeg", ".png" };

    // Id and label are DERIVED from that number, so they can never drift apart from
    // the files. {0} is the number.
    //   id    — the tracker id, the <imagetarget> tag, and the poster_id the hunt
    //           server scores against. MUST match ^[A-Za-z0-9_-]{1,64}$ — NO SPACES.
    //   label — display only: the hunt chip caption, <= 20 chars. Spaces fine.
    private static readonly string IdFormat    = "Target{0}";
    private static readonly string LabelFormat = "Target {0}";

    // ─── VIDEO BY CONVENTION ────────────────────────────────────────────────
    // Poster n plays video n. {0} is the same number the file name started with.
    //
    // The repo's videos/ folder is what jsDelivr publishes, so a file committed and
    // pushed there is live at the URL below. Note the SUBFOLDER and the %20: the
    // shipped clips are named "Target 1.mp4" with a real space, and a raw space in a
    // URL 404s on jsDelivr.
    private static readonly string VideoUrlFormat =
        "https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/ARMemeHuntVideos/Target-{0}.mp4";
    // The same file on disk, relative to <projectRoot> (the folder holding Assets/).
    // Checked at build time PURELY to report what is still missing — a missing clip
    // never fails the build, because the scene is often built before the edit is
    // finished. The URL goes in either way and starts working the moment the file is
    // committed and pushed.
    private static readonly string VideoProjectPathFormat = "videos/ARMemeHuntVideos/Target-{0}.mp4";

    // Escape hatch: one poster whose video does not follow the convention. Keyed by
    // the generated id. An entry here wins over VideoUrlFormat, and its local-file
    // check is skipped (the override may point at any host at all).
    //   { "Target4", "https://cdn.jsdelivr.net/gh/.../videos/some%20other%20clip.mp4" },
    private static readonly Dictionary<string, string> VideoUrlOverrides =
        new Dictionary<string, string>
    {
    };

    // ─── HINTS (optional) ───────────────────────────────────────────────────
    // "Find this next" text, keyed by id, <= 300 chars. EMPTY IS FINE and is the
    // default: hunt.php accepts an empty hint, and hunt/admin.html ▸ Settings can
    // write hints live during the event without a rebuild. Fill these in only to
    // ship a sensible starting set.
    //   { "Target1", "Entry gate ke saamne wala pillar — pehla meme wahin chipka hai." },
    private static readonly Dictionary<string, string> Hints =
        new Dictionary<string, string>
    {
    };

    // ─── Generated asset locations ──────────────────────────────────────────
    private static readonly string MatFolder  = "Assets/AR_Assets/Materials";
    private static readonly string MeshFolder = "Assets/AR_Assets/Planes/Generated";
    // Pre-roll placeholder for the video plane, generated on first use if absent.
    // It is 1×1 BLACK on purpose. The obvious choice — the poster's own art — paints
    // a second, slightly-offset copy of the poster over the real printed one for the
    // whole time the CDN is buffering, and that ghost reads as broken tracking. Black
    // reads as "the video is loading", which is what is actually happening.
    private static readonly string BlackTexturePath = "Assets/AR_Assets/Materials/HuntVideoPlaceholder_Black.png";

    // ─── Physical size ──────────────────────────────────────────────────────
    // The tracker normalises the target's WIDTH to 1.0 world unit, so a poster is
    // 1.00 wide × (imgH/imgW) tall. Leave PhysicalWidth at 1.0.
    private static readonly float PhysicalWidth = 1.0f;

    // Multiplies everything drawn, about the poster centre. The invisible tracking
    // quad stays at the poster's true size, so it stays the honest reference for what
    // "1.0" means. 1.0 = the video exactly fills the poster, which is right here: the
    // posters are 1414 × 2000 (h/w 1.41443) and the clips 1122 × 1586 (h/w 1.41359) —
    // a 0.06 % difference, invisible.
    // NO REBUILD NEEDED TO RETUNE: ARContentScale on the "Content" object reads
    // ?scale=… from the page URL at runtime, so this is only the shipped DEFAULT:
    //     https://your-host/memehunt/?scale=0.9
    private static readonly float ContentScale = 1.0f;

    // Video plane aspect (height / width). 0 = use the POSTER's aspect, which is what
    // you want whenever the clip was rendered to fill the poster. Set it only for a
    // clip that is deliberately a different shape from the art it sits on.
    private static readonly float VideoAspectOverride = 0f;

    // ─── Z CONVENTION ───────────────────────────────────────────────────────
    // Quad normals are Vector3.back and the camera looks down +Z, so MORE NEGATIVE Z
    // = CLOSER TO THE CAMERA.
    //     z = -0.010   video plane         → covers the whole poster
    //     z =  0.000   tracking-image quad → Renderer is disabled by ImageTracker.Start(),
    //                                        it exists only because Start() requires one
    private static readonly float CardWorldZ  =  0.000f;
    private static readonly float VideoWorldZ = -0.010f;

    // ─── Campaign ───────────────────────────────────────────────────────────
    private static readonly string CampaignName = "AR Meme Hunt — Bharatiya Vyapar Mahotsav 2026";

    // Analytics project this campaign reports into (admin panel ▸ Projects ▸ API Key).
    // It lives here as well as on the scene's CampaignSettings because rebuilding
    // DELETES and re-creates the scene — a key typed only into the inspector would be
    // lost on the next rebuild. Must be EMPTY (ship with no analytics: the tracker tag
    // is removed from the build) or exactly 64 hex characters, or PreBuildCheck blocks
    // the build. This is the key the live Meme Hunt build already reports with.
    private static readonly string AnalyticsApiKey =
        "e4cfcb9cfd6d120305bb7c268a981b8e8a65c32d0d5062563c8e2c1980ae769c";

    // ─── Template clean-up ──────────────────────────────────────────────────
    // "Button Menu" (ImageTracker.StopTracker → freezes all tracking) and every demo
    // GoToUrl (window.location.assign navigates the AR tab away mid-hunt) are ALWAYS
    // removed — both would break the hunt outright.
    // "LogoAndLink" is the template's screen CTA. It is KEPT by default because that
    // is what the live event build shipped with; flip this to remove it. Either way
    // the UIManager whose Start() dereferences that button with no null check is
    // removed if the button is gone.
    // The committed MemeHunt.unity has no Canvas at all, so there is nothing to
    // preserve here: leaving this false ships the template's LogoAndLink CTA (and
    // the UIManager that hard-codes a URL onto it) into the hunt.
    private static readonly bool RemoveTemplateCtaButton = true;

    // Root/screen UI the template carries that a hunt must never show. Names are
    // matched at any depth; missing ones are silently fine.
    private static readonly string[] TemplateScreenUiToStrip =
    {
        "FirstCanvas",   // ScreenSpaceOverlay, sortingOrder 1 — a splash over everything
        "Panel Main",    // the demo onboarding panel
    };

    // ─── Poster-list export ─────────────────────────────────────────────────
    // Path relative to <projectRoot> (the folder holding Assets/), so the file sits
    // outside Assets/ and Unity never imports it.
    private static readonly string PosterManifestProjectPath = "hunt-posters.json";

    // Mirrors of the server's own validation (hunt.php ▸ normalizePosterList): an
    // entry that fails these is DROPPED there, and a dropped poster is a silent
    // failure — the camera tracks it and the video plays, but the scan is rejected
    // and the chip never ticks. So the same rules are enforced here, at authoring
    // time, where the message can actually be read.
    private const string PosterIdPattern = "^[A-Za-z0-9_-]{1,64}$";
    private const int MaxLabelChars = 20;
    private const int MaxHintChars  = 300;
    // hunt_settings.setting_value is a TEXT column, so hunt.php stops reading at 60
    // posters — silently, mid-list. Fail here instead.
    private const int MaxPosters    = 60;

    // ═══════════════════════════════════════════════════════════════════════
    //  ██  E N D   C O N F I G  ██
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>One discovered poster. Every field is DERIVED from the file on disk —
    /// nothing here is authored, which is the whole point.</summary>
    private class Poster
    {
        public int    order;          // the leading number in the file name = hunt order
        public string id;             // string.Format(IdFormat, order)
        public string label;          // string.Format(LabelFormat, order)
        public string texturePath;    // the asset path it was discovered at
        public Texture2D texture;
        public string videoUrl;       // override, else string.Format(VideoUrlFormat, order)
        public bool   videoUrlIsOverride;
        public string videoProjectPath;   // <projectRoot>-relative, "" when overridden
        public bool   videoFilePresent;
        public string videoHash;          // content hash, so DUPLICATE clips are reported
    }

    /// <summary>MD5 of a file's bytes. Used only to notice that two posters ship the
    /// SAME clip — File.Exists happily reports "all present" while three posters play
    /// one meme, which is exactly the kind of thing nobody spots until the event.</summary>
    private static string FileHash(string fullPath)
    {
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = System.IO.File.OpenRead(fullPath))
                return System.BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
        catch { return ""; }   // unreadable is not worth failing a build over
    }

    // ─────────────────────────────────────────────────────────────────
    [MenuItem("Tools/Meme Hunt/1. Build MemeHunt Scene")]
    public static void BuildSceneMenu()
    {
        try
        {
            string result = BuildScene();
            EditorUtility.DisplayDialog("Meme Hunt", result, "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Meme Hunt — Error", e.Message, "OK");
            Debug.LogException(e);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>Batch-mode entry point:
    /// Unity.exe -batchmode -projectPath ... -executeMethod MemeHuntSceneBuilder.BuildSceneBatch -quit</summary>
    public static void BuildSceneBatch()
    {
        try
        {
            string result = BuildScene();
            Debug.Log("[MemeHunt] " + result);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[MemeHunt] FAILED: " + e);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            throw;
        }
        finally
        {
            // A failed batch run must not leave a progress bar stuck on screen when the
            // Editor is reopened interactively.
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

        // 1. Discover + validate EVERYTHING before touching a single asset. An id the
        //    server would reject can never be scanned, and finding that out AFTER the
        //    scene is rewritten helps nobody.
        Progress("Discovering posters", 0.05f);
        EnsureFolder("Assets/AR_Assets");
        EnsureFolder(MatFolder);
        EnsureFolder("Assets/AR_Assets/Planes");
        EnsureFolder(MeshFolder);

        if (AssetDatabase.LoadAssetAtPath<Object>(TemplatePath) == null)
            throw new System.Exception("Template scene not found: " + TemplatePath);

        List<Poster> posters = DiscoverPosters();
        ResolveVideos(posters);
        // Generated here, while nothing is half-built: creating it re-imports an asset,
        // and that is not something to do with a freshly cloned scene open.
        Texture2D placeholder = GetOrCreateBlackTexture();

        // 2. Register every target in the global settings (build-time export source)
        Progress("Registering global image targets", 0.15f);
        RegisterGlobalTargets(posters);

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

        // 4. Find the ImageTracker + the ARCamera
        Progress("Configuring ImageTracker", 0.35f);
        ImageTracker tracker = FindInScene<ImageTracker>(newScene);
        if (tracker == null)
            throw new System.Exception("No ImageTracker found in the template scene.");
        if (tracker.transform.parent != null)
            throw new System.Exception("ImageTracker must stay a root transform (the JS bridge sends messages by name).");
        // ImageTracker finds the camera at runtime, so a scene without one compiles,
        // builds, and opens to a black screen.
        if (FindInScene<ARCamera>(newScene) == null)
            throw new System.Exception("No ARCamera found in the template scene.");

        var trackerSo = new SerializedObject(tracker);
        var targetsProp = trackerSo.FindProperty("imageTargets");
        if (targetsProp == null)
            throw new System.Exception("ImageTracker.imageTargets not found — API changed?");

        // 5. Destroy EVERY template target. Nothing is inherited: a poster that came
        //    from the template would be built by a different set of rules from one
        //    built here, and only one of those two sets is the one under test.
        for (int i = 0; i < targetsProp.arraySize; i++)
        {
            var t = targetsProp.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("transform").objectReferenceValue as Transform;
            if (t != null) Object.DestroyImmediate(t.gameObject);
        }
        // Belt and braces: the tracker's children ARE its targets, so anything still
        // hanging off it was never in the list — a template edit that added an object
        // and forgot to register it. Left behind, it ships ACTIVE (nothing deactivates
        // an unlisted child), so its CDNARVideoController would start playing at page
        // load. Verified empty on today's Demo-Video.unity; the warning is the point.
        for (int i = tracker.transform.childCount - 1; i >= 0; i--)
        {
            var stray = tracker.transform.GetChild(i);
            Debug.LogWarning("[MemeHunt] Removing unlisted object under ImageTracker: '" +
                             stray.name + "' — it is not in the template's imageTargets list.");
            Object.DestroyImmediate(stray.gameObject);
        }

        // 6. Strip demo UI. Runs BEFORE our own content exists so the blanket
        //    GoToUrl sweep can only ever hit the template's objects.
        Progress("Removing demo UI", 0.42f);
        string strippedInfo = StripTemplateDemoUi(newScene);

        // 7. Build every poster, identically, from its own image.
        Progress("Building poster targets", 0.5f);
        var builtRoots = new List<GameObject>();
        for (int i = 0; i < posters.Count; i++)
        {
            Progress("Building " + posters[i].id, 0.5f + 0.25f * (i / (float)posters.Count));
            builtRoots.Add(BuildPosterTarget(posters[i], tracker, placeholder));
        }

        // 8. Rewrite the tracker's target list with exactly these posters, in hunt order
        targetsProp.ClearArray();
        for (int i = 0; i < posters.Count; i++)
        {
            targetsProp.InsertArrayElementAtIndex(i);
            var elem = targetsProp.GetArrayElementAtIndex(i);
            elem.FindPropertyRelative("id").stringValue = posters[i].id;
            elem.FindPropertyRelative("transform").objectReferenceValue = builtRoots[i].transform;
        }
        // CAMERA_ORIGIN is the safe origin mode for a multi-target hunt scene
        var originProp = trackerSo.FindProperty("trackerOrigin");
        if (originProp != null) originProp.enumValueIndex = 0;
        trackerSo.ApplyModifiedProperties();

        // 9. Declare this scene as a HUNT campaign. HuntFlagPostBuild reads this at
        //    build time and switches the hunt overlay on in the built index.html, so
        //    registration + chips + timer + leaderboard ship automatically.
        var campaignGo = new GameObject("Campaign Settings");
        var campaign = campaignGo.AddComponent<CampaignSettings>();
        campaign.huntEnabled  = true;
        campaign.campaignName = CampaignName;
        // AnalyticsKeyPostBuild writes this into the built index.html's tracker tag.
        campaign.analyticsApiKey = AnalyticsApiKey;

        // 10. Save + build settings
        Progress("Saving scene", 0.85f);
        EditorSceneManager.MarkSceneDirty(newScene);
        if (!EditorSceneManager.SaveScene(newScene))
            throw new System.Exception("Failed to save " + NewScenePath);
        AssetDatabase.SaveAssets();
        // A hunt build MUST boot this scene: HuntFlagPostBuild and AnalyticsKeyPostBuild
        // both read only the FIRST scene of the build.
        SetAsOnlyEnabledScene(NewScenePath);

        // 11. Hand the ids to the server. The scene is already saved at this point, so a
        //     failure here cannot cost the build — ExportPosterList reports and moves on.
        Progress("Exporting poster list", 0.95f);
        string posterInfo = ExportPosterList(BuildPosterListJson(posters));

        var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(NewScenePath);
        if (sceneAsset != null && !Application.isBatchMode)
        {
            EditorGUIUtility.PingObject(sceneAsset);
            Selection.activeObject = sceneAsset;
        }

        EditorUtility.ClearProgressBar();
        return BuildReport(posters, strippedInfo, posterInfo);
    }

    // ─────────────────────────────────────────────────────────────────
    //  DISCOVERY  — the poster set comes from the folder, never from code
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Scans PosterFolder for poster images and returns them in HUNT ORDER
    /// (ascending leading number). Throws — with the actual file names in the message —
    /// on anything that would ship a hunt nobody can finish.</summary>
    private static List<Poster> DiscoverPosters()
    {
        if (!AssetDatabase.IsValidFolder(PosterFolder))
            throw new System.Exception(
                "Poster folder not found: " + PosterFolder + "\n\n" +
                "Create it and drop the poster images in, named with their hunt order:\n" +
                "   1 Target.jpg, 2 Target.jpg, 3 Target.jpg …");

        var rx = new Regex(PosterFilePattern, RegexOptions.IgnoreCase);
        var byOrder = new Dictionary<int, Poster>();
        var skipped = new List<string>();

        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { PosterFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) continue;

            // FindAssets RECURSES. Keep only direct children of PosterFolder, so an
            // image parked in a sub-folder can never silently become a poster.
            string dir = (System.IO.Path.GetDirectoryName(path) ?? "").Replace("\\", "/");
            if (dir != PosterFolder) continue;

            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (!PosterExtensions.Contains(ext)) continue;

            string file = System.IO.Path.GetFileNameWithoutExtension(path);
            Match m = rx.Match(file);
            if (!m.Success) { skipped.Add(file + ext); continue; }

            int n;
            if (!int.TryParse(m.Groups[1].Value,
                              System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out n))
            { skipped.Add(file + ext); continue; }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null)
                throw new System.Exception("Could not load the poster image as a texture: " + path);
            if (tex.width <= 0 || tex.height <= 0)
                throw new System.Exception("Poster image has no pixels: " + path);

            Poster clash;
            if (byOrder.TryGetValue(n, out clash))
                throw new System.Exception(
                    "Two posters claim hunt position " + n + ":\n\n" +
                    "   " + clash.texturePath + "\n" +
                    "   " + path + "\n\n" +
                    "The leading number IS the poster's identity — it becomes its id, its " +
                    "video and its place in the route. Renumber one of them.");

            byOrder[n] = new Poster
            {
                order       = n,
                id          = string.Format(System.Globalization.CultureInfo.InvariantCulture, IdFormat, n),
                label       = string.Format(System.Globalization.CultureInfo.InvariantCulture, LabelFormat, n),
                texturePath = path,
                texture     = tex,
            };
        }

        foreach (string s in skipped)
            Debug.LogWarning("[MemeHunt] Ignoring '" + s + "' in " + PosterFolder +
                             " — a poster file name must start with its hunt-order number, " +
                             "e.g. \"1 Target.jpg\". (Pattern: " + PosterFilePattern + ")");

        if (byOrder.Count == 0)
            throw new System.Exception(
                "No posters found in " + PosterFolder + " — there is no hunt to build.\n\n" +
                "A poster is an image (" + string.Join(", ", PosterExtensions) + ") sitting DIRECTLY in that " +
                "folder whose name starts with its hunt-order number:\n" +
                "   1 Target.jpg, 2 Target.jpg, 3 Target.jpg …\n\n" +
                (skipped.Count > 0
                    ? skipped.Count + " file(s) were skipped for not matching — see the Console."
                    : "The folder has no images in it at all."));

        if (byOrder.Count > MaxPosters)
            throw new System.Exception(
                byOrder.Count + " posters found, but the hunt server keeps only the first " + MaxPosters + ".\n\n" +
                "Its setting is a TEXT column, so a longer list is truncated mid-JSON and the tail simply " +
                "stops scoring — with no error anywhere. Split the campaign instead.");

        var posters = byOrder.Values.OrderBy(p => p.order).ToList();
        ValidatePosterIds(posters);

        Debug.Log("[MemeHunt] Discovered " + posters.Count + " poster(s) in " + PosterFolder + ":\n" +
                  string.Join("\n", posters.Select(p =>
                      "   " + p.order + "  " + p.id + "  (" + p.label + ")  ← " +
                      System.IO.Path.GetFileName(p.texturePath) +
                      "  " + p.texture.width + "×" + p.texture.height)));
        return posters;
    }

    /// <summary>Fills in each poster's video URL (convention, or its override) and
    /// records whether the matching LOCAL file exists yet. A missing clip is reported,
    /// never fatal: the scene is routinely built before the edit is finished, and the
    /// URL starts working the moment the file is committed and pushed.</summary>
    private static void ResolveVideos(List<Poster> posters)
    {
        string root = System.IO.Directory.GetParent(Application.dataPath).FullName;

        foreach (var p in posters)
        {
            string url;
            if (VideoUrlOverrides.TryGetValue(p.id, out url) && !string.IsNullOrEmpty(url))
            {
                p.videoUrl = url.Trim();
                p.videoUrlIsOverride = true;
                p.videoProjectPath = "";
                p.videoFilePresent = true;   // an override may live on any host — nothing to check
            }
            else
            {
                p.videoUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                           VideoUrlFormat, p.order).Trim();
                p.videoUrlIsOverride = false;
                p.videoProjectPath = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                   VideoProjectPathFormat, p.order);
                string full = System.IO.Path.Combine(root,
                    p.videoProjectPath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
                p.videoFilePresent = System.IO.File.Exists(full);
                if (p.videoFilePresent) { p.videoHash = FileHash(full); }
            }

            // Both of these are build-BLOCKING errors in PreBuildCheck, so they are
            // caught here where the message can name the poster.
            if (string.IsNullOrEmpty(p.videoUrl))
                throw new System.Exception("Poster '" + p.id + "' resolved to an empty video URL. " +
                    "A video plane with no URL is a blank rectangle, and the pre-flight check refuses to build it.");
            if (p.videoUrl.StartsWith("http://"))
                throw new System.Exception("Poster '" + p.id + "' has an http:// video URL:\n   " + p.videoUrl +
                    "\n\nThe AR page is served over https, so the browser blocks it as mixed content. Use https://.");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  CONTENT  — one poster, built procedurally from its own image
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Builds ONE tracked poster: root (carrying the tracking-image mesh) →
    /// Content (ARContentScale) → video plane. Returns the root.</summary>
    private static GameObject BuildPosterTarget(Poster p, ImageTracker tracker, Texture2D placeholder)
    {
        // Target root — the MESH carries the physical size, the transform stays
        // identity. ImageTracker.ParseData() hard-assigns localScale/position/rotation
        // on this object EVERY tracked frame, so nothing may be authored on it.
        float targetW = PhysicalWidth;
        float targetH = PhysicalWidth * ((float)p.texture.height / p.texture.width);

        var targetObj = new GameObject(p.id);
        targetObj.layer = 0;
        targetObj.transform.SetParent(tracker.transform, false);
        targetObj.transform.localPosition = new Vector3(0, 0, CardWorldZ);
        targetObj.transform.localRotation = Quaternion.identity;
        targetObj.transform.localScale    = Vector3.one;

        targetObj.AddComponent<MeshFilter>().sharedMesh =
            GetOrCreateMesh(targetW, targetH, p.id + "_TrackImg");
        // ImageTracker.Start() does GetComponent<Renderer>().enabled = false with no null
        // check — the MeshRenderer is MANDATORY even though the poster art never renders.
        targetObj.AddComponent<MeshRenderer>().sharedMaterial =
            GetOrCreateUnlitMaterial(MatFolder + "/" + p.id + "_Mat.mat", p.id + "_Mat", p.texture);

        // Content container — everything visible hangs off THIS, never off the target
        // root (ParseData overwrites the root's localScale every tracked frame).
        // ARContentScale owns its scale, so the size can be retuned live with ?scale=…
        // instead of costing a rebuild per guess.
        var contentObj = new GameObject("Content");
        contentObj.layer = 0;
        contentObj.transform.SetParent(targetObj.transform, false);
        contentObj.transform.localPosition = Vector3.zero;
        contentObj.transform.localRotation = Quaternion.identity;
        contentObj.transform.localScale    = Vector3.one * ContentScale;
        contentObj.AddComponent<ARContentScale>().scale = ContentScale;

        // Video plane — built at REFERENCE size (PhysicalWidth); the Content container
        // does the scaling, so the number lives in exactly one place.
        float vidW = PhysicalWidth;
        float vidH = PhysicalWidth * (VideoAspectOverride > 0f
                                        ? VideoAspectOverride
                                        : ((float)p.texture.height / p.texture.width));

        var vidObj = new GameObject(p.id + " vid");
        vidObj.layer = 0;
        vidObj.transform.SetParent(contentObj.transform, false);
        vidObj.transform.localPosition = new Vector3(0, 0, VideoWorldZ);
        vidObj.transform.localRotation = Quaternion.identity;
        vidObj.transform.localScale    = Vector3.one;

        vidObj.AddComponent<MeshFilter>().sharedMesh = GetOrCreateMesh(vidW, vidH, p.id + "_Vid");
        var vidRend = vidObj.AddComponent<MeshRenderer>();
        // 1×1 BLACK, never the poster art — see BlackTexturePath in CONFIG.
        vidRend.sharedMaterial = GetOrCreateUnlitMaterial(
            MatFolder + "/" + p.id + "_VidMat.mat", p.id + "_VidMat", placeholder);

        var vp = vidObj.AddComponent<VideoPlayer>();
        vp.playOnAwake = false;
        // NEVER vp.clip: a baked VideoClip is a build-blocking error (it would also
        // bloat the WebGL data file with the whole video). CDNARVideoController.Awake()
        // clones url → source → clip in that order, so the in-scene `source` always
        // wins — set it explicitly.
        vp.source = VideoSource.Url;
        vp.url    = p.videoUrl;
        vp.renderMode = VideoRenderMode.MaterialOverride;
        vp.targetMaterialRenderer = vidRend;
        vp.targetMaterialProperty = "_MainTex";   // the CDN controller copies this verbatim
        vp.isLooping         = true;
        vp.waitForFirstFrame = true;
        vp.skipOnDrop        = true;
        vp.audioOutputMode   = VideoAudioOutputMode.Direct;   // the default AudioSource mode is silent

        var cdn = vidObj.AddComponent<CDNARVideoController>();
        cdn.cdnVideoUrl = p.videoUrl;
        // Must EQUAL the tracker id — that is the key the iOS tap-for-sound unlock is
        // stored under. Nothing checks this: the pre-flight's sound-key check only
        // fires when the controller's parent is itself a scene target id, and the
        // "Content" container in between means it never is. Correct by construction.
        cdn.webGLSoundTargetKey = p.id;
        cdn.loopVideo = true;
        // Skip the 2-frame anti-flicker hold: nothing here depends on the video plane's
        // depth, so the hold only delays the first frame.
        // fastLoadMode draws the video plane from the instant of tracking-found, which
        // with the black pre-roll texture means a black rectangle over the poster until
        // the first frame decodes. Leaving it OFF keeps the plane hidden until
        // IsVideoTextureReady(), so the poster itself shows through instead.
        cdn.fastLoadMode = false;

        // Template convention: target roots start INACTIVE — ImageTracker re-activates
        // on tracking found. This is BEHAVIOURAL, not cosmetic: ImageTracker.Start()
        // deactivates the roots only AFTER the children's Awake/OnEnable have run, and
        // CDNARVideoController.OnEnable starts playback — so an active root means every
        // meme's audio plays at page load, before anything is scanned.
        targetObj.SetActive(false);
        return targetObj;
    }

    // ─────────────────────────────────────────────────────────────────
    //  GLOBAL REGISTRATION
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Add-or-update every discovered poster in ImageTrackerGlobalSettings —
    /// the list the build exports as &lt;imagetarget&gt; tags. A target missing from here
    /// is a build-blocking error: no tag is exported, so the camera can never recognise
    /// it. Loaded with Resources.Load rather than the Instance property so a stale
    /// static cache from an earlier domain reload can never be written back.</summary>
    private static void RegisterGlobalTargets(List<Poster> posters)
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null)
            throw new System.Exception("ImageTrackerGlobalSettings.asset not found in Resources.");
        if (gs.imageTargetInfos == null)
            gs.imageTargetInfos = new List<ImageTargetInfo>();

        // Two registered targets whose IMAGE FILES share a name is a build-blocking
        // error: the exporter copies them into <build>/targets/ by file name and one
        // silently overwrites the other.
        var byFileName = new Dictionary<string, string>();
        foreach (var p in posters)
        {
            string fn = System.IO.Path.GetFileName(p.texturePath);
            string other;
            if (byFileName.TryGetValue(fn, out other))
                throw new System.Exception("Posters '" + other + "' and '" + p.id +
                    "' use image files with the same name (" + fn + "). The build copies target " +
                    "images by file name, so one would overwrite the other.");
            byFileName[fn] = p.id;
        }

        foreach (var p in posters)
        {
            bool found = false;
            foreach (var info in gs.imageTargetInfos)
            {
                if (info.id != p.id) continue;
                info.texture = p.texture;
                found = true;
                break;
            }
            if (!found)
                gs.imageTargetInfos.Add(new ImageTargetInfo { id = p.id, texture = p.texture });
        }
        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();

        Debug.Log("[MemeHunt] ImageTrackerGlobalSettings now registers " +
                  gs.imageTargetInfos.Count + " target(s): " +
                  string.Join(", ", gs.imageTargetInfos.Select(i => i.id)));
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
                    Debug.Log("[MemeHunt] Disabling scene in Build Settings: " + s.path);
                list.Add(new EditorBuildSettingsScene(s.path, false));
            }
        }
        if (!present)
            list.Insert(0, new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────
    [MenuItem("Tools/Meme Hunt/2. Trim Global Targets To Hunt Posters (lean build)")]
    public static void TrimGlobalTargets()
    {
        List<Poster> posters;
        try
        {
            posters = DiscoverPosters();
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Meme Hunt — Error", e.Message, "OK");
            Debug.LogException(e);
            return;
        }

        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null || gs.imageTargetInfos == null)
        {
            EditorUtility.DisplayDialog("Meme Hunt", "ImageTrackerGlobalSettings.asset not found.", "OK");
            return;
        }

        var huntIds = new HashSet<string>(posters.Select(p => p.id));
        var removed = gs.imageTargetInfos.Where(i => !huntIds.Contains(i.id)).Select(i => i.id).ToList();
        if (removed.Count == 0)
        {
            EditorUtility.DisplayDialog("Meme Hunt",
                "Global target list already contains only the " + posters.Count + " hunt poster(s).", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Meme Hunt — Trim Global Targets",
            "This removes " + removed.Count + " non-hunt target(s) from ImageTrackerGlobalSettings:\n\n" +
            string.Join(", ", removed) + "\n\n" +
            "The browser downloads and feature-extracts every registered target at page load, so trimming " +
            "makes the hunt build start faster. This edits a SHARED asset — other campaign scenes will need " +
            "their targets re-registered before THEIR next build (re-run their builder, or restore with " +
            "git checkout -- " + AssetDatabase.GetAssetPath(gs) + ").",
            "Trim to " + posters.Count, "Cancel"))
            return;

        Debug.Log("[MemeHunt] Removed global targets: " + string.Join(", ", removed) +
                  "\nRestore the full list after this build: git checkout -- " + AssetDatabase.GetAssetPath(gs));
        gs.imageTargetInfos = gs.imageTargetInfos.Where(i => huntIds.Contains(i.id)).ToList();
        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Meme Hunt",
            "Global target list trimmed to the " + posters.Count + " hunt poster(s):\n\n" +
            string.Join(", ", posters.Select(p => p.id)), "OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  POSTER LIST EXPORT  (see EXPORTING THE POSTER LIST at the top)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Regenerates the poster-list JSON without touching the scene, puts it on
    /// the clipboard, writes it to &lt;projectRoot&gt;/hunt-posters.json and logs it.
    /// Re-runs discovery, so this is also the one to run after adding a poster or
    /// editing a hint — no scene rebuild needed just to tell the server.</summary>
    [MenuItem("Tools/Meme Hunt/3. Copy Poster List JSON")]
    public static void CopyPosterListMenu()
    {
        try
        {
            List<Poster> posters = DiscoverPosters();
            string json = BuildPosterListJson(posters);
            if (!Application.isBatchMode)
                EditorGUIUtility.systemCopyBuffer = json;
            string info = ExportPosterList(json);

            EditorUtility.DisplayDialog("Meme Hunt — Poster List",
                posters.Count + " poster(s), in hunt order:\n\n" +
                string.Join(", ", posters.Select(p => p.id)) + "\n\n" +
                info +
                "  • Copied to the clipboard\n\n" +
                "Paste it into hunt/admin.html ▸ Settings ▸ Poster list and Save — that is\n" +
                "how the server learns these ids. Clearing that setting puts the server\n" +
                "back on its own built-in defaults.", "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Meme Hunt — Error", e.Message, "OK");
            Debug.LogException(e);
        }
    }

    /// <summary>Logs the JSON (always) and writes it to &lt;projectRoot&gt;/hunt-posters.json
    /// (best effort). Returns a bullet line for the caller's dialog. Never throws: the
    /// Console copy is the one that actually reaches the admin textarea, and losing the
    /// file must not fail a scene build that has already succeeded.</summary>
    private static string ExportPosterList(string json)
    {
        // Logged FIRST so the copy-pasteable text exists even if the write below fails.
        Debug.Log("[MemeHunt] Poster list JSON — paste into hunt/admin.html ▸ Settings ▸ Poster list:\n" + json);
        try
        {
            string root = System.IO.Directory.GetParent(Application.dataPath).FullName;
            string full = System.IO.Path.Combine(root,
                PosterManifestProjectPath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
            // UTF8Encoding(false) => no BOM. A BOM in front of '[' makes JSON.parse and
            // json_decode both reject the file.
            System.IO.File.WriteAllText(full, json, new System.Text.UTF8Encoding(false));
            Debug.Log("[MemeHunt] Poster list written: " + full);
            return "  • Poster list written to " + PosterManifestProjectPath + " and logged to the Console\n";
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MemeHunt] Could not write " + PosterManifestProjectPath + ": " + e.Message);
            return "  • Poster list logged to the Console (the file could not be written — see Console)\n";
        }
    }

    /// <summary>Serialises the discovered posters to the exact shape hunt.php's
    /// 'poster_list' setting expects — a JSON ARRAY of {"id","label","hint"} objects,
    /// in hunt order:
    ///
    ///   [
    ///     {"id":"Target1","label":"Target 1","hint":"..."},
    ///     ...
    ///   ]
    ///
    /// Hand-built rather than JsonUtility, which cannot serialise a bare array at all
    /// (it only emits a top-level object). Output is deliberately 7-bit ASCII — every
    /// non-ASCII character is \uXXXX-escaped — so it survives the Console, the clipboard
    /// and the POST body byte-for-byte, whatever the encoding of what it passes through.
    /// That is still exactly the same decoded string to JSON.parse and json_decode.</summary>
    private static string BuildPosterListJson(List<Poster> posters)
    {
        ValidatePosterIds(posters);

        var sb = new System.Text.StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < posters.Count; i++)
        {
            var p = posters[i];
            // Same defaults the server applies: no label => the id; no hint => "".
            // An empty hint is legal and is the normal case — hunt/admin.html can write
            // hints live during the event without a rebuild.
            string rawHint;
            if (!Hints.TryGetValue(p.id, out rawHint) || rawHint == null) rawHint = "";

            string label = Clamp(string.IsNullOrEmpty(p.label) ? p.id : p.label, MaxLabelChars, p.id, "label");
            string hint  = Clamp(rawHint, MaxHintChars, p.id, "hint");

            sb.Append("  {\"id\":").Append(JsonString(p.id))
              .Append(",\"label\":").Append(JsonString(label))
              .Append(",\"hint\":").Append(JsonString(hint))
              .Append('}');
            if (i < posters.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("]\n");
        return sb.ToString();
    }

    /// <summary>The server drops any poster whose id fails its own check, and a dropped
    /// poster is invisible in the worst way — it tracks and plays but never scores. So
    /// this refuses to export (or to build a scene) that could not work. Unity itself
    /// only WARNS about a bad id, which is why this has to be a hard stop.</summary>
    private static void ValidatePosterIds(List<Poster> posters)
    {
        if (posters == null || posters.Count == 0)
            throw new System.Exception("No posters discovered — there is no hunt to build.");

        var seen = new HashSet<string>();
        foreach (var p in posters)
        {
            if (string.IsNullOrEmpty(p.id) || !Regex.IsMatch(p.id, PosterIdPattern))
                throw new System.Exception(
                    "Poster id '" + p.id + "' (from " + p.texturePath + ") is not a usable hunt id.\n\n" +
                    "Allowed: letters, digits, '_' and '-', 1-64 characters. The server rejects anything " +
                    "else, and a rejected poster tracks and plays its video but never ticks its chip.\n\n" +
                    "Ids are generated from IdFormat in CONFIG — check it has no spaces.");
            if (!seen.Add(p.id))
                throw new System.Exception("Two posters generated the same id: " + p.id +
                    ". Check IdFormat in CONFIG — it must include {0}.");
        }
    }

    /// <summary>Truncates to the server's limit, loudly, so what Unity reports is what the
    /// server will actually store.</summary>
    private static string Clamp(string s, int max, string posterId, string field)
    {
        if (s.Length <= max) return s;
        int cut = max;
        // Never cut between a surrogate pair: the lone half would be an unpaired
        // \uD800-range escape, which json_decode rejects outright.
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        Debug.LogWarning("[MemeHunt] Poster '" + posterId + "' " + field + " is " + s.Length +
                         " characters; the server keeps only the first " + max +
                         ". Exporting the truncated value.");
        return s.Substring(0, cut);
    }

    /// <summary>One JSON string literal, quotes included.</summary>
    private static string JsonString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    // < 0x20 is illegal raw inside a JSON string; everything above plain
                    // ASCII is escaped by choice, to keep the payload 7-bit clean.
                    if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else                      sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
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

    /// <summary>Removes the template widgets that would break the hunt:
    /// "Button Menu" (its confirm calls ImageTracker.StopTracker → freezes all
    /// tracking), every demo GoToUrl (on WebGL that is window.location.assign, which
    /// navigates the AR tab away mid-hunt and loses the player's session), optionally
    /// "LogoAndLink", and any UIManager left dereferencing a button we deleted.
    /// Must run BEFORE the posters are built.</summary>
    private static string StripTemplateDemoUi(Scene scene)
    {
        var removed = new List<string>();
        var doomed = new List<GameObject>();

        // A hunt scene has NO in-Unity UI at all: every pixel the visitor sees is
        // either the camera feed, a tracked video, or hunt-overlay.js in the DOM.
        // Demo-Video.unity ships a full demo onboarding UI — a root "FirstCanvas"
        // (ScreenSpaceOverlay, sortingOrder 1, i.e. ABOVE the other Canvas) plus
        // "Panel Main" — which would otherwise sit as a splash screen over the whole
        // hunt. Strip the template's screen UI wholesale, by name, at any depth.
        foreach (string uiName in TemplateScreenUiToStrip)
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                var found = go.name == uiName ? go.transform : FindChildRecursive(go.transform, uiName);
                if (found == null) continue;
                if (doomed.Contains(found.gameObject)) continue;
                doomed.Add(found.gameObject);
                removed.Add(uiName + " (template demo UI)");
                break;
            }
        }

        foreach (var go in scene.GetRootGameObjects())
        {
            var menu = FindChildRecursive(go.transform, "Button Menu");
            if (menu == null) continue;
            if (doomed.Contains(menu.gameObject)) continue;
            doomed.Add(menu.gameObject);
            removed.Add("Button Menu (StopTracker)");
            break;
        }

        if (RemoveTemplateCtaButton)
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                var cta = FindChildRecursive(go.transform, "LogoAndLink");
                if (cta == null) continue;
                doomed.Add(cta.gameObject);
                removed.Add("LogoAndLink (hard-coded CTA)");
                break;
            }
        }

        foreach (var g in Object.FindObjectsByType<Imagine.WebAR.Samples.GoToUrl>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (g == null || doomed.Contains(g.gameObject)) continue;
            doomed.Add(g.gameObject);
            removed.Add("demo GoToUrl '" + g.gameObject.name + "'");
        }

        foreach (var go in doomed)
            if (go != null) Object.DestroyImmediate(go);

        // UIManager.Start() does contectButton.onClick.AddListener(...) with no null
        // check — leaving it behind after its button died is a guaranteed NRE at load.
        foreach (var ui in Object.FindObjectsByType<UIManager>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (ui == null || ui.contectButton != null) continue;
            Object.DestroyImmediate(ui);
            removed.Add("UIManager component (its CTA button is gone — Start() would NRE)");
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

    /// <summary>Quad mesh, XY-vertical, centred on the origin, normals Vector3.back so it
    /// faces a camera looking down +Z. Cached per size, so a rebuild reuses it.</summary>
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
        Debug.Log("[MemeHunt] Mesh generated: " + path + "  (" + w + " × " + h + " units)");
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

    /// <summary>The video plane's pre-roll fill: a 1×1 black texture, generated once.
    /// Deliberately NOT the poster art — that would paint a ghost second copy of the
    /// poster over the real printed one for the whole time the CDN is buffering.</summary>
    private static Texture2D GetOrCreateBlackTexture()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(BlackTexturePath);
        if (existing != null) return existing;

        var tmp = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tmp.SetPixel(0, 0, Color.black);
        tmp.Apply();
        byte[] png = ImageConversion.EncodeToPNG(tmp);
        Object.DestroyImmediate(tmp);

        string root = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string full = System.IO.Path.Combine(root,
            BlackTexturePath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
        string dir = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(full, png);
        AssetDatabase.ImportAsset(BlackTexturePath, ImportAssetOptions.ForceSynchronousImport);

        // Uncompressed and mip-free: 4 bytes, and no chance of a compression artefact
        // turning "black" into "nearly black" on some platform.
        var imp = AssetImporter.GetAtPath(BlackTexturePath) as TextureImporter;
        if (imp != null)
        {
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.mipmapEnabled = false;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
        }

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(BlackTexturePath);
        if (tex == null)
            throw new System.Exception("Could not create the video placeholder texture: " + BlackTexturePath);
        Debug.Log("[MemeHunt] Video placeholder texture generated: " + BlackTexturePath);
        return tex;
    }

    /// <summary>Single-level folder creation — call it for each ancestor, in order.</summary>
    private static void EnsureFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        string parent = (System.IO.Path.GetDirectoryName(assetPath) ?? "").Replace("\\", "/");
        string folder = System.IO.Path.GetFileName(assetPath);
        if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folder))
            AssetDatabase.CreateFolder(parent, folder);
    }

    // ─────────────────────────────────────────────────────────────────
    //  REPORT
    // ─────────────────────────────────────────────────────────────────

    private static string BuildReport(List<Poster> posters, string strippedInfo, string posterInfo)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("MemeHunt scene created at ").Append(NewScenePath)
          .Append(" with ").Append(posters.Count).Append(" poster(s), in hunt order:\n\n");

        foreach (var p in posters)
            sb.Append("  ").Append(p.order).Append(". ").Append(p.id)
              .Append("  \"").Append(p.label).Append("\"\n")
              .Append("       image: ").Append(System.IO.Path.GetFileName(p.texturePath))
              .Append("  (").Append(p.texture.width).Append("×").Append(p.texture.height).Append(")\n")
              .Append("       video: ").Append(p.videoUrl)
              .Append(p.videoUrlIsOverride ? "   [CONFIG override]" : "").Append('\n');

        sb.Append('\n');

        var missing = posters.Where(p => !p.videoFilePresent).ToList();
        if (missing.Count == 0)
        {
            sb.Append("  • All ").Append(posters.Count)
              .Append(" meme video(s) are present on disk.\n")
              .Append("    Commit and push videos/ so jsDelivr serves them.\n");
        }
        else
        {
            // The point of this list: the clips are usually still being edited when the
            // scene is first built. The build is fine — these are the files to make.
            sb.Append("  • ").Append(missing.Count).Append(" MEME VIDEO(S) STILL MISSING — create these files:\n");
            foreach (var p in missing)
                sb.Append("       ").Append(p.videoProjectPath)
                  .Append("        (for ").Append(p.id).Append(")\n");
            sb.Append("    The scene is built and correct: each poster already points at its CDN url,\n")
              .Append("    which starts working the moment that file is committed and pushed.\n")
              .Append("    Until then that poster tracks and scores, but plays nothing.\n");
        }

        // "Present" is not the same as "different". Report posters that ship the SAME
        // clip — three identical files pass every existence check while three posters
        // play one meme, and nobody notices until the event.
        var dupeGroups = posters
            .Where(p => !string.IsNullOrEmpty(p.videoHash))
            .GroupBy(p => p.videoHash)
            .Where(g => g.Count() > 1)
            .ToList();
        if (dupeGroups.Count > 0)
        {
            sb.Append("\n  • ⚠ DUPLICATE MEME VIDEOS — these posters play the SAME clip:\n");
            foreach (var g in dupeGroups)
                sb.Append("       ")
                  .Append(string.Join(" = ", g.Select(p => p.id + " (" +
                          System.IO.Path.GetFileName(p.videoProjectPath) + ")").ToArray()))
                  .Append('\n');
            sb.Append("    Byte-identical files. Replace them with the real clips before the event —\n")
              .Append("    the hunt still scores correctly, it just is not five different memes.\n");
        }

        sb.Append(strippedInfo);
        sb.Append(posterInfo);
        sb.Append('\n');
        sb.Append("MemeHunt.unity is now the only enabled scene in Build Settings.\n\n");
        sb.Append("Next:\n");
        sb.Append("  1. Paste hunt-posters.json into hunt/admin.html ▸ Settings ▸ Poster list and Save.\n");
        sb.Append("     Without that the server scores against its OWN built-in ids and every scan of\n");
        sb.Append("     these posters is rejected — while the video still plays, so it looks fine.\n");
        sb.Append("  2. File ▸ Build Settings ▸ WebGL ▸ Build (template iTracker).\n");
        return sb.ToString();
    }

    private static void Progress(string step, float t)
    {
        if (!Application.isBatchMode)
            EditorUtility.DisplayProgressBar("Meme Hunt Scene Builder", step, t);
    }
}
