using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Imagine.WebAR;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Meme Hunt Scene Builder — Bharatiya Vyapar Mahotsav 2026
///
/// One-click generator for the AR Meme Hunt scene:
///   • Clones the Demo-Video.unity template
///   • Keeps its proven BookCover / CultGym / Shoes targets (working CDN videos)
///   • Rebuilds FIFA_Target / One8Traget from the existing campaign meshes + materials
///   • Rewires the ImageTracker to exactly the posters in the Posters table below
///   • Verifies every one is registered in ImageTrackerGlobalSettings (build-time export)
///   • Makes MemeHunt the only enabled scene in Build Settings
///   • Exports the poster list as JSON (see EXPORTING THE POSTER LIST) so the server
///     can LEARN the ids instead of hard-coding a second copy of them
///
/// The hunt UI itself lives in the WebGL template (hunt-overlay.js) — no scene
/// UI changes are needed; ImageTracker's analytics bridge feeds the overlay.
///
/// ─── EXPORTING THE POSTER LIST ──────────────────────────────────────────
/// The target ids are baked into the WebGL build and cannot be changed after it
/// ships, so UNITY OWNS THEM and the server is told what they are. Building the
/// scene (or Tools ▸ Meme Hunt ▸ 3.) writes &lt;projectRoot&gt;/hunt-posters.json and
/// logs the same JSON to the Console; paste it into hunt/admin.html ▸ Settings ▸
/// Poster list. With that setting empty the server stays on its own built-in
/// defaults, which is exactly the behaviour that shipped for the 12-15 Aug event.
/// </summary>
public static class MemeHuntSceneBuilder
{
    private const string TemplatePath = "Assets/Scenes_1/Demo-Video.unity";
    private const string NewScenePath = "Assets/Scenes_1/MemeHunt.unity";

    // Poster-list export. Path is relative to <projectRoot> (the folder holding
    // Assets/), so the file sits outside Assets/ and Unity never imports it.
    private const string PosterManifestProjectPath = "hunt-posters.json";
    // Mirrors of the server's own validation (hunt.php ▸ huntPosters): an entry that
    // fails these is DROPPED there, and a dropped poster is a silent failure — the
    // camera tracks it and the video plays, but the scan is rejected and the chip
    // never ticks. So the same rules are enforced here, at authoring time.
    private const string PosterIdPattern = "^[A-Za-z0-9_-]{1,64}$";
    private const int MaxLabelChars = 20;
    private const int MaxHintChars  = 300;

    private class HuntPoster
    {
        public string id;
        public string texturePath;
        public bool keepFromTemplate;       // already exists in Demo-Video.unity with a working video
        public string meshPath;             // for rebuilt targets
        public string materialPath;         // for rebuilt targets
        public string cdnUrl;               // for rebuilt targets
        public Vector3 vidLocalPos;
        public Vector3 vidLocalScale;
        public string label;                // hunt chip caption, <= 20 chars (empty => the id)
        public string hint;                 // "find this next" text, <= 300 chars (empty => "")
    }

