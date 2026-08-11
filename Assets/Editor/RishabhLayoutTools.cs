using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Imagine.WebAR;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Rishabh AR Visiting-Card Layout Tools — Rionick Studios
///
/// The card is ARRANGED BY HAND in Rishabh.unity, which creates two problems that
/// this file solves and nothing else does:
///
///   1. Hand-arranging fixes ONE tracked target. The other face of the printed card
///      is a separate, identical subtree, and it does not move.
///        → Tools ▸ Rishabh ▸ Sync UI To All Targets
///          copies the first target's UI onto every other target.
///
///   2. RishabhSceneBuilder DELETES and regenerates the scene, so the next rebuild
///      wipes the hand work.
///        → Tools ▸ Rishabh ▸ Read Layout From Scene (write into builder CONFIG)
///          reads the arranged layout back into the builder's own Layout table, so
///          it survives every future rebuild.
///
/// THE MATH (the exact inverse of what RishabhSceneBuilder writes)
///     pxPerUnit = 1 / CanvasScaleConst          (the World Canvas' localScale)
///     width     = sizeDelta.x / pxPerUnit
///     aspect    = sizeDelta.y / sizeDelta.x
///     dx        = anchoredPosition.x / pxPerUnit
///     dy        = anchoredPosition.y / pxPerUnit
///     ContentScale = Content.localScale.x
/// The scene does NOT carry url, vCard or worldZ, so those are MERGED through from
/// the existing table — never invented, never dropped.
///
/// WHAT EACH COMMAND REFUSES TO TOUCH
///   Sync   never touches the video object, VideoPlayer, CDNARVideoController (its
///          webGLSoundTargetKey is per-target and must stay that way), materials,
///          meshes, or the Canvas / CanvasScaler / GraphicRaycaster components. It
///          never writes the scene to disk — look first, then Ctrl+S or Ctrl+Z.
///   Read   never touches the scene at all. It rewrites RishabhSceneBuilder.cs only
///          between its AUTO markers, after taking a timestamped .bak, and aborts
///          without writing a byte if anything at all does not line up.
/// </summary>
public static class RishabhLayoutTools
{
    // ═══════════════════════════════════════════════════════════════════════
    //  ██  C O N F I G  ██
    // ═══════════════════════════════════════════════════════════════════════

    private const string Title    = "Rishabh Layout Tools";
    private const string UndoName = "Sync Rishabh UI To All Targets";

    private static readonly string ScenePath        = "Assets/Scenes_1/Rishabh.unity";
    private static readonly string BuilderAssetPath = "Assets/Editor/RishabhSceneBuilder.cs";

    // The names RishabhSceneBuilder gives the two objects this tool walks through.
    private static readonly string ContentName = "Content";
    private static readonly string CanvasName  = "World Canvas";

    // Mirrors RishabhSceneBuilder.CanvasScaleConst. It is only a SANITY CHECK: the
    // real pixels-per-unit is read from the World Canvas' own localScale, so the
    // tool stays right even if the builder's constant is retuned.
    private const float ExpectedCanvasScaleConst = 0.00072f;

    // Markers in RishabhSceneBuilder.cs. Matched on this plain-ASCII fragment, not
    // on the full comment, so a mangled box-drawing character cannot hide them.
    private const string LayoutBeginKey = "LAYOUT AUTO-BEGIN";
    private const string LayoutEndKey   = "LAYOUT AUTO-END";
    private const string ScaleMarkerKey = "CONTENTSCALE AUTO-LINE";

    // Backups land OUTSIDE Assets/ (like the builder's vCard) so Unity never
    // imports them and no stray .meta files appear.
    private static readonly string BackupProjectFolder = "Backups";

    // ═══════════════════════════════════════════════════════════════════════
    //  ██  E N D   C O N F I G  ██
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly Regex ContentScaleLine = new Regex(
        @"^(?<pre>\s*private\s+static\s+readonly\s+float\s+ContentScale\s*=\s*)(?<num>[^;]+?)(?<post>\s*;.*)$");

    // ─────────────────────────────────────────────────────────────────
    //  A.  SYNC UI TO ALL TARGETS
    // ─────────────────────────────────────────────────────────────────

