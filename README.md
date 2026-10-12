# Profiler CSV Exporter

原创 Unity 编辑器工具：把 Profiler 窗口 CPU 层级视图里**选中的节点**（或其子树）导出为 CSV，同时自动复制到剪切板。

## 功能

- 自动跟踪 Profiler 窗口当前选中状态（Frame / Thread / 选中函数，0.5s 轮询）
- 导出范围可选：选中节点自身 / 选中节点 + 子树（深度可配：-1 无限、0 仅自身、N 层）
- 无选中时导出整帧调用树
- 每次导出同步复制到剪切板；也可只用剪切板按钮（跳过文件对话框）
- CSV 用 UTF-8 BOM 编码，Excel 双击直开无需导入向导
- 纯内置 Profiler API + 反射读取选中状态，零外部依赖

## 安装

UPM 一键导入（Unity 2022.3+）：

1. `Window → Package Manager`
2. `+` → **Add package from git URL**
3. 输入：

```
https://github.com/liushengliuyun/ProfilerCsvExporter.git
```

## 使用

1. 打开 Profiler 窗口并采集一帧数据
2. 菜单 `Tools → Profiler → CSV Exporter` 打开导出窗口
3. 在 Profiler CPU 层级里选中一个节点，导出窗口会显示当前选中状态
4. 点导出按钮：

| 按钮 | 行为 |
|------|------|
| 导出选中节点自身 | 只导出该节点一行 |
| 导出选中节点 + 子树 | 按深度配置导出子树 |
| 复制选中节点自身 / + 子树 | 只进剪切板，不弹文件对话框 |

深度参数：`-1` 无限子树、`0` 仅自身、`N` 最多 N 层（持久化）。

## CSV 列

```
Depth,FunctionName,TotalTime(ms),SelfTime(ms),TotalPercent(%),SelfPercent(%),Calls,GCAlloc(bytes),StartTime(ms)
```

## 工作原理与兼容性

选中状态通过反射读取 ProfilerWindow 内部字段（`m_FrameDataView`、TreeView 选中项），
不同 Unity 版本内部结构可能变化。若选中识别失效，可用菜单 `Tools → Profiler → Hunt Selection Id (deep)`
深度枚举内部 int 字段辅助排查。

> 注：工具 UI 为中文。

## License

MIT
