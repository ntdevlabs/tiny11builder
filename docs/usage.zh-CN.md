# Tiny11 Builder：用法与开发（简体中文）

[English](usage.md) | 简体中文

以下命令均从仓库根目录运行。下载、视频与截图请见[项目主页](../README.zh-CN.md)。

可通过侧边栏与构建成功页中的 **支持开发** 使用可选的[项目支持](support.zh-CN.md)。支持选项只有点击才会打开，绝不会中断构建，也绝不会解锁任何功能。支付发生在所选服务商的网站上，而非应用内部。

**WinUI 3 桌面应用，配备 C# 映像构建器。** 其分步布局参考了 [S9 WoA 安装器](https://github.com/ntdevlabs/exynos9810-woa)。它不封装、也不执行 PowerShell 构建脚本。为保持兼容，原始脚本原样保留在本文档下方。

## 应用功能

| 模式 | 行为 |
| --- | --- |
| Tiny11 | 固定的原始风格移除集：预装应用、Edge/WebView2 与 OneDrive 二进制文件、硬件检查设置、保留存储与自动加密设置。保留 Windows 更新、Defender、WinRE、应用商店和共享运行时。选择自定义模式可编辑移除项。 |
| Tiny11 Core | 在重置组件基础前应用更改。保留 common-controls/GDI+/CRT/维护文件，删除其他 WinSxS 组件文件夹，移除 WinRE 并禁用 Windows 更新与 Defender。这些更改不可逆。仅用于测试虚拟机。 |
| 自定义（Custom） | 初始不选择任何移除项。可选择单个移除组，或列出预装应用、受保护的系统应用、可选功能、语言功能、CBS 包、服务、驱动与 WinSxS 组件文件夹。功能与服务还提供仅禁用选项。Core 的不可逆移除是单独的未勾选选项，带有相同的警告与确认。 |

预设采用原始风格，但并非脚本的逐命令复刻。Tiny11 在不使用 `/ResetBase` 的情况下清理被取代的组件，保留卸载现有更新的能力。Core 则会重置组件基础，保留额外检测到的运行时/维护文件族，并移除 WinRE 但不创建原始脚本中的空占位文件。这些兼容性选择不因可靠性更新而改变；进一步精简需要单独的 Setup/启动测试。

六步向导提供：

- 全宽欢迎页与可点击的构建模式卡片。选择卡片即设定模式并打开 ISO 选择。窄窗口下卡片自动堆叠；页面向下滚动时，淡色的大号数字（1-6）会淡出。返回键位于左上角，支持 Alt+Left。所有前进操作（包括开始与构建）位于右下角。没有页脚条或多余的步骤计数。
- 一键下载 Microsoft 最新发布的 Windows 11 ISO，使用主机架构与显示语言。不支持的语言会回退到英文并显示明确的状态提示。无需输入 URL 或保存对话框；下载保存到所选工作区的 `downloads` 目录。Microsoft 的可用性/地区限制会被如实报告而不是绕过。
  瞬时连接重置与服务器繁忙响应会在可见状态下进行有界重试。安全握手重置后，应用可用 TLS 1.2 重试，仍使用 Windows 证书验证。证书、DNS、代理与权限错误不会被绕过。失败提示会链接到 Microsoft 下载页；本地 ISO 仍是可选方案。中断的 ISO 下载会保留部分文件以供重试/续传。
- 官方 Microsoft ISO 下载带速度/进度显示、基于强 ETag 的可续传部分文件、ISO 结构检查，以及可选的发布 SHA-256 校验。只接受 Microsoft 的 ISO 主机，包括每一次重定向。过期的查询令牌不会被持久化；续传元数据只保存 Microsoft 资源的主机/路径、ETag 与长度。
- 本地 ISO 与解压后的安装介质输入、WIM/ESD 检测、版本选择，以及自动只读原生 ISO 挂载。找到 Windows 11 Pro 时优先选择；否则选择索引 1。恢复的配方若其保存的版本仍存在，则保留该版本。
- 三张构建模式卡片：Tiny11、Tiny11 Core 与自定义。单独的保留/移除控件仅在自定义模式显示。Core 需要先通过明确的同意对话框；取消则不批准 Core。
  完整映像清单需要先显示专家警告，并在构建前进行单独的风险确认。
