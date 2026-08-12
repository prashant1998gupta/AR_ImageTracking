using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The pre-flight window: what you see the moment you press Build.
///
/// It answers, in one glance, the three questions that have gone wrong silently
/// before — WHICH campaign is shipping, is the hunt on or off, and whose
/// analytics project it reports into — then lists everything PreBuildCheck found,
/// each with the one action that fixes it.
///
/// Modal on purpose: the build waits for an answer, so a red banner cannot be
/// scrolled past in the Console after the fact. Errors disable the Build button;
/// warnings never block, they just have to be seen.
///
/// Applying a fix during a build CANCELS that build: assets edited halfway
/// through would be picked up inconsistently. The fix is applied, and you press
/// Build again — which re-runs this window against the fixed project.
/// </summary>
public class PreBuildDialog : EditorWindow
{
    private const float W = 620f, H = 660f;

    private static readonly Color BG        = new Color(0.086f, 0.086f, 0.094f);
    private static readonly Color PANEL     = new Color(0.137f, 0.137f, 0.149f);
    private static readonly Color RED       = new Color(0.863f, 0.118f, 0.118f);
    private static readonly Color AMBER     = new Color(0.910f, 0.765f, 0.408f);
    private static readonly Color GREEN     = new Color(0.373f, 0.816f, 0.416f);
    private static readonly Color DIM       = new Color(1f, 1f, 1f, 0.45f);

    private List<PreBuildIssue> issues;
    private PreBuildSummary sum;
    private bool duringBuild;
    private bool proceed;
    private bool fixApplied;
    private Vector2 scroll;

    private GUIStyle hTitle, hSub, chip, issueTitle, issueFix, metaKey, metaVal, banner;

    /// <returns>true = let the build continue.</returns>
    public static bool Show(List<PreBuildIssue> issues, PreBuildSummary summary, bool duringBuild)
    {
        issues = issues ?? new List<PreBuildIssue>();
        summary = summary ?? new PreBuildSummary();

        try
        {
            var w = CreateInstance<PreBuildDialog>();
            w.issues = issues;
            w.sum = summary;
            w.duringBuild = duringBuild;
            w.proceed = false;
            w.titleContent = new GUIContent("ARRISE Pre-Flight");
            w.minSize = w.maxSize = new Vector2(W, H);
            w.position = new Rect(
                (Screen.currentResolution.width - W) * 0.5f,
                (Screen.currentResolution.height - H) * 0.5f, W, H);
            w.ShowModalUtility();       // blocks until the window closes
            return w.proceed;
        }
        catch (System.Exception e)
        {
            // Some Unity versions refuse a custom modal window once a build is
            // already running. Never let that swallow the report: fall back to the
            // native dialog, which always works.
            Debug.LogWarning("[Pre-Flight] Custom window unavailable (" + e.GetType().Name + ") — using the plain dialog.");
            return NativeFallback(issues, summary, duringBuild);
        }
    }

    private static bool NativeFallback(List<PreBuildIssue> issues, PreBuildSummary s, bool duringBuild)
    {
        int errors = issues.Count(i => i.level == PreBuildLevel.Error);
        int warns  = issues.Count(i => i.level == PreBuildLevel.Warn);

        var sb = new System.Text.StringBuilder();
        sb.Append(s.campaignName).Append("   ·   ").Append(System.IO.Path.GetFileName(s.scenePath)).Append('\n');
        sb.Append("Meme Hunt: ").Append(s.huntEnabled ? "ON" : "off")
          .Append("    Analytics: ").Append(string.IsNullOrEmpty(s.analyticsKey) ? "disabled"
              : s.analyticsKey.Substring(0, System.Math.Min(8, s.analyticsKey.Length)) + "…")
          .Append("    Targets: ").Append(s.targetCount).Append('\n');
        sb.Append(errors > 0 ? "\n" + errors + " problem(s) must be fixed:\n"
                : warns > 0 ? "\nReady to build. " + warns + " warning(s):\n"
                : "\nEverything checks out.\n");

        foreach (var i in issues.OrderBy(i => (int)i.level).Take(8))
            sb.Append('\n').Append(i.level == PreBuildLevel.Error ? "✗ " : i.level == PreBuildLevel.Warn ? "▲ " : "· ")
              .Append(i.what).Append("\n    → ").Append(i.fix).Append('\n');

        if (!duringBuild) { EditorUtility.DisplayDialog("ARRISE Pre-Flight", sb.ToString(), "Close"); return false; }
        if (errors > 0) { EditorUtility.DisplayDialog("ARRISE Pre-Flight — build blocked", sb.ToString(), "Cancel build"); return false; }
        return EditorUtility.DisplayDialog("ARRISE Pre-Flight", sb.ToString(), "Build", "Cancel build");
    }

    private int Count(PreBuildLevel l) { return issues.Count(i => i.level == l); }

