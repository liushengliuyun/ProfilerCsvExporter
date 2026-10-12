using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEngine;

namespace ProfilerCsvExporter
{
    /// <summary>
    /// Reads the currently-selected hierarchy item from Unity's native Profiler window.
    /// Unity 2022.3 exposes the selection via GUIContent selectionHighlightLabel on the
    /// active CPUProfilerModule. We read the function name from there, and reverse-resolve
    /// it to an item id by walking the ProfilerWindow.m_FrameDataView directly.
    /// The same m_FrameDataView MUST be used for both name→id resolution and CSV export,
    /// because item ids are not stable across different HierarchyFrameDataView instances.
    /// </summary>
    internal static class ProfilerSelectionReader
    {
        const string kProfilerWindowType = "UnityEditor.ProfilerWindow";

        public struct Selection
        {
            public bool FrameValid;
            public int FrameIndex;
            public int ResolvedItemId;     // -1 if name lookup failed
            public string FunctionName;    // raw label from selectionHighlightLabel
            public string ThreadName;
            public string Warning;
        }

        static Type s_ProfilerWindowType;
        static bool s_TypeResolved;

        static FieldInfo s_CurrentFrameField;
        static FieldInfo s_FrameDataViewField;
        static PropertyInfo s_SelectedModuleProperty;
        static FieldInfo s_SelectionLabelField;

        // Reflection caches for the exact TreeView selection path:
        //   selectedModule -> m_FrameDataHierarchyView -> m_TreeViewState -> {lastClickedID | selectedIDs}
        // Lets us resolve the real selected item id even when the frame has many
        // identically-named nodes (the name-based FindItemIdByName can't disambiguate those).
        static FieldInfo s_HierarchyViewField;       // on CPUOrGPUProfilerModule
        static FieldInfo s_HierarchyFrameIndexField; // on ProfilerFrameDataHierarchyView
        static FieldInfo s_TreeViewStateField;       // on ProfilerFrameDataHierarchyView
        static FieldInfo s_LastClickedIdField;       // on TreeViewState
        static FieldInfo s_SelectedIdsField;         // on TreeViewState (List<int>)

        static Type ProfilerWindowType
        {
            get
            {
                if (!s_TypeResolved)
                {
                    s_TypeResolved = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try { s_ProfilerWindowType = asm.GetType(kProfilerWindowType, false); }
                        catch { }
                        if (s_ProfilerWindowType != null) break;
                    }
                    if (s_ProfilerWindowType == null)
                        Debug.LogWarning($"[ProfilerCsvExporter] Could not resolve {kProfilerWindowType}.");
                }
                return s_ProfilerWindowType;
            }
        }

        public static EditorWindow FindProfilerWindow()
        {
            var t = ProfilerWindowType;
            if (t == null) return null;
            foreach (var w in Resources.FindObjectsOfTypeAll(t))
                if (w is EditorWindow ew) return ew;
            return null;
        }

        /// <summary>
        /// Returns the HierarchyFrameDataView currently held by ProfilerWindow.m_FrameDataView.
        /// Caller MUST NOT dispose it — Unity owns the underlying handle.
        /// </summary>
        public static HierarchyFrameDataView GetFrameDataView()
        {
            var window = FindProfilerWindow();
            if (window == null) return default;
            var box = ReadFieldValue(window, ref s_FrameDataViewField, "m_FrameDataView");
            return box is HierarchyFrameDataView h ? h : default;
        }

        /// <summary>
        /// Defensive validity check. Unity's HierarchyFrameDataView.valid can throw
        /// NullReferenceException when the underlying ProfilerFrameDataView is in an
        /// inconsistent state (e.g., during frame transitions, window init, domain reload).
        /// Treat any such throw as "not valid" instead of letting it propagate.
        /// </summary>
        public static bool IsFdValid(HierarchyFrameDataView fd)
        {
            try { return fd.valid; }
            catch { return false; }
        }

