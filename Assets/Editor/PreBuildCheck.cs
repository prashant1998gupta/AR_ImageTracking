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
        CheckPlayerSettings(issues);

        var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (active.isDirty)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "This scene has unsaved changes.",
                "A build reads the scene FILE, not what is open — every edit since the last save would be missing " +
                "from the build while looking correct in the editor.")
                .WithFix("Save scene", () => EditorSceneManager.SaveScene(active)));

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

    /// <summary>
    /// Player/build settings that decide whether the build LOADS at all on a shared
    /// host, and how long a visitor stares at a progress bar before the camera opens.
    /// None of these break in the editor, so none of them are ever noticed here.
    /// </summary>
    private static void CheckPlayerSettings(List<PreBuildIssue> issues)
    {
        // A development build ships the profiler, no code stripping and full symbols:
        // several times the download, on a phone, over event Wi-Fi.
        if (EditorUserBuildSettings.development)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "Development Build is ticked.",
                "It ships a much larger, slower build with the profiler attached. Untick it for anything a visitor touches.")
                .WithFix("Turn off", () => { EditorUserBuildSettings.development = false; }));

        // Compressed builds need the server to send Content-Encoding. Shared hosting
        // usually does not for .br/.gz, and the page then hangs on "Loading..." forever.
        // The fallback loader decompresses in JS instead: slightly slower, always works.
        if (PlayerSettings.WebGL.compressionFormat != WebGLCompressionFormat.Disabled &&
            !PlayerSettings.WebGL.decompressionFallback)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "Compression is " + PlayerSettings.WebGL.compressionFormat + " but Decompression Fallback is off.",
                "On Hostinger-style shared hosting the browser never gets Content-Encoding and the build hangs at " +
                "'Loading...'. Turning the fallback on makes the loader decompress it itself.")
                .WithFix("Enable fallback", () => { PlayerSettings.WebGL.decompressionFallback = true; }));

        // Every repeat scan re-downloads tens of MB without this — at an event most
        // visitors open the same build more than once.
        if (!PlayerSettings.WebGL.dataCaching)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Data Caching is off.",
                "The whole build is re-downloaded on every visit. With it on, the second scan starts almost instantly.")
                .WithFix("Enable caching", () => { PlayerSettings.WebGL.dataCaching = true; }));

        // Full exception support adds megabytes and slows every call — it exists for
        // debugging, not for a poster someone scans once.
        if (PlayerSettings.WebGL.exceptionSupport == WebGLExceptionSupport.FullWithStacktrace)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Exception support is Full With Stacktrace.",
                "Biggest, slowest option. 'Explicitly Thrown Exceptions Only' keeps real errors and drops the weight.")
                .WithFix("Use explicit only", () =>
                    { PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly; }));

        // Linear colour on a WebGL AR build washes out the camera feed and chroma-key
        // video on some phones; every campaign here is authored in Gamma.
        if (PlayerSettings.colorSpace == ColorSpace.Linear)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Colour space is Linear.",
                "The camera feed and green-screen video are authored for Gamma — Linear can wash them out on mobile GPUs."));

        // Threads need the server to send COOP/COEP headers. Shared hosting does not,
        // and without cross-origin isolation the build refuses to start at all.
        if (PlayerSettings.WebGL.threadsSupport)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "WebGL multithreading is on.",
                "It needs Cross-Origin-Opener/Embedder-Policy headers, which shared hosting does not send — the build " +
                "then fails to start. Turn it off unless you control the server headers.")
                .WithFix("Turn off threads", () => { PlayerSettings.WebGL.threadsSupport = false; }));

        // Dead engine code is pure download weight on a phone.
        if (!PlayerSettings.stripEngineCode)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Strip Engine Code is off.",
                "Engine modules this campaign never touches are shipped anyway — usually several MB.")
                .WithFix("Enable stripping", () => { PlayerSettings.stripEngineCode = true; }));

        try
        {
            var lvl = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.WebGL);
            if (lvl == ManagedStrippingLevel.Disabled || lvl == ManagedStrippingLevel.Low)
                issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                    "Managed stripping level is " + lvl + ".",
                    "These scenes are small and use no reflection — Medium trims a lot of unused .NET code with no risk.")
                    .WithFix("Set Medium", () =>
                        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Medium)));
        }
        catch { /* API differs across versions — never block a build over it */ }

        // Mobile GPUs render the camera feed every frame already; these are pure cost
        // in a scene that is a few unlit quads.
        if (QualitySettings.shadows != ShadowQuality.Disable)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Realtime shadows are enabled (" + QualitySettings.shadows + ").",
                "Nothing in an AR overlay casts a shadow worth the GPU time on a phone.")
                .WithFix("Disable shadows", () => { QualitySettings.shadows = ShadowQuality.Disable; }));

        if (QualitySettings.antiAliasing > 0)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Anti-aliasing is " + QualitySettings.antiAliasing + "×.",
                "MSAA costs real fill-rate on mobile; AR content is video-on-a-quad, which gains almost nothing.")
                .WithFix("Turn off AA", () => { QualitySettings.antiAliasing = 0; }));

        // Everything under a Resources folder ships whether or not it is used.
        long resKb = 0;
        foreach (var dir in Directory.GetDirectories("Assets", "Resources", SearchOption.AllDirectories))
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta")) resKb += new FileInfo(f).Length / 1024;
        if (resKb > 3000)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Resources folders hold " + (resKb / 1024f).ToString("0.0") + " MB.",
                "EVERYTHING under a Resources folder is built in, used or not. Move anything this campaign does not " +
                "load by name out of Resources."));
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

        CheckOverlaySync(name, dir, html, issues);
    }

    /// <summary>
    /// hunt-overlay.js lives in three places (both templates and the deployed build)
    /// and is cache-busted by hand with ?v=N. Both of those have already gone wrong:
    /// the copies drifted apart, and an edited overlay shipped under an old ?v=,
    /// so phones kept serving the previous file from cache.
    /// </summary>
    private static void CheckOverlaySync(string name, string dir, string html, List<PreBuildIssue> issues)
    {
        string mine = dir + "/hunt-overlay.js";
        if (!File.Exists(mine)) return;

        // 1. The two templates must ship the same overlay.
        string other = "Assets/WebGLTemplates/" + (name == "iTracker" ? "iTracker6" : "iTracker") + "/hunt-overlay.js";
        if (File.Exists(other) && Hash(mine) != Hash(other))
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "iTracker and iTracker6 have DIFFERENT hunt-overlay.js files.",
                "Whichever template you build decides which behaviour ships. Copy the newer file over the other one.")
                .WithFix("Copy mine to the other", () =>
                {
                    File.Copy(mine, other, true);
                    AssetDatabase.Refresh();
                    Debug.Log("[Pre-Flight] Copied " + mine + " → " + other);
                }));

        // 2. If the overlay changed but ?v= did not, phones keep the cached copy.
        var m = Regex.Match(html, @"hunt-overlay\.js\?v=(\d+)");
        if (!m.Success)
        {
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "Template " + name + " loads hunt-overlay.js with no ?v= cache-buster.",
                "Phones will keep serving an old copy after you update it. Use <script src=\"hunt-overlay.js?v=1\">."));
            return;
        }

        string version = m.Groups[1].Value;
        string key = "ARRISE.overlay." + name + "." + Application.dataPath.GetHashCode() + ".v" + version;
        string hash = Hash(mine);
        string seen = EditorPrefs.GetString(key, "");

        if (seen == "")
            EditorPrefs.SetString(key, hash);      // first build of this version
        else if (seen != hash)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "hunt-overlay.js changed but it is still ?v=" + version + ".",
                "Anyone who already opened this build keeps the OLD overlay from cache — the change would look like " +
                "it never shipped. Bump it to ?v=" + (int.Parse(version) + 1) + " in the template (and in the uploaded build's index.html).")
                .WithFix("Bump to v" + (int.Parse(version) + 1), () =>
                {
                    string index = dir + "/index.html";
                    string next = "hunt-overlay.js?v=" + (int.Parse(version) + 1);
                    File.WriteAllText(index, Regex.Replace(File.ReadAllText(index), @"hunt-overlay\.js\?v=\d+", next));
                    EditorPrefs.SetString("ARRISE.overlay." + name + "." + Application.dataPath.GetHashCode() +
                                          ".v" + (int.Parse(version) + 1), hash);
                    AssetDatabase.Refresh();
                    Debug.Log("[Pre-Flight] " + name + " now loads " + next);
                }));
    }

    private static string Hash(string path)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        using (var fs = File.OpenRead(path))
            return System.BitConverter.ToString(md5.ComputeHash(fs));
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

        // The camera feed itself. ImageTracker finds it at runtime, so a scene with
        // no ARCamera compiles, builds and opens to a black screen.
