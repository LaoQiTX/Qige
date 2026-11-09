# 分发与安装包

当前正式项目为 .NET 10 / WPF / Win32 应用。源码项目和命名空间沿用 `DesktopGroups`，正式构建的程序集与入口为 `Qige.dll` / `Qige.exe`。测试与诊断工具继续使用原名称。历史已发布包的入口仍为 `DesktopGroups.exe`。

## 改动范围

| 方案 | 主体代码改动 | 发布侧改动 | 主要限制 |
| --- | --- | --- | --- |
| 当前文件夹便携包 | 已完成入口改名、图标资源引用及旧启动项升级 | 现有 ZIP 脚本 | 运行库文件较多 |
| 自带运行时的单 EXE | 较少，现有 `AppContext.BaseDirectory` 适合保持 exe 旁数据目录 | 增加单文件发布模式、原生库解包设置和验证 | WPF 原生库仍需运行时解包；不能把“分发单文件”等同于“运行只产生一个文件” |
| Inno Setup 安装包（已实现） | 增加供安装器检查的应用生命周期标记 | 安装脚本、快捷方式、升级与卸载保护 | 需要安装器构建工具；应选择可写安装目录 |

两种路线均无需重写抽屉、Shell、绘制或数据存储层。当前已选择安装包路线，继续保留 ZIP 便携版。`QigeData` 是固定的数据目录，始终位于用户选择的安装目录内，不隐藏到系统临时目录。

## 构建安装包

需要 Inno Setup，当前使用 7.1.1 验证。可从 [Inno Setup 官方发布页](https://github.com/jrsoftware/issrc/releases) 获取编译工具，安装或以官方 `/PORTABLE=1` 模式放到项目 `.build/tools/InnoSetup` 中。

```powershell
.\publish.ps1 -Installer
# 编译器不在自动搜索位置时：
.\publish.ps1 -Installer -InnoSetupCompiler 'D:\Tools\InnoSetup\ISCC.exe'
# 已有当前版本的文件夹包时，无需重复发布：
.\scripts\build-installer.ps1 -PackageDirectory '.\artifacts\releases\Qige-0.1.0-beta.8-win-x64' -Version '0.1.0-beta.8'
```

输出为 `artifacts/installers/Qige-<版本>-setup-win-x64.exe`，附 SHA256。安装包包含 .NET 运行时，不支持与 `-FrameworkDependent` 同用。脚本拒绝覆盖已有同版本产物，也拒绝把个人数据、快捷方式或测试工具加入安装包。

安装器先在 `.build/installer-staging` 编译，再同盘移动到发布目录，以保留暂存文件的权限。不要改为直接在发布目录编译：部分工作区会向其附加可执行文件限制，导致安装 EXE 在系统临时目录创建解包目录时报“错误 5：拒绝访问”。这与 `QigeData` 无关，无需修改系统临时目录权限或使用管理员运行。

验收必须直接运行交付位置的安装 EXE，使用默认系统 `TEMP/TMP`，不能复制改名后运行或先改到项目临时目录。`scripts/test-installer.ps1` 提供隔离安装、升级、卸载和重装检查，会在项目 `.build` 下创建样例，结束后移除测试安装项并保留数据校验记录；若已经登记正式 Qige 安装则拒绝运行。

新安装默认使用安装包所在盘的根目录下 `Qige` 文件夹，例如安装包在 F 盘时默认 `F:\Qige`；网络位置启动时回退系统盘的 `Qige` 文件夹。已有安装升级时优先沿用原目录，避免换到另一套数据。中文向导仍允许手动选择可写目录，并在安装前显示 `QigeData` 完整位置；开始菜单同时提供应用和数据文件夹入口。安装器不自动结束应用，升级与卸载前检查应用生命周期标记及默认数据会话锁。升级保留数据；卸载只删除安装过的程序文件和快捷方式，保留 `QigeData`，并仅移除完全匹配默认路径的栖格开机启动项。

## 单 EXE

基础发布参数如下，独立输出到测试目录，不能覆盖正式包：

```powershell
dotnet publish src/DesktopGroups/DesktopGroups.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -p:DebugType=embedded `
  -o .build/single-file-evaluation
```

`IncludeNativeLibrariesForSelfExtract` 将原生库也打入 EXE，启动时仍要提取这些库。当前使用 `AppContext.BaseDirectory` 定位程序和数据目录，没有依赖 `Assembly.Location` 读取磁盘程序集；WPF 图标通过程序集资源 URI 加载。应保留 `PublishTrimmed=false`，不能直接改为裁剪或 NativeAOT 来追求体积。

关键约束是解包位置：Windows 下默认使用 `%TEMP%/.net`。已有 `AppStorage.ConfigureTemporaryDirectory()` 在托管入口执行，晚于宿主解包，无法控制这一阶段。可在启动前设置 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，但只在 CMD 启动器里设置不能覆盖用户直接双击 EXE 的情况。若必须同时满足“真正双击一个 EXE”和“全部解包位于所选目录”，还需要宿主／引导程序设计，工作量会明显高于开启单文件发布开关。

发布前需在无 .NET 的 Windows 环境验证首次与再次启动、原生库加载、图标和托盘、开机启动、升级后恢复，以及解包目录不可写时的行为。程序仍在 exe 旁生成 `QigeData`，不能将其打入 EXE。

## 安装包

使用 Inno Setup 将自带运行时的文件夹包封装成一个安装 EXE，安装后展开运行库；用户从开始菜单或桌面启动 `Qige.exe`。这种方式延续当前的数据与缓存约定。

- 使用当前用户安装，允许选择可写目录；默认不使用需要提权写入的 Program Files。若以后要支持该目录，应显式指定独立可写数据目录，并一起调整启动和升级策略。
- 固定安装器应用标识，升级优先沿用原安装路径。覆盖前要求退出应用并归还已收纳内容，避免文件被占用和数据仍在移动。
- 快捷方式指向 `Qige.exe`；可复用应用内的开机启动选项。旧 EXE 改名的启动项由应用在下次正式启动时精确升级，不覆盖用户手动修改的命令。
- 卸载仅删除安装器管理的程序文件和快捷方式，默认保留整个 `QigeData`，不递归删除安装目录或映射的真实目录。保留数据后应告知位置，便于恢复。
- 旧版本升级时只清理已确认的旧程序文件，包括 `DesktopGroups.exe` 和配套程序集，不删除用户放入的其他文件。
- 打包流水线新增安装器编译和 SHA256；安装、原目录升级、卸载及重装均需在干净环境验收。代码签名可随后接入，两种 EXE 方案都受益。

单文件便携发布仍属于后续候选方案，尚未作为正式构建模式提供。
