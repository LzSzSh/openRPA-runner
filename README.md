# Maxwell麦威数字员工

Maxwell 是一个面向 OpenRPA workflow 的独立桌面执行器。用户导入 OpenRPA 导出的 workflow JSON 后，由 Maxwell 自带的 RuntimeHost 加载其中的 XAML 并执行；目标电脑不需要安装或启动 OpenRPA。

## 当前实现

- 沿用原有 .NET 8 WPF GUI。
- 选择本地项目文件夹，扫描并管理 `_type = "workflow"` 的 JSON。
- JSON 直接导入本地项目，不再导入 OpenRPA LiteDB。
- 支持导入 OpenRPA `.rpaproj`：保留 workflow、图片/脚本等项目资源，并生成 `maxwell-project.json` 清单记录 workflow 与 NuGet 依赖声明。
- 通过独立的 `Maxwell.RuntimeHost.exe` 执行 workflow。
- RuntimeHost 从 JSON 读取 `Xaml`、`culture` 和 workflow 元数据。
- 扫描 XAML 中声明的程序集，在运行前报告缺失依赖。
- 使用 `ActivityXamlServices.Load` 和 `WorkflowApplication` 执行 Windows Workflow Foundation 活动。
- 每次执行使用独立进程，支持完成、失败、空闲、输出参数和强制停止。
- RuntimeHost 与 GUI 通过标准输入参数和逐行 JSON 输出通信，不依赖 OpenRPA IPC。

## 当前兼容边界

第一阶段已经打通 Windows Workflow Foundation 内置活动的执行链。OpenRPA 自定义活动的程序集和插件初始化正在按模块接入：

1. `OpenRPA.Interfaces` 和 OpenRPA 核心活动；
2. Windows UI Automation；
3. 浏览器 Native Messaging；
4. Office、图片、脚本、Java、SAP；
5. 子 workflow、OpenFlow 活动及持久化策略。

后续还将提供可选的 OpenCore 联网模式：由 Maxwell Agent 兼容 OpenCore 的机器人注册、远程调用、参数传递、状态回报和任务队列协议，再交给现有 RuntimeHost 执行。OpenCore 将作为可选调度控制平面；离线导入和本地执行仍不得依赖 OpenCore 或持续网络连接。

如果 workflow 引用了尚未打包的程序集，RuntimeHost 会在执行前返回明确的缺失程序集错误，不会静默执行不完整的流程。

架构和阶段说明见 [`docs/RUNNER_ARCHITECTURE.md`](../docs/RUNNER_ARCHITECTURE.md)。最小测试 workflow 位于 [`test-workflows/BasicWorkflow.json`](../test-workflows/BasicWorkflow.json)。

## 源码基线

OpenRPA 1.4.57.13 源码固定在：

```text
upstream/openrpa
commit b78115e45bcfdc1a22398662bac355fdd52fac87
```

该目录用于分析、编译和逐步提取兼容活动，不作为目标电脑上的 OpenRPA 安装。

## 开发构建

完整 GUI 构建需要 Windows、Visual Studio Build Tools 或包含 Windows Desktop SDK 的 .NET 8 SDK。

```powershell
cd OpenRpaWorkflowLauncher
dotnet build .\Maxwell.RuntimeHost\Maxwell.RuntimeHost.csproj -c Release
dotnet build .\OpenRpaWorkflowLauncher.csproj -c Release
```

在 Windows 上编译并暂存 OpenRPA 桌面活动运行库：

```powershell
..\scripts\build-openrpa-runtime.ps1 -Profile Desktop
```

脚本会验证固定的源码 commit、初始化源码依赖、编译所选活动模块，并将运行时放入 `runtime-staging`。RuntimeHost 会加载这些程序集，但不会启动 `OpenRPA.exe`。

运行 RuntimeHost 兼容性检查：

```powershell
.\Maxwell.RuntimeHost\bin\Release\net48\Maxwell.RuntimeHost.exe inspect `
  ..\test-workflows\BasicWorkflow.json
```

直接执行：

```powershell
.\Maxwell.RuntimeHost\bin\Release\net48\Maxwell.RuntimeHost.exe run `
  ..\test-workflows\BasicWorkflow.json
```

## 发布

生成免安装 .NET 8 Desktop Runtime 的独立包：

```powershell
.\publish-win-x64.ps1 -Mode Standalone
```

生成需要目标电脑安装 .NET 8 Desktop Runtime 的轻量 GUI 包：

```powershell
.\publish-win-x64.ps1 -Mode Slim
```

两种包都会包含：

```text
Maxwell麦威数字员工.exe
runtime\Maxwell.RuntimeHost.exe
runtime\Maxwell.RuntimeHost.exe.config
runtime\Newtonsoft.Json.dll
```

RuntimeHost 目标为 .NET Framework 4.8，Windows 10/11 通常已具备该运行环境；发布测试仍会显式检查。

## 导入完整 OpenRPA Project

在 Maxwell 中先选择一个本地项目文件夹，再点击“导入 JSON”，选择 OpenRPA 导出的 `.rpaproj` 文件。Maxwell 会在本地项目文件夹中创建同名子目录，保留导出目录结构，并生成：

```text
<ProjectName>\maxwell-project.json
```

该文件记录导入时发现的 workflow 和 `.rpaproj` 的 `dependencies`。Maxwell 会建立本地 workflow registry，可通过 `_id`、`Project/Workflow` 或 `Project\Filename` 找到同一项目中的 workflow。当前阶段会保留并报告这些依赖，尚未自动下载第三方 NuGet 包；`InvokeOpenRPA` 子 workflow 的实际执行将在下一项 P2 工作中加入。

## 本地设置

GUI 设置保存在：

```text
%APPDATA%\OpenRpaWorkflowLauncher\settings.json
```

旧版本保存的 `OpenRpaExePath` 会被忽略。
