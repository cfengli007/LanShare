# UI 契约（LanShare 重构 · 全员必读）

> 本文件在重构期间是**唯一权威**的界面约定。任何页面都不得偏离，否则合并时会返工。
> 设计来源：JavaDeck 的自绘体系（见 `docs/UI-DESIGN.md` 的经验沉淀）。

---

## 一、窗口骨架（MainWindow，固定 5 行）

```
┌────────────────────────────────────────────────────────┐
│ 标题栏 38px    LanShare  内网快传        [─][□][✕]     │  ← 自绘，可拖动
├────────────────────────────────────────────────────────┤
│ 菜单栏 34px    文件 分享 服务 更新 工具      [徽标][徽标] │  ← Panel 底 + 1px 下边线
├──────────┬─────────────────────────────────────────────┤
│ 图标栏   │                                             │
│ 52px     │            内容区（当前页面）                │
│          │                                             │
├──────────┴─────────────────────────────────────────────┤
│ 状态条 Auto  就绪                                       │  ← 11px FgDim，左右 14 内边距
├────────────────────────────────────────────────────────┤
│ 按钮栏 62px  [主操作…]                    [次要操作…]    │  ← Panel 底 + 1px 上边线
└────────────────────────────────────────────────────────┘
```

- 窗口：`WindowStyle="None"` + `WindowChrome CaptionHeight="38" ResizeBorderThickness="6"`，
  `Background="{DynamicResource Bg}"`，字体 `Segoe UI Variable Text, Segoe UI`，字号 13。
- 标题栏按钮必须 `shell:WindowChrome.IsHitTestVisibleInChrome="True"`，
  且拖动处理器要 `if (IsInButton(e.OriginalSource as DependencyObject)) return;`
  ——否则 `DragMove()` 会抢走鼠标捕获，按钮永远收不到 Click。
- 图标栏 52px：4 个 40×40 图标按钮（概览 / 我的分享 / 新建分享 / 设置），
  选中态 = `AccentSoft` 底 + `Accent` 描边图标；悬停 = `CaptionHover`。
  每个按钮必须有 `ToolTip` 文字，并支持 `Ctrl+1..4` 快捷键。

## 二、页面基类

所有内容页继承 `Pages/ShellPage.cs` 里的 `ShellPage : UserControl`（**不是 Page**）。

| 成员 | 用途 |
|---|---|
| `RefreshPage()` | 菜单"刷新"/重新激活时调用 |
| `BuildActions()` | 返回底部按钮栏里的主操作 `StackPanel`；返回 null 表示无 |
| `ActivatePage()` | 每次导航到本页时调用 |
| `SetStatus(text)` | 写状态条 |
| `Toast(title, message, isError, isSuccess)` | 右下角轻提示（替代 Snackbar） |

页面内部结构固定：

```xml
<pages:ShellPage x:Class="LanShare.Desktop.Pages.XxxPage"
                 xmlns:pages="clr-namespace:LanShare.Desktop.Pages" ...>
  <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
    <StackPanel Margin="18,14,18,18">
      <!-- 1. 页头：标题 + 副标题（右上角允许放 1~2 个次要按钮） -->
      <!-- 2. 若干 Style="{StaticResource Card}" 的 Border 分块 -->
    </StackPanel>
  </ScrollViewer>
</pages:ShellPage>
```

> ⚠️ 根元素**必须是 `pages:ShellPage`，不能写 `<UserControl>`**。
> WPF 会给 `x:Class` 生成一个固定基类的 partial（`<UserControl>` → `System.Windows.Controls.UserControl`），
> 与 C# 里的 `: ShellPage` 冲突，编译报 CS0263「分部声明不能指定不同的基类」。
> 用 `<pages:ShellPage>` 生成出来的基类正好是 `ShellPage`。

## 三、可用的样式键（全部在 App.xaml）

**文字**：`PageTitle`(22 SemiBold) `PageSubtitle`(12 FgDim) `SectionTitle`(11 FgDim SemiBold)
`FieldLabel`(12) `HintText`(11 FgFaint) `Mono`(Cascadia Mono)

**按钮**：`FlatButton` `PrimaryButton` `DangerButton` `GhostButton`
`SmallButton` `SmallPrimaryButton` `SmallDangerButton` `BadgeButton` `CaptionButton` `CloseButton`

**容器**：`Card`(白底/1px Line/圆角 8/内边距 16,14) `FlatCard`(无边框) `RowSeparator`

> ⚠️ 注意区分：`Card` 是 **Border 样式**，`CardFill` 是**颜色画刷**。
> `Background="{StaticResource Card}"` 会编译通过但在运行期崩溃（把 Style 当 Brush 用）。
> 背景色一律用 `{DynamicResource CardFill}`。

**徽标**：`Badge` `BadgeAccent` `BadgeOk` `BadgeWarn` `BadgeDanger`
配合 `BadgeText` / `BadgeTextAccent` / `BadgeTextOk` / `BadgeTextWarn` / `BadgeTextDanger`
（状态必须**颜色 + 文字双编码**，不能只用颜色）

**颜色键**（只在必须自定义时直接用）：
`Bg Panel CardFill CardFillHover Line LineSoft Fg FgDim FgFaint Accent AccentSoft
Ok OkSoft Warn WarnSoft Danger DangerSoft Input Popover Overlay CaptionHover
BtnBg BtnHover BtnPressed BtnBorder BtnBorderHover ScrollThumb TrackBg CheckBorder MenuHighlight`

> ⚠️ **禁止**：任何页面写死十六进制颜色、写死 FontSize 以外的排版魔法值、使用 WPF-UI 控件
> （`ui:` 前缀已随依赖移除）、使用 `MessageBox`。

## 四、已全局重写模板的控件（直接用即可）

`TextBlock`(默认 Fg) `TextBox`(含只读态) `PasswordBox`(PasswordBoxStyle)
`ComboBox` `ComboBoxItem` `CheckBox` `RadioButton` `ListBox` `ListBoxItem`
`ContextMenu` `MenuItem` `Separator` `ToolTip` `ProgressBar` `ScrollBar`

这 12 个的默认模板在深色下都是白底，必须整套重写——已完成，页面**不要**再自建模板。

## 五、反馈规范

| 场景 | 用什么 |
|---|---|
| 页面内错误/成功提示 | 页面顶部 `Border`（`BadgeDanger`/`BadgeOk` 配色）+ 文字，或 `SetStatus()` |
| 一次性轻提示（已复制等） | `Toast()` |
| 耗时 > 300ms 的操作 | Shell 的 `BusyOverlay`（`Shell.Busy("正在保存…")`） |
| 需要用户确认/看详情 | `MsgWindow`（自带"复制全部"） |

**禁止** `MessageBox`：它不跟主题走、不能复制堆栈。

## 六、对话框规范

对话框统一 `WindowStyle="None"` + `WindowChrome CaptionHeight="40"`，结构 = 标题栏 40 / 内容 * / 按钮栏 64。
标题栏左侧标题、右侧关闭按钮（`CloseButton` 样式）。圆角/深色框架由 `WindowEffects` 处理。

## 七、验证要求

改完必须自己验证（不要只信编译通过）：
1. `dotnet build` 各项目**单独**编译（解决方案级 build 在本机有 MSBuild 节点残留问题，
   用 `dotnet build <单个 csproj> -m:1 -nodeReuse:false`）。
2. 浅色 + 深色两套主题各跑一次，确认没有白底控件、没有看不清的文字。
3. 截图存到 `D:\project-dsh\work\lanshare-ui\`，文件名格式 `<页面>-<light|dark>.png`。
