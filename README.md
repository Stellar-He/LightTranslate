# 轻译 · LightTranslate

**选中英文，译文自然浮起；继续操作，浮层自然消失。**

轻译是一个 Windows 桌面划词翻译工具。拖选或双击英文，译文就会出现在鼠标旁。
浮层只显示译文，保留当前应用的输入焦点，让你顺手读完、继续操作。

![轻译浮层预览](assets/preview.png)

## 阅读时少一步

- **选中即译**：拖选、双击选词或使用键盘选择文字，自动显示译文。
- **顺手收起**：点击、打字、滚动页面或切换应用时，浮层自动淡出。
- **单词本地快查**：约 76 万条 ECDICT 词库，支持离线查词，并补充常见编程术语。
- **句子在线翻译**：使用微软翻译接口，腾讯接口作为备用。
- **留足阅读时间**：鼠标悬停时保持显示，长译文可在浮层内滚动。
- **融入桌面**：圆角、淡入淡出、明暗主题和托盘开关。

## 开始使用

运行环境：Windows 10/11 x64，.NET Framework 4.8。

1. 从 [Releases](https://github.com/qhmiao/LightTranslate/releases) 下载便携包。
2. 解压到固定目录，例如 `%LOCALAPPDATA%\LightTranslate`，双击 `LightTranslate.exe`。
3. 在桌面应用中选中英文，查看鼠标旁的译文。

启动后，系统托盘会出现“译”图标。右键可开关自动翻译、打开阅读设置、打开设置文件或退出；
双击图标可直接切换自动翻译。

轻译面向支持复制选区的桌面应用，默认跳过终端和纯中文选区，每次最多选择 2,000 个字符。
当前译文目标为简体中文。

## 按自己的习惯设置

在托盘“译”图标上右键，选择 **阅读设置…**：

- **字体和字号**：选择已安装字体，字号范围为 10–48，默认 14。
- **背景色**：留空跟随系统明暗主题，或使用选色器 / `#RRGGBB`；文字颜色自动调整，保持至少 4.5:1 的对比度。
- **自动消失**：默认按译文长度停留 6–22 秒，也可设置 1–120 秒。鼠标悬停时续时；移远、打字、点击外部、滚动外部页面或切换应用仍会提前收起。等待和错误提示保留原来的时长，短停留时间不会提前中断等待中的翻译。
- **最低英文占比**：范围 0–100%。大于 0% 时，A–Z/a–z 字母数占全部 Unicode 文字字母数的比例达到阈值（含等于）才翻译；中文和其他语言字母计入分母，标点、空白、数字、表情及组合音调符号不计入。全角字母和带重音字母计入分母，但不计为 A–Z/a–z。例如 `abc中文` 为 60%，`Hello中文` 约为 71%。

默认 0% 关闭新增比例限制，保留原有的非中文语言过滤（包括原先可翻译的其他语言）；
纯符号、纯数字、空白和纯中文选区仍跳过。大于 0% 时，选区必须含 A–Z/a–z。
这只是字符规则，不是自然语言识别。

保存后上述设置立即生效并写入应用目录的 `settings.json`；取消不会保存。
“恢复阅读默认值”仅重置这五项阅读选项的预览，点击保存后才生效。
旧配置缺少新字段时沿用原有字体、主题、停留时间和语言过滤。
其他高级配置仍可退出轻译后修改文件、重启生效。
可以通过 `ExcludedApps` 排除指定应用，填写进程名称，例如：

```json
"ExcludedApps": ["SomeApp", "AnotherApp"]
```

如需开机启动，在解压目录使用 PowerShell 7 运行：

```powershell
pwsh -File .\startup.ps1
```

取消开机启动：

```powershell
pwsh -File .\startup.ps1 -Remove
```

完整配置见 [settings.example.json](settings.example.json)。

## 文字如何处理

命中本地词库的文字在电脑上查询；其他文字发送给微软翻译，主服务失败时会尝试腾讯。
译文缓存保存在当前进程内，退出后清空。程序默认不记录选区原文或用户译文。
取词通过临时复制选区完成，并尝试恢复原剪贴板；系统或剪贴板管理器可能保留复制记录。

## 从源码构建

在 Windows 上使用 PowerShell 7 和 .NET Framework 4.8 编译器：

```powershell
git clone https://github.com/qhmiao/LightTranslate.git
cd LightTranslate
pwsh -File .\build.ps1
pwsh -File .\scripts\download-dictionary.ps1
```

构建结果位于 `dist\LightTranslate`。词库下载固定版本，并校验 SHA-256。
配置细节、自检、打包和兼容性记录见 [开发与测试](docs/TESTING.md)。

当前版本：[v0.1.0-alpha.2](https://github.com/qhmiao/LightTranslate/releases/tag/v0.1.0-alpha.2)。
欢迎通过 [Issues](https://github.com/qhmiao/LightTranslate/issues) 分享使用反馈。

## 许可证与致谢

轻译采用 [MIT](LICENSE) 许可证。英文词库来自
[ECDICT](https://github.com/skywind3000/ECDICT)，微软接口实现参考
[STranslate](https://github.com/STranslate/STranslate)。
上游版权声明和许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
