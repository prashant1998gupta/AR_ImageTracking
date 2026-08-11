using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

/// <summary>
/// Writes the Meme Hunt opt-in flag into the built index.html, reading the answer
/// from the SCENE ITSELF (its CampaignSettings component) rather than from any
/// list of scene names kept here.
///
/// Why the flag exists: the WebGL template ships
///
///     window.HUNT_CONFIG = { enabled: false };
///
/// and hunt-overlay.js does nothing unless that says true — no HUD, no
/// registration gate, no network. That is what keeps the Meme Hunt out of the
/// visiting-card and product builds which share the template (and share one
/// browser localStorage, where a leftover hunt_token used to light the HUD up on
/// every campaign).
///
/// Why it is decided here: setting it by hand is a footgun both ways — forget to
/// turn it ON and the hunt ships with no registration or scoring; forget to turn
/// it OFF and a client sees someone else's game HUD over their card.
///
/// How a scene declares itself:
///     add a CampaignSettings component and tick "huntEnabled".
/// Add a new hunt campaign, rename a scene, move a folder — nothing here changes.
/// A scene with no CampaignSettings is treated as a plain AR experience.
/// </summary>
public static class HuntFlagPostBuild
{
    /// <summary>Set by OnPostProcessScene for the FIRST scene of the build — the one
    /// Unity boots — then consumed and cleared by OnPostProcessBuild.</summary>
    private static bool? bootSceneIsHunt;
    private static string bootSceneName;

    // The exact statement the templates ship. Group 1 is everything up to the bool.
    private static readonly Regex FlagRe =
        new Regex(@"(window\.HUNT_CONFIG\s*=\s*\{\s*enabled:\s*)(true|false)", RegexOptions.IgnoreCase);

    /// <summary>Runs once per scene while the player is being built, with that scene
    /// loaded — the only place the scene's own components can be inspected.</summary>
    [PostProcessScene(0)]
    public static void OnPostProcessScene()
    {
        // This callback also fires on every Play-mode entry; ignore those.
        if (!BuildPipeline.isBuildingPlayer) return;
        // Only the first scene processed matters: that is the one the build boots.
        if (bootSceneIsHunt.HasValue) return;

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bootSceneName = scene.path;

        var all = Object.FindObjectsOfType<CampaignSettings>(true);
        bootSceneIsHunt = all.Length > 0 && all[0].huntEnabled;

        if (all.Length > 1)
            Debug.LogWarning("[HuntFlag] " + all.Length + " CampaignSettings components in " +
                             scene.name + " — using the first (huntEnabled=" + bootSceneIsHunt + ").");
    }

    [PostProcessBuild(100)]
    public static void OnPostProcessBuild(BuildTarget target, string buildPath)
    {
        bool isHunt   = bootSceneIsHunt ?? false;
        bool declared = bootSceneIsHunt.HasValue;
        string scene  = string.IsNullOrEmpty(bootSceneName) ? "<unknown>" : bootSceneName;
        bootSceneIsHunt = null;                     // reset for the next build
        bootSceneName   = null;

        if (target != BuildTarget.WebGL) return;

        string indexPath = Path.Combine(buildPath, "index.html");
        if (!File.Exists(indexPath))
        {
            Debug.LogWarning("[HuntFlag] No index.html at " + indexPath + " — flag not set.");
            return;
        }

        string html = File.ReadAllText(indexPath);
        if (!FlagRe.IsMatch(html))
        {
            // No line means the overlay defaults to OFF: harmless for a card build,
            // broken for the hunt. Only shout when it actually breaks something.
            if (isHunt)
                Debug.LogError(
                    "[HuntFlag] This scene declares huntEnabled, but index.html has no " +
                    "'window.HUNT_CONFIG = { enabled: ... }' line, so the hunt will NOT run " +
                    "(no registration, no chips, no scoring). Add it to the WebGL template " +
                    "just above the hunt-overlay.js <script> tag.");
            else
                Debug.Log("[HuntFlag] No HUNT_CONFIG line in index.html; overlay stays off " +
                          "(correct for a non-hunt build).");
            return;
        }

        string patched = FlagRe.Replace(html, "${1}" + (isHunt ? "true" : "false"), 1);
        if (patched != html) File.WriteAllText(indexPath, patched);

        Debug.Log(string.Format(
            "[HuntFlag] {0} → Meme Hunt {1}\n            scene: {2}{3}",
            Path.GetFileName(buildPath.TrimEnd('/', '\\')),
            isHunt ? "ENABLED — registration, chips, timer, leaderboard"
                   : "disabled — plain AR experience",
            scene,
            declared ? "" : "   (no CampaignSettings component found — defaulted to off)"));
    }
}
