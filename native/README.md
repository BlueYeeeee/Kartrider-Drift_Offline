# 单机原生兼容模块

`KdLocalNative.dll` 复用纯联机原生适配模块，只加载到本启动器刚创建、主线程尚未恢复的游戏进程。没有 Frida、通用 JavaScript 引擎或首次启动下载；本地网关、数据库、车辆性能和塔影子继续由原单机 C# 代码处理。不会修改游戏 EXE 或 SDK 文件，必要的进程内钩子仍保留。

原生实现来自联机仓库提交 `e10696cda8642b4742c7c63a347d681d5bff9bac` 的 `pure-online/native`。本次仅调整单机模块名称、状态通道和构建位置；C# 登录完成检查要求全部六项兼容标志就绪。

选择原因、构建代价及验收范围见 [单机兼容层决策](../docs/decisions/001-native-offline-compatibility.md)。

## 替换范围

| 旧脚本 | 处理方式 |
| --- | --- |
| `audit_reuse_ticket` | 按原 SDK 指令签名替换初始化及两处票据接口，保持原单机身份值。 |
| `local_vehicle_restore` | 匹配请求/回复编号、真实换车、允许的部件差异和原生调用点；确认两秒过期且只消费一次。 |
| `local_english_language` | 用引擎自身复制/释放函数扩展语言数组，保留原索引及限定调用点的 Polyglot 处理。 |
| `offline_analytics` | 在游戏调用前使已确认的遥测初始化入口不可用。 |
| `offline_playtime_notice` | 仅过滤已确认计时器调用点的 119 类型通知，转发其他通知。 |
| `observe_startup_latency`、`capture_latency_datagrams` | 从运行链移除诊断脚本；保留原单机网关的本地 ping 响应。 |

MinHook v1.3.4 提供指令重定位，nlohmann JSON v3.12.0 解析相关登录/预设报文。`vendor/` 包含原源码、许可证和逐文件 SHA256；构建前校验哈希。使用静态 C/C++ 运行库，不额外捆绑 VC 运行库安装器。

## 保留的约束

- 游戏、SDK、语言资源及原生 DLL 均按哈希核验；DLL 哈希在构建时写入嵌入清单。
- 每次启动使用随机名称与所属 PID 的共享状态区，只含就绪标志、错误码及计数。
- 模块通知与工作线程配合，SDK/遥测处理先于加载线程调用；握手期间工作线程不查询系统加载器。
- 等待原脚本使用的解包后指令签名，版本不符拒绝处理。登录完成需 SDK、加解密、语言、预设、通知和遥测六项全部就绪。
- 汇编跳板保留寄存器、标志、XMM0–5、栈参数及返回值，用线程局部帧区分并发调用；语言内存由引擎自身分配和释放。
- 原生模块一直保留到所属游戏退出；失败或限时登录检查只结束本次创建的游戏，然后释放状态区。

## 检查与验收边界

```powershell
./rebuild.ps1 -Tests
./build/native/NativeContracts.exe ./native/fixtures/vehicle-switch-477.json
./build/client/KartDriftLocal.exe --native-sdk-check <游戏SDK路径> ./build/native/NativeSdkProbe.exe <报告完整路径>
python -m unittest discover -s tests
```

`fixtures/vehicle-switch-477.json` 来自研发工作区已有的同名换车报文，只含装备预设和请求/回复字段，无账号或票据。372 项原生检查覆盖参数/返回值、语言内存与幂等、预设接受/拒绝、调用点/过期/单次消费、Polyglot 范围、全部通知类型、指令签名拒绝及并发线程。

2026-10-24 已通过 13 项单机/构建检查和真实 SDK 隔离验证。真实游戏使用独立新存档完成本地登录：一次 SDK 初始化、一次票据、一次登录、两次语言扩展，标志 63、错误 0。运行时模块采样确认已加载单机原生 DLL，未发现 Frida。退出后原有存档、`Game.ini` 和 `GameUserSettings.ini` 哈希保持一致。

登录检查使用 `--smoke-test <游戏目录> <独立数据目录> [语言]`，只运行到兼容模块全部就绪，随后停止自身游戏并恢复语言/图形配置。正常启动继续保留用户选择的语言。

真实换车界面、完整比赛结算、视觉语言、长时间提醒、异机、断网冷启动及挑战之塔实玩仍未验收。0.2.9 以预发布提供，升级时使用新目录并备份旧存档。
