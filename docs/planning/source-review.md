# 栖格 · DeskNest：源码复核与改造结论

> 只读调查结果，不是已修复或已跑测试。将上游已有能力与本项目新增要求之间的差距列清楚，避免“直接复用”掩盖安全和跨平台改造。

## 固定来源与纠错

DeskBox 本轮通过 `/commits/main` 核验：commit 为 `44e7a0d48769f8dbfb6d107715e0508918fc7d87`，其 tree 为 `9d93c751ec83b20559a05e80ddeaf0c7c39a4c42`。此前文档把前者称为 tree SHA 不准确，本次更正。下表 DeskBox 路径除测试外都相对 `src/DeskBox/Services/`，源码链接统一使用固定 commit，不跟踪 main。

## DeskBox：已确认的改造点

| 编号 | 源码证据 | 对计划的影响 | 必须覆盖的验证 |
| --- | --- | --- | --- |
| S01 | `DesktopOrganizationTransaction.cs:381–414`：按 `Path.GetPathRoot` 区分卷；跳过 UNC；注释明确不递归计算目录体积 | 保留事务骨架，但卷判定必须使用平台卷/设备身份。Linux 不同挂载点都返回 `/`，不能因此判为同盘；跨盘目录需计算完整复制预算，未知空间不得伪装通过 | 同盘、不同挂载点、挂载目录/共享、跨盘目录、复制中目标空间耗尽 |
| S02 | `DesktopOrganizationTransaction.Restore.cs:461–481`：恢复需目标对象 ID/卷号及 size/mtime；无身份则禁止自动恢复 | 保留这个保守规则；增加 Windows/macOS/Linux 身份适配，不能以同路径代替身份。物理移动成功但回执未落盘的窗口需要“待人工核对”，不是承诺自动回滚一切 | 杀进程发生于移动后/回执前、目标同名替换、旧日志无 ID、外部编辑、原位置已占用 |
| S03 | `DesktopOrganizationRecoveryStore.cs:35–49` 丢弃 `LoadWithResultAsync` 的 Source，只返回 Value；`ResilientJsonStore.cs:3–13,54–111` 区分 DefaultMissing/DefaultAfterFailure | 栖格恢复入口必须保留“确实不存在”和“损坏后无可用备份”区别。后者阻断新文件变更并展示可恢复位置，保留持久化故障标记，不能下一次启动因只剩 .corrupt 文件就当作无历史 | 主/备份同时坏、只读、隔离后重启、手动核对后明确解除阻断；配置不能自动空白覆盖 |
| S04 | `DesktopAutoOrganizationWatcher.cs:80–88,498–499,757–767`：只监听根目录、每事件启动异步处理、稳定探测明确排除目录；`DesktopOrganizationScanner.cs:156–172` 把 Folder 作为排除项 | 本项目需要多目录与文件夹整体分类，不能仅取消 Folder 排除：新增有界队列、根目录候选去重、目录稳定性检查、内部临时文件/链接检查；大或持续变化的目录转人工，不拆内部项目 | 文件夹持续写入、解压/复制/下载、深层改动、重复源目录、事件风暴、白名单目录内任意变更 |
| S05 | `DesktopAutoOrganizationSuppressionRegistry.cs:26–47,86–111,141–158`：按路径短期缓存、TryConsume 一次即删除、完成指纹仅用 FileInfo | 复用操作关联思路，扩展文件/目录身份与 generation；必须识别同一操作的多次迟到事件及撤销后的重启，不能全路径 TTL 永久压住后来同名新文件 | 一次移动多事件、完成前事件、目录撤销、重启重扫、同名替换仍可处理、Flow 与监测循环 |
| S06 | `DesktopAutoOrganizationBaseline.cs` 保留完整枚举；`DesktopAutoOrganizationWatcher.cs:98–125,185–265` 先开 watcher、缓冲事件并双次完整捕获；重扫失败保留旧基线 | 直接复用这些已经解决竞态的能力，不退化为“列一次文件”。多目录各自有基线/健康状态；新产品明确停止期间新文件的处理方式，不能仅依赖创建时间推断跨平台新旧 | 初始化期间新建/删除/重命名、枚举中权限失败、溢出重扫、系统时钟变化、应用关闭期间新增文件 |
| S07 | `DesktopAutoOrganizationStateMachine.cs:202–217` 只暂停 Pending/Settling，Processing 留给已开始移动；watcher 自己在实际移动前重查状态 | AI 推理不能直接占用原本代表不可随意打断文件操作的阶段。增加推理/等待用户/待提交状态，暂停/改 Provider/改分类版本使推理结果失效，已开始文件操作只安全收尾 | 点击暂停后迟到推理绝不启动新移动；取消期间已完成项正确落盘；用户动作不被模型覆盖 |
| S08 | `DesktopOrganizationTransaction.cs:7,43–47,105–116,219–241`：进程内全局锁，settings 先落盘、history 作为提交证据后落盘，再 Clear journal；`RecoveryStore.cs:72–86` 先删备份再删主日志 | 保留提交先后顺序和单执行器；模型推理/扫描不占事务锁，日志与物理移动仍在操作锁保护下执行且不阻塞 UI。另加单实例与数据目录写锁；配置迁移/导入备份暂停队列并等事务安全结束 | 各持久化点故障、双开同一数据目录、退出/升级期间事务、清日志一半崩溃、回滚失败后重启 |

