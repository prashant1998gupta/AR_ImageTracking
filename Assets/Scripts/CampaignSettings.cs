using UnityEngine;

/// <summary>
/// Per-scene campaign declaration. Drop ONE of these on any object in an AR scene
/// (the scene builders add it automatically) and the scene owns the answer to
/// "what kind of campaign am I?" — no central list of scene names anywhere.
///
/// At build time HuntFlagPostBuild reads this from the scene Unity is about to
/// boot and writes the matching flag into the built index.html, which is what
/// hunt-overlay.js checks. So:
///
///   huntEnabled = true   →  window.HUNT_CONFIG = { enabled: true }
///                           registration gate, poster chips, timer, leaderboard
///   huntEnabled = false  →  window.HUNT_CONFIG = { enabled: false }
///                           plain AR experience (visiting cards, posters, demos)
///
/// A scene with no CampaignSettings behaves as false, which is the safe default:
/// a client's card build can never accidentally show someone else's game HUD.
///
/// This is a plain data holder — it has no Update, costs nothing at runtime, and
/// exists only so the decision travels WITH the scene instead of being remembered
/// by whoever presses Build.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("ARRISE/Campaign Settings")]
public class CampaignSettings : MonoBehaviour
{
    [Tooltip("Tick this ONLY for a hunt-style campaign (Meme Hunt): the player must " +
             "register first, then scans several posters against a timer and a " +
             "leaderboard. Leave unticked for a normal AR experience — a visiting " +
             "card, a poster, a product demo — where the AR content is the whole thing.")]
    public bool huntEnabled = false;

    [Tooltip("Free text, for humans reading the scene. Not used by the build.")]
    public string campaignName = "";

    [Space]
    [Tooltip("Analytics project API key for THIS campaign — copy it from the admin " +
             "panel (Projects ▸ the project ▸ API Key). At build time " +
             "AnalyticsKeyPostBuild writes it into the built index.html, so every " +
             "campaign reports into its own project instead of sharing one.\n\n" +
             "Leave EMPTY to ship with no analytics at all: the tracker script tag " +
             "is removed from the build.")]
    public string analyticsApiKey = "";
}
