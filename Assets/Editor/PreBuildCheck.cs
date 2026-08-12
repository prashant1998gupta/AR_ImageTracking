using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Video;
using Imagine.WebAR;

public enum PreBuildLevel { Error, Warn, Info }

/// <summary>One thing that is wrong (or worth knowing) about this build.</summary>
public class PreBuildIssue
{
    public PreBuildLevel level;
    public string what;                 // what is wrong
    public string fix;                  // what to do about it
    public string fixLabel;             // button caption, when it can be fixed from here
    public System.Action fixAction;     // the one-click fix

    public PreBuildIssue(PreBuildLevel l, string w, string f) { level = l; what = w; fix = f; }
    public PreBuildIssue WithFix(string label, System.Action act) { fixLabel = label; fixAction = act; return this; }
}

/// <summary>The headline facts the pre-flight window shows at a glance.</summary>
public class PreBuildSummary
{
    public string scenePath = "";
    public string campaignName = "";
    public bool hasCampaign;
    public bool huntEnabled;
    public string analyticsKey = "";
    public int targetCount;
    public int registeredCount;
    public int unusedCount;
    public string template = "";
    public string buildTarget = "";
}

/// <summary>
/// Pre-flight validation for every ARRISE WebGL campaign.
///
/// Everything checked here has already shipped broken at least once, and every one
/// of those failures was SILENT — the build succeeded, the page loaded, and the
/// damage only showed up in front of a visitor:
///
///   • the hunt scene built without CampaignSettings    → no registration, no scoring
///   • a target missing from ImageTrackerGlobalSettings → that poster never tracks
///   • the global list left at 14 targets for a 2-target card → slow first load
///   • two target images sharing a filename             → one silently overwrites the other
///   • an analytics key mis-pasted                      → a client's dashboard stays at zero
///   • the wrong WebGL template selected                → tracker never initialises
///
/// Pressing BUILD opens the pre-flight window (PreBuildDialog) with everything it
/// found; the build only continues if you say so. Tools ▸ ARRISE ▸ Pre-Build Check
/// opens the same window any time, without building.
///
/// [PostProcessScene] stays as the authoritative net: it sees the real boot scene,
/// and an error there aborts the build even if the window was skipped.
/// </summary>
public class PreBuildCheck : IPreprocessBuildWithReport
{
    public int callbackOrder { get { return -100; } }   // before every other build hook

    private static readonly Regex IdRe  = new Regex(@"^[A-Za-z0-9_-]{1,64}$");
    private static readonly Regex KeyRe = new Regex(@"^[0-9a-fA-F]{64}$");

    // ════════════════════════════════════════════════════════════════════
    //  MENU — review without building (no shortcut: Ctrl+Shift+B is Unity's)
    // ════════════════════════════════════════════════════════════════════
    [MenuItem("Tools/ARRISE/Pre-Build Check")]
    public static void RunManually()
    {
        PreBuildSummary s;
        var issues = Collect(out s);
        PreBuildDialog.Show(issues, s, false);
    }

    // ════════════════════════════════════════════════════════════════════
    //  THE BUILD BUTTON
    //
    //  Two doors lead into a build, so both are covered:
    //
    //   1. Build / Build And Run in the Build Settings window. Unity hands that
    //      click to whatever RegisterBuildPlayerHandler installed, BEFORE any
    //      build work starts — the right moment for a window that can say no.
    //   2. Anything else (Build Profiles, a menu item, a script). Those never
    //      reach the handler, so OnPreprocessBuild opens the same window once
    //      the build is already under way and aborts it if you cancel.
    //
    //  handledAt stops the window appearing twice when door 1 was used.
    // ════════════════════════════════════════════════════════════════════
    private static double handledAt = -999;

