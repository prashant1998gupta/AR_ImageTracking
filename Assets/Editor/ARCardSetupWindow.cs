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
/// AR Business Card Scene Generator — Enhanced Wizard (v3)
///
/// A non-developer-friendly tool that guides users through creating AR business card scenes:
///   • Step-by-step UI with real-time validation and status indicators
///   • Auto-detects and fixes Read/Write on textures with one-click buttons
///   • Auto-sanitises Target IDs (removes spaces/special characters)
///   • Pre-flight checklist ensures everything is correct before creation
///   • Supports Instagram, Facebook, YouTube, Website, Phone, Email & VCF links
///   • Optional video overlay (normal or green-screen) with CDNARVideoController
///   • Progress bar and detailed completion dialog
///
/// Template scene: Demo-VisitingCard.unity
/// </summary>
public class ARCardSetupWindow : EditorWindow
{
    // ─── Core inputs ────────────────────────────────────────────────
    private string sceneName = "";
    private string targetId  = "";
    private Texture2D imageTexture;
    private float physicalWidth = 1.0f;

    // ─── Card Design ───────────────────────────────────────────────
    private Sprite cardBackground;

    // ─── Video Overlay (optional) ──────────────────────────────────
    private bool enableVideo      = false;
    private string cdnVideoUrl    = "https://";
    private bool isGreenScreen    = false;
    private Texture2D firstFrameTexture;
    private Texture2D prevFirstFrame;
    private int videoWidthPx      = 1080;
    private int videoHeightPx     = 1920;
    private float videoScale      = 1.0f;
    private bool alignToBottom    = true;
    private Vector2 videoOffset   = Vector2.zero;
    private bool overrideVideoMesh = false;
    private Mesh customVideoMesh;
    private bool showAdvancedVideo = false;

    // ─── Social Links ──────────────────────────────────────────────
    private class SocialLink
    {
        public bool enabled;
        public string url = "https://";
        public Sprite icon;
        public string name;
        public string urlHint;
        public SocialLink(string name, string hint)
        {
            this.name = name;
            this.urlHint = hint;
        }
    }

    private SocialLink lnkInsta;
    private SocialLink lnkFb;
    private SocialLink lnkYt;
    private SocialLink lnkCustom;
    private SocialLink lnkPhone;
    private SocialLink lnkEmail;
    private SocialLink lnkContact;

    // ─── Constants ──────────────────────────────────────────────────
    private const string TemplatePath = "Assets/Scenes_1/Demo-VisitingCard.unity";
    private const string ScenesFolder = "Assets/Scenes_1";
    private const string MatFolder    = "Assets/AR_Assets/Materials";
    private const string MeshFolder   = "Assets/AR_Assets/Planes/Generated";
    private const string IconsFolder  = "Assets/AR_Assets/Visiting Card";

    // ─── UI state ──────────────────────────────────────────────────
    private Vector2 scrollPosition;
    private bool showAdvancedImage = false;

    // ─── Tracking image mesh ───────────────────────────────────────
    private bool overrideImageMesh = false;
    private Mesh customImageMesh;

    // ─── Cached style references (rebuilt on domain reload) ────────
    private GUIStyle _headerStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _stepBoxStyle;
    private GUIStyle _okStyle;
    private GUIStyle _warnStyle;
    private GUIStyle _errStyle;
    private GUIStyle _hintStyle;

    // ───────────────────────────────────────────────────────────────
    [MenuItem("Tools/AR Card Setup Wizard")]
    public static void ShowWindow()
    {
        var window = GetWindow<ARCardSetupWindow>("AR Card Setup");
        window.minSize = new Vector2(460, 700);
        window.Show();
    }

    // ═══════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════════════════
    private void OnEnable()
    {
        InitSocialLinks();
    }

    private void InitSocialLinks()
    {
        lnkInsta   = new SocialLink("Instagram",
            "Your Instagram profile URL.\nExample: https://instagram.com/yourname");
        lnkFb      = new SocialLink("Facebook",
            "Your Facebook page or profile URL.\nExample: https://facebook.com/yourpage");
        lnkYt      = new SocialLink("YouTube",
            "Your YouTube channel or video URL.\nExample: https://youtube.com/@yourname");
        lnkCustom  = new SocialLink("Website / Custom",
            "Any website URL.\nExample: https://yourwebsite.com");
        lnkPhone   = new SocialLink("Phone Number",
            "A telephone link.  Format:  tel:+91XXXXXXXXXX\nExample: tel:+919876543210");
        lnkEmail   = new SocialLink("Email",
            "A mailto link.  Format:  mailto:you@example.com\nExample: mailto:hello@yourcompany.com");
        lnkContact = new SocialLink("Save Contact (.vcf)",
            "A direct-download link to a .vcf vCard file.\nExample: https://yoursite.com/contact.vcf");

        // Set default URL prefixes
        lnkPhone.url = "tel:+91";
        lnkEmail.url = "mailto:";

        // Try to load default icons
        lnkInsta.icon   = AssetDatabase.LoadAssetAtPath<Sprite>($"{IconsFolder}/Insta_Icon.png");
        lnkFb.icon      = AssetDatabase.LoadAssetAtPath<Sprite>($"{IconsFolder}/FB_Icon.png");
        lnkYt.icon      = AssetDatabase.LoadAssetAtPath<Sprite>($"{IconsFolder}/YT_Icon.png");
        lnkContact.icon = AssetDatabase.LoadAssetAtPath<Sprite>($"{IconsFolder}/Save_Button.png");
    }

