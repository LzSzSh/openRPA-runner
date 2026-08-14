# Maxwell 麦威数字助手最终交接文档

> 冻结日期：2026-08-14  
> 产品形态：Windows 桌面独立 OpenRPA workflow 执行器  
> 主源码仓库：<https://github.com/LzSzSh/openRPA-runner>  
> OpenRPA 定制仓库：<https://github.com/LzSzSh/openrpa>  
> 正式发布模式：`publish-win-x64.ps1 -Mode Final`

## 1. 交接结论

Maxwell 已从“调用本机 OpenRPA 的启动器”演进为自带运行库的独立执行器。目标电脑不需要安装或启动 OpenRPA；程序读取共享目录中的 OpenRPA workflow JSON，由独立 `Maxwell.RuntimeHost.exe` 加载其中的 WWF/XAML 和 OpenRPA 活动程序集执行。

正式版主要能力：

- .NET 8 WPF 中文桌面界面；
- 直接扫描公司共享工作流库；
- 显示项目、工作流、兼容性、运行状态和日志；
- 独立进程执行、停止、失败隔离和输出参数回传；
- 内置 Chrome++、私有 MV3 扩展和 Native Messaging Host；
- 浏览器运行模式/设计器编辑模式切换；
- Windows UI Automation 浏览器元素定位兼容；
- 每周、每月定时任务；
- 运行/停止快捷键；
- 自包含 win-x64 单文件 GUI 发布。

## 2. 最终架构

```text
Maxwell麦威数字助手.exe                 .NET 8 WPF GUI
├── 扫描共享项目与 workflow JSON
├── 保存设置、快捷键和定时任务
├── 切换运行/编辑模式
└── 启动 runtime/Maxwell.RuntimeHost.exe
    ├── 读取 JSON 中的 Xaml、culture 和元数据
    ├── 发现并加载 WWF/OpenRPA 活动程序集
    ├── 建立子 workflow 注册表
    ├── 初始化 OpenRPA RobotInstance/插件/扩展
    ├── 预检并连接浏览器 Native Messaging
    ├── 使用 WorkflowApplication 执行
    └── 通过逐行 JSON 返回日志、状态、错误和输出

runtime/browser-launcher/chrome.exe      Maxwell.ChromeLauncher
└── 透明接管 workflow 的 StartProcess("chrome")
    ├── 定位内置 Chromium 真正引擎
    ├── 附加自动化和无障碍参数
    └── 等待主窗口与 UI Automation Document 就绪

runtime/Maxwell.NotificationHost.exe     WPF 通知宿主
└── 在 RuntimeHost 外显示 OpenRPA 通知，避免执行进程/UI 线程耦合
```

GUI 与 RuntimeHost 不使用 OpenRPA IPC。每次执行使用单独进程；停止时终止当前 RuntimeHost 进程树，但浏览器默认保留。

## 3. 源码与项目说明

| 路径 | 用途 |
| --- | --- |
| `App.xaml(.cs)` | WPF 启动、全局异常捕获和 `%APPDATA%` 崩溃日志 |
| `Views/MainWindow.xaml(.cs)` | 主界面、工作流列表、定时任务、设置页与快捷键捕获 |
| `ViewModels/MainViewModel.cs` | 页面状态、扫描、运行、停止、模式切换、快捷键和定时调度 |
| `Models/` | 设置、工作流、兼容性、最近项目、浏览器策略、定时任务模型 |
| `Services/WorkflowScanner.cs` | 递归扫描项目 JSON 并生成工作流列表 |
| `Services/WorkflowCompatibilityScanner.cs` | 从 XAML 判断活动模块和可执行性 |
| `Services/WorkflowRegistry.cs` | 子 workflow 名称/ID/路径解析 |
| `Services/MaxwellRuntimeRunner.cs` | 启动 RuntimeHost、传参、解析 JSON 结果和停止进程树 |
| `Services/BundledChromeLocator.cs` | 定位、复制、校验内置浏览器、配置、扩展和 Host |
| `Services/ChromeAutomationStatusService.cs` | Native Messaging 注册与运行/编辑模式切换 |
| `Services/AppSettingsService.cs` | 用户设置的原子读写 |
| `Services/AutomationScheduleService.cs` | 定时任务的原子读写 |
| `Maxwell.RuntimeHost/` | .NET Framework 4.8 无界面执行核心 |
| `Maxwell.ChromeLauncher/` | 浏览器启动与 UIA 就绪等待辅助进程 |
| `Maxwell.NotificationHost/` | 独立 WPF 通知窗口 |
| `patches/` | 对固定 OpenRPA 基线的完整补丁和历史补丁 |
| `publish-win-x64.ps1` | 构建、裁剪、脱敏、校验、压缩发布包 |

