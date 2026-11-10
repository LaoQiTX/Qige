# 开发说明

## 环境

- Windows 11 x64，桌面交互检查需要已登录的图形会话。
- PowerShell 7。
- .NET 10 SDK，版本由根目录 `global.json` 指定。
- 编译安装包时使用 Inno Setup；应用本身不依赖它。

业务代码无第三方 NuGet 包依赖。`NuGet.Config` 默认无网络源，自带运行时发布按需从微软源获取运行时。构建脚本使用已有 SDK，不安装新环境。

## 工程结构

```text
src/DesktopGroups/             正式应用
tests/DesktopGroups.Tests/     隔离回归检查
tools/DesktopGroups.Diagnostics/ 桌面诊断工具
scripts/                       构建、发布与素材导出
installer/                     中文安装器与升级/卸载保护
assets/brand/                  应用和托盘图标源文件
assets/design/                 管理面板交互参考
docs/                          当前文档与运行截图
Qige.slnx                      解决方案
```

根目录 `build.ps1`、`publish.ps1` 是稳定入口，实现位于 `scripts`。`ProjectEnvironment.ps1` 将可控 SDK/NuGet 缓存、TEMP/TMP 和日志定向到 `.build`，结束后恢复进程环境。bin/obj 保持在各项目内。

## 编译和运行

```powershell
.\build.ps1 -Configuration Release
.\build.ps1 -Task Demo -Configuration Release
.\build.ps1 -Task Run -Configuration Release
```

`Demo` 使用隔离样例；`Run` 使用 `.local/QigeData` 并操作真实桌面。正式发行程序默认使用 EXE 旁的 `QigeData`。直接运行程序时，可传入绝对路径 `--data-dir`。

不要清理正在使用的数据目录。普通编译或 bin/obj 清理不应触及 `.local`。

## 检查入口

```powershell
.\build.ps1 -Task Test -Configuration Release
```

| Task | 检查内容 |
| --- | --- |
| `Test` | SelfTest、StorageTest、LayoutTest、P0Test |
| `SelfTest` | 数据路径、原子写入、兼容迁移、冲突与中断 |
| `StorageTest` | 收纳、同步、拖回、校验搬移及恢复 |
| `LayoutTest` | 所有权、安全移除、吸附、DPI 和工作区约束 |
| `P0Test` | 后台 STA、图标缓存、重试、快照合并和采样上限 |
| `ManagerTest` | WPF 管理面板、后台收纳、恢复、退出及 PNG 导出 |
| `ResidentTest` | 托盘、单实例、环境消息和隔离宿主重建 |
| `StorageRenderTest` | 动画、悬停、OLE 回执与 120 次移动 |
| `IconTest` | 默认、显式、相对路径及 Axolotl 图标 |

每次测试在 `.build/test-results/runs/<id>` 建立独立数据。交互检查与单元/文件检查的边界见[验证说明](validation.md)。

诊断项目提供 `Probe`、`ShellTest`、`VisibilityProbe` 等入口。其默认界面含桌面实验操作，不作为普通使用入口，也不随应用分发。

## 发布与素材

```powershell
.\publish.ps1 -Version 0.1.0-beta.9
.\publish.ps1 -Version 0.1.0-beta.9 -Installer
pwsh -STA -File scripts/build-brand.ps1
```

发布脚本拒绝覆盖已有同版本产物，构建新版本时同步更新版本配置。完整分发规则见[分发与安装](distribution.md)。

品牌 SVG、ICO 以 `assets/brand` 为准。素材导出默认写入 `artifacts/brand`；只有 `-UpdateAssets` 才更新规范资源。品牌预览画板只作为本地导出，不进入项目首页。

## 文档与提交

文本使用 UTF-8、LF。变更文件状态或搬移协议需运行 `Test`；变更窗口、拖放或绘制再执行相关交互检查。文档改动核对链接、截图和功能边界，避免把规划写成现有能力。

不提交 `.build`、`.local`、`artifacts`、bin/obj、`QigeData`、个人快捷方式、凭据或日志。文档使用固定文件名，截图集中于 `docs/images`。版本历史在 Git、CHANGELOG 和 GitHub Releases 中维护。
