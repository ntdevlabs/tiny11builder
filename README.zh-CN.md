# Tiny11 Builder（简体中文）

[English](../README.md) | 简体中文

使用原生 WinUI 应用构建更小的 Windows 11 ISO。可下载官方 Windows 映像或使用本地映像，选择要移除的内容，然后创建你的 ISO。

## 下载

[**获取最新版本**](https://github.com/ntdevlabs/tiny11builder/releases/latest)

Intel/AMD 电脑选择 **x64**，ARM 版 Windows 选择 **ARM64**。解压完整 ZIP 后运行 `Tiny11Builder.exe`。需要管理员权限。

## 选择构建模式

| 模式 | 作用 |
| --- | --- |
| Tiny11 | 移除预装应用和不需要的功能，同时保留 Windows 更新、Defender 和恢复环境。 |
| Tiny11 Core | 进一步精简：移除维护与恢复支持，并禁用 Defender。 |
| 自定义（Custom） | 逐项选择要移除或保留的应用、组件、功能和服务。 |

Turbo 模式可加快映像处理速度。映像提交或压缩后中断的构建可从已保存的进度继续。Core 和组件存储移除会自动使用旧版 Windows 安装程序。

**Core 无法再更新或维护。自定义模式移除受保护项可能导致 Windows 无法使用。安装前请先在虚拟机中测试你的 ISO。**

## 视频

[**在 YouTube 上观看**](https://youtu.be/LfEWIz5mQ8A)

## 截图

### 欢迎页

![Tiny11 Builder 欢迎界面，包含 Tiny11、Tiny11 Core 与自定义模式](images/ui-welcome.png)

### 组件

![组件选择与 Tiny11 Core 警告](images/ui-components.png)

### 构建设置

![包含 Turbo 模式与可选映像清理的构建设置](images/ui-build-settings.png)

## 更多信息

[用法、兼容性与构建说明（中文）](usage.zh-CN.md) ·
[署名与分发说明](../NOTICE.md)

原始 PowerShell 构建脚本仍保留在本仓库中。

## 支持本项目

Tiny11 Builder 完全免费。如果它为你节省了时间，你可以赞助开发、跨 Windows 版本的测试以及问题修复。

**[按月支持](https://github.com/sponsors/ntdevlabs)** ·
**[一次性捐赠](https://ko-fi.com/ntdev)**

偏好其他平台？[Patreon](https://www.patreon.com/ntdev) ·
[PayPal](https://paypal.me/ntdev2)

使用任何功能都无需捐赠。反馈可复现的 Bug、在虚拟机中测试构建、分享[演示视频](https://youtu.be/LfEWIz5mQ8A)同样是对项目的帮助。

## 界面语言

应用内置简体中文与英文界面，可在侧边栏“设置”中切换，重启应用后生效。本页与 [用法说明](usage.zh-CN.md)、[支持说明](support.zh-CN.md) 提供中文文档。
