using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Imagine.WebAR;
using System.Collections.Generic;

/// <summary>
/// AR Video Scene Generator
/// Reads target image and video pixel dimensions, auto-generates correctly
/// sized plane meshes at runtime, and wires up the full scene hierarchy.
/// For green screen videos: Parent = target image BG mesh, Child = chroma key video mesh.
/// </summary>
public class ARAutomationWindow : EditorWindow
{
    // ─── Core inputs ────────────────────────────────────────────────
    private string sceneName      = "AutoScene_1";
    private string targetId       = "NewTarget";
    private Texture2D imageTexture;
    private string cdnVideoUrl    = "https://";
    private float physicalWidth   = 1.0f; // Added physical width for mesh generation

    // ─── Tracking image mesh ─────────────────────────────────────────
    private bool overrideImageMesh = false;
    private Mesh customImageMesh;

    // ─── Green screen ────────────────────────────────────────────────
    private bool isGreenScreen = false;
    private Texture2D firstFrameTexture;

    // Video layer dimensions (pixels) – used to auto-generate a mesh
    private int videoWidthPx  = 1080;
    private int videoHeightPx = 1920;
    private float videoScale  = 1.0f;     // Added scale tweak for varying green screen videos
    private Vector2 videoOffset = Vector2.zero; // Added position offset tweak
    private bool overrideVideoMesh = false;
    private Mesh customVideoMesh;

    // ─── Mesh save folder ────────────────────────────────────────────
    private const string MeshFolder = "Assets/AR_Assets/Planes/Generated";
    private const string MatFolder  = "Assets/AR_Assets/Materials";
    private const string TemplatePath = "Assets/Scenes_1/Demo-Video.unity";

    // ─────────────────────────────────────────────────────────────────
    [MenuItem("Tools/AR Setup Automation")]
    public static void ShowWindow() =>
        GetWindow<ARAutomationWindow>("AR Setup Wizard");

    // ─── GUI ─────────────────────────────────────────────────────────
    private void OnGUI()
    {
        GUILayout.Label("AR Video Scene Generator", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Provide your target image and CDN link. The tool will auto-generate " +
            "correctly-sized plane meshes from the image pixel dimensions and wire " +
            "everything up automatically.", MessageType.Info);
        GUILayout.Space(8);

        // ── Core ──
        GUILayout.Label("Scene Info", EditorStyles.boldLabel);
        sceneName    = EditorGUILayout.TextField("New Scene Name", sceneName);
        targetId     = EditorGUILayout.TextField("Target ID (no spaces)", targetId);
        cdnVideoUrl  = EditorGUILayout.TextField("CDN Video URL", cdnVideoUrl);
        GUILayout.Space(8);

        // ── Tracking image ──
        GUILayout.Label("Tracking Image", EditorStyles.boldLabel);
        imageTexture = (Texture2D)EditorGUILayout.ObjectField(
            "Target Image", imageTexture, typeof(Texture2D), false);

        if (imageTexture != null)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("  Pixel size",
                $"{imageTexture.width} × {imageTexture.height} px");
            EditorGUI.EndDisabledGroup();
        }

        physicalWidth = EditorGUILayout.FloatField("  Physical Width (Units)", physicalWidth);

        overrideImageMesh = EditorGUILayout.Toggle("  Override with custom mesh", overrideImageMesh);
        if (overrideImageMesh)
        {
            customImageMesh = (Mesh)EditorGUILayout.ObjectField(
                "  Custom Image Mesh", customImageMesh, typeof(Mesh), false);
        }
        GUILayout.Space(8);

        // ── Green screen ──
        GUILayout.Label("Video Settings", EditorStyles.boldLabel);
        isGreenScreen = EditorGUILayout.Toggle("Is Green Screen Video?", isGreenScreen);