    [MenuItem("Tools/Rishabh/Sync UI To All Targets", priority = 1100)]
    public static void SyncUiToAllTargetsMenu()
    {
        try
        {
            if (!CheckNotPlaying()) return;
            Scene scene;
            if (!EnsureSceneOpen(out scene)) return;

            string result = SyncUiToAllTargets(scene);
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog(Title, result, "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog(Title + " — Error", e.Message, "OK");
            Debug.LogException(e);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>Copies the FIRST target's World Canvas layout onto every other
    /// target. One undo step, scene marked dirty, nothing saved.</summary>
    private static string SyncUiToAllTargets(Scene scene)
    {
        var warnings = new List<string>();

        Progress("Reading the tracker's target list", 0.10f);
        var targets = ReadTrackerTargets(scene, warnings);
        if (targets.Count < 2)
            throw new System.Exception(
                "Only " + targets.Count + " tracked target(s) in this scene — there is nothing to sync to.\n\n" +
                "This command copies the FIRST target's UI onto the others.");

        var src = targets[0];
        Transform srcContent = RequireChild(src.root, ContentName, src.id);
        Transform srcCanvas  = RequireChild(srcContent, CanvasName, src.id);

        List<RectTransform> srcChildren;
        Dictionary<string, RectTransform> srcByName;
        CollectUiChildren(srcCanvas, src.id + " (source)", warnings, out srcChildren, out srcByName);
        if (srcChildren.Count == 0)
            throw new System.Exception("The source target '" + src.id + "' has no UI children under " +
                                       ContentName + "/" + CanvasName + " — nothing to copy.");

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);

        var perTarget = new List<string>();
        int syncedTargets = 0;

        for (int ti = 1; ti < targets.Count; ti++)
        {
            var dst = targets[ti];
            Progress("Syncing " + dst.id, 0.10f + 0.80f * ti / targets.Count);

            Transform dstContent = dst.root.Find(ContentName);
            if (dstContent == null)
            {
                warnings.Add(dst.id + ": no \"" + ContentName + "\" child — target SKIPPED entirely");
                continue;
            }
            Transform dstCanvas = dstContent.Find(CanvasName);
            if (dstCanvas == null)
            {
                warnings.Add(dst.id + ": no \"" + ContentName + "/" + CanvasName + "\" — target SKIPPED entirely");
                continue;
            }

            // One hierarchy snapshot per target: this is what makes the sibling-index
            // reordering undoable (child order lives on the PARENT, so a RecordObject
            // on the children alone would not bring it back).
            Undo.RegisterFullObjectHierarchyUndo(dstContent.gameObject, UndoName);

            // Content scale — the "AR is bigger than the card" knob, copied as a pair:
            // the transform is what you see in the viewport, ARContentScale.scale is
            // what actually ships (its Start() assigns localScale at runtime).
            Undo.RecordObject(dstContent, UndoName);
            dstContent.localScale = srcContent.localScale;
            EditorUtility.SetDirty(dstContent);

            var srcScale = srcContent.GetComponent<ARContentScale>();
            var dstScale = dstContent.GetComponent<ARContentScale>();
            if (srcScale != null && dstScale != null)
            {
                Undo.RecordObject(dstScale, UndoName);
                dstScale.scale = srcScale.scale;
                EditorUtility.SetDirty(dstScale);
            }
            else if (srcScale != null)
            {
                warnings.Add(dst.id + ": Content has no ARContentScale, so its scale was copied to the " +
                             "transform only — ?scale=… in the URL will not move this target");
            }
            else if (dstScale != null)
            {
                warnings.Add(dst.id + ": Content has an ARContentScale but the SOURCE does not — " +
                             "its scale (" + dstScale.scale.ToString("0.####", Inv) + ") was left alone");
            }

            // The Canvas itself is deliberately NOT written (see the class comment).
            // A divergent canvas scale would silently change what every copied
            // number means, so say so out loud instead of quietly fixing it.
            var srcCanvasRt = srcCanvas as RectTransform;
            var dstCanvasRt = dstCanvas as RectTransform;
            if (srcCanvasRt != null && dstCanvasRt != null &&
                Mathf.Abs(srcCanvasRt.localScale.x - dstCanvasRt.localScale.x) >
                    0.001f * Mathf.Max(1e-6f, Mathf.Abs(srcCanvasRt.localScale.x)))
            {
                warnings.Add(dst.id + ": its World Canvas localScale is " +
                             dstCanvasRt.localScale.x.ToString("0.######", Inv) + " but the source's is " +
                             srcCanvasRt.localScale.x.ToString("0.######", Inv) +
                             " — the Canvas is never written by this command, so the copied UI will " +
                             "come out a different SIZE on this target. Fix the Canvas by hand.");
            }

            List<RectTransform> dstChildren;
            Dictionary<string, RectTransform> dstByName;
            CollectUiChildren(dstCanvas, dst.id, warnings, out dstChildren, out dstByName);

            int copied = 0;
            var matched = new HashSet<string>();
            foreach (var s in srcChildren)
            {
                RectTransform d;
                if (!dstByName.TryGetValue(s.name, out d))
                {
                    warnings.Add(dst.id + ": MISSING element \"" + s.name +
                                 "\" — present in the source, absent here, so it was NOT synced");
                    continue;
                }
                matched.Add(s.name);
                CopyRect(s, d);
                d.SetSiblingIndex(s.GetSiblingIndex());
                copied++;
            }

            foreach (var d in dstChildren)
            {
                if (matched.Contains(d.name)) continue;
                warnings.Add(dst.id + ": EXTRA element \"" + d.name +
                             "\" — not in the source, so it was left untouched and now sorts last");
            }

            perTarget.Add("  • " + dst.id + ": " + copied + " of " + srcChildren.Count + " elements");
            syncedTargets++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        // Only claim the scene changed if it actually did — a dirty flag over an
        // untouched scene is a lie the user would have to save to clear.
        if (syncedTargets > 0) EditorSceneManager.MarkSceneDirty(scene);
        EditorUtility.ClearProgressBar();

        var sb = new StringBuilder();
        sb.Append("Synced from \"").Append(src.id).Append("\" (the first tracked target).\n\n");
        sb.Append(syncedTargets).Append(" target(s) synced, ")
          .Append(srcChildren.Count).Append(" source elements:\n");
        foreach (var line in perTarget) sb.Append(line).Append('\n');
        sb.Append("\nCopied per element: anchoredPosition3D, sizeDelta, localScale,\n")
          .Append("localRotation, anchorMin/Max, pivot, sibling index.\n")
          .Append("Plus Content.localScale and ARContentScale.scale.\n")
          .Append("Untouched: the video plane, VideoPlayer, CDNARVideoController\n")
          .Append("(its webGLSoundTargetKey stays per-target), materials and the Canvas.\n");

        if (warnings.Count > 0)
        {
            sb.Append("\n⚠ WARNINGS (").Append(warnings.Count).Append(") — read these:\n");
            AppendCapped(sb, warnings, 12, "  ! ");
            Debug.LogWarning("[Rishabh] Sync UI warnings:\n  ! " + string.Join("\n  ! ", warnings));
        }

        if (syncedTargets == 0)
            sb.Append("\nNOTHING was changed and the scene was not marked dirty.");
        else
            sb.Append("\nThe scene is DIRTY but NOT saved — look at it first.\n")
              .Append("Ctrl/Cmd+Z undoes the whole sync in one step.\n\n")
              .Append("When it looks right, save it, then run\n")
              .Append("Tools ▸ Rishabh ▸ Read Layout From Scene so the next rebuild keeps it.");
        return sb.ToString();
    }

    /// <summary>Anchors and pivot FIRST, then size, then position — setting anchors
    /// after positioning silently re-solves anchoredPosition. Same ordering rule the
    /// builder follows when it lays the canvas out.</summary>
    private static void CopyRect(RectTransform s, RectTransform d)
    {
        Undo.RecordObject(d, UndoName);
        d.anchorMin           = s.anchorMin;
        d.anchorMax           = s.anchorMax;
        d.pivot               = s.pivot;
        d.sizeDelta           = s.sizeDelta;
        d.localScale          = s.localScale;
        d.localRotation       = s.localRotation;
        d.anchoredPosition3D  = s.anchoredPosition3D;
        EditorUtility.SetDirty(d);
    }

    // ─────────────────────────────────────────────────────────────────
    //  B.  READ LAYOUT FROM SCENE  →  BUILDER CONFIG
    // ─────────────────────────────────────────────────────────────────

    [MenuItem("Tools/Rishabh/Read Layout From Scene (write into builder CONFIG)", priority = 1101)]
    public static void ReadLayoutFromSceneMenu()
    {
        try
        {
            if (!CheckNotPlaying()) return;
            Scene scene;
            if (!EnsureSceneOpen(out scene)) return;

            string result = ReadLayoutFromScene(scene);
            EditorUtility.ClearProgressBar();
            if (result == null) return;                     // aborted; a dialog was already shown

            EditorUtility.DisplayDialog(Title, result, "OK");
            AssetDatabase.Refresh();                        // last: this triggers the recompile
        }
        catch (System.Exception e)
        {
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog(Title + " — Error", e.Message, "OK");
            Debug.LogException(e);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>Returns the report, or null when the run aborted (having already
    /// explained itself in a dialog and changed NOTHING).</summary>
    private static string ReadLayoutFromScene(Scene scene)
    {
        var notes = new List<string>();

        // ── 1. the scene side ────────────────────────────────────────────────
        Progress("Reading the arranged layout", 0.10f);
        var targets = ReadTrackerTargets(scene, notes);
        if (targets.Count == 0)
            throw new System.Exception("The ImageTracker in this scene has no targets — nothing to read.");

        var src = targets[0];
        Transform content = RequireChild(src.root, ContentName, src.id);
        var canvasRt = RequireChild(content, CanvasName, src.id) as RectTransform;
        if (canvasRt == null)
            throw new System.Exception("\"" + CanvasName + "\" under '" + src.id + "' has no RectTransform.");

        float canvasScale = canvasRt.localScale.x;
        if (!(canvasScale > 0f) || float.IsInfinity(canvasScale))
            throw new System.Exception("The World Canvas localScale.x is " + canvasScale +
                                       " — the layout math cannot be inverted. Nothing was written.");
        if (Mathf.Abs(canvasScale - ExpectedCanvasScaleConst) / ExpectedCanvasScaleConst > 0.001f)
        {
            notes.Add("The World Canvas localScale is " + canvasScale.ToString("0.#######", Inv) +
                      ", not the builder's CanvasScaleConst " +
                      ExpectedCanvasScaleConst.ToString("0.#######", Inv) +
                      ". The numbers below were inverted with the SCENE's value, so a rebuild " +
                      "will only reproduce this layout if you set CanvasScaleConst to match.");
        }
        float pxPerUnit    = 1f / canvasScale;
        float canvasWorldZ = canvasRt.localPosition.z;

        // ContentScale: the transform is what you see, ARContentScale.scale is what
        // ships. If they disagree only a human can say which one is the truth.
        float contentScale = content.localScale.x;
        var contentScaler  = content.GetComponent<ARContentScale>();
        if (contentScaler != null && Mathf.Abs(contentScaler.scale - contentScale) > 0.0005f)
        {
            EditorUtility.ClearProgressBar();
            int choice = EditorUtility.DisplayDialogComplex(Title,
                "The Content object disagrees with itself:\n\n" +
                "    transform localScale = " + contentScale.ToString("0.####", Inv) + "\n" +
                "    ARContentScale.scale = " + contentScaler.scale.ToString("0.####", Inv) + "\n\n" +
                "ARContentScale.Start() assigns its own value at runtime, so THAT is what a " +
                "player sees; the transform is what you see in the Editor.\n\n" +
                "Which one should become the builder's ContentScale?",
                "transform (" + contentScale.ToString("0.####", Inv) + ")",
                "Cancel",
                "ARContentScale (" + contentScaler.scale.ToString("0.####", Inv) + ")");
            if (choice == 1) return null;
            if (choice == 2) contentScale = contentScaler.scale;
            notes.Add("Content transform localScale and ARContentScale.scale disagreed; " +
                      "wrote " + contentScale.ToString("0.####", Inv) + " as you chose. A rebuild re-unifies them.");
        }
        if (!(contentScale > 0f) || float.IsInfinity(contentScale))
            throw new System.Exception("Content.localScale.x is " + contentScale + " — refusing to write that.");

        var sceneEls  = new List<SceneElement>();
        var seenNames = new HashSet<string>();
        var dupNames  = new List<string>();
        for (int i = 0; i < canvasRt.childCount; i++)
        {
            Transform childT = canvasRt.GetChild(i);
            var rt = childT as RectTransform;
            if (rt == null)
                throw new System.Exception("\"" + childT.name + "\" under the World Canvas has no RectTransform. " +
                                           "Nothing was written.");
            if (!seenNames.Add(rt.name)) dupNames.Add(rt.name);

            if (!(rt.sizeDelta.x > 0f))
                throw new System.Exception("\"" + rt.name + "\" has sizeDelta.x = " + rt.sizeDelta.x +
                                           " — width and aspect cannot be derived. Nothing was written.");

            Vector3 ap = rt.anchoredPosition3D;
            float sceneAspect = rt.sizeDelta.y / rt.sizeDelta.x;
            float sceneWidth  = rt.sizeDelta.x / pxPerUnit;

            // WHAT IS ON SCREEN, not what the rect says. Two builder behaviours make
            // those differ once an element has been resized off-aspect by hand:
            //   1. BuildScene's "aspect drift" guard overrides any authored aspect
            //      more than 2 % off the sprite's own and substitutes the sprite's.
            //   2. Image.preserveAspect is on, so the art is FITTED inside the rect —
            //      a rect SHORTER than the sprite's aspect is height-bound and the
            //      picture is drawn narrower than the rect it sits in.
            // Emitting the raw rect would therefore be silently undone by the next
            // rebuild. Emit instead the width that REPRODUCES the drawn picture, and
            // the sprite's own aspect (which the builder is going to force anyway).
            float emitWidth  = sceneWidth;
            float emitAspect = sceneAspect;
            var img = rt.GetComponent<Image>();
            if (img != null && img.sprite != null && sceneAspect > 0f)
            {
                Rect sr = img.sprite.rect;
                float spriteAspect = sr.width > 0f ? sr.height / sr.width : 0f;
                if (spriteAspect > 0f)
                {
                    bool drifted = Mathf.Abs(spriteAspect - sceneAspect) / sceneAspect > 0.02f;
                    emitAspect = spriteAspect;

                    float bind = sceneAspect / spriteAspect;   // < 1 = height-bound
                    if (img.preserveAspect && bind < 1f) { emitWidth = sceneWidth * bind; }

                    if (drifted)
                    {
                        string what = (img.preserveAspect && bind < 1f)
                            ? "it was height-bound, so the picture drew " +
                              ((1f - bind) * 100f).ToString("0.#", Inv) + "% narrower than the rect — " +
                              "width written as " + F(emitWidth) + " so the rebuild reproduces that exact size"
                            : "only the rect was taller than the art, so the drawn size is unchanged — " +
                              "the rebuild just trims the TAP AREA back onto the picture";
                        notes.Add("\"" + rt.name + "\" was resized off its sprite's aspect (rect " +
                                  sceneAspect.ToString("0.####", Inv) + " vs " + img.sprite.name + " " +
                                  spriteAspect.ToString("0.####", Inv) + "): " + what + ".");
                    }
                }
            }

            sceneEls.Add(new SceneElement
            {
                name   = rt.name,
                dx     = ap.x / pxPerUnit,
                dy     = ap.y / pxPerUnit,
                width  = emitWidth,
                aspect = emitAspect,
                localZ = ap.z,
            });
        }
        if (dupNames.Count > 0)
            return Abort("Two elements under the World Canvas share a name:\n\n    " +
                         string.Join(", ", dupNames.ToArray()) +
                         "\n\nThe builder's table is keyed by name, so this cannot be resolved " +
                         "automatically. Rename them in the scene and run this again.\n\nNothing was written.");
        if (sceneEls.Count == 0)
            return Abort("The World Canvas under '" + src.id + "' has no children. Nothing was written.");

        // ── 2. the source side ───────────────────────────────────────────────
        Progress("Parsing the builder's CONFIG table", 0.35f);
        string builderFullPath = ResolveBuilderPath();
        if (builderFullPath == null)
            return Abort("Could not find " + BuilderAssetPath + " on disk. Nothing was written.");

        string original = System.IO.File.ReadAllText(builderFullPath);
        string newline  = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(original.Replace("\r\n", "\n").Split('\n'));

        int beginIdx, endIdx, scaleIdx;
        string markerError;
        if (!FindMarkers(lines, out beginIdx, out endIdx, out scaleIdx, out markerError))
            return Abort(markerError + "\n\nNothing was written. Restore the markers in\n" +
                         BuilderAssetPath + " and run this again:\n\n" +
                         "    // ─── " + LayoutBeginKey + " …\n" +
                         "    …the List<Element> entries…\n" +
                         "    // ─── " + LayoutEndKey + "\n\n" +
                         "and, above the ContentScale line:\n\n" +
                         "    // ─── " + ScaleMarkerKey + " …");

        var region = lines.GetRange(beginIdx + 1, endIdx - beginIdx - 1);
        List<TableEntry> entries;
        string parseError;
        if (!TryParseTable(region, out entries, out parseError))
            return Abort("The existing Layout table could not be parsed:\n\n" + parseError +
                         "\n\nNothing was written. url / vCard / worldZ live ONLY in that table — " +
                         "guessing them is not an option.");
        if (entries.Count == 0)
            return Abort("No `new Element { … }` entries were found between the AUTO markers. Nothing was written.");

        var byName = new Dictionary<string, TableEntry>();
        foreach (var e in entries)
        {
            if (byName.ContainsKey(e.name))
                return Abort("The Layout table has two entries named \"" + e.name +
                             "\". Fix that by hand first. Nothing was written.");
            byName[e.name] = e;
        }

        // ── 3. the two sides must describe the same set ──────────────────────
        var inSceneOnly = new List<string>();
        foreach (var s in sceneEls) if (!byName.ContainsKey(s.name)) inSceneOnly.Add(s.name);
        var inTableOnly = new List<string>();
        foreach (var e in entries) if (!seenNames.Contains(e.name)) inTableOnly.Add(e.name);

        if (inSceneOnly.Count > 0 || inTableOnly.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("The scene and the builder's table do not describe the same elements, " +
                      "so nothing was written.\n");
            if (inSceneOnly.Count > 0)
            {
                sb.Append("\nIn the SCENE but NOT in the table:\n    ")
                  .Append(string.Join(", ", inSceneOnly.ToArray()))
                  .Append("\n  These carry no sprite / url / vCard, so they cannot be written as ")
                  .Append("entries — add them to the Layout table by hand (with their sprite and url) ")
                  .Append("and run this again.");
            }
            if (inTableOnly.Count > 0)
            {
                sb.Append("\n\nIn the TABLE but NOT in the scene:\n    ")
                  .Append(string.Join(", ", inTableOnly.ToArray()))
                  .Append("\n  Deleting a configured element is a decision only you can make — ")
                  .Append("remove those entries by hand, or put the objects back in the scene.");
            }
            return Abort(sb.ToString());
        }

        // ── 4. merge: dx/dy/width/aspect from the scene, everything else carried ─
        Progress("Merging the layout into the CONFIG table", 0.55f);
        var merged   = new List<TableEntry>(sceneEls.Count);
        var report   = new List<string>();
        var zCarried = new List<string>();
        int changed  = 0;
        bool reordered = false;
        for (int i = 0; i < sceneEls.Count; i++)
        {
            var s = sceneEls[i];
            var e = byName[s.name];
            if (entries.IndexOf(e) != i) reordered = true;

            string oldDx = F(e.dx), oldDy = F(e.dy), oldW = F(e.width), oldA = F(e.aspect);

            var m = e.CloneForOutput();
            m.dx = s.dx; m.dy = s.dy; m.width = s.width; m.aspect = s.aspect;
            merged.Add(m);

            string newDx = F(m.dx), newDy = F(m.dy), newW = F(m.width), newA = F(m.aspect);
            bool diff = oldDx != newDx || oldDy != newDy || oldW != newW || oldA != newA;
            if (diff) changed++;
            report.Add("  " + (diff ? "✎" : "•") + " " + Pad(s.name, 14) +
                       " dx " + Pad(Trim(oldDx), 7) + "→ " + Pad(Trim(newDx), 8) +
                       " dy " + Pad(Trim(oldDy), 7) + "→ " + Pad(Trim(newDy), 8) +
                       " w " + Pad(Trim(oldW), 6) + "→ " + Trim(newW));

            // A Z nudge in the scene is real work that this round-trip cannot carry:
            // worldZ is authored, not measured. Say so rather than losing it silently.
            if (e.worldZToken == null && Mathf.Abs(s.localZ) > 0.5f)
            {
                float worldZ = canvasWorldZ + s.localZ * canvasScale;
                notes.Add("\"" + s.name + "\" sits " + s.localZ.ToString("0.#", Inv) +
                          " canvas-px off the canvas plane (worldZ ≈ " + worldZ.ToString("0.####", Inv) +
                          ") but its entry has no worldZ, so a rebuild will flatten it. " +
                          "Add  worldZ = " + F(worldZ) + "  to that entry by hand.");
            }
            else if (e.worldZToken != null)
            {
                zCarried.Add(s.name + " (worldZ = " + e.worldZToken + ")");
            }
        }
        if (zCarried.Count > 0)
            notes.Add("Z carried through from the table, never read from the scene: " +
                      string.Join(", ", zCarried.ToArray()) + ".");
        if (reordered)
            notes.Add("Entry ORDER was taken from the scene's sibling order (it changed) — " +
                      "that is what sets UI draw order, so the table now matches the hierarchy.");

        // ── 5. generate, then re-parse what we generated before trusting it ──
        Progress("Rewriting " + BuilderAssetPath, 0.75f);
        var generated = EmitTable(merged);

        List<TableEntry> verify;
        string verifyError;
        if (!TryParseTable(generated, out verify, out verifyError))
            return Abort("SELF-CHECK FAILED — the table this tool generated does not parse:\n\n" +
                         verifyError + "\n\nNothing was written. Please report this.");
        if (verify.Count != merged.Count)
            return Abort("SELF-CHECK FAILED — generated " + verify.Count + " entries from " +
                         merged.Count + ". Nothing was written.");
        for (int i = 0; i < verify.Count; i++)
        {
            var a = merged[i];
            var b = verify[i];
            if (a.name != b.name || a.spriteToken != b.spriteToken ||
                a.urlToken != b.urlToken || a.vCardToken != b.vCardToken ||
                a.worldZToken != b.worldZToken)
                return Abort("SELF-CHECK FAILED — entry " + (i + 1) + " (\"" + a.name +
                             "\") did not survive the round-trip intact. Nothing was written.");
        }
        var verifyNames = new HashSet<string>();
        foreach (var v in verify) verifyNames.Add(v.name);
        if (verifyNames.Count != entries.Count || !verifyNames.SetEquals(seenNames))
            return Abort("SELF-CHECK FAILED — the regenerated table's element names do not match " +
                         "what was parsed. Nothing was written.");

        // ContentScale, on its own marked line
        var scaleMatch = ContentScaleLine.Match(lines[scaleIdx]);
        if (!scaleMatch.Success)
            return Abort("The line under the " + ScaleMarkerKey + " marker is not a ContentScale " +
                         "assignment:\n\n    " + lines[scaleIdx].Trim() +
                         "\n\nNothing was written.");
        string newScaleLine = scaleMatch.Groups["pre"].Value + F(contentScale) + scaleMatch.Groups["post"].Value;

        // ── 6. backup, then write ────────────────────────────────────────────
        string backupPath;
        try
        {
            backupPath = WriteBackup(builderFullPath, original);
        }
        catch (System.Exception e)
        {
            return Abort("Could not write the .bak backup, so nothing was written:\n\n" + e.Message);
        }

        var output = new List<string>();
        output.AddRange(lines.GetRange(0, beginIdx + 1));
        output.AddRange(generated);
        output.AddRange(lines.GetRange(endIdx, lines.Count - endIdx));

        int scaleShift = generated.Count - (endIdx - beginIdx - 1);
        int outScaleIdx = scaleIdx > endIdx ? scaleIdx + scaleShift : scaleIdx;
        if (outScaleIdx < 0 || outScaleIdx >= output.Count || output[outScaleIdx] != lines[scaleIdx])
            return Abort("Internal line-bookkeeping check failed while placing the ContentScale line. " +
                         "Nothing was written (backup at " + backupPath + " is identical to the original).");
        output[outScaleIdx] = newScaleLine;

        // UTF8 without a BOM, original line endings: the file has box-drawing
        // characters in its comments and must stay byte-compatible with git.
        try
        {
            // Atomic: write beside the file, then swap it in. A crash or a lock
            // part-way through can then never leave RishabhSceneBuilder.cs truncated —
            // it is either entirely the old file or entirely the new one.
            string tmpPath = builderFullPath + ".tmp";
            System.IO.File.WriteAllText(
                tmpPath,
                string.Join(newline, output.ToArray()),
                new UTF8Encoding(false));
            if (System.IO.File.Exists(builderFullPath))
                System.IO.File.Replace(tmpPath, builderFullPath, null);
            else
                System.IO.File.Move(tmpPath, builderFullPath);
        }
        catch (System.Exception e)
        {
            // A half-written builder is the one outcome worth shouting about.
            throw new System.Exception(
                "WRITING " + BuilderAssetPath + " FAILED — it may now be incomplete.\n\n" +
                e.Message + "\n\nRestore it from the backup taken a moment ago:\n" + backupPath, e);
        }

        EditorUtility.ClearProgressBar();

        var msg = new StringBuilder();
        msg.Append("Layout read from \"").Append(src.id).Append("\" and written into\n")
           .Append(BuilderAssetPath).Append("\n\n")
           .Append(merged.Count).Append(" entries, ").Append(changed).Append(" changed. ")
           .Append("ContentScale = ").Append(F(contentScale)).Append("\n")
           .Append("url / vCard / worldZ carried through unchanged.\n\n");
        AppendCapped(msg, report, 24, "");
        if (notes.Count > 0)
        {
            msg.Append("\nNOTES (").Append(notes.Count).Append("):\n");
            AppendCapped(msg, notes, 10, "  ! ");
        }
        msg.Append("\nBackup: ").Append(backupPath)
           .Append("\n\nUnity will recompile now. Rebuild with\n")
           .Append("Tools ▸ Rishabh ▸ Build Rishabh Scene to confirm the round-trip.");

        var log = new StringBuilder("[Rishabh] Layout read into the builder CONFIG. Backup: ");
        log.Append(backupPath).Append('\n');
        foreach (var r in report) log.Append(r).Append('\n');
        foreach (var n in notes)  log.Append("  ! ").Append(n).Append('\n');
        Debug.Log(log.ToString());

        return msg.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    //  THE CONFIG-TABLE PARSER  (RishabhSceneBuilder.cs is the only file it reads)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>One `new Element { … }` from the builder's Layout table. Every field
    /// except dx/dy/width/aspect is kept as its RAW SOURCE TOKEN and written back
    /// verbatim — that is what makes `worldZ = LinesWorldZ` (a symbol, not a number)
    /// and any future field survive the round-trip untouched.</summary>
    private class TableEntry
    {
        public string trivia = "";      // comment / blank lines that preceded it
        public string trailing = "";    // comment after the closing "},"
        public string name;             // unquoted, for matching
        public string nameToken;        // raw, e.g. "\"linesBg\""
        public string spriteToken;
        public string urlToken;         // null when the field is absent
        public string vCardToken;       // null when the field is absent
        public string worldZToken;      // null when the field is absent
        public float  dx, dy, width, aspect;
        public List<KeyValuePair<string, string>> extras = new List<KeyValuePair<string, string>>();

        public TableEntry CloneForOutput()
        {
            return new TableEntry
            {
                trivia = trivia, trailing = trailing,
                name = name, nameToken = nameToken, spriteToken = spriteToken,
                urlToken = urlToken, vCardToken = vCardToken, worldZToken = worldZToken,
                dx = dx, dy = dy, width = width, aspect = aspect,
                extras = new List<KeyValuePair<string, string>>(extras),
            };
        }
    }

    private class SceneElement
    {
        public string name;
        public float dx, dy, width, aspect, localZ;
    }

    private static bool FindMarkers(List<string> lines, out int beginIdx, out int endIdx,
                                    out int scaleIdx, out string error)
    {
        beginIdx = endIdx = scaleIdx = -1;
        error = null;
        int begins = 0, ends = 0, scales = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].TrimStart();
            if (!t.StartsWith("//")) continue;
            if (t.Contains(LayoutBeginKey)) { beginIdx = i; begins++; }
            else if (t.Contains(LayoutEndKey)) { endIdx = i; ends++; }
            else if (t.Contains(ScaleMarkerKey)) { scaleIdx = i; scales++; }
        }
        if (begins != 1 || ends != 1)
        {
            error = "Expected exactly one " + LayoutBeginKey + " and one " + LayoutEndKey +
                    " marker in " + BuilderAssetPath + "; found " + begins + " and " + ends + ".";
            return false;
        }
        if (scales != 1)
        {
            error = "Expected exactly one " + ScaleMarkerKey + " marker in " + BuilderAssetPath +
                    "; found " + scales + ".";
            return false;
        }
        if (endIdx <= beginIdx)
        {
            error = "The " + LayoutEndKey + " marker comes before the " + LayoutBeginKey + " marker.";
            return false;
        }
        if (scaleIdx > beginIdx && scaleIdx < endIdx)
        {
            error = "The " + ScaleMarkerKey + " marker is inside the layout region.";
            return false;
        }
        scaleIdx = scaleIdx + 1;                       // the assignment sits on the NEXT line
        if (scaleIdx >= lines.Count)
        {
            error = "The " + ScaleMarkerKey + " marker is the last line of the file.";
            return false;
        }
        return true;
    }

    private static bool TryParseTable(List<string> region, out List<TableEntry> entries, out string error)
    {
        entries = new List<TableEntry>();
        error = null;
        var trivia = new StringBuilder();

        for (int i = 0; i < region.Count; i++)
        {
            string raw = region[i];
            string t   = raw.Trim();
            if (t.Length == 0 || t.StartsWith("//"))
            {
                trivia.Append(raw).Append('\n');
                continue;
            }

            // Gather lines until the entry's braces balance (they are one-liners today,
            // but a hand-wrapped entry must not blow this up).
            var chunk = new StringBuilder(raw);
            int close = FindMatchingBrace(chunk.ToString());
            int j = i;
            while (close < 0 && j + 1 < region.Count)
            {
                j++;
                chunk.Append('\n').Append(region[j]);
                close = FindMatchingBrace(chunk.ToString());
            }
            string text = chunk.ToString();
            if (close < 0)
            {
                error = "Unbalanced braces starting at:\n    " + t;
                return false;
            }

            int open = text.IndexOf('{');
            string head = text.Substring(0, open).Trim();
            if (head != "new Element")
            {
                error = "Expected `new Element { … }` but found:\n    " + head + " {";
                return false;
            }

            string tail = text.Substring(close + 1).Trim();
            if (tail.StartsWith(",")) tail = tail.Substring(1).Trim();
            if (tail.Length > 0 && !tail.StartsWith("//"))
            {
                error = "Unexpected text after an entry:\n    " + tail;
                return false;
            }

            var entry = new TableEntry { trivia = trivia.ToString(), trailing = tail };
            trivia.Length = 0;

            if (!TryParseBody(text.Substring(open + 1, close - open - 1), entry, out error))
                return false;
            entries.Add(entry);
            i = j;
        }

        if (trivia.Length > 0 && entries.Count > 0)
        {
            // Trailing comments with no entry beneath them would be silently dropped
            // by the regenerate, so refuse instead. (With NO entries at all the region
            // is simply empty — the caller says so far more clearly than this would.)
            error = "There are comment/blank lines after the LAST entry and before the " +
                    LayoutEndKey + " marker:\n" + trivia.ToString().TrimEnd() +
                    "\n\nMove them outside the markers (above " + LayoutBeginKey +
                    " or below " + LayoutEndKey + ") so they cannot be regenerated away.";
            return false;
        }
        return true;
    }

    private static bool TryParseBody(string body, TableEntry entry, out string error)
    {
        error = null;
        bool hasDx = false, hasDy = false, hasWidth = false, hasAspect = false;

        foreach (string part in SplitTopLevel(body))
        {
            string p = part.Trim();
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            if (eq <= 0)
            {
                error = "Field without an `=` in an entry:\n    " + p;
                return false;
            }
            string key = p.Substring(0, eq).Trim();
            string val = p.Substring(eq + 1).Trim();
            if (val.Length == 0)
            {
                error = "Field `" + key + "` has no value.";
                return false;
            }

            switch (key)
            {
                case "name":
                    entry.nameToken = val;
                    if (!TryUnquote(val, out entry.name))
                    {
                        error = "`name` is not a plain string literal: " + val;
                        return false;
                    }
                    break;
                case "sprite":  entry.spriteToken = val; break;
                case "url":     entry.urlToken    = val; break;
                case "vCard":   entry.vCardToken  = val; break;
                case "worldZ":  entry.worldZToken = val; break;
                case "dx":      if (!TryFloat(val, out entry.dx,     key, ref error)) return false; hasDx = true; break;
                case "dy":      if (!TryFloat(val, out entry.dy,     key, ref error)) return false; hasDy = true; break;
                case "width":   if (!TryFloat(val, out entry.width,  key, ref error)) return false; hasWidth = true; break;
                case "aspect":  if (!TryFloat(val, out entry.aspect, key, ref error)) return false; hasAspect = true; break;
                default:
                    entry.extras.Add(new KeyValuePair<string, string>(key, val));
                    break;
            }
        }

        if (string.IsNullOrEmpty(entry.name))    { error = "An entry has no `name`.";   return false; }
        if (string.IsNullOrEmpty(entry.spriteToken))
        {
            error = "Entry \"" + entry.name + "\" has no `sprite` — the builder cannot draw it.";
            return false;
        }
        if (!hasDx || !hasDy || !hasWidth || !hasAspect)
        {
            error = "Entry \"" + entry.name + "\" is missing dx / dy / width / aspect — " +
                    "this tool will not invent the ones it cannot see.";
            return false;
        }
        return true;
    }

    /// <summary>Splits on commas that are outside string literals.</summary>
    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inStr = false, esc = false;
        foreach (char c in body)
        {
            if (inStr)
            {
                sb.Append(c);
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == ',') { parts.Add(sb.ToString()); sb.Length = 0; continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Index of the `}` that closes the first `{`, or −1. String literals and
    /// // comments are skipped so a brace inside either cannot fool it.</summary>
    private static int FindMatchingBrace(string s)
    {
        int depth = 0;
        bool inStr = false, esc = false, sawOpen = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                int nl = s.IndexOf('\n', i);
                if (nl < 0) return -1;
                i = nl;
                continue;
            }
            if (c == '{') { depth++; sawOpen = true; continue; }
            if (c == '}')
            {
                depth--;
                if (sawOpen && depth == 0) return i;
            }
        }
        return -1;
    }

    private static bool TryUnquote(string token, out string value)
    {
        value = null;
        if (token.Length < 2 || token[0] != '"' || token[token.Length - 1] != '"') return false;
        var sb = new StringBuilder();
        for (int i = 1; i < token.Length - 1; i++)
        {
            char c = token[i];
            if (c == '\\' && i + 1 < token.Length - 1) { i++; sb.Append(token[i]); continue; }
            sb.Append(c);
        }
        value = sb.ToString();
        return true;
    }

    private static bool TryFloat(string token, out float value, string key, ref string error)
    {
        string t = token.TrimEnd('f', 'F');
        if (float.TryParse(t, NumberStyles.Float, Inv, out value)) return true;
        error = "`" + key + " = " + token + "` is not a plain float literal. " +
                "This tool rewrites those numbers, so it refuses to guess at an expression.";
        value = 0f;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    //  THE CONFIG-TABLE EMITTER
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Renders the merged entries back as C#, column-aligned the way the
    /// table is written by hand. Comments ride along with the entry they preceded.</summary>
    private static List<string> EmitTable(List<TableEntry> entries)
    {
        var nameSeg   = new List<string>();
        var spriteSeg = new List<string>();
        var dxSeg     = new List<string>();
        var dySeg     = new List<string>();
        var wSeg      = new List<string>();
        var aSeg      = new List<string>();

        foreach (var e in entries)
        {
            nameSeg.Add("name = " + e.nameToken + ",");
            spriteSeg.Add("sprite = " + e.spriteToken + ",");
            dxSeg.Add(F(e.dx));
            dySeg.Add(F(e.dy));
            wSeg.Add(F(e.width));
            aSeg.Add(F(e.aspect));
        }

        int wName = MaxLen(nameSeg), wSprite = MaxLen(spriteSeg);
        int wDx = MaxLen(dxSeg), wDy = MaxLen(dySeg), wW = MaxLen(wSeg), wA = MaxLen(aSeg);

        var outLines = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.trivia.Length > 0)
            {
                foreach (string tl in e.trivia.TrimEnd('\n').Split('\n')) outLines.Add(tl);
            }

            var sb = new StringBuilder();
            sb.Append("        new Element { ");
            sb.Append(nameSeg[i].PadRight(wName + 1)).Append(' ');
            sb.Append(spriteSeg[i].PadRight(wSprite + 1)).Append(' ');
            sb.Append("dx = ").Append(dxSeg[i].PadLeft(wDx)).Append(", ");
            sb.Append("dy = ").Append(dySeg[i].PadLeft(wDy)).Append(", ");
            sb.Append("width = ").Append(wSeg[i].PadLeft(wW)).Append(", ");
            sb.Append("aspect = ").Append(aSeg[i].PadLeft(wA));
            if (e.urlToken    != null) sb.Append(", url = ").Append(e.urlToken);
            if (e.vCardToken  != null) sb.Append(", vCard = ").Append(e.vCardToken);
            if (e.worldZToken != null) sb.Append(", worldZ = ").Append(e.worldZToken);
            foreach (var extra in e.extras) sb.Append(", ").Append(extra.Key).Append(" = ").Append(extra.Value);
            sb.Append(" },");
            if (e.trailing.Length > 0) sb.Append("   ").Append(e.trailing);
            outLines.Add(sb.ToString());
        }
        return outLines;
    }

    private static int MaxLen(List<string> items)
    {
        int m = 0;
        foreach (var s in items) if (s.Length > m) m = s.Length;
        return m;
    }

    /// <summary>InvariantCulture, always. On a comma-decimal machine (de-DE, fr-FR…)
    /// "0,09f" would not compile — the same trap the builder guards in GetOrCreateMesh.</summary>
    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v))
            throw new System.Exception("Non-finite number (" + v + ") in the layout — nothing was written.");
        string s = v.ToString("0.####", Inv);
        if (s == "-0") s = "0";
        return s + "f";
    }

    private static string Trim(string floatToken)
    {
        return floatToken.EndsWith("f") ? floatToken.Substring(0, floatToken.Length - 1) : floatToken;
    }

    // ─────────────────────────────────────────────────────────────────
    //  SCENE HELPERS
    // ─────────────────────────────────────────────────────────────────

    private class TargetRef
    {
        public string id;
        public Transform root;
    }

    /// <summary>Targets come from the ImageTracker's own serialized list, not from the
    /// builder's private Targets table — the scene is the thing being edited, so the
    /// scene is the thing that gets to say which roots exist and in what order.</summary>
    private static List<TargetRef> ReadTrackerTargets(Scene scene, List<string> warnings)
    {
        ImageTracker tracker = FindInScene<ImageTracker>(scene);
        if (tracker == null)
            throw new System.Exception("No ImageTracker in " + scene.path +
                                       " — is this the scene RishabhSceneBuilder generates?");

        var so   = new SerializedObject(tracker);
        var prop = so.FindProperty("imageTargets");
        if (prop == null || !prop.isArray)
            throw new System.Exception("ImageTracker.imageTargets not found (or no longer a list) — API changed?");

        var list = new List<TargetRef>();
        for (int i = 0; i < prop.arraySize; i++)
        {
            var el     = prop.GetArrayElementAtIndex(i);
            var idProp = el.FindPropertyRelative("id");
            var trProp = el.FindPropertyRelative("transform");
            string id  = idProp != null ? idProp.stringValue : ("#" + i);
            var t      = trProp != null ? trProp.objectReferenceValue as Transform : null;
            if (t == null)
            {
                warnings.Add("tracker target '" + id + "' has no transform assigned — skipped");
                continue;
            }
            list.Add(new TargetRef { id = id, root = t });
        }
        if (list.Count == 0)
            throw new System.Exception("The ImageTracker has no usable targets in " + scene.path + ".");
        return list;
    }

    private static void CollectUiChildren(Transform canvas, string label, List<string> warnings,
                                          out List<RectTransform> ordered,
                                          out Dictionary<string, RectTransform> byName)
    {
        ordered = new List<RectTransform>();
        byName  = new Dictionary<string, RectTransform>();
        for (int i = 0; i < canvas.childCount; i++)
        {
            Transform child = canvas.GetChild(i);
            var rt = child as RectTransform;
            if (rt == null)
            {
                warnings.Add(label + ": \"" + child.name + "\" under the World Canvas has no RectTransform — ignored");
                continue;
            }
            if (byName.ContainsKey(rt.name))
            {
                warnings.Add(label + ": DUPLICATE element name \"" + rt.name +
                             "\" — only the first one is used, the rest are ignored");
                continue;
            }
            byName[rt.name] = rt;
            ordered.Add(rt);
        }
    }

    private static Transform RequireChild(Transform parent, string name, string targetId)
    {
        Transform t = parent.Find(name);
        if (t == null)
            throw new System.Exception("Target '" + targetId + "' has no \"" + name + "\" child.\n\n" +
                                       "This command expects the hierarchy RishabhSceneBuilder makes:\n" +
                                       "    <target>/" + ContentName + "/" + CanvasName + "/<ui elements>");
        return t;
    }

    private static T FindInScene<T>(Scene scene) where T : Component
    {
        foreach (var go in scene.GetRootGameObjects())
        {
            var c = go.GetComponentInChildren<T>(true);
            if (c != null) return c;
        }
        return null;
    }

    /// <summary>Works on the open scene when that is Rishabh.unity (loaded but not
    /// active counts), otherwise offers to open it.</summary>
    private static bool EnsureSceneOpen(out Scene scene)
    {
        scene = EditorSceneManager.GetActiveScene();
        if (scene.path == ScenePath) return true;

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.isLoaded && s.path == ScenePath) { scene = s; return true; }
        }