补充：`RevalidateSource`（Transaction.cs:417–439）只比较目录根 mtime 或文件 size/mtime；这是快速探测，不是“内容完整性证明”。跨盘删除源前应校验复制结果，并重新检查源/目录清单；符号链接、云占位、网络盘、无法取得可靠身份/一致快照时转保守处理。对外部进程持续改写不承诺无条件安全自动移动。

## 直接保留、避免重造的部分

- `BeginPending/TryTransition/MarkDeferred` 的 generation 检查和重试调度；仅扩展 AI 阶段。
- `ReconcileBaselinePair` 的双枚举与事件缓冲，不另造简单但有竞态的 watcher。
- `ExecuteAsync` 的已完成回执、部分成功结果、持久化提交顺序和 `UndoAsync/RecoverPendingAsync` 的身份门槛。
- `ResilientJsonStore.LoadWithResultAsync` 的明确来源状态；修复调用方丢失来源，而不是替换整个保存工具。
- 上游对应测试尽量迁入；新增故障测试对准本项目扩展，不以截图库代替文件系统测试。

## 源码链接

- 固定 commit：https://github.com/Tianyu199509/DeskBox/commit/44e7a0d48769f8dbfb6d107715e0508918fc7d87
- 事务：https://github.com/Tianyu199509/DeskBox/blob/44e7a0d48769f8dbfb6d107715e0508918fc7d87/src/DeskBox/Services/DesktopOrganizationTransaction.cs
- 恢复：https://github.com/Tianyu199509/DeskBox/blob/44e7a0d48769f8dbfb6d107715e0508918fc7d87/src/DeskBox/Services/DesktopOrganizationTransaction.Restore.cs
- 日志存储：https://github.com/Tianyu199509/DeskBox/blob/44e7a0d48769f8dbfb6d107715e0508918fc7d87/src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs
- 监测：https://github.com/Tianyu199509/DeskBox/blob/44e7a0d48769f8dbfb6d107715e0508918fc7d87/src/DeskBox/Services/DesktopAutoOrganizationWatcher.cs
- 本地推理源码和对照方案：见 [inference-validation.md](inference-validation.md)。

## Pogget 集成：交叉审查采纳项

只读审查 run `d3d0014f-661b-4c4e-96bd-c8858389ba9d`，没有构建/测试。以下 blob 为文件标识，实施时统一固定仓库 commit。

| 编号 | 源码证据 | 改造与验证 |
| --- | --- | --- |
| S09 | `PoggetCoreManager.cpp:6–107,121–140`（`ba294e34d29e753f3ef06bfd5f5ff097f628846a`）及头文件引入 Meta/Storage；VinaStorage 又依赖 VinaBuilder/parser/ordered_map，部分 cpp 依赖外部 framework.h | 抽取布局 DTO/纯计算编译单元，不为布局带入整个宿主。配置传快照、计算可改自身输入副本；编译独立布局测试及分页/列表/内联 golden，不假定所有模式同时支持分页 |
| S10 | `PoggetCoreItemManager.cpp:8–32,73–131`（`293f25e759f87266e4b930452600e7a1026ed7c0`）锁内通知、vector 元素裸指针、插入/刷新重编号 | 通知移锁外、返回复制快照，稳定 ID 与排序序号分离；回调重入、扩容和延迟拖放不能死锁/指错文件 |
| S11 | `Flow/FlowRuntime.hpp:60–93,545–585,1126–1139`（`536e3e0bf5ac5564dd105b6b1bf1461d962918d9`）析构 join，但节点/同步提示没有完整取消；adapters 在锁外调用，不能误记成这里也锁内回调 | 新增显式停止、取消提示、回调 generation 与排空/解绑协议；delegate 保活到原生停止；UI 线程不阻塞等待正在等待 UI 的 worker；异常不跨 ABI |
| S12 | 同一 Runtime `:796–830,867–1005,1023–1095`，move/rename 直接用 HistoryFileSystem、普通文件检查；失败多数写 success=false 后继续；手动执行读传入路径 | 必须接统一宿主 executor，返回实际路径/操作 ID/部分成功/取消结果；保留“失败输出可接 if 分支”的显式语义，取消/安全拒绝停止当前运行；手动执行只用已批准的固定 Flow 定义，不绕过路径权限 |
| S13 | 同一 Runtime `:104–109,371–400,545–577,645–667` 外部 tick 驱动，intervalMs/2600 按 tick 计数，定时按本地分钟匹配，去重只在内存 | 调度交宿主单调时钟，持久化上次触发槽和 run ID；默认错过不补跑、重复小时只触发一次，允许用户显式设置“仅补最近一次”；休眠/时区变更/重启与慢动作压测 |
| S14 | 同一 Runtime `:587–643,724–793,1234–1239` 单 Flow 最多 256 事件却推进全量基线 | 宿主有界积压/溢出重扫，未处理变化不可随基线被消掉；测 257/1000 项单批变化。Core 统一提供带 revision 的分类快照和已保存 Flow 定义，原生不另起目录扫描/主数据源 |
| S15 | `FlowStorage.hpp:521–557,579–704,827–920`（`c87b9ca8a99df67ea9178c629ff70551b29bc1bc`），非 Windows 只见 flush/rename 而无 fsync/目录同步；`PoggetStorageProvider.cpp:35–182`（`1fb937e434a26cbb3e353ccf4a2d12e485cf778b`）未完整保存分页/tab字段 | Vina 限兼容导入，Core 唯一写入者；字段 round-trip 清单与保存失败传播。各系统单独测试耐久性；不能把 flush/rename 一概称作断电安全 |
| S16 | `FlowDataflow.hpp:443–467,509–542`（`3bca144d7d0e68787d667e32e791bfccc4b95967`）顺序作用域；Runtime `:182–200,476–528` 非 Windows UTF-8 字节直接提升 wchar | 编辑器是顺序步骤/嵌套分支而非任意 DAG；复用端口/作用域校验。统一 UTF-8/Unicode 转换，测中文/emoji/UTF-16 代理对，不只测 C ABI 参数编码 |

