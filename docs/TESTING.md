# 开发与测试

## 构建、自检与打包

在 Windows 上使用 PowerShell 7 和 .NET Framework 4.8 的 64 位编译器：

```powershell
pwsh -File .\build.ps1
pwsh -File .\scripts\download-dictionary.ps1
pwsh -File .\scripts\test.ps1
```

构建产物位于 `dist\LightTranslate`，下载的词库源文件保存在 `cache`。
词库脚本使用固定版本的 ECDICT，验证 SHA-256 后生成索引。

默认自检在本地运行，覆盖词库查询、剪贴板快照、终端与语言过滤、原生密码控件识别、
请求取消、对象回收和浮层窗口样式。它会创建自己的测试控件并渲染浮层，
不会显示窗口或向其他桌面应用注入输入。

阅读设置自检额外覆盖旧配置兼容、非法数值回退、字体/背景/时间/比例保存、
表单校验、保存失败不应用、取消与恢复默认值不误保存。配置写入先使用临时文件，
再原子替换原配置。测试只在输出目录创建自己的临时配置并在结束时清理。
自检还覆盖悬停续时、移远宽限期和超时边界，以及英文比例的等号边界、混合文字、
补充平面 Unicode 和标点/数字/空白/表情排除。背景文字对比度检查采样 4,096 个 RGB 颜色。

输出目录中会生成 `reading-settings-preview.png`、`reading-settings-custom-preview.png`
和 `reading-settings-error-preview.png`，使用真实 WPF 控件离屏渲染默认、定制及错误状态。
这些渲染图不代替实际桌面的焦点、键盘、选色器、DPI 和滚动交互验收。

输入监听回归测试会阻塞 UI 调度器，检查独立监听线程仍能处理消息；
随后移除本进程的原生监听，检查恢复机制自动重新安装，并确认捕获的消息交回 UI 调度器。

测试在线服务：

```powershell
pwsh -File .\scripts\test.ps1 -Online
```

在线自检发送三句固定示例文字。结果写入输出目录的 `self-test.json`；
运行日志为 `status.log`，两者均被 Git 忽略。

生成带词库、许可证和默认设置的便携包：

```powershell
pwsh -File .\scripts\package.ps1 -Version 0.1.0-alpha.2
```

GitHub Actions 会执行编译、词库构建和离线自检，并上传构建产物。

## 配置字段

托盘“设置…”分为翻译、浮层和启动三页。配置保存后立即生效，修改应用目录的 `settings.json` 则重启后生效。
自动翻译的托盘快捷切换与已打开面板同步；保存开关失败会恢复托盘勾选并提示错误。
`Diagnostics` 开关每两秒读取一次，可在排查后直接关闭，无需重启。

| 字段 | 默认值 | 作用 |
|---|---|---|
| `Enabled` | `true` | 自动划词翻译开关，也可通过托盘修改 |
| `ClipboardFallback` | `true` | 选区取词开关，关闭后停止所有取词 |
| `TranslationProvider` | `auto` | `auto` 保留本地词库优先、微软失败后腾讯；`microsoft` / `tencent` / `deepl` 仅调用选定服务并跳过词库 |
| `Target` | `zh-Hans` | 微软 / DeepL 的目标语言；本地词库和腾讯输出中文 |
| `MaxSelectionLength` | `2000` | 单次选区字符数上限 |
| `ExcludedApps` | `[]` | 排除的进程名称，可带或不带 `.exe` |
| `Diagnostics` | `false` | 触发诊断日志开关 |
| `BubbleFontFamily` | `Microsoft YaHei UI` | 浮层字体族，使用已安装的字体；缺失或未知字体回退默认 |
| `BubbleFontSize` | `14` | 浮层字号，10–48 DIP（随系统 DPI 缩放） |
| `BubbleBackground` | `""` | 空字符串跟随系统主题，或不透明的 `#RRGGBB`；自动选择对比度 ≥4.5:1 的文字颜色 |
| `AutoHideSeconds` | `0` | 0 沿用原有译文时长（6–22 秒）；1–120 为译文固定秒数，保留悬停续时及提前收起规则。等待和错误提示仍使用原来的时长，避免短设置值中断请求 |
| `MinEnglishPercent` | `0` | 0 关闭新增过滤并保留旧语言规则；1–100 为 A–Z/a–z ÷ 全部 Unicode 字母的最低百分比，等于阈值也触发 |

这些新字段缺失时保留原有默认行为；手动写入越界或非有限字号、非法颜色、未知字体时回退对应默认值。
英文比例排除空白、标点、数字、表情及组合音调符号；中文和其他语言字母计入分母。
补充平面的字母按 Unicode 标量计一次，全角 Latin / 重音字母计入分母但不计英文分子。
大于 0% 时没有 A–Z/a–z 的文本不触发；0% 时无文字字母或纯中文仍被原有规则跳过。