#if UNITY_2023_1_OR_NEWER
        var arCam = Object.FindFirstObjectByType<ARCamera>(FindObjectsInactive.Include);
#else
        var arCam = Object.FindObjectOfType<ARCamera>(true);
#endif
        if (arCam == null)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error, "No ARCamera in this scene.",
                "Nothing draws the camera feed — the build opens to a black screen. " +
                "Imagine WebAR ▸ Create ▸ AR Camera, or re-run the scene builder."));

        // ── AR-specific waste, all invisible in the editor ──────────────
        if (arCam != null)
        {
            var cam = arCam.GetComponent<Camera>();
            if (cam != null)
            {
                // The webcam feed is drawn UNDER the Unity canvas: a skybox paints over it.
                if (cam.clearFlags == CameraClearFlags.Skybox)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                        "The AR camera clears to Skybox.",
                        "That paints over the webcam feed — the visitor sees a sky instead of the room. " +
                        "Clear to a Solid Color with alpha 0.")
                        .WithFix("Use transparent", () =>
                        {
                            Undo.RecordObject(cam, "AR camera clear flags");
                            cam.clearFlags = CameraClearFlags.SolidColor;
                            cam.backgroundColor = new Color(0, 0, 0, 0);
                            EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
                        }));

                if (cam.allowHDR || cam.allowMSAA)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                        "The AR camera has " + (cam.allowHDR ? "HDR " : "") + (cam.allowMSAA ? "MSAA" : "") + " enabled.",
                        "Extra full-screen buffers on a phone, for content that is unlit video quads.")
                        .WithFix("Turn both off", () =>
                        {
                            Undo.RecordObject(cam, "AR camera buffers");
                            cam.allowHDR = false; cam.allowMSAA = false;
                            EditorSceneManager.MarkSceneDirty(cam.gameObject.scene);
                        }));
            }
        }

        if (RenderSettings.skybox != null)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                "A skybox material is assigned to this scene.",
                "It is never visible behind a camera feed, but its cubemap is still built and loaded.")
                .WithFix("Remove skybox", () =>
                {
                    RenderSettings.skybox = null;
                    EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                }));

        if (LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                LightmapSettings.lightmaps.Length + " baked lightmap(s) are stored in this scene.",
                "Unlit AR quads never sample them — it is pure download weight. Window ▸ Rendering ▸ Lighting ▸ " +
                "Generate Lighting ▸ Clear Baked Data, and untick Auto Generate."));

        int listeners = Object.FindObjectsOfType<AudioListener>(true).Length;
        if (listeners > 1)
            issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                listeners + " AudioListeners in this scene.",
                "Unity keeps one and logs a warning every frame in the browser console; audio can also go silent."));

        // A video shipped INSIDE the build instead of streamed from the CDN is the
        // single biggest download mistake available here.
        foreach (var vp in Object.FindObjectsOfType<VideoPlayer>(true))
        {
            if (vp.clip == null) continue;
            string cp = AssetDatabase.GetAssetPath(vp.clip);
            long mb = (!string.IsNullOrEmpty(cp) && File.Exists(cp)) ? new FileInfo(cp).Length / 1048576 : 0;
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                "'" + vp.gameObject.name + "' has a VideoClip baked into the build" + (mb > 0 ? " (" + mb + " MB)" : "") + ".",
                "Every visitor downloads it before the experience starts. Put the file on the CDN and set the URL on " +
                "CDNARVideoController instead — that is what streaming is for."));
        }

        // Read/Write doubles a texture's memory. Tracking images never need it: the
        // build copies the ORIGINAL file into targets/ and the browser decodes that.
        if (gs != null && gs.imageTargetInfos != null)
        {
            var rw = new List<string>();
            foreach (var info in gs.imageTargetInfos)
            {
                if (info.texture == null) continue;
                var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(info.texture)) as TextureImporter;
                if (imp != null && imp.isReadable) rw.Add(info.id);
            }
            if (rw.Count > 0)
                issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                    rw.Count + " target texture(s) have Read/Write enabled: " + string.Join(", ", rw.Take(5)) +
                    (rw.Count > 5 ? " …" : ""),
                    "That keeps a second uncompressed copy in memory for nothing — the tracker reads the original " +
                    "file from targets/, not the imported texture.")
                    .WithFix("Turn Read/Write off", () =>
                    {
                        foreach (var info in gs.imageTargetInfos)
                        {
                            if (info.texture == null) continue;
                            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(info.texture)) as TextureImporter;
                            if (imp != null && imp.isReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
                        }
                    }));
        }

        // 60 fps doubles the tracking work and drains a phone that is already running
        // a camera; the tracker's own default is 30.
        var fps = so.FindProperty("trackerSettings.targetFrameRate");
        if (fps != null && fps.intValue == -1)
            issues.Add(new PreBuildIssue(PreBuildLevel.Info,
                "Tracker frame rate is set to 60 FPS.",
                "30 FPS tracks just as well for video-on-poster content and roughly halves CPU and battery use."));

        // Broken references survive in a scene file and only fail at runtime.
        var missingScripts = new List<string>();
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
        {
            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
                if (comps[i] == null) { missingScripts.Add(t.name); break; }
        }
        if (missingScripts.Count > 0)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                missingScripts.Count + " object(s) have a MISSING script: " +
                string.Join(", ", missingScripts.Take(5)) + (missingScripts.Count > 5 ? " …" : ""),
                "A deleted or renamed script leaves a null component behind. Whatever it did — a button, a video, a " +
                "tracker hook — silently does nothing in the build. Remove it or restore the script."));

        var pinkMaterials = new List<string>();
        foreach (var rend in Object.FindObjectsOfType<Renderer>(true))
            foreach (var mat in rend.sharedMaterials)
                if (mat == null || mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                { pinkMaterials.Add(rend.gameObject.name); break; }
        if (pinkMaterials.Count > 0)
            issues.Add(new PreBuildIssue(PreBuildLevel.Error,
                pinkMaterials.Count + " renderer(s) have a missing material or shader: " +
                string.Join(", ", pinkMaterials.Take(5)) + (pinkMaterials.Count > 5 ? " …" : ""),
                "These draw as solid magenta in the build. Re-assign the material, or re-run the scene builder."));

        // Every registered target image is DOWNLOADED and feature-extracted before the
        // camera opens, so their combined weight is the visitor's wait.
        if (gs != null && gs.imageTargetInfos != null)
        {
            long total = 0;
            foreach (var info in gs.imageTargetInfos)
            {
                if (info.texture == null) continue;
                string p = AssetDatabase.GetAssetPath(info.texture);
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;

                long kb = new FileInfo(p).Length / 1024;
                total += kb;

                if (kb > 1500)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                        "Target image '" + info.id + "' is " + (kb / 1024f).ToString("0.0") + " MB.",
                        "It is downloaded before tracking can start. Re-export it around 1000-1600px wide as a " +
                        "quality JPG — tracking uses features, not pixels, and 200-400 KB is plenty."));

                int w = info.texture.width, h = info.texture.height;
                if (w > 0 && System.Math.Max(w, h) < 500)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                        "Target image '" + info.id + "' is only " + w + "×" + h + ".",
                        "Too few pixels to extract stable features — expect slow, jittery tracking. Use ~1000-1600px."));
                else if (System.Math.Max(w, h) > 4096)
                    issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                        "Target image '" + info.id + "' is " + w + "×" + h + ".",
                        "Oversized images cost seconds of feature extraction on a phone with no tracking benefit. " +
                        "Downscale to ~1600px."));
            }
            if (total > 0)
                issues.Add(new PreBuildIssue(total > 6000 ? PreBuildLevel.Warn : PreBuildLevel.Info,
                    "Target images add up to " + (total / 1024f).ToString("0.0") + " MB, downloaded before the camera opens.",
                    total > 6000
                        ? "On event Wi-Fi that is a long stare at a progress bar. Trim unused targets and re-export the heavy ones."
                        : "Fine — just so you know what the visitor waits for."));
        }

        // The iOS sound-unlock is keyed by target: a mismatched key means the first
        // tap unlocks audio for a target that is not playing.
        foreach (var cdn in Object.FindObjectsOfType<CDNARVideoController>(true))
        {
            string k = (cdn.webGLSoundTargetKey ?? "").Trim();
            if (k.Length == 0) continue;                       // empty = derived at runtime, fine
            var parent = cdn.transform.parent;
            if (parent != null && sceneIds.Contains(parent.name) && k != parent.name)
                issues.Add(new PreBuildIssue(PreBuildLevel.Warn,
                    "'" + cdn.gameObject.name + "' has sound key '" + k + "' but sits under target '" + parent.name + "'.",
                    "On iPhone the tap-for-sound unlock is stored per target key — a mismatch can leave that video muted."));
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
