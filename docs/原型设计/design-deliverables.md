# 「栖格 Qige」设计交付物与规范索引

本目录已归档「栖格 Qige」桌面应用管理面板与品牌色彩重构的全部设计产出与交互原型：

## 1. 交互原型 (可在浏览器中直接打开预览)

| 文件名 | 类型 | 说明 |
| :--- | :--- | :--- |
| [`qige_management_panel.html`](./qige_management_panel.html) | **主原型 (双模合一版)** | **最终交付主界面**。顶栏支持一键无缝切换：<br>① **空间沙盘模式 (Bento Canvas)**：宏观桌面微缩沙盘、抽屉大图标网格、右上角贴边/手柄状态、闪烁定位；<br>② **工作台详情模式 (Studio Mode)**：经典侧栏分类、纵向文件列表（含快捷方式角标等真实系统级 .ico）、右侧属性检查器。严格统一为海渊蓝白高定调色盘。 |
| [`qige_management_panel_v2.html`](./qige_management_panel_v2.html) | 独立原型 (Bento 沙盘版) | 纯粹桌面空间沙盘矩阵形态，提供大图标网格视图与列表视图切换。 |
| [`qige_blue_white_palette.html`](./qige_blue_white_palette.html) | 调色盘画板 | 方案一（海渊双蓝 · 推荐）、方案二（冰川瓷蓝）、方案三（皇家单色蓝）交互对比画板。 |
| [`qige_logo_redesign_presentation.html`](./qige_logo_redesign_presentation.html) | 品牌设计画板 | 品牌概念解析、符号语法、深浅两色 App 图标及任务栏多尺寸落地演示。 |

---

## 2. 品牌与应用图标矢量源文件 (SVG)

严格基于原版几何轮廓（保持外框线宽、圆角与右下角小方块比例不变），应用**方案一（海渊双蓝与天青）**：

| 文件名 | 适用场景 | 颜色定义 |
| :--- | :--- | :--- |
| [`qige_icon_light.svg`](./qige_icon_light.svg) | **浅色/白底背景** | 外框：海渊藏青 `#0F1D38`，独立方块：皇家宝石钴蓝 `#2563EB` |
| [`qige_icon_dark.svg`](./qige_icon_dark.svg) | **深色/黑底背景** | 外框：纯粹霜白 `#FFFFFF`，独立方块：透光天青蓝 `#38BDF8` |
| [`qige_app_icon_light.svg`](./qige_app_icon_light.svg) | **Windows 11 浅色应用图标** | 自带微倒角纯白底板与极细质感描边 (128×128) |
| [`qige_app_icon_dark.svg`](./qige_app_icon_dark.svg) | **Windows 11 深色应用图标** | 自带海渊深蓝底板 `#0B132B` 与夜景图标 (128×128) |

---

## 3. 设计规范与交互架构文档

- [`qige_design_specification.md`](./qige_design_specification.md)：
  * 两种抽屉心智模型（桌面整理抽屉的物理坐标记忆与归还机制 vs 文件夹映射抽屉的视窗与解除关联约定）；
  * 异常与状态处理（同名文件冲突自动重命名/保留策略、移动硬盘离线重连与重新定位）；
  * 关闭窗口常驻托盘 vs 完全退出程序的区分与安全保护；
  * 纯净蓝白色彩规范与字体层级。