`ClipboardFallback` 沿用早期字段名，同时控制剪贴板取词和独立进程选区读取。

面板纳入 `Enabled`、`ClipboardFallback`、`Target`、`MaxSelectionLength`、`ExcludedApps`
和五项阅读选项。目标语言只校验修改后输入的语言标签语法，不联网获取语言列表；服务是否支持自定义标签由所选服务决定。
修改后的选区上限要求正整数，排除应用支持换行及中英文逗号/分号，按进程名校验、去重。
未改动的原有自定义目标、长度和排除数组逐值保留；隐藏的 `Diagnostics` 克隆保留，不因普通设置或恢复默认值而重置。

### 开机自启

开机自启以当前用户启动文件夹的 `LightTranslate.lnk` 和实际目标路径为准，不额外持久化一个可能失真的 JSON 布尔值。
读取还会保守检查当前用户 `StartupApproved/StartupFolder` 的常见 12 字节记录：已禁用或未知状态不视为启用，且不修改审批注册表。
同名快捷方式指向其他目标或带有参数时不覆盖/删除；读取失败显示无法确认，其他配置仍可保存。
仅用户改变自启选择并保存时写入当前目录的快捷方式，不使用服务、计划任务、提权或安全设置变更。
写入后重新读取验证；配置保存失败时恢复启动项原字节，无法恢复时明确报错并显示重新读取的状态。
回滚发现启动项已被其他程序更改时拒绝覆盖新内容。

统一设置测试使用模拟后端覆盖往返、取消、保存失败、部分失败、未知/禁用状态及托盘同步；
真实 WScript 快捷方式测试使用输出目录中的独立临时文件夹，验证创建/读取/移除、原字节恢复、异目标保护及 I/O 失败，
不使用真实 Startup 目录、不读取或写入真实审批值。测试结束清理自身临时文件。
另生成 `settings-translation-preview.png`、`settings-startup-preview.png` 和 `settings-startup-error-preview.png`。
图中的自启状态是明确标注的隔离/模拟样例，不代表测试机真实状态。

## 取词与数据处理

程序优先使用带内部标记的 `Ctrl+C` 获取选区，快照仅接受支持的文本格式。原剪贴板无法完整安全备份时，改由独立进程读取 UIA 选区，保留剪贴板原内容。
如果用户或其他应用在取词期间更新了剪贴板，程序会避免覆盖新的内容。
系统剪贴板历史和第三方剪贴板管理器仍可能保留临时复制记录。

自动模式命中本地词库时不发送选区；未命中发送给微软，失败后可能发送给腾讯。
自动或微软模式启动时预热微软连接，不包含选区；腾讯和 DeepL 模式不预热。
明确选择单一服务时跳过词库且不回退；DeepL 需要用户保存密钥，可能计费。
微软和腾讯内置接口无需个人密钥；接口可用性由服务端决定，可能限流或变更。

普通服务和 DeepL 各有最多 256 条译文缓存，仅保存在当前进程内。程序不记录选区原文或用户译文。
诊断日志启用后会记录进程名、进程编号、字符数和触发状态；启动错误日志可能包含本机路径。

## 兼容性记录

首个版本已在开发者的 Codex 桌面环境中手动确认划词显示。早期剪贴板取词也在 Chrome 中验证过。
Windows 本地编译、离线自检、在线翻译测试及首次 GitHub Actions 构建均通过。

取词实现已经移除 UIAutomationClient 句柄；此前测试发现 UIAutomationCore 的原生对象回收会触发访问冲突。
后续兼容性验证重点为 Claude 桌面应用、长时间运行、多显示器及重启后的自启动。

0.1.0-alpha.2 将鼠标和键盘监听移至独立消息线程，回调只捕获输入并排队，
复制选区、跨进程检查和界面操作由 UI 线程执行。监听会在系统恢复、会话切换、
检测到输入与回调不一致时重新安装，并每 30 秒在鼠标按钮释放时刷新。
修复版已在 Codex 中再次手动确认显示译文。
Windows 的低级监听超时机制见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)。

取词依赖应用支持复制选区。原生密码编辑框和已识别的终端进程会被跳过；
网页或自绘密码字段依赖应用本身的复制规则，嵌入其他应用的终端需要单独验证。
从普通权限进程获取管理员应用的选区受 Windows 权限限制。

当前功能聚焦划词阅读：本地词库可离线，句子翻译使用网络。
OCR、朗读、翻译历史、原位替换、AI 润色、自动更新、安装器和代码签名暂未实现。

## 手动验证清单

记录 Windows 版本、轻译版本和目标应用的进程名称。