`MaxwellOpenRpa.sln` 已包含 GUI、RuntimeHost、ChromeLauncher 和 NotificationHost 四个项目。

## 4. 技术基线

### Maxwell

- GUI：`net8.0-windows`、WPF、win-x64、单文件、自包含；
- RuntimeHost：`.NET Framework 4.8`；
- ChromeLauncher：`.NET Framework 4.8`；
- NotificationHost：`.NET Framework 4.8` WPF；
- RuntimeHost JSON：Newtonsoft.Json 13.0.3；
- 推荐开发工具：Visual Studio 2022，安装“.NET 桌面开发”；
- 当前构建机 SDK：.NET SDK 8.0.422。

### OpenRPA

- 官方仓库：<https://github.com/open-rpa/openrpa>；
- 官方固定基线：`b78115e45bcfdc1a22398662bac355fdd52fac87`；
- 版本：OpenRPA 1.4.57.13；
- Maxwell 定制提交：`db3aff08cc2e6acf2e84256adcfb8b096f18d66c`；
- Maxwell 定制分支：`agent/maxwell-runtime-20260814`；
- 完整补丁：`patches/0001-Add-Maxwell-runtime-compatibility.patch`；
- 许可证：Mozilla Public License 2.0；发布运行库保留 `OPENRPA-LICENSE.txt`。

不要直接升级 OpenRPA master。升级会影响 XAML 类型、程序集版本、浏览器 Host 协议和选择器行为，必须重新做真实工作流回归。

## 5. 共享工作流库

公司默认配置位于 `company-settings.json`：

```json
{
  "SharedLibraryFolder": "\\\\new-server\\0-共享文件夹\\5-物流科\\RPA(勿删)\\项目"
}
```

用户可在设置页覆盖。共享根目录的每个直接子文件夹视为一个项目；项目内递归扫描 `_type = "workflow"` 的 JSON。workflow、子 workflow、脚本、图片、Office 文件及其他资源必须保持设计时的相对目录结构。

程序直接读取共享盘，不复制 workflow 项目到本地。发布人员应先在临时目录完成更新，再整体切换项目目录，避免用户执行期间覆盖文件。

## 6. 工作流执行规则

1. GUI 扫描 JSON、提取 XAML 并计算兼容性。
2. 用户在运行模式点击运行；编辑模式中运行按钮禁用。
3. GUI 启动独立 RuntimeHost，并传入 workflow、项目根目录和浏览器模式。
4. RuntimeHost 建立整个项目的 workflow 注册表，解析子流程。
5. 扫描 XAML 的程序集引用；缺少依赖时返回明确错误。
6. 浏览器流程先注册 Host、准备内置浏览器/配置、等待扩展连接。
7. 使用 `ActivityXamlServices.Load` 和 `WorkflowApplication` 执行。
8. `started/idle/completed/failed/aborted` 等事件以逐行 JSON 输出。
9. GUI 汇总日志、错误、输出参数和最终状态。

一个时间点只允许一个 GUI 发起的 workflow。RuntimeHost 对浏览器自动化增加了本机互斥，降低多个进程争用同一 Native Messaging 管道的风险。

## 7. 浏览器自动化

正式版是 `BundledOnly`，携带 Chrome++ 和扩展 ID `hpnihnhlcnfejboocnckgchjdofeaphe`。

关键实现：

- 私有扩展 Native Messaging 名称是 `com.maxwell.openrpa.msg`；
- 保留 manifest key，确保扩展 ID 不变；
- MV3 service worker 在启动、安装和内容脚本加载时主动重连；
- Host 与浏览器程序在共享盘发布时复制到 `%LOCALAPPDATA%`；
- ChromeLauncher 绕开会丢弃命令行参数的 Chrome++ 外层启动器；
- 正式版加入 `--force-renderer-accessibility`，确保浏览器页面暴露 UIA 树；
- RuntimeHost 在执行前探测扩展连接，连接失败会明确终止而不是静默卡住。

用户本机目录：

```text
%LOCALAPPDATA%\Maxwell\BundledChrome
%LOCALAPPDATA%\Maxwell\BrowserProfile
%LOCALAPPDATA%\Maxwell\NativeMessagingHost
%LOCALAPPDATA%\Maxwell\OpenRpaExtension
```

发布脚本会删除种子配置里的登录数据库、Cookie、历史、会话、表单数据、网络状态和日志。开发人员不得把运行过的完整 Chrome `Data` 目录直接发送给他人。

