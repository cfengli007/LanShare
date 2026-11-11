# 打包工具链（signing/）

本目录存放**编译安装包所需的工具与证书脚本**。其中的 Inno 副本版本为 **6.7.1**
（`ISCC.exe` 不写 PE 版本资源，`VersionInfo` 一律是 0.0.0.0；
版本以运行 `ISCC.exe /?` 打印的 `Compiler engine version` 为准，勿看文件属性）。

二进制文件由 `robocopy` 整份复制而来，
不要手工重建（改过的 `SetupLdr.e32` 无法靠重新安装 Inno 还原）。

## 目录内容

| 路径 | 用途 |
|---|---|
| `InnoSetup6-patched/` | **打过补丁的 Inno Setup 6 副本**，`scripts/publish.ps1` 用的就是这里的 `ISCC.exe` |
| `Patch-InnoSetup-Loader.ps1` | 重新给 `SetupLdr.e32` 打提权补丁（幂等） |
| `toolchain/Sign-*.ps1`、`New-CodeSigningCert.ps1`、`Trust-Root.ps1` | 可选的自签名代码签名工具 |
| `toolchain/*.manifest` | 打补丁过程中的 manifest 变体与校验产物 |

## 为什么必须用 `InnoSetup6-patched`，不能用系统装的 Inno Setup 6

部分国产安全软件（联想电脑管家内置的**火绒引擎**）会拦截「非提权进程静默写 `%TEMP%`」，报 **Error 5**。

Inno Setup 默认的 `SetupLdr.e32` 清单是 `asInvoker`：

```
安装器启动（asInvoker，无 UAC）
   → 把 Setup*.tmp 解压到 %TEMP%        ← 火绒在这里拦截，Error 5
   → 才申请 UAC
```

把 `SetupLdr.e32` 的 manifest 改成 `requireAdministrator` 之后：

```
安装器启动（立即弹 UAC）
   → 用户同意，进程已是管理员
   → 把 Setup*.tmp 解压到 %TEMP%        ← 提权进程写，不触发拦截
```

`signing/InnoSetup6-patched/SetupLdr.e32` 已经打好了补丁，可以直接用。
校验方法：

```powershell
$mt = 'C:\Program Files (x86)\Windows Kits\10\bin\<ver>\x64\mt.exe'
& $mt '-inputresource:signing\InnoSetup6-patched\SetupLdr.e32;#1' '-out:ldr.manifest'
Select-String ldr.manifest 'requireAdministrator'   # 应该命中
```

> `scripts/publish.ps1` 在编译前会检查 `signing/InnoSetup6-patched/ISCC.exe` 是否存在，
> 缺失时直接失败并提示原因 —— 避免有人不小心用了系统那份 Inno。

## 重建补丁副本（换机器 / Inno 升级后）

```powershell
robocopy "C:\Program Files (x86)\Inno Setup 6" signing\InnoSetup6-patched /E
.\signing\Patch-InnoSetup-Loader.ps1 -InnoDir .\signing\InnoSetup6-patched
```

补丁脚本需要 `mt.exe`（Windows SDK 的 Manifest Tool），会自动从 Windows Kits 里找 x64 那份。

## 关于代码签名

`toolchain/` 里的签名脚本是**可选**的，构建流程不强依赖：

```powershell
.\signing\toolchain\New-CodeSigningCert.ps1        # 生成自签名证书（含私钥 pfx）
# 以管理员运行一次，把证书导入受信任根：
#   Start-Process powershell -Verb RunAs -ArgumentList '-File', "$PWD\signing\toolchain\Trust-Root.ps1"
.\signing\toolchain\Sign-Files.ps1 -Path .\dist\LanShare-Setup-1.1.0-win-x64.exe
```

**自签名证书只对本机有效**。要分发给别人，需要购买正式代码签名证书（约 100–400 美元/年）
或使用 SignPath 之类的免费开源方案。

`LanShare-CodeSign.pfx` 含私钥，已在 `.gitignore` 里排除 —— 绝不提交。

## 体积

`InnoSetup6-patched/` 约 32.2 MB（82 个文件，Inno Setup **6.7.1**）。
是否入库需要权衡：它是**二进制、不含源码、也不含私钥**，进库能让别人 clone 后直接打包；
不进库则每人自己 `robocopy` + 打补丁（见上）。当前策略见 `.gitignore` 中的注释。
