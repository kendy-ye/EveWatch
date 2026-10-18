# EveWatch

EVE Online 多开监控与预警工具，Windows 桌面端，基于 .NET 8 + WPF。

## 功能

- **多账号监控**：自动发现 EVE 客户端窗口，展示在线状态与窗口信息；掉线后保留 60 秒「重连中」宽限，超时或未登录账号自动隐藏，重连后回到原排序位置。
- **ROI 敌对预警**：框选画面区域并设置颜色，命中后播放报警音，支持多账号、多 ROI、独立音量与全局静音。
- **ROI 并排窗**：将多个账号的 ROI 画面并排置顶显示，方便同时观察。
- **账号设置克隆**：把一个角色的 ROI、颜色与报警配置复制到其他账号。
- **游戏布局同步**：在多个客户端之间同步窗口布局。
- **全局快捷键**：默认不绑定，支持单键与 `Ctrl / Alt / Shift / Win` 组合键。
- **频道预警**：读取 EVE 聊天日志，从「本地」频道识别角色当前位置，从监听的预警频道解析星系名，按 `1-N`、`N-N` 跳数规则播放不同报警声音。
- **预警弹窗**：置顶显示最短星门路径或邻域关联图，支持多账号批次列表、备选路线、右键拖动与位置持久化；消息包含 `clr` 时视为安全，不触发报警。
- **本地星系图**：内置 8,490 个星系、6,989 条星门连接，运行时无需联网。

## 运行要求

- Windows 10 2004（19041）及以上 / Windows 11，x64
- 使用发布包：.NET 8 Desktop Runtime
- 从源码构建：.NET 8 SDK

## 构建与运行

```powershell
dotnet build src\EveWatch.App\EveWatch.App.csproj -c Release
dotnet run --project src\EveWatch.App\EveWatch.App.csproj
```

发布为免配置文件的程序目录：

```powershell
dotnet publish src\EveWatch.App\EveWatch.App.csproj -c Release --self-contained false -o dist\EveWatch
```

## 目录结构

```text
src/EveWatch.App        WPF 界面、窗口、全局快捷键、托盘
src/EveWatch.Core       采集、检测、报警、配置、日志预警、星系图
src/EveWatch.Selftest   命令行自检工具
docs                    设计文档
mockup                  界面原型（HTML）
tools                   星系图数据生成脚本
```

## 配置

- `evewatch.json`：功能配置，位于 exe 同目录，首次启动自动生成。
- `evewatch.ui.json`：窗口位置等界面状态，位于 `%APPDATA%\EveWatch`。
- 仓库不包含任何个人配置、日志或截图。

聊天日志目录默认自动检测以下位置，也可以在「频道预警」窗口中手动选择：

```text
系统「文档」\EVE\logs\Chatlogs
%USERPROFILE%\Documents\EVE\logs\Chatlogs
%USERPROFILE%\OneDrive\Documents|文档\EVE\logs\Chatlogs
各磁盘的 Documents / 文档 / OneDrive*\EVE\logs\Chatlogs
```

## 星系图数据

星系与星门数据由 EVE SDE（Fuzzwork CSV dump）生成，压缩后作为嵌入资源随程序发布：

```text
tools/build-galaxy-data.js
src/EveWatch.Core/Data/galaxy.json.gz
```

## 免责声明

本项目不是 CCP Games 的官方产品，与 CCP 无关联。EVE Online 及相关名称是 CCP hf 的商标。使用本工具产生的任何后果由使用者自行承担，请遵守游戏服务条款。

## 许可证

[MIT](LICENSE)
