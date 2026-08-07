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
/// One-click generator for the 5-poster AR Meme Hunt scene:
///   • Clones the Demo-Video.unity template
///   • Keeps its proven BookCover / CultGym / Shoes targets (working CDN videos)
///   • Rebuilds FIFA_Target / One8Traget from the existing campaign meshes + materials
///   • Rewires the ImageTracker to exactly these 5 targets
///   • Verifies all 5 are registered in ImageTrackerGlobalSettings (build-time export)
///   • Makes MemeHunt the only enabled scene in Build Settings
///
/// The hunt UI itself lives in the WebGL template (hunt-overlay.js) — no scene
/// UI changes are needed; ImageTracker's analytics bridge feeds the overlay.
/// </summary>
public static class MemeHuntSceneBuilder
{
    private const string TemplatePath = "Assets/Scenes_1/Demo-Video.unity";
    private const string NewScenePath = "Assets/Scenes_1/MemeHunt.unity";

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
    }

    // The 5 posters — ids must match ImageTrackerGlobalSettings + hunt.php config
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
        },
        new HuntPoster { id = "BookCover", texturePath = "Assets/AR_Assets/BookCover/Book_Cover_AR_Target_Image.png", keepFromTemplate = true },
        new HuntPoster { id = "CultGym",   texturePath = "Assets/AR_Assets/GYM Poster/GYM Video Target.png",          keepFromTemplate = true },
        new HuntPoster { id = "Shoes",     texturePath = "Assets/AR_Assets/Images/Shoes_Poster.png",                  keepFromTemplate = true },
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

        // 1. Register all 5 targets in the global settings (build-time export source)
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

        // 6. Rewrite the tracker's imageTargets list with exactly the 5 posters
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

        // 8. Save + build settings
        Progress("Saving scene", 0.85f);
        EditorSceneManager.MarkSceneDirty(newScene);
        EditorSceneManager.SaveScene(newScene);
        SetAsOnlyEnabledScene(NewScenePath);

        var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(NewScenePath);
        if (sceneAsset != null && !Application.isBatchMode)
        {
            EditorGUIUtility.PingObject(sceneAsset);
            Selection.activeObject = sceneAsset;
        }

        EditorUtility.ClearProgressBar();
        return "MemeHunt scene created at " + NewScenePath + " with 5 targets:\n" +
               string.Join(", ", Posters.Select(p => p.id)) + "\n\n" +
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
            EditorUtility.DisplayDialog("Meme Hunt", "Global target list already contains only the 5 hunt targets.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("Meme Hunt — Trim Global Targets",
            "This removes " + removed.Count + " non-hunt targets from ImageTrackerGlobalSettings:\n\n" +
            string.Join(", ", removed) + "\n\n" +
            "The browser feature-extracts every registered target at page load, so trimming makes the " +
            "hunt build start faster. Other campaign scenes will need their targets re-registered " +
            "before THEIR next build (their wizards / this dialog's log has the list).",
            "Trim to 5", "Cancel"))
            return;

        Debug.Log("[MemeHunt] Removed global targets: " + string.Join(", ", removed));
        gs.imageTargetInfos = gs.imageTargetInfos.Where(i => huntIds.Contains(i.id)).ToList();
        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
        EditorUtility.DisplayDialog("Meme Hunt", "Global target list trimmed to the 5 hunt targets.", "OK");
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