    private void BuildStyles()
    {
        if (hTitle != null) return;

        hTitle = new GUIStyle(EditorStyles.label) {
            fontSize = 19, fontStyle = FontStyle.Bold, richText = true,
            normal = { textColor = Color.white } };
        hSub = new GUIStyle(EditorStyles.label) {
            fontSize = 10, normal = { textColor = DIM } };
        banner = new GUIStyle(EditorStyles.label) {
            fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white } };
        chip = new GUIStyle(EditorStyles.miniLabel) {
            fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white } };
        issueTitle = new GUIStyle(EditorStyles.label) {
            fontSize = 12, wordWrap = true, normal = { textColor = Color.white } };
        issueFix = new GUIStyle(EditorStyles.label) {
            fontSize = 11, wordWrap = true, normal = { textColor = DIM } };
        metaKey = new GUIStyle(EditorStyles.miniLabel) {
            fontSize = 9, normal = { textColor = DIM } };
        metaVal = new GUIStyle(EditorStyles.label) {
            fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
    }

    private void OnGUI()
    {
        BuildStyles();
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BG);

        int errors = Count(PreBuildLevel.Error);
        int warns  = Count(PreBuildLevel.Warn);

        DrawHeader();
        DrawBanner(errors, warns);
        DrawSummaryGrid();
        DrawIssues();
        DrawFooter(errors);
    }

    // ─── header ─────────────────────────────────────────────────────────
    private void DrawHeader()
    {
        var r = new Rect(0, 0, position.width, 58);
        EditorGUI.DrawRect(r, PANEL);
        EditorGUI.DrawRect(new Rect(0, 0, 4, 58), RED);

        GUI.Label(new Rect(20, 10, 400, 26), "AR<color=#dc1e1e>RISE</color>  Pre-Flight", hTitle);
        GUI.Label(new Rect(21, 34, 560, 14),
            duringBuild ? "Checks run before this build ships" : "Review only — nothing is being built", hSub);
    }

    // ─── status banner ──────────────────────────────────────────────────
    private void DrawBanner(int errors, int warns)
    {
        var r = new Rect(16, 70, position.width - 32, 42);
        Color c = errors > 0 ? RED : (warns > 0 ? AMBER : GREEN);
        EditorGUI.DrawRect(r, new Color(c.r, c.g, c.b, 0.15f));
        EditorGUI.DrawRect(new Rect(r.x, r.y, 3, r.height), c);

        string msg = errors > 0
            ? "✗  " + errors + " problem" + (errors == 1 ? "" : "s") + " must be fixed before this build ships"
            : warns > 0
                ? "▲  Ready to build — " + warns + " warning" + (warns == 1 ? "" : "s") + " worth a look"
                : "✓  Everything checks out — safe to build";

        var s = new GUIStyle(banner); s.normal.textColor = c;
        GUI.Label(r, msg, s);
    }

    // ─── what is actually shipping ──────────────────────────────────────
    private void DrawSummaryGrid()
    {
        var r = new Rect(16, 122, position.width - 32, 96);
        EditorGUI.DrawRect(r, PANEL);

        float colW = r.width / 3f;
        Cell(new Rect(r.x + 14, r.y + 10, colW - 20, 34), "CAMPAIGN",
             string.IsNullOrEmpty(sum.campaignName) ? "—" : sum.campaignName);
        Cell(new Rect(r.x + 14 + colW, r.y + 10, colW - 20, 34), "MEME HUNT",
             sum.hasCampaign ? (sum.huntEnabled ? "ON" : "off") : "off (undeclared)",
             sum.huntEnabled ? GREEN : DIM);
        Cell(new Rect(r.x + 14 + colW * 2, r.y + 10, colW - 20, 34), "ANALYTICS",
             string.IsNullOrEmpty(sum.analyticsKey) ? "disabled"
                 : sum.analyticsKey.Substring(0, System.Math.Min(8, sum.analyticsKey.Length)) + "…",
             string.IsNullOrEmpty(sum.analyticsKey) ? DIM : GREEN);

        Cell(new Rect(r.x + 14, r.y + 52, colW - 20, 34), "SCENE", System.IO.Path.GetFileName(sum.scenePath));
        Cell(new Rect(r.x + 14 + colW, r.y + 52, colW - 20, 34), "TARGETS",
             sum.targetCount + " tracked" + (sum.unusedCount > 0 ? "  (+" + sum.unusedCount + " unused)" : ""),
             sum.unusedCount > 0 ? AMBER : Color.white);
        Cell(new Rect(r.x + 14 + colW * 2, r.y + 52, colW - 20, 34), "TEMPLATE",
             sum.template.Replace("PROJECT:", ""));
    }

    private void Cell(Rect r, string key, string val) { Cell(r, key, val, Color.white); }
    private void Cell(Rect r, string key, string val, Color valColor)
    {
        GUI.Label(new Rect(r.x, r.y, r.width, 12), key, metaKey);
        var s = new GUIStyle(metaVal); s.normal.textColor = valColor;
        GUI.Label(new Rect(r.x, r.y + 13, r.width, 18), val, s);
    }

    // ─── the findings ───────────────────────────────────────────────────
    private void DrawIssues()
    {
        var area = new Rect(16, 228, position.width - 32, position.height - 228 - 66);
        if (issues.Count == 0)
        {
            GUI.Label(new Rect(area.x, area.y + 30, area.width, 40),
                "Nothing to report. Campaign declaration, image targets, template and\ncontent URLs all look right.",
                new GUIStyle(issueFix) { alignment = TextAnchor.UpperCenter });
            return;
        }

        var ordered = issues.OrderBy(i => (int)i.level).ToList();
        float y = 0, rowW = area.width - 16;
        var content = new Rect(0, 0, rowW, MeasureAll(ordered, rowW));
        scroll = GUI.BeginScrollView(area, scroll, content);

        foreach (var i in ordered)
        {
            float h = RowHeight(i, rowW);
            var row = new Rect(0, y, rowW, h - 6);
            EditorGUI.DrawRect(row, PANEL);
            Color c = i.level == PreBuildLevel.Error ? RED : i.level == PreBuildLevel.Warn ? AMBER : DIM;
            EditorGUI.DrawRect(new Rect(row.x, row.y, 3, row.height), c);

            var badge = new Rect(row.x + 12, row.y + 10, 52, 15);
            EditorGUI.DrawRect(badge, c);
            GUI.Label(badge, i.level == PreBuildLevel.Error ? "ERROR"
                           : i.level == PreBuildLevel.Warn ? "WARNING" : "NOTE", chip);

            float textX = row.x + 74, textW = row.width - 74 - 12;
            float th = issueTitle.CalcHeight(new GUIContent(i.what), textW);
            GUI.Label(new Rect(textX, row.y + 8, textW, th), i.what, issueTitle);
            float fh = issueFix.CalcHeight(new GUIContent("→  " + i.fix), textW);
            GUI.Label(new Rect(textX, row.y + 10 + th, textW, fh), "→  " + i.fix, issueFix);

            if (i.fixAction != null)
            {
                var b = new Rect(textX, row.y + 14 + th + fh, 150, 20);
                if (GUI.Button(b, i.fixLabel))
                {
                    i.fixAction();
                    fixApplied = true;
                    if (duringBuild)
                    {
                        proceed = false;
                        Close();   // assets changed mid-build: stop and let them press Build again
                        return;
                    }
                    RefreshAfterFix();
                    return;
                }
            }
            y += h;
        }
        GUI.EndScrollView();
    }

    private float MeasureAll(List<PreBuildIssue> list, float w)
    {
        float t = 0;
        foreach (var i in list) t += RowHeight(i, w);
        return t;
    }

    private float RowHeight(PreBuildIssue i, float w)
    {
        float textW = w - 74 - 12;
        float h = 8
                + issueTitle.CalcHeight(new GUIContent(i.what), textW)
                + 2 + issueFix.CalcHeight(new GUIContent("→  " + i.fix), textW)
                + 10;
        if (i.fixAction != null) h += 26;
        return h + 6;
    }

    private void RefreshAfterFix()
    {
        PreBuildSummary s;
        issues = PreBuildCheck.Collect(out s);
        sum = s;
        Repaint();
    }

    // ─── footer ─────────────────────────────────────────────────────────
    private void DrawFooter(int errors)
    {
        var r = new Rect(0, position.height - 58, position.width, 58);
        EditorGUI.DrawRect(r, PANEL);

        if (fixApplied)
            GUI.Label(new Rect(20, r.y + 8, position.width - 40, 14),
                "A fix was applied — press Build again to re-check.", hSub);

        if (!duringBuild)
        {
            if (GUI.Button(new Rect(position.width - 130, r.y + 16, 110, 26), "Close")) { proceed = false; Close(); }
            if (GUI.Button(new Rect(position.width - 260, r.y + 16, 120, 26), "Re-check")) RefreshAfterFix();
            return;
        }

        if (GUI.Button(new Rect(20, r.y + 16, 130, 26), "Cancel Build")) { proceed = false; Close(); }

        bool blocked = errors > 0;
        using (new EditorGUI.DisabledScope(blocked))
        {
            var bt = new Rect(position.width - 190, r.y + 16, 170, 26);
            var old = GUI.backgroundColor;
            GUI.backgroundColor = blocked ? Color.gray : GREEN;
            if (GUI.Button(bt, blocked ? "Fix errors to build" : "Build  ▸")) { proceed = true; Close(); }
            GUI.backgroundColor = old;
        }
    }

    private void OnDestroy()
    {
        // Closing with the X during a build means "don't ship this".
        if (duringBuild && !proceed) proceed = false;
    }
}
