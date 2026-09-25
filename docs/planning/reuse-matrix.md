# 源码复用与功能验收矩阵（规划草案）

> 本文件配合 `PLAN.md`，不是实现完成报告。两仓库此前已通过抓取工具克隆阅读；临时缓存已失效，本轮通过 GitHub Contents API 只读复核。执行开始时重新固定上游 commit、保留来源和许可证，不追踪浮动 main。未安装/编译/运行任何上游软件，不能宣称全功能或全平台已经验证。

## 复用路线

项目名称为「栖格 · DeskNest」，按现代 DeskBox C# 核心、PoggetCore C++ 布局/Flow 与原生 ONNX 推理路线实施。许可建议采用标准 GPL-3.0-only，简短发行说明见 [许可记录](licensing.md)；不在功能矩阵展开历史调查。

保留 C# 非 UI 代码和测试，统一 Avalonia UI；PoggetCore/Laya 保留 Apache-2.0 声明。Core 是唯一主状态与文件执行器，native 只消费版本化快照。R01–R14 均为待实施项，具体源码差距与测试见 [S01–S16 复核](source-review.md)。

| 编号 | 上游源码（相对各自仓库） | 复用/改造方式 | 新项目落点 | 验收重点 |
| --- | --- | --- | --- | --- |
| R01 | DeskBox `src/DeskBox/Services/DesktopAutoOrganizationStateMachine.cs`，以及 `DesktopAutoOrganizationBaseline*`/`Watcher`/`SuppressionRegistry` | 保留 generation/退避/双枚举基线；补多目录、文件夹稳定性、推理中/待决/待提交与监测epoch | `src/DeskNest.Core/Organization/` | 延迟结果、暂停/重启、监测溢出、深层写入、白名单和自生成事件 |
| R02 | DeskBox `Services/DesktopOrganizationRuleResolver.cs`（以下 Services/Models 均相对 src/DeskBox） | 保留 Resolve/FindConflicts/AssignExtensionExclusively，规则与 AI 优先级显式配置 | `src/DeskNest.Core/Organization/` | 冲突、停用分类、白名单 |
| R03 | DeskBox `Services/DesktopOrganizationTransaction.cs`、`.Restore.cs`、`DesktopOrganizationRecoveryStore.cs` | 保留日志/回执/commit，解耦宿主；补卷/文件身份、缺失与损坏区分、持锁重验；统一所有文件入口 | `src/DeskNest.Core/Storage/` | 跨真实卷/挂载点、缺回执、双损坏、外部修改、取消/部分恢复 |
| R04 | DeskBox `Services/ResilientJsonStore.cs` | 复用备份/坏文件隔离/流式保存，替换日志与平台错误分类 | `src/DeskNest.Core/Storage/` | 主配置/备份损坏、只读/写盘失败不丢数据 |
| R05 | DeskBox `Models/Todo*.cs`、`QuickCapture*.cs`、`Services/TodoRecurrenceService.cs` | 保留模型/重复规则；通知/剪贴板移平台层 | `src/DeskNest.Core/Productivity/` | 月末/夏令时、撤销、附件关系 |
| R06 | DeskBox `Services/LocalizationService.cs`、`Strings/*.json`、`tests/DeskBox.Tests/Localization*Tests.cs`（测试相对仓库） | 复用十二语言和资源契约，新增 Avalonia 动态绑定/复数/RTL及新功能翻译 | `src/DeskNest.Core/Localization/`、`src/DeskNest.App/Strings/` | 热切换、缺键、占位符、长文案 |
| R07 | DeskBox `Services/WidgetSnapCalculator.cs`、`WidgetTopologyLayoutService.cs`、`Models/WidgetGroupConfig.cs` | 保留布局状态/算法，窗口句柄/DPI/屏幕事件换适配 | `src/DeskNest.Core/Layout/`、`src/DeskNest.Platform/` | 多屏插拔、DPI、组移动、休眠恢复 |
| R08 | DeskBox `Services/CloudBackupService.cs`、`DeskBoxDataBackupService.cs`、附件健康服务 | 保留快照/校验/合并恢复/墓碑规则，替换凭据与 UI 回调 | `src/DeskNest.Core/Backup/` | 断网、部分上传、恢复前快照、删除项不复活 |
| R09 | DeskBox `Services/EverythingSearchService.cs`、搜索模型/排序/选择策略 | 保留 Windows Everything/结果策略；其他平台替换来源 | `src/DeskNest.Core/Search/`、`src/DeskNest.Platform/` | 增量结果、单来源失败隔离、框选、历史收藏 |
| R10 | DeskBox `Services/WeatherService.cs`、天气/时光/音乐模型、`native/deskbox-native/src/`（原生相对仓库） | 复用数据逻辑和 Windows 原生模块，UI 换 Avalonia，其他平台不加载 Windows DLL | `src/DeskNest.Core/Widgets/`、`native/deskbox-native/` | 缓存/离线/媒体能力、平台退化 |
| R11 | PoggetCore `PoggetCoreManager.hpp/.cpp`：`CalculatePositionsCore` 与布局结构 | 抽纯布局编译单元/DTO，配置传快照；去 framework.h/Vina/Meta依赖，宿主稳定ID与排序序号分离 | `native/pogget/`、`native/pogget-bridge/` | 独立编译；模式条件、分页/合并/锚点；复制快照、锁外回调、无裸指针导出 |
| R12 | PoggetCore `Flow/FlowModules.hpp`、`FlowDataflow.hpp`、`FlowRuntime.hpp`、`Storage/Flow*.hpp` | 复用schema/顺序端口校验；动作接R03，宿主发布定义/调度/快照；修事件溢出、Unicode、停止/回调寿命 | `native/pogget/`、`src/DeskNest.Core/Automation/` | 10类节点、失败分支、257/1000事件、DST/休眠/重启、取消/退出、目录与文件语义区分 |
| R13 | PoggetCore `PoggetCoreContainer.hpp`、`PoggetStorageProvider.*`、`PoggetHistoryFileSystem.hpp` | Vina仅兼容导入；配置字段/安全策略按差异移入Core；补未落盘的分页/tab，不另起撤销栈 | `src/DeskNest.Core/Import/`、`native/pogget/` | 字段round-trip、保存失败、跨平台耐久性；删除历史不删用户文件 |
| R14 | Laya `laya-ts/scripts/export_onnx.py`、`laya/common.py`、`laya/agent.py`、`laya-ts/src/common.ts` | 固定同commit与checkpoint、审计权重加载；原生tokenizer，C#移植预/后处理；CPU优先、后端资产隔离 | `tools/model-export/`、`src/DeskNest.Inference/` | [A–D全链路](inference-validation.md)：输入张量/概率/决策/文件动作、质量集、安装包与资源证据 |