    // The posters — ids must match ImageTrackerGlobalSettings, and the server must be
    // told about them (see EXPORTING THE POSTER LIST above).
    //
    // Order = canonical hunt order: sequential mode and the "next hint" both walk this
    // list top-down, so it must read as the route a visitor actually takes.
    //
    // label/hint below are the same strings hunt.php ships as its built-in defaults, so
    // exporting this table today reproduces exactly what the live server already serves.
    // They are DEFAULTS: hunt/admin.html ▸ Settings can still override any label/hint
    // per poster, and those overrides are applied on top of whichever list is in force.
    private static readonly List<HuntPoster> Posters = new List<HuntPoster>
    {
        new HuntPoster {
            id = "FIFA_Target",
            texturePath = "Assets/AR_Assets/Images/Fifa Target Image.jpg",
            keepFromTemplate = false,
            meshPath = "Assets/AR_Assets/Planes/Generated/FIFA_Target_TrackImg_1.00x1.33.mesh",
            materialPath = "Assets/AR_Assets/Materials/FIFA_Target_Mat.mat",
            cdnUrl = "https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/Fifa%20Video.mp4",
            vidLocalPos = new Vector3(0f, 0f, -0.01f),
            vidLocalScale = new Vector3(0.755f, 0.755f, 1f),
            label = "FIFA",
            hint = "Kick-off ho chuka hai! Football wala poster dhoondo — jahan game ki baat hoti hai, FIFA card wahin hai.",
        },
        new HuntPoster {
            id = "One8Traget",
            texturePath = "Assets/AR_Assets/Images/One8.png",
            keepFromTemplate = false,
            meshPath = "Assets/AR_Assets/Planes/Generated/One8Traget_TrackImg_1.00x1.78.mesh",
            materialPath = "Assets/AR_Assets/Materials/One8Traget_Mat.mat",
            cdnUrl = "https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/one8.mp4",
            vidLocalPos = new Vector3(0f, 0f, -0.01f),
            vidLocalScale = new Vector3(0.57f, 0.57f, 1f),
            label = "One8",
            hint = "Ab thodi King Kohli wali energy! One8 shoes ka poster aas-paas hi hai — sneakerheads ko turant dikh jayega.",
        },
        new HuntPoster {
            id = "BookCover", texturePath = "Assets/AR_Assets/BookCover/Book_Cover_AR_Target_Image.png", keepFromTemplate = true,
            label = "Book",
            hint = "Ab thoda intellectual bano — ek book cover ka poster dhoondo. Padhai nahi karni, bas scan karna hai!",
        },
        new HuntPoster {
            id = "CultGym", texturePath = "Assets/AR_Assets/GYM Poster/GYM Video Target.png", keepFromTemplate = true,
            label = "Gym",
            hint = "Networking zyada, patience kam? Gym poster ke paas jao — gains yahin milenge.",
        },
        new HuntPoster {
            id = "Shoes", texturePath = "Assets/AR_Assets/Images/Shoes_Poster.png", keepFromTemplate = true,
            label = "Shoes",
            hint = "Last one! Jo shoes sabse zyada chamak rahe hain, wahi poster scan karna hai. Finish line paas hai!",
        },
    };

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

