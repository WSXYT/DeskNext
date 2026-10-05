<div align="center">

<img src="src/DeskNest.App/Assets/desknext-mark.svg" width="64" alt="DeskNext" />

# DeskNext

**让文件有序，让工作更清晰。**

用桌面分类空间使用文件，用 AI 辅助判断，由你掌握每一次移动。

[English](README.md) · **简体中文**

[开始使用](#开始使用) · [模型包](https://github.com/WSXYT/DeskNext/releases/tag/model-laya-multilingual-fp32-v1) · [路线图](PLAN.md) · [许可证](LICENSE)

</div>

> **开发预览版。** 安全的手动本地文件整理闭环已实现，AI 部署和其余功能仍在开发。模型建议不会自动移动文件；当前不是已签名的正式发行版。

![DeskNext 工作台与隔离示例文件](docs/images/workbench.png)

*来自真实 Windows 应用窗口的截图，已裁去其他桌面应用；图中内容均为隔离示例。实际外观随系统、主题和显示缩放而变化。*

## 不只是把文件再堆进一个文件夹

- **按工作方式划分空间。** 使用托管空间收纳文件，或直接映射已有文件夹而不搬动内容。每个空间都能独立开窗，切换列表、网格、详细信息，并保存窗口位置。
- **熟悉的文件操作。** 打开、定位、预览、重命名、剪切粘贴、跨空间移动，以及对符合条件的历史操作撤销。外部导入先确认再移动；托管删除进入应用恢复区，移除映射引用不删除原文件。
- **更轻巧的桌面入口。** 通过收集箱胶囊收集待处理项目、跨空间搜索已登记文件，或从托盘返回。空间窗和胶囊默认不置顶。
- **看得懂、可选择的 AI 建议。** 本地 Laya CPU 推理与需明确授权的 Jev 云端预览二选一。文件名模糊、分类不适用时保留在待处理队列；采纳建议只是选择目标，不等于授权移动文件。
- **适合自己的外观。** 浅色、深色和系统主题，自定义强调色，跟随 Windows 强调色或从当前壁纸取色，以及仅改变应用显示的自定义图标。支持十二种界面语言，包括简体中文、繁体中文、英语和 RTL 阿拉伯语。

## 当前能力

| 功能 | 已实现范围 |
| --- | --- |
| 本地文件和目录整理 | Windows、macOS、Linux 上的日志化移动、身份核验撤销和确认导入 |
| 复制粘贴 | Windows NTFS 中已登记的项目；保留源文件，不宣称复制可撤销。Unix 支持剪切粘贴，尚无应用内复制发布 |
| 文件管理器交互 | 已检查 Explorer、Finder、Nautilus 的文件/目录交互；Linux 证据来自 X11/Xvfb，不是原生 Wayland |
| 智能分类 | 只读本地 CPU / Jev 预览、待处理项目建议及显式目标选择 |
| 模型安装 | 独立分发固定模型包；校验后安装到新目录，保存设置才启用 |
| 文件夹观察 | 手动开启、逐来源控制，仅把发现的项目加入待处理；不自动接管桌面 |

这些是有明确边界的能力，不代表所有系统版本、文件系统、虚拟文件载荷及故障场景均已支持。详细覆盖见[功能矩阵](docs/planning/feature-tracker.md)。

## 开始使用

### 从源码运行

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，然后执行：

```sh
git clone https://github.com/WSXYT/DeskNext.git
cd DeskNext
dotnet restore DeskNest.sln
dotnet build src/DeskNest.App -c Release -m:1 -p:UseSharedCompilation=false
dotnet run --project src/DeskNest.App -c Release --no-build
```

为兼容已有数据，内部解决方案、工程名及数据路径继续使用 `DeskNest.*`；产品显示名称统一为 **DeskNext**。

Windows x64、Linux x64 和 macOS ARM64 已有原生验证记录。其余架构、安装路径和系统集成分别跟踪。原生桥接模块的构建信息见[架构文档](docs/architecture.md)与[验证报告](docs/planning/p1-report.md)。

### 第一次整理

1. 完成首次引导；不配置模型也可以开始手动整理。
2. 新建托管空间，或者映射已有本地文件夹并登记其直接子项。
3. 将外部文件或文件夹拖入空间，核对来源与目标后确认导入。
4. 通过文件菜单或操作历史撤销符合条件的操作。文件已改变或证据不足时，不会静默覆盖。
5. 需要更小的桌面入口时，打开独立空间窗或收集箱胶囊。

### 可选的智能分类

**本地 Laya：** 获取独立的[多语言 FP32 模型包](https://github.com/WSXYT/DeskNext/releases/tag/model-laya-multilingual-fp32-v1)，约 813 MB / 775 MiB。在设置中选择 Laya，点击**下载模型**或离线安装 ZIP，再保存已校验的模型目录设置。服务器支持时，下载中断后可重试续传。也可选择已有的兼容解压目录。运行时无需 Python 或云端 API 密钥。

**Jev：** 在设置中选择 Jev，填写 TypeSafe 密钥，明确允许发送后再请求预览。请求包含文件名、项目类型和分类说明，不包含文件正文或绝对路径；API 使用可能收费。发送许可仅在当前会话生效；Windows 可显式选择将密钥保存在凭据管理器中。

### Windows 开发安装包

```powershell
powershell -NoProfile -File build/Publish-Windows.ps1 -Version 0.4.0-dev
```

这会生成**未签名的开发包**，不是正式安装程序。安装、版本并存和保留数据的卸载方式见[打包说明](build/windows/README.md)。模型权重独立分发，绝不提交进源码仓库。

## 本地优先，边界明确

- 文件始终是普通文件；托管空间与映射空间有明确区分。
- 身份或恢复核验失败时保留证据，不猜测应该移动或删除什么。
- 预览不执行文件内容；向外拖放只导出 Copy 文件引用，绝不删除源文件。
- 本地推理失败不会静默切换云端；工作区 JSON 不保存 API 密钥。
- 校验和固定模型字节，**不等于发布者签名，也不是分类质量证明**。

请备份重要数据。恢复日志不能代替备份，开发预览也尚不承诺掉电耐久性或无人值守自动整理。

## 开发进度

项目按 [P0–P7](PLAN.md) 推进。用户确认范围内的 P3 手动闭环已通过，P4 智能分类与部署正在完善。持续自动化与 Flow、其余效率工具和备份功能、签名后的跨平台发行仍在路线图中。

实现细节：[P3 手动整理](docs/planning/p3-report.md) · [P4 分类与部署](docs/planning/p4-report.md) · [功能覆盖](docs/planning/feature-tracker.md)。

反馈问题时，请附上系统、应用版本、复现步骤和脱敏后的诊断信息，不要上传私人文件、API 密钥或含敏感路径的恢复记录。

## 许可证与致谢

DeskNext 源码使用 **[GPL-3.0-only](LICENSE)**。第三方组件与模型资产保留各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

感谢 [DeskBox](https://github.com/Tianyu199509/DeskBox)、[PoggetCore](https://github.com/EnderMo/PoggetCore)、[Laya](https://github.com/NandhaKishorM/laya)、Avalonia 和 Microsoft Fluent UI System Icons。固定版本与适配说明见 [docs/upstream.md](docs/upstream.md)。
