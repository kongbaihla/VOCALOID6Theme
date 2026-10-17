# V6 Theme Hook

在**内存里**给 VOCALOID6 Editor 换配色，**一个字节都不写进安装目录**。

通过 .NET 官方的 `DOTNET_STARTUP_HOOKS` 机制在 VOCALOID6 启动时加载本 DLL，
用 Lib.Harmony 挂钩 WPF 内部的颜色解析器（`MS.Internal.Parsers.ParseColor`
等 4 个方法），把原始颜色在内存中替换成目标色。

> ⚠️ 非 Yamaha 官方工具。仅修改运行中进程的内存，不修改、不重签名
> `VOCALOID6.dll`。任何失败只会导致「主题不生效」，不会让编辑器崩溃。

---

## ⚠️ 运行前必读

### 1. 确认 VOCALOID6 Editor 已安装

默认路径：`C:\Program Files\VOCALOID6\Editor\VOCALOID6.exe`

装在别处的话，设一个环境变量再启动即可：

```bat
set V6_EXE=D:\你的路径\VOCALOID6.exe
```

### 2. 直接用现成的，不用装任何东西

`dist/` 里是编译好的成品，**双击即可**：

```
dist/
├── V6主题版.exe        ← 双击这个
├── V6ThemeHook.dll     ← 注入器本体
└── 0Harmony.dll        ← 运行时依赖
```

把 `dist/` 整个拷到任意位置（**不要放 `Program Files`**，那需要管理员权限），
然后双击 `V6主题版.exe`。

**三个文件必须在同一目录**，缺 `0Harmony.dll` 会抛
`Could not load file or assembly '0Harmony'`，表现是编辑器能开但没有主题效果。

> 启动器是自包含的（内置 .NET 运行时），目标机**不需要装 .NET SDK 或 Runtime**。
> 编辑器本身需要的 .NET 8 由 VOCALOID6 自己提供。

### 3. 首次运行会生成什么

与 exe 同目录自动生成：

| 文件 | 内容 | 初始值 |
|---|---|---|
| `v6.cfg` | 配置 | 默认值 |
| `v6.theme` | 强调色 | `off` |
| `v6.usage` | 使用时长 | **从 0 开始** |
| `v6theme.log` | 运行日志 | 新建 |
| `v6.ptime` | 各工程计时 | 首次计时后 |

**DLL/exe 里不含任何数据**——全部是首次运行时在你机器上新建的，不会带上
别人的记录。删掉 `v6.usage` / `v6.ptime` 即可清零。

### 4. 想关闭主题就用官方图标

**双击官方快捷方式启动的是原版界面**，这是设计而不是 bug：
`DOTNET_STARTUP_HOOKS` 只对通过 `V6主题版.exe` 启动的进程生效。

所以「暂时不要主题」不需要改配置，直接用官方图标启动即可。

### 5. 出问题怎么排查

先设 `V6_THEME_DISABLE=1` 完全禁用注入：

```bat
set V6_THEME_DISABLE=1
```

- 此时编辑器正常 → 问题在注入器，看 `v6theme.log`
- 仍然异常 → 与本工具无关

**最常见的三个坑：**

| 现象 | 原因 |
|---|---|
| 界面没变化 | 是双击官方图标启动的，不是 `V6主题版.exe` |
| 改了颜色没生效 | 颜色在 BAML 解析时染色，**必须重启编辑器**；`smooth` / `smoothplay` 才可实时切换 |
| 双击 exe 没反应 | 检查三个文件是否在同一目录；看有没有 `launcher.log` |

### 6. 已安装 V6PAC 的话

V6PAC 改写 `VOCALOID6.dll` 本体，本工具只改内存，**可以同时用**。
但如果 VOCALOID6 升级过，先确认 V6PAC 的状态与当前 DLL 版本匹配。

---

## 效果

- 界面整体染色（可调饱和度 / 背景亮度 / 自由强调色）
- 平滑滚动与平滑播放跟随
- 菜单栏显示累计使用时长 + 当前工程真实计时
- 点击时长打开「使用记录」窗口：统计卡 + 工程活动热力图 + 目录选择

## 目录

```
ram-theme/
├── dist/                   开箱即用（exe + 两个 DLL）
├── src/                    注入器（C# / WPF / net8.0-windows）
│   ├── Program.cs          启动钩子入口、配置读取
│   ├── ThemeMenu.cs        菜单栏接管 + 时长显示
│   ├── ThemeWindow.cs      主题设置窗口
│   ├── UsageStats.cs       使用计时（真实）
│   ├── ProjectTimer.cs     工程计时（真实，标题栏自动识别）
│   ├── ProjectStats.cs     工程扫描（热力图数据）
│   ├── UsageWindow.cs      使用记录窗口 + 热力图
│   ├── PlaybackSmooth.cs   平滑播放
│   ├── SmoothScroll.cs     平滑滚动
│   └── lib/0Harmony.dll    Lib.Harmony（构建依赖）
├── launcher/               启动器（WinExe，无控制台窗口）
├── wheel-lab/              滚轮行为实验台
├── usage-lab/              计时数学离线断言（19 项）
├── corners-lab/            圆角渲染实验台
├── 开发文档.md              设计取舍、踩坑记录、验证方法
└── 使用说明.md              面向使用者的操作说明
```

