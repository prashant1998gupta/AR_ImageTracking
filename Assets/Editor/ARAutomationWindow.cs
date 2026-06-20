using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Imagine.WebAR;
using System.Collections.Generic;

/// <summary>
/// AR Video Scene Generator — Enhanced Wizard (v2)
/// 
/// A non-developer-friendly tool that guides users through creating AR scenes:
///   • Step-by-step UI with real-time validation and status indicators
///   • Auto-detects and fixes Read/Write on textures with one-click buttons
///   • Auto-sanitizes Target IDs (removes spaces/special characters)
///   • Validates CDN URLs and warns about format issues
///   • Auto-detects video dimensions from the first frame image
///   • Pre-flight checklist ensures everything is correct before creation
///   • Progress bar and detailed completion dialog
///
/// For green screen videos: Parent = target image BG mesh, Child = chroma key video mesh.
/// </summary>
public class ARAutomationWindow : EditorWindow
{
    // ─── Core inputs ────────────────────────────────────────────────
    private string sceneName      = "";
    private string targetId       = "";
    private Texture2D imageTexture;
    private string cdnVideoUrl    = "https://";
    private float physicalWidth   = 1.0f;

    // ─── Tracking image mesh ─────────────────────────────────────────
    private bool overrideImageMesh = false;
    private Mesh customImageMesh;

    // ─── Green screen ────────────────────────────────────────────────
    private bool isGreenScreen = false;
    private Texture2D firstFrameTexture;

    // Video layer dimensions (pixels) – used to auto-generate a mesh
    private int videoWidthPx  = 1080;
    private int videoHeightPx = 1920;
    private float videoScale  = 1.0f;
    private Vector2 videoOffset = Vector2.zero;
    private bool overrideVideoMesh = false;
    private Mesh customVideoMesh;

    // ─── Mesh save folder ────────────────────────────────────────────
    private const string MeshFolder = "Assets/AR_Assets/Planes/Generated";
    private const string MatFolder  = "Assets/AR_Assets/Materials";
    private const string TemplatePath = "Assets/Scenes_1/Demo-Video.unity";

    // ─── UI state ────────────────────────────────────────────────────
    private Vector2 scrollPosition;
    private bool showAdvancedImage = false;
    private bool showAdvancedVideo = false;
    private Texture2D prevFirstFrame;

    // ─── Cached style references (rebuilt on domain reload) ─────────
    private GUIStyle _headerStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _okStyle;
    private GUIStyle _warnStyle;
    private GUIStyle _errStyle;
    private GUIStyle _hintStyle;
    private GUIStyle _stepBoxStyle;

    // ─────────────────────────────────────────────────────────────────
    [MenuItem("Tools/AR Setup Automation")]
    public static void ShowWindow()
    {
        var win = GetWindow<ARAutomationWindow>("AR Setup Wizard");
        win.minSize = new Vector2(460, 550);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STYLES  (lazy-built so they survive domain reloads)
    // ═══════════════════════════════════════════════════════════════════
    private void EnsureStyles()
    {
        if (_headerStyle != null) return;

        _headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 15,
            alignment = TextAnchor.MiddleCenter,
            margin    = new RectOffset(0, 0, 6, 6)
        };
        _sectionStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };

