using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.Profiling;

namespace ProfilerCsvExporter
{
    /// <summary>
    /// Traverses a HierarchyFrameDataView starting from a given item id and writes
    /// CPU profiler columns to CSV. UTF-8 with BOM so Excel opens without import wizard.
    /// </summary>
    internal static class ProfilerCsvWriter
    {
        const string kHeader = "Depth,FunctionName,TotalTime(ms),SelfTime(ms),TotalPercent(%),SelfPercent(%),Calls,GCAlloc(bytes),StartTime(ms)";

        static readonly List<int> s_ChildrenBuffer = new List<int>();

        public static int WriteSelection(HierarchyFrameDataView fd, int rootItemId, int maxDepth, string path)
        {
            string csv = BuildCsv(fd, rootItemId, maxDepth, out int rows);
            File.WriteAllText(path, csv, new UTF8Encoding(true));
            return rows;
        }

        /// <param name="maxDepth">-1 = unlimited, 0 = self only, N = recurse N levels deep.</param>
        public static string BuildCsv(HierarchyFrameDataView fd, int rootItemId, int maxDepth, out int rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(kHeader);
            if (rootItemId < 0)
            {
                int actualRoot = fd.GetRootItemID();
                s_ChildrenBuffer.Clear();
                fd.GetItemChildren(actualRoot, s_ChildrenBuffer);
                rows = 0;
                foreach (var childId in s_ChildrenBuffer)
                    rows += WriteNode(fd, childId, 0, maxDepth, sb);
            }
            else
            {
                rows = WriteNode(fd, rootItemId, 0, maxDepth, sb);
            }
            return sb.ToString();
        }

        static int WriteNode(HierarchyFrameDataView fd, int itemId, int relativeDepth, int maxDepth, StringBuilder sb)
        {
            string name = SafeName(fd, itemId);
            float totalTime = SafeFloat(fd, itemId, HierarchyFrameDataView.columnTotalTime);
            float selfTime = SafeFloat(fd, itemId, HierarchyFrameDataView.columnSelfTime);
            float totalPct = SafeFloat(fd, itemId, HierarchyFrameDataView.columnTotalPercent);
            float selfPct = SafeFloat(fd, itemId, HierarchyFrameDataView.columnSelfPercent);
            float calls = SafeFloat(fd, itemId, HierarchyFrameDataView.columnCalls);
            float gc = SafeFloat(fd, itemId, HierarchyFrameDataView.columnGcMemory);
            float startTime = SafeFloat(fd, itemId, HierarchyFrameDataView.columnStartTime);

            sb.Append(relativeDepth).Append(',');
            sb.Append(Escape(name)).Append(',');
            sb.Append(totalTime.ToString("F4")).Append(',');
            sb.Append(selfTime.ToString("F4")).Append(',');
            sb.Append(totalPct.ToString("F2")).Append(',');
            sb.Append(selfPct.ToString("F2")).Append(',');
            sb.Append(((long)System.Math.Round(calls)).ToString()).Append(',');
            sb.Append(((long)System.Math.Round(gc)).ToString()).Append(',');
            sb.AppendLine(startTime.ToString("F4"));

            int count = 1;
            bool descend = maxDepth == -1 || relativeDepth < maxDepth;
            if (descend && fd.HasItemChildren(itemId))
            {
                s_ChildrenBuffer.Clear();
                fd.GetItemChildren(itemId, s_ChildrenBuffer);
                // Snapshot before recursing: WriteNode reuses the same static buffer,
                // which would invalidate this enumeration.
                var snapshot = s_ChildrenBuffer.ToArray();
                foreach (var childId in snapshot)
                    count += WriteNode(fd, childId, relativeDepth + 1, maxDepth, sb);
            }
            return count;
        }

        static float SafeFloat(HierarchyFrameDataView fd, int itemId, int column)
        {
            try { return fd.GetItemColumnDataAsFloat(itemId, column); }
            catch { return 0f; }
        }

        static string SafeName(HierarchyFrameDataView fd, int itemId)
        {
            try { return fd.GetItemName(itemId) ?? string.Empty; }
            catch { return string.Empty; }
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
