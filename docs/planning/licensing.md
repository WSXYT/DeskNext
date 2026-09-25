# 栖格 · DeskNest：发行许可

规划建议使用 **GPL-3.0-only** 标准文本，直接复用现代 DeskBox 代码；PoggetCore、Laya 及其他依赖保留各自的许可、署名与修改说明。不再采用此前的 AFPL 方案，不添加自定义禁售条款。

GPL 允许商业使用和收费分发；它要求的是分发时履行对应源码、许可与声明义务，并非“所有使用者都必须公开自己的私人修改”。因此采用 GPL 就不再承诺禁止第三方收费。本次提交将这一变更一并列入计划审阅，批准本许可方案即以 GPL 的标准边界替代先前禁售要求；当前不记为已获批准。

实施时仅做常规发行工作：

- `LICENSE` 使用标准全文；`THIRD_PARTY_NOTICES.md` 与 `docs/upstream.md` 记录依赖/固定版本及改动。
- 源文件保留必要声明，README 底部简短致谢，应用提供关于/许可入口，不强加主界面宣传。
- 发布与二进制对应的完整源码及必要构建/安装资料；不能只链接未修改的上游仓库。
- 原生运行时、模型权重、tokenizer、字体等按实际制品检查各自的分发要求。

参考：[GPL v3 正文](https://www.gnu.org/licenses/gpl-3.0.html)、[GPL 与收费](https://www.gnu.org/licenses/gpl-faq.html#DoesTheGPLAllowMoney)。
