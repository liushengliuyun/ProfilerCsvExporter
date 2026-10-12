using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ProfilerCsvExporter
{
    public static class ProfilerWindowDumper
    {
        // Deep, targeted hunt for the real selected hierarchy item id.
        // The shallow DrillForSelection only matches *Select*/*Item* int fields at depth 3
        // and misses the TreeViewState (usually deeper, behind a non-Select name, in a base
        // class). This walks further and, for hierarchy/treeview-ish types, dumps ALL int
        // fields — the id field is often named m_Something without "Select".
        [MenuItem("Tools/Profiler/Hunt Selection Id (deep)")]
        public static void HuntSelectionId()
        {
            var sb = new StringBuilder();
            Type t = ResolveProfilerWindowType();
            if (t == null) { Debug.LogError("[Hunt] UnityEditor.ProfilerWindow not found"); return; }

            EditorWindow instance = null;
            foreach (var w in Resources.FindObjectsOfTypeAll(t))
                if (w is EditorWindow ew) { instance = ew; break; }
            if (instance == null) { Debug.Log("[Hunt] ProfilerWindow not open"); return; }

            var moduleProp = t.GetProperty("selectedModule",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var module = moduleProp?.GetValue(instance, null);
            if (module == null) { Debug.Log("[Hunt] selectedModule is null"); return; }

            // 1) Full inherited field chain of the module — reveals hidden hierarchy GUI
            //    fields that live in the base ProfilerModule class (hidden by DeclaredOnly).
            sb.AppendLine($"=== HUNT: {module.GetType().FullName} field chain ===");
            var mt = module.GetType();
            while (mt != null && mt != typeof(object))
            {
                sb.AppendLine($"-- {mt.FullName} --");
                foreach (var f in mt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    sb.AppendLine($"   {f.FieldType.Name}  {f.Name}");
                mt = mt.BaseType;
            }

            // 2) Resolve TreeViewState type for instance-of checks.
            Type tvsType = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { tvsType = asm.GetType("UnityEditor.IMGUI.Controls.TreeViewState"); } catch { }
                if (tvsType != null) break;
            }
            sb.AppendLine();
            sb.AppendLine($"TreeViewState resolved = {(tvsType == null ? "null" : tvsType.FullName)}");
            sb.AppendLine("=== HUNT: recursive selection search ===");

            var visited = new System.Collections.Generic.HashSet<int>();
            int visitedCount = 0;
            HuntGraph(sb, module, module.GetType(), "module", tvsType, 0, 7, visited, ref visitedCount);
            sb.AppendLine($"(visited {visitedCount} objects)");
            Debug.Log(sb.ToString());
        }

        static void HuntGraph(StringBuilder sb, object obj, Type objType, string path,
            Type tvsType, int depth, int maxDepth, HashSet<int> visited, ref int count)
        {
            if (obj == null || depth > maxDepth || count > 6000) return;
            int hc = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(hc)) return; // cycle guard
            count++;
            var t = obj.GetType();

            // If this is a TreeViewState, dump its selection ints.
            if (tvsType != null && (t == tvsType || t.IsSubclassOf(tvsType)))
            {
                sb.AppendLine($">>> TreeViewState @ {path} ({t.Name})");
                DumpStateIntFields(sb, obj, "      ");
            }

            // For hierarchy/treeview/column-ish types, dump ALL int-ish fields — the id
            // field may not contain "Select".
            bool interesting = IsHierarchyType(t);
            if (interesting)
                sb.AppendLine($"  [type {t.Name} @ {path}]");

            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string fn = f.Name;
                object val = null;
                try { val = f.GetValue(obj); } catch { continue; }

                if (IsIntLike(f.FieldType) && (IsSelectishName(fn) || interesting))
                    sb.AppendLine($"  [F] {path}.{fn} ({f.FieldType.Name}) = {FormatValue(val)}");

                if (ShouldTraverse(f.FieldType))
                    HuntGraph(sb, val, f.FieldType, $"{path}.{fn}", tvsType, depth + 1, maxDepth, visited, ref count);
            }

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                string pn = p.Name;
                if (!IsIntLike(p.PropertyType)) continue;
                if (!IsSelectishName(pn) && !interesting) continue;
                object val = null;
                try { val = p.GetValue(obj, null); } catch { continue; }
                sb.AppendLine($"  [P] {path}.{pn} ({p.PropertyType.Name}) = {FormatValue(val)}");
            }
        }

        static void DumpStateIntFields(StringBuilder sb, object obj, string pad)
        {
            var bt = obj.GetType();
            while (bt != null && bt != typeof(object))
            {
                foreach (var f in bt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType == typeof(int) || f.FieldType == typeof(int[]) ||
                        typeof(System.Collections.IList).IsAssignableFrom(f.FieldType))
                    {
                        try { sb.AppendLine($"{pad}[{bt.Name}] {f.Name} ({f.FieldType.Name}) = {FormatValue(f.GetValue(obj))}"); }
                        catch { }
                    }
                }
                bt = bt.BaseType;
            }
        }

        static bool IsSelectishName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Click", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Mark", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Highlight", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsIntLike(Type t)
        {
            if (t == null) return false;
            return t == typeof(int)
                || t == typeof(int[])
                || typeof(System.Collections.IList).IsAssignableFrom(t);
        }

        static bool IsHierarchyType(Type t)
        {
            if (t == null) return false;
            string n = t.Name;
            return n.IndexOf("Hierarchy", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("TreeView", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ShouldTraverse(Type t)
        {
            if (t == null) return false;
            if (t.IsPrimitive || t.IsEnum || t == typeof(string)) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return false;
            if (!t.IsClass && !t.IsArray && !typeof(System.Collections.IList).IsAssignableFrom(t)) return false;
            string n = t.Name;
            return n.IndexOf("TreeView", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Hierarchy", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("State", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("GUI", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("View", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Module", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string FormatValue(object v)
        {
            if (v == null) return "null";
            if (v is System.Collections.IList list)
            {
                var s = new StringBuilder("[");
                int i = 0;
                foreach (var x in list)
                {
                    if (i > 0) s.Append(',');
                    if (i >= 50) { s.Append("..."); break; }
                    s.Append(x);
                    i++;
                }
                s.Append(']');
                return s.ToString();
            }
            var str = v.ToString();
            return str.Length > 80 ? str.Substring(0, 80) + "..." : str;
        }

        static Type ResolveProfilerWindowType()
        {
            var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.ProfilerWindow");
            if (t != null) return t;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("UnityEditor.ProfilerWindow"); if (t != null) return t; }
                catch { }
            }
            return null;
        }
    }
}
