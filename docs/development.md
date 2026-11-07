# 开发说明

需要 Windows 11 x64、现有 PowerShell 7 和 .NET 10 SDK，版本见 `global.json`。脚本使用现有 SDK，不安装新环境。`NuGet.Config` 默认无网络包源；首次自带运行时发布仅显式使用微软 NuGet 源获取运行时。

## 构建与检查

根目录 `build.ps1` / `publish.ps1` 是稳定入口，实现位于 `scripts`。统一环境设置在 `scripts/ProjectEnvironment.ps1`，执行结束恢复调用进程环境。缓存、TEMP/TMP、NuGet、MSBuild 日志定向到 `.build`，标准 bin/obj 留在各项目目录并忽略提交。

- `-Task Test`：路径、旧数据迁移和收纳恢复；每次创建独立的 `.build/test-results/runs/<id>`。
- `-Task SelfTest`：数据目录与迁移；包括活动入口、NTFS 下载来源流、配置备份、幂等、中断和冲突。
- `-Task StorageTest`：收纳、同步、拖回、冲突和中断恢复，不移动真实桌面文件。
- `-Task ResidentTest`：需交互桌面，使用内存启动项模拟，不更改实际开机启动。
- `-Task StorageRenderTest`：需交互桌面，创建样例分区，检查动画、OLE 和 120 次移动。
- `-Task IconTest`：隔离样例；本机存在 Axolotl 快捷方式时可选只读验证。
- `-Task Probe` / `ShellTest` / `VisibilityProbe`：独立诊断项目，仅检查桌面接口。
- `-Task RenderTest`：历史原型绘制检查，属于诊断项目。

诊断工具不随应用发布。工具直接启动且不指定模式会打开历史诊断窗口，其中的旧图标位置实验可能改变桌面位置，应只在需要的人工验证中使用。

## 本机数据与发布

开发运行传入 `--data-dir <项目>/.local/QigeData`，与 bin/obj 清理分离。发行程序默认在 exe 旁创建 `QigeData`。直接 `dotnet run` 未指定参数时也采用此默认行为。

开发机旧数据可在软件退出后执行 `.\build.ps1 -Task MigrateData`，将项目根 beta.1 `Data` / `Storage` 迁入 `.local/QigeData`。不要合并不同安装的数据。迁移支持同盘目录重命名，保留 NTFS 数据流；跨盘需先复制完整旧安装到目标磁盘，再就地升级。

`.\publish.ps1` 生成自带运行时 ZIP；`-FrameworkDependent` 生成依赖现有运行时的小包。产物在 `artifacts/releases`，附带 SHA256；已存在同名目录或 ZIP 时拒绝覆盖。发布仅编译正式项目。界面从程序集读取版本。

品牌源文件和 ICO 在 `assets/brand`。`scripts/build-brand.ps1` 将各尺寸导出至 `artifacts/brand`；指定 `-UpdateAssets` 才更新需提交的 SVG、ICO 与预览，不提交所有重复 PNG。

## 提交约定

文本统一 UTF-8、LF，规则在 `.editorconfig` 和 `.gitattributes`。修改搬移、恢复或数据布局时运行 `Test`；修改窗口、拖放、绘制时再运行对应交互检查。

不提交 `.local`、`.build`、`artifacts`、bin/obj、`QigeData`、旧版数据、本机技能配置、凭证、转储和日志。不要添加真实个人快捷方式。发布前检查暂存 diff 和打包内容。历史设计保留在 `docs/history`，当前行为以使用说明、架构和代码为准。
