# 分发与安装

当前提供 Windows 11 x64 中文安装包和文件夹式便携 ZIP，两者均自带 .NET 10 运行时，入口为 `Qige.exe`。

| 形式 | 特点 |
| --- | --- |
| 安装包 | 向导选择目录，创建应用/数据入口，支持升级与卸载 |
| 便携 ZIP | 完整解压即可运行，程序文件和数据可一并移动 |
| 单 EXE 便携版 | 尚未作为分发方式提供 |

数据默认放在安装目录内的 `QigeData`。运行库文件属于程序，不能只复制 EXE。

## 构建

```powershell
.\publish.ps1 -Version 0.1.0-beta.8
.\publish.ps1 -Version 0.1.0-beta.8 -Installer

# 指定已有编译器：
.\publish.ps1 -Version 0.1.0-beta.8 -Installer -InnoSetupCompiler 'D:\Tools\InnoSetup\ISCC.exe'
```

默认构建 Release、自带运行时的 x64 包，关闭裁剪和单文件发布。发布编译正式项目，不包含测试与诊断程序。同版本目标已存在时拒绝覆盖。

| 输出位置 | 内容 |
| --- | --- |
| `artifacts/releases/Qige-<版本>-win-x64/` | 完整程序文件 |
| `artifacts/releases/Qige-<版本>-win-x64.zip` | 便携压缩包及 SHA256 |
| `artifacts/installers/Qige-<版本>-setup-win-x64.exe` | 安装器及 SHA256 |

`-FrameworkDependent` 可构建需另装 .NET Desktop Runtime 的开发包，不能与 `-Installer` 合用。

## 安装器

使用 Inno Setup，脚本为 `installer/Qige.iss`，编译入口为 `scripts/build-installer.ps1`。可使用现有编译器或项目内 `.build/tools/InnoSetup/ISCC.exe`。

已有完整包时可单独构建：

```powershell
.\scripts\build-installer.ps1 -PackageDirectory '.\artifacts\releases\Qige-0.1.0-beta.8-win-x64' -Version '0.1.0-beta.8'
```

安装采用当前用户权限。新安装默认在安装包所在盘的 `Qige` 文件夹，已有安装优先沿用原路径；向导允许修改，并显示数据目录。开始菜单包含程序和数据文件夹入口，桌面快捷方式可选。

安装前检查目录可写、程序生命周期标记和数据会话锁，不自动强制结束程序。升级前应在应用中退出并归还。

编译在 `.build/installer-staging` 暂存后移动到交付目录，保持该流程，避免部分工作区对直接编译的安装 EXE 附加执行限制。发布前核对交付位置文件的运行情况。

## 更新与卸载

- 更新保留整个 `QigeData`，只替换程序文件。
- 入口改名时只移除明确属于应用的废弃程序文件，不递归清理用户安装目录。
- 启动项升级/卸载只处理与当前安装路径完全匹配的命令，保留用户手工修改的入口。
- 卸载默认保留 `QigeData`，不删除用户自己放入目录的其他文件。
- 迁移软件应先退出归还，再移动完整程序和数据；避免合并不同安装的数据。

## 发布检查

1. 确认版本、提交、标签与打包内容一致。
2. 运行相关回归，核对工作区和忽略规则。
3. 排除个人数据、快捷方式、密钥、日志、测试与诊断程序集。
4. 生成并核对 ZIP 和安装器的 SHA256。
5. 在 GitHub Release 附上两类包及各自的校验文件，测试版标为预发布。
6. 验证下载附件、标签和 CI；保留兼容性与人工验收说明。

`scripts/test-installer.ps1` 提供隔离安装/升级/卸载检查。它会操作测试安装登记与快捷方式，若已登记正式安装则拒绝运行。验收覆盖交付位置与系统默认临时目录；不要用改名、复制或修改临时目录来绕过安装器失败。

## 单 EXE 的后续约束

WPF 自带运行时的单文件发布仍可能在托管入口之前解包原生库。`AppContext.BaseDirectory` 适合定位数据，但应用入口内设置 TEMP 无法控制更早的宿主解包阶段。

若要求双击单 EXE，且可控解包也留在软件目录，需要单独设计宿主/引导流程，并验证首次与后续启动、路径不可写、开机启动及升级恢复。当前不将单文件发布开关视为完整方案。