- Core 与自定义的 WinSxS/组件负载移除始终使用旧版 Windows 安装程序，包括 24H2 及更新介质。私有 Setup 启动映像先运行 `wpeinit.exe`，再运行其匹配的 `sources\setup.exe`（如存在 `setup_old.exe` 则用它），而不是较新的 SetupHost 流程。
  这是自动行为，不是绕过硬件检查的选项；不会下载其他 Windows 版本的安装程序。缺少经典可执行文件的介质会明确报错失败。恢复的 Core 作业同样会应用此更改，即使其最终安装映像已经压缩完成。
  这通过 [Winpeshl.ini](https://learn.microsoft.com/zh-cn/windows-hardware/manufacture/desktop/winpeshlini-reference-launching-an-app-when-winpe-starts) 配置启动 ISO 的 WinPE shell；它不是就地升级的变通方案。
- 可选的来自匹配 `sources\sxs` 的 .NET 3.5、紧凑安装，以及尽力而为的本地账户设置。不会注入任何账户、密码、产品密钥、自动登录或 EULA 接受。
- 自动映像工具检测与兼容版本检查。oscdimg 在本地查找，或从 Microsoft 符号服务器以精确 SHA-256 固定值下载。没有工具选择控件，也不会更改脚本执行策略。
- 带开关的图示化安装选项卡片、结构化评审，以及可展开的构建阶段（含待处理、运行中、已完成、已跳过与失败的操作）。应用、功能与能力移除会显示确切的标识。控制台风格的进度条只写入磁盘日志，不进入界面。
  总进度跟随阶段边界，而非时间估算。条目按其所属阶段分组；失败的条目不会被重复计为其阶段的第二次失败。未开始的工作在停止后显示 **未运行**。
  构建页将总进度、当前操作、输出详情与可展开的操作历史分区显示，区块之间留有更多空间。
- 架构正确的 setup XML、x64 与 ARM64 的 UEFI 启动介质、源中包含时 x64 的 BIOS 启动项，以及恢复 ESD、最大 WIM 或快速 WIM 压缩。
- 配方导入/导出、实时日志、安全边界取消、持久化作业状态、**继续构建** 的已验证保存进度、输出 SHA-256、JSON 报告，以及独立的中断工作区丢弃/恢复。

## Turbo 模式与更小的映像

设置页有独立的 **Turbo 模式**、**移除冗余映像数据** 与 **省略 .NET 3.5 安装负载** 开关。所有默认关闭，因此现有配方与固定的 Tiny11/Core 移除集保持原有行为。

| 设置 | 改变什么 | 取舍 |
| --- | --- | --- |
| Turbo 模式 | 序列化的功能/能力批次带状态验证、最多八个复制工作线程以及更少的日志 I/O。 | 更多 CPU/I/O 与临时空间占用；实际收益取决于映像与主机。 |
| Turbo + Core 或快速 WIM 输出 | 使用快速压缩的可写中间 WIM，而不是把大量 Core 更改重新压缩进 LZX 工作映像。 | 初始转换可能更慢且工作 WIM 更大。最终压缩仍按所选执行。 |
| 恢复 ESD | 最终版本使用实体恢复压缩。 | Microsoft 工具链下最小的输出，通常也是压缩最慢的阶段。Turbo 不会静默替换它。 |
| 最大 WIM | 强力且可挂载的 WIM 压缩。 | 更易于维护，通常大于恢复 ESD。 |
| 快速 WIM | 更快的最终压缩。 | 更大的 ISO；构建时间比存储空间更重要时使用。 |
| 移除冗余映像数据 | 移除已下载的更新缓存、临时文件、归档的 CBS/DISM 日志、崩溃转储与 Windows 错误报告归档/队列。 | 记录的是逻辑移除字节数；它们不是对压缩后 ISO 节省量的预测。 |
| 省略 .NET 3.5 安装负载 | 不把 `sources\sxs` 暂存进输出 ISO。构建期间仍可从原始源启用 .NET 3.5。 | 事后添加需要其他匹配介质或 Windows 更新；Core 无法事后添加功能。 |

构建与清单默认使用常规挂载，Turbo 亦然。工具版本变化时不会自动尝试延迟挂载（`/Optimize`）：在这台电脑上一次失败的生产探测曾耗时超过十二分钟。开发选优开关保留了已知错误回退与带版本的 `mount-optimization.json` 咨询缓存。无效或不可访问的咨询缓存会以诊断方式禁用延迟挂载，而不是中止构建。Turbo 不会针对单个已挂载映像并行化维护操作、不会禁用主机杀毒软件、不会更改 OS 电源设置，也不会移除完整性检查。批次会跟踪每个功能并回读其状态；失败的批次按失败报告，而不是按单个成功报告。

能力移除在标准与 Turbo 模式下都会验证。**卸载挂起（Uninstall Pending）** 表示 Windows 已接受卸载但仍需在首次启动时完成。可维护的构建会保留挂起的负载并在可见警告下继续；每能力报告与操作详情会标注 **deferred（延迟）** 而非缺失。这些移除挂起期间，被取代组件的清理与选定的 Edge/WebView2 文件移除会被跳过。**安装挂起（Install Pending）**、仍已安装、未知、缺失或移除后冲突的状态仍视为错误。Core 与自定义的组件存储精简仍拒绝挂起的移除。

这种情况可能出现在之前维护过或 UUP 转换的介质上，包括已集成更新与 .NET 3.5 的介质。它不需要删除 `pending.xml`、回滚维护操作或更改 UUP 应用列表。Core 构建需要没有挂起操作的稳定源；可维护的映像可以保留它们到首次启动。更改压缩不能解决这些问题。

清理器使用固定的允许清单。它保留缓存/日志根目录、当前 `CBS.log` 与 `dism.log`、更新数据库、注册表文件、驱动仓库、Windows Installer 缓存与维护清单。当 `pending.xml` 或 `reboot.xml` 表明存在挂起的维护，或脱机注册表带有维护/更新重启标记时，下载与临时负载会被保留。如果注册表配置单元缺失或损坏而无法验证，敏感目标会在警告下保留；这不会被报告为成功移除。注册表检查使用私有的配置单元/日志副本，因此加载它不会把事务重放进映像。其他 I/O/访问错误仍按失败处理。在删除清理目标之前会拒绝未知的符号链接与联接点。它不会扫描正在运行的电脑寻找可删除项。

组件清理在移除之后、启用 .NET Framework 3.5 之前运行：脱机功能更改可能留下只在 Windows 首次启动时才完成的工作。如果清理返回 `0x800F0806`（挂起操作），可维护的 Tiny11/自定义构建会保留该工作并将清理报告为 **已跳过** 且附带警告；映像可能更大。其他清理错误仍为致命错误。挂起的操作永远不会被删除或回滚以绕过此错误。当出现这一确切的挂起状态错误时，选定 Edge/WebView2 文件移除在该次运行中同样被跳过，因为其 WinSxS 文件可能被挂起的维护引用。报告会记录这些文件被保留，而不会声称所请求的移除已成功。这些警告在续传后依然有效。

当 Edge 的文件确实被移除时，匹配的 Microsoft Edge 条目也会从两个脱机已安装程序注册表位置中移除。确切的标识与发布者检查确保无关软件、Edge Update 与 WebView2 的注册不在此浏览器条目清理范围内。如果文件移除失败或被延迟，该条目会保留。绝不针对正在运行电脑的注册表。Core 与显式的自定义组件负载移除也会在删除组件文件之前立即检查挂起的文件、包状态与功能状态，即使清理已关闭。如果启用 .NET 3.5 留下挂起的工作，这些破坏性构建会带着解释停止，而不是移除首次启动所需的文件。此时请在不带 .NET 3.5 的情况下构建，或使用可维护的映像。

标准与 Turbo 都通过备份/还原句柄删除脱机条目，不更改其 ACL，也不清除与剩余硬链接共享的只读属性。两者都能处理长映像路径，并拒绝非映像的重分析点。每次构建都会记录每操作的耗时、`timings.json` 中每阶段的耗时、复制/省略的安装字节数，以及报告中的清理文件/字节计数。失败报告同样保留耗时。

两种模式共用常见优化：不使用快速工作映像时保留源 WIM 压缩；重复目标只处理一次；已移除的负载被跳过；预检只重新查询所选版本；省略不必要的功能清单；安装文件按从大到小复制；同卷 ISO 发布采用重命名而非复制。日志仅保留在作业内部，临时文件使用作业的 NTFS 工作区。进度计时器只在操作期间运行并处理有界批次，而不是在应用空闲时持续唤醒。

在采用共享删除路径之前，三次运行 4,000 个文件的基准测试中，旧的 ACL 重写路径耗时 3.55-4.12 秒，原生删除耗时 0.69-0.75 秒。这不是当前两种模式的对比。真实的 977 文件 ISO 暂存中，四个工作线程耗时 1.74-5.90 秒，八个工作线程耗时 1.68-1.85 秒；结果取决于缓存与主机，不是普遍的加速承诺。省略此映像的可选 .NET 负载避免了复制 71,656,916 字节（约 68.3 MiB）。

一次端到端的 26H2 x64 Core 构建（相同移除集、恢复 ESD 压缩）耗时 **31 分 44 秒**，此前在这台电脑上的运行为 **90 分 31 秒**。Core 精简耗时 37 秒而非约 4 分 38 秒；提交耗时 3 分 54 秒而非约 29 分 38 秒。初始转换为快速工作 WIM 更慢（3 分 8 秒而非 33 秒）。这些是缓存/系统负载不同的实测开发运行，不是保证的加速倍数。未使用优化的延迟挂载，因为此主机的提供程序拒绝了它。

得到的 Turbo ISO 为 **3,259,584,512 字节（3.04 GiB）**，此前为 3,331,207,168 字节。其中约 68.3 MiB 来自省略可选安装负载；这个干净的 Microsoft 源没有额外的缓存文件可移除。该构建不声称有缓存节省。ISO 的 Pro 版本、内部版本号、压缩与 SHA-256 已验证；虚拟机启动认证仍然独立进行。

**这是映像构建器，不是安装器，也不是运行中系统的精简工具。** 它绝不故意针对正在运行的 Windows 安装、格式化磁盘或修改源介质。只有新建的工作副本会被维护。已存在的输出 ISO 会被拒绝，而不会被覆盖。

脱机注册表编辑会把复制的映像配置单元临时挂载到唯一命名的 `HKLM\Tiny11Builder_Offline_<guid>` 键下并显式卸载。不编辑主机现有的注册表设置。配置单元加载/卸载失败会停止操作并报告确切的临时键，而不是隐藏错误。每作业的配置单元日志让工作区恢复能在应用于卸载前被中断时，只卸载该作业的临时键。DWORD 与字符串写入通过以受限备份/还原权限打开的句柄使用原生 `RegSetValueExW` API，并且每个写入的值都会回读。小组件使用 Microsoft 有文档记载的 `SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests=0` 策略禁用。受保护的 `Explorer\Advanced\TaskbarDa` 首选项被刻意不写入：较新的主机即使通过原生 API 也可能拒绝它。主机的保护服务不会被禁用。该策略适用于小组件及其任务栏内容；支持程度仍因 Windows 版本而异。

搜索亮点使用计算机策略 `SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB=0`。受保护的默认用户 `SearchSettings\IsDynamicSearchBoxEnabled` 首选项不被编辑。必需的策略写入仍会验证；访问错误不会被静默忽略，也不会禁用任何主机注册表保护。

Core 负载删除使用超长 Windows 路径，因此较长的 WinSxS 文件名不会在 260 字符的旧限制处失败。符号链接与联接点限制保持不变。

加速使用 Microsoft 有文档记载的 [/Mount-Image /Optimize](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-image-management-command-line-options-s14) 与 [多功能 /Disable-Feature](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-operating-system-package-servicing-command-line-options) 选项。功能批次针对默认 Foundation 包；单个专家维护操作保持序列化。

脱机映像文件可以合法使用 WIM/WOF 重分析标记。这两个非名称代理的映像提供程序标记在所拥有的映像内部被接受；普通符号链接与联接点仍被拒绝。源、工作区与输出路径保留其更严格的重分析点检查。

## Windows 11 内部版本兼容性

构建器接受 **Windows 11 客户端内部版本 22621（22H2）及更新**，包括 23H2、24H2、25H2、26H2 与 Insider/Canary 介质，支持 **x64 与 ARM64**，没有硬编码的最大版本号。映像架构、版本与build号从 DISM 读取，而不是从 ISO 文件名推断。包/能力/功能标识来自挂载的映像，因此缺失项会被记录为 *not present（不存在）*，而不是猜测。版本名称使用 Windows 映像工具写入的 OEM 代码页解码，并显式识别 UTF 字节序标记。西里尔文本不会通过替换字体或猜测版本名来修复。无效的编码输出会明确失败，而不是静默替换字符。

接受并不代表认证**每个版本与每种自定义移除组合都能启动**。这一新实现需要针对每个发布/渠道的代表性介质进行虚拟机验证。单元测试覆盖构建元数据以及跨正式版与更新/Canary 风格版本号的模拟管线；可选的集成测试以只读方式使用真实介质。

Windows 10（包括 22H2）可以使用兼容的 Microsoft 部署工具构建 Windows 11 映像；要求不在主机的版本号。应用检查实际的维护提供程序，而不只是 `dism.exe` 启动器的版本。已知的启用包版本共享维护基础：23H2（22631）使用 22621；25H2（26200）与 26H2（26300）使用 26100。因此 26100 工具不会仅因为 26H2 映像报告 26300 而被拒绝。请保持 Windows/ADK 维护更新为最新。

Microsoft 的 [ADK 支持列表](https://learn.microsoft.com/windows-hardware/get-started/adk-install) 为 26100 ADK 列出了 24H2、25H2 与 26H2；[DISM 支持的平台](https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-supported-platforms) 记录了在 Windows 10 上使用 Windows 11 部署工具。未知的更新/Canary 版本不会被并入这些家族，可能仍需要预览版 ADK或更新的工具提供程序。当工具不满足该维护基础时，应用会在修改之前停止。它会自动检测已安装的工具；没有手动工具选择器。

### Windows 10 报告映像工具为 10.0.19041

对于映像 **26300**，工具 **10.0.19041.3636** 与早前误拒 26100 工具的情况不同。Windows 10 可以保持安装，但其内置维护工具比此映像的维护基础更旧。

1. 通过应用错误提示中的 **获取映像工具** 打开 [Microsoft ADK 页面](https://learn.microsoft.com/windows-hardware/get-started/adk-install)。
2. 安装 Microsoft 为 Windows 11 24H2/25H2/26H2（26100 家族）列出的 ADK，选择 **Deployment Tools**，并应用其维护更新。修改完整 ISO 中已有的 `boot.wim` 不需要单独的 Windows PE 加载项。
3. 关闭并重新打开 Tiny11 Builder，让它检测已安装的工具，然后用你的原始 ISO 再次开始构建。

不要替换 `Windows\System32` 中的文件、下载独立的 DISM EXE 或删除挂起操作文件。应用需要完整且兼容的工具集，而不是一个改名的启动器。

高级组件清单是只读的，它检查实际的清单操作，而不是强加可写构建的版本门槛或输出空间要求。如果已安装的工具无法查询更新的映像，应用会停止清单、丢弃其拥有的挂载并提供 **获取映像工具**，链接到 Microsoft 的 ADK 页面。成功列出映像并不等于允许用旧工具修改它：构建与续传预检仍要求兼容的维护提供程序。

保存的映像的哈希、版本、架构、安装类型与完整版本在续传时仍会验证。其本地化显示名称不被视为标识：修复解码错误的文本不会使未更改的已保存映像字节失效。Microsoft 同样决定哪些正式版/Insider ISO 可用：应用不声称能下载未发布的 Canary 版本，也不使用非官方 UUP 镜像。主“下载”按钮会自动解析当前的公开版本。可选的“其他官方下载”区仍接受其他语言或 Insider 介质的手动链接。Microsoft 可能要求你在其 Insider 页面登录；应用不处理你的凭据。

**策略依赖内部版本/版本 edition。** 脱机注册表写入成功会被验证，但这并不能证明 Home、Pro、Enterprise 或未来版本会遵循每个策略。必需的诊断仍保留在均衡电源配置中。Windows 功能更新可能恢复应用与设置。本地账户与硬件检查设置是尽力而为，不保证在新版本上绕过。任何修改都不会绕过 Windows 授权或激活。

**Core 与专家警告：** Core 没有可用的更新/组件维护或恢复映像，并禁用杀毒保护。运行时布局未知的未来版本会在 Core 精简开始前被拒绝。专家模式刻意允许移除受保护与启动关键项；没有依赖求解器。DISM 可以拒绝受保护 CBS 包的移除，此时构建明确失败。直接的系统应用/WinSxS 负载移除被单独标记为不支持的操作。移除服务会删除其脱机注册表注册；共享的 `svchost.exe` 与其他共享二进制文件永远不会被盲目删除。请在隔离的虚拟机中测试激进的映像，并保留已知良好的介质。

## 构建与运行

要求：Windows 10 2004 或更新作为主机、.NET 8 SDK 以及与项目目标兼容的 Windows SDK。应用会选择最新安装的 Microsoft 维护工具并自动准备 oscdimg。较新的映像版本仍需要足够新的 Windows 主机或 ADK。Windows App SDK 与 .NET 运行时在发布的应用文件夹中是自包含的。

已安装的工具要求受信任的 Microsoft 签名。Microsoft 符号服务器上的 oscdimg 副本省略了其内嵌证书，因此只接受确切的经批准的 2.56 二进制文件：SHA-256 `2000160B2C5044691B2F9A0AC308E5207F273D4880A572457AF16D05886BA861`。下载仅限于经批准的 Microsoft 符号服务器资源及其观察到的 Microsoft blob 重定向。不同的校验或重定向会在执行前停止获取。这不是通用的未签名工具例外。

```powershell
dotnet build .\Tiny11Builder.sln -c Release
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-x64 -p:Platform=x64 -o .\artifacts\Tiny11Builder-x64
.\artifacts\Tiny11Builder-x64\Tiny11Builder.exe
```

ARM64 主机：

```powershell
dotnet publish .\src\Tiny11Builder.App\Tiny11Builder.App.csproj -c Release -r win-arm64 -p:Platform=ARM64 -o .\artifacts\Tiny11Builder-arm64
```

应用**启动时始终要求管理员权限**。Windows 会在打开它之前要求 UAC 批准（或管理员账户凭据）；拒绝则应用不会启动。这内嵌在可执行文件中，不是可选的重启按钮或 UAC 绕过。请运行 `Tiny11Builder.exe`，而不是托管 DLL。构建仍需要明确的确认与风险确认。提权应用中文件/文件夹选择器可以正常工作。

## GitHub 源码与发布包

提交源码、测试、解决方案、脚本、README、NOTICE 与 `.github` 配置。生成的 `bin`、`obj`、`artifacts`、测试结果与 Windows 映像会被忽略。不要在公开上传中包含你的工作介质、带个人路径的配方或构建日志。

GitHub Actions 工作流在 Windows 上运行测试并构建自包含的 x64 与 ARM64 ZIP。它只上传工作流工件；不发布 GitHub Release、不更改权限，也不打包 Windows ISO/ADK 工具。原生测试需要提权的 Windows 运行器。真实映像测试保持可选。

在提权的 PowerShell 中本地构建同样的压缩包：

```powershell
.\scripts\package.ps1 -Architecture x64
.\scripts\package.ps1 -Architecture arm64
```

压缩包与 SHA-256 文件出现在 `artifacts\release`。脚本会验证可执行文件架构、内嵌管理员清单与捆绑运行时，并包含依赖项许可声明。它拒绝覆盖该版本的现有压缩包。将整个 ZIP 解压到文件夹后运行 `Tiny11Builder.exe`；只复制 EXE 是不够的。

当前应用版本为 **0.1.3**。包未签名，也不是 Microsoft 认证的 Windows 映像。公开再分发前请查阅 [NOTICE.md](../NOTICE.md)：此上游检出没有独立的许可文件，且此重写不会为别人的源码授予许可。现有的 `origin` 指向上游项目；推送前请选择你自己的 GitHub 仓库或 fork。打包脚本不会推送任何代码。

默认工作区为 **`C:\tiny11`**，输出 ISO 也在那里。请使用 **NTFS 工作区**，通常需要 40-70 GiB 可用空间。由于 WIM 挂载限制，ReFS、FAT32 与网络工作区会被拒绝。输出不能是 FAT32。支持工作区与输出在同一驱动器。网络路径、备用数据流、源/工作区/输出中的重分析点，以及作为输出/工作区的系统目录都会被拒绝。输入必须包含 `boot.wim`、`install.wim` 或 `install.esd` 以及 UEFI 启动介质；不支持拆分的 `install.swm` 输入。导出单个版本可以避免先复制多版本安装映像再将其删除。

## 日志、配方与恢复

状态与缓存工具位于 `%LOCALAPPDATA%\Tiny11Builder`；工作介质默认为 `C:\tiny11` 且可更改。每次操作都会创建唯一的 `tiny11-<guid>` 子目录。构建会保留 `job.json`、`recipe.json`、`build.log`、`dism.log`、`operations.json`、`timings.json` 与 `report.json`。`operations.json` 也会记录失败的构建，包括确切的操作与错误详情。原始磁盘日志保留异常与控制台输出。成功时，除非选择 **保留工作介质**，工作介质会被移除；日志与报告保留。失败时，工作文件被保留，应用会给出其路径。配方包含本地路径与选择项，分享前请先检查。切换构建模式会替换之前的移除选择。自定义初始不选择任何移除组、高级操作或 Core 移除。单个组件操作叠加在已选组之上：要保留一个组件，其组与其单个条目都不要勾选。保存的配方保留所选模式与确切移除项。旧配方仍被接受；修改过的 Core 配方会加载为启用 Core 移除的自定义模式，绝不会静默丢弃。

取消**不会**杀死正在运行的 DISM 挂载/提交进程。它等待操作完成后，在安全边界丢弃此作业的挂载。操作运行时关闭应用会被阻止。ISO 创建与下载流可以取消。在下一边界取消之前，已完成的阶段会被记录。失败/取消时工作文件与日志会保留。失败提示提供 **打开日志**。红色 **安全取消** 按钮在工作运行期间保持于右上角。点击它首先要求确认；“继续构建/继续”是默认项，不发送取消。确认后，同一对话框保持打开并显示进度条，同时当前操作完成、拥有的挂载被丢弃、日志被保存。等待进度条是不确定模式，不是猜测的关闭百分比。运行完全稳定后，对话框显示其实际结果。失败的清理或必需的恢复显示为错误，绝不会伪装成安全完成。如果操作在确认打开期间已完成，则不会取消任何内容。取消清理期间 Esc 与关闭应用被阻止。

安装映像成功提交后，作业会保存 `checkpoint.json`，包含原始配方哈希、映像标识、映像哈希与已暂存安装文件的哈希清单。最终压缩后，第二个检查点允许重试 ISO 创建而无需重新压缩。检查点在持有作业独占锁的情况下原子地刷新与替换。安装映像编辑使用私有副本，在替换之前提交并验证。

使用 **继续构建** 并选择已停止作业的 `job.json`，包括在重启应用之后。引擎在继续之前验证检查点、哈希、工具与作业所有权。它拒绝更改过的移除选择、损坏或过期的进度、活动作业与不安全路径。有效提交后不再需要原始源。UI 会用新定位的工具恢复原始配方；如果输出已存在，请选择新的 ISO 名称。继续构建确认对话框默认为取消，并重复适用的 Core/自定义风险。

没有检查点的较早中断作业无法自动继续，即使它们恰好包含已提交的 WIM。选择它们继续时不会被收养或删除。如果需要，请保留其工作文件。

发布从不覆盖现有 ISO。跨卷复制在最终重命名前会刷新并校验哈希。输出竞争时，验证过的 ISO 保留在作业的 `output.iso`，构建保持可续传。成功报告只在发布之后写入。发布后的日志/报告/清理问题显示为警告，而不是对已发布 ISO 的虚假失败。

如果应用/电脑崩溃，使用 **恢复工作区**，选择该作业的 `job.json`，并明确确认丢弃其工作副本。恢复拒绝活动锁定作业与已完成作业。如果存在已保存进度，UI 会提供 **改为继续**，绝不会自动删除已提交的映像。恢复只处理该作业的挂载与暂存目录；它从不使用全局 DISM 挂载清理，并保留源、日志、配方与现有输出。

## 测试

从提权终端运行测试。原生文件与注册表测试需要 Windows 备份与还原权限。

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj
```

一个小型原生映像提供程序检查会把一个合成目录捕获进其自己的临时 WIM，删除一个脱机组件，提交，然后只读重新挂载以验证保留的硬链接的内容、权限与只读状态。它不使用你的 Windows ISO：

```powershell
$env:TINY11_NATIVE_WIM_CHECK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Shared_deletion_survives_a_synthetic_WIM_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_NATIVE_WIM_CHECK=1
Remove-Item Env:\TINY11_NATIVE_WIM_CHECK
```

旧版安装程序集成可以针对真实 ISO 检查。此测试只把 `boot.wim` 复制进一次性作业，应用启动配置，提交，只读重新挂载，并检查保存的配置与映像计数。它不会更改原始 ISO；它不认证虚拟机中的 Windows 安装程序：

```powershell
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Legacy_startup_survives_a_real_Setup_image_commit_when_configured -- RunConfiguration.EnvironmentVariables.TINY11_SETUP_ISO_CHECK=C:\media\Windows11.iso
```

真实映像测试是可选的，需要有效的 Windows 11 WIM。它以**只读**方式挂载 WIM，列出其组件，并在其注册表文件的单独副本上测试策略与服务更改。然后丢弃挂载并检查源时间戳未改变：

```powershell
$env:TINY11_TEST_WIM = 'C:\media\sources\install.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Read_only_real_Windows_image_integration_when_configured
```

应用是 `src\Tiny11Builder.Core` 之上的薄 WinUI 表现层；测试无需 GUI 运行。实时 Microsoft 目录与工具检查是可选的：

```powershell
$env:TINY11_TEST_DOWNLOADS = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Live_official_catalog_and_pinned_tool_when_configured
```

这会解析 x64 与 ARM64 ISO 链接并下载/验证 oscdimg，但不执行它，也不下载完整 Windows ISO。Windows 介质与部署可执行文件不由本仓库再分发。

可写配置单元回归测试也是可选的。它把完整的 Core 策略集应用到临时挂载，验证小组件策略，然后丢弃更改：

```powershell
$env:TINY11_PERMISSION_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Writable_mount_permission_regression_when_configured
```

此测试请使用工作 WIM 副本。完整 Core ISO 验证读取显式保存的配方，在 `C:\tiny11` 下写入单独的 ISO，且不声称虚拟机启动验证：

```powershell
$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

一次 Windows 11 Pro 26H2 x64（build 26300.9457）的端到端构建已完成，使用原始 Core 移除集、紧凑安装与恢复 ESD 压缩。得到的 ISO 为 3,331,207,168 字节（3.10 GiB），在索引 1 包含一个安装版本。源介质为 8.31 GiB 的多版本介质，因此该大小对比包含只导出 Pro 以及移除与压缩。虚拟机启动验证仍与 ISO 创建分开。

可选的 Turbo 基准测试：

```powershell
$env:TINY11_TURBO_BENCHMARK = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Native_file_deletion_benchmark_when_configured

$env:TINY11_TURBO_TEST_WIM = 'C:\media\working.wim'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Real_Turbo_mount_staging_and_cleanup_benchmark_when_configured

$env:TINY11_BUILD_RECIPE = 'C:\tiny11\tiny11-job\recipe.json'
$env:TINY11_TEST_TURBO = '1'
dotnet test .\tests\Tiny11Builder.Core.Tests\Tiny11Builder.Core.Tests.csproj --filter Full_real_Core_build_when_configured
```

最后一条命令使用单独的 `C:\tiny11\turbo-verification` 工作区与 `C:\tiny11\Tiny11Core-26300-turbo.iso` 输出。它保留恢复压缩、启用 Turbo 与冗余数据清理，并省略可选安装负载。挂载基准测试请使用工作副本 WIM，并让每个测试在进程结束前丢弃其挂载。

---

# 原始 PowerShell tiny11builder
*构建精简 Windows 11 映像的脚本 —— 现已用 **PowerShell** 实现！*

## 简介：
原始 PowerShell 脚本使用 DISM 和 oscdimg 创建修改过的 Windows 11 安装映像。`tiny11maker.ps1` 保留更新支持；`tiny11Coremaker.ps1` 移除它。

现在你可以在任何 Windows 11 版本（不只是特定 build）上使用它，也支持任何语言或架构。这要归功于 PowerShell 相比早期 Batch 版本大幅改进的脚本能力。

脚本使用 DISM 恢复压缩与 Windows ADK 中的 **oscdimg.exe** 创建 ISO。附带的无人值守应答文件配置紧凑安装与本地账户设置。本地账户绕过在较新的 Windows 版本上可能无效。

**tiny11 Core** 移除组件存储文件与 Windows 恢复支持，并禁用 Windows 更新与 Defender。之后无法添加语言、更新或功能。仅用于测试虚拟机。

---

## ⚠️ 脚本版本：
- **tiny11maker.ps1**：常规脚本，移除大量预装内容但保持系统可维护。创建后可以添加语言、更新与功能。这是日常使用的推荐脚本。
- ⚠️ **tiny11coremaker.ps1**：核心脚本，移除更多内容但同时也移除维护映像的能力。之后无法添加语言、更新或功能。推荐用于快速测试或开发用途。

## 使用说明：
1. 从 [Microsoft 网站](https://www.microsoft.com/software-download/windows11) 或 [Rufus](https://github.com/pbatard/rufus) 下载 Windows 11
2. 使用 Windows 资源管理器挂载下载的 ISO 映像。
3. 以管理员身份打开 **PowerShell 5.1**。
4. 更改脚本执行策略：
```powershell
Set-ExecutionPolicy Bypass -Scope Process
```
> 使用 `-Scope Process` 可保持你原有的策略不变，因为此更改只在当前 PowerShell 会话中生效。

5. 启动脚本：
```powershell
C:/path/to/your/tiny11/script.ps1 -ISO <letter> -SCRATCH <letter>
```
> 运行 `get-help` 命令可以查看脚本帮助。

6. 选择映像挂载的驱动器号（只输入字母，不带冒号（:））
7. 选择你希望映像基于的 SKU。
8. 然后休息等待 :)
9. 映像完成后，你会在脚本解压的文件夹中看到名为 tiny11.iso 的文件

---

## 移除的内容：
<table>
  <tbody>
    <tr>
      <th>Tiny11maker</th>
      <th>Tiny11coremaker</th>
    </tr>
    <tr>
      <td>
        <ul>
          <li>Clipchamp</li>
          <li>新闻</li>
          <li>天气</li>
          <li>Xbox</li>
          <li>获取帮助</li>
          <li>入门</li>
          <li>Office Hub</li>
          <li>纸牌</li>
          <li>人脉</li>
          <li>PowerAutomate</li>
          <li>To Do</li>
          <li>闹钟</li>
          <li>邮件和日历</li>
          <li>反馈中心</li>
          <li>地图</li>
          <li>录音机</li>
          <li>你的手机</li>
          <li>媒体播放器</li>
          <li>快速助手</li>
          <li>Internet Explorer</li>
          <li>Tablet PC 数学</li>
          <li>Edge</li>
          <li>OneDrive</li>
        </ul>
      </td>
      <td>
        <ul>
          <li>常规 tiny11 的全部内容，另加</li>
          <li>Windows 组件存储（WinSxS）</li>
          <li>Windows Defender（仅禁用，需要时可以重新启用）</li>
          <li>Windows 更新（没有 WinSxS 无法工作，启用会使系统进入故障状态）</li>
          <li>WinRE</li>
        </ul>
      </td>
    </tr>
  </tbody>
</table>

请记住 **tiny11 core 中无法添加回功能**！<br>
创建映像时会询问你是否启用 .NET 3.5 支持！

---

## 已知问题：
- 虽然 Edge 已被移除，但设置中仍有一些残留，不过应用本身已被删除。
- 你可能需要先更新 Winget 才能通过 Microsoft Store 安装应用。
- Outlook 与 Dev Home 可能过段时间重新出现。这是一场持续的斗争，不过最新的脚本更新更积极地阻止这一点。
- 如果你在 arm64 上使用此脚本，运行时可能看到一闪而过的错误。这是因为 arm64 映像的 System32 文件夹中没有包含 OneDriveSetup.exe。

---

## 计划实现的功能：
- ~~禁用遥测~~（已在 2024-04-29 版本中实现！）
- ~~更多广告抑制~~（已在 2025-09-06 版本中部分实现！）
- 改进的语言与架构检测
- 更灵活的保留/删除选择
- 也许一个 GUI？？？

目前就这些了！

## ❤️ 支持本项目

如果这个项目对你有帮助，请考虑表达你的支持！小额捐赠能帮助我投入更多时间到这样的项目中。谢谢！

**[Patreon](http://patreon.com/ntdev) | [PayPal](http://paypal.me/ntdev2) | [Ko-fi](http://ko-fi.com/ntdev)**

感谢你的尝试，欢迎告诉我你的使用感受！