        _okStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal    = { textColor = new Color(0.3f, 0.9f, 0.35f) },
            fontStyle = FontStyle.Bold
        };
        _warnStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal    = { textColor = new Color(1f, 0.82f, 0.2f) },
            fontStyle = FontStyle.Bold
        };
        _errStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            normal    = { textColor = new Color(1f, 0.35f, 0.35f) },
            fontStyle = FontStyle.Bold
        };
        _hintStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true,
            normal   = { textColor = new Color(0.65f, 0.65f, 0.65f) }
        };
        _stepBoxStyle = new GUIStyle("HelpBox")
        {
            padding = new RectOffset(10, 10, 8, 8),
            margin  = new RectOffset(4, 4, 4, 4)
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GUI
    // ═══════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        EnsureStyles();
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        // ── Title ───────────────────────────────────────────────────
        GUILayout.Space(6);
        GUILayout.Label("AR Video Scene Generator", _headerStyle);
        EditorGUILayout.HelpBox(
            "Create a complete AR scene in 3 simple steps:\n\n" +
            "  Step 1 →  Fill in Scene Info  (name, ID, video URL)\n" +
            "  Step 2 →  Add your Tracking Image  (the real-world image the camera recognises)\n" +
            "  Step 3 →  Configure Video  (green-screen or normal overlay)\n\n" +
            "The tool auto-generates meshes, materials, and wires everything up.\n" +
            "All issues are shown inline — fix them and the ▶ Create button will unlock.",
            MessageType.Info);
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════════
        //  STEP 1 — Scene Info
        // ═════════════════════════════════════════════════════════════
        DrawSectionHeader("Step 1 :  Scene Info", IsStep1Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            // --- Scene Name ---
            sceneName = EditorGUILayout.TextField(
                new GUIContent("Scene Name",
                    "A human-readable name for the new scene, e.g. 'India Post Card'.\n" +
                    "A Unity scene file will be created with this name."),
                sceneName);
            if (string.IsNullOrEmpty(sceneName))
                EditorGUILayout.HelpBox("Enter a descriptive scene name (e.g. 'Wedding Invite').", MessageType.Warning);
            else if (System.IO.File.Exists("Assets/Scenes_1/" + sceneName + ".unity"))
                EditorGUILayout.HelpBox(
                    "A scene with this name already exists and will be overwritten.",
                    MessageType.Warning);

            // --- Target ID ---
            EditorGUI.BeginChangeCheck();
            targetId = EditorGUILayout.TextField(
                new GUIContent("Target ID  (no spaces)",
                    "A short unique key with NO SPACES.  Used internally to identify this image.\n" +
                    "Example: 'Post_Card', 'Wedding_Invite'\n\n" +
                    "Spaces are automatically replaced with underscores."),
                targetId);
            if (EditorGUI.EndChangeCheck())
            {
                // Auto-sanitize: spaces → underscores, strip risky chars
                targetId = targetId.Replace(" ", "_");
                // Remove characters that could break file paths or JS keys
                targetId = System.Text.RegularExpressions.Regex.Replace(targetId, @"[^a-zA-Z0-9_\-]", "");
            }
            if (string.IsNullOrEmpty(targetId))
                EditorGUILayout.HelpBox("Enter a Target ID (e.g. 'Post_Card'). Spaces are auto-replaced with underscores.", MessageType.Warning);
            else if (IsTargetIdRegistered(targetId))
                EditorGUILayout.HelpBox(
                    $"'{targetId}' already exists in the global target list — the existing entry will be updated.",
                    MessageType.Info);

            // --- CDN Video URL ---
            cdnVideoUrl = EditorGUILayout.TextField(
                new GUIContent("CDN Video URL",
                    "A direct link to your .mp4 or .webm video hosted on a CDN.\n\n" +
                    "Example:\nhttps://cdn.jsdelivr.net/gh/user/repo@main/videos/demo.mp4\n\n" +
                    "The URL must point directly to the video file, not to a web-page that contains it."),
                cdnVideoUrl);
            DrawUrlValidation();
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════════
        //  STEP 2 — Tracking Image
        // ═════════════════════════════════════════════════════════════
        DrawSectionHeader("Step 2 :  Tracking Image", IsStep2Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            if (imageTexture == null)
            {
                EditorGUILayout.LabelField(
                    "Drag a Texture2D from your Project window into the slot below.",
                    _hintStyle);
                GUILayout.Space(2);
            }

            EditorGUI.BeginChangeCheck();
            imageTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Target Image",
                    "The real-world image the AR camera will recognise (e.g. a postcard, poster, book cover).\n\n" +
                    "Tips for best tracking:\n" +
                    "• High contrast with lots of unique detail\n" +
                    "• Non-symmetrical designs work best\n" +
                    "• Avoid plain text, logos, or solid colours\n" +
                    "• Minimum 500 × 500 px recommended"),
                imageTexture, typeof(Texture2D), false);
            bool imageJustChanged = EditorGUI.EndChangeCheck();

            if (imageTexture != null)
            {
                // Auto-suggest Target ID from the image name when first assigned
                if (imageJustChanged && string.IsNullOrEmpty(targetId))
                {
                    targetId = SanitiseId(imageTexture.name);
                }
                // Auto-suggest Scene Name too
                if (imageJustChanged && string.IsNullOrEmpty(sceneName))
                {
                    sceneName = imageTexture.name;
                }

                // ── Read / Write check ──
                DrawReadWriteCheck(imageTexture, "Target Image");

                // ── Image dimensions ──
                DrawImageInfo(imageTexture);

                // ── Physical Width ──
                GUILayout.Space(4);
                physicalWidth = EditorGUILayout.FloatField(
                    new GUIContent("Physical Width (Units)",
                        "The width of the target image in Unity world-units.\n" +
                        "The height is calculated automatically from the image aspect ratio.\n\n" +
                        "For most setups, leave this at 1.0."),
                    physicalWidth);
                if (physicalWidth <= 0)
                {
                    physicalWidth = 1f;
                    EditorGUILayout.HelpBox("Width must be > 0. Reset to 1.0.", MessageType.Error);
                }

                // Show calculated mesh size
                if (imageTexture.width > 0)
                {
                    float h = physicalWidth * ((float)imageTexture.height / imageTexture.width);
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField("  → Generated Mesh", $"{physicalWidth:F2} × {h:F2} units");
                    EditorGUI.EndDisabledGroup();
                }

                // ── Advanced (custom mesh override) ──
                showAdvancedImage = EditorGUILayout.Foldout(showAdvancedImage, "Advanced  (custom mesh override)");
                if (showAdvancedImage)
                {
                    EditorGUI.indentLevel++;
                    overrideImageMesh = EditorGUILayout.Toggle(
                        new GUIContent("Use Custom Mesh",
                            "Replace the auto-generated plane with your own mesh asset."),
                        overrideImageMesh);
                    if (overrideImageMesh)
                    {
                        customImageMesh = (Mesh)EditorGUILayout.ObjectField(
                            "Custom Mesh", customImageMesh, typeof(Mesh), false);
                        if (customImageMesh == null)
                            EditorGUILayout.HelpBox("Assign a mesh or uncheck 'Use Custom Mesh'.", MessageType.Warning);
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "👆  Drag a Texture2D here.\n\n" +
                    "Good tracking images have:\n" +
                    "  ✓  High contrast & unique detail\n" +
                    "  ✓  Non-symmetrical design\n" +
                    "  ✓  At least 500 × 500 pixels\n\n" +
                    "Avoid:\n" +
                    "  ✗  Plain text or simple logos\n" +
                    "  ✗  Solid / flat colours\n" +
                    "  ✗  Repeating patterns",
                    MessageType.None);
            }
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════════
        //  STEP 3 — Video Settings
        // ═════════════════════════════════════════════════════════════
        DrawSectionHeader("Step 3 :  Video Settings", IsStep3Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            isGreenScreen = EditorGUILayout.Toggle(
                new GUIContent("Is Green Screen Video?",
                    "Turn ON if your video has a green (or solid-colour) background " +
                    "that should become transparent.\n\n" +
                    "When ON:\n" +
                    "  • Background layer = your tracking image\n" +
                    "  • Foreground layer = the video (green removed)\n\n" +
                    "When OFF:\n" +
                    "  • The video plays directly on the image surface"),
                isGreenScreen);

            if (isGreenScreen)
            {
                GUILayout.Space(4);
                EditorGUILayout.LabelField(
                    "Two-layer setup:   background = tracking image,   foreground = video with green removed.",
                    _hintStyle);
                GUILayout.Space(4);

                // ── First Frame ──
                EditorGUI.BeginChangeCheck();
                firstFrameTexture = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("First Frame Image",
                        "A screenshot of the first video frame (with green screen visible).\n" +
                        "This acts as a placeholder texture while the video loads.\n\n" +
                        "Quick way to extract it:\n" +
                        "  ffmpeg -i video.mp4 -vframes 1 first_frame.png"),
                    firstFrameTexture, typeof(Texture2D), false);
                bool ffChanged = EditorGUI.EndChangeCheck();

                if (firstFrameTexture != null)
                {
                    DrawReadWriteCheck(firstFrameTexture, "First Frame");

                    // Auto-detect video dimensions when first frame is assigned
                    if (ffChanged && firstFrameTexture != prevFirstFrame)
                    {
                        videoWidthPx  = firstFrameTexture.width;
                        videoHeightPx = firstFrameTexture.height;
                        Debug.Log($"[AR Wizard] Auto-detected video size from first frame: {videoWidthPx} × {videoHeightPx}");
                    }
                    prevFirstFrame = firstFrameTexture;

                    GUILayout.Label(
                        $"  ✓  First frame: {firstFrameTexture.width} × {firstFrameTexture.height} px",
                        _okStyle);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Drag the first frame of your video here.\n\n" +
                        "To extract it, run:\n" +
                        "  ffmpeg -i your_video.mp4 -vframes 1 first_frame.png",
                        MessageType.Warning);
                }

                GUILayout.Space(6);
                GUILayout.Label("Video Layer Settings", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Pixel dimensions of your source video. " +
                    "Auto-filled when you assign the First Frame image above.",
                    _hintStyle);
                GUILayout.Space(2);

                videoWidthPx  = EditorGUILayout.IntField(
                    new GUIContent("Source Width (px)",
                        "Pixel width of the source video file."),
                    videoWidthPx);
                videoHeightPx = EditorGUILayout.IntField(
                    new GUIContent("Source Height (px)",
                        "Pixel height of the source video file."),
                    videoHeightPx);

                if (videoWidthPx <= 0 || videoHeightPx <= 0)
                    EditorGUILayout.HelpBox("Width and Height must be greater than 0.", MessageType.Error);
                else
                {
                    float vW = physicalWidth;
                    float vH = physicalWidth * ((float)videoHeightPx / videoWidthPx);
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField("  → Video Mesh", $"{vW:F2} × {vH:F2} units");
                    EditorGUI.EndDisabledGroup();
                }

                GUILayout.Space(4);
                videoScale = EditorGUILayout.FloatField(
                    new GUIContent("Video Scale",
                        "Multiplier for the video layer size.\n\n" +
                        "  1.0  = same size as calculated\n" +
                        "  1.2  = 20 % larger  (if subject looks too small)\n" +
                        "  0.8  = 20 % smaller"),
                    videoScale);
                videoOffset = EditorGUILayout.Vector2Field(
                    new GUIContent("Video Offset (X, Y)",
                        "Shifts the video layer relative to the background.\n" +
                        "  +X = right,  +Y = up"),
                    videoOffset);

                EditorGUILayout.HelpBox(
                    "💡  Tuning tips:\n" +
                    "• If the person/object looks too small  →  increase Video Scale (try 1.2)\n" +
                    "• If misaligned with the background  →  adjust Video Offset\n" +
                    "• The video layer sits 0.001 units in front to avoid z-fighting",
                    MessageType.None);

                // ── Advanced (custom video mesh) ──
                showAdvancedVideo = EditorGUILayout.Foldout(showAdvancedVideo, "Advanced  (custom video mesh)");
                if (showAdvancedVideo)
                {
                    EditorGUI.indentLevel++;
                    overrideVideoMesh = EditorGUILayout.Toggle("Use Custom Mesh", overrideVideoMesh);
                    if (overrideVideoMesh)
                    {
                        customVideoMesh = (Mesh)EditorGUILayout.ObjectField(
                            "Custom Mesh", customVideoMesh, typeof(Mesh), false);
                        if (customVideoMesh == null)
                            EditorGUILayout.HelpBox("Assign a mesh or uncheck 'Use Custom Mesh'.", MessageType.Warning);
                    }
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                GUILayout.Space(2);
                EditorGUILayout.LabelField(
                    "Normal mode — the video plays directly on the tracking-image surface.",
                    _hintStyle);
            }
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(8);

        // ═════════════════════════════════════════════════════════════
        //  PRE-FLIGHT CHECKLIST
        // ═════════════════════════════════════════════════════════════
        DrawPreFlightChecklist();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════════
        //  CREATE BUTTON
        // ═════════════════════════════════════════════════════════════
        bool allGood = IsAllValid();

        EditorGUI.BeginDisabledGroup(!allGood);
        GUI.backgroundColor = allGood
            ? new Color(0.25f, 0.85f, 0.4f)
            : new Color(0.45f, 0.45f, 0.45f);
        if (GUILayout.Button("▶   Create Scene & Setup AR", GUILayout.Height(48)))
        {
            RunSetup();
        }
        GUI.backgroundColor = Color.white;
        EditorGUI.EndDisabledGroup();

        if (!allGood)
        {
            EditorGUILayout.HelpBox(
                "Fix all ✗ items in the checklist above — the button will unlock automatically.",
                MessageType.Warning);
        }

        GUILayout.Space(12);

        // ── Reset button ──
        GUI.backgroundColor = new Color(0.85f, 0.35f, 0.3f);
        if (GUILayout.Button("↺   Reset All Fields", GUILayout.Height(26)))
        {
            if (EditorUtility.DisplayDialog("Reset",
                "Clear all fields and start over?", "Yes, Reset", "Cancel"))
            {
                ResetFields();
            }
        }
        GUI.backgroundColor = Color.white;

        GUILayout.Space(16);
        EditorGUILayout.EndScrollView();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DRAWING HELPERS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Draw a section header with a ✓ Ready / ○ Incomplete badge.</summary>
    private void DrawSectionHeader(string title, bool ok)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(title, _sectionStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label(ok ? "✓ Ready" : "○ Incomplete", ok ? _okStyle : _warnStyle);
        EditorGUILayout.EndHorizontal();
    }

    /// <summary>Check whether a texture has Read/Write enabled and offer a one-click fix.</summary>
    private void DrawReadWriteCheck(Texture2D tex, string label)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return;

        TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) return;

        if (imp.isReadable)
        {
            GUILayout.Label($"  ✓  {label}: Read/Write is enabled", _okStyle);
        }
        else
        {
            EditorGUILayout.HelpBox(
                $"⚠  READ / WRITE  is disabled on  '{tex.name}'.\n\n" +
                "The wizard needs this to read pixel dimensions and the AR tracker " +
                "needs it for feature extraction at runtime.\n\n" +
                "Click the button below to fix it automatically.",
                MessageType.Error);

            GUI.backgroundColor = new Color(1f, 0.6f, 0.15f);
            if (GUILayout.Button($"🔧   Enable Read/Write on  '{tex.name}'", GUILayout.Height(28)))
            {
                imp.isReadable = true;
                imp.SaveAndReimport();
                Debug.Log($"[AR Wizard] ✅ Enabled Read/Write on: {path}");
            }
            GUI.backgroundColor = Color.white;
        }
    }

    /// <summary>Show image dimensions, aspect ratio, and quality warnings.</summary>
    private void DrawImageInfo(Texture2D tex)
    {
        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.TextField("  Pixel Size",
            $"{tex.width} × {tex.height} px");
        float ar = (float)tex.height / Mathf.Max(tex.width, 1);
        EditorGUILayout.TextField("  Aspect Ratio",
            $"{ar:F3}  ({(ar > 1 ? "Portrait" : ar < 1 ? "Landscape" : "Square")})");
        EditorGUI.EndDisabledGroup();

        if (tex.width < 400 || tex.height < 400)
        {
            EditorGUILayout.HelpBox(
                "⚠  Image resolution is low.  For reliable tracking, use at least 500 × 500 px.",
                MessageType.Warning);
        }
    }

    /// <summary>Validate CDN URL format and show feedback.</summary>
    private void DrawUrlValidation()
    {
        if (string.IsNullOrEmpty(cdnVideoUrl) || cdnVideoUrl == "https://")
        {
            EditorGUILayout.HelpBox(
                "Paste the direct URL to your .mp4 or .webm video file.\n\n" +
                "Free CDN example (via GitHub + jsDelivr):\n" +
                "  https://cdn.jsdelivr.net/gh/USER/REPO@main/videos/demo.mp4",
                MessageType.Warning);
            return;
        }

        if (!cdnVideoUrl.StartsWith("http://") && !cdnVideoUrl.StartsWith("https://"))
        {
            EditorGUILayout.HelpBox("URL must start with  http://  or  https://", MessageType.Error);
            return;
        }

        bool looksLikeVideo =
            cdnVideoUrl.Contains(".mp4") || cdnVideoUrl.Contains(".webm") ||
            cdnVideoUrl.Contains(".m3u8") || cdnVideoUrl.Contains(".ogg");

        if (!looksLikeVideo)
        {
            EditorGUILayout.HelpBox(
                "⚠  This URL doesn't seem to point to a video file (.mp4 / .webm).\n" +
                "Make sure it's a direct download link, not a web-page.",
                MessageType.Warning);
        }
        else
        {
            GUILayout.Label("  ✓  URL looks valid", _okStyle);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PRE-FLIGHT CHECKLIST
    // ═══════════════════════════════════════════════════════════════════

    private void DrawPreFlightChecklist()
    {
        GUILayout.Label("Pre-Flight Checklist", _sectionStyle);
        EditorGUILayout.BeginVertical(_stepBoxStyle);

        CheckItem("Scene Name filled in",
            !string.IsNullOrEmpty(sceneName));

        CheckItem("Target ID filled in  (no spaces)",
            !string.IsNullOrEmpty(targetId));

        CheckItem("CDN Video URL provided",
            IsUrlAcceptable());

        CheckItem("Target Image assigned",
            imageTexture != null);

        // Texture Read/Write
        if (imageTexture != null)
        {
            CheckItem("Target Image → Read/Write enabled",
                IsTextureReadable(imageTexture));
        }

        CheckItem("Template scene exists  (Demo-Video.unity)",
            System.IO.File.Exists(TemplatePath));

        if (isGreenScreen)
        {
            CheckItem("First Frame Image assigned",
                firstFrameTexture != null);

            if (firstFrameTexture != null)
            {
                CheckItem("First Frame → Read/Write enabled",
                    IsTextureReadable(firstFrameTexture));
            }

            CheckItem("Video dimensions > 0",
                videoWidthPx > 0 && videoHeightPx > 0);

            bool chromaOk = Shader.Find("Imagine/ChromaKeyCutout") != null;
            CheckItem("ChromaKeyCutout shader available", chromaOk);
            if (!chromaOk)
            {
                EditorGUILayout.HelpBox(
                    "'Imagine/ChromaKeyCutout' shader not found.  A fallback will be used, " +
                    "but green-screen removal won't work.\n" +
                    "Make sure the Imagine WebAR SDK is fully imported.",
                    MessageType.Error);
            }
        }

        // Physical Width sanity
        CheckItem("Physical Width > 0", physicalWidth > 0);

        // Custom mesh overrides (only if toggled)
        if (overrideImageMesh)
            CheckItem("Custom Image Mesh assigned", customImageMesh != null);
        if (isGreenScreen && overrideVideoMesh)
            CheckItem("Custom Video Mesh assigned", customVideoMesh != null);

        EditorGUILayout.EndVertical();
    }

    private void CheckItem(string label, bool ok)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(ok ? "  ✓" : "  ✗", ok ? _okStyle : _errStyle, GUILayout.Width(28));
        GUILayout.Label(label, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION  (pure-logic, no drawing)
    // ═══════════════════════════════════════════════════════════════════

    private bool IsStep1Valid()
    {
        return !string.IsNullOrEmpty(sceneName) &&
               !string.IsNullOrEmpty(targetId) &&
               IsUrlAcceptable();
    }

    private bool IsStep2Valid()
    {
        if (imageTexture == null) return false;
        if (!IsTextureReadable(imageTexture)) return false;
        if (physicalWidth <= 0) return false;
        if (overrideImageMesh && customImageMesh == null) return false;
        return true;
    }

    private bool IsStep3Valid()
    {
        if (!isGreenScreen) return true;
        if (firstFrameTexture == null) return false;
        if (!IsTextureReadable(firstFrameTexture)) return false;
        if (videoWidthPx <= 0 || videoHeightPx <= 0) return false;
        if (overrideVideoMesh && customVideoMesh == null) return false;
        return true;
    }

    private bool IsAllValid()
    {
        return IsStep1Valid() && IsStep2Valid() && IsStep3Valid() &&
               System.IO.File.Exists(TemplatePath);
    }

    private bool IsUrlAcceptable()
    {
        return !string.IsNullOrEmpty(cdnVideoUrl) &&
               cdnVideoUrl != "https://" &&
               (cdnVideoUrl.StartsWith("http://") || cdnVideoUrl.StartsWith("https://"));
    }

    private static bool IsTextureReadable(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (string.IsNullOrEmpty(path)) return false;
        TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
        return imp != null && imp.isReadable;
    }

    private static bool IsTargetIdRegistered(string id)
    {
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null || gs.imageTargetInfos == null) return false;
        foreach (var info in gs.imageTargetInfos)
            if (info.id == id) return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UTILITY
    // ═══════════════════════════════════════════════════════════════════

    private static string SanitiseId(string raw)
    {
        string s = raw.Replace(" ", "_");
        return System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9_\-]", "");
    }

    private void ResetFields()
    {
        sceneName         = "";
        targetId          = "";
        imageTexture      = null;
        cdnVideoUrl       = "https://";
        physicalWidth     = 1f;
        overrideImageMesh = false;
        customImageMesh   = null;
        isGreenScreen     = false;
        firstFrameTexture = null;
        prevFirstFrame    = null;
        videoWidthPx      = 1080;
        videoHeightPx     = 1920;
        videoScale        = 1f;
        videoOffset       = Vector2.zero;
        overrideVideoMesh = false;
        customVideoMesh   = null;
        showAdvancedImage = false;
        showAdvancedVideo = false;
        Repaint();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MAIN SETUP LOGIC  (with progress bar)
    // ═══════════════════════════════════════════════════════════════════

    private void RunSetup()
    {
        // Final guard
        if (!IsAllValid())
        {
            EditorUtility.DisplayDialog("Cannot Proceed",
                "Some checklist items are still failing.  Please fix them first.", "OK");
            return;
        }

        try
        {
            // ── 0. Folders ──
            EditorUtility.DisplayProgressBar("AR Setup", "Creating folders…", 0.05f);
            EnsureFolder(MeshFolder);
            EnsureFolder(MatFolder);

            // ── 1. Register in Global Settings ──
            EditorUtility.DisplayProgressBar("AR Setup", "Registering image target…", 0.15f);
            RegisterImageTarget();

            // ── 2. Duplicate template scene ──
            EditorUtility.DisplayProgressBar("AR Setup", "Duplicating template scene…", 0.25f);
            string newScenePath = "Assets/Scenes_1/" + sceneName + ".unity";
            if (System.IO.File.Exists(newScenePath))
            {
                // Delete old scene so CopyAsset succeeds
                AssetDatabase.DeleteAsset(newScenePath);
            }
            if (!AssetDatabase.CopyAsset(TemplatePath, newScenePath))
            {
                EditorUtility.DisplayDialog("Error",
                    "Failed to duplicate template scene.  Check the Console for details.", "OK");
                return;
            }

            EditorUtility.DisplayProgressBar("AR Setup", "Opening new scene…", 0.35f);
            Scene newScene = EditorSceneManager.OpenScene(newScenePath, OpenSceneMode.Single);

            // ── 3. Clean up ImageTracker ──
            EditorUtility.DisplayProgressBar("AR Setup", "Configuring ImageTracker…", 0.45f);
            CleanImageTracker();

            // ── 4. Wire CDNARVideoController ──
            EditorUtility.DisplayProgressBar("AR Setup", "Wiring video controller…", 0.55f);
#if UNITY_2023_1_OR_NEWER
            var cdn = Object.FindFirstObjectByType<CDNARVideoController>(FindObjectsInactive.Include);
#else
            var cdn = Object.FindObjectOfType<CDNARVideoController>(true);
#endif

            string meshInfo = "";

            if (cdn == null)
            {
                Debug.LogWarning("[AR Wizard] CDNARVideoController not found in template scene.");
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
                EditorUtility.DisplayProgressBar("AR Setup", "Generating tracking-image mesh…", 0.65f);
                float targetW = physicalWidth;
                float targetH = physicalWidth * ((float)imageTexture.height / imageTexture.width);

                Mesh imgMesh = overrideImageMesh && customImageMesh != null
                    ? customImageMesh
                    : GetOrCreateMesh(targetW, targetH, targetId + "_TrackImg");

                SetupParentObject(parentObj, imgMesh);
                meshInfo = $"  • Background mesh: {targetW:F2} × {targetH:F2} units\n";

                // ── Materials & child (video) layer ──
                EditorUtility.DisplayProgressBar("AR Setup", "Creating materials…", 0.75f);
                if (!isGreenScreen)
                {
                    SetupNormalVideo(parentObj, childObj, vp);
                    meshInfo += "  • Mode: normal video overlay\n";
                }
                else
                {
                    float vidW = physicalWidth;
                    float vidH = physicalWidth * ((float)videoHeightPx / videoWidthPx);

                    Mesh vidMesh = overrideVideoMesh && customVideoMesh != null
                        ? customVideoMesh
                        : GetOrCreateMesh(vidW, vidH, targetId + "_Vid");

                    SetupGreenScreenVideo(parentObj, childObj, vp, vidMesh);
                    meshInfo += $"  • Video mesh: {vidW:F2} × {vidH:F2} units\n";
                    meshInfo += "  • Mode: green-screen (chroma key)\n";
                }
            }

            // ── 5. Save scene & add to Build Settings ──
            EditorUtility.DisplayProgressBar("AR Setup", "Saving scene…", 0.90f);
            EditorSceneManager.SaveScene(newScene);
            AddToBuildSettings(newScenePath);

            EditorUtility.DisplayProgressBar("AR Setup", "Done!", 1.0f);

            // ── Success dialog ──
            EditorUtility.DisplayDialog("✅  Scene Created Successfully!",
                $"Scene:  '{sceneName}'\n" +
                $"Target ID:  '{targetId}'\n\n" +
                meshInfo +
                $"  • Materials saved to:  {MatFolder}/\n" +
                $"  • Scene added to Build Settings\n\n" +
                "─── What to do next ───\n\n" +
                "1.  Press ▶ Play in Unity to test the scene\n" +
                "2.  If green-screen:  select the chroma material in\n" +
                "      AR_Assets/Materials/ and tweak Sensitivity / Cutoff\n" +
                "3.  When ready, build for WebGL  (File → Build Settings)",
                "Got it!");

            // Highlight the new scene in the Project window
            var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(newScenePath);
            if (sceneAsset != null)
            {
                EditorGUIUtility.PingObject(sceneAsset);
                Selection.activeObject = sceneAsset;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SCENE-BUILDING HELPERS  (unchanged core logic)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Add or update image target in ImageTrackerGlobalSettings.</summary>
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

    /// <summary>Strip the duplicated template's ImageTracker down to a single slot.</summary>
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
        Debug.Log($"[AR Wizard] Mesh generated: {path}  ({w} × {h} units)");
        return mesh;
    }

    /// <summary>Assign mesh to parent object and reset scale to (1,1,1).</summary>
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

    /// <summary>Normal video: 1 material on parent, video → parent renderer.</summary>
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

    /// <summary>Green screen: 2 materials, 2 meshes (background + chroma-keyed video).</summary>
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

    /// <summary>Append scene to the Build Settings list (if not already there).</summary>
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

    /// <summary>Create a directory (and parents) if it doesn't already exist.</summary>
    private static void EnsureFolder(string assetPath)
    {
        if (!System.IO.Directory.Exists(assetPath))
            System.IO.Directory.CreateDirectory(assetPath);
    }

    /// <summary>Safely remove a component if present.</summary>
    private static void Destroy<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c != null) DestroyImmediate(c);
    }
}