        public static Selection Read()
        {
            var sel = new Selection { FrameIndex = -1, ResolvedItemId = -1 };

            var window = FindProfilerWindow();
            if (window == null)
            {
                sel.Warning = "Profiler 窗口未打开";
                return sel;
            }

            int frame = ReadIntField(window, ref s_CurrentFrameField, "m_CurrentFrame");
            sel.FrameIndex = frame;
            sel.FrameValid = frame >= 0;
            if (!sel.FrameValid)
            {
                sel.Warning = "Profiler 无选中帧";
                return sel;
            }

            var fd = GetFrameDataView();
            if (!IsFdValid(fd))
            {
                sel.Warning = "Profiler m_FrameDataView 不可用";
                return sel;
            }
            sel.ThreadName = fd.threadName;

            var moduleObj = ReadPropertyValue(window, ref s_SelectedModuleProperty, "selectedModule");
            if (moduleObj == null)
            {
                sel.Warning = "Profiler selectedModule 为 null";
                return sel;
            }

            sel.FunctionName = ParseSelectionLabel(moduleObj);

            // Prefer the EXACT item id from the CPU module's TreeView state — unambiguous even
            // when the frame has many nodes with the same name (e.g. several Instantiate calls).
            // Falls back to name-based lookup only when the TreeView path is unavailable
            // (older Unity, field renamed, hierarchy tab not built, or selection stale).
            sel.ResolvedItemId = TryResolveSelectedIdByTreeView(moduleObj, fd, frame);

            if (sel.ResolvedItemId > 0)
            {
                // Trust the id; refresh the display name from fd if the label was empty/stale.
                if (string.IsNullOrEmpty(sel.FunctionName))
                {
                    try { sel.FunctionName = fd.GetItemName(sel.ResolvedItemId); } catch { }
                }
                return sel;
            }

            if (string.IsNullOrEmpty(sel.FunctionName))
            {
                sel.Warning = "未读取到 Profiler 选中节点,将导出整树";
                return sel;
            }

            sel.ResolvedItemId = FindItemIdByName(fd, sel.FunctionName);
            if (sel.ResolvedItemId < 0)
            {
                sel.Warning = $"在 fd 中找不到 '{sel.FunctionName}',将导出整树(详情见 Console)";
                LogLookupFailure(fd, sel.FunctionName);
            }
            return sel;
        }

        /// <summary>
        /// Reads the EXACT selected hierarchy item id straight from the CPU module's TreeView
        /// state. Path: selectedModule.m_FrameDataHierarchyView.m_TreeViewState.{lastClickedID|selectedIDs}.
        /// Unlike name-based lookup this is unambiguous when the frame contains many nodes with
        /// the same function name (e.g. several Instantiate calls). Returns -1 when the path is
        /// unavailable (older Unity, field renamed, hierarchy tab not yet built) or the resolved
        /// id doesn't validate against the current frame data view — callers then fall back to
        /// FindItemIdByName.
        /// </summary>
        static int TryResolveSelectedIdByTreeView(object module, HierarchyFrameDataView fd, int currentFrame)
        {
            if (module == null || !IsFdValid(fd)) return -1;

            var hv = ReadFieldValue(module, ref s_HierarchyViewField, "m_FrameDataHierarchyView");
            if (hv == null) return -1;

            // Reject stale selection: the hierarchy view may still hold an id from a previous
            // frame if the user just changed frames; fd.GetItemName would then silently map it
            // to a different node in the new frame.
            int hvFrame = ReadIntField(hv, ref s_HierarchyFrameIndexField, "m_FrameIndex");
            if (hvFrame >= 0 && hvFrame != currentFrame) return -1;

            var tvs = ReadFieldValue(hv, ref s_TreeViewStateField, "m_TreeViewState");
            if (tvs == null) return -1;

            int id = ReadIntField(tvs, ref s_LastClickedIdField, "m_LastClickedID");
            if (id <= 0)
                id = ReadFirstSelectedId(tvs, ref s_SelectedIdsField, "m_SelectedIDs");
            if (id <= 0) return -1;

            // Validate the id against the live fd before trusting it.
            try
            {
                if (string.IsNullOrEmpty(fd.GetItemName(id))) return -1;
            }
            catch { return -1; }

            return id;
        }

