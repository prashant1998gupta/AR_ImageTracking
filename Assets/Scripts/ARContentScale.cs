using UnityEngine;

/// <summary>
/// Live-tunable scale for a tracked target's AR content.
///
/// WHY THIS EXISTS
/// Getting "the AR matches the printed card" right is a judgement call that can
/// only be made on a phone, against the real card, in real light. Baking the
/// number into the scene meant every guess cost a Unity rebuild AND an upload —
/// minutes per attempt, so it got guessed rather than measured.
///
/// This component moves that number out of the build:
///
///     https://your-host/rishabh/?scale=0.55
///
/// Open the AR page with ?scale=… , point at the card, see the result instantly.
/// Try 0.55, 0.60, 0.63 in a few seconds each, decide, THEN bake the winner into
/// RishabhSceneBuilder's ContentScale and rebuild once.
///
/// WHERE IT LIVES
/// On a "Content" object BETWEEN the tracker's target root and the visible
/// content, never on the root itself: ImageTracker.ParseData hard-assigns the
/// root's localScale every tracked frame, so anything set there is wiped.
///
///     Rishabh_Card        ← target root, scale owned by the tracker
///     └── Content         ← THIS, scale owned by us
///         ├── … vid       ← video plane
///         └── World Canvas← the surround
///
/// With no ?scale= in the URL the serialized `scale` is used, so a shipped build
/// behaves exactly as authored.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("ARRISE/AR Content Scale")]
public class ARContentScale : MonoBehaviour
{
    [Tooltip("Baked default. 1.0 = content exactly fills the tracking quad. " +
             "Overridden at runtime by ?scale=… in the page URL.")]
    public float scale = 1f;

    [Tooltip("Query-string keys accepted, in order of preference.")]
    public string[] urlKeys = { "scale", "ar_scale" };

    [Tooltip("Guard rails for the URL override — a typo like ?scale=100 should " +
             "not fling the content into the far distance.")]
    public float minScale = 0.05f;
    public float maxScale = 5f;

    void Start()
    {
        float s = scale;
        float fromUrl;
        if (TryReadUrlScale(out fromUrl))
        {
            s = Mathf.Clamp(fromUrl, minScale, maxScale);
            Debug.Log("[ARContentScale] URL override: scale=" + s + " (baked default was " + scale + ")");
        }
        if (s <= 0f) { s = 1f; }
        transform.localScale = Vector3.one * s;
    }

    /// <summary>Reads ?scale= from the page URL. Application.absoluteURL carries the
    /// full URL (query string included) in a WebGL build, so no JS plugin is needed;
    /// in the Editor it is empty and the baked value is used.</summary>
    private bool TryReadUrlScale(out float value)
    {
        value = 0f;
        string url = Application.absoluteURL;
        if (string.IsNullOrEmpty(url)) { return false; }

        int q = url.IndexOf('?');
        if (q < 0 || q == url.Length - 1) { return false; }
        string query = url.Substring(q + 1);

        int hash = query.IndexOf('#');
        if (hash >= 0) { query = query.Substring(0, hash); }

        foreach (string pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) { continue; }
            string key = pair.Substring(0, eq);
            string raw = pair.Substring(eq + 1);
            foreach (string wanted in urlKeys)
            {
                if (!string.Equals(key, wanted, System.StringComparison.OrdinalIgnoreCase)) { continue; }
                // InvariantCulture: the page may be viewed on a comma-decimal locale,
                // but the URL always spells the number with a dot.
                if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