        if (isGreenScreen)
        {
            firstFrameTexture = (Texture2D)EditorGUILayout.ObjectField(
                "  First Frame Image", firstFrameTexture, typeof(Texture2D), false);

            GUILayout.Space(4);
            GUILayout.Label("  Video Layer Settings", EditorStyles.miniLabel);
            videoWidthPx  = EditorGUILayout.IntField("    Source Width (px)",  videoWidthPx);
            videoHeightPx = EditorGUILayout.IntField("    Source Height (px)", videoHeightPx);
            
            GUILayout.Space(4);
            videoScale  = EditorGUILayout.FloatField("    Video Scale", videoScale);
            videoOffset = EditorGUILayout.Vector2Field("    Video Offset (X,Y)", videoOffset);

            EditorGUILayout.HelpBox(
                "Plane meshes will be generated proportionally to Physical Width.\n" +
                "Video Scale and Offset will be automatically applied to the video object to perfectly align varying green screen contents.", MessageType.None);

            overrideVideoMesh = EditorGUILayout.Toggle("  Override with custom mesh", overrideVideoMesh);
            if (overrideVideoMesh)
            {
                customVideoMesh = (Mesh)EditorGUILayout.ObjectField(
                    "  Custom Video Mesh", customVideoMesh, typeof(Mesh), false);
            }
        }

        GUILayout.Space(16);

        GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
        if (GUILayout.Button("▶  Create Scene & Setup AR", GUILayout.Height(44)))
        {
            SetupScene();
        }
        GUI.backgroundColor = Color.white;
    }

    // ─── Main logic ───────────────────────────────────────────────────
    private void SetupScene()
    {
        // Validate
        if (string.IsNullOrEmpty(targetId) || imageTexture == null ||
            string.IsNullOrEmpty(cdnVideoUrl) || string.IsNullOrEmpty(sceneName))
        {
            EditorUtility.DisplayDialog("Missing info",
                "Please fill in: Scene Name, Target ID, Target Image, and CDN URL.", "OK");
            return;
        }

        if (!System.IO.File.Exists(TemplatePath))
        {
            EditorUtility.DisplayDialog("Error",
                "Template scene not found at:\n" + TemplatePath, "OK");
            return;
        }

        // Ensure asset folders exist
        EnsureFolder(MeshFolder);
        EnsureFolder(MatFolder);

        // ── 1. Register in Global Settings ──
        RegisterImageTarget();

        // ── 2. Duplicate template scene ──
        string newScenePath = "Assets/Scenes_1/" + sceneName + ".unity";
        if (!AssetDatabase.CopyAsset(TemplatePath, newScenePath))
        {
            EditorUtility.DisplayDialog("Error", "Failed to duplicate template scene.", "OK");
            return;
        }

        Scene newScene = EditorSceneManager.OpenScene(newScenePath, OpenSceneMode.Single);

        // ── 3. Clean up ImageTracker – keep only slot 0, rename ──
        CleanImageTracker();

        // ── 4. Find CDNARVideoController ──
#if UNITY_2023_1_OR_NEWER
        var cdn = Object.FindFirstObjectByType<CDNARVideoController>(FindObjectsInactive.Include);
#else
        var cdn = Object.FindObjectOfType<CDNARVideoController>(true);
#endif

        if (cdn == null)
        {
            Debug.LogWarning("CDNARVideoController not found. Make sure Demo-Video template has one.");
        }
        else
        {
            // Set CDN url + sound key
            var sp = new SerializedObject(cdn);
            sp.FindProperty("cdnVideoUrl").stringValue = cdnVideoUrl;
            sp.FindProperty("webGLSoundTargetKey").stringValue = targetId;
            sp.ApplyModifiedProperties();

            GameObject childObj  = cdn.gameObject;
            GameObject parentObj = childObj.transform.parent.gameObject;

            parentObj.name = targetId;
            childObj.name  = targetId + " vid";

            VideoPlayer vp = cdn.GetComponent<VideoPlayer>();

            // ── Build / assign parent (tracking image) mesh ──
            float targetW = physicalWidth;
            float targetH = physicalWidth * ((float)imageTexture.height / imageTexture.width);

            Mesh imgMesh = overrideImageMesh && customImageMesh != null
                ? customImageMesh
                : GetOrCreateMesh(targetW, targetH, targetId + "_TrackImg");

            SetupParentObject(parentObj, imgMesh);

            // ── Materials & child (video) layer ──
            if (!isGreenScreen)
            {
                SetupNormalVideo(parentObj, childObj, vp);
            }
            else
            {
                float vidW = physicalWidth;
                float vidH = physicalWidth * ((float)videoHeightPx / videoWidthPx);

                Mesh vidMesh = overrideVideoMesh && customVideoMesh != null
                    ? customVideoMesh
                    : GetOrCreateMesh(vidW, vidH, targetId + "_Vid");

                SetupGreenScreenVideo(parentObj, childObj, vp, vidMesh);
            }
        }

        // ── 5. Save scene & add to Build Settings ──
        EditorSceneManager.SaveScene(newScene);
        AddToBuildSettings(newScenePath);

        EditorUtility.DisplayDialog("✅ Done",
            $"Scene '{sceneName}' created!\n\n" +
            "• Mesh(es) auto-generated from pixel dimensions\n" +
            "• Materials created in AR_Assets/Materials\n" +
            "• Scene added to Build Settings", "Awesome!");
    }

    // ─── Register image target ────────────────────────────────────────
    private void RegisterImageTarget()
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null) { Debug.LogWarning("ImageTrackerGlobalSettings not found."); return; }

        if (gs.imageTargetInfos == null) gs.imageTargetInfos = new List<ImageTargetInfo>();

        bool found = false;
        foreach (var info in gs.imageTargetInfos)
        {
            if (info.id != targetId) continue;
            info.texture = imageTexture;
            found = true;
            break;
        }
        if (!found) gs.imageTargetInfos.Add(new ImageTargetInfo { id = targetId, texture = imageTexture });

        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
    }

    // ─── Clean ImageTracker component ────────────────────────────────
    private void CleanImageTracker()
    {
#if UNITY_2023_1_OR_NEWER
        var tracker = Object.FindFirstObjectByType<ImageTracker>(FindObjectsInactive.Include);
#else
        var tracker = Object.FindObjectOfType<ImageTracker>(true);
#endif
        if (tracker == null) return;

        var so = new SerializedObject(tracker);
        var targets = so.FindProperty("imageTargets");
        if (targets == null || targets.arraySize == 0) return;

        // Keep slot 0 transform; destroy others
        Transform keep = (Transform)targets.GetArrayElementAtIndex(0)
                          .FindPropertyRelative("transform").objectReferenceValue;

        for (int i = 1; i < targets.arraySize; i++)
        {
            Transform t = (Transform)targets.GetArrayElementAtIndex(i)
                           .FindPropertyRelative("transform").objectReferenceValue;
            if (t != null) DestroyImmediate(t.gameObject);
        }

        if (keep != null) keep.name = targetId;

        targets.arraySize = 1;
        targets.GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue = targetId;
        so.ApplyModifiedProperties();
    }

    // ─── Auto-generate or reuse a plane mesh from physical dimensions ───
    /// <summary>
    /// Creates a Quad scaled to the physical dimensions provided.
    /// The mesh is saved as an asset so it appears in the project and can be reused.
    /// </summary>
    private Mesh GetOrCreateMesh(float w, float h, string meshName)
    {
        string path = $"{MeshFolder}/{meshName}_{w:F2}x{h:F2}.mesh";
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
        mesh.triangles  = new int[] { 0, 2, 1, 2, 3, 1 };
        mesh.normals    = new Vector3[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.RecalculateBounds();

        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AR Automation] Mesh generated: {path}  ({w}×{h} units)");
        return mesh;
    }

    // ─── Parent object: assign mesh, keep scale = 1 ──────────────────
    private void SetupParentObject(GameObject parentObj, Mesh mesh)
    {
        MeshFilter mf = parentObj.GetComponent<MeshFilter>() ?? parentObj.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        // Since mesh is dimensionally correct, reset scale to (1,1,1)
        parentObj.transform.localScale = Vector3.one;

        // Ensure MeshRenderer exists
        if (parentObj.GetComponent<MeshRenderer>() == null)
            parentObj.AddComponent<MeshRenderer>();
    }

    // ─── Normal video: 1 material on parent, video → parent renderer ─
    private void SetupNormalVideo(GameObject parentObj, GameObject childObj, VideoPlayer vp)
    {
        // Material: Unlit/Texture + target image
        Material mat = new Material(Shader.Find("Unlit/Texture"))
        {
            name = targetId + "_Mat",
            mainTexture = imageTexture
        };
        AssetDatabase.CreateAsset(mat, $"{MatFolder}/{mat.name}.mat");

        var rend = parentObj.GetComponent<Renderer>();
        rend.sharedMaterial = mat;

        // Remove any lingering mesh/renderer on child
        Destroy<MeshRenderer>(childObj);
        Destroy<MeshFilter>(childObj);

        if (vp != null) vp.targetMaterialRenderer = rend;
    }

    // ─── Green screen: 2 materials, 2 meshes ─────────────────────────
    private void SetupGreenScreenVideo(GameObject parentObj, GameObject childObj,
                                       VideoPlayer vp, Mesh vidMesh)
    {
        // Material 1: background (target image, Unlit)
        Material bgMat = new Material(Shader.Find("Unlit/Texture"))
        {
            name = targetId + "_BGMat",
            mainTexture = imageTexture
        };
        AssetDatabase.CreateAsset(bgMat, $"{MatFolder}/{bgMat.name}.mat");
        parentObj.GetComponent<Renderer>().sharedMaterial = bgMat;

        // Material 2: chroma key (first frame on child)
        Shader chromaShader = Shader.Find("Imagine/ChromaKeyCutout");
        Material chromaMat = new Material(chromaShader != null
            ? chromaShader : Shader.Find("Unlit/Transparent"))
        {
            name = targetId + "_ChromaMat"
        };
        if (chromaShader != null)
        {
            chromaMat.SetColor("_MaskCol", Color.green);
            chromaMat.SetFloat("_Sensitivity", 0.35f);
            chromaMat.SetFloat("_Cutoff", 0.134f);
            chromaMat.SetFloat("_Feather", 1f);
        }
        if (firstFrameTexture != null) chromaMat.mainTexture = firstFrameTexture;
        AssetDatabase.CreateAsset(chromaMat, $"{MatFolder}/{chromaMat.name}.mat");

        // Child mesh
        MeshFilter childMf = childObj.GetComponent<MeshFilter>() ?? childObj.AddComponent<MeshFilter>();
        childMf.sharedMesh = vidMesh;

        MeshRenderer childRend = childObj.GetComponent<MeshRenderer>() ?? childObj.AddComponent<MeshRenderer>();
        childRend.sharedMaterial = chromaMat;

        // Apply user-defined offset and scale for varying green screen videos,
        // and keep slightly in front (Z=-0.001) to avoid Z-fighting.
        childObj.transform.localPosition = new Vector3(videoOffset.x, videoOffset.y, -0.001f);
        childObj.transform.localRotation = Quaternion.identity;
        childObj.transform.localScale    = new Vector3(videoScale, videoScale, 1f);

        if (vp != null) vp.targetMaterialRenderer = childRend;
    }

    // ─── Add scene to Build Settings ─────────────────────────────────
    private static void AddToBuildSettings(string scenePath)
    {
        var existing = EditorBuildSettings.scenes;
        foreach (var s in existing)
            if (s.path == scenePath) return;

        var updated = new EditorBuildSettingsScene[existing.Length + 1];
        System.Array.Copy(existing, updated, existing.Length);
        updated[updated.Length - 1] = new EditorBuildSettingsScene(scenePath, true);
        EditorBuildSettings.scenes = updated;
    }

    // ─── Helpers ─────────────────────────────────────────────────────
    private static void EnsureFolder(string assetPath)
    {
        if (!System.IO.Directory.Exists(assetPath))
            System.IO.Directory.CreateDirectory(assetPath);
    }

    private static void Destroy<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c != null) DestroyImmediate(c);
    }
}