        static int ReadFirstSelectedId(object obj, ref FieldInfo cache, string name)
        {
            var f = cache ?? (cache = obj.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            if (f == null) return -1;
            try
            {
                if (f.GetValue(obj) is System.Collections.IList list && list.Count > 0)
                    return Convert.ToInt32(list[0]);
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// BFS over fd. Tries exact name match first (returns shallowest), then falls back
        /// to word-boundary regex matching (prefers shortest name = closest to target), then
        /// to bidirectional substring matching (prefers length closest to target).
        /// Word-boundary is essential: "BlitPass.Execute()" must NOT match "FinalBlitPass.Execute()"
        /// because there's no word boundary between "Final" and "Blit" (both are word chars).
        /// Returns -1 if nothing matches.
        /// </summary>
        public static int FindItemIdByName(HierarchyFrameDataView fd, string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || !IsFdValid(fd)) return -1;

            var allItems = new List<(int id, string name)>();
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            var children = new List<int>();
            int rootId = fd.GetRootItemID();
            queue.Enqueue(rootId);
            visited.Add(rootId);

            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                if (id != rootId)
                {
                    string name = null;
                    try { name = fd.GetItemName(id); } catch { }
                    if (name == targetName) return id; // exact, shallowest (BFS)
                    allItems.Add((id, name));
                }
                if (fd.HasItemChildren(id))
                {
                    children.Clear();
                    fd.GetItemChildren(id, children);
                    foreach (var c in children)
                        if (visited.Add(c)) queue.Enqueue(c);
                }
            }

            // Word-boundary regex match: requires the target to start at a \b boundary.
            // This correctly rejects "FinalBlitPass.Execute()" when target is "BlitPass.Execute()"
            // (no boundary between "Final" and "Blit"), while accepting namespace-qualified
            // names like "UnityEngine.Rendering.BlitPass.Execute()" (boundary after the dot).
            // Prefer the SHORTEST matching name — closest to target length is most likely correct.
            try
            {
                var regex = new Regex(@"\b" + Regex.Escape(targetName), RegexOptions.IgnoreCase);
                int bestId = -1;
                int bestLen = int.MaxValue;
                foreach (var (id, name) in allItems)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (regex.IsMatch(name) && name.Length < bestLen)
                    {
                        bestLen = name.Length;
                        bestId = id;
                    }
                }
                if (bestId >= 0) return bestId;
            }
            catch { } // regex failure — fall through to last-resort substring match

            // Last-resort: bidirectional substring (target contains name OR name contains target).
            // Prefer the name whose length is closest to target (avoids bias toward longest).
            {
                int targetLen = targetName.Length;
                int bestDist = int.MaxValue;
                int bestId = -1;
                foreach (var (id, name) in allItems)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    bool nc = name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool tc = targetName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (nc || tc)
                    {
                        int dist = Math.Abs(name.Length - targetLen);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId = id;
                        }
                    }
                }
                return bestId;
            }
        }

        static void LogLookupFailure(HierarchyFrameDataView fd, string targetName)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[ProfilerCsvExporter] FindItemIdByName failed for '{targetName}'.");
            sb.AppendLine($"  fd.viewMode={fd.viewMode}, sortColumn={fd.sortColumn}, threadName={fd.threadName}");

            // Pull a keyword from targetName (first '.'-delimited segment) to grep partial matches.
            string keyword = targetName.IndexOf('.') > 0
                ? targetName.Substring(0, targetName.IndexOf('.'))
                : targetName;

            try
            {
                var matches = new List<string>();
                int total = 0;
                var queue = new Queue<int>();
                var visited = new HashSet<int>();
                var children = new List<int>();
                int rootId = fd.GetRootItemID();
                queue.Enqueue(rootId);
                visited.Add(rootId);
                while (queue.Count > 0 && matches.Count < 20)
                {
                    int id = queue.Dequeue();
                    if (id != rootId)
                    {
                        total++;
                        string n = null;
                        try { n = fd.GetItemName(id); } catch { }
                        if (!string.IsNullOrEmpty(n) && n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            matches.Add($"    - {n}");
                    }
                    if (fd.HasItemChildren(id))
                    {
                        children.Clear();
                        fd.GetItemChildren(id, children);
                        foreach (var c in children)
                            if (visited.Add(c)) queue.Enqueue(c);
                    }
                }
                sb.AppendLine($"  Total items scanned (partial, capped at BFS window): {total}");
                sb.AppendLine($"  Items containing keyword '{keyword}' (first 20):");
                if (matches.Count == 0) sb.AppendLine("    (none)");
                foreach (var m in matches) sb.AppendLine(m);
            }
            catch { }
            Debug.LogWarning(sb.ToString());
        }

        static string ParseSelectionLabel(object module)
        {
            var label = ReadFieldValue(module, ref s_SelectionLabelField, "selectionHighlightLabel");
            if (label is GUIContent gc && gc != null)
            {
                string text = gc.text ?? "";
                const string prefix = "Selected:";
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    text = text.Substring(prefix.Length).Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            return null;
        }

        static int ReadIntField(object obj, ref FieldInfo cache, string name)
        {
            var f = cache ?? (cache = obj.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            if (f != null && f.FieldType == typeof(int))
            {
                try { return (int)f.GetValue(obj); } catch { }
            }
            return -1;
        }

        static object ReadFieldValue(object obj, ref FieldInfo cache, string name)
        {
            var f = cache ?? (cache = obj.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            try { return f?.GetValue(obj); } catch { return null; }
        }

        static object ReadPropertyValue(object obj, ref PropertyInfo cache, string name)
        {
            var p = cache ?? (cache = obj.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            try { return p?.GetValue(obj, null); } catch { return null; }
        }
    }
}