## 平台发行：文档核查采纳项

只读审查 run `57163b51-3a73-4e73-b627-4f4057a5b177`。来源证明约束，不代表当前打包产物已经验证。

- [.NET 单文件机制](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)：自包含不等于单文件/无 native 依赖；worker 必须在 UI/托盘/单实例锁/监测初始化前分流。首发采用目录式自包含包，无显示服务也能启动推理。
- [ORT 依赖说明](https://onnxruntime.ai/docs/install/) 与[同名 native 包历史问题](https://github.com/microsoft/onnxruntime/issues/15953)：按版本/RID/后端变体隔离原生资产，切换变体启新 worker；应用私有依赖闭包不依靠开发机 CUDA/Python。历史 issue 只证明风险，不充当当前版本复现证据。
- [Avalonia 支持平台](https://docs.avaloniaui.net/docs/supported-platforms)：框架支持等级不等于整个产品支持。逐 RID 核实 .NET、UI、ORT、tokenizer、Pogget 和真实安装报告；未经验证的 ARM64/GPU 包不写“已支持”。
- [Linux 后端](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)：明确 X11/XWayland 基线，不把原生 Wayland 实验后端当无差别替换；双向拖放、虚拟文件、Move/Copy 协商仍需实测。
- [Secret Service 锁定](https://specifications.freedesktop.org/secret-service/latest/unlocking.html)：无服务/已锁/用户取消解锁时只存会话内存或不启云端，不退为明文 JSON，不伪称 key 已保存。

## Gemini UI 审查：采纳与纠偏

UI 专项 `google-me/gemini-3.8-flash-high`，run `f2e96345-78d7-48a8-b714-ccf650571015`；仅规划，无 UI 实现。

- 采纳深浅色中性色/鸢尾强调色、托管/映射文字标牌、非模态待决抽屉、五步可恢复 OOBE、路径 LTR 隔离、键盘/可访问性要求。名称不由审查代理定案；后续用户选择中文「栖格」、英文「DeskNest」，以此为准。
- 保持用户已定边界：引擎仍为 Jev/Laya 二选一，未就绪时允许先手动整理；模板仍为办公/研发/创意/完全自定义四项。不采纳新增第三个引擎、极简第五模板、统一把待决文件搬暂存区等扩范围建议。
- 映射只是索引已有路径，不等于创建操作系统软链接；类别保存继续用统一存储，不引入报告里假设的分类 DB 或新服务抽象。
- 对比实际 Avalonia 源码确认 `ListBox.cs:24–27,64–66` 默认 `VirtualizingStackPanel`（blob `9307bf9b444c2671ebe66779936eefaac19e3af1`），`ItemsControl.cs:29–32` 默认普通 StackPanel（`8b7a6ca06d6ca2880ee0168eb7e59813f54e46da`）。不采纳未验证的“内置 VirtualizingWrapPanel”、特定版本移除 API 和固定 60/80 控件上限；网格需单独验证 viewport 实现。
- 性能按可视项+有界 overscan 衡量，不因分辨率大或高密度出现 >80 可视项就误判。Mica/Acrylic 只作系统允许的增强，有不透明回退；不承诺各平台同等背景模糊。

这些结论已进入主计划的依赖拆分、状态/文件安全、UI/OOBE、发布矩阵和验收步骤；没有把子代理建议未经核查直接当作源码事实。