    [InitializeOnLoadMethod]
    private static void InstallBuildButtonHandler()
    {
        BuildPlayerWindow.RegisterBuildPlayerHandler(options =>
        {
            PreBuildSummary s;
            var issues = Collect(out s);
            if (!PreBuildDialog.Show(issues, s, true))
            {
                Debug.Log("[Pre-Flight] Build cancelled — nothing was built.");
                return;
            }
            handledAt = EditorApplication.timeSinceStartup;
            BuildPlayerWindow.DefaultBuildMethods.BuildPlayer(options);
        });
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (Application.isBatchMode) return;             // CI: no window, PostProcessScene still guards
        if (EditorApplication.timeSinceStartup - handledAt < 30) return;   // already asked at the button

        PreBuildSummary s;
        var issues = Collect(out s);
        if (!PreBuildDialog.Show(issues, s, true))
            throw new BuildFailedException("Build cancelled in the ARRISE pre-flight check.");
    }

    // ════════════════════════════════════════════════════════════════════
    //  AUTHORITATIVE NET — runs with the real boot scene loaded
    // ════════════════════════════════════════════════════════════════════
    private static bool checkedThisBuild;

    [PostProcessScene(-100)]
    public static void OnPostProcessScene()
    {
        if (!BuildPipeline.isBuildingPlayer) return;    // also fires entering Play mode
        if (checkedThisBuild) return;                   // only the boot scene matters
        checkedThisBuild = true;
        EditorApplication.update += ResetGuard;

        var issues = new List<PreBuildIssue>();
        var s = new PreBuildSummary();
        CheckScene(issues, s);

        var errors = issues.Where(i => i.level == PreBuildLevel.Error).ToList();
        if (errors.Count > 0)
            throw new BuildFailedException("[Pre-Flight] the scene being built has " + errors.Count +
                " blocking problem(s):\n\n" + string.Join("\n", errors.Select(e => "  ✗ " + e.what + "\n    → " + e.fix)));
    }

    private static void ResetGuard()
    {
        if (BuildPipeline.isBuildingPlayer) return;
        checkedThisBuild = false;
        EditorApplication.update -= ResetGuard;
    }