1. 拖选、双击选词，以及 Shift + 方向键选择文字。
2. 在线选择句子，断网后查询词库中的单词。
3. 打字、滚动外部页面、点击外部和切换应用，检查浮层隐藏及输入焦点。
4. 在浮层上悬停，滚动长译文。
5. 预先复制文本、HTML、图片和文件，检查取词后的剪贴板内容。
6. 翻译等待期间复制其他内容，检查新的剪贴板内容。
7. 连续快速选择不同文字，检查旧响应是否影响当前译文。
8. 检查终端、密码框、排除应用、多显示器、DPI 和屏幕边缘。
9. 持续运行数小时，并在重启 Windows 后检查自启动与划词。
10. 在设置的浮层页选择不同字体、10/48 字号及浅/深/中等亮度背景；确认实际浮层、预览、文字对比度、缩放和长译文滚动。
11. 将时间设为 1/10/120 秒，鼠标停在选区附近但不覆盖浮层检查超时；悬停后移回附近检查重新计时，移远和外部操作仍提前收起。
12. 比例设为 60%，选择 `abc中文` 应翻译，设为 61% 则不触发；选择 `abc中文！？ 123` 的结果相同。100% 时纯英文触发、中英混合不触发；0% 保留旧过滤。
13. 输入非法颜色、字号、秒数或比例，确认就地报错且不保存；取消和关闭设置不应用，重启后保留已保存的值且不改动自动翻译开关等其他设置。
14. 打开面板后切换托盘自动翻译，再保存其他选项，确认开关不会被旧值覆盖；目标语言、选区上限、排除应用和取词开关即时应用，取消不应用。
15. 自启验证只在隔离目录或模拟后端完成；打开、勾选、恢复配置默认值和取消均不得写入，只有保存才能修改；覆盖禁用/未知/异目标以及保存和回滚失败状态。真实用户启动项由用户自行选择，不作为自动测试动作。

## Clipboard capture regression

`./scripts/test-clipboard.ps1` tests capture cancellation, concurrent selections,
snapshot/read/restore races and bounded retries without accessing the clipboard.
`./scripts/test-clipboard.ps1 -Native` creates a private window station and desktop
before opening a clipboard. It aborts if isolation is unavailable; it never falls
back to the interactive clipboard. It tests exact byte preservation for Unicode,
HTML and RTF, empty/unsupported/oversized data, busy access, stale restoration and
delayed rendering. Both commands run in CI; neither needs the dictionary.

Capture now accepts only HGLOBAL text formats (Unicode/ANSI/OEM text, locale,
HTML Format and Rich Text Format), at most 64 formats, 8 MiB per format and
16 MiB total. Unknown or incomplete snapshots bypass Ctrl+C and try the isolated selection helper;
images, file lists and application-private formats are not silently discarded.
Delayed rendering may cause a snapshot to be skipped when its version changes.
A slow native clipboard owner can still delay GetClipboardData. Restore checks
the sequence while holding OpenClipboard, and preallocates every buffer before
EmptyClipboard. Windows provides no atomic multi-format replacement; a native
SetClipboardData failure is reported, and newer user data is never deliberately
replaced to retry a failed restore. Cancellation leaves the current clipboard
alone. A copy from the same source with no observable input/version distinction
cannot be attributed with certainty; this is not a proof of clipboard ownership.

The change bypasses the observed IDataObject/OLE IStream conversion crash path.
The original native crash has not been reproduced, and the faulty format or
provider has not been identified. These tests exercise the replacement behavior,
not the original corrupt COM pointer.

## Tray icon rendering

The offline self-test renders `tray-icon-preview.png` at 16/20/24/32/48/64 px
with three font/background combinations. Glyph ink bounds are centered inside
the solid circle. The selected reading font and automatic contrast foreground
are reused; font size is fitted to the icon, independently of bubble text size.
The test replaces an invisible NotifyIcon 100 times and checks USER handle growth.
This does not replace a live Explorer tray/DPI test. Saved appearance settings
update the running tray icon and dispose its previous Icon; canceled edits do not.

## Translation provider regression

`TranslationProvider` defaults to `auto` for old settings. The panel also offers
`microsoft`, `tencent` and `deepl`; explicit choices bypass the local dictionary and never
fall back to another service. Tencent currently outputs Chinese only. Its target
control is disabled without erasing the last target. Automatic mode
retains the existing Chinese dictionary and Tencent fallback behavior even when
Microsoft's target is another language. DeepL is never part of automatic fallback.

The offline self-test uses an injected HTTP handler to verify routing, automatic
fallback, explicit-provider failure, provider/target cache separation, canceled
in-flight responses, and settings save/cancel/reopen/live application. It makes
no network requests. Applying settings uses the existing capture/request
cancellation path before any new translation. Live service availability is not
covered by these fixture tests.


## Optional DeepL API

