# 当前发布说明

## 栖格 Qige 0.1.0-beta.8

面向 Windows 11 x64 的预发布测试版，提供中文安装包和自带运行时的便携 ZIP。[前往下载](https://github.com/LaoQiTX/Qige/releases/tag/v0.1.0-beta.8)。

### 本次更新

- 桌面环境恢复调度、连接失败退避、监听重建与低频完整同步。
- 多屏自动回收保留首选布局，接回后尝试恢复。
- “运行与恢复”面板及托盘入口：连接、后台任务、性能、冲突位置、检查重试和另存副本。
- 后台 STA 执行文件操作，图标共享缓存；后台结果合并用户期间的布局修改。
- 退出等待已排队操作，归还失败保留程序与文件。
- 中文安装向导、应用/数据入口、升级和卸载保留数据。

### 下载文件

| 文件 | 用途 |
| --- | --- |
| `Qige-0.1.0-beta.8-setup-win-x64.exe` | 中文安装包 |
| `Qige-0.1.0-beta.8-win-x64.zip` | 完整解压后运行 Qige.exe |
| 各自同名 `.sha256` | 下载校验 |

更新前在旧版选择退出并归还，备份并保留整个 `QigeData`。安装版沿用原安装目录；便携版使用完整新版文件。首次可以运行“打开样例.cmd”。

### 运行效果

![管理面板](images/manager-canvas.png)

[工作台详情](images/manager-studio.png) · [桌面抽屉](images/drawer.png) · [运行与恢复](images/recovery.png)

### 当前限制

只收纳个人桌面根目录 .lnk / .url。映射抽屉、普通文件/文件夹、公共桌面入口、跨抽屉拖放、原生右键菜单和全桌面双击隐藏尚未开放。

自动与隔离检查已通过；真实休眠、多屏变化、Explorer 重启和长时间共存继续验收。具体范围见[验证说明](validation.md)。

完整历史更新记录见 [CHANGELOG](../CHANGELOG.md)，历史安装文件见 [GitHub Releases](https://github.com/LaoQiTX/Qige/releases)。