    // ═══════════════════════════════════════════════════════════════
    //  STYLES  (lazy-built so they survive domain reloads)
    // ═══════════════════════════════════════════════════════════════
    private void EnsureStyles()
    {
        if (_headerStyle != null) return;

        _headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 16,
            alignment = TextAnchor.MiddleCenter,
            margin    = new RectOffset(0, 0, 8, 8)
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

    // ═══════════════════════════════════════════════════════════════
    //  GUI
    // ═══════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        EnsureStyles();
        // Guard against links being null after domain reload
        if (lnkInsta == null) InitSocialLinks();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        // ── Title ────────────────────────────────────────────────
        GUILayout.Space(6);
        GUILayout.Label("AR Business Card Generator", _headerStyle);
        EditorGUILayout.HelpBox(
            "Create a complete AR Business Card scene in 4 simple steps:\n\n" +
            "  Step 1 →  Scene & Target Info  (name, ID, tracking image)\n" +
            "  Step 2 →  Video Overlay  (optional — play a video on the card)\n" +
            "  Step 3 →  Card Design  (optional overlay background)\n" +
            "  Step 4 →  Social Links  (Instagram, Facebook, YouTube, etc.)\n\n" +
            "The tool auto-generates meshes, materials, a World-Space Canvas\n" +
            "with icon buttons, and wires everything up.\n" +
            "All issues are shown inline — fix them and the ▶ Create button will unlock.",
            MessageType.Info);
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════
        //  STEP 1 — Scene & Target Info
        // ═════════════════════════════════════════════════════════
        DrawSectionHeader("Step 1 :  Scene & Target Info", IsStep1Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            // --- Scene Name ---
            sceneName = EditorGUILayout.TextField(
                new GUIContent("Scene Name",
                    "A human-readable name for the new scene, e.g. 'My Business Card'.\n" +
                    "A Unity scene file will be created with this name."),
                sceneName);
            if (string.IsNullOrEmpty(sceneName))
                EditorGUILayout.HelpBox("Enter a descriptive scene name (e.g. 'My Business Card').", MessageType.Warning);
            else if (System.IO.File.Exists($"{ScenesFolder}/{sceneName}.unity"))
                EditorGUILayout.HelpBox(
                    "A scene with this name already exists and will be overwritten.",
                    MessageType.Warning);

            // --- Target ID ---
            EditorGUI.BeginChangeCheck();
            targetId = EditorGUILayout.TextField(
                new GUIContent("Target ID  (no spaces)",
                    "A short unique key with NO SPACES.  Used internally to identify this image.\n" +
                    "Example: 'Business_Card', 'My_Card'\n\n" +
                    "Spaces are automatically replaced with underscores."),
                targetId);
            if (EditorGUI.EndChangeCheck())
            {
                targetId = targetId.Replace(" ", "_");
                targetId = System.Text.RegularExpressions.Regex.Replace(targetId, @"[^a-zA-Z0-9_\-]", "");
            }
            if (string.IsNullOrEmpty(targetId))
                EditorGUILayout.HelpBox("Enter a Target ID (e.g. 'Business_Card'). Spaces are auto-replaced with underscores.", MessageType.Warning);
            else if (IsTargetIdRegistered(targetId))
                EditorGUILayout.HelpBox(
                    $"'{targetId}' already exists in the global target list — the existing entry will be updated.",
                    MessageType.Info);

            GUILayout.Space(4);

            // --- Tracking Image ---
            if (imageTexture == null)
            {
                EditorGUILayout.LabelField(
                    "Drag a Texture2D from your Project window into the slot below.",
                    _hintStyle);
                GUILayout.Space(2);
            }

            EditorGUI.BeginChangeCheck();
            imageTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Tracking Image",
                    "The real-world image the AR camera will recognise (your business card).\n\n" +
                    "Tips for best tracking:\n" +
                    "• High contrast with lots of unique detail\n" +
                    "• Non-symmetrical designs work best\n" +
                    "• Avoid plain text, logos, or solid colours\n" +
                    "• Minimum 500 × 500 px recommended"),
                imageTexture, typeof(Texture2D), false);
            bool imageJustChanged = EditorGUI.EndChangeCheck();

