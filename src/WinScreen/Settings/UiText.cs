using System.Globalization;

namespace WinScreen.Settings;

/// <summary>UI translations, keyed by the English fallback. Program names and hotkeys are never translated.</summary>
public static class UiText
{
    public static string Language { get; private set; } = "en";

    public static void Initialize(string? language, string? systemLanguage = null)
    {
        string culture = string.IsNullOrEmpty(language) || language == "system"
            ? systemLanguage ?? CultureInfo.CurrentUICulture.Name : language;
        Language = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("HK", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("MO", StringComparison.OrdinalIgnoreCase) ? "zh-Hant" : "zh-Hans")
            : "en";
    }

    public static string T(string text) => Translations.TryGetValue(text, out var pair)
        ? Language switch { "zh-Hans" => pair[0], "zh-Hant" => pair[1], _ => text } : text;

    private static readonly Dictionary<string, string[]> Translations = Catalog.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        // Raw string literals retain checkout line endings. Remove CR without trimming intentional label spaces.
        .Select(line => line.TrimEnd('\r').Split('|')).ToDictionary(parts => parts[0], parts => parts[1..]);

    private const string Catalog = """
Restore this window|还原此窗口|還原此視窗
Enable experimental effects (Dark, Glass, Crop, Shrink, auto-move)|启用实验性效果（深色、玻璃、裁剪、缩小、自动避让）|啟用實驗性效果（深色、玻璃、裁切、縮小、自動避讓）
Forward PiP clicks (experimental; off uses Go to original)|转发画中画点击（实验性；关闭时切换到原窗口）|轉送子母畫面點擊（實驗性；關閉時切換至原視窗）
Target: |目标： |目標： |
No eligible window|没有可用窗口|沒有可用視窗
Last result: |最近结果： |最近結果： |
Recovery needs attention|需要处理窗口恢复|需要處理視窗復原
Commands are paused for the full-screen app.|全屏应用运行期间，普通命令已暂停。|全螢幕應用程式執行期間，一般命令已暫停。
The target is unavailable or excluded. Recovery is still available from the tray.|目标窗口不可用或已排除，仍可从托盘菜单还原。|目標視窗無法使用或已排除，仍可從系統匣選單還原。
Enable experimental effects in Settings to use this action.|请先在设置中启用实验性效果。|請先在設定中啟用實驗性效果。
All changes restored.|已还原所有更改。|已還原所有變更。
Left: show • Right: menu • Middle: close • Drag: move|左键：显示 • 右键：菜单 • 中键：关闭 • 拖动：移动|左鍵：顯示 • 右鍵：選單 • 中鍵：關閉 • 拖曳：移動
Left: show • Right: minimize • Middle: close • Shift+right: menu|左键：显示 • 右键：最小化 • 中键：关闭 • Shift+右键：菜单|左鍵：顯示 • 右鍵：最小化 • 中鍵：關閉 • Shift+右鍵：選單
Show window|显示窗口|顯示視窗
Minimize window|最小化窗口|最小化視窗
Close window and remove Anchor|关闭窗口并移除锚点|關閉視窗並移除錨點
Remove Anchor only|仅移除锚点|僅移除錨點
Automatic updates need a release repository configured in this build.|此版本需要配置发布仓库才能自动更新。|此版本需要設定發行儲存庫才能自動更新。
Download updates automatically; install when WinScreen exits|自动下载更新；退出 WinScreen 时安装|自動下載更新；結束 WinScreen 時安裝
Update ready; it will install when WinScreen exits.|更新已就绪，将在退出 WinScreen 时安装。|更新已就緒，將在結束 WinScreen 時安裝。
Update check failed. The current version will keep running.|更新检查失败，当前版本将继续运行。|更新檢查失敗，目前版本將繼續執行。
WinScreen update|WinScreen 更新|WinScreen 更新
Shrunk - |实时预览 - |即時預覽 - |
This window can't be shrunk.|无法缩小此窗口。|無法縮小此視窗。
WinScreen crop|WinScreen 裁剪|WinScreen 裁切
Drag over the part to keep. Esc cancels.|拖动以选择保留区域。按 Esc 取消。|拖曳以選取保留區域。按 Esc 取消。
WinScreen dark overlay|WinScreen 深色覆盖层|WinScreen 深色覆蓋層
Windows didn't provide the screen filter that dark mode needs.|Windows 未提供深色模式所需的屏幕滤镜。|Windows 未提供深色模式所需的螢幕濾鏡。
read the position of|读取位置|讀取位置
crop|裁剪|裁切
uncrop|取消裁剪|取消裁切
move|移动|移動
put back|还原位置|還原位置
restore the transparency of|还原透明度|還原透明度
change the transparency of|更改透明度|變更透明度
pin|置顶|置頂
unpin|取消置顶|取消置頂
read|读取|讀取
change|更改|變更
Couldn't {0} "{1}": {2}|无法执行「{0}」，窗口「{1}」：{2}|無法執行「{0}」，視窗「{1}」：{2}
"{0}" draws its own shape, so it can't be cropped.|「{0}」自行绘制窗口形状，无法裁剪。|「{0}」自行繪製視窗形狀，無法裁切。
Windows wouldn't bring "{0}" forward to pin it. Click the window and try the pin command again.|Windows 无法将「{0}」移至前台置顶。请点击该窗口后重试置顶。|Windows 無法將「{0}」移至前景置頂。請點選該視窗後重試置頂。
Windows ignored the request to {0} "{1}".|Windows 忽略了「{0}」请求，窗口「{1}」。|Windows 忽略了「{0}」要求，視窗「{1}」。
WinScreen Settings|WinScreen 设置|WinScreen 設定
General|常规|一般
Hotkeys|快捷键|快捷鍵
Programs|程序|程式
Saved setups|已保存的配置|已儲存的設定
Show notifications|显示通知|顯示通知
Language (restart WinScreen to apply)|语言（重启 WinScreen 后生效）|語言（重新啟動 WinScreen 後生效）
System default|跟随系统|跟隨系統
Start WinScreen when I sign in|登录时启动 WinScreen|登入時啟動 WinScreen
Show the toolbar when the mouse rests on a title bar|鼠标停在标题栏时显示工具栏|滑鼠停在標題列時顯示工具列
Turn off during games and full-screen apps|游戏及全屏应用运行时暂停|遊戲及全螢幕應用程式執行時暫停
Pause pinning while a pinned window is maximized|置顶窗口最大化时暂停置顶|置頂視窗最大化時暫停置頂
Make see-through windows solid while the mouse is over them|鼠标悬停时使透明窗口不透明|滑鼠停留時使透明視窗不透明
Put a red border around pinned windows|为置顶窗口显示红色边框|為置頂視窗顯示紅色外框
Move Picture-in-Picture views away from text and images|让画中画避开文字和图像|讓子母畫面避開文字和影像
Views that do this stay out of screenshots and screen sharing.|启用后，画中画不会出现在截图或屏幕共享中。|啟用後，子母畫面不會出現在螢幕擷取或螢幕分享中。
Default see-through level: solid at|默认不透明度：|預設不透明度：
Pin on top|置顶|置頂
More opaque|增加不透明度|增加不透明度
More see-through|增加透明度|增加透明度
Click-through|鼠标穿透|滑鼠穿透
Picture-in-Picture|画中画|子母畫面
Dark mode|深色模式|深色模式
Glass mode|玻璃模式|玻璃模式
Anchor|浮动锚点|浮動錨點
See-through at the default level|切换默认透明度|切換預設透明度
Shrink|缩小为实时预览|縮小為即時預覽
Crop|裁剪|裁切
Remember setup for the program|记住此程序的配置|記住此程式的設定
Remember this setup for the program|记住此程序的配置|記住此程式的設定
Restore all windows|还原所有窗口|還原所有視窗
Click a box and press the keys, or Delete to clear|点击输入框后按快捷键；按 Delete 清除|點選輸入框後按快捷鍵；按 Delete 清除
Program name, for example ExampleProgram.exe|程序名称，例如 ExampleProgram.exe|程式名稱，例如 ExampleProgram.exe
Programs WinScreen leaves alone|WinScreen 忽略的程序|WinScreen 忽略的程式
Only work on these programs (leave empty for all programs)|仅对以下程序生效（留空表示全部）|僅對以下程式生效（留空表示全部）
&Add|添加(&A)|新增(&A)
&Remove|删除(&R)|移除(&R)
&Save|保存(&S)|儲存(&S)
Cancel|取消|取消
Windows that start set up|自动应用的窗口配置|自動套用的視窗設定
Another app already uses: |其他应用已占用： |其他應用程式已占用： |
Pin or unpin last window|切换上个窗口的置顶状态|切換上個視窗的置頂狀態
Picture-in-Picture of last window|打开上个窗口的画中画|開啟上個視窗的子母畫面
Dark mode for last window|切换上个窗口的深色模式|切換上個視窗的深色模式
Anchor for last window|切换上个窗口的浮动锚点|切換上個視窗的浮動錨點
Shrink last window|缩小上个窗口|縮小上個視窗
Crop last window|裁剪上个窗口|裁切上個視窗
Remember last window's setup|记住上个窗口的配置|記住上個視窗的設定
Settings|设置|設定
Exit|退出|結束
WinScreen is running|WinScreen 正在运行|WinScreen 正在執行
Right-click this icon for more.|右键点击此图标以显示菜单。|以滑鼠右鍵點選此圖示以顯示選單。
Some hotkeys are taken by another app: |部分快捷键已被其他应用占用： |部分快捷鍵已被其他應用程式占用： |
WinScreen couldn't do that|WinScreen 无法执行此操作|WinScreen 無法執行此操作
Setup saved|配置已保存|設定已儲存
Saved setup removed|已删除保存的配置|已移除儲存的設定
Pinned on top|已置顶|已置頂
Unpinned|已取消置顶|已取消置頂
Click-through on|已开启鼠标穿透|已開啟滑鼠穿透
Click-through off|已关闭鼠标穿透|已關閉滑鼠穿透
Restore all windows from the tray icon if you can't reach it.|无法操作窗口时，可从托盘菜单还原所有窗口，或按 Ctrl+Alt+R。|無法操作視窗時，可從系統匣選單還原所有視窗，或按 Ctrl+Alt+R。
Some windows couldn't be restored|部分窗口无法还原|部分視窗無法還原
{0} window(s) refused the change.|{0} 个窗口拒绝了更改。|{0} 個視窗拒絕了變更。
&Hide original window|隐藏原窗口(&H)|隱藏原視窗(&H)
&Move away from text and images|避开文字和图像(&M)|避開文字和影像(&M)
&Go to original window|切换到原窗口(&G)|切換到原視窗(&G)
&Remove crop|取消裁剪(&R)|取消裁切(&R)
&Close PiP|关闭画中画(&C)|關閉子母畫面(&C)
This version of Windows can't move the view by itself.|此 Windows 版本不支持自动移动画中画。|此 Windows 版本不支援自動移動子母畫面。
This window can't be shown in Picture-in-Picture.|无法为此窗口显示画中画。|無法為此視窗顯示子母畫面。
WinScreen toolbar|WinScreen 工具栏|WinScreen 工具列
WinScreen anchor - |WinScreen 浮动锚点 - |WinScreen 浮動錨點 - |
PiP - |画中画 - |子母畫面 - |
pinned|置顶|置頂
dark|深色|深色
{0}% solid|不透明度 {0}%|不透明度 {0}%
WinScreen is already running. Look for its icon in the system tray.|WinScreen 已在运行，请在系统托盘中查找其图标。|WinScreen 已在執行，請在系統匣中尋找其圖示。
WinScreen stopped unexpectedly|WinScreen 意外停止|WinScreen 意外停止
WinScreen doesn't change this window.|WinScreen 不会修改此窗口。|WinScreen 不會修改此視窗。
Set a window up the way you like it and press the Remember setup hotkey. Its program's windows then start that way: pinned, see-through and dark. Click-through, Glass, Crop and Shrink aren't remembered.|调整窗口后，按「记住配置」快捷键。该程序的窗口将自动应用置顶、透明度及深色模式。鼠标穿透、玻璃、裁剪及缩小不会保存。|調整視窗後，按「記住設定」快捷鍵。該程式的視窗將自動套用置頂、透明度及深色模式。滑鼠穿透、玻璃、裁切及縮小不會儲存。
""";
}
