using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reads the boot scene's CampaignSettings values straight out of the .unity file
/// on disk, without needing the scene to be open or any build callback to fire.
///
/// WHY THIS EXISTS
/// ---------------
/// HuntFlagPostBuild and AnalyticsKeyPostBuild used to learn what the scene
/// declared from [PostProcessScene], which runs with the scene loaded. That
/// callback is NOT guaranteed to run: Unity skips scene processing when an
/// incremental build reuses cached scene data. When it is skipped, the statics
/// those callbacks fill stay null, and the post-build step falls back to its
/// "nothing declared" default — which wrote
///
///     window.HUNT_CONFIG = { enabled: false };
///
/// into a Meme Hunt build. The result ships with no registration gate, no chips,
/// no timer and no scoring, and looks like a perfectly healthy AR build, so the
/// failure is invisible until players cannot register. Worse, it does not
/// self-correct: once the scene stops being reprocessed, EVERY later build is
/// wrong. That happened for six consecutive builds (Editor.log shows
/// "scene: &lt;unknown&gt;" on each) during a live event.
///
/// Unity builds the player from the scene FILES, so reading the file is reading
/// exactly what was built. That makes this source authoritative and, unlike a
/// callback, it cannot silently not happen.
///
/// Requires text-serialized scenes (Edit ▸ Project Settings ▸ Editor ▸ Asset
/// Serialization ▸ Force Text). This project is set that way; if it ever is not,
/// Found comes back false and the callers say so loudly instead of guessing.
/// </summary>
public static class BootSceneCampaign
{
    public struct Result
    {
        /// <summary>True when a CampaignSettings block was located in the scene file.</summary>
        public bool Found;
        public bool HuntEnabled;
        public string ApiKey;
        public string CampaignName;
        /// <summary>Boot scene path, or null when it could not be determined.</summary>
        public string ScenePath;
        /// <summary>Non-null when the scene could not be read at all (not merely absent settings).</summary>
        public string Error;
        /// <summary>How many CampaignSettings components the scene file contains.</summary>
        public int Count;
    }

    // A scene file is a stream of YAML documents separated by lines starting "--- ".
    private static readonly Regex DocSplitRe = new Regex(@"(?m)^--- ");
    private static readonly Regex HuntRe     = new Regex(@"(?m)^\s*huntEnabled:\s*(\d+)\s*$");
    private static readonly Regex KeyRe      = new Regex(@"(?m)^\s*analyticsApiKey:\s*(.*)$");
    private static readonly Regex NameRe     = new Regex(@"(?m)^\s*campaignName:\s*(.*)$");

    /// <summary>The scene Unity boots: the first ENABLED entry in Build Settings.</summary>
    public static string BootScenePath()
    {
        foreach (var s in EditorBuildSettings.scenes)
            if (s.enabled && !string.IsNullOrEmpty(s.path))
                return s.path;
        return null;
    }

    public static Result ReadFromDisk()
    {
        var r = new Result();

        r.ScenePath = BootScenePath();
        if (string.IsNullOrEmpty(r.ScenePath))
        {
            r.Error = "Build Settings has no enabled scene, so there is no boot scene to read.";
            return r;
        }

        if (!File.Exists(r.ScenePath))
        {
            r.Error = "Boot scene file not found on disk: " + r.ScenePath;
            return r;
        }

        string text;
        try { text = File.ReadAllText(r.ScenePath); }
        catch (System.Exception e) { r.Error = "Could not read " + r.ScenePath + " — " + e.Message; return r; }

        var parsed = ParseSceneText(text, CampaignSettingsScriptGuid());
        parsed.ScenePath = r.ScenePath;
        if (!string.IsNullOrEmpty(parsed.Error))
            parsed.Error = r.ScenePath + " " + parsed.Error;
        return parsed;
    }

    /// <summary>
    /// The pure half: given the text of a .unity file and the CampaignSettings script
    /// GUID, pull out what the scene declares. No Unity APIs are touched, so this can
    /// be exercised directly against real scene files.
    /// </summary>
    public static Result ParseSceneText(string text, string guid)
    {
        var r = new Result();

        // A binary-serialized scene is unreadable here; say so rather than guess.
        if (string.IsNullOrEmpty(text) ||
            text.IndexOf("MonoBehaviour", System.StringComparison.Ordinal) < 0)
        {
            r.Error = "is not text-serialized (Project Settings ▸ Editor ▸ Asset Serialization " +
                      "must be Force Text), so its CampaignSettings could not be read.";
            return r;
        }

        foreach (var doc in DocSplitRe.Split(text))
        {
            // Match on the script GUID; fall back to the class identifier Unity also
            // writes, so a re-imported script with a new GUID still resolves.
            bool isCampaign =
                (!string.IsNullOrEmpty(guid) &&
                 doc.IndexOf("guid: " + guid, System.StringComparison.Ordinal) >= 0) ||
                doc.IndexOf("::CampaignSettings", System.StringComparison.Ordinal) >= 0;
            if (!isCampaign) continue;

            var hunt = HuntRe.Match(doc);
            if (!hunt.Success) continue;        // not actually a CampaignSettings body

            r.Count++;
            if (r.Found) continue;              // keep the first, mirroring the old behaviour

            r.Found        = true;
            r.HuntEnabled  = hunt.Groups[1].Value != "0";
            r.ApiKey       = Unquote(KeyRe.Match(doc));
            r.CampaignName = Unquote(NameRe.Match(doc));
        }

        return r;
    }

    private static string CampaignSettingsScriptGuid()
    {
        foreach (var g in AssetDatabase.FindAssets("CampaignSettings t:MonoScript"))
        {
            var p = AssetDatabase.GUIDToAssetPath(g);
            if (Path.GetFileNameWithoutExtension(p) == "CampaignSettings") return g;
        }
        return null;
    }

    /// <summary>YAML scalars may be quoted; the API key never is, but names are.</summary>
    private static string Unquote(Match m)
    {
        if (!m.Success) return "";
        var v = m.Groups[1].Value.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            v = v.Substring(1, v.Length - 2).Replace("\\\"", "\"");
        return v;
    }
}
