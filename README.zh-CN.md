![MDNote](screenshots/banner.png)

[English](README.md) · [한국어](README.ko.md) · [日本語](README.ja.md) · **简体中文** · [繁體中文](README.zh-TW.md)

像记事本一样轻快敏捷的 Windows Markdown 与 TXT 编辑器。

## MDNote 的优势

- **无需记住符号的 Markdown**：在类似 Word 的排版界面中直接写作，文件会保存为标准 Markdown 格式。
- **如记事本般轻巧，大文档同样快速**：安装包约 1.3MB。数百万字的文档也能立即打开，打字不卡顿。
- **一个程序兼顾 MD 与 TXT**：TXT 可像带行号的记事本一样编辑，MD 可像带排版的文档一样写作，支持多种编码打开和保存。
- **对中、日、韩输入法友好**：输入中文、日文或韩文时，不会出现乱码或光标乱跳的情况。
- **防止内容丢失**：即使未保存意外退出，下次启动时也能恢复；即使保存过程中出错，也会保护原文件不受损坏。
- **免费开源**：基于 GPL v3 许可证发布，任何人都可以自由使用和修改。

![MDNote](screenshots/zh-CN.png)

![输入演示](screenshots/demo.gif)

## 下载

[下载最新版本 (Releases)](https://github.com/Lahai-Roi/MDNote/releases/latest)

- 安装程序（`MDNote-Setup-<版本>.exe`）：在安装向导的第一个界面选择语言。
- 免安装便携版（`MDNote-<版本>-portable.zip`）：解压后直接运行 `MDNote.exe`。
- 需要 Windows 10/11 64 位与 .NET Framework 4.8（Windows 系统自带）。
- 由于程序未包含数字签名，首次运行时 Windows 可能会弹出安全提示，点击 **更多信息 → 仍要运行** 即可。

## 主要功能

- **所见即所得编辑**：隐藏 `#`、`**` 等符号，按排版效果直接书写。需要查看符号时，可切换到**实时模式**或**源码**。
- **工具栏与表格编辑**：用按钮插入标题、粗体、文字颜色、列表、代码、链接、图片和表格，表格可直接在单元格中编辑。
- **GitHub 格式与公式**：提示框（GitHub 与 Obsidian 风格）、高亮、脚注、`[TOC]`、表情符号和公式（`$E=mc^2$`）都会直接显示。
- **TXT 模式**：像记事本一样带行号的纯文本编辑。
- **标签页、窗口与大纲**：在标签页或新窗口中打开多个文档，通过标题列表或同一文件夹的文件列表跳转。
- **图片整理**：粘贴或拖入的图片会在保存时集中到文档旁的文件夹。
- **查找替换、打印、保存为 HTML、多种编码**（UTF-8、CP949、Shift JIS、GBK、Big5 等）
- **继续上次会话、文件变更提醒**
- **深色、专注与打字机模式，自定义快捷键，5 种语言**

![深色模式](screenshots/en-dark.png)

## 界面功能预览

| | |
|---|---|
| ![实时模式](screenshots/features/en-live.png)<br>**实时模式**：仅光标所在行显示 Markdown 符号，其余部分按排版效果显示。 | ![表格编辑](screenshots/features/en-table.png)<br>**表格编辑**：直接在单元格内输入，使用工具栏添加行列与调整对齐。 |
| ![大纲](screenshots/features/en-outline.png)<br>**大纲**：点击标题列表即可直接跳转到对应位置。 | ![查找替换](screenshots/features/en-find.png)<br>**查找替换**：查找文档中的文字并一次性全部替换。 |
| ![TXT 模式](screenshots/features/en-txt.png)<br>**TXT 模式**：像带行号的记事本一样编辑纯文本。 | ![语言选择](screenshots/features/en-lang.png)<br>**5 种语言**：菜单、帮助与示例文档均会切换为所选语言。 |
| ![提示框与格式](screenshots/features/en-github.png)<br>**提示框与格式**：GitHub 提示框、可自定义标题的 Obsidian 风格标注框，以及高亮、脚注和按键样式。 | ![公式](screenshots/features/en-math.png)<br>**公式**：句中的 `$…$` 和单独成行的 `$$…$$` 会渲染为公式。 |

## 使用方法

应用的**帮助**菜单中包含两份文档：

- **MD文件编写方法**：标题、列表、表格、图片、代码的编写方法与示例
- **MDNote使用说明**：菜单、快捷键与视图设置的完整说明

## 局限性

- 仅支持 Windows（Windows 10/11 64 位）。
- 流程图（Mermaid）等扩展语法不会渲染为图形，而是直接显示输入的原文字。
- 如需保存为 PDF，请在打印菜单中选择“Microsoft Print to PDF”。

## 常见问题

**保存的文件可以用其他程序打开吗？**
可以。文件会保存为标准 Markdown（.md）或纯文本（.txt），因此在其他编辑器中也能正常打开。不过，像文字颜色这种标准 Markdown 中没有的格式，会保存为 HTML 标签。

**我想边看 Markdown 符号边写作。**
开启 **视图 → Markdown 实时预览** 后，仅光标所在行会显示符号。如果想让整个文档都显示符号，请点击右上角的**源码**按钮。

**紧接着粗体文字后面继续打字，还会是粗体吗？**
会。紧接着粗体末尾输入会与粗体按钮的状态一致，继续保持粗体；先输入空格再打字则为普通文字。

**首次运行时 Windows 弹出了警告。**
这是因为可执行文件没有数字签名。点击 **更多信息 → 仍要运行** 即可。

**可以不安装直接使用吗？**
可以。从最新版本（Releases）下载以 `-portable.zip` 结尾的文件，解压后运行 `MDNote.exe` 即可。

**如何卸载？**
安装版可在 Windows 设置的应用列表中卸载。如果安装文件夹中有您手动放入的文件，则只会保留这些文件。免安装便携版直接删除解压后的文件夹即可。

## 隐私说明

- MDNote 默认不连接互联网，仅在点击**帮助 → 检查更新**时，或开启**自动检查更新**后（默认关闭，每次启动程序时），从 GitHub 检查最新版本号。不会向任何地方发送使用记录或文档内容。
- 为了恢复未保存的内容，正在编辑的内容副本仅保存在本机用户文件夹（AppData）中，保存或关闭文档后即会删除。
- 各项设置（字体、语言、快捷键等）也仅保存在您的电脑上。

## 错误反馈与建议

请在 [Issues](https://github.com/Lahai-Roi/MDNote/issues) 中留言。如果能一并提供以下信息，将有助于我们更快修复问题：

- 进行了什么操作（点击的菜单或按键、输入的文字）、发生了什么情况、原本应该是什么效果
- Windows 版本、使用的输入法（中文、日文、韩文等）
- 如有可能请附上截图。请勿上传包含个人隐私的文档内容。

## 从源码构建

使用 Windows 自带的 .NET Framework 4.8 C# 编译器即可构建，无需安装 Visual Studio。

```powershell
.\src\build.ps1
```

会在 `src` 文件夹中生成 `MDNote.exe`。

安装程序使用 [NSIS 3](https://nsis.sourceforge.io/) 制作，具体步骤请参考 [src/installer/README.md](src/installer/README.md)。

## 许可证

MDNote 基于 [GNU General Public License v3.0](LICENSE) 发布。
包含修改后的 [AvalonEdit](https://github.com/icsharpcode/AvalonEdit)（MIT 许可证）。详情请参阅 [THIRD_PARTY_NOTICES.txt](src/THIRD_PARTY_NOTICES.txt)。

Copyright (c) 2026 Lahai-Roi