本轮逐段检查了 R01 监测/基线/抑制、R03 事务/恢复、R04 存储、R11–R13 布局/Runtime/Dataflow/Storage 的关键路径，以及 R14 分词/导出/校准；R05/R06 读过关键服务，其他功能仍需迁入时补读。三路只读审查已合并到源码复核附录，所有结论均不等于已构建/测试通过。

## 全功能覆盖分组（不是仅交付分类 MVP）

| 编号 | 必须覆盖的行为 | 来源/负责模块 | 完成证明 |
| --- | --- | --- | --- |
| F01 | 托管格子、映射目录/虚拟文件、真实路径显示、打开/定位、图标与列表、文件名/密度/排序/手动次序、内联目录 | 两项目 / Spaces | 导入、重启、外部改名、路径失联回归 |
| F02 | 复制/剪切/粘贴、拖入拖出、跨应用虚拟文件/URL 导入、快捷方式、应用图标接收文件、回收站/永久删除确认、原生菜单/预览 | DeskBox / Platform | Explorer、浏览器、聊天软件拖放实测；非 Windows 等价动作 |
| F03 | 手动叠放与自动分组、弹窗/内联展开、格子组合/拆分/标签与滚轮切换、Pogget 横纵分页/连续流 | 两项目 / Layout | 各模式、空组、拖动中切换、屏幕边缘与焦点 |
| F04 | 胶囊/胶囊条、悬停/点击展开、方向、重排、隐私摘要、锁定/淡出/标题自动隐藏、桌面嵌入/临时唤起 | 两项目 / Surfaces | 拖放/菜单期间不收起；钩子恢复；平台限制可见 |
| F05 | 一键扫描预览、已有/新分类选择、自动新文件/文件夹监测、白名单、任务队列、撤销/恢复 | 两项目 + 新 AI / Organization | 稳定性与崩溃安全测试；不误搬基线文件 |
| F06 | Pogget Flow 全部上述 10 类节点、端口绑定/条件分支、导入导出、启动停止、日志与手动运行 | Pogget / Automation | 每节点 golden fixture 和组合流程；启用导入流程须确认 |
| F07 | 待办截止/提醒/重复/子步骤/颜色/Markdown/附件/筛选/批量；随记文本/链接/图像/文件/置顶/纸张/专注 | DeskBox / Productivity | 数据持久化与动作测试，键鼠及拖放附件 |
| F08 | 统一搜索文件/目录/应用/设置/笔记/待办，Everything 检测/路径/高级语法，多选、详情列、收藏历史、全局热键 | DeskBox / Search | 多源增量、关闭释放、不重复全盘建索引 |
| F09 | 时光、农历/节气/节日、背景轮播；天气实时/小时/多日与两种皮肤；音乐布局/会话/进度/模式/音量 | DeskBox / Widgets | 各尺寸截图、缓存/离线、媒体提供者能力判断 |
| F10 | 多屏拓扑/DPI/吸附/组移动、主题/透明度/边框/阴影/字体/图标/单格覆盖、动效、省资源/均衡/自定义 | 两项目 / Layout & Settings | 混合 DPI、减少动态效果、长时间空闲与唤起 |
| F11 | 开机自启/托盘/单实例、更新与安全安装、配置快照、ZIP/WebDAV 备份、选择性合并恢复、诊断脱敏、附件/孤立目录维护、卸载保留文件 | DeskBox / Lifecycle & Backup | 真安装包升级/回退/卸载与损坏恢复 |
| F12 | 12 语言与可访问性；新的 AI、OOBE、部署、工作流 UI 同样覆盖 | DeskBox 十二语言资源 + 新 UI / i18n | 资源完整性、复数/RTL、屏幕阅读器与键盘测试 |
| F13 | Jev、本地 Laya 无 Python 一键部署、两个特殊候选、低置信度兜底、手动纠错/新分类 | 新增 / Inference & Organization | 原生对照、故障注入、隐私/权限测试 |

“全部功能”以固定源码版本的 UI 设置项、菜单、模型字段、服务和测试交叉盘点后签核为准。缺失项目继续加行，不因 README 未提及而省略。不在公开仓库中的 Pogget 完整 UI 不能声称已经复用；实现其已公开核心能力并单独标记无法核对的宿主特性。

## 主要资料

- DeskBox：https://github.com/Tianyu199509/DeskBox ，本轮核实 commit `44e7a0d48769f8dbfb6d107715e0508918fc7d87`，对应 tree `9d93c751ec83b20559a05e80ddeaf0c7c39a4c42`；P0统一冻结导入清单。
- PoggetCore：https://github.com/EnderMo/PoggetCore 。
- Laya：https://github.com/NandhaKishorM/laya 、https://huggingface.co/convaiinnovations/laya 。
- Jev：https://docs.typesafe.ai/api 、https://docs.typesafe.ai/confidence 。
- 原生 C# ORT：https://onnxruntime.ai/docs/get-started/with-csharp.html 。
- GPU 限制：https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html 、https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html 。
- UI 平台边界：https://docs.avaloniaui.net/docs/overview/supported-platforms 。
