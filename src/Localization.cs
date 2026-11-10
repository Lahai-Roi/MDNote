using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace MDNote {
public partial class MainWindow {
    string language="ko";
    /* Weak keys: ApplyLanguage walks the whole window and every translated one-shot dialog, so a plain
       Dictionary kept each closed window/menu (and the Click closures on it) alive for the whole session. */
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string> originalLabels=new System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string>();
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string> originalTips=new System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string>();
    /* Screen-reader names are a second string a control carries in one language; the tooltip pass is
       the only place that re-applies text after a language switch, so it re-applies this one too. */
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string> originalNames=new System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject,string>();
    readonly List<MenuItem> languageItems=new List<MenuItem>();
    static readonly string[] languageCodes={"ko","en","ja","zh-CN","zh-TW"};
    static readonly string[] languageNames={"한국어(Korean)","English","日本語(Japanese)","简体中文(Simplified Chinese)","繁體中文(Traditional Chinese)"};
    static readonly Dictionary<string,string[]> translations=new Dictionary<string,string[]> {
        {"인코딩:",new[]{"Encoding:","エンコード:","编码:","編碼:"}},

        {"하위 섹션",new[]{"Subsection","サブセクション","小节","小節"}},

        {"섹션",new[]{"Section","セクション","节","節"}},

        {"부제목",new[]{"Subheading","小見出し","副标题","副標題"}},

        {"머리글",new[]{"Heading","見出し","标题","標題"}},

        {"부제",new[]{"Subtitle","サブタイトル","副标题","副標題"}},

        {"H6 제목(가장 작은 제목)",new[]{"H6 heading (smallest)","H6 見出し（最小）","H6 标题（最小标题）","H6 標題（最小標題）"}},
        {"H5 제목",new[]{"H5 heading","H5 見出し","H5 标题","H5 標題"}},
        {"H4 제목",new[]{"H4 heading","H4 見出し","H4 标题","H4 標題"}},
        {"H3 제목",new[]{"H3 heading","H3 見出し","H3 标题","H3 標題"}},
        {"H2 제목",new[]{"H2 heading","H2 見出し","H2 标题","H2 標題"}},
        {"H1 제목(큰 제목)",new[]{"H1 heading (largest)","H1 見出し（大）","H1 标题（大标题）","H1 標題（大標題）"}},
        {"줄",new[]{" Ln"," 行"," 行"," 行"}},

        /* b55: the status bar's caret, one pattern per language. The key is the Korean text itself, so Korean
           output is unchanged; the old shared "줄"/"열" keys made English read "8 Ln, 4Columns". */
        {"{0}줄, {1}열",new[]{"Ln {0}, Col {1}","{0} 行、{1} 列","第 {0} 行，第 {1} 列","第 {0} 行，第 {1} 欄"}},

        {"선택한 인코딩으로 저장할 수 없는 문자가 있습니다. UTF-8로 저장해 주세요.",new[]{"Some characters cannot be saved in this encoding. Please use UTF-8.","この文字コードでは保存できない文字があります。UTF-8で保存してください。","所选编码无法保存部分字符。请使用 UTF-8。","所選編碼無法儲存部分字元。請使用 UTF-8。"}},

        {"텍스트 인코딩을 선택하세요.",new[]{"Choose a text encoding.","文字コードを選択してください。","请选择文本编码。","請選擇文字編碼。"}},

        {"저장 인코딩",new[]{"Save encoding","保存エンコーディング","保存编码","儲存編碼"}},

        {"아래 문단 추가",new[]{"Add paragraph below","下に段落を追加","在下方添加段落","在下方新增段落"}},

        {"서식 없이 붙여넣기",new[]{"Paste without formatting","書式なしで貼り付け","粘贴为纯文本","貼上純文字"}},

        {"새 문서는 항상 TXT 모드 활성화",new[]{"Start new documents in TXT mode","新規文書をTXTモードで開く","新文档默认使用 TXT 模式","新文件預設使用 TXT 模式"}},

        {"TXT 모드",new[]{"TXT mode","TXTモード","TXT 模式","TXT 模式"}},

        /* 2026-11-09: the status bar's TXT toggle. Its tooltip names the direction the press will
           take (TXT on/off); the accessibility name stays the short "TXT 모드" above. */
        {"TXT 모드 켜기: .txt 파일 형식으로 전환합니다.",new[]{"Turn on TXT mode: Switches to the .txt file format.","TXTモードをオン: .txt ファイル形式に切り替えます。","开启 TXT 模式：切换为 .txt 文件格式。","開啟 TXT 模式：切換為 .txt 檔案格式。"}},
        {"TXT 모드 끄기: .md 파일 형식으로 전환합니다.",new[]{"Turn off TXT mode: Switches to the .md file format.","TXTモードをオフ: .md ファイル形式に切り替えます。","关闭 TXT 模式：切换为 .md 文件格式。","關閉 TXT 模式：切換為 .md 檔案格式。"}},

        {"항상 수정 기능 활성화",new[]{"Always open for editing","常に編集モードで開く","始终以编辑模式打开","一律以編輯模式開啟"}},

        {"단축키",new[]{"Shortcuts","ショートカット","快捷键","快速鍵"}},

        {"환경설정",new[]{"Preferences","環境設定","偏好设置","偏好設定"}},
        {"F1~F12 단축키…",new[]{"F1–F12 shortcuts…","F1～F12ショートカット…","F1–F12快捷键…","F1–F12快速鍵…"}},
        {"F1~F12 단축키",new[]{"F1–F12 shortcuts","F1～F12ショートカット","F1–F12快捷键","F1–F12快速鍵"}},
        {"F키 기능을 선택하세요. 기존 Ctrl 단축키는 유지됩니다.",new[]{"Choose each function key. Existing Ctrl shortcuts remain available.","各Fキーの機能を選択します。既存のCtrlキー操作も使えます。","选择各F键功能。原有Ctrl快捷键保持不变。","選擇各F鍵功能。原有Ctrl快速鍵維持不變。"}},
        {"기본값 복원",new[]{"Restore defaults","初期設定に戻す","恢复默认值","還原預設值"}},
        {"지정 안 함",new[]{"Unassigned","割り当てなし","未分配","未指定"}},
        {"수정 모드",new[]{"Editing mode","編集モード","编辑模式","編輯模式"}},
        {"글자색 초기화",new[]{"Reset text color","文字色をリセット","重置文字颜色","重設文字色彩"}},
        {"글자 크기 선택",new[]{"Choose text size","文字サイズを選択","选择文字大小","選擇文字大小"}},
        {"마지막 선택 제목",new[]{"Last heading level","前回の見出し","上次标题级别","上次標題層級"}},
        {"일반 문단",new[]{"Normal paragraph","通常段落","普通段落","一般段落"}},
        {"마지막 선택 목록",new[]{"Last list style","前回のリスト","上次列表类型","上次清單樣式"}},
        {"체크리스트 삽입/해제",new[]{"Toggle task list","チェックリスト切替","切换待办清单","切換待辦清單"}},
        {"완료/미완료 전환",new[]{"Toggle task completion","完了状態を切替","切换完成状态","切換完成狀態"}},
        {"이전 탭",new[]{"Previous tab","前のタブ","上一个标签页","上一個分頁"}},
        {"다음 탭",new[]{"Next tab","次のタブ","下一个标签页","下一個分頁"}},
        {"확대율 초기화",new[]{"Reset zoom","ズームをリセット","重置缩放","重設縮放"}},
        {"체크/해제 | 저장 필요",new[]{"Check/uncheck | Save required","チェック切替 | 保存が必要","勾选/取消 | 需要保存","勾選/取消 | 需要儲存"}},

        /* b55: link hover tooltip. The host appends " · <target>"; Translate() splits on " · "
           so only this phrase is looked up and the target is kept verbatim. */
        {"Ctrl+클릭하여 열기",new[]{"Ctrl+click to open","Ctrl+クリックで開く","Ctrl+单击打开","Ctrl+按一下開啟"}},
        {"클릭하여 열기",new[]{"Click to open","クリックで開く","单击打开","按一下開啟"}},

        {"행 추가",new[]{"Add row","行を追加","添加行","新增列"}},
        {"행 제거",new[]{"Remove row","行を削除","删除行","移除列"}},
        {"열 추가",new[]{"Add column","列を追加","添加列","新增欄"}},
        {"열 제거",new[]{"Remove column","列を削除","删除列","移除欄"}},
        {"표 삭제",new[]{"Delete table","表を削除","删除表格","刪除表格"}},
        {"MD파일 작성방법",new[]{"Writing Markdown","MDファイルの書き方","MD文件编写方法","MD檔案撰寫方法"}},
        {"MDNote 사용법",new[]{"Using MDNote","MDNoteの使い方","MDNote使用说明","MDNote使用說明"}},
        {"마지막 선택 제목 적용",new[]{"Apply last heading level","前回の見出しレベルを適用","应用上次标题级别","套用上次標題層級"}},
        {"제목 단계 선택",new[]{"Choose heading level","見出しレベルの選択","选择标题级别","選擇標題層級"}},
        {"체크",new[]{"To-do","チェック","待办","待辦"}},
        {"체크리스트",new[]{"Task list","チェックリスト","待办清单","待辦清單"}},
        {"구분선",new[]{"Horizontal rule","水平線","分隔线","分隔線"}},
        {"코드 블록",new[]{"Code block","コードブロック","代码块","程式碼區塊"}},
        {"아래 문단",new[]{"Paragraph below","下に段落","下方段落","下方段落"}},
        {"본문",new[]{"Body","本文","正文","內文"}},
        {"원문",new[]{"Source","ソース","源码","原始碼"}},
        {"코드",new[]{"Code","コード","代码","程式碼"}},
        /* Live code block copy button: the transient status line answer. */
        {"코드를 복사했습니다",new[]{"Code copied","コードをコピーしました","已复制代码","已複製程式碼"}},
        {"구분",new[]{"Divider","水平線","分隔线","分隔線"}},
        {"체크리스트 삽입",new[]{"Insert task list","チェックリストを挿入","插入待办清单","插入待辦清單"}},
        {"코드 블록 삽입",new[]{"Insert code block","コードブロックを挿入","插入代码块","插入程式碼區塊"}},
        {"구분선 삽입",new[]{"Insert horizontal rule","水平線を挿入","插入水平分隔线","插入水平分隔線"}},
        {"표 삽입",new[]{"Insert table","表を挿入","插入表格","插入表格"}},
        {"표 크기 선택",new[]{"Choose table size","表のサイズを選択","选择表格大小","選擇表格大小"}},
        {"열",new[]{"Columns","列","列","欄"}},
        {"행",new[]{"Rows","行","行","列"}},
        {"삽입",new[]{"Insert","挿入","插入","插入"}},
        {"마지막 선택 목록 적용",new[]{"Apply last list style","前回のリスト形式を適用","应用上次列表样式","套用上次清單樣式"}},
        {"목록",new[]{"List","リスト","列表","清單"}},
        {"목록 종류 선택",new[]{"Choose list style","リスト形式を選択","选择列表样式","選擇清單樣式"}},
        {"글머리 기호 목록",new[]{"Bulleted list","箇条書き","项目符号列表","項目符號清單"}},
        {"번호 목록",new[]{"Numbered list","番号付きリスト","编号列表","編號清單"}},
        {"할 일",new[]{"Task","タスク","待办事项","待辦事項"}},
        {"코드 입력",new[]{"Enter code","コードを入力","输入代码","輸入程式碼"}},
        {"큰 제목",new[]{"Large heading","大見出し","大标题","大標題"}},
        {"가장 작은 제목",new[]{"Smallest heading","最小の見出し","最小标题","最小標題"}},
        {"열은 1~50, 행은 1~200 사이로 입력해 주세요.",new[]{"Enter 1–50 columns and 1–200 rows.","列は1～50、行は1～200で入力してください。","请输入1～50列、1～200行。","請輸入1～50欄、1～200列。"}},
        {"이미지 삽입",new[]{"Insert image","画像を挿入","插入图片","插入圖片"}},
        {"저장하지 않은 변경 내용이 있습니다.",new[]{"This document has unsaved changes.","保存されていない変更があります。","此文档有未保存的更改。","此文件有尚未儲存的變更。"}},
        {"긴 문단",new[]{"Long paragraph","長い段落","长段落","長段落"}},
        {"자 넘는 문단이 있어 줄바꿈을 끄면 입력이 느려집니다. 자동 줄바꿈을 켤까요?",new[]{" characters or more are in one paragraph, so typing gets slow with word wrap off. Turn word wrap on?","文字以上の段落があるため、折り返しを切ると入力が遅くなります。折り返しをオンにしますか？","字以上的段落，关闭自动换行后输入会变慢。要开启自动换行吗？","字以上的段落，關閉自動換行後輸入會變慢。要開啟自動換行嗎？"}},
        {"자 넘는 문단은 줄바꿈 없이 편집할 수 없어 자동 줄바꿈을 켭니다.",new[]{" characters or more cannot be edited without word wrap, so word wrap is turned on.","文字以上の段落は折り返しなしでは編集できないため、折り返しをオンにします。","字以上的段落无法在关闭自动换行时编辑，因此开启自动换行。","字以上的段落無法在關閉自動換行時編輯，因此開啟自動換行。"}},
        {"자동 줄바꿈 켜기",new[]{"Turn word wrap on","折り返しをオンにする","开启自动换行","開啟自動換行"}},
        {"그대로 진행",new[]{"Continue as is","このまま続ける","继续","繼續"}},
        {"이미지 삽입 (Ctrl+Shift+I)",new[]{"Insert image (Ctrl+Shift+I)","画像を挿入 (Ctrl+Shift+I)","插入图片 (Ctrl+Shift+I)","插入圖片 (Ctrl+Shift+I)"}},
        {"변경 내용 저장",new[]{"Save changes","変更内容の保存","保存更改","儲存變更"}},
        {"변경 내용을 저장할까요?",new[]{"Save your changes?","変更内容を保存しますか？","是否保存更改？","是否儲存變更？"}},
        {"버리기를 누르면 마지막 저장 상태로 돌아갑니다.",new[]{"Discard returns to the last saved version.","破棄すると最後に保存した状態に戻ります。","放弃将恢复为上次保存的版本。","捨棄將還原為上次儲存的版本。"}},
        {"에 저장하지 않은 변경 내용이 있습니다.",new[]{" has unsaved changes."," に未保存の変更があります。"," 有未保存的更改。"," 有尚未儲存的變更。"}},
        {"링크 삽입",new[]{"Insert link","リンクを挿入","插入链接","插入連結"}},
        {"링크 주소",new[]{"Link address","リンク先","链接地址","連結位址"}},
        {"링크는 한 문단 안에서 선택해 주세요.",new[]{"Select link text within one paragraph.","同じ段落内の文字を選択してください。","请在同一段落内选择链接文字。","請在同一段落內選取連結文字。"}},
        {"코드나 구조 안에는 링크를 넣을 수 없습니다",new[]{"Links cannot go inside code or structure","コードや構造の中にはリンクを挿入できません","无法在代码或结构中插入链接","無法在程式碼或結構中插入連結"}},
        {"인라인 코드",new[]{"Inline code","インラインコード","行内代码","行內程式碼"}},
        {"글자색",new[]{"Text color","文字色","文字颜色","文字色彩"}},
        {"글자색 선택",new[]{"Choose text color","文字色を選択","选择文字颜色","選擇文字色彩"}},
        {"다른 색…",new[]{"More colors…","その他の色…","更多颜色…","其他色彩…"}},
        {"마지막 글자색 적용",new[]{"Apply last text color","前回の文字色を適用","应用上次的文字颜色","套用上次的文字色彩"}},
        {"색 없음 (기본색)",new[]{"Default color","既定の色","默认颜色","預設色彩"}},
        {"탭 닫기",new[]{"Close tab","タブを閉じる","关闭标签页","關閉分頁"}},
        {"읽기",new[]{"Read","閲覧","阅读","閱讀"}},
        {"TXT 편집",new[]{"Edit TXT","TXT編集","编辑 TXT","編輯 TXT"}},
        {"수정 시작",new[]{"Start editing","編集を開始","开始编辑","開始編輯"}},
        {"수정 종료",new[]{"Finish editing","編集を終了","结束编辑","結束編輯"}},
        {"불러오기",new[]{"Open","開く","打开","開啟"}},
        {"다른 이름으로 저장",new[]{"Save as","名前を付けて保存","另存为","另存新檔"}},
        {"굵게",new[]{"Bold","太字","粗体","粗體"}},
        {"기울임",new[]{"Italic","斜体","斜体","斜體"}},
        {"취소선 켜기 / 끄기",new[]{"Toggle strikethrough","取り消し線の切り替え","切换删除线","切換刪除線"}},
        {"켜짐: 본문 직접 편집 / 꺼짐: MD·HTML 코드 편집",new[]{"On: edit document / Off: edit Markdown source","オン：本文編集／オフ：Markdownソース編集","开启：编辑正文 / 关闭：编辑 Markdown 源码","開啟：編輯內文 / 關閉：編輯 Markdown 原始碼"}},
        {"줄 간격 (%)",new[]{"Line spacing (%)","行間 (%)","行距 (%)","行距 (%)"}},
        {"50~400% 사이의 숫자를 입력해 주세요.",new[]{"Enter a number from 50 to 400%.","50～400%の数値を入力してください。","请输入 50～400% 的数字。","請輸入 50～400% 的數字。"}},
        {"사용자 지정",new[]{"Custom","カスタム","自定义","自訂"}},
        {"사용자 지정 표…",new[]{"Custom table…","カスタム表…","自定义表格…","自訂表格…"}},
        {"파일을 열 수 없습니다.\n",new[]{"Cannot open the file.\n","ファイルを開けません。\n","无法打开文件。\n","無法開啟檔案。\n"}},
        {"저장할 수 없습니다.\n",new[]{"Cannot save the file.\n","ファイルを保存できません。\n","无法保存文件。\n","無法儲存檔案。\n"}},
        {"파일을 찾을 수 없습니다.\n",new[]{"File not found.\n","ファイルが見つかりません。\n","找不到文件。\n","找不到檔案。\n"}},
        {"인쇄할 수 없습니다.\n",new[]{"Cannot print.\n","印刷できません。\n","无法打印。\n","無法列印。\n"}},
        {"개를 바꿨습니다.",new[]{" replacements made."," 件を置換しました。"," 处已替换。"," 處已取代。"}},
        {"개는 구조에 걸려 건너뛰었습니다.",new[]{" could not be replaced because of structure."," 件は構造があるため置換できませんでした。"," 处因结构原因未能替换。"," 處因結構原因未能取代。"}},
        {"이 항목은 구조에 걸려 바꿀 수 없습니다",new[]{"This item holds structure and cannot be replaced","この項目は構造があるため置換できません","此项包含结构，无法替换","此項包含結構，無法取代"}},
        {"Markdown / 텍스트|*.md;*.markdown;*.txt|모든 파일|*.*",new[]{"Markdown / Text|*.md;*.markdown;*.txt|All files|*.*","Markdown / テキスト|*.md;*.markdown;*.txt|すべてのファイル|*.*","Markdown / 文本|*.md;*.markdown;*.txt|所有文件|*.*","Markdown / 文字|*.md;*.markdown;*.txt|所有檔案|*.*"}},
        {"Markdown|*.md|텍스트|*.txt|모든 파일|*.*",new[]{"Markdown|*.md|Text|*.txt|All files|*.*","Markdown|*.md|テキスト|*.txt|すべてのファイル|*.*","Markdown|*.md|文本|*.txt|所有文件|*.*","Markdown|*.md|文字|*.txt|所有檔案|*.*"}},
        {"자",new[]{" characters","文字","字","字"}},
        {"MDNote 문서",new[]{"MDNote document","MDNote 文書","MDNote 文档","MDNote 文件"}},
        {"코드 복사",new[]{"Copy code","コードをコピー","复制代码","複製程式碼"}},
        {"수정 중",new[]{"Editing","編集中","编辑中","編輯中"}},
        {"클릭하여 종료",new[]{"Click to finish","クリックして終了","单击结束","按一下以結束"}},
        {"이미지 삽입…",new[]{"Insert image…","画像を挿入…","插入图片…","插入圖片…"}},
        {"이미지는 본문 수정 모드에서 삽입해 주세요.",new[]{"Insert images in formatted editing mode.","書式表示の編集モードで画像を挿入してください。","请在格式视图的编辑模式中插入图片。","請在格式檢視的編輯模式中插入圖片。"}},
        {"이미지를 열 수 없습니다.",new[]{"Cannot open the image.","画像を開けません。","无法打开图片。","無法開啟圖片。"}},
        {"이미지는 MD 문서에서만 넣을 수 있습니다.",new[]{"Images can only be inserted in Markdown documents.","画像は Markdown 文書にのみ挿入できます。","图片只能插入 Markdown 文档。","圖片只能插入 Markdown 文件。"}},
        {"아웃라인 열기/닫기",new[]{"Open/close outline","目次の開閉","打开/关闭大纲","開啟/關閉大綱"}},
        {"새 문서 만들기 (Ctrl+N)",new[]{"New document (Ctrl+N)","新規文書 (Ctrl+N)","新建文档 (Ctrl+N)","新增文件 (Ctrl+N)"}},
        {"파일 열기 (Ctrl+O)",new[]{"Open file (Ctrl+O)","ファイルを開く (Ctrl+O)","打开文件 (Ctrl+O)","開啟檔案 (Ctrl+O)"}},
        {"저장 (Ctrl+S)",new[]{"Save (Ctrl+S)","保存 (Ctrl+S)","保存 (Ctrl+S)","儲存 (Ctrl+S)"}},
        {"다른 이름으로 저장 (Ctrl+Shift+S)",new[]{"Save as (Ctrl+Shift+S)","名前を付けて保存 (Ctrl+Shift+S)","另存为 (Ctrl+Shift+S)","另存新檔 (Ctrl+Shift+S)"}},
        {"실행 취소 (Ctrl+Z)",new[]{"Undo (Ctrl+Z)","元に戻す (Ctrl+Z)","撤销 (Ctrl+Z)","復原 (Ctrl+Z)"}},
        {"다시 실행 (Ctrl+Y)",new[]{"Redo (Ctrl+Y)","やり直す (Ctrl+Y)","重做 (Ctrl+Y)","重做 (Ctrl+Y)"}},
        {"굵게 켜기/끄기 (Ctrl+B)",new[]{"Toggle bold (Ctrl+B)","太字の切り替え (Ctrl+B)","切换粗体 (Ctrl+B)","切換粗體 (Ctrl+B)"}},
        {"기울임 켜기/끄기 (Ctrl+I)",new[]{"Toggle italic (Ctrl+I)","斜体の切り替え (Ctrl+I)","切换斜体 (Ctrl+I)","切換斜體 (Ctrl+I)"}},
        {"취소선 켜기/끄기",new[]{"Toggle strikethrough","取り消し線の切り替え","切换删除线","切換刪除線"}},
        {"찾기 열기/닫기 (Ctrl+F)",new[]{"Open/close find (Ctrl+F)","検索を開く/閉じる (Ctrl+F)","打开/关闭查找 (Ctrl+F)","開啟/關閉尋找 (Ctrl+F)"}},
        {"바꾸기 열기/닫기 (Ctrl+H)",new[]{"Open/close replace (Ctrl+H)","置換を開く/閉じる (Ctrl+H)","打开/关闭替换 (Ctrl+H)","開啟/關閉取代 (Ctrl+H)"}},
        {"본문 미리보기 켜기/끄기",new[]{"Toggle document preview","本文プレビューの切り替え","切换正文预览","切換內文預覽"}},
        {"인라인 코드 켜기/끄기",new[]{"Toggle inline code","インラインコードの切り替え","切换行内代码","切換行內程式碼"}},
        {"링크(URL) 삽입",new[]{"Insert link (URL)","リンク(URL)を挿入","插入链接(URL)","插入連結(URL)"}},
        {"글머리 기호 목록 켜기/끄기",new[]{"Toggle bullet list","箇条書きの切り替え","切换项目符号列表","切換項目符號清單"}},
        {"인용문 켜기/끄기",new[]{"Toggle quote","引用の切り替え","切换引用","切換引用"}},
        {"선택한 글자 크기 설정",new[]{"Set selected text size","選択した文字のサイズを設定","设置所选文字大小","設定所選文字大小"}},
        {"다크 모드 켜기/끄기",new[]{"Toggle dark mode","ダークモードの切り替え","切换深色模式","切換深色模式"}},
        {"수정 모드 켜기: 본문을 편집할 수 있습니다. (Ctrl+E)",new[]{"Turn on editing mode: Lets you edit the text. (Ctrl+E)","編集モードをオン: 本文を編集できます。(Ctrl+E)","开启编辑模式：可以编辑正文。(Ctrl+E)","開啟編輯模式：可以編輯內文。(Ctrl+E)"}},
        {"수정 모드 끄기: 읽기 모드로 전환합니다. (Ctrl+E)",new[]{"Turn off editing mode: Switches to read mode. (Ctrl+E)","編集モードをオフ: 閲覧モードに切り替えます。(Ctrl+E)","关闭编辑模式：切换到阅读模式。(Ctrl+E)","關閉編輯模式：切換到閱讀模式。(Ctrl+E)"}},
        /* b56: the narrow toolbar row's overflow button ("»") */
        {"도구 더 보기",new[]{"More tools","その他のツール","更多工具","更多工具"}},
        {"탭 닫기 (Ctrl+W)",new[]{"Close tab (Ctrl+W)","タブを閉じる (Ctrl+W)","关闭标签页 (Ctrl+W)","關閉分頁 (Ctrl+W)"}},
        {"글자 크기는 6~120 사이의 숫자로 입력해 주세요.",new[]{"Enter a text size from 6 to 120.","文字サイズは6～120の数値で入力してください。","请输入 6～120 之间的文字大小。","請輸入 6～120 之間的文字大小。"}},
        {"적용",new[]{"Apply","適用","应用","套用"}},
        {"이전 검색 결과 (Shift+F3)",new[]{"Previous match (Shift+F3)","前の検索結果 (Shift+F3)","上一个匹配项 (Shift+F3)","上一個符合項目 (Shift+F3)"}},
        {"다음 검색 결과 (F3)",new[]{"Next match (F3)","次の検索結果 (F3)","下一个匹配项 (F3)","下一個符合項目 (F3)"}},
        /* b58: the find bar's up/down buttons use these shorter captions for both the tooltip and the
           screen-reader name; only the "(Shift+F3)"/"(F3)" variants existed. */
        {"이전 검색 결과",new[]{"Previous match","前の検索結果","上一个匹配项","上一個符合項目"}},
        {"다음 검색 결과",new[]{"Next match","次の検索結果","下一个匹配项","下一個符合項目"}},
        {"현재 항목 바꾸기",new[]{"Replace current match","現在の検索結果を置換","替换当前匹配项","取代目前符合項目"}},
        {"모든 일치 항목 바꾸기",new[]{"Replace all matches","すべての検索結果を置換","替换所有匹配项","取代所有符合項目"}},
        {"현재 항목 바꾸기 (Enter, Alt+R)",new[]{"Replace the current match (Enter, Alt+R)","現在の検索結果を置換 (Enter、Alt+R)","替换当前匹配项 (Enter、Alt+R)","取代目前符合項目 (Enter、Alt+R)"}},
        {"모든 일치 항목 바꾸기 (Alt+A)",new[]{"Replace all matches (Alt+A)","すべての検索結果を置換 (Alt+A)","替换所有匹配项 (Alt+A)","取代所有符合項目 (Alt+A)"}},
        {"찾기/바꾸기 닫기 (Esc)",new[]{"Close find/replace (Esc)","検索/置換を閉じる (Esc)","关闭查找/替换 (Esc)","關閉尋找/取代 (Esc)"}},
        {"라이선스 보기",new[]{"View license","ライセンスを表示","查看许可证","檢視授權條款"}},
        {"가벼운 Markdown / TXT 편집기",new[]{"A lightweight Markdown / TXT editor","軽量な Markdown / TXT エディター","轻量的 Markdown / TXT 编辑器","輕巧的 Markdown / TXT 編輯器"}},
        {"파일(_F)",new[]{"File(_F)","ファイル(_F)","文件(_F)","檔案(_F)"}},
        {"편집(_E)",new[]{"Edit(_E)","編集(_E)","编辑(_E)","編輯(_E)"}},
        {"보기(_V)",new[]{"View(_V)","表示(_V)","视图(_V)","檢視(_V)"}},
        {"도움말(_H)",new[]{"Help(_H)","ヘルプ(_H)","帮助(_H)","說明(_H)"}},
        {"최근 파일",new[]{"Recent files","最近使ったファイル","最近的文件","最近的檔案"}},
        /* 2026-11-07: the two commands the recent-files submenu now carries below its entries (a forum
           reader asked how to clear the history). */
        {"최근 파일 기록 삭제",new[]{"Clear recent files","最近使ったファイルの履歴を消去","清除最近的文件记录","清除最近的檔案記錄"}},
        {"최근 파일 사용 안 함",new[]{"Disable recent files","最近使ったファイルを無効にする","禁用最近的文件","停用最近的檔案"}},
        {"언어",new[]{"Language","言語","语言","語言"}},
        {"새로 만들기",new[]{"New","新規作成","新建","新增"}},
        {"열기…",new[]{"Open…","開く…","打开…","開啟…"}},
        /* b56: the two File-menu entries that were built in Korean only (OpenEncoding.cs, Recovery.cs); the
           strings come from the shared menu-translation list, zh-TW/zh-CN keep their own wording. */
        {"인코딩을 지정하여 열기…",new[]{"Open with Encoding…","エンコードを指定して開く…","以指定编码打开…","以指定編碼開啟…"}},
        {"복구할 문서…",new[]{"Recover Unsaved Documents…","保存されていない文書の回復…","恢复未保存的文档…","復原未儲存的文件…"}},
        {"저장",new[]{"Save","保存","保存","儲存"}},
        {"다른 이름으로 저장…",new[]{"Save as…","名前を付けて保存…","另存为…","另存新檔…"}},
        {"인쇄…",new[]{"Print…","印刷…","打印…","列印…"}},
        /* 이전 세션 계속하기 (user, 2026-11-03): the File-menu option that closes without the save
           prompt and reopens the unsaved text on the next start, named after Windows 11 Notepad's
           own item. */
        {"이전 세션 계속하기",new[]{"Continue previous session","前のセッションを続行","继续上一个会话","繼續先前的工作階段"}},
        {"끝내기",new[]{"Exit","終了","退出","結束"}},
        {"수정 시작/종료",new[]{"Toggle editing","編集の開始 / 終了","开始 / 结束编辑","開始 / 結束編輯"}},
        {"실행 취소",new[]{"Undo","元に戻す","撤销","復原"}},
        {"실행 취소 기록이 문서와 맞지 않아 초기화했습니다",new[]{"Undo history did not match the document and was reset.","元に戻す履歴が文書と一致しないため、初期化しました。","撤销记录与文档不匹配，已重置。","復原記錄與文件不符，已重設。"}},
        {"다시 실행",new[]{"Redo","やり直す","重做","重做"}},
        {"잘라내기",new[]{"Cut","切り取り","剪切","剪下"}},
        {"복사",new[]{"Copy","コピー","复制","複製"}},
        {"붙여넣기",new[]{"Paste","貼り付け","粘贴","貼上"}},
        {"모두 선택",new[]{"Select all","すべて選択","全选","全選"}},
        {"찾기…",new[]{"Find…","検索…","查找…","尋找…"}},
        {"바꾸기…",new[]{"Replace…","置換…","替换…","取代…"}},
        {"새 탭으로 보기",new[]{"Open files in tabs","新しいタブで開く","在新标签页中打开","在新分頁中開啟"}},
        {"자동 줄바꿈",new[]{"Word wrap","右端で折り返す","自动换行","自動換行"}},
        {"글자 확대",new[]{"Increase text size","文字を拡大","放大文字","放大文字"}},
        {"글자 축소",new[]{"Decrease text size","文字を縮小","缩小文字","縮小文字"}},
        {"기본 글자 크기",new[]{"Reset text size","文字サイズをリセット","重置文字大小","重設文字大小"}},
        {"줄 간격",new[]{"Line spacing","行間","行距","行距"}},
        {"글꼴 및 크기…",new[]{"Font and size…","フォントとサイズ…","字体和大小…","字型與大小…"}},
        {"기본 글자색…",new[]{"Default text color…","既定の文字色…","默认文字颜色…","預設文字色彩…"}},
        {"기본 글자색 초기화",new[]{"Reset default text color","既定の文字色をリセット","重置默认文字颜色","重設預設文字色彩"}},
        {"다크 모드",new[]{"Dark mode","ダークモード","深色模式","深色模式"}},
        /* 2.4.0 memory study (user 2026-11-09): the View-menu switch back to hardware drawing, off by default. */
        {"GPU 가속 사용",new[]{"Use GPU acceleration","GPU アクセラレーションを使う","使用 GPU 加速","使用 GPU 加速"}},
        {"끄면 메모리를 덜 씁니다. 화면이 끊기면 켜세요.",new[]{"Off uses less memory. Turn on if scrolling stutters.","オフにするとメモリ使用量が減ります。表示がカクつく場合はオンにしてください。","关闭可减少内存占用。若滚动卡顿请开启。","關閉可減少記憶體使用量。若捲動卡頓請開啟。"}},
        {"MDNote 정보",new[]{"About MDNote","MDNoteについて","关于 MDNote","關於 MDNote"}},
        /* The help menu's manual update check (UpdateCheck.cs): its menu item, title, three outcomes and the one new button. */
        {"업데이트 확인",new[]{"Check for updates","更新の確認","检查更新","檢查更新"}},
        {"새 버전이 있습니다: {0}\n현재 버전: {1}",new[]{"A new version, {0}, is available.\nCurrent version: {1}","新しいバージョン {0} があります。\n現在のバージョン: {1}","有新版本 {0}。\n当前版本：{1}","有新版本 {0}。\n目前版本：{1}"}},
        {"최신 버전입니다. ({0})",new[]{"You are up to date. ({0})","最新バージョンです。({0})","已是最新版本。({0})","已是最新版本。({0})"}},
        {"업데이트를 확인하지 못했습니다.\n인터넷 연결을 확인한 뒤 다시 시도해 주세요.",new[]{"Could not check for updates.\nCheck your internet connection and try again.","更新を確認できませんでした。\nインターネット接続を確認してから、もう一度お試しください。","无法检查更新。\n请检查网络连接后重试。","無法檢查更新。\n請檢查網路連線後再試一次。"}},
        {"릴리스 페이지 열기",new[]{"Open release page","リリースページを開く","打开发布页面","開啟發行頁面"}},
        /* 2.2.0 automatic check: the Help item that switches it, and the new-version dialog's check box. */
        {"업데이트 자동 확인",new[]{"Check for updates automatically","更新を自動的に確認","自动检查更新","自動檢查更新"}},
        {"새 버전({0})이 나왔습니다.",new[]{"Version {0} is available.","バージョン {0} が利用可能です。","新版本 {0} 已发布。","新版本 {0} 已發布。"}},
        {"자세히",new[]{"Details","詳細","详情","詳細資訊"}},
        {"이 버전은 다시 알리지 않음",new[]{"Skip this version","このバージョンをスキップ","不再提醒此版本","不再提醒此版本"}},
        {"사용법",new[]{"Getting started","使い方","使用方法","使用方法"}},
        {"제목",new[]{"Heading","見出し","标题","標題"}},
        {"• 목록",new[]{"• List","• リスト","• 列表","• 清單"}},
        {"❯ 인용",new[]{"❯ Quote","❯ 引用","❯ 引用","❯ 引用"}},
        {"링크",new[]{"Link","リンク","链接","連結"}},
        {"원문 편집",new[]{"Source editing","ソース編集","源码编辑","原始碼編輯"}},
        {"원문 편집 켜기/끄기",new[]{"Toggle source editing","ソース編集の切り替え","切换源码编辑","切換原始碼編輯"}},
        {"원문 보기 켜기: 서식 기호(#, ** 등)를 표시합니다.",new[]{"Show source: Shows formatting marks (#, **, etc.).","ソース表示をオン: 書式記号（#、** など）を表示します。","显示源码：显示格式符号（#、** 等）。","顯示原始碼：顯示格式符號（#、** 等）。"}},
        {"원문 보기 끄기: 서식 기호를 숨깁니다.",new[]{"Hide source: Hides formatting marks.","ソース表示をオフ: 書式記号を非表示にします。","隐藏源码：隐藏格式符号。","隱藏原始碼：隱藏格式符號。"}},
        {"서식 보기",new[]{"Formatted view","書式表示","格式视图","格式檢視"}},
        {"서식 보기 / 원문 편집 전환",new[]{"Switch formatted view / source editing","書式表示とソース編集の切り替え","切换格式视图与源码编辑","切換格式檢視與原始碼編輯"}},
        {"미리보기",new[]{"Preview","プレビュー","预览","預覽"}},
        {"Markdown 라이브 미리보기",new[]{"Markdown Live Preview","Markdown ライブプレビュー","Markdown 实时预览","Markdown 即時預覽"}},
        {"커서가 있는 줄만 원문으로 보여 줍니다.",new[]{"Shows only the caret line as source text.","カーソルのある行だけを原文で表示します。","仅将光标所在行显示为源文本。","僅將游標所在行顯示為原始文字。"}},
        {"크기",new[]{"Size","サイズ","字号","大小"}},
        {"찾기",new[]{"Find","検索","查找","尋找"}},
        {"바꿀 내용",new[]{"Replace with","置換後","替换为","取代為"}},
        {"이전",new[]{"Previous","前へ","上一个","上一個"}},
        {"다음",new[]{"Next","次へ","下一个","下一個"}},
        {"바꾸기",new[]{"Replace","置換","替换","取代"}},
        {"모두 바꾸기",new[]{"Replace all","すべて置換","全部替换","全部取代"}},
        {"닫기",new[]{"Close","閉じる","关闭","關閉"}},
        {"대소문자 구분",new[]{"Match case","大文字と小文字を区別","区分大小写","區分大小寫"}},
        {"아웃라인",new[]{"Outline","目次","大纲","大綱"}},
        {"목차",new[]{"Outline","目次","大纲","大綱"}},
        {"목차 열기: 제목 목록을 표시합니다.",new[]{"Open outline: Shows the list of headings.","目次を開く: 見出しの一覧を表示します。","打开大纲：显示标题列表。","開啟大綱：顯示標題清單。"}},
        {"목차 닫기: 제목 목록을 숨깁니다.",new[]{"Close outline: Hides the list of headings.","目次を閉じる: 見出しの一覧を非表示にします。","关闭大纲：隐藏标题列表。","關閉大綱：隱藏標題清單。"}},
        {"확인",new[]{"OK","OK","确定","確定"}},
        {"취소",new[]{"Cancel","キャンセル","取消","取消"}},
{"HTML로 내보내기…",new[]{"Export as HTML…","HTMLとしてエクスポート…","导出为 HTML…","匯出為 HTML…"}},
{"집중 모드",new[]{"Focus mode","フォーカスモード","专注模式","專注模式"}},
{"제목 단계 표시",new[]{"Heading level marks","見出しレベルの表示","显示标题级别","顯示標題層級"}},
{"슬래시 막대",new[]{"Slash bars","スラッシュバー","斜线条","斜線條"}},
{"# 기호",new[]{"# symbols","# 記号","# 符号","# 符號"}},
{"타자기 모드",new[]{"Typewriter mode","タイプライターモード","打字机模式","打字機模式"}},
{"집중 모드: 끄기",new[]{"Off","オフ","关闭","關閉"}},
{"집중 모드: 문단",new[]{"Paragraph","段落","段落","段落"}},
{"집중 모드: 줄",new[]{"Line","行","行","行"}},
{"HTML 파일 (*.html)",new[]{"HTML files (*.html)","HTML ファイル (*.html)","HTML 文件 (*.html)","HTML 檔案 (*.html)"}},
{"HTML 파일을 저장하지 못했습니다.\n",new[]{"Failed to save the HTML file.\n","HTML ファイルを保存できませんでした。\n","无法保存 HTML 文件。\n","無法儲存 HTML 檔案。\n"}},
{"다른 프로그램에서 이 파일이 바뀌었습니다.\n{0}\n\n다시 불러올까요?",new[]{"This file has been modified by another program.\n{0}\n\nDo you want to reload it?","このファイルは別のプログラムによって変更されました。\n{0}\n\n再読み込みしますか？","此文件已被其他程序修改。\n{0}\n\n要重新加载吗？","此檔案已被其他程式修改。\n{0}\n\n要重新載入嗎？"}},
{"다시 불러오기",new[]{"Reload","再読み込み","重新加载","重新載入"}},
{"그대로 두기",new[]{"Keep current","そのままにする","保持不变","維持現狀"}},
{"다른 프로그램에서 이 파일이 바뀌었습니다.\n{0}\n\nMDNote에서 수정한 내용은 아직 저장하지 않았습니다. 다시 불러오면 수정한 내용이 사라집니다.",new[]{"This file has been modified by another program.\n{0}\n\nYou have unsaved changes in MDNote. Reloading will discard your changes.","このファイルは別のプログラムによって変更されました。\n{0}\n\nMDNoteでの変更はまだ保存されていません。再読み込みすると変更内容が失われます。","此文件已被其他程序修改。\n{0}\n\n您在 MDNote 中的更改尚未保存。重新加载将丢失这些更改。","此檔案已被其他程式修改。\n{0}\n\n您在 MDNote 中的變更尚未儲存。重新載入將會遺失這些變更。"}},
{"내 변경 내용 유지",new[]{"Keep my changes","変更を保持","保留我的更改","保留我的變更"}},
{"저장하려는 파일이 다른 프로그램에서 바뀌었습니다.\n{0}\n\n덮어쓸까요?",new[]{"The file you are trying to save has been modified by another program.\n{0}\n\nDo you want to overwrite it?","保存しようとしているファイルは別のプログラムによって変更されました。\n{0}\n\n上書きしますか？","要保存的文件已被其他程序修改。\n{0}\n\n要覆盖吗？","要儲存的檔案已被其他程式修改。\n{0}\n\n要覆寫嗎？"}},
{"덮어쓰기",new[]{"Overwrite","上書き","覆盖","覆寫"}},
{"이 파일이 없어졌습니다(지워졌거나 옮겨졌습니다).\n{0}\n\n지금 내용은 그대로 남아 있고, 저장하면 파일을 다시 만듭니다.",new[]{"This file is missing (deleted or moved).\n{0}\n\nThe current content is kept in the editor, and saving will recreate the file.","このファイルが見つかりません（削除または移動されました）。\n{0}\n\n現在の内容はそのまま残っており、保存するとファイルが再作成されます。","此文件已不存在（已被删除或移动）。\n{0}\n\n当前内容仍保留在编辑器中，保存时将重新创建该文件。","此檔案已不存在（已被刪除或移動）。\n{0}\n\n目前內容仍保留在編輯器中，儲存時將重新建立該檔案。"}},
{"정규식",new[]{"Regular expression","正規表現","正则表达式","正規表示式"}},
{"정규식 사용",new[]{"Use regular expression","正規表現を使用する","使用正则表达式","使用正規表示式"}},
{"정규식이 올바르지 않습니다",new[]{"Invalid regular expression","正規表現が正しくありません","无效的正则表达式","無效的正規表示式"}},
        {"버리기",new[]{"Discard","破棄","放弃","捨棄"}},
        {"새 문서",new[]{"Untitled","無題","未命名","未命名"}},
        {"수정중",new[]{"Editing","編集中","编辑中","編輯中"}},
        {"수정",new[]{"Edit","編集","编辑","編輯"}},

        /* b57: strings the localization audit found hardcoded (bughunt-ui item 8 / missing-keys). The
           Korean text is the key, so Korean output is unchanged; "인용" is also inserted at runtime by
           InsertControls, and shipping it keeps the two routes identical. */
        {"현재 인코딩 유지",new[]{"Keep current encoding","現在のエンコードを維持","保持当前编码","維持目前編碼"}},
        {"문서가 저장되지 않았습니다.",new[]{"The document was not saved.","文書は保存されませんでした。","文档未保存。","文件未儲存。"}},
        {"저장이 취소되었거나 다른 작업 때문에 중단되었습니다.",new[]{"Saving was canceled or interrupted by another operation.","保存がキャンセルされたか、別の操作により中断されました。","保存已取消或因其他操作而中断。","儲存已取消或因其他作業而中斷。"}},
        {"화면 준비 중…",new[]{"Preparing view…","画面を準備中…","正在准备视图…","正在準備畫面…"}},
        {"화면 구성 중…",new[]{"Building view…","画面を構成中…","正在构建视图…","正在建構畫面…"}},
        {"파일을 읽는 중…",new[]{"Reading file…","ファイルを読み込み中…","正在读取文件…","正在讀取檔案…"}},
        {"자동 감지",new[]{"Auto-detect","自動検出","自动检测","自動偵測"}},
        {"인코딩 지정",new[]{"Choose Encoding","エンコードの指定","指定编码","指定編碼"}},
        {"파일을 읽을 인코딩을 선택하세요.",new[]{"Choose an encoding to open the file.","ファイルを読み込むエンコードを選択してください。","请选择用于读取文件的编码。","請選擇用來讀取檔案的編碼。"}},
        {"열기",new[]{"Open","開く","打开","開啟"}},
        {"복구할 문서가 없습니다.",new[]{"There are no documents to recover.","回復する文書はありません。","没有可恢复的文档。","沒有可復原的文件。"}},
        {"문서 복구",new[]{"Recover Documents","文書の回復","恢复文档","復原文件"}},
        {"저장하지 못한 문서가 있습니다. 복구하면 새 탭으로 열립니다.",new[]{"Unsaved documents were found. Recovered documents open in new tabs.","保存されていない文書があります。回復すると新しいタブで開きます。","有未保存的文档。恢复后将在新标签页中打开。","有尚未儲存的文件。復原後將在新分頁中開啟。"}},
        {"나중에",new[]{"Later","後で","稍后","稍後"}},
        {"선택 복구본 삭제",new[]{"Delete selected","選択した回復データを削除","删除所选恢复文件","刪除所選復原檔案"}},
        {"선택한 복구본을 삭제할까요? 원본 파일은 유지되며 복구본 삭제는 되돌릴 수 없습니다.",new[]{"Delete the selected recovery copy? The original file is kept, and deleting a recovery copy cannot be undone.","選択した回復データを削除しますか？元のファイルは保持されますが、回復データの削除は元に戻せません。","要删除所选的恢复文件吗？原文件将保留，删除恢复文件无法撤销。","要刪除所選的復原檔案嗎？原始檔案會保留，刪除復原檔案無法復原。"}},
        {"선택 복구",new[]{"Recover selected","選択した文書を回復","恢复所选","復原所選"}},
        {"복구할 수 없습니다.\n",new[]{"Cannot recover the document.\n","回復できません。\n","无法恢复文档。\n","無法復原文件。\n"}},
        {"검색 중…",new[]{"Searching…","検索中…","正在查找…","正在尋找…"}},
        {"검색 실패",new[]{"Search failed","検索失敗","查找失败","尋找失敗"}},
        {"형식 변환 중…",new[]{"Converting format…","形式を変換中…","正在转换格式…","正在轉換格式…"}},
        {"인쇄 준비 중…",new[]{"Preparing to print…","印刷を準備中…","正在准备打印…","正在準備列印…"}},
        {"페이지 계산 중…",new[]{"Laying out pages…","ページを計算中…","正在计算页面…","正在計算頁面…"}},
        {"왼쪽 정렬",new[]{"Align left","左揃え","左对齐","靠左對齊"}},
        {"가운데 정렬",new[]{"Align center","中央揃え","居中对齐","置中對齊"}},
        {"오른쪽 정렬",new[]{"Align right","右揃え","右对齐","靠右對齊"}},
        {"표 내어쓰기",new[]{"Outdent table","表のインデント解除","减少表格缩进","減少表格縮排"}},
        {"표 들여쓰기",new[]{"Indent table","表のインデント","增加表格缩进","增加表格縮排"}},
        {"취소선",new[]{"Strikethrough","取り消し線","删除线","刪除線"}},
        {"형광펜",new[]{"Highlight","ハイライト","高亮","醒目提示"}},
        {"위 첨자",new[]{"Superscript","上付き","上标","上標"}},
        {"아래 첨자",new[]{"Subscript","下付き","下标","下標"}},
        {"이미지",new[]{"Image","画像","图片","圖片"}},
        {"바로 위에 목록 항목이 없어 표를 들여쓸 수 없습니다.",new[]{"Cannot indent table because there is no list item directly above it","直前にリスト項目がないため、表をインデントできません","正上方没有列表项，无法增加表格缩进","正上方沒有清單項目，無法增加表格縮排"}},
        {"표가 이미 목록 항목 안에 있어 더 들여쓸 수 없습니다.",new[]{"Cannot indent further because the table is already inside a list item","表はすでにリスト項目内にあるため、これ以上インデントできません","表格已在列表项内，无法继续增加缩进","表格已在清單項目內，無法再增加縮排"}},
        {"들여쓰기가 없어 표를 내어쓸 수 없습니다.",new[]{"Cannot outdent table because it is not indented","インデントされていないため、表のインデントを解除できません","表格没有缩进，无法减少缩进","表格沒有縮排，無法減少縮排"}},
        {"인용",new[]{"Quote","引用","引用","引用"}},
        {"한국어 (CP949)",new[]{"Korean (CP949)","韓国語 (CP949)","韩语 (CP949)","韓文 (CP949)"}},
        {"일본어 (Shift JIS)",new[]{"Japanese (Shift JIS)","日本語 (Shift JIS)","日语 (Shift JIS)","日文 (Shift JIS)"}},
        {"중국어 간체 (GBK)",new[]{"Chinese Simplified (GBK)","中国語 簡体字 (GBK)","简体中文 (GBK)","簡體中文 (GBK)"}},
        {"중국어 번체 (Big5)",new[]{"Chinese Traditional (Big5)","中国語 繁体字 (Big5)","繁体中文 (Big5)","繁體中文 (Big5)"}},
        {"서유럽 (Windows-1252)",new[]{"Western European (Windows-1252)","西ヨーロッパ言語 (Windows-1252)","西欧语言 (Windows-1252)","西歐語言 (Windows-1252)"}},
        {"다른 이름으로 저장하시겠습니까?",new[]{"Would you like to save it under a different name?","名前を付けて保存しますか？","要另存为其他名称吗？","要另存新檔嗎？"}},
        {"문서.txt",new[]{"Document.txt","文書.txt","文档.txt","文件.txt"}},
        {"문서.md",new[]{"Document.md","文書.md","文档.md","文件.md"}},
        {"링크를 열 수 없습니다.\n",new[]{"Cannot open the link.\n","リンクを開けません。\n","无法打开链接。\n","無法開啟連結。\n"}},
        {"MDNote 창으로 파일을 전달하지 못했습니다. 잠시 후 다시 열어 주세요.",new[]{"Could not pass the file to the MDNote window. Please try opening it again in a moment.","MDNote ウィンドウにファイルを渡せませんでした。しばらくしてからもう一度開いてください。","未能将文件传递到 MDNote 窗口。请稍后重新打开。","無法將檔案傳遞至 MDNote 視窗。請稍後再試著開啟。"}},
        {"[이미지: ",new[]{"[Image: ","[画像: ","[图片: ","[圖片: "}},
        {"텍스트 (*.txt)",new[]{"Text (*.txt)","テキスト (*.txt)","文本 (*.txt)","文字 (*.txt)"}},
        {"모든 파일",new[]{"All files","すべてのファイル","所有文件","所有檔案"}},
        {"파일을 연 뒤 다른 창이나 프로그램에서 내용이 변경되었습니다. 다른 이름으로 저장하여 두 버전을 보존해 주세요.",new[]{"The file was changed in another window or program after it was opened. Use Save As to keep both versions.","ファイルを開いた後、別のウィンドウまたはプログラムで内容が変更されました。名前を付けて保存して両方のバージョンを残してください。","打开文件后，其内容已在其他窗口或程序中被更改。请使用“另存为”保留两个版本。","開啟檔案後，內容已在其他視窗或程式中變更。請使用「另存新檔」保留兩個版本。"}},
        {"다른 창에서 이 파일을 저장하고 있습니다. 잠시 후 다시 저장해 주세요.",new[]{"Another window is saving this file. Please try saving again in a moment.","別のウィンドウでこのファイルを保存しています。しばらくしてからもう一度保存してください。","其他窗口正在保存此文件。请稍后再保存。","其他視窗正在儲存此檔案。請稍後再儲存。"}},
        {"이 파일은 다른 탭이나 창에서 이미 열려 있습니다. 그 탭이나 창에서 저장해 주세요.",new[]{"This file is already open in another tab or window. Please save it from that tab or window.","このファイルは別のタブまたはウィンドウで既に開かれています。そのタブまたはウィンドウで保存してください。","此文件已在其他标签页或窗口中打开。请在该标签页或窗口中保存。","此檔案已在其他索引標籤或視窗中開啟。請在該索引標籤或視窗中儲存。"}},
        {"선택한 인코딩과 파일의 BOM이 다릅니다. 자동 감지 또는 BOM에 맞는 인코딩을 선택해 주세요.",new[]{"The selected encoding does not match the file's BOM. Choose Auto-detect or the encoding that matches the BOM.","選択したエンコードとファイルの BOM が一致しません。自動検出、または BOM に合うエンコードを選択してください。","所选编码与文件的 BOM 不一致。请选择自动检测或与 BOM 匹配的编码。","所選編碼與檔案的 BOM 不一致。請選擇自動偵測或與 BOM 相符的編碼。"}},

        /* 2026-10-31 bug hunt 4/6: these two sentences reached the user in Korean under every UI
           language (TextMode.cs's aborted conversion tail and Workspace.cs's image-busy notice). */
        {"변환 중에 보기나 문서가 바뀌어 저장하지 못했습니다. 다시 저장해 주세요.",new[]{"The view or document changed during conversion, so the file was not saved. Please save again.","変換中に表示または文書が変わったため保存できませんでした。もう一度保存してください。","转换过程中视图或文档发生了变化，未能保存。请重新保存。","轉換過程中檢視或文件發生變化，未能儲存。請重新儲存。"}},
        {"지금은 이미지를 넣을 수 없습니다. 파일을 여는/저장하는 작업이 끝난 뒤 다시 시도해 주세요.",new[]{"Images cannot be inserted right now. Try again after the open or save operation finishes.","現在は画像を挿入できません。ファイルを開く／保存する操作が終わった後で、もう一度お試しください。","现在无法插入图片。请等文件打开或保存操作结束后再试。","現在無法插入圖片。請等檔案開啟或儲存操作結束後再試。"}},
        /* 2026-10-31 bug hunt 4/6: dropping a folder answered in Korean under every UI language
           (FileOpening.cs threw the raw Korean sentence). */
        {"폴더는 열 수 없습니다. MDNote는 파일만 열 수 있습니다.",new[]{"Folders cannot be opened. MDNote can open files only.","フォルダーを開くことはできません。MDNote で開けるのはファイルだけです。","无法打开文件夹。MDNote 只能打开文件。","無法開啟資料夾。MDNote 只能開啟檔案。"}},
        /* 2026-11-01: another program can hold the clipboard for a moment; after the retries the
           status line says so instead of a paste or copy that silently did nothing. */
        {"인쇄 미리 보기",new[]{"Print Preview","印刷プレビュー","打印预览","預覽列印"}},
        {"클립보드를 열 수 없습니다. 잠시 후 다시 붙여넣으세요.",new[]{"The clipboard cannot be opened. Please try pasting again in a moment.","クリップボードを開けません。しばらくしてからもう一度貼り付けてください。","无法打开剪贴板。请稍后再次粘贴。","無法開啟剪貼簿。請稍後再次貼上。"}},
        {"클립보드를 열 수 없습니다. 잠시 후 다시 복사하세요.",new[]{"The clipboard cannot be opened. Please try copying again in a moment.","クリップボードを開けません。しばらくしてからもう一度コピーしてください。","无法打开剪贴板。请稍后再次复制。","無法開啟剪貼簿。請稍後再次複製。"}},
        /* 2026-11-09: the 파일 tab's own captions, moved here from the file-tab table in FolderTree.cs
           so the panel reads them through T() like every other caption ("목차" is already above). */
        {"파일",new[]{"Files","ファイル","文件","檔案"}},
        {"다른 폴더 열기",new[]{"Open another folder","別のフォルダーを開く","打开其他文件夹","開啟其他資料夾"}},
        {"새로 고침",new[]{"Refresh","更新","刷新","重新整理"}},
        {"탐색기에서 보기",new[]{"Show in Explorer","エクスプローラーで表示","在资源管理器中显示","在檔案總管中顯示"}},
        {"경로 복사",new[]{"Copy path","パスをコピー","复制路径","複製路徑"}},
        {"더 있음",new[]{"More","さらにあります","更多","還有更多"}},
        {"폴더 열기",new[]{"Open folder","フォルダーを開く","打开文件夹","開啟資料夾"}}
    };
    internal string TranslateLabel(string value){return T(value);}
    string OriginalKey(string value){if(translations.ContainsKey(value))return value;foreach(var pair in translations)foreach(string translated in pair.Value)if(translated==value)return pair.Key;return value;}
    string T(string key){return TranslatedText(language,key);}
    /* Stateless lookup for a caller with no window: the second instance reports a failed file forward
       (DocumentActivation.Run) before any MainWindow exists. Same rules as T. */
    internal static string TranslatedText(string code,string key){int exact=Array.IndexOf(languageCodes,code)-1;string[] whole;if(exact>=0&&translations.TryGetValue(key,out whole))return whole[exact];/* a whole key first: "제목 단계 표시" must not become "Heading 단계 표시" */if(key.StartsWith("제목 ",StringComparison.Ordinal))return TranslatedText(code,"제목")+key.Substring(2).Replace("적용",TranslatedText(code,"적용"));if(key.Contains(" · ")){int split=key.IndexOf(" · ",StringComparison.Ordinal);return TranslatedText(code,key.Substring(0,split))+key.Substring(split);}int index=Array.IndexOf(languageCodes,code)-1;string[] values;return index>=0&&translations.TryGetValue(key,out values)?values[index]:key;}
    /* A probe never loads the user's settings file (LoadPreferences), so a window-less message read from
       that file came out in whatever language the user last picked (VM uifix s4, 11-10). In a probe the
       language the probe window last applied answers instead. */
    static string probeLanguage;
    internal static string SavedLanguage(){if(Program.IsSelfTest&&probeLanguage!=null)return probeLanguage;try{bool unreadable;return ViewPreferences.Load(PreferencesPath,out unreadable).Language;}catch(Exception){return "ko";}}
    void BuildLanguages(Menu menu,MenuItem help){
        var languages=new MenuItem {Header="언어"};menu.Items.Insert(menu.Items.IndexOf(help),languages);
        for(int i=0;i<languageCodes.Length;i++){string code=languageCodes[i];var item=new MenuItem {Header=languageNames[i],IsCheckable=true,Tag=code};item.Click+=delegate{language=code;ApplyLanguage();SavePreferences();};languages.Items.Add(item);languageItems.Add(item);}
        ApplyLanguage();
    }
    void ApplyLanguage(){
        if(Program.IsSelfTest)probeLanguage=language;
        if(Array.IndexOf(languageCodes,language)<0)language="ko";FontFamily=Ui.ChromeFamily(language);Resources["ChromeFont"]=FontFamily;foreach(var chrome in new DependencyObject[]{root.Children[0],documentTabs,ribbonHost,tableToolsHost,searchHost,statusBar,saveMenu,headingMenu,colorPopup.Child})if(chrome!=null)Ui.ToolbarText(chrome,FontFamily);TranslateTree(root);if(colorPopup.Child!=null)TranslateTree(colorPopup.Child);TranslateTree(saveMenu);/* b58: the » lists are built once per width change; their items keep the old language until they are rebuilt, so translate them now (names and tooltips). */if(ribbonOverflowMenu!=null)TranslateTree(ribbonOverflowMenu);if(topOverflowMenu!=null)TranslateTree(topOverflowMenu);foreach(var document in documents){var header=document.Item.Header as StackPanel;if(header!=null&&header.Children.Count>1)((Button)header.Children[1]).ToolTip=T("탭 닫기 (Ctrl+W)");}UpdateEditCaption();editButton.ToolTip=EditToggleTip();preview.ToolTip=SourceToggleTip();if(outlineButton!=null)outlineButton.ToolTip=OutlineToggleTip();if(quickText!=null)quickText.ToolTip=TextToggleTip();SyncViewButtonWidths();/* b58: this one name follows the mode, not only the language (UpdateTools rewrites it on a mode change); re-apply it after the tree pass so a switch in either direction is right. */System.Windows.Automation.AutomationProperties.SetName(editButton,editing?T("수정 종료"):T("수정 시작"));if(sizes.Items.Count>0)sizes.Items[0]=T("크기");UpdateHeadingCaption();UpdateListCaption();UpdateTitle();UpdateLineMenu();foreach(var item in languageItems)item.IsChecked=Object.Equals(item.Tag,language);
    }
    void TranslateTree(DependencyObject element){
        /* The list dropdown headers are composed in UpdateListCaption ("· " / "1. " plus the
           translated name); translating them here would replace the header and drop the prefix.
           The custom line-spacing caption ("사용자 지정 (175%)") is rebuilt by UpdateLineMenu after
           the translation pass; its cached original would otherwise put the Korean caption back
           when a narrow window's View menu is translated after that rebuild. */
        if(TranslateFocusChoice(element))return;
        if(element==status||element==documentTabs||element==source||element==outline||element==listBulletMenuItem||element==listNumberMenuItem||element==customLine)return;var menu=element as MenuItem;var content=element as ContentControl;var textBlock=element as TextBlock;
        string value=menu!=null?menu.Header as string:content!=null?content.Content as string:textBlock!=null?textBlock.Text:null;
        if(value!=null){string original;if(!originalLabels.TryGetValue(element,out original)){original=OriginalKey(value);originalLabels.Remove(element);originalLabels.Add(element,original);}string translated=T(original);if(menu!=null)menu.Header=translated;else if(content!=null)content.Content=translated;else textBlock.Text=translated;}
        var framework=element as FrameworkElement;if(framework!=null&&framework.ToolTip is string){string tip;if(!originalTips.TryGetValue(element,out tip)){tip=(string)framework.ToolTip;tip=OriginalKey(tip);originalTips.Remove(element);originalTips.Add(element,tip);}framework.ToolTip=T(tip);}
        /* b58: AutomationProperties.Name was a Korean literal or the startup language for the life of
           the window (the toolbar icons, the find bar and the » button are created once). It is
           re-applied here, next to the tooltip, so a language switch reaches what Narrator reads. */
        string automationName=System.Windows.Automation.AutomationProperties.GetName(element);
        if(!String.IsNullOrEmpty(automationName)){string originalName;if(!originalNames.TryGetValue(element,out originalName)){originalName=OriginalKey(automationName);originalNames.Remove(element);originalNames.Add(element,originalName);}System.Windows.Automation.AutomationProperties.SetName(element,T(originalName));}
        foreach(object child in LogicalTreeHelper.GetChildren(element)){var dependency=child as DependencyObject;if(dependency!=null)TranslateTree(dependency);}
    }
}
}