## 8. 运行模式与编辑模式

- 运行模式注册 Maxwell Host，允许执行流程。
- 编辑模式恢复正式 OpenRPA 的 `com.openrpa.msg` manifest，供 OpenRPA 设计器录制/高亮。
- 编辑模式必须在电脑上已有正式 OpenRPA；否则切换会提示失败。
- 模式切换后完全关闭 Chrome、OpenRPA 和 RuntimeHost，再重新打开。
- 当前模式保存在用户 `settings.json`。

## 9. Windows UI Automation

OpenRPA 定制增加了以下兼容性：

- 浏览器渲染窗口和 Document 树的等待与重试；
- Chromium/Chrome++ 进程族与窗口关联；
- UIA COM 暂时性失败重试；
- 选择器属性规范化与宽容匹配；
- 浏览器元素专用兼容层 `MaxwellChromeCompatibility.cs`；
- 登录页账号/密码输入框定位稳定性测试脚本。

相关测试：

```powershell
.\test-windows-uia-launch.ps1
.\test-maxwell-login-stability.ps1
```

测试会在 `%LOCALAPPDATA%\Maxwell` 下创建独立验证配置，不应使用真实账号或真实密码。

## 10. 定时任务

- 支持每周选多个星期，或每月选日期；时间精确到分钟。
- UI 每 20 秒检查一次。
- 允许 10 分钟补触发窗口，避免短暂卡顿错过整点。
- 到期时执行项目内按工作流名称、文件名排序后的第一个可识别 workflow。
- 同时只触发一个任务；程序正在执行或处于编辑模式时不会触发。
- Maxwell 必须保持打开；当前实现不是 Windows 服务，也不会在注销后运行。
- 任务保存到 `%APPDATA%\OpenRpaWorkflowLauncher\automations.json`。
- 删除任务前有确认；最后运行时间和状态会持久化。

## 11. 本机设置和日志

```text
%APPDATA%\OpenRpaWorkflowLauncher\settings.json      快捷键、共享目录、模式
%APPDATA%\OpenRpaWorkflowLauncher\automations.json   定时任务
%APPDATA%\OpenRpaWorkflowLauncher\crash.log          未处理异常
```

写入设置和定时任务采用临时文件加替换，降低突然断电导致 JSON 损坏的风险。

## 12. 从零构建

### 12.1 获取源码

```powershell
git clone https://github.com/LzSzSh/openRPA-runner.git OpenRpaWorkflowLauncher
git -C OpenRpaWorkflowLauncher switch agent/final-handoff-20260814

git clone https://github.com/LzSzSh/openrpa.git upstream\openrpa
git -C upstream\openrpa switch agent/maxwell-runtime-20260814
```

目录必须保持为：

```text
RPA执行器\
├── OpenRpaWorkflowLauncher\
├── upstream\openrpa\
└── scripts\build-openrpa-runtime.ps1
```

如果只使用官方 OpenRPA 基线，则在 `b78115e...` 上应用完整补丁：

```powershell
git -C upstream\openrpa checkout b78115e45bcfdc1a22398662bac355fdd52fac87
git -C upstream\openrpa am OpenRpaWorkflowLauncher\patches\0001-Add-Maxwell-runtime-compatibility.patch
```

### 12.2 生成 OpenRPA Desktop 运行库

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\build-openrpa-runtime.ps1 -Profile Desktop
```

输出：`OpenRpaWorkflowLauncher\runtime-staging`。脚本会校验官方基线、构建活动程序集、提取/改写私有扩展、配置 Chrome++ 并写入运行库清单。

### 12.3 构建解决方案

```powershell
dotnet build .\OpenRpaWorkflowLauncher\MaxwellOpenRpa.sln -c Release
```

### 12.4 生成正式包

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\OpenRpaWorkflowLauncher\publish-win-x64.ps1 -Mode Final
```

输出：

```text
OpenRpaWorkflowLauncher\publish\Maxwell办公助手-20260814\
OpenRpaWorkflowLauncher\publish\Maxwell办公助手-20260814.zip
```

`Final` 模式会自包含 .NET 8、包含内置浏览器、加入 UIA 参数、裁剪无关架构/语言/PDB/缓存、删除浏览器隐私数据，并逐项校验关键文件。

## 13. 发布包关键文件

