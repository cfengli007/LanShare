# UI 设计经验（LanShare 自绘体系）

> 来源：姊妹项目 [JavaDeck](https://github.com/cfengli007/JavaDeck) 的 `docs/UI-DESIGN.md`。
> 那套体系是从「深色主题到处白底黑字」一路踩过来的，LanShare 直接沿用结论，不再重走一遍。
> 团队契约见 [UI-CONTRACT.md](UI-CONTRACT.md)，本文件讲的是**为什么**。

---

## 一、最重要的一条：砍掉 WPF-UI

LanShare 1.0 用的是 WPF-UI 4.3.0（`ui:FluentWindow` / `ui:NavigationView` / `ui:SnackbarPresenter`）。
1.1 起完全移除（XAML、C# 引用、csproj 包引用全部清掉），改为自绘。原因是**掌控权**，不是审美：

| 问题 | 具体表现 |
|---|---|
| 版本漂移 | WPF-UI 在 4.x 内部改过 FluentWindow 的标题栏实现，升级小版本就可能让自绘按钮的命中区域失效 |
| 主题色不可控 | 想用一套语义色板（`Panel`/`Line`/`FgDim`）覆盖到 `ComboBox` 弹出层、`ContextMenu`、`ScrollBar`，最终还是要自己写模板 |
| 骨架受限 | 我们要的是「标题栏 38 / 菜单栏 34 / 内容区 / 状态条 / 按钮栏 62」五区，NavigationView 给的是左侧导航 + 头部的固定组合 |
| 打包体积 | 少一个数 MB 级的依赖 |
| 调试成本 | 出问题时要在别人的模板里找原因；自绘时问题永远在自己的 XAML 里 |

**代价**：WPF 没有「全局深色模式」。所有交互控件的默认 `ControlTemplate` 都硬编码了系统颜色，
光设 `Foreground` / `Background` 没用，必须**整套重写模板**。这份账在砍掉 WPF-UI 的那一刻就要认。

**清理顺序有讲究**：先让所有页面脱离 `ui:` 控件、再删最后一处 C# 依赖（本项目的
`Services/PageViewport.cs` 曾是最后一个 `using Wpf.Ui.Controls`），**最后**才删 csproj 里的包引用。
反过来做的话，删包的那一刻全仓编译失败，会误以为是页面写错了。

## 二、必须整套重写模板的控件清单

`src/LanShare.Desktop/App.xaml` 里给了这些控件完整的 `ControlTemplate`（隐式样式，页面直接用即可，
**不要**再在页面里自建模板）：

| # | 控件 | 不重写会怎样 |
|---|---|---|
| 1 | `TextBox` | 白底 + 系统焦点框，深色下像一块补丁 |
| 2 | `PasswordBox` | 同上，另有额外的水印层 |
| 3 | `ComboBox` | 下拉按钮和弹出层都是系统白色 |
| 4 | `ComboBoxItem` | 弹出项高亮是系统蓝 |
| 5 | `CheckBox` | 勾选框是系统控件，且没有主题化的对勾 |
| 6 | `RadioButton` | 同上，圆点用系统色 |
| 7 | `ListBox` | 白底 + 系统选中色（本项目只改了属性，容器走 `ListBoxItem` 模板；见下方说明） |
| 8 | `ListBoxItem` | 悬停/选中都是系统色 |
| 9 | `ContextMenu` | 整个菜单是白色纸片 |
| 10 | `MenuItem` | 子菜单弹层白色；`IsCheckable` 不画就看不到对勾 |
| 11 | `Separator` | 菜单分隔线是系统灰线 |
| 12 | `ToolTip` | 白色气泡 |
| 13 | `ProgressBar` | `PART_Indicator` 要自己画，否则深色下是一条白槽 |
| 14 | `ScrollBar` | 系统滚动条在深色里非常突兀；**还必须按 Orientation 分别处理**（见下方「滚动条的两个坑」） |

> **精确统计**（`App.xaml` 当前状态，共 17 个隐式样式）：
>
> - **12 个带完整 `ControlTemplate`**：`TextBox` `ComboBox` `ComboBoxItem` `CheckBox` `RadioButton`
>   `ListBoxItem` `ContextMenu` `MenuItem` `Separator` `ToolTip` `ProgressBar` `ScrollBar`
> - **`PasswordBox` 走键名样式 `PasswordBoxStyle`**（也带模板），页面里显式引用，不在隐式样式里
> - **5 个只设属性、没有模板**：`TextBlock`（默认 `Fg` 前景色）、`Label`、`ListBox`（只设背景/前景）、
>   `Hyperlink`、`GridSplitter`
>
> 上表 14 行 = 12 个隐式模板 + `PasswordBox` + `ListBox`（`ListBox` 只改属性，真正画行的是 `ListBoxItem` 的模板；
> 容器背景用 `Background` 就能压住，但**行内的悬停/选中态必须靠 `ListBoxItem` 模板**，这是最容易只改一半的地方）。
>
> 教训：**一开始就要把交互控件列全**，边写边补最容易漏掉的是 `ScrollBar` 和 `ToolTip`。

### 滚动条的两个坑

**坑 1：默认模板只设 `Width`，横向滚动条会塌成一条 9 px 的缝。**
`ScrollBar` 是同一个控件类，竖着用时 `Width=9` 是对的，横着用时它需要的是 `Height=9`、
而 `Width` 必须是 `Auto`。只在样式上写死 `Width=9` 会让横向滚动条永远只有 9 px 宽。
本项目的做法是用 `Style.Triggers` 按 `Orientation` 分别下套：

```xml
<Style TargetType="ScrollBar">
  <!-- 竖向：宽 9 -->
  <Setter Property="Width" Value="9"/>
  <Setter Property="MinWidth" Value="9"/>
  <Style.Triggers>
    <Trigger Property="Orientation" Value="Horizontal">
      <!-- 横向：宽 Auto、高 9，并且换一套 IsDirectionReversed=False 的模板 -->
      <Setter Property="Width" Value="Auto"/>
      <Setter Property="MinWidth" Value="0"/>
      <Setter Property="Height" Value="9"/>
      <Setter Property="MinHeight" Value="9"/>
      <Setter Property="Template" Value="{...横向模板...}"/>
    </Trigger>
  </Style.Triggers>
</Style>
```

注意横向模板里 `Track.IsDirectionReversed="False"`（竖向是 `True`），否则拖拽方向是反的。

**坑 2：`Thumb` 必须显式设 `Background`。** `Thumb` 自己有一套默认模板，
不覆盖的话它内部会画出系统默认的浅灰色（深色主题下很刺眼）。本项目给 `Thumb` 同时设
`Background="{DynamicResource ScrollThumb}"` **和**自带的 `ControlTemplate`（一个圆角 `Border`），
两层都要，只设一个都会漏。

## 三、双主题语义色板

两套 ResourceDictionary，键**完全一致**、按**语义**命名：

```
Themes/Light.xaml   34 个键
Themes/Dark.xaml    同样的 34 个键
```

```xml
<!-- 对：语义命名，换主题时名字仍然成立 -->
<SolidColorBrush x:Key="Panel" Color="#FFFFFF"/>
<SolidColorBrush x:Key="FgDim" Color="#5B6273"/>

<!-- 错：按颜色命名，深色主题里 "White" 是 #1B1E25 -->
<SolidColorBrush x:Key="White" Color="#FFFFFF"/>
```

### 核心色值（`Color` 原值，两边一一对应）

| 键 | 用途 | 浅色 | 深色 |
|---|---|---|---|
| `Bg` | 窗口背景 | `#F3F4F7` | `#15171C` |
| `Panel` | 卡片 / 面板 | `#FFFFFF` | `#1B1E25` |
| `CardFill` | 列表项 / 行底 | `#FAFBFC` | `#21252E` |
| `CardFillHover` | 列表项悬停 | `#EEF1F6` | `#2A2F3A` |
| `Line` | 分隔线 / 描边 | `#E2E5EB` | `#2E333E` |
| `LineSoft` | 更淡的分隔 | `#EDEFF3` | `#262B34` |
| `Fg` | 主文字 | `#1C1F26` | `#E8EBF0` |
| `FgDim` | 次要文字 | `#5B6273` | `#9BA3B4` |
| `FgFaint` | 辅助 / 提示 | `#9AA0AE` | `#6A7285` |
| `Accent` | 主色 | `#2F6FE0` | `#5B95F5` |
| `AccentSoft` | 主色浅底 | `#E8F0FE` | `#22334F` |
| `Ok` | 成功 | `#1E8E5A` | `#4FC98A` |
| `Warn` | 警告 | `#B26A00` | `#E8A93D` |
| `Danger` | 错误 / 危险 | `#C62828` | `#E8736B` |
| `Overlay` | 忙碌遮罩 | `#D9FFFFFF` | `#D915171C` |

其余键：`AccentDim DangerHover Input Popover Hint TitleBarBg CaptionHover BtnBg BtnHover BtnPressed
BtnBorder BtnBorderHover ScrollThumb TrackBg CheckBorder MenuHighlight OkSoft WarnSoft DangerSoft`。

### 深色下的对比度陷阱

浅色的 `#2F6FE0` 蓝放到 `#15171C` 上偏暗、发灰。深色必须提亮到 `#5B95F5`，状态色同理：

```
Ok     #1E8E5A → #4FC98A
Warn   #B26A00 → #E8A93D
Danger #C62828 → #E8736B
```

深色主题里**不要**直接复用浅色的强调色。这条是 JavaDeck 那边肉眼对比度调出来的，照抄即可。

## 四、窗口骨架

```
┌────────────────────────────────────────────────────────┐
│ 标题栏 38px    LanShare  内网快传        [─][□][✕]     │  ← 自绘，可拖动
├────────────────────────────────────────────────────────┤
│ 菜单栏 34px    文件 分享 服务 更新 工具      [有新版本]  │  ← Panel 底 + 1px 下边线
├──────────┬─────────────────────────────────────────────┤
│ 图标栏   │                                             │
│ 52px     │            内容区（当前页面）                │
├──────────┴─────────────────────────────────────────────┤
│ 状态条 Auto  就绪                                       │  ← 11px FgDim，左右 14 内边距
├────────────────────────────────────────────────────────┤
│ 按钮栏 62px  [主操作…]                    [次要操作…]    │  ← Panel 底 + 1px 上边线
└────────────────────────────────────────────────────────┘
```

对话框复用同样的结构，只把按钮栏从「主操作」换成「取消 / 确定」，标题栏高度从 38 改成 40。

**为什么不用左侧导航**：初版用 `ui:NavigationView` 的左侧导航，占掉 200 px 宽，而实际只有 4 个入口。
改成 52 px 纯图标栏 + 顶部菜单承载其余操作，内容区多出近 150 px。**固定的次要信息不该占固定宽度**。

## 五、自绘标题栏的两个坑

### 坑 1：`IsHitTestVisibleInChrome`

`WindowChrome.CaptionHeight` 定义的区域由系统当作标题栏处理（`WM_NCHITTEST` 返回 `HTCAPTION`），
里面的按钮收不到鼠标事件。必须逐个标出来：

```xml
<Button Style="{StaticResource CaptionButton}" Click="OnMinimize"
        shell:WindowChrome.IsHitTestVisibleInChrome="True"/>
```

关闭按钮同理，且它还要单独走 `CloseButton` 样式（悬停变红）。

### 坑 2：`DragMove()` 抢走鼠标捕获

这是排查了三层才找到的问题。现象：标题栏按钮**看起来能点**，但 `Click` 永远不触发。
排查结果：

```
WM_NCHITTEST 返回 HTCLIENT(1)     ← 系统层没问题
IsHitTestVisibleInChrome = True   ← 命中测试没问题
AutomationPeer.Invoke() 能触发     ← 逻辑没问题
```

元凶是拖动处理器：

```csharp
// 错：点按钮时事件冒泡到标题栏 Grid，DragMove() 抢走捕获进入模态循环，
//     按钮的 Click 永远等不到 MouseUp
private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
{
    if (e.ButtonState == MouseButtonState.Pressed) DragMove();
}
```

本项目的正确写法（`MainWindow.xaml.cs`）比「只加一行判断」多三件事：

```csharp
private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
{
    // ① 点在按钮上时不拖动 —— 否则 DragMove() 抢走鼠标捕获，按钮永远收不到 Click
    if (IsInButton(e.OriginalSource as DependencyObject)) return;

    // ② 双击 = 最大化/还原（WindowChrome 不会自动帮我们做）
    if (e.ClickCount == 2) { ToggleMaximize(); return; }

    if (e.ButtonState != MouseButtonState.Pressed) return;

    // ③ 最大化状态下拖拽：先还原再跟随，否则窗口会突然跳到屏幕外
    if (WindowState == WindowState.Maximized)
    {
        var pos = e.GetPosition(this);
        var ratio = pos.X / Math.Max(1, ActualWidth);
        WindowState = WindowState.Normal;
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, pos.X + Left - ActualWidth * ratio);
        Top  = Math.Max(workArea.Top, 0);
    }

    // ④ 快速点击时 DragMove 会抛异常（没有可拖动的移动量），必须吞掉
    try { DragMove(); } catch { }
}

/// <summary>判断某个视觉元素是否位于按钮内部（用于区分「拖窗口」和「点按钮」）。</summary>
private static bool IsInButton(DependencyObject? source)
{
    var cur = source;
    for (var i = 0; i < 12 && cur is not null; i++)   // 限深度，防异常树导致死循环
    {
        if (cur is ButtonBase) return true;
        cur = VisualTreeHelper.GetParent(cur);
    }
    return false;
}
```

判断要**向上遍历父链**，只判断 `e.OriginalSource is Button` 不够——事件源常常是按钮模板里的
`TextBlock` 或 `Path`。遍历循环要**带上限**（本项目取 12 层），避免异常的可视化树把 UI 线程卡住。

**这三条都是独立的坑**，少一条就是一个可复现的 bug：

| 少做 | 现象 |
|---|---|
| ① 排除按钮 | 标题栏按钮点不动（本文件的核心案例） |
| ② 双击最大化 | 双击标题栏没反应，与系统窗口行为不一致 |
| ③ 最大化时先还原 | 最大化状态下一拖，窗口直接飞出屏幕 |
| ④ try/catch | 快速单击标题栏偶尔抛 `InvalidOperationException` |

### 配套：DWM 原生窗口效果

`WindowStyle="None"` 之后非客户区（圆角、外框、阴影）仍由 DWM 绘制，必须显式告诉它深浅：

```csharp
DwmSetWindowAttribute(hwnd, 33 /*CORNER_PREFERENCE*/, ref round, 4);   // Win11 圆角
DwmSetWindowAttribute(hwnd, 20 /*IMMERSIVE_DARK_MODE*/, ref dark, 4);  // Win10 1809+ 深色框架
DwmSetWindowAttribute(hwnd, 34 /*BORDER_COLOR*/, ref colorRef, 4);     // 0x00BBGGRR，注意字节序
DwmSetWindowAttribute(hwnd, 35 /*CAPTION_COLOR*/, ref colorRef, 4);
```

封装在 `src/LanShare.Desktop/Services/WindowEffects.cs`。低版本 Windows 会返回错误码，吞掉即可。
还要在 `SourceInitialized` 之后**再应用一次**——`Show()` 时 HWND 才存在。

## 六、主题切换的三种失效方式

全部踩过，全部记录在 `Services/ThemeService.cs` 的注释里：

**① 相对 URI 找不到资源**

```csharp
// 错：运行时被解析成 "themes/dark.xaml"，PackWebRequest 找不到 baml，静默失败
new Uri("Themes/Dark.xaml", UriKind.Relative)
// 对：绝对 pack URI，程序集名必须写对（本项目是 LanShare，不是 LanShare.Desktop）
new Uri("pack://application:,,,/LanShare;component/Themes/Dark.xaml", UriKind.Absolute)
```

**② 替换字典实例不触发重绘**

```csharp
// 错：资源值变了，但已绑定的元素不会重新求值
MergedDictionaries[0] = new ResourceDictionary { Source = darkUri };
// 对：原地改同一实例的 Source
MergedDictionaries[0].Source = darkUri;
```

**③ `Show()` 时部分属性已经求值完毕**

子窗口内部的 `Border`/`Grid` 用的 `DynamicResource` 在 `Show()` 那一刻已求值，之后再换主题不会追。
解决：`ThemeService` 遍历视觉树，把属于「已知主题背景色」的元素重新推成当前主题色（`ReassertBrushes`）。

配套两条纪律：
- 主题色一律 `DynamicResource`，**不用** `StaticResource`
- 用 `EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, ...)` 做全局钩子，
  避免每开一个窗口就要记得改一处

## 七、布局与信息密度

### 1. 列表行三列

```xml
[ 名称（*，TextTrimming=CharacterEllipsis） ][ 大小 ][ 有效期 ][ 下载 ][ 来源 ][ 操作 ]
```

- 名称列用 `*` 撑开并 `TextTrimming="CharacterEllipsis"` —— 长文件名不能把布局撑破
- 数字列（大小 / 下载次数）用 `Mono` 样式（Cascadia Mono / Consolas），便于纵向对齐
- 操作列 `Auto`，按钮用 `SmallButton` / `SmallDangerButton`

### 2. 页面内边距统一 18,14,18,18

`Margins` 到处都是 12/16/20 时，滚动条一出现整页会跳。统一之后不会。

### 3. 卡片用 `Card` 样式

`Card` 是 `Border` 的**样式**（不是颜色键）：`Panel` 底 + 1px `Line` 描边 + 圆角 8 + 内边距 16,14。
页面**不要**自己画背景色。

> 命名上有个容易混的点：样式键叫 `Card` / `FlatCard`（`Border` 样式），
> 而主题色板里的**画刷**叫 `CardFill` / `CardFillHover`（列表行、行内块的底色）。
> 两者不是一回事：`{StaticResource Card}` 取样式，`{DynamicResource CardFill}` 取颜色。

## 八、反馈与状态

### 分层规则

| 场景 | 用什么 | 为什么 |
|---|---|---|
| 页面内错误 / 成功 | 页面顶部内联 `Border`（`BadgeDanger`/`BadgeOk` 配色） | 错误要和上下文待在一起，不能被关掉就找不到 |
| 一次性轻提示（已复制等） | 右下角 Toast，Info/Success 4 秒、Warning/Error 7 秒后淡出 | 不打断操作；错误要留够阅读时间 |
| 耗时 > 300 ms | `BusyOverlay` 遮罩 + 旋转圆环 | 不给转圈用户就以为程序死了 |
| 需要用户确认 / 看详情 | `MsgWindow`（自带「复制全部」） | 见下 |

### 禁止 `MessageBox`

系统 `MessageBox` 不跟主题走（深色下弹出一个白框），也**不能选中复制**堆栈。
自制的 `MsgWindow`：左侧 4 px 色条区分信息 / 警告 / 错误，正文是只读 `TextBox`，
左下角「复制全部」把消息 + 堆栈 + 日志路径一次拷走。

### 忙碌遮罩的两个坑

**① 只盖内容区，不盖标题栏。** 第一版把整个 `Window.Content` 包进 Grid，结果关闭按钮点不动。

**② 绝不设 `owner.IsEnabled = false`。** 那会让整个窗口（含标题栏）失效，用户连关都关不掉。

`Controls/BusyOverlay.cs` 的 API 是 `Show(Panel? host, string message)`，**只接受 `Panel`** 作为宿主
（内容区的那个 Grid），不接受 `Window` —— 就是为了从接口层面堵死上面两个错误。

## 九、其它交互细节

1. **多选列表不要依赖 Ctrl**：`SelectionMode="Multiple"`（点一下就选中、再点取消），
   而不是 `Extended`（必须按住 Ctrl）。
2. **动态创建的 `RadioButton` 必须设 `GroupName`**，否则互斥失效——「圈点亮但其它不消失」就是这个原因。
3. **禁用父子联动要慎重**：JavaDeck 曾经把「检查频率」子菜单跟着「自动检查更新」的开关一起灰掉，
   用户就没法先设频率再开启。只在「操作真的无意义」时才禁用。
   本项目同理：**手动点「检查更新」永远真的发请求**，不受 `UpdateSettings.Enabled` 影响。
4. **状态用颜色 + 文字双编码**：`[运行中]` 绿色底 + 文字、`[已停止]` 红色底 + 文字。
   色盲用户依赖文字，正常用户依赖颜色。禁止只用一个色点表示状态。
5. **不要用字体图标**：图标栏的 4 个图标是 `Path` 矢量（16 px 线性），字体图标在缩放和缺字体时会变砖。

## 十、验证方法

### 编译

解决方案级 `dotnet build` 在本机不可靠（残留 MSBuild 节点会让 `Restore` 阶段报「0 个错误、生成失败」）。
一律单项目编译，三个开关一起上：

```powershell
dotnet build src/LanShare.Desktop/LanShare.Desktop.csproj -m:1 -nodeReuse:false
```

详细原因与 `dotnet publish` 需要的第三个开关见 [DEVELOPMENT.md 第一节](DEVELOPMENT.md#一编译先学会绕开-msbuild-节点残留)。

### 视觉验证不要只信截图

`PrintWindow` API 截窗口**不可靠**——它返回的是缓存的表面（连续三次截图字节完全相同才发现问题）。

### 更要紧的一条：属性值和实际渲染是**两条独立的真相**

同一个概念在 WPF 里有两个来源：**DependencyProperty 的值**，和 **ControlTemplate 实际画出来的结果**。
两者经常不一致，所以**只读属性做主题审计一定会有假阳性**。

实例（本项目实测）：`ScrollBar` 的 `Thumb.Background` 属性值读出来是默认样式的 `#F0F0F0`，
而真正画在屏幕上的是模板里 `{DynamicResource ScrollThumb}`（深色 `#3E4654`）。
只看属性会误判成「深色下有浅色控件」，实际上渲染完全正确。

### 可靠的验证顺序

1. **`RenderTargetBitmap` + 数像素**（最接近真相）

   ```csharp
   var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
   rtb.Render(el);
   // 逐像素判断：亮度 > 0.88 且 alpha 不透明 -> 记为「浅色像素」
   // 深色主题下浅色像素占比过高 = 真的有白底控件
   ```

   判据用「亮度 + 不透明」两个条件，不要只比 RGB —— 半透明遮罩会骗过纯色比较。

2. **全屏截图**：`Graphics.CopyFromScreen`，绕过窗口缓存
3. **读控件属性**：适合验证「绑定的资源键有没有解析到」（`DynamicResource` 拼错时会静默取不到值），
   但**不能**用来判断实际颜色
4. **UIA 遍历**：验证元素存在与状态（`IsEnabled` / `IsOffscreen`）

### Popup 是独立 HWND，截图截不到

`ComboBox` 的下拉层、`ContextMenu`、`ToolTip` **不在主窗口的视觉树里**，它们是独立的顶层 `HWND`。
对窗口做 `RenderTargetBitmap` 或全屏截图都**拍不到它们**，于是「下拉框是白底」这种问题会从审计里漏掉。

正确做法：拿到 `Popup.Child` 单独渲染 ——

```csharp
if (popup.Child is FrameworkElement child)
{
    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    child.Arrange(new Rect(child.DesiredSize));
    var rtb = new RenderTargetBitmap(/* ... */);
    rtb.Render(child);   // 渲染 Popup 内容本身，而不是宿主窗口
}
```

**审计清单里必须有「逐个打开 Popup 单独验」这一项**，否则 14 个重写模板里的 `ComboBox`、
`ContextMenu`、`MenuItem`、`ToolTip` 四个等于没验。

### 主题验证矩阵

每个窗口 × 每种主题都要验一遍：

```
                 Light        Dark
MainWindow       #FFF3F4F7    #FF15171C
MsgWindow        #FFF3F4F7    #FF15171C
AboutWindow      #FFF3F4F7    #FF15171C
AppUpdateWindow  #FFF3F4F7    #FF15171C
ModifyIpDialog   #FFF3F4F7    #FF15171C
页面内容区        （背景来自 Card 样式，不是固定值）
```

截图按 `<页面>-<light|dark>.png` 存到 `docs/screenshots/`。

### 检查清单

- [ ] 没有白底下拉框 / 右键菜单 / 复选框 / 提示气泡（**必须逐个打开 Popup 单独验**）
- [ ] 滚动条在深色下不刺眼，**横向滚动条不是一条缝**（用 `RenderTargetBitmap` 数浅色像素）
- [ ] 窗口圆角 + 边框颜色跟主题一致（Win11）
- [ ] 标题栏按钮可点，窗口可拖动
- [ ] 长文件名不撑破布局
- [ ] 忙碌遮罩出现时标题栏仍可操作
- [ ] 所有提示文字在两种主题下都能看清（对比度）

## 十一、可复用的结论

1. **深色主题 = 全部重写控件模板**，没有捷径
2. **配色键按语义命名**（`Fg`/`Panel`，不是 `White`/`Gray`）
3. **次要信息收进菜单**，不占固定宽度
4. **> 100 ms 的操作都要有转圈**
5. **遮罩只盖内容区**，不动 `Window.Content`，不设 `IsEnabled = false`
6. **自绘标题栏必加 `IsHitTestVisibleInChrome`**，且拖动处理器要向上遍历父链排除按钮，
   并处理双击最大化、最大化后拖拽先还原、`DragMove` 的异常
7. **主题切换原地改 `Source`**，用绝对 pack URI，还要重推已 Show 窗口的已知背景色
8. **错误框要能复制**，禁止 `MessageBox`
9. **状态用颜色 + 文字双编码**
10. **属性值 ≠ 渲染结果**：主题审计要用 `RenderTargetBitmap` 数像素，只读 DependencyProperty 会有假阳性
11. **Popup 是独立 HWND**：下拉框 / 右键菜单 / 气泡必须单独渲染 `Popup.Child`，窗口截图拍不到
12. **`ScrollBar` 要按 `Orientation` 分别设宽高**，`Thumb` 同时设 `Background` 和模板