        Progress("Validating assets", 0.05f);
        // Ids first: an id the server would reject can never be scanned, and finding
        // that out AFTER the scene is rewritten helps nobody.
        ValidatePosterIds();
        foreach (var p in Posters)
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(p.texturePath) == null)
                throw new System.Exception("Missing target texture: " + p.texturePath);
            if (!p.keepFromTemplate)
            {
                if (AssetDatabase.LoadAssetAtPath<Mesh>(p.meshPath) == null)
                    throw new System.Exception("Missing mesh: " + p.meshPath);
                if (AssetDatabase.LoadAssetAtPath<Material>(p.materialPath) == null)
                    throw new System.Exception("Missing material: " + p.materialPath);
            }
        }

        // 1. Register every target in the global settings (build-time export source)
        Progress("Registering global image targets", 0.15f);
        RegisterGlobalTargets();

        // 2. Clone the template scene
        Progress("Cloning template scene", 0.3f);
        if (System.IO.File.Exists(NewScenePath))
            AssetDatabase.DeleteAsset(NewScenePath);
        if (!AssetDatabase.CopyAsset(TemplatePath, NewScenePath))
            throw new System.Exception("Could not copy template scene: " + TemplatePath);
        AssetDatabase.Refresh();
        Scene newScene = EditorSceneManager.OpenScene(NewScenePath, OpenSceneMode.Single);

        // 3. Find the ImageTracker
        ImageTracker tracker = null;
        foreach (var go in newScene.GetRootGameObjects())
        {
            tracker = go.GetComponentInChildren<ImageTracker>(true);
            if (tracker != null) break;
        }
        if (tracker == null)
            throw new System.Exception("No ImageTracker found in the template scene.");

        // 4. Sort template targets: keep the wanted ones, destroy the rest
        Progress("Rewiring image targets", 0.5f);
        var keepIds = new HashSet<string>(Posters.Where(p => p.keepFromTemplate).Select(p => p.id));
        var keptTransforms = new Dictionary<string, Transform>();

        var trackerSo = new SerializedObject(tracker);
        var targetsProp = trackerSo.FindProperty("imageTargets");
        for (int i = 0; i < targetsProp.arraySize; i++)
        {
            var elem = targetsProp.GetArrayElementAtIndex(i);
            string id = elem.FindPropertyRelative("id").stringValue;
            var t = elem.FindPropertyRelative("transform").objectReferenceValue as Transform;
            if (t == null) continue;
            if (keepIds.Contains(id) && !keptTransforms.ContainsKey(id))
                keptTransforms[id] = t;
            else
                Object.DestroyImmediate(t.gameObject);
        }
        foreach (string id in keepIds)
        {
            if (!keptTransforms.ContainsKey(id))
                throw new System.Exception("Template is missing expected target '" + id + "'.");
        }

        // 5. Build FIFA_Target + One8Traget from the proven campaign assets
        Progress("Building FIFA + One8 targets", 0.65f);
        var allTransforms = new Dictionary<string, Transform>(keptTransforms);
        foreach (var p in Posters.Where(x => !x.keepFromTemplate))
            allTransforms[p.id] = CreateVideoTarget(tracker.transform, p);

        // 6. Rewrite the tracker's imageTargets list with exactly the hunt posters
        targetsProp.ClearArray();
        for (int i = 0; i < Posters.Count; i++)
        {
            targetsProp.InsertArrayElementAtIndex(i);
            var elem = targetsProp.GetArrayElementAtIndex(i);
            elem.FindPropertyRelative("id").stringValue = Posters[i].id;
            elem.FindPropertyRelative("transform").objectReferenceValue = allTransforms[Posters[i].id];
        }
        // CAMERA_ORIGIN is the safe origin mode for a multi-target hunt scene
        var originProp = trackerSo.FindProperty("trackerOrigin");
        if (originProp != null) originProp.enumValueIndex = 0;
        trackerSo.ApplyModifiedProperties();

        // 7. Give every kept target's video controller an explicit sound key
        foreach (var kv in keptTransforms)
        {
            var cdn = kv.Value.GetComponentInChildren<CDNARVideoController>(true);
            if (cdn == null) continue;
            var so = new SerializedObject(cdn);
            so.FindProperty("webGLSoundTargetKey").stringValue = kv.Key;
            so.ApplyModifiedProperties();
        }

        // 7b. Strip demo UI that would hijack the hunt:
        //  - GoToUrl CTA buttons (Amazon/Flipkart on BookCover) navigate the tab
        //    away from the AR page mid-hunt via window.location.assign
        //  - the screen-space "Button Menu" opens a dialog whose confirm calls
        //    ImageTracker.StopTracker(), freezing all tracking
        Progress("Removing demo UI", 0.75f);
        foreach (var goToUrl in Object.FindObjectsByType<Imagine.WebAR.Samples.GoToUrl>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            Debug.Log("[MemeHunt] Removing demo CTA button: " + goToUrl.gameObject.name);
            Object.DestroyImmediate(goToUrl.gameObject);
        }
        foreach (var go in newScene.GetRootGameObjects())
        {
            var menu = FindChildRecursive(go.transform, "Button Menu");
            if (menu != null)
            {
                Debug.Log("[MemeHunt] Removing demo menu button under: " + go.name);
                Object.DestroyImmediate(menu.gameObject);
            }
        }

        // 7b. Declare this scene as a HUNT campaign. HuntFlagPostBuild reads this at
        //     build time and switches the hunt overlay on in the built index.html, so
        //     registration + chips + timer + leaderboard ship automatically.
        var campaignGo = new GameObject("Campaign Settings");
        var campaign = campaignGo.AddComponent<CampaignSettings>();
        campaign.huntEnabled  = true;
        campaign.campaignName = "AR Meme Hunt — Bharatiya Vyapar Mahotsav 2026";

        // 8. Save + build settings
        Progress("Saving scene", 0.85f);
        EditorSceneManager.MarkSceneDirty(newScene);
        EditorSceneManager.SaveScene(newScene);
        SetAsOnlyEnabledScene(NewScenePath);

        // 9. Hand the ids to the server. The scene is already saved at this point, so a
        //    failure here cannot cost the build — ExportPosterList reports and moves on.
        Progress("Exporting poster list", 0.95f);
        string posterInfo = ExportPosterList(BuildPosterListJson());

        var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(NewScenePath);
        if (sceneAsset != null && !Application.isBatchMode)
        {
            EditorGUIUtility.PingObject(sceneAsset);
            Selection.activeObject = sceneAsset;
        }

        EditorUtility.ClearProgressBar();
        return "MemeHunt scene created at " + NewScenePath + " with " + Posters.Count + " targets:\n" +
               string.Join(", ", Posters.Select(p => p.id)) + "\n\n" +
               posterInfo + "\n" +
               "It is now the only enabled scene in Build Settings.\n" +
               "Next: File > Build Settings > WebGL > Build (template iTracker).";
    }

    // ─────────────────────────────────────────────────────────────────
    private static Transform CreateVideoTarget(Transform trackerRoot, HuntPoster p)
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(p.meshPath);
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(p.materialPath);

        var targetObj = new GameObject(p.id);
        targetObj.transform.SetParent(trackerRoot, false);
        targetObj.transform.localPosition = Vector3.zero;
        targetObj.transform.localRotation = Quaternion.identity;
        targetObj.transform.localScale = Vector3.one;
        targetObj.AddComponent<MeshFilter>().sharedMesh = mesh;
        targetObj.AddComponent<MeshRenderer>().sharedMaterial = mat;

        var vidObj = new GameObject(p.id + " vid");
        vidObj.transform.SetParent(targetObj.transform, false);
        vidObj.transform.localPosition = p.vidLocalPos;
        vidObj.transform.localRotation = Quaternion.identity;
        vidObj.transform.localScale = p.vidLocalScale;
        vidObj.AddComponent<MeshFilter>().sharedMesh = mesh;
        var vidRend = vidObj.AddComponent<MeshRenderer>();
        vidRend.sharedMaterial = mat;

        var vp = vidObj.AddComponent<VideoPlayer>();
        vp.playOnAwake = false;
        vp.source = VideoSource.Url;
        vp.url = p.cdnUrl;
        vp.renderMode = VideoRenderMode.MaterialOverride;
        vp.targetMaterialRenderer = vidRend;
        vp.isLooping = true;
        vp.waitForFirstFrame = true;
        vp.skipOnDrop = true;
        vp.audioOutputMode = VideoAudioOutputMode.Direct;

        var cdn = vidObj.AddComponent<CDNARVideoController>();
        cdn.cdnVideoUrl = p.cdnUrl;
        cdn.webGLSoundTargetKey = p.id;

        // Template convention: target roots start inactive; the tracker activates
        // them when the image is found.
        targetObj.SetActive(false);
        return targetObj.transform;
    }

    private static void RegisterGlobalTargets()
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null)
            throw new System.Exception("ImageTrackerGlobalSettings.asset not found in Resources.");
        if (gs.imageTargetInfos == null)
            gs.imageTargetInfos = new List<ImageTargetInfo>();

        foreach (var p in Posters)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p.texturePath);
            bool found = false;
            foreach (var info in gs.imageTargetInfos)
            {
                if (info.id != p.id) continue;
                info.texture = tex;
                found = true;
                break;
            }
            if (!found)
                gs.imageTargetInfos.Add(new ImageTargetInfo { id = p.id, texture = tex });
        }
        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
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
    [MenuItem("Tools/Meme Hunt/2. Trim Global Targets To Hunt 5 (lean build)")]
    public static void TrimGlobalTargets()
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null || gs.imageTargetInfos == null)
        {
            EditorUtility.DisplayDialog("Meme Hunt", "ImageTrackerGlobalSettings.asset not found.", "OK");
            return;
        }
        var huntIds = new HashSet<string>(Posters.Select(p => p.id));
        var removed = gs.imageTargetInfos.Where(i => !huntIds.Contains(i.id)).Select(i => i.id).ToList();
        if (removed.Count == 0)
        {
            EditorUtility.DisplayDialog("Meme Hunt",
                "Global target list already contains only the " + Posters.Count + " hunt targets.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Meme Hunt — Trim Global Targets",
            "This removes " + removed.Count + " non-hunt targets from ImageTrackerGlobalSettings:\n\n" +
            string.Join(", ", removed) + "\n\n" +
            "The browser feature-extracts every registered target at page load, so trimming makes the " +
            "hunt build start faster. Other campaign scenes will need their targets re-registered " +
            "before THEIR next build (their wizards / this dialog's log has the list).",
            "Trim to " + Posters.Count, "Cancel"))
            return;

        Debug.Log("[MemeHunt] Removed global targets: " + string.Join(", ", removed));
        gs.imageTargetInfos = gs.imageTargetInfos.Where(i => huntIds.Contains(i.id)).ToList();
        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Meme Hunt",
            "Global target list trimmed to the " + Posters.Count + " hunt targets.", "OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  POSTER LIST EXPORT  (see EXPORTING THE POSTER LIST at the top)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Regenerates the poster-list JSON without touching the scene, puts it on
    /// the clipboard, writes it to &lt;projectRoot&gt;/hunt-posters.json and logs it.
    /// This is the one to run after editing a label or a hint.</summary>
    [MenuItem("Tools/Meme Hunt/3. Copy Poster List JSON")]
    public static void CopyPosterListMenu()
    {
        try
        {
            string json = BuildPosterListJson();
            if (!Application.isBatchMode)
                EditorGUIUtility.systemCopyBuffer = json;
            string info = ExportPosterList(json);

            EditorUtility.DisplayDialog("Meme Hunt — Poster List",
                Posters.Count + " posters, in hunt order:\n\n" +
                string.Join(", ", Posters.Select(p => p.id)) + "\n\n" +
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

    /// <summary>Serialises the Posters table to the exact shape hunt.php's 'poster_list'
    /// setting expects — a JSON ARRAY of {"id","label","hint"} objects, in hunt order:
    ///
    ///   [
    ///     {"id":"FIFA_Target","label":"FIFA","hint":"..."},
    ///     ...
    ///   ]
    ///
    /// Hand-built rather than JsonUtility, which cannot serialise a bare array at all
    /// (it only emits a top-level object). Output is deliberately 7-bit ASCII — every
    /// non-ASCII character is \uXXXX-escaped — so it survives the Console, the clipboard
    /// and the POST body byte-for-byte, whatever the encoding of what it passes through.
    /// That is still exactly the same decoded string to JSON.parse and json_decode.</summary>
    private static string BuildPosterListJson()
    {
        ValidatePosterIds();

        var sb = new System.Text.StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < Posters.Count; i++)
        {
            var p = Posters[i];
            // Same defaults the server applies: no label => the id; no hint => "".
            string label = Clamp(string.IsNullOrEmpty(p.label) ? p.id : p.label, MaxLabelChars, p.id, "label");
            string hint  = Clamp(p.hint ?? "", MaxHintChars, p.id, "hint");

            sb.Append("  {\"id\":").Append(JsonString(p.id))
              .Append(",\"label\":").Append(JsonString(label))
              .Append(",\"hint\":").Append(JsonString(hint))
              .Append('}');
            if (i < Posters.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("]\n");
        return sb.ToString();
    }

    /// <summary>The server drops any poster whose id fails its own check, and a dropped
    /// poster is invisible in the worst way — it tracks and plays but never scores. So
    /// this refuses to export (or to build a scene) that could not work.</summary>
    private static void ValidatePosterIds()
    {
        if (Posters.Count == 0)
            throw new System.Exception("The Posters table is empty — there is no hunt to build.");

        var seen = new HashSet<string>();
        foreach (var p in Posters)
        {
            if (string.IsNullOrEmpty(p.id) ||
                !System.Text.RegularExpressions.Regex.IsMatch(p.id, PosterIdPattern))
                throw new System.Exception(
                    "Poster id '" + p.id + "' is not a usable hunt id.\n\n" +
                    "Allowed: letters, digits, '_' and '-', 1-64 characters. The server " +
                    "rejects anything else, and a rejected poster tracks and plays its " +
                    "video but never ticks its chip.");
            if (!seen.Add(p.id))
                throw new System.Exception("Duplicate poster id in the Posters table: " + p.id);
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

    private static Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        for (int i = 0; i < parent.childCount; i++)
        {
            var found = FindChildRecursive(parent.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static void Progress(string step, float t)
    {
        if (!Application.isBatchMode)
            EditorUtility.DisplayProgressBar("Meme Hunt Scene Builder", step, t);
    }
}