```text
Maxwell麦威数字助手.exe
company-settings.json
browser-mode.json
版本说明.txt
install-browser-automation.ps1
install-openrpa-extension.ps1
test-windows-uia-launch.ps1
test-maxwell-login-stability.ps1
runtime/
├── Maxwell.RuntimeHost.exe
├── Maxwell.NotificationHost.exe
├── OpenRPA*.dll / OpenRPA.exe（仅作为程序集加载）
├── OpenRPA.NativeMessagingHost.exe
├── chromemanifest.template.json
├── maxwell-openrpa-extension/
├── browser-launcher/chrome.exe
└── chrome-portable/
```

不要只复制顶层 EXE。浏览器、活动程序集和辅助进程都在 `runtime` 中。

## 14. 修改指南

| 需求 | 首选位置 |
| --- | --- |
| 改界面布局、颜色、页面 | `Views/MainWindow.xaml` |
| 改按钮行为、扫描、运行和定时逻辑 | `ViewModels/MainViewModel.cs` |
| 改用户设置字段 | `Models/AppSettings.cs`、`Services/AppSettingsService.cs` |
| 改共享目录扫描 | `Services/WorkflowScanner.cs` |
| 改兼容性判断 | `Services/WorkflowCompatibilityScanner.cs` |
| 改进程通信和停止 | `Services/MaxwellRuntimeRunner.cs` |
| 改 XAML/OpenRPA 执行 | `Maxwell.RuntimeHost/Program.cs` |
| 改浏览器复制/路径/参数 | `Services/BundledChromeLocator.cs`、`Maxwell.ChromeLauncher/Program.cs` |
| 改 Host 注册和模式切换 | `Services/ChromeAutomationStatusService.cs` |
| 改 OpenRPA 活动行为 | `upstream/openrpa` 定制分支，随后重建 runtime-staging |
| 改发布内容 | `publish-win-x64.ps1` |

## 15. 验证记录

2026-08-14 冻结时执行：

- OpenRPA Desktop 运行库全量构建完成并生成新清单；
- `OpenRPA.csproj`：0 错误；
- Maxwell 四项目 Release 构建；
- `Final` 正式发布脚本；
- 发布包关键文件检查；
- 浏览器隐私文件扫描；
- ZIP 可读取性和 SHA-256 清单检查。

最终 Git 提交号、PR 地址、发布包哈希和实际执行结果记录在交付文件夹根目录的 `交付清单.txt`。

## 16. 已知限制与风险

1. OpenRPA 基线较旧，构建时 NuGet 会报告已知漏洞，包括旧版 `NuGet.Protocol` 高危公告和其他中低危公告。当前为兼容历史 workflow 固定版本，后续应单独建立依赖升级与回归项目。
2. 不能宣称兼容所有 OpenRPA 插件。Java、SAP、终端模拟器、PowerShell、OpenFlow 在线活动等未做全面企业实机回归。
3. Office 自动化依赖目标电脑的 Office 安装、位数和安全策略。
4. 浏览器自动化依赖扩展、Host 注册、浏览器版本、UIA 和进程状态；更新 Chrome++ 后必须回归。
5. 编辑模式依赖目标电脑安装正式 OpenRPA。
6. 定时任务依赖 Maxwell 前台进程持续运行，不是服务级调度。
7. 共享盘权限、断网和执行中覆盖项目文件仍可能导致失败。
8. 当前版本号仍是项目默认 `1.0.0.0`；后续建议引入显式语义化版本和自动 Release 标签。
9. `company-settings.json` 含公司内部共享路径，向公司外提供前必须替换或删除。

## 17. 安全与合规

- 不要把 `real-workflows`、真实账号、密码、Cookie、History、登录数据库或用户 `%APPDATA%` 打包。
- 不要提交 `bin`、`obj`、`publish`、`runtime-staging`、`.dotnet` 和临时验证目录。
- 公司外分发前复核 OpenRPA MPL 2.0、Chrome/Chrome++ 和所有第三方程序集许可。
- GitHub PR 目前用于保留冻结分支和审阅记录；合并前由接手人再次检查组织策略。
- 生产部署建议使用公司代码签名证书给 EXE/脚本签名，并通过受控共享盘发布。

## 18. 接手后的第一轮操作

1. 从交付文件夹解压正式运行版，在一台干净 Windows 10/11 电脑启动。
2. 检查共享工作流目录权限。
3. 在设置中确认运行模式，点击浏览器自动化检查。
4. 运行一个纯 WWF 流程、一个 Windows UIA 流程、一个浏览器流程和一个真实业务流程。
5. 创建一个 2 分钟后的定时任务，保持程序打开并确认触发。
6. 从 GitHub 冻结分支在全新目录执行“从零构建”。
7. 对比新生成 ZIP 与交付清单，确认关键文件和哈希。

完成以上步骤后，再合并 Draft PR 或打正式 Release 标签。
