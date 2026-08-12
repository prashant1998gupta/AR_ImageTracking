using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

/// <summary>
/// Writes the analytics project API key into the built index.html, reading it from
/// the SCENE ITSELF (its CampaignSettings component) — the same pattern as
/// HuntFlagPostBuild, for the same reason: the decision travels with the scene
/// instead of being remembered by whoever presses Build.
///
/// Why it exists: the WebGL template ships ONE hard-coded data-project key, so
/// every campaign built from it reported into the same analytics project — a
/// client's card, the Meme Hunt and a product demo all mixed into one funnel.
///
/// How a scene declares its key:
///     CampaignSettings ▸ analyticsApiKey  =  the key from
///     dashboard.rionick.com/admin/ ▸ Projects ▸ (project) ▸ API Key
///
/// Outcomes, all logged at build time:
///   key set            → data-project is rewritten to that key
///   key EMPTY          → the tracker &lt;script&gt; tag is REMOVED (no analytics,
///                        no request to the dashboard at all)
///   no CampaignSettings→ the tag is left exactly as the template ships it
///                        (backwards compatible with every older scene)
///
/// Nothing here breaks when analytics is absent: Analytics.jslib guards on
/// window.arAnalytics, and hunt-overlay.js installs a no-op shim for it before
/// its own opt-in gate, for every build.
/// </summary>
public static class AnalyticsKeyPostBuild
{
    /// <summary>Captured from the FIRST scene of the build — the one Unity boots —
    /// then consumed and cleared by OnPostProcessBuild.</summary>
    private static string bootSceneKey;      // null = scene had no CampaignSettings
    private static bool bootSceneDeclared;
    private static string bootSceneName;

    // The tracker tag the templates ship. Group 1 = everything before the key,
    // group 2 = the key, group 3 = everything after it.
    private static readonly Regex TrackerTagRe = new Regex(
        @"(<script\b[^>]*\bsrc\s*=\s*""[^""]*ar-analytics\.js""[^>]*\bdata-project\s*=\s*"")([^""]*)(""[^>]*>\s*</script>)",
        RegexOptions.IgnoreCase);

    // Whole-line match, used when the key is empty and the tag must go away.
    private static readonly Regex TrackerLineRe = new Regex(
        @"[ \t]*<script\b[^>]*\bsrc\s*=\s*""[^""]*ar-analytics\.js""[^>]*>\s*</script>[ \t]*\r?\n?",
        RegexOptions.IgnoreCase);

    // Auth::generateApiKey() = bin2hex(random_bytes(32)) → 64 hex chars.
    private static readonly Regex KeyShapeRe = new Regex(@"^[0-9a-fA-F]{64}$");

    [PostProcessScene(0)]
    public static void OnPostProcessScene()
    {
        if (!BuildPipeline.isBuildingPlayer) return;   // also fires on Play mode
        if (bootSceneName != null) return;             // only the boot scene matters

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bootSceneName = scene.path;

        var all = Object.FindObjectsOfType<CampaignSettings>(true);
        bootSceneDeclared = all.Length > 0;
        bootSceneKey = bootSceneDeclared ? (all[0].analyticsApiKey ?? "").Trim() : null;
    }

    [PostProcessBuild(101)]   // after HuntFlagPostBuild (100); order is irrelevant, both patch different lines
    public static void OnPostProcessBuild(BuildTarget target, string buildPath)
    {
        string key = bootSceneKey;
        bool declared = bootSceneDeclared;
        string scene = string.IsNullOrEmpty(bootSceneName) ? "<unknown>" : bootSceneName;
        bootSceneKey = null;
        bootSceneDeclared = false;
        bootSceneName = null;

        if (target != BuildTarget.WebGL) return;

        // Same failure as HuntFlagPostBuild: [PostProcessScene] does not always run,
        // and when it does not, every build silently shipped the template's own
        // hard-coded data-project key — so a client's build reported into the wrong
        // analytics project. Read the boot scene off disk in that case.
        if (!declared)
        {
            var disk = BootSceneCampaign.ReadFromDisk();
            if (!string.IsNullOrEmpty(disk.Error))
            {
                Debug.LogError("[Analytics] Could not determine this build's analytics key. " +
                               disk.Error + "\n            The tracker tag was left as the template " +
                               "ships it, so this build may report into the wrong project.");
            }
            else if (disk.Found)
            {
                key      = (disk.ApiKey ?? "").Trim();
                declared = true;
                scene    = disk.ScenePath + " (read from disk — PostProcessScene did not run)";
            }
            else if (!string.IsNullOrEmpty(disk.ScenePath))
            {
                scene = disk.ScenePath;
            }
        }

        string indexPath = Path.Combine(buildPath, "index.html");
        if (!File.Exists(indexPath))
        {
            Debug.LogWarning("[Analytics] No index.html at " + indexPath + " — key not set.");
            return;
        }

        string buildName = Path.GetFileName(buildPath.TrimEnd('/', '\\'));
        string html = File.ReadAllText(indexPath);

        if (!declared)
        {
            Debug.Log("[Analytics] " + buildName + " → tracker left as the template ships it.\n" +
                      "            scene: " + scene + " has no CampaignSettings, so no per-campaign key was applied.\n" +
                      "            Add one (Add Component ▸ ARRISE ▸ Campaign Settings) to give this build its own analytics project.");
            return;
        }

        if (!TrackerTagRe.IsMatch(html) && !TrackerLineRe.IsMatch(html))
        {
            if (!string.IsNullOrEmpty(key))
                Debug.LogError("[Analytics] This scene declares an API key, but index.html has no " +
                               "ar-analytics.js <script> tag — nothing was tracked. Add the tag to the " +
                               "WebGL template, or clear analyticsApiKey to silence this.");
            return;
        }

        if (string.IsNullOrEmpty(key))
        {
            string stripped = TrackerLineRe.Replace(html, "", 1);
            if (stripped != html) File.WriteAllText(indexPath, stripped);
            Debug.Log("[Analytics] " + buildName + " → analytics DISABLED (tracker tag removed).\n" +
                      "            scene: " + scene + " — CampaignSettings.analyticsApiKey is empty.");
            return;
        }

        if (!KeyShapeRe.IsMatch(key))
            Debug.LogWarning("[Analytics] The key on " + scene + " is not 64 hex characters — the dashboard " +
                             "issues keys in that shape, so this one may be truncated or mis-pasted. " +
                             "Writing it anyway; if the project's stats stay at zero, re-copy the key.");

        string patched = TrackerTagRe.Replace(html, "${1}" + key.Replace("$", "$$") + "${3}", 1);
        if (patched != html) File.WriteAllText(indexPath, patched);

        Debug.Log("[Analytics] " + buildName + " → project key " + key.Substring(0, 8) + "… applied.\n" +
                  "            scene: " + scene);
    }
}
