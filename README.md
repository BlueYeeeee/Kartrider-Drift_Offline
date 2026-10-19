# Kartrider: Drift Offline

## 启动方法

1. 在 [Releases](https://github.com/BlueYeeeee/Kartrider-Drift_Offline/releases) 下载 0.2.7 完整压缩包并解压。
2. 保留解压后的所有文件和文件夹，双击 `KartDriftLocal.exe`。
3. 选择包含 `KartDrift` 文件夹的游戏目录，选择游戏语言。
4. 点击“启动本地游戏”。游戏运行期间保持启动器开启，退出游戏后再关闭启动器。

需要自行准备匹配版本的游戏客户端(参考链接: https://pan.baidu.com/s/1iTHawG-eX3E_amkXbt1ABw?pwd=c84q 请勿运行压缩包内的“跑2体验版启动器.1.0.0.2c.exe” 而是使用我们开源的启动器)。若提示客户端版本不匹配，请使用对应版本。

## 0.2.7 更新

- 按原生配件表补齐默认喷焰、车牌、胎印、专属车身部件、角色动作与初始表情；保留自选装备、动作及表情。
- 修复简中文案误标为繁中、登录后覆盖原生繁中的问题，保留已校订简中地图名。
- 新档默认关闭辅助驾驶，已有存档继续保留个人设置。
- 接入挑战之塔 100 层的原生 SAV 影子，保留原文件名主体；修复对手名称本地化键被替换的问题。

挑战之塔完整开始、结算和进度流程仍未完成；本版未重跑真实客户端四语言和赛道验收。自动测试覆盖范围见 [发布说明](CHANGELOG.md)。

升级前正常退出游戏和启动器，备份旧版 `local-data` 后，将其复制到新版启动器同级目录。请保留完整 `TrialTower`、`Localization` 和 `runtime` 文件夹。

## 从源码构建

Windows 中运行 `powershell -ExecutionPolicy Bypass -File .\rebuild.ps1`，使用系统 .NET Framework 编译器生成启动器。运行时还需按 `THIRD-PARTY-NOTICES.txt` 准备 `runtime/frida-inject.exe`。`TrialTower` 为独立原生影子文件目录，构建时无需转换格式。

可用 Python 3 运行 `python -m unittest discover -s tests`，检查实际 C# 存档接口、默认装备、语言数据及影子文件哈希，不启动游戏。
