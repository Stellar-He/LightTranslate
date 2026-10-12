# 轻译 · LightTranslate

**选中英文，译文自然浮起；继续操作，浮层自然消失。**

一个面向 Windows 桌面英文阅读的小工具。无需额外点击翻译图标，浮层只显示译文，
不会抢走当前应用的输入焦点。

![轻译浮层预览](assets/preview.png)

> 当前为 **0.1.0-alpha.1**，功能已在开发者的 Codex 桌面环境中手动验证，
> 仍需要长期运行、多显示器、不同 Windows 版本和更多应用的兼容性测试。
> Claude 桌面应用尚未验证；请勿将“全局划词”理解为所有应用都保证可用。

## 功能

- 拖选、双击选词，以及释放 Shift 后的键盘选区自动触发。
- 命中 ECDICT 的英文单词和部分短语在本地查询，约 76 万条词库；附带常见编程术语。
- 其他文字通过微软在线接口翻译，失败时尝试腾讯备用接口。
- 圆角浮层、淡入淡出、自动匹配启动时的系统明暗主题。
- 点击外部、滚动外部页面、打字、切换应用或将鼠标移远后自动隐藏。
- 鼠标停在浮层上时延长阅读时间；长译文可在浮层内滚动。
- 托盘开关、应用排除列表、可选开机启动。

## 使用

运行环境：**Windows 10/11 x64、.NET Framework 4.8**。

从 [Releases](https://github.com/qhmiao/LightTranslate/releases) 下载便携 ZIP，
解压到当前用户可写的固定目录，例如 `%LOCALAPPDATA%\LightTranslate`，
然后双击 `LightTranslate.exe`。不要直接在压缩包内运行，也不要放在需要管理员写入权限的目录。

启动后没有主窗口，系统托盘出现“译”图标。拖选可复制的英文文本即可显示译文。
右键托盘可切换自动翻译、打开设置文件或退出；双击托盘切换自动翻译。
纯中文选区会被忽略。

如需开机启动，在解压目录运行：

```powershell
pwsh -File .\startup.ps1
```

取消开机启动：

```powershell
pwsh -File .\startup.ps1 -Remove
```

设置保存于应用旁的 `settings.json`。退出应用后修改，再重新启动。默认配置见
[settings.example.json](settings.example.json)。例如排除特定应用：

```json
"ExcludedApps": ["SomeApp", "AnotherApp"]
```

填写进程名称，可带或不带 `.exe`。`ClipboardFallback` 是沿用早期实现的字段名；
当前它控制整个剪贴板取词路径，设为 `false` 会关闭取词。
当前完整支持的目标语言为简体中文（`zh-Hans`）；其他目标语言未完整验证，
本地词库及腾讯备用服务仍返回中文。

## 从源码构建

需要 Windows、PowerShell 7、.NET Framework 4.8 的 64 位编译器。
使用 Windows 自带的 .NET Framework 编译器，不需要 Node.js 或额外的 .NET 桌面运行时。

```powershell
git clone https://github.com/qhmiao/LightTranslate.git
cd LightTranslate
pwsh -File .\build.ps1
pwsh -File .\scripts\download-dictionary.ps1
pwsh -File .\scripts\test.ps1
```

输出位于 `dist\LightTranslate`。词库脚本下载固定版本的 ECDICT，验证 SHA-256，
然后将其构建为索引文件；首次下载需要网络，缓存保存在 `cache`。

离线自检覆盖词库查询、剪贴板快照、终端/语言过滤、原生密码控件识别、
取消请求、无 UIAutomationClient 依赖、对象回收和浮层窗口样式。
可用下面的命令额外测试在线服务（仅发送程序内固定的测试句子）：

```powershell
pwsh -File .\scripts\test.ps1 -Online
```

构建带词库、许可证和默认设置的便携包：

```powershell
pwsh -File .\scripts\package.ps1 -Version 0.1.0-alpha.1
```

自检不能替代实际应用中的划词验证。操作测试清单见 [docs/TESTING.md](docs/TESTING.md)。

## 取词与数据处理

当前通过带内部标记的 `Ctrl+C` 获取选区，并尝试保存、恢复原剪贴板的各格式。
若剪贴板在取词期间被用户或其他应用更新，程序会避免覆盖更新后的内容。
应用仍可能在系统剪贴板历史或第三方剪贴板管理器中留下临时复制记录。

- 本地词库命中不会把选区发送给在线服务。
- 未命中词库的文字会发送给微软，主服务失败时可能发送给腾讯。
- 程序启动时会预热微软连接；该请求不包含选区文字。
- 译文缓存仅在当前进程内保存，最多 256 条；退出后清空。
- 程序不记录选区原文或用户译文。`Diagnostics` 默认关闭；启用后日志含进程名、
  进程编号、字符数和触发状态。启动错误日志可能包含本机路径。

如不希望某个应用触发取词，可加入排除列表，或在托盘中关闭自动翻译。

## 当前限制

- 依赖应用支持复制选区；没有 OCR 或不可复制文字识别。
- 终端进程默认跳过，避免 `Ctrl+C` 干扰命令；嵌入其他应用的终端不保证被识别。
- 原生密码编辑框会被跳过，网页/自绘密码字段依赖应用本身的复制限制。
- 普通权限运行时，无法保证能从以管理员身份运行的应用获取文字。
- 默认最多选择 2,000 个字符。词库查词可离线，句子翻译需要网络。
- 在线接口是实验性兼容接口，当前无需个人 API 密钥；并非官方 Azure 或腾讯云 SDK，
  没有服务可用性保证，可能限流或变更。
- 暂无 OCR、朗读、翻译历史、原位替换、AI 润色或图形设置界面。
- 程序尚未代码签名，暂无自动更新和完整安装器。

## 许可证与致谢

轻译采用 [MIT](LICENSE) 许可证。英文词库来自
[ECDICT](https://github.com/skywind3000/ECDICT)，微软接口实现参考
[STranslate](https://github.com/STranslate/STranslate)。
上游版权声明、来源版本和许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