        if (!System.IO.File.Exists(ScenePath))
        {
            EditorUtility.DisplayDialog(Title,
                ScenePath + " does not exist yet.\n\nBuild it first:\n" +
                "Tools ▸ Rishabh ▸ Build Rishabh Scene", "OK");
            return false;
        }
        if (!EditorUtility.DisplayDialog(Title,
                "This works on " + ScenePath + ", which is not open.\n\nOpen it now?",
                "Open Rishabh.unity", "Cancel"))
            return false;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

        scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        return scene.IsValid();
    }

    private static bool CheckNotPlaying()
    {
        if (!EditorApplication.isPlayingOrWillChangePlaymode) return true;
        EditorUtility.DisplayDialog(Title,
            "Exit Play mode first.\n\n" +
            "In Play mode ARContentScale.Start() has already overwritten Content.localScale " +
            "and every edit is thrown away when you press stop.", "OK");
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    //  FILE / UI HELPERS
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Absolute path of RishabhSceneBuilder.cs, or null. Falls back to a GUID
    /// search so moving the file does not break this command.</summary>
    private static string ResolveBuilderPath()
    {
        string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string full = System.IO.Path.Combine(projectRoot,
            BuilderAssetPath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
        if (System.IO.File.Exists(full)) return full;

        foreach (string guid in AssetDatabase.FindAssets("RishabhSceneBuilder t:MonoScript"))
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.EndsWith("/RishabhSceneBuilder.cs")) continue;
            string moved = System.IO.Path.Combine(projectRoot,
                assetPath.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString()));
            if (System.IO.File.Exists(moved)) return moved;
        }
        return null;
    }

    /// <summary>Timestamped, and OUTSIDE Assets/ so Unity never imports it. Timestamped
    /// because the second run of a bad edit must not overwrite the one good backup.</summary>
    private static string WriteBackup(string builderFullPath, string contents)
    {
        string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName;
        string dir = System.IO.Path.Combine(projectRoot, BackupProjectFolder);
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
        string path  = System.IO.Path.Combine(dir,
            System.IO.Path.GetFileNameWithoutExtension(builderFullPath) + "_" + stamp + ".cs.bak");
        System.IO.File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Aborts a run: dialog, console line, nothing written. Returns null so
    /// the caller can `return Abort(…)`.</summary>
    private static string Abort(string message)
    {
        EditorUtility.ClearProgressBar();
        EditorUtility.DisplayDialog(Title + " — Aborted", message, "OK");
        Debug.LogWarning("[Rishabh] Read Layout aborted — nothing was written.\n" + message);
        return null;
    }

    private static void AppendCapped(StringBuilder sb, List<string> items, int max, string prefix)
    {
        for (int i = 0; i < items.Count && i < max; i++)
            sb.Append(prefix).Append(items[i]).Append('\n');
        if (items.Count > max)
            sb.Append("  … and ").Append(items.Count - max).Append(" more — see the Console.\n");
    }

    private static string Pad(string s, int width)
    {
        return s.Length >= width ? s + " " : s.PadRight(width);
    }

    private static void Progress(string step, float t)
    {
        if (!Application.isBatchMode)
            EditorUtility.DisplayProgressBar(Title, step, t);
    }
}