## 从源码构建（可选）

普通使用**不需要**这一节，`dist/` 已经可用。只有要改代码时才需要：

1. 装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
   （**不是 Runtime**；9/10 的 SDK 也能编过 `net8.0-windows`）
2. 确认：`dotnet --list-sdks` 有输出（只有 Runtime 时这条**没有任何输出**）
3. 构建注入器：

```bat
dotnet build src\V6ThemeHook.csproj -c Release
```

产物在 `src\bin\Release\net8.0-windows\`，**两个 DLL 都要拷**
（`V6ThemeHook.dll` 和 `0Harmony.dll`）到 `dist\`。

4. 发布启动器（可选，只有改了 `launcher/` 才需要）：

```bat
dotnet publish launcher\Launcher.csproj -c Release -o launcher\pub
copy launcher\pub\V6ThemeLauncher.exe dist\V6主题版.exe
```

## 配置

同目录下 `v6.cfg`，纯文本 `key=value`：

| 键 | 含义 | 默认 |
|---|---|---|
| `sat` | 饱和度倍率 | `0.85` |
| `bg` | 背景亮度倍率 | `1.00` |
| `smooth` | 平滑滚动 0/1 | `1` |
| `smoothplay` | 平滑播放 0/1 | `1` |
| `fps` | 平滑动画帧率上限 | 不限 |
| `projects` | 工程目录路径（热力图数据源） | 未设置 |
| `showproject` | 菜单栏是否显示工程计时 0/1 | `1` |

强调色**不在** `v6.cfg` 里，而是同目录的 `v6.theme` 单文件，
内容是 `#RRGGBB` 或 `off`，由设置窗口自动读写。

**颜色改动需要重启编辑器**（颜色在 BAML 解析时染色）；`smooth` /
`smoothplay` 可实时切换。

`projects` 指向存放 `.vpr` 工程的目录。**不设置就不会显示热力图**——
本仓库不含任何个人路径。也可以在「使用记录」窗口里直接点按钮选择。

## 数据文件

运行时在 exe 同目录生成，均**不含在仓库中**：

| 文件 | 内容 |
|---|---|
| `v6.cfg` | 数值配置 |
| `v6.theme` | 强调色（单行 `#RRGGBB` 或 `off`） |
| `v6.usage` | 使用时长（纯文本，删掉即清零） |
| `v6.ptime` | 各工程计时（纯文本，删掉即清零） |
| `v6theme.log` | 日志（唯一的版本指纹） |

## 热力图说明

窗口里的「工程活动热力图」按天显示工程活跃度：

- **日期是真实的**：优先读文件夹名里的日期（如 `深海少女2026.6.4`），
  取不到才回退到 `.vpr` 创建时间
- **启动次数与时长是估算**：`.vpr` 里没有会话记录，按工程跨度与体积推算

为什么不直接用文件修改时间：实测 48 个 `.vpr` 里有 24 个修改时间是同一天
（批量操作），直接用会画出一根假尖峰。

## 工程计时说明

「当前工程」的数字是**真实秒表**，不是估算：

- 从编辑器**标题栏**读取当前打开的 `.vpr` 文件名（标题形如
  `VOCALOID6 Editor - 曲名`），自动切换到对应工程
- 只在 `V6主题版.exe` 启动的编辑器运行期间累加
- 机器休眠导致的超长间隔不计入
- 一个文件夹里有多个 `.vpr` 时，它们**分别计时**

因为编辑器不锁 `.vpr`、窗口标题属性也不含工程名，这个信息只能从自绘标题栏
的文本节点读出来——所以是「尽力识别」，读不到时就不计时。

## 验证

```bat
dotnet build usage-lab\UsageLab.csproj -c Release
usage-lab\bin\Release\net8.0-windows\UsageLab.exe
```

19 项离线断言，覆盖计时数学（心跳、睡眠、时钟回拨、格式、上界）与
热力图日期计算。**这些是纯函数测试，不需要启动编辑器。**

## 已知限制

- 只对 VOCALOID6 **Editor** 生效
- 颜色改动需重启；平滑项可实时
- 主题只在通过 `V6主题版.exe` 启动时生效
- 热力图的次数/时长是**估算**；「当前工程」才是真实计时
- 工程识别依赖标题栏文本，取不到时不计时
- 与改写 `VOCALOID6.dll` 的补丁工具（如 V6PAC）同时使用可能冲突

## 许可

仅供学习研究。VOCALOID 及相关商标归 Yamaha 所有，本项目与之无关联。
