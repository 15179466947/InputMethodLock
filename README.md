# InputMethodLock — Windows 输入法锁定工具

一个零依赖的单文件 Windows 托盘工具，用于把输入法锁定为英文（或指定键盘布局），
防止打字/写代码/玩游戏时输入法状态乱跳。

- **交付形态：exe 可执行文件**（C# / .NET Framework 4.8，所有 Windows 10/11 自带运行时，双击即用，无需安装任何依赖）
- 单实例运行，最小化到系统托盘常驻，内存占用极小
- 锁定机制：100ms 轮询兜底 + 变化监听（键盘钩子只观察不吞键 + 系统 IME 事件）双保险

> 变更记录见 [docs/更新日志.md](docs/更新日志.md)；完整设计方案见 [docs/设计方案.md](docs/设计方案.md)。

## 功能

| 功能 | 说明 |
|---|---|
| 英文锁定 | 保留你选择的输入法，但强制其中英状态为英文（推荐，打代码场景） |
| 中文锁定 | 已是简体中文输入法时直接锁中文（不动布局）；当前是英文键盘时自动切到拼音（以其默认中文模式启动） |
| 布局锁定 | 只钉住键盘/IME 选择（精确到微软拼音等 IME），其中的中英文模式由用户自行切换 |
| 全局热键 | 默认 `Ctrl+`` 临时开/关锁定；可自定义、可在设置中按 Delete 清除（不启用热键） |
| 托盘控制 | **左键单击 = 快速开/关锁定**；右键菜单：启用开关、模式切换、设置、退出；彩色=启用、灰化=停用 |
| 停用恢复 | 可设置"停用锁定时切到指定输入法"（按完整 HKL 精确还原到微软拼音等 IME） |
| 启动提示 | 启动成功/失败气泡通知；"下次启动自动启用"为独立持久选项，不受运行时开关影响 |
| 例外程序 | 在设置中列出的进程（如 `wechat`、`code`）不锁定，自由输入 |
| 开机自启 | 设置中一键开关（写入 HKCU 注册表，无需管理员权限） |
| 配置记忆 | 配置保存在 `%AppData%\InputMethodLock\config.ini`，重启后恢复 |
| 状态同步 | 托盘/热键/设置窗口/菜单四处状态实时一致，打开中的设置窗口自动刷新 |

> 完整设计方案（架构、模式实现原理、已知问题评估、路线图）见 [docs/设计方案.md](docs/设计方案.md)。

## 使用

1. 运行 `build.cmd` 生成 `bin\InputMethodLock.exe`（或直接使用已编译好的 exe）。
2. 双击运行，托盘出现绿色"锁"图标，锁定自动生效。
3. 右键托盘图标进行控制；按 `Ctrl+~` 快速临时解锁。

## 配置文件格式（config.ini）

```ini
LockEnabled=true          # 下次启动时是否自动启用（运行时开关不改变此项）
Mode=english              # english=英文锁定；chinese=中文锁定；layout=布局锁定
TargetLayout=00000409     # 布局锁定目标 KLID（00000409=美式英文）
HotkeyModifiers=Control
HotkeyKey=Oemtilde        # None=不启用热键
UnlockLayout=none         # 停用锁定时切到的输入法：hkl:XXXXXXXX（精确 IME）| KLID | none
Exceptions=wechat,code    # 不锁定的进程名，逗号分隔
StartWithWindows=false
```

## 构建

```
build.cmd           → bin\InputMethodLock.exe（约 150KB，零依赖）
build-release.cmd   → release\ 自包含发布文件夹 + dist\InputMethodLock-1.0.0-setup.exe 安装包
```

- `build.cmd` 使用系统自带的 .NET Framework 4.8 编译器（`csc.exe`），无需安装 SDK。
- `build-release.cmd` 需要安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)
  （`winget install -e --id JRSoftware.InnoSetup`），自动检测三种常见安装路径；
  简体中文安装界面语言包随仓库附带（`packaging/ChineseSimplified.isl`）。
- 安装包按**当前用户**安装（免管理员权限），可选桌面快捷方式与开机自启，
  与程序内"开机自动运行"设置共用同一开关；卸载时自动停止运行中的实例。

## 技术说明

- 锁定采用 **100ms 轮询兜底 + 变化监听即时响应**（键盘钩子只观察不吞键 + 系统 IME 事件钩子）；
  轮询对前台窗口调用 `ImmSetConversionStatus`（强制中英状态）或投递
  `WM_INPUTLANGCHANGEREQUEST`（锁定布局）。
- 例外程序通过前台窗口进程名匹配（小写、不含扩展名，带 1 秒缓存）。
- 全局热键使用 `RegisterHotKey`；单实例使用会话内命名 `Mutex`。
- 诊断日志：`%AppData%\InputMethodLock\debug.log`（启动、异常、热键注册结果，超 256KB 自动截断）。

## 已知限制

- **管理员权限窗口**：以管理员运行的目标程序（UIPI 完整性级别更高）可能锁定失效，
  如需覆盖请以管理员身份运行本程序。
- 轮询在极少数全屏独占游戏中可能收不到系统事件，此时依赖兜底轮询。
- 可扩展：按程序自动切换输入法规则表、悬浮状态指示条（见 docs/设计方案.md 路线图）。