    // ════════════════════════════════════════════════════════════════════
    //  RULES
    // ════════════════════════════════════════════════════════════════════
    public static List<PreBuildIssue> Collect(out PreBuildSummary summary)
    {
        var issues = new List<PreBuildIssue>();
        summary = new PreBuildSummary();

        CheckProject(issues, summary);

        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        summary.scenePath = string.IsNullOrEmpty(active.path) ? "(unsaved scene)" : active.path;

        string boot = EditorBuildSettings.scenes.Where(x => x.enabled).Select(x => x.path).FirstOrDefault();
        if (boot != null && boot != active.path)
        {
            string bootPath = boot;
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "The open scene is not the one this build boots.",
                "The build would ship " + bootPath + ", but the checks below can only read the scene that is open.")
                .WithFix("Open boot scene", () =>
                {
                    if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        EditorSceneManager.OpenScene(bootPath, OpenSceneMode.Single);
                }));
        }

        CheckScene(issues, summary);
        return issues;
    }

    private static void CheckProject(List<PreBuildIssue> issues, PreBuildSummary s)
    {
        s.buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
        s.template = PlayerSettings.WebGL.template;

        // 1. WebGL + the AR template. The jslib bridge calls window.iTracker, which
        //    only exists in these templates: any other one boots to a black screen.
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Active build target is " + s.buildTarget + ", not WebGL.",
                "File ▸ Build Settings ▸ WebGL ▸ Switch Platform."));

        if (s.template != "PROJECT:iTracker" && s.template != "PROJECT:iTracker6")
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "WebGL template is '" + s.template + "'.",
                "Player Settings ▸ Resolution and Presentation ▸ WebGL Template ▸ iTracker. " +
                "Any other template has no arcamera.js / itracker.js, so nothing tracks."));
        else
            CheckTemplateFolder(s.template.Replace("PROJECT:", ""), issues);

        // 2. Exactly one enabled scene: the first is what boots, and both build hooks
        //    read their answers from it.
        var enabled = EditorBuildSettings.scenes.Where(x => x.enabled).ToList();
        if (enabled.Count == 0)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "No scene is enabled in Build Settings.",
                "Tick exactly one scene — the campaign you mean to ship."));
        else if (enabled.Count > 1)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                enabled.Count + " scenes are enabled; the build boots '" + enabled[0].path + "'.",
                "Untick the rest unless you really ship several scenes."));
        foreach (var sc in enabled)
            if (!File.Exists(sc.path))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                    "Build Settings lists a scene that no longer exists: " + sc.path,
                    "Remove it from File ▸ Build Settings."));

        // 3. The global registry PostProcessBuild exports every <imagetarget> from.
        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        if (gs == null || gs.imageTargetInfos == null || gs.imageTargetInfos.Count == 0)
        {
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "ImageTrackerGlobalSettings has no image targets.",
                "Re-run the scene builder for this campaign, or register the targets manually."));
            return;
        }
        s.registeredCount = gs.imageTargetInfos.Count;

        var seenId = new HashSet<string>();
        var byFile = new Dictionary<string, string>();
        foreach (var info in gs.imageTargetInfos)
        {
            if (string.IsNullOrEmpty(info.id))
            { issues.Add(new PreBuildIssue(PreBuildLevel.Error, "A global image target has an empty id.",
                "Fix it in Assets/Imagine/ImageTracker/Resources/ImageTrackerGlobalSettings.asset.")); continue; }

            if (!seenId.Add(info.id))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Duplicate global target id '" + info.id + "'.",
                    "Two entries share one id: the tracker keeps only one. Remove the extra."));

            if (info.texture == null)
            { issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Global target '" + info.id + "' has no texture.",
                "Assign its image, or delete the entry — it exports a broken <imagetarget> tag.")); continue; }

            string path = AssetDatabase.GetAssetPath(info.texture);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            { issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Global target '" + info.id + "' points at a missing file.",
                "Re-assign its image asset.")); continue; }

            // PostProcessBuild copies each image into targets/ BY ITS FILENAME, so two
            // different images with the same filename silently overwrite each other.
            string file = Path.GetFileName(path);
            if (byFile.ContainsKey(file))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                    "Targets '" + byFile[file] + "' and '" + info.id + "' both use a file named '" + file + "'.",
                    "Rename one image file: they are copied into targets/ by filename and one would overwrite the other."));
            else
                byFile[file] = info.id;
        }
    }

    private static void CheckTemplateFolder(string name, List<PreBuildIssue> issues)
    {
        string dir = "Assets/WebGLTemplates/" + name;
        string index = dir + "/index.html";
        if (!File.Exists(index))
        { issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Template " + name + " has no index.html.",
            "Restore the template folder.")); return; }

        string html = File.ReadAllText(index);
        if (!html.Contains("<!--IMAGETARGETS-->"))
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Template " + name + " is missing the <!--IMAGETARGETS--> marker.",
                "PostProcessBuild injects every <imagetarget> tag there; without it nothing is tracked."));
        if (!File.Exists(dir + "/itracker.js") || !File.Exists(dir + "/arcamera.js"))
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Template " + name + " is missing itracker.js / arcamera.js.",
                "Restore them from the Imagine WebAR package."));
        if (!File.Exists(dir + "/hunt-overlay.js"))
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Template " + name + " has no hunt-overlay.js.",
                "Only matters for hunt campaigns, but the <script> tag would 404 on every build."));
        else if (!Regex.IsMatch(html, @"window\.HUNT_CONFIG\s*=\s*\{\s*enabled:"))
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Template " + name + " has no window.HUNT_CONFIG line.",
                "HuntFlagPostBuild needs it to switch the hunt on; without it every build ships with the hunt OFF."));
        if (!html.Contains("ar-analytics.js"))
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Template " + name + " has no analytics <script> tag.",
                "AnalyticsKeyPostBuild has nothing to write the project key into."));
    }

    private static void CheckScene(List<PreBuildIssue> issues, PreBuildSummary s)
    {
#if UNITY_2023_1_OR_NEWER
        var tracker = Object.FindFirstObjectByType<ImageTracker>(FindObjectsInactive.Include);
#else
        var tracker = Object.FindObjectOfType<ImageTracker>(true);
#endif
        if (tracker == null)
        {
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "No ImageTracker in this scene.",
                "Nothing can be tracked. Re-run the campaign's scene builder."));
            return;
        }
        if (tracker.transform.parent != null)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "ImageTracker '" + tracker.name + "' is not a root GameObject.",
                "The browser sends tracking messages to it by name — it must sit at the scene root."));

        var gs = Resources.Load<ImageTrackerGlobalSettings>("ImageTrackerGlobalSettings");
        var registered = new HashSet<string>();
        if (gs != null && gs.imageTargetInfos != null)
            foreach (var i in gs.imageTargetInfos)
                if (!string.IsNullOrEmpty(i.id)) registered.Add(i.id);

        var so = new SerializedObject(tracker);
        var targets = so.FindProperty("imageTargets");
        var sceneIds = new List<string>();

        if (targets == null || targets.arraySize == 0)
        {
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "The ImageTracker has no image targets.",
                "Re-run the campaign's scene builder."));
        }
        else
        {
            for (int i = 0; i < targets.arraySize; i++)
            {
                var e = targets.GetArrayElementAtIndex(i);
                string id = e.FindPropertyRelative("id").stringValue;
                var tf = e.FindPropertyRelative("transform").objectReferenceValue as Transform;

                if (string.IsNullOrEmpty(id))
                { issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Image target #" + i + " has an empty id.",
                    "Give it the id its image is registered under.")); continue; }

                if (sceneIds.Contains(id))
                    issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Image target id '" + id + "' appears twice in this scene.",
                        "Ids are the tracker's keys — only one target per id can ever fire."));
                sceneIds.Add(id);

                if (!IdRe.IsMatch(id))
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Target id '" + id + "' has characters outside A-Z a-z 0-9 _ -",
                        "The hunt server drops ids like this, so its scans would never count. Rename it."));
                if (tf == null)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Target '" + id + "' has no Transform assigned.",
                        "Its content can never be shown. Re-run the scene builder."));
                if (!registered.Contains(id))
                    issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Target '" + id + "' is NOT in ImageTrackerGlobalSettings.",
                        "No <imagetarget> tag is exported for it, so the camera can never recognise it. " +
                        "Re-run the scene builder, or register the image manually."));
            }
        }
        s.targetCount = sceneIds.Count;

        // Extra registered targets cost real time: the browser downloads and
        // feature-extracts EVERY one at page load, tracked or not.
        var unused = registered.Where(r => !sceneIds.Contains(r)).ToList();
        s.unusedCount = unused.Count;
        if (unused.Count > 0 && gs != null)
        {
            var keep = new HashSet<string>(sceneIds);
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                unused.Count + " image target(s) are registered globally but unused by this scene.",
                "Every one is downloaded and feature-extracted at page load, slowing the first impression. " +
                "Trimming edits a SHARED asset — restore it afterwards with: git checkout -- " +
                "Assets/Imagine/ImageTracker/Resources/ImageTrackerGlobalSettings.asset")
                .WithFix("Trim to this scene", () =>
                {
                    gs.imageTargetInfos = gs.imageTargetInfos.Where(i => keep.Contains(i.id)).ToList();
                    EditorUtility.SetDirty(gs);
                    AssetDatabase.SaveAssets();
                    Debug.Log("[Pre-Flight] Trimmed global targets to: " + string.Join(", ", keep) +
                              "\nRestore the full list after this build: git checkout -- " +
                              AssetDatabase.GetAssetPath(gs));
                }));
        }

        // Campaign declaration: what HuntFlagPostBuild and AnalyticsKeyPostBuild read.
        var campaigns = Object.FindObjectsOfType<CampaignSettings>(true);
        s.hasCampaign = campaigns.Length > 0;
        if (campaigns.Length == 0)
        {
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "This scene has no CampaignSettings component.",
                "The build then ships with the hunt OFF and the template's default analytics key.")
                .WithFix("Add component", () =>
                {
                    var go = new GameObject("Campaign Settings");
                    go.AddComponent<CampaignSettings>();
                    Undo.RegisterCreatedObjectUndo(go, "Add Campaign Settings");
                    EditorSceneManager.MarkSceneDirty(go.scene);
                    Selection.activeGameObject = go;
                }));
        }
        else
        {
            if (campaigns.Length > 1)
                issues.Add(new PreBuildIssue(PreBuildLevel.Warn, campaigns.Length + " CampaignSettings components in this scene.",
                    "Only the first is read. Delete the extras so the answer is unambiguous."));

            var c = campaigns[0];
            s.campaignName = string.IsNullOrEmpty(c.campaignName) ? "(unnamed)" : c.campaignName;
            s.huntEnabled = c.huntEnabled;
            string key = (c.analyticsApiKey ?? "").Trim();
            s.analyticsKey = key;

            if (key.Length == 0)
                issues.Add(new PreBuildIssue(PreBuildLevel.Info,
                    "No analytics key on this scene — the build ships with analytics disabled.",
                    "Intentional? Fine. Otherwise paste the project key from Admin ▸ Projects ▸ API Key."));
            else if (!KeyRe.IsMatch(key))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                    "The analytics key is not 64 hex characters (" + key.Length + " chars).",
                    "It looks truncated or mis-pasted — copy it again from Admin ▸ Projects ▸ API Key."));

            if (c.huntEnabled)
            {
                string manifest = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "hunt-posters.json");
                if (!File.Exists(manifest))
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Hunt build, but hunt-posters.json was never exported.",
                        "Tools ▸ Meme Hunt ▸ 3. Copy Poster List JSON, then paste it into hunt/admin.html ▸ Settings ▸ " +
                        "Poster list — otherwise the server scores against its own built-in ids."));
                else
                {
                    string json = File.ReadAllText(manifest);
                    foreach (string id in sceneIds)
                        if (json.IndexOf("\"" + id + "\"", System.StringComparison.Ordinal) < 0)
                            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                                "Poster '" + id + "' is in the scene but not in hunt-posters.json.",
                                "The export is stale — re-export it and re-paste it into the dashboard."));
                }
            }
        }

        // Content that silently fails in a browser.
        foreach (var vp in Object.FindObjectsOfType<VideoPlayer>(true))
        {
            if (vp.source != VideoSource.Url) continue;
            string url = vp.url ?? "";
            var cdn = vp.GetComponent<CDNARVideoController>();
            if (cdn != null && !string.IsNullOrEmpty(cdn.cdnVideoUrl)) url = cdn.cdnVideoUrl;

            string where = "'" + vp.gameObject.name + "'";
            if (string.IsNullOrEmpty(url.Trim()))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Video player " + where + " has no URL.",
                    "Set its CDN url, or delete the object — it shows a blank plane."));
            else if (url.StartsWith("http://"))
                issues.Add(new PreBuildIssue(PreBuildLevel.Error, "Video " + where + " uses http://",
                    "The page is https, so the browser blocks it as mixed content. Use https://."));
            else if (url.Trim() != url)
                issues.Add(new PreBuildIssue(PreBuildLevel.Warn, "Video URL on " + where + " has leading/trailing spaces.",
                    "Some CDNs 404 on the encoded space. Trim it."));
        }
    }
}