            if (imageTexture != null)
            {
                // Auto-suggest Target ID & Scene Name from image name
                if (imageJustChanged && string.IsNullOrEmpty(targetId))
                    targetId = SanitiseId(imageTexture.name);
                if (imageJustChanged && string.IsNullOrEmpty(sceneName))
                    sceneName = imageTexture.name;

                // Read/Write check
                DrawReadWriteCheck(imageTexture, "Tracking Image");

                // Image dimensions
                DrawImageInfo(imageTexture);

                // Physical Width
                GUILayout.Space(4);
                physicalWidth = EditorGUILayout.FloatField(
                    new GUIContent("Physical Width (Units)",
                        "The width of the card in Unity world-units.\n" +
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

                // Advanced (custom mesh override)
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

        // ═════════════════════════════════════════════════════════
        //  STEP 2 — Video Overlay (optional)
        // ═════════════════════════════════════════════════════════
        DrawSectionHeader("Step 2 :  Video Overlay  (optional)", IsStep2Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            enableVideo = EditorGUILayout.Toggle(
                new GUIContent("Enable Video Overlay",
                    "Turn ON to play a video on the business card.\n\n" +
                    "When ON:\n" +
                    "  • A video child object is created under the tracking target\n" +
                    "  • CDNARVideoController + VideoPlayer are added automatically\n" +
                    "  • The video auto-plays when the card is tracked\n\n" +
                    "When OFF:\n" +
                    "  • The card shows only the social-link buttons (no video)"),
                enableVideo);

            if (enableVideo)
            {
                GUILayout.Space(4);

                // — CDN Video URL —
                cdnVideoUrl = EditorGUILayout.TextField(
                    new GUIContent("CDN Video URL",
                        "A direct link to your .mp4 or .webm video hosted on a CDN.\n\n" +
                        "Example:\nhttps://cdn.jsdelivr.net/gh/user/repo@main/videos/demo.mp4\n\n" +
                        "The URL must point directly to the video file, not to a web-page."),
                    cdnVideoUrl);
                DrawUrlValidation();

                GUILayout.Space(4);

                // — Green Screen —
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
                    EditorGUILayout.LabelField(
                        "Two-layer setup:  background = tracking image,  foreground = video with green removed.",
                        _hintStyle);
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "Normal mode — the video plays on the tracking-image surface.",
                        _hintStyle);
                }

                GUILayout.Space(4);

                // — First Frame —
                EditorGUI.BeginChangeCheck();
                firstFrameTexture = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent(isGreenScreen ? "First Frame Image" : "First Frame Image  (optional)",
                        isGreenScreen
                            ? "A screenshot of the first video frame (with green screen visible).\n" +
                              "This acts as a placeholder texture while the video loads.\n\n" +
                              "Quick way to extract it:\n" +
                              "  ffmpeg -i video.mp4 -vframes 1 first_frame.png"
                            : "Drag a screenshot of any video frame to auto-detect the video resolution.\n\n" +
                              "Quick way to extract it:\n" +
                              "  ffmpeg -i video.mp4 -vframes 1 first_frame.png"),
                    firstFrameTexture, typeof(Texture2D), false);
                bool ffChanged = EditorGUI.EndChangeCheck();

                if (firstFrameTexture != null)
                {
                    DrawReadWriteCheck(firstFrameTexture, "First Frame");
                    if (ffChanged && firstFrameTexture != prevFirstFrame)
                    {
                        videoWidthPx  = firstFrameTexture.width;
                        videoHeightPx = firstFrameTexture.height;
                        Debug.Log($"[AR Card Wizard] Auto-detected video size: {videoWidthPx} × {videoHeightPx}");
                    }
                    prevFirstFrame = firstFrameTexture;
                    GUILayout.Label($"  ✓  First frame: {firstFrameTexture.width} × {firstFrameTexture.height} px", _okStyle);
                }
                else if (isGreenScreen)
                {
                    EditorGUILayout.HelpBox(
                        "Drag the first frame of your video here.\n" +
                        "To extract it:  ffmpeg -i your_video.mp4 -vframes 1 first_frame.png",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "💡  Optional: drag any frame to auto-fill the dimensions below.",
                        MessageType.None);
                }

                // — Video Dimensions —
                GUILayout.Space(4);
                GUILayout.Label("Video Dimensions", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Pixel dimensions of your source video.  Auto-filled from the First Frame image.",
                    _hintStyle);

                videoWidthPx  = EditorGUILayout.IntField(
                    new GUIContent("Source Width (px)", "Pixel width of the source video."),
                    videoWidthPx);
                videoHeightPx = EditorGUILayout.IntField(
                    new GUIContent("Source Height (px)", "Pixel height of the source video."),
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

                // — Green-screen-only settings —
                if (isGreenScreen)
                {
                    GUILayout.Space(4);
                    videoScale = EditorGUILayout.FloatField(
                        new GUIContent("Video Scale",
                            "Multiplier for the video layer size.\n" +
                            "  1.0 = same size,  1.2 = 20% larger,  0.8 = 20% smaller"),
                        videoScale);

                    alignToBottom = EditorGUILayout.Toggle(
                        new GUIContent("Align to Bottom Edge",
                            "Automatically aligns the bottom of the video with the bottom of the card.\n" +
                            "Ideal when the subject is standing on the card."),
                        alignToBottom);

                    videoOffset = EditorGUILayout.Vector2Field(
                        new GUIContent(alignToBottom ? "Extra Offset (X, Y)" : "Video Offset (X, Y)",
                            "Shifts the video layer.  +X = right,  +Y = up"),
                        videoOffset);

                    EditorGUILayout.HelpBox(
                        "💡  Tuning tips:\n" +
                        "• Subject too small  →  increase Video Scale\n" +
                        "• Misaligned  →  adjust Video Offset\n" +
                        "• The video sits 0.001 units forward to avoid z-fighting",
                        MessageType.None);

                    // Advanced (custom video mesh)
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
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Toggle 'Enable Video Overlay' to play a video when the card is tracked.",
                    _hintStyle);
            }
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════
        //  STEP 3 — Card Design
        // ═════════════════════════════════════════════════════════
        DrawSectionHeader("Step 3 :  Card Design  (optional)", true);
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            EditorGUILayout.LabelField(
                "Add a sprite that overlays the tracking image as a card background.\n" +
                "Leave empty to show only the social-link buttons on the tracking image.",
                _hintStyle);
            GUILayout.Space(2);

            cardBackground = (Sprite)EditorGUILayout.ObjectField(
                new GUIContent("Card Background",
                    "A sprite that acts as the overlay background over the tracking image.\n" +
                    "This is optional — if you leave it empty, the buttons appear\n" +
                    "directly over the tracking image."),
                cardBackground, typeof(Sprite), false);

            if (cardBackground != null)
                GUILayout.Label("  ✓  Card background assigned", _okStyle);
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════
        //  STEP 4 — Social Links
        // ═════════════════════════════════════════════════════════
        DrawSectionHeader("Step 4 :  Social Links", IsStep3Valid());
        EditorGUILayout.BeginVertical(_stepBoxStyle);
        {
            EditorGUILayout.LabelField(
                "Enable the social links you want to display on the AR card.\n" +
                "Each enabled link needs a valid URL and an icon sprite.",
                _hintStyle);
            GUILayout.Space(4);

            DrawSocialLink(lnkInsta);
            DrawSocialLink(lnkFb);
            DrawSocialLink(lnkYt);
            DrawSocialLink(lnkCustom);
            DrawSocialLink(lnkPhone);
            DrawSocialLink(lnkEmail);
            DrawSocialLink(lnkContact);

            // Count enabled
            int enabledCount = CountEnabledLinks();
            GUILayout.Space(4);
            if (enabledCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "No social links are enabled.  Enable at least one link above.",
                    MessageType.Warning);
            }
            else
            {
                GUILayout.Label(
                    $"  {enabledCount} link(s) enabled — buttons will be laid out horizontally at the bottom of the card.",
                    _hintStyle);
            }
        }
        EditorGUILayout.EndVertical();
        GUILayout.Space(8);


        // ═════════════════════════════════════════════════════════
        //  PRE-FLIGHT CHECKLIST
        // ═════════════════════════════════════════════════════════
        DrawPreFlightChecklist();
        GUILayout.Space(6);

        // ═════════════════════════════════════════════════════════
        //  CREATE BUTTON
        // ═════════════════════════════════════════════════════════
        bool allGood = IsAllValid();

        EditorGUI.BeginDisabledGroup(!allGood);
        GUI.backgroundColor = allGood
            ? new Color(0.25f, 0.85f, 0.4f)
            : new Color(0.45f, 0.45f, 0.45f);
        if (GUILayout.Button("▶   Create Scene & Setup AR Card", GUILayout.Height(48)))
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

    // ═══════════════════════════════════════════════════════════════
    //  DRAWING HELPERS
    // ═══════════════════════════════════════════════════════════════

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
                Debug.Log($"[AR Card Wizard] ✅ Enabled Read/Write on: {path}");
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

    /// <summary>Draw a single social link with toggle, URL, icon, and validation.</summary>
    private void DrawSocialLink(SocialLink link)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        link.enabled = EditorGUILayout.ToggleLeft(link.name, link.enabled, EditorStyles.boldLabel, GUILayout.Width(180));
        if (link.enabled && link.icon != null && !string.IsNullOrEmpty(link.url) && link.url != "https://" && link.url != "tel:+91" && link.url != "mailto:")
            GUILayout.Label("✓", _okStyle);
        EditorGUILayout.EndHorizontal();

        if (link.enabled)
        {
            EditorGUI.indentLevel++;

            link.url = EditorGUILayout.TextField(
                new GUIContent("URL", link.urlHint),
                link.url);

            link.icon = (Sprite)EditorGUILayout.ObjectField(
                new GUIContent("Icon Sprite",
                    "The icon displayed on the AR card button.\n" +
                    "Recommended: square image, at least 128 × 128 px."),
                link.icon, typeof(Sprite), false);

            // Validation
            if (link.icon == null)
                EditorGUILayout.HelpBox("Assign an icon sprite for this button.", MessageType.Warning);

            if (IsLinkUrlEmpty(link))
                EditorGUILayout.HelpBox($"Enter a valid URL.  {link.urlHint}", MessageType.Error);

            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndVertical();
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRE-FLIGHT CHECKLIST
    // ═══════════════════════════════════════════════════════════════

    private void DrawPreFlightChecklist()
    {
        GUILayout.Label("Pre-Flight Checklist", _sectionStyle);
        EditorGUILayout.BeginVertical(_stepBoxStyle);

        CheckItem("Scene Name filled in",
            !string.IsNullOrEmpty(sceneName));

        CheckItem("Target ID filled in  (no spaces)",
            !string.IsNullOrEmpty(targetId));

        CheckItem("Tracking Image assigned",
            imageTexture != null);

        if (imageTexture != null)
        {
            CheckItem("Tracking Image → Read/Write enabled",
                IsTextureReadable(imageTexture));
        }

        CheckItem("Physical Width > 0", physicalWidth > 0);

        CheckItem("Template scene exists  (Demo-VisitingCard.unity)",
            System.IO.File.Exists(TemplatePath));

        // Video overlay checks
        if (enableVideo)
        {
            CheckItem("CDN Video URL provided", IsUrlAcceptable());
            CheckItem("Video dimensions > 0",
                videoWidthPx > 0 && videoHeightPx > 0);

            if (isGreenScreen)
            {
                CheckItem("First Frame Image assigned",
                    firstFrameTexture != null);
                if (firstFrameTexture != null)
                    CheckItem("First Frame \u2192 Read/Write enabled",
                        IsTextureReadable(firstFrameTexture));

                bool chromaOk = Shader.Find("Imagine/ChromaKeyCutout") != null;
                CheckItem("ChromaKeyCutout shader available", chromaOk);
                if (!chromaOk)
                {
                    EditorGUILayout.HelpBox(
                        "'Imagine/ChromaKeyCutout' shader not found.  A fallback will be used, " +
                        "but green-screen removal won't work.",
                        MessageType.Error);
                }

                if (overrideVideoMesh)
                    CheckItem("Custom Video Mesh assigned", customVideoMesh != null);
            }
        }

        CheckItem("Social links configured correctly",
            IsStep3Valid());

        if (overrideImageMesh)
            CheckItem("Custom Image Mesh assigned", customImageMesh != null);

        EditorGUILayout.EndVertical();
    }

    private void CheckItem(string label, bool ok)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(ok ? "  ✓" : "  ✗", ok ? _okStyle : _errStyle, GUILayout.Width(28));
        GUILayout.Label(label, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    // ═══════════════════════════════════════════════════════════════
    //  VALIDATION  (pure-logic, no drawing)
    // ═══════════════════════════════════════════════════════════════

    private bool IsStep1Valid()
    {
        return !string.IsNullOrEmpty(sceneName) &&
               !string.IsNullOrEmpty(targetId) &&
               imageTexture != null &&
               IsTextureReadable(imageTexture) &&
               physicalWidth > 0 &&
               (!overrideImageMesh || customImageMesh != null);
    }

    private bool IsStep2Valid()
    {
        if (!enableVideo) return true; // disabled = always valid
        if (!IsUrlAcceptable()) return false;
        if (videoWidthPx <= 0 || videoHeightPx <= 0) return false;
        if (isGreenScreen)
        {
            if (firstFrameTexture == null) return false;
            if (!IsTextureReadable(firstFrameTexture)) return false;
            if (overrideVideoMesh && customVideoMesh == null) return false;
        }
        return true;
    }

    private bool IsStep3Valid()
    {
        SocialLink[] links = GetAllLinks();
        foreach (var l in links)
        {
            if (!l.enabled) continue;
            if (l.icon == null) return false;
            if (IsLinkUrlEmpty(l)) return false;
        }
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
        var gs = ImageTrackerGlobalSettings.Instance;
        if (gs == null || gs.imageTargetInfos == null) return false;
        foreach (var info in gs.imageTargetInfos)
            if (info.id == id) return true;
        return false;
    }

    private bool IsLinkUrlEmpty(SocialLink link)
    {
        if (string.IsNullOrEmpty(link.url)) return true;
        if (link.url == "https://" || link.url == "tel:+91" || link.url == "mailto:") return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    //  UTILITY
    // ═══════════════════════════════════════════════════════════════

    private static string SanitiseId(string raw)
    {
        string s = raw.Replace(" ", "_");
        return System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9_\-]", "");
    }

    private SocialLink[] GetAllLinks()
    {
        return new SocialLink[] { lnkInsta, lnkFb, lnkYt, lnkCustom, lnkPhone, lnkEmail, lnkContact };
    }

    private int CountEnabledLinks()
    {
        int count = 0;
        foreach (var l in GetAllLinks())
            if (l.enabled) count++;
        return count;
    }

    private void ResetFields()
    {
        sceneName          = "";
        targetId           = "";
        imageTexture       = null;
        physicalWidth      = 1f;
        cardBackground     = null;
        overrideImageMesh  = false;
        customImageMesh    = null;
        showAdvancedImage  = false;
        enableVideo        = false;
        cdnVideoUrl        = "https://";
        isGreenScreen      = false;
        firstFrameTexture  = null;
        prevFirstFrame     = null;
        videoWidthPx       = 1080;
        videoHeightPx      = 1920;
        videoScale         = 1f;
        alignToBottom      = true;
        videoOffset        = Vector2.zero;
        overrideVideoMesh  = false;
        customVideoMesh    = null;
        showAdvancedVideo  = false;
        InitSocialLinks();
        Repaint();
    }

    // ═══════════════════════════════════════════════════════════════
    //  MAIN SETUP LOGIC  (with progress bar)
    // ═══════════════════════════════════════════════════════════════

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
            EditorUtility.DisplayProgressBar("AR Card Setup", "Creating folders…", 0.05f);
            EnsureFolder("Assets/AR_Assets");
            EnsureFolder(MatFolder);
            EnsureFolder("Assets/AR_Assets/Planes");
            EnsureFolder(MeshFolder);

            // ── 1. Register image target in Global Settings ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Registering image target…", 0.15f);
            RegisterImageTarget();

            // ── 2. Duplicate template scene ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Duplicating template scene…", 0.25f);
            string newScenePath = $"{ScenesFolder}/{sceneName}.unity";

            if (EditorSceneManager.GetActiveScene().isDirty)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            }

            if (System.IO.File.Exists(newScenePath))
                AssetDatabase.DeleteAsset(newScenePath);

            if (!AssetDatabase.CopyAsset(TemplatePath, newScenePath))
            {
                EditorUtility.DisplayDialog("Error",
                    "Failed to duplicate template scene.  Check the Console for details.", "OK");
                return;
            }
            AssetDatabase.Refresh();

            EditorUtility.DisplayProgressBar("AR Card Setup", "Opening new scene…", 0.35f);
            Scene newScene = EditorSceneManager.OpenScene(newScenePath, OpenSceneMode.Single);

            // ── 3. Find ImageTracker ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Configuring ImageTracker…", 0.45f);
            ImageTracker tracker = null;
            foreach (var go in newScene.GetRootGameObjects())
            {
                tracker = go.GetComponentInChildren<ImageTracker>(true);
                if (tracker != null) break;
            }
            if (tracker == null)
            {
                EditorUtility.DisplayDialog("Error",
                    "Could not find ImageTracker in the template scene.  " +
                    "Make sure the template 'Demo-VisitingCard.unity' contains an ImageTracker.", "OK");
                return;
            }

            // ── 4. Clean up existing targets in tracker ──
            // ImageTracker.imageTargets is [SerializeField] private, so we use SerializedObject
            var trackerSo = new SerializedObject(tracker);
            var imageTargetsProp = trackerSo.FindProperty("imageTargets");

            // Destroy child objects that were part of old targets
            if (imageTargetsProp != null)
            {
                for (int i = 0; i < imageTargetsProp.arraySize; i++)
                {
                    Transform t = imageTargetsProp.GetArrayElementAtIndex(i)
                        .FindPropertyRelative("transform").objectReferenceValue as Transform;
                    if (t != null) DestroyImmediate(t.gameObject);
                }
            }

            // ── 5. Create new target object ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Generating tracking-image mesh…", 0.55f);
            float targetW = physicalWidth;
            float targetH = physicalWidth * ((float)imageTexture.height / imageTexture.width);

            Mesh targetMesh = overrideImageMesh && customImageMesh != null
                ? customImageMesh
                : GetOrCreateMesh(targetW, targetH, targetId + "_TrackImg");

            GameObject targetObj = new GameObject(targetId);
            targetObj.transform.SetParent(tracker.transform, false);
            targetObj.transform.localPosition = Vector3.zero;
            targetObj.transform.localRotation = Quaternion.identity;
            targetObj.transform.localScale    = Vector3.one;

            var targetMf = targetObj.AddComponent<MeshFilter>();
            targetMf.sharedMesh = targetMesh;

            var targetMr = targetObj.AddComponent<MeshRenderer>();
            Material mat = new Material(Shader.Find("Unlit/Texture"))
            {
                name = targetId + "_Mat",
                mainTexture = imageTexture
            };
            AssetDatabase.CreateAsset(mat, $"{MatFolder}/{mat.name}.mat");
            targetMr.sharedMaterial = mat;

            // Wire into tracker's imageTargets list via SerializedObject
            if (imageTargetsProp != null)
            {
                imageTargetsProp.ClearArray();
                imageTargetsProp.InsertArrayElementAtIndex(0);
                var elem = imageTargetsProp.GetArrayElementAtIndex(0);
                elem.FindPropertyRelative("id").stringValue = targetId;
                elem.FindPropertyRelative("transform").objectReferenceValue = targetObj.transform;
                trackerSo.ApplyModifiedProperties();
            }

            // ── 6. Create Video Overlay (if enabled) ──
            string videoInfo = "";
            if (enableVideo)
            {
                EditorUtility.DisplayProgressBar("AR Card Setup", "Creating video overlay…", 0.60f);

                float vidW = physicalWidth;
                float vidH = physicalWidth * ((float)videoHeightPx / videoWidthPx);

                // Create video child object
                GameObject vidObj = new GameObject(targetId + " vid");
                vidObj.transform.SetParent(targetObj.transform, false);
                vidObj.transform.localRotation = Quaternion.identity;

                // Video mesh
                Mesh vidMesh = (overrideVideoMesh && customVideoMesh != null)
                    ? customVideoMesh
                    : GetOrCreateMesh(vidW, vidH, targetId + "_Vid");

                MeshFilter vidMf = vidObj.AddComponent<MeshFilter>();
                vidMf.sharedMesh = vidMesh;

                MeshRenderer vidRend = vidObj.AddComponent<MeshRenderer>();

                // VideoPlayer
                VideoPlayer vp = vidObj.AddComponent<VideoPlayer>();
                vp.playOnAwake = false;
                vp.renderMode  = VideoRenderMode.MaterialOverride;
                vp.targetMaterialRenderer = vidRend;

                // CDNARVideoController
                CDNARVideoController cdn = vidObj.AddComponent<CDNARVideoController>();
                cdn.cdnVideoUrl = cdnVideoUrl;
                cdn.webGLSoundTargetKey = targetId;

                if (!isGreenScreen)
                {
                    // Normal video: Unlit/Texture material
                    Material vidMat = new Material(Shader.Find("Unlit/Texture"))
                    {
                        name = targetId + "_VidMat",
                        mainTexture = firstFrameTexture != null ? (Texture)firstFrameTexture : imageTexture
                    };
                    AssetDatabase.CreateAsset(vidMat, $"{MatFolder}/{vidMat.name}.mat");
                    vidRend.sharedMaterial = vidMat;

                    vidObj.transform.localPosition = new Vector3(0, 0, -0.01f);
                    vidObj.transform.localScale    = Vector3.one;

                    videoInfo = $"  \u2022 Video mesh: {vidW:F2} \u00d7 {vidH:F2} units\n" +
                                "  \u2022 Mode: normal video overlay\n";
                }
                else
                {
                    // Green screen: ChromaKeyCutout material
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
                    if (firstFrameTexture != null)
                        chromaMat.mainTexture = firstFrameTexture;
                    AssetDatabase.CreateAsset(chromaMat, $"{MatFolder}/{chromaMat.name}.mat");
                    vidRend.sharedMaterial = chromaMat;

                    // Calculate Y offset for bottom-alignment
                    float finalYOffset = videoOffset.y;
                    if (alignToBottom && imageTexture != null)
                    {
                        float bgH = physicalWidth * ((float)imageTexture.height / imageTexture.width);
                        finalYOffset = (vidH * videoScale - bgH) / 2f + videoOffset.y;
                    }

                    vidObj.transform.localPosition = new Vector3(videoOffset.x, finalYOffset, -0.001f);
                    vidObj.transform.localScale    = new Vector3(videoScale, videoScale, 1f);

                    videoInfo = $"  \u2022 Video mesh: {vidW:F2} \u00d7 {vidH:F2} units\n" +
                                "  \u2022 Mode: green-screen (chroma key)\n";
                }
            }

            // ── 7. Create World Canvas ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Generating UI canvas…", 0.68f);

            GameObject canvasObj = new GameObject("World Canvas");
            canvasObj.transform.SetParent(targetObj.transform, false);

            Canvas worldCanvas = canvasObj.AddComponent<Canvas>();
            worldCanvas.renderMode = RenderMode.WorldSpace;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            RectTransform canvasRt = canvasObj.GetComponent<RectTransform>();
            float scaleConst = 0.00072f;
            canvasRt.localScale    = new Vector3(scaleConst, scaleConst, scaleConst);
            // Canvas sits in front of video layer (which is at -0.01)
            canvasRt.localPosition = new Vector3(0, 0, enableVideo ? -0.02f : -0.01f);
            canvasRt.localRotation = Quaternion.identity;

            float canvasWidthPx  = targetW / scaleConst;
            float canvasHeightPx = targetH / scaleConst;
            canvasRt.sizeDelta = new Vector2(canvasWidthPx, canvasHeightPx);

            // ── 8. Card Background (optional) ──
            if (cardBackground != null)
            {
                GameObject bgObj = new GameObject("Bg");
                bgObj.transform.SetParent(canvasRt, false);
                Image bgImg = bgObj.AddComponent<Image>();
                bgImg.sprite = cardBackground;

                RectTransform bgRt = bgObj.GetComponent<RectTransform>();
                bgRt.anchorMin        = new Vector2(0, 0);
                bgRt.anchorMax        = new Vector2(1, 1);
                bgRt.sizeDelta        = Vector2.zero;
                bgRt.anchoredPosition = Vector2.zero;
            }

            // ── 9. Social Buttons ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Creating social buttons…", 0.75f);

            SocialLink[] allLinks = GetAllLinks();
            List<SocialLink> activeLinks = new List<SocialLink>();
            foreach (var l in allLinks) if (l.enabled) activeLinks.Add(l);

            string buttonInfo = "";

            if (activeLinks.Count > 0)
            {
                // Horizontal container at the bottom
                GameObject containerObj = new GameObject("ButtonContainer");
                containerObj.transform.SetParent(canvasRt, false);
                RectTransform containerRt = containerObj.AddComponent<RectTransform>();

                containerRt.anchorMin = new Vector2(0, 0);
                containerRt.anchorMax = new Vector2(1, 0);
                containerRt.pivot     = new Vector2(0.5f, 0);
                containerRt.sizeDelta = new Vector2(canvasWidthPx * 0.9f, canvasHeightPx * 0.25f);
                containerRt.anchoredPosition = new Vector2(0, canvasHeightPx * 0.05f);

                HorizontalLayoutGroup hlg = containerObj.AddComponent<HorizontalLayoutGroup>();
                hlg.childAlignment        = TextAnchor.MiddleCenter;
                hlg.childControlWidth     = false;
                hlg.childControlHeight    = false;
                hlg.childForceExpandWidth  = false;
                hlg.childForceExpandHeight = false;
                hlg.spacing = canvasWidthPx * 0.05f;

                float btnSize = Mathf.Min(canvasWidthPx / (activeLinks.Count + 1), canvasHeightPx * 0.2f);

                foreach (var link in activeLinks)
                {
                    GameObject btnObj = new GameObject(link.name.Replace(" ", "").Replace("/", ""));
                    btnObj.transform.SetParent(containerRt, false);

                    Image btnImg = btnObj.AddComponent<Image>();
                    btnImg.sprite = link.icon;
                    btnImg.preserveAspect = true;

                    Button btn = btnObj.AddComponent<Button>();
                    GoToUrl goUrl = btnObj.AddComponent<GoToUrl>();

                    RectTransform rt = btnObj.GetComponent<RectTransform>();
                    rt.sizeDelta = new Vector2(btnSize, btnSize);

                    // Wire button → GoToUrl.GoTo(link.url) as a persistent listener
                    var methodInfo = UnityEvent.GetValidMethodInfo(goUrl, "GoTo", new System.Type[] { typeof(string) });
                    if (methodInfo != null)
                    {
                        var action = System.Delegate.CreateDelegate(
                            typeof(UnityEngine.Events.UnityAction<string>),
                            goUrl, methodInfo, false) as UnityEngine.Events.UnityAction<string>;
                        if (action != null)
                        {
                            UnityEditor.Events.UnityEventTools.AddStringPersistentListener(
                                btn.onClick, action, link.url);
                        }
                    }

                    buttonInfo += $"  • {link.name} → {link.url}\n";
                }
            }

            // ── 10. Wire ScanArea events ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Wiring ScanArea events…", 0.85f);
            WireScanAreaEvents(newScene, trackerSo);

            // ── 11. Save scene & add to Build Settings ──
            EditorUtility.DisplayProgressBar("AR Card Setup", "Saving scene…", 0.95f);
            EditorSceneManager.MarkSceneDirty(newScene);
            EditorSceneManager.SaveScene(newScene);
            AddToBuildSettings(newScenePath);

            EditorUtility.DisplayProgressBar("AR Card Setup", "Done!", 1.0f);

            // ── Success dialog ──
            EditorUtility.DisplayDialog("✅  Scene Created Successfully!",
                $"Scene:  '{sceneName}'\n" +
                $"Target ID:  '{targetId}'\n\n" +
                $"  • Tracking mesh: {targetW:F2} × {targetH:F2} units\n" +
                (enableVideo ? videoInfo : "") +
                $"  • {activeLinks.Count} social button(s) created\n" +
                (cardBackground != null ? "  • Card background applied\n" : "") +
                buttonInfo +
                $"  • Material saved to:  {MatFolder}/\n" +
                $"  • Scene added to Build Settings\n\n" +
                "─── What to do next ───\n\n" +
                "1.  Press ▶ Play in Unity to test the scene\n" +
                "2.  Adjust button positions in the World Canvas if needed\n" +
                (enableVideo ? "3.  Fine-tune green-screen scale/offset in the Inspector\n" : "") +
                (enableVideo ? "4.  When ready, build for WebGL  (File → Build Settings)" :
                               "3.  When ready, build for WebGL  (File → Build Settings)"),
                "Got it!");

            // Highlight the new scene in the Project window
            var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(newScenePath);
            if (sceneAsset != null)
            {
                EditorGUIUtility.PingObject(sceneAsset);
                Selection.activeObject = sceneAsset;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[AR Card Wizard] Setup failed: {ex}");
            EditorUtility.DisplayDialog("Error",
                "An error occurred during setup.  Check the Console for details.\n\n" +
                ex.Message, "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  SCENE-BUILDING HELPERS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Add or update image target in ImageTrackerGlobalSettings.</summary>
    private void RegisterImageTarget()
    {
        var gs = ImageTrackerGlobalSettings.Instance;
        if (gs == null)
        {
            Debug.LogWarning("[AR Card Wizard] ImageTrackerGlobalSettings not found in Resources.");
            return;
        }

        if (gs.imageTargetInfos == null)
            gs.imageTargetInfos = new List<ImageTargetInfo>();

        bool found = false;
        foreach (var info in gs.imageTargetInfos)
        {
            if (info.id != targetId) continue;
            info.texture = imageTexture;
            found = true;
            break;
        }
        if (!found)
            gs.imageTargetInfos.Add(new ImageTargetInfo { id = targetId, texture = imageTexture });

        EditorUtility.SetDirty(gs);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AR Card Wizard] Registered image target '{targetId}' in Global Settings.");
    }

    /// <summary>
    /// Find the ScanArea UI element and wire OnImageFound → ScanArea.SetActive(false),
    /// OnImageLost → ScanArea.SetActive(true) via SerializedObject.
    /// </summary>
    private void WireScanAreaEvents(Scene scene, SerializedObject trackerSo)
    {
        // Find the ScanArea game object in the screen-space canvas
        GameObject scanArea = null;
        foreach (var go in scene.GetRootGameObjects())
        {
            var canvas = go.GetComponent<Canvas>();
            if (canvas != null && !go.name.Contains("World"))
            {
                Transform sa = go.transform.Find("ScanArea");
                if (sa != null) { scanArea = sa.gameObject; break; }
            }
        }

        if (scanArea == null)
        {
            Debug.Log("[AR Card Wizard] ScanArea not found in template — skipping event wiring.");
            return;
        }

        // OnImageFound and OnImageLost are [SerializeField] private UnityEvent<string>,
        // so we use SerializedObject to manipulate their persistent calls.
        WireEventSetActive(trackerSo, "OnImageFound", scanArea, false);
        WireEventSetActive(trackerSo, "OnImageLost",  scanArea, true);
        trackerSo.ApplyModifiedProperties();
        Debug.Log("[AR Card Wizard] Wired ScanArea show/hide to ImageTracker events.");
    }

    /// <summary>
    /// Wire a single serialized UnityEvent to call target.SetActive(value)
    /// as a persistent listener with a static bool parameter.
    /// </summary>
    private void WireEventSetActive(SerializedObject so, string eventFieldName,
                                     GameObject target, bool value)
    {
        var eventProp = so.FindProperty(eventFieldName);
        if (eventProp == null)
        {
            Debug.LogWarning($"[AR Card Wizard] Could not find event '{eventFieldName}' on tracker.");
            return;
        }

        var callsProp = eventProp.FindPropertyRelative("m_PersistentCalls.m_Calls");
        if (callsProp == null) return;

        // Clear existing persistent calls
        callsProp.ClearArray();

        // Add new persistent call: target.SetActive(value)
        callsProp.InsertArrayElementAtIndex(0);
        var call = callsProp.GetArrayElementAtIndex(0);
        call.FindPropertyRelative("m_Target").objectReferenceValue = target;
        call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue =
            "UnityEngine.GameObject, UnityEngine";
        call.FindPropertyRelative("m_MethodName").stringValue = "SetActive";
        call.FindPropertyRelative("m_Mode").intValue = 6; // PersistentListenerMode.Bool
        call.FindPropertyRelative("m_CallState").intValue = 2; // UnityEventCallState.RuntimeOnly

        var argsProp = call.FindPropertyRelative("m_Arguments");
        if (argsProp != null)
        {
            argsProp.FindPropertyRelative("m_BoolArgument").boolValue = value;
        }
    }

    /// <summary>
    /// Creates a Quad mesh scaled to the physical dimensions provided.
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
        Debug.Log($"[AR Card Wizard] Mesh generated: {path}  ({w} × {h} units)");
        return mesh;
    }

    /// <summary>Append scene to the Build Settings list (if not already there).</summary>
    private static void AddToBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in scenes)
            if (s.path == scenePath) return;

        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    /// <summary>Create a directory (and parents) if it doesn't already exist.</summary>
    private static void EnsureFolder(string assetPath)
    {
        if (!AssetDatabase.IsValidFolder(assetPath))
        {
            string parent = System.IO.Path.GetDirectoryName(assetPath).Replace("\\", "/");
            string folder = System.IO.Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folder))
                AssetDatabase.CreateFolder(parent, folder);
        }
    }
}
