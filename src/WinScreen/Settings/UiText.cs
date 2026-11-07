using System.Globalization;

namespace WinScreen.Settings;

/// <summary>UI translations, keyed by the English fallback. Program names and hotkeys are never translated.</summary>
public static class UiText
{
    public static string Language { get; private set; } = "en";

    public static void Initialize(string? language, string? systemLanguage = null)
    {
        string culture = string.IsNullOrEmpty(language) || language == "system"
            ? systemLanguage ?? CultureInfo.CurrentUICulture.Name
            : language;
        Language = culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? (culture.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("TW", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("HK", StringComparison.OrdinalIgnoreCase) ||
               culture.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant"
                : "zh-Hans")
            : "en";
    }

    public static string T(string text) => Translations.TryGetValue(text, out var pair)
        ? Language switch { "zh-Hans" => pair[0], "zh-Hant" => pair[1], _ => text }
        : text;

    private static readonly Dictionary<string, string[]> Translations = Catalog
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        // Raw string literals retain checkout line endings. Remove CR without trimming intentional label spaces.
        .Select(line => line.TrimEnd('\r').Split('|')).ToDictionary(parts => parts[0], parts => parts[1..]);

    private const string Catalog = """
                                   Auto|自动|自動
                                   Compatibility (DWM)|兼容模式（DWM）|相容模式（DWM）
                                   Advanced (WGC)|高级模式（WGC）|進階模式（WGC）
                                   Auto (area filtering)|自动（面积滤波）|自動（面積濾波）
                                   Crisp (nearest texel)|清晰（最近纹素）|清晰（最近紋素）
                                   Smooth (linear)|平滑（线性）|平滑（線性）
                                   Match Monitor|匹配显示器|符合顯示器
                                   Include hardware in PiP diagnostics|在画中画诊断中包含硬件信息|在子母畫面診斷中包含硬體資訊
                                   PiP rendering|画中画渲染|子母畫面繪製
                                   Frame limit|帧率上限|影格率上限
                                   Scaling|缩放|縮放
                                   PiP opacity (%)|画中画不透明度（%）|子母畫面不透明度（%）
                                   DWM cadence and scaling are managed by Windows. Frame limits and filters apply to WGC only.|DWM 的帧率与缩放由 Windows 管理。帧率上限与滤镜仅适用于 WGC。|DWM 的影格率與縮放由 Windows 管理。影格率上限與濾鏡僅適用於 WGC。
                                   Advanced capture keeps the Windows capture indicator. If unavailable, PiP uses DWM.|高级捕获保留 Windows 捕获指示器。不可用时，画中画将使用 DWM。|進階擷取會保留 Windows 擷取指示器。無法使用時，子母畫面會使用 DWM。
                                   Right-click a view for diagnostics and Retry advanced backend. Retry is available after a 10-second cooldown.|右键点击画中画可查看诊断或重试高级后端。重试需等待 10 秒。|以滑鼠右鍵點選子母畫面可檢視診斷或重試進階後端。重試需等候 10 秒。
                                   PiP diagnostics|画中画诊断|子母畫面診斷
                                   Copy diagnostics|复制诊断|複製診斷
                                   The clipboard is busy. Try copying diagnostics again.|剪贴板正忙，请重试复制诊断。|剪貼簿忙碌中，請重試複製診斷。
                                   Retry advanced backend|重试高级后端|重試進階後端
                                   Advanced PiP unavailable; using Compatibility (DWM).|高级画中画不可用；正在使用兼容模式（DWM）。|進階子母畫面無法使用；正在使用相容模式（DWM）。
                                   Restore this window|还原此窗口|還原此視窗
                                   Enable experimental effects (Dark, Glass, Crop, Shrink, auto-move)|启用实验性效果（深色、玻璃、裁剪、缩小、自动避让）|啟用實驗性效果（深色、玻璃、裁切、縮小、自動避讓）
                                   Target: |目标： |目標： |
                                   No eligible window|没有可用窗口|沒有可用視窗
                                   Last result: |最近结果： |最近結果： |
                                   Recovery needs attention|需要处理窗口恢复|需要處理視窗復原
                                   Enable experimental effects in Settings to use this action.|请先在设置中启用实验性效果。|請先在設定中啟用實驗性效果。
                                   All changes restored.|已还原所有更改。|已還原所有變更。
                                   Left: show • Right: menu • Middle: close • Drag: move|左键：显示 • 右键：菜单 • 中键：关闭 • 拖动：移动|左鍵：顯示 • 右鍵：選單 • 中鍵：關閉 • 拖曳：移動
                                   Show window|显示窗口|顯示視窗
                                   Minimize window|最小化窗口|最小化視窗
                                   Close window and remove Anchor|关闭窗口并移除锚点|關閉視窗並移除錨點
                                   Remove Anchor only|仅移除锚点|僅移除錨點
                                   Automatic updates need a release repository configured in this build.|此版本需要配置发布仓库才能自动更新。|此版本需要設定發行儲存庫才能自動更新。
                                   Download updates automatically; install when WinScreen exits|自动下载更新；退出 WinScreen 时安装|自動下載更新；結束 WinScreen 時安裝
                                   Update ready; it will install when WinScreen exits.|更新已就绪，将在退出 WinScreen 时安装。|更新已就緒，將在結束 WinScreen 時安裝。
                                   Update check failed. The current version will keep running.|更新检查失败，当前版本将继续运行。|更新檢查失敗，目前版本將繼續執行。
                                   WinScreen update|WinScreen 更新|WinScreen 更新
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
                                   Language (restart WinScreen to apply)|语言（重启 WinScreen 后生效）|語言（重新啟動 WinScreen 後生效）
                                   System default|跟随系统|跟隨系統
                                   Start WinScreen when I sign in|登录时启动 WinScreen|登入時啟動 WinScreen
                                   Show the toolbar when the mouse rests on a title bar|鼠标停在标题栏时显示工具栏|滑鼠停在標題列時顯示工具列
                                   Pause pinning while a pinned window is maximized|置顶窗口最大化时暂停置顶|置頂視窗最大化時暫停置頂
                                   Make see-through windows solid while the mouse is over them|鼠标悬停时使透明窗口不透明|滑鼠停留時使透明視窗不透明
                                   Put a red border around pinned windows|为置顶窗口显示红色边框|為置頂視窗顯示紅色外框
                                   Move Picture-in-Picture views away from text and images|让画中画避开文字和图像|讓子母畫面避開文字和影像
                                   Pin on top|置顶|置頂
                                   More opaque|增加不透明度|增加不透明度
                                   More see-through|增加透明度|增加透明度
                                   Click-through|鼠标穿透|滑鼠穿透
                                   Picture-in-Picture|画中画|子母畫面
                                   Dark mode|深色模式|深色模式
                                   Glass mode|玻璃模式|玻璃模式
                                   Anchor|浮动锚点|浮動錨點
                                   Shrink|缩小为实时预览|縮小為即時預覽
                                   Crop|裁剪|裁切
                                   Restore all windows|还原所有窗口|還原所有視窗
                                   Click a box and press the keys, or Delete to clear|点击输入框后按快捷键；按 Delete 清除|點選輸入框後按快捷鍵；按 Delete 清除
                                   Programs WinScreen leaves alone|WinScreen 忽略的程序|WinScreen 忽略的程式
                                   Only work on these programs (leave empty for all programs)|仅对以下程序生效（留空表示全部）|僅對以下程式生效（留空表示全部）
                                   &Add|添加(&A)|新增(&A)
                                   &Remove|删除(&R)|移除(&R)
                                   &Save|保存(&S)|儲存(&S)
                                   Cancel|取消|取消
                                   Another app already uses: |其他应用已占用： |其他應用程式已占用： |
                                   Settings|设置|設定
                                   Exit|退出|結束
                                   WinScreen is running|WinScreen 正在运行|WinScreen 正在執行
                                   WinScreen couldn't do that|WinScreen 无法执行此操作|WinScreen 無法執行此操作
                                   Setup saved|配置已保存|設定已儲存
                                   Saved setup removed|已删除保存的配置|已移除儲存的設定
                                   Pinned on top|已置顶|已置頂
                                   Unpinned|已取消置顶|已取消置頂
                                   Click-through on|已开启鼠标穿透|已開啟滑鼠穿透
                                   Click-through off|已关闭鼠标穿透|已關閉滑鼠穿透
                                   Some windows couldn't be restored|部分窗口无法还原|部分視窗無法還原
                                   {0} window(s) refused the change.|{0} 个窗口拒绝了更改。|{0} 個視窗拒絕了變更。
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
                                   Set a window up the way you like it and press the Remember setup hotkey. Its program's windows then start that way: pinned, see-through and dark. Click-through, Glass, Crop and Shrink aren't remembered.|调整窗口后，按「记住配置」快捷键。该程序的窗口将自动应用置顶、透明度及深色模式。鼠标穿透、玻璃、裁剪及缩小不会保存。|調整視窗後，按「記住設定」快捷鍵。該程式的視窗將自動套用置頂、透明度及深色模式。滑鼠穿透、玻璃、裁切及縮小不會儲存。
                                   Shrunk - |已缩小 - |已縮小 - |
                                   Show confirmations (problems always appear)|显示操作确认（问题始终会提示）|顯示操作確認（問題一律會提示）
                                   Pause new commands during games and full-screen apps|游戏及全屏应用运行时暂停新命令|遊戲及全螢幕應用程式執行時暫停新命令
                                   Views that move themselves don't appear in screenshots or screen sharing.|自动移动的画中画不会出现在截图和屏幕共享中。|自動移動的子母畫面不會出現在螢幕擷取和螢幕分享中。
                                   Send clicks on a Picture-in-Picture view to the original window|将画中画上的点击发送到原窗口|將子母畫面上的點擊傳送至原視窗
                                   Default opacity:|默认不透明度：|預設不透明度：
                                   Use default opacity|切换默认不透明度|切換預設不透明度
                                   Remember setup|记住配置|記住設定
                                   Startup and updates|启动与更新|啟動與更新
                                   Windows|窗口|視窗
                                   Experimental|实验性功能|實驗性功能
                                   WinScreen version {0}|WinScreen 版本 {0}|WinScreen 版本 {0}
                                   Check for updates|检查更新|檢查更新
                                   About WinScreen|关于 WinScreen|關於 WinScreen
                                   No actions yet|暂无操作|尚無操作
                                   Type a program name or choose a running one|输入程序名称或选择正在运行的程序|輸入程式名稱或選擇執行中的程式
                                   Hold Ctrl, Alt or Win with that key. Function keys also work on their own.|请同时按住 Ctrl、Alt 或 Win。功能键可单独使用。|請同時按住 Ctrl、Alt 或 Win。功能鍵可單獨使用。
                                   Some hotkeys aren't available. The others work.|部分快捷键不可用，其余快捷键正常。|部分快捷鍵無法使用，其餘快捷鍵正常。
                                   Click this icon for Settings, or right-click it for more.|点击此图标打开设置，右键点击查看更多。|點選此圖示開啟設定，以滑鼠右鍵點選可查看更多。
                                   {0}: "{1}" isn't a valid hotkey.|{0}：「{1}」不是有效的快捷键。|{0}：「{1}」不是有效的快捷鍵。
                                   {0} and {1} both use {2}.|{0} 和 {1} 都使用了 {2}。|{0} 和 {1} 都使用了 {2}。
                                   {0}: {1} types "{2}" on your keyboard, so it can't be a hotkey.|{0}：{1} 在你的键盘上会输入「{2}」，不能用作快捷键。|{0}：{1} 在您的鍵盤上會輸入「{2}」，無法作為快捷鍵。
                                   WinScreen couldn't save its settings|WinScreen 无法保存设置|WinScreen 無法儲存設定
                                   Nothing to remember|没有可记住的内容|沒有可記住的內容
                                   Pin this window or change its opacity first.|请先置顶此窗口或更改其不透明度。|請先置頂此視窗或變更其不透明度。
                                   Updates only work in an installed copy of WinScreen.|只有已安装的 WinScreen 才能更新。|只有已安裝的 WinScreen 才能更新。
                                   Turn on automatic updates in Settings first.|请先在设置中开启自动更新。|請先在設定中開啟自動更新。
                                   WinScreen is up to date.|WinScreen 已是最新版本。|WinScreen 已是最新版本。
                                   Already checking for updates.|正在检查更新。|正在檢查更新。
                                   WinScreen didn't exit because some windows couldn't be restored. Choose Restore all windows to try again.|部分窗口无法还原，因此 WinScreen 未退出。请选择“还原所有窗口”重试。|部分視窗無法還原，因此 WinScreen 未結束。請選擇「還原所有視窗」重試。
                                   Commands are paused while a full-screen app is running.|全屏应用运行期间，命令已暂停。|全螢幕應用程式執行期間，命令已暫停。
                                   If you can't reach this window, use Restore all windows from the tray icon.|如果无法操作此窗口，请从托盘图标选择“还原所有窗口”。|如果無法操作此視窗，請從系統匣圖示選擇「還原所有視窗」。
                                   WinScreen hasn't changed this window.|WinScreen 未更改此窗口。|WinScreen 未變更此視窗。
                                   That window is no longer available. Click the window you want, then try again.|该窗口已不可用。请点击目标窗口后重试。|該視窗已無法使用。請點選目標視窗後重試。
                                   No window is selected. Click the window you want, then try again.|未选择窗口。请点击目标窗口后重试。|未選擇視窗。請點選目標視窗後重試。
                                   The desktop, taskbar and other parts of Windows can't be changed.|无法更改桌面、任务栏及其他 Windows 组件。|無法變更桌面、工作列及其他 Windows 元件。
                                   Use this on the original window, not on a WinScreen window.|请对原窗口使用此功能，而不是 WinScreen 窗口。|請對原視窗使用此功能，而不是 WinScreen 視窗。
                                   {0} is on your list of programs WinScreen leaves alone. You can change this in Settings.|{0} 在 WinScreen 忽略的程序列表中。可在设置中更改。|{0} 在 WinScreen 忽略的程式清單中。可在設定中變更。
                                   {0} isn't on your list of programs WinScreen works on. You can change this in Settings.|{0} 不在 WinScreen 生效的程序列表中。可在设置中更改。|{0} 不在 WinScreen 生效的程式清單中。可在設定中變更。
                                   Choose a nonempty crop.|请选择要保留的区域。|請選取要保留的區域。
                                   The source window could not be activated.|无法激活原窗口。|無法啟用原視窗。
                                   WinScreen can't read its recovery file, so new changes are paused. Make sure {0} isn't open in another program, then choose Restore all windows to try again.|WinScreen 无法读取恢复文件，新更改已暂停。请确认 {0} 未被其他程序占用，然后选择“还原所有窗口”重试。|WinScreen 無法讀取復原檔案，新變更已暫停。請確認 {0} 未被其他程式占用，然後選擇「還原所有視窗」重試。
                                   WinScreen's recovery file was damaged, so a copy was kept at {0}. A window changed before WinScreen stopped may stay that way until you close and reopen it.|WinScreen 的恢复文件已损坏，副本保存在 {0}。WinScreen 停止前更改过的窗口可能需要关闭并重新打开才能恢复。|WinScreen 的復原檔案已損毀，副本儲存於 {0}。WinScreen 停止前變更過的視窗可能需要關閉並重新開啟才能復原。
                                   {0} window(s) couldn't be recovered yet. Choose Restore all windows to try again.|{0} 个窗口暂时无法恢复。请选择“还原所有窗口”重试。|{0} 個視窗暫時無法復原。請選擇「還原所有視窗」重試。
                                   WinScreen couldn't save its recovery data, so this change wasn't made: |WinScreen 无法保存恢复数据，因此未进行此更改： |WinScreen 無法儲存復原資料，因此未進行此變更： |
                                   This window draws its own transparency, so WinScreen can't change it.|此窗口自行绘制透明效果，WinScreen 无法更改。|此視窗自行繪製透明效果，WinScreen 無法變更。
                                   refresh the frame of|刷新边框|重新整理外框
                                   The window isn't responding right now. Try again in a moment.|窗口暂时没有响应，请稍后重试。|視窗暫時沒有回應，請稍後重試。
                                   The target window no longer exists.|目标窗口已不存在。|目標視窗已不存在。
                                   The target window cannot be safely identified; it may run as administrator.|无法安全识别目标窗口，它可能以管理员身份运行。|無法安全識別目標視窗，它可能以系統管理員身分執行。
                                   The target window changed while it was being selected.|选择目标窗口时窗口发生了变化。|選取目標視窗時視窗發生了變化。
                                   The target window's process cannot be verified.|无法验证目标窗口的进程。|無法驗證目標視窗的處理程序。
                                   The original target window is no longer available.|原目标窗口已不可用。|原目標視窗已無法使用。
                                   Windows refused the forwarded click.|Windows 拒绝了转发的点击。|Windows 拒絕了轉送的點擊。
                                   Windows could not render the live preview. The original will remain available.|Windows 无法显示实时预览，原窗口仍可使用。|Windows 無法顯示即時預覽，原視窗仍可使用。
                                   The screen could not be sampled for Glass. The window was not changed: |无法为玻璃模式读取屏幕，窗口未更改： |無法為玻璃模式讀取螢幕，視窗未變更： |
                                   Another copy of WinScreen is running from a different folder. Exit it, then try again.|另一个 WinScreen 正从其他文件夹运行。请退出后重试。|另一個 WinScreen 正從其他資料夾執行。請結束後重試。
                                   Windows refused WinScreen's shutdown request.|Windows 拒绝了 WinScreen 的关闭请求。|Windows 拒絕了 WinScreen 的關閉要求。
                                   WinScreen did not confirm shutdown. Restore its windows and exit it before updating or uninstalling.|WinScreen 未确认关闭。请先还原其窗口并退出，再更新或卸载。|WinScreen 未確認關閉。請先還原其視窗並結束，再更新或解除安裝。
                                   The preview is not ready; its original window will not be hidden.|预览尚未就绪，不会隐藏原窗口。|預覽尚未就緒，不會隱藏原視窗。
                                   The PiP click was cancelled because its target could not be verified. Use Go to original window.|无法验证目标，画中画点击已取消。请使用“切换到原窗口”。|無法驗證目標，子母畫面點擊已取消。請使用「切換到原視窗」。
                                   The source has no drawable preview surface.|原窗口没有可显示的预览内容。|原視窗沒有可顯示的預覽內容。
                                   Click: go to the original • Drag: move • Wheel: resize • Ctrl+drag: crop • Right-click: menu|点击：转到原窗口 • 拖动：移动 • 滚轮：调整大小 • Ctrl+拖动：裁剪 • 右键：菜单|點選：前往原視窗 • 拖曳：移動 • 滾輪：調整大小 • Ctrl+拖曳：裁切 • 右鍵：選單
                                   Click: show the window • Drag: move • Right-click: menu|点击：显示窗口 • 拖动：移动 • 右键：菜单|點選：顯示視窗 • 拖曳：移動 • 右鍵：選單
                                   &Keep updating (hide original)|保持更新（隐藏原窗口）(&K)|保持更新（隱藏原視窗）(&K)
                                   Source minimized. Click to keep updating|原窗口已最小化。点击以保持更新|原視窗已最小化。點選以保持更新
                                   No new picture|没有新画面|沒有新畫面
                                   "{0}" was minimized, so it was restored to start its view.|“{0}”已最小化，因此已将其还原以开始预览。|「{0}」已最小化，因此已將其還原以開始預覽。
                                   Still allow opening Picture-in-Picture in full-screen apps|仍允许在全屏应用中打开画中画|仍允許在全螢幕應用程式中開啟子母畫面
                                   Views can't reliably appear over exclusive full-screen apps. Use borderless or windowed mode.|预览无法可靠地显示在独占全屏应用上方。请使用无边框或窗口模式。|預覽無法可靠地顯示在獨占全螢幕應用程式上方。請使用無邊框或視窗模式。
                                   Choose what to show|选择显示内容|選擇顯示內容
                                   Picture-in-Picture views|画中画预览|子母畫面預覽
                                   Close|关闭|關閉
                                   Displays|显示器|顯示器
                                   Open|打开|開啟
                                   Search by title or program|按标题或程序搜索|依標題或程式搜尋
                                   Select a region...|选择区域...|選取區域...
                                   (main)|（主显示器）|（主顯示器）
                                   (minimized, will be restored)|（已最小化，将被还原）|（已最小化，將被還原）
                                   Display {0}|显示器 {0}|顯示器 {0}
                                   Display {0} region|显示器 {0} 区域|顯示器 {0} 區域
                                   That display is no longer available.|该显示器已不可用。|該顯示器已無法使用。
                                   Screen capture stopped, so this view closed.|屏幕捕获已停止，因此此预览已关闭。|螢幕擷取已停止，因此此預覽已關閉。
                                   This version of Windows can't show a view of the screen.|此版本的 Windows 无法显示屏幕预览。|此版本的 Windows 無法顯示螢幕預覽。
                                   Drag: move • Wheel: resize • Ctrl+drag: crop • Right-click: menu|拖动：移动 • 滚轮：调整大小 • Ctrl+拖动：裁剪 • 右键：菜单|拖曳：移動 • 滾輪：調整大小 • Ctrl+拖曳：裁切 • 右鍵：選單
                                   View &options|预览选项(&O)|預覽選項(&O)
                                   Use Settings|使用设置|使用設定
                                   Opacity|不透明度|不透明度
                                   Lock position|锁定位置|鎖定位置
                                   Click-through (use the tray to turn off)|点击穿透（从托盘关闭）|滑鼠穿透（從系統匣關閉）
                                   Size|大小|大小
                                   Small|小|小
                                   Medium|中|中
                                   Large|大|大
                                   Fit to source|适应原始大小|符合原始大小
                                   Presets|预设|預設組合
                                   Save as preset...|保存为预设...|儲存為預設組合...
                                   Remove preset|移除预设|移除預設組合
                                   Save|保存|儲存
                                   Arrange window|排列窗口|排列視窗
                                   Top left|左上|左上
                                   Top center|上方居中|上方置中
                                   Top right|右上|右上
                                   Middle left|左侧居中|左側置中
                                   Center|居中|置中
                                   Middle right|右侧居中|右側置中
                                   Bottom left|左下|左下
                                   Bottom center|下方居中|下方置中
                                   Bottom right|右下|右下
                                   Move to next display|移到下一台显示器|移至下一台顯示器
                                   Exact position and size...|精确位置和大小...|精確位置與大小...
                                   Restore prior position|恢复之前的位置|還原先前的位置
                                   Send to bottom|置于底层|移至最下層
                                   Window information|窗口信息|視窗資訊
                                   There is only one display.|只有一台显示器。|只有一台顯示器。
                                   "{0}" didn't go where it was asked.|“{0}”未移动到指定位置。|「{0}」未移動到指定位置。
                                   Show this window first, then try again.|请先显示此窗口，然后重试。|請先顯示此視窗，然後再試一次。
                                   Unpin this window first, then send it to the bottom.|请先取消置顶此窗口，然后再置于底层。|請先取消置頂此視窗，然後再移至最下層。
                                   WinScreen hasn't moved this window.|WinScreen 未移动过此窗口。|WinScreen 未移動過此視窗。
                                   Left|左|左
                                   Top|上|上
                                   Width|宽度|寬度
                                   Height|高度|高度
                                   Apply|应用|套用
                                   Copy|复制|複製
                                   Open file location|打开文件位置|開啟檔案位置
                                   Unavailable|不可用|無法使用
                                   Title|标题|標題
                                   Program|程序|程式
                                   Program file|程序文件|程式檔案
                                   Process ID|进程 ID|處理程序識別碼
                                   Thread ID|线程 ID|執行緒識別碼
                                   Window class|窗口类|視窗類別
                                   Visible frame|可见边框|可見外框
                                   Display|显示器|顯示器
                                   Runs as administrator|以管理员身份运行|以系統管理員身分執行
                                   Yes|是|是
                                   No|否|否
                                   """;
}