DeepL uses only `POST https://api.deepl.com/v2/translate` or
`POST https://api-free.deepl.com/v2/translate`, with `DeepL-Auth-Key` Authorization.
The key suffix `:fx` selects the latter, following the official
[authentication guide](https://developers.deepl.com/docs/getting-started/auth) and
[SDK endpoint selection](https://github.com/DeepLcom/deepl-dotnet/blob/main/DeepL/Translator.cs).
This does not infer a host from plan marketing names. DeepL says its API documentation
applies to all plans. Its [current plans](https://support.deepl.com/hc/en-us/articles/360021200939-DeepL-API-plans)
include Developer (a total, non-resetting allowance), Growth and Enterprise;
legacy Free/Pro are no longer sold. The app does not promise a recurring free quota.
Region-specific Enterprise endpoints are not included in this implementation.
No custom URL, redirect, automatic retry or automatic switch to/from DeepL is allowed.
`zh-Hans` and `zh-Hant` normalize to `ZH-HANS` and `ZH-HANT`.

`deepl-key.bin` contains only current-user DPAPI ciphertext. It is excluded from Git
and release artifacts. Plaintext is not serialized into settings, logs, error messages,
previews or test reports. Opening settings checks only file existence and never fills
the PasswordBox with a stored key. Replacement/clear is staged until Save; config-save
failure attempts to restore the previous encrypted file. Rollback failure is reported.
This is not a crash-atomic transaction across config, credentials and startup state.
DPAPI protects storage from other users, not processes already running as that user;
the key necessarily exists briefly in managed memory and the outgoing auth header.
The controller cancels pending translations on settings application. The DeepL client
also rejects a response if credentials changed during its request, checks credentials
before cache access and clears its separate cache when the credential scope changes.

Run `./scripts/test-providers.ps1` after building, or run the complete offline self-test.
The provider test is also a separate CI step. Only fictitious sentinel credentials,
injected HTTP handlers, a temporary DPAPI file and fake startup registration are used.
Coverage includes zero requests without a key, default/explicit routing, both hosts,
Chinese targets, provider/target/credential cache isolation, authentication failures,
quota exhaustion, 429, invalid targets, request size, malformed responses, timeout,
network failure, cancellation, stale responses, and save/clear/cancel/reopen/rollback.
No live DeepL account, registration, key validation or billed request is exercised.
The generated settings previews show an empty credential field.


## 图片或专用格式剪贴板下的选区读取

仅允许文本快照的守卫会拒绝图片和应用专用格式。这是保护原剪贴板所需的行为，
但不能因此让划词静默失效。快照失败时，现在通过短时独立进程
`LightTranslate.SelectionHost.exe` 请求应用的 UIA TextPattern 选区，不读取或修改剪贴板。
主程序仍不引用 UIAutomationClient，不持有跨进程 UIA/COM 对象，也不恢复原 OLE 快照路径。
辅助进程只接受源窗口、焦点窗口句柄和长度上限，验证窗口归属并拒绝密码字段；
选区通过匿名标准输出管道返回，不写文件或日志。辅助进程有 1.5 秒的自身终止期限，
主程序异步等待上限为 2.2 秒，并丢弃已取消或焦点改变后的响应。
这不是权限沙箱；它隔离 UIA 原生故障，不会终止用户源应用或轻译主进程。
无法安全读取、辅助程序缺失或超时时，浮层提示取词失败并保留剪贴板。

`./scripts/test-selection.ps1` 使用真实 Windows 编辑控件和真实外部辅助进程，
合成选区经过拒绝备份分支、选区读取、英文比例检查，最终命中本地词典。
它同时验证空选区、密码框、错误源窗口、取消和输出长度上限，不调用在线翻译，
不读取用户剪贴板内容，也不改变用户窗口焦点；只记录剪贴板序列号是否保持不变。
`./scripts/test-clipboard.ps1 -Native` 在私有 window station 中额外验证真实编辑控件的
复制命令、原生快照恢复，以及专用格式不会被覆盖；所有数据均为合成数据。
这些测试不等于已验证每个浏览器、Word、管理员权限窗口或远程桌面的实际兼容性。

### Chromium 选区定位

鼠标拖选网页文字不一定把键盘焦点移到该文档。辅助进程优先从鼠标释放位置的
UIA 元素向上查找 TextPattern，随后才尝试原生焦点和 UIA 键盘焦点；每一步验证
源窗口归属，最多查找 32 层，并保留独立进程的总超时限制。它不扫描其他文档、
不读整个页面或 ValuePattern，也不修改 Chromium 的可访问性设置。
主程序仅记录固定失败码，例如无 TextPattern、空选区、密码字段、窗口不符、
深度上限、提供方异常和超时；不记录控件名称、选区原文或页面内容。
真实控件测试不覆盖网页布局、iframe 与键盘焦点差异，仍须进行实际拖选验收。