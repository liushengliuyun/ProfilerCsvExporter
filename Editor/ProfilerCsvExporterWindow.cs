using System.IO;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEngine;

namespace ProfilerCsvExporter
{
    public class ProfilerCsvExporterWindow : EditorWindow
    {
        const float kRefreshIntervalSec = 0.5f;
        const string k_MaxDepthPrefKey = "ProfilerCsvExporter.MaxDepth";

        int m_MaxDepth = -1;
        ProfilerSelectionReader.Selection m_LastSelection;
        double m_NextRefreshTime;
        string m_LastExportPath;
        string m_LastExportSummary;
        Vector2 m_Scroll;

        [MenuItem("Tools/Profiler/CSV Exporter")]
        public static void Open()
        {
            var w = GetWindow<ProfilerCsvExporterWindow>(false, "Profiler CSV Exporter");
            w.minSize = new Vector2(380, 260);
        }

        void OnEnable()
        {
            m_MaxDepth = EditorPrefs.GetInt(k_MaxDepthPrefKey, -1);
            m_NextRefreshTime = EditorApplication.timeSinceStartup + kRefreshIntervalSec;
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < m_NextRefreshTime) return;
            m_NextRefreshTime = EditorApplication.timeSinceStartup + kRefreshIntervalSec;
            RefreshSelection();
        }

        void RefreshSelection()
        {
            m_LastSelection = ProfilerSelectionReader.Read();
            Repaint();
        }

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.LabelField("状态", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Frame", m_LastSelection.FrameIndex.ToString());
                EditorGUILayout.LabelField("Thread", m_LastSelection.ThreadName ?? "(unknown)");

                string selStr;
                if (!string.IsNullOrEmpty(m_LastSelection.FunctionName))
                {
                    string idStr = m_LastSelection.ResolvedItemId >= 0
                        ? $"(id={m_LastSelection.ResolvedItemId})"
                        : "(id 未找到 → 导出整树)";
                    selStr = $"{m_LastSelection.FunctionName}  {idStr}";
                }
                else
                {
                    selStr = "(无选中 → 导出整树)";
                }
                EditorGUILayout.LabelField("Selected", selStr);

                if (!string.IsNullOrEmpty(m_LastSelection.Warning))
                    EditorGUILayout.HelpBox(m_LastSelection.Warning, MessageType.Warning);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                int newDepth = EditorGUILayout.IntField("子树深度 (-1=无限, 0=仅自身, N=N 层)", m_MaxDepth);
                if (newDepth != m_MaxDepth)
                {
                    m_MaxDepth = newDepth;
                    EditorPrefs.SetInt(k_MaxDepthPrefKey, m_MaxDepth);
                }
                if (GUILayout.Button("刷新", GUILayout.Width(60)))
                    RefreshSelection();
            }

            EditorGUILayout.Space();
            bool canExport = m_LastSelection.FrameIndex >= 0;
            using (new EditorGUI.DisabledScope(!canExport))
            {
                // Row 1: file export (also copies to clipboard).
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("导出选中节点自身", GUILayout.Height(28)))
                        DoExport(maxDepth: 0, copyOnly: false);
                    if (GUILayout.Button("导出选中节点 + 子树", GUILayout.Height(28)))
                        DoExport(maxDepth: m_MaxDepth, copyOnly: false);
                }
                // Row 2: clipboard only (no file dialog).
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("复制选中节点自身", GUILayout.Height(24)))
                        DoExport(maxDepth: 0, copyOnly: true);
                    if (GUILayout.Button("复制选中节点 + 子树", GUILayout.Height(24)))
                        DoExport(maxDepth: m_MaxDepth, copyOnly: true);
                }
            }

            if (!string.IsNullOrEmpty(m_LastExportSummary))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(m_LastExportSummary, MessageType.Info);
                if (!string.IsNullOrEmpty(m_LastExportPath) && GUILayout.Button("在文件夹中显示"))
                    EditorUtility.RevealInFinder(m_LastExportPath);
            }

            EditorGUILayout.EndScrollView();
        }

        void DoExport(int maxDepth, bool copyOnly)
        {
            // Use ProfilerWindow.m_FrameDataView directly so the itemId we resolve
            // by name lives in the same view we hand to the writer.
            var fd = ProfilerSelectionReader.GetFrameDataView();
            if (!ProfilerSelectionReader.IsFdValid(fd))
            {
                m_LastExportSummary = "Profiler m_FrameDataView 不可用,请确保 Profiler 窗口已打开且有帧数据";
                return;
            }

            // If Read() resolved an item id, use it. Otherwise re-resolve now (covers the case
            // where Read ran on a stale fd and the user has since changed selection).
            int rootId = m_LastSelection.ResolvedItemId;
            string funcName = m_LastSelection.FunctionName;
            if (rootId < 0 && !string.IsNullOrEmpty(funcName))
                rootId = ProfilerSelectionReader.FindItemIdByName(fd, funcName);

            string target = rootId < 0 ? "整树" : (funcName ?? "root");
            string depthDesc = maxDepth == -1 ? "无限子树" : (maxDepth == 0 ? "仅自身" : $"深度 {maxDepth}");

            string csv;
            int rows;
            try
            {
                csv = ProfilerCsvWriter.BuildCsv(fd, rootId, maxDepth, out rows);
            }
            catch (System.Exception e)
            {
                m_LastExportSummary = "生成 CSV 失败: " + e.Message;
                Debug.LogException(e);
                return;
            }

            GUIUtility.systemCopyBuffer = csv;

            if (copyOnly)
            {
                m_LastExportSummary = $"[{depthDesc}] {target}: {rows} 行 已复制到剪切板";
                Debug.Log($"[ProfilerCsvExporter] Copied {rows} rows (target={target}, depth={depthDesc}) to clipboard");
                return;
            }

            string threadSlug = string.IsNullOrEmpty(fd.threadName) ? "thread" : fd.threadName.Replace(' ', '_');
            string defaultName;
            if (rootId < 0)
            {
                defaultName = $"profiler_frame{m_LastSelection.FrameIndex}_{threadSlug}";
            }
            else
            {
                string fn = funcName ?? "";
                foreach (char c in Path.GetInvalidFileNameChars())
                    fn = fn.Replace(c, '_');
                defaultName = string.IsNullOrEmpty(fn) ? $"profiler_frame{m_LastSelection.FrameIndex}" : $"{fn}_f{m_LastSelection.FrameIndex}";
            }

            string path = EditorUtility.SaveFilePanel("导出 Profiler 节点", "", defaultName, "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));
                m_LastExportPath = path;
                var fi = new FileInfo(path);
                m_LastExportSummary = $"[{depthDesc}] {target}: {rows} 行 → {Path.GetFileName(path)}  ({fi.Length / 1024.0:F1} KB)  已复制到剪切板";
                Debug.Log($"[ProfilerCsvExporter] Exported {rows} rows (target={target}, depth={depthDesc}) to {path} and copied to clipboard");
            }
            catch (System.Exception e)
            {
                m_LastExportSummary = "导出失败: " + e.Message;
                Debug.LogException(e);
            }
        }
    }
}
