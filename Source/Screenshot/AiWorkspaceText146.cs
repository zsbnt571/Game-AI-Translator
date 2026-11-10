namespace ScreenshotTranslationUiTester;
internal static class AiWorkspaceText146
{
    private static readonly Dictionary<string,string[]> Values=new(StringComparer.Ordinal)
    {
        ["ai_task_title_invalid"]=["Enter a conversation name of 1–200 characters without control characters.","请输入 1–200 个字符的对话名称，不含控制字符。","請輸入 1–200 個字元的對話名稱，不含控制字元。","制御文字を含まない1～200文字の会話名を入力してください。","제어 문자 없이 1~200자의 대화 이름을 입력하세요.","Escribe un nombre de 1 a 200 caracteres, sin caracteres de control.","Saisissez un nom de 1 à 200 caractères, sans caractères de contrôle.","Gesprächsnamen mit 1–200 Zeichen ohne Steuerzeichen eingeben.","Введите название длиной 1–200 символов без управляющих знаков."],
        ["TaskOptions"]=["Conversation options","对话操作","對話操作","会話の操作","대화 옵션","Opciones de conversación","Options du dialogue","Gesprächsoptionen","Действия с диалогом"],
        ["RenameTask"]=["Rename","重命名","重新命名","名前を変更","이름 변경","Cambiar nombre","Renommer","Umbenennen","Переименовать"],
        ["PinTask"]=["Pin","置顶","置頂","固定","고정","Fijar","Épingler","Anheften","Закрепить"],
        ["UnpinTask"]=["Unpin","取消置顶","取消置頂","固定を解除","고정 해제","Desfijar","Désépingler","Lösen","Открепить"],
        ["DeleteTask"]=["Remove conversation","删除对话","刪除對話","会話を削除","대화 삭제","Eliminar conversación","Supprimer le dialogue","Gespräch entfernen","Удалить диалог"],
        ["TaskName"]=["Conversation name","对话名称","對話名稱","会話名","대화 이름","Nombre de conversación","Nom du dialogue","Gesprächsname","Название диалога"],
        ["DeleteTaskHelp"]=["Remove this conversation from the list? Project files and saved execution materials will be kept.","从对话列表删除此对话？项目文件和已保存的执行材料会保留。","從對話清單刪除此對話？專案檔案與已儲存的執行資料會保留。","一覧からこの会話を削除しますか？プロジェクトファイルと保存済みの実行資料は保持されます。","목록에서 이 대화를 삭제할까요? 프로젝트 파일과 저장된 실행 자료는 유지됩니다.","¿Quitar esta conversación de la lista? Se conservarán los archivos del proyecto y los materiales de ejecución guardados.","Retirer ce dialogue de la liste ? Les fichiers du projet et les données d’exécution enregistrées seront conservés.","Dieses Gespräch aus der Liste entfernen? Projektdateien und gespeicherte Ausführungsdaten bleiben erhalten.","Удалить диалог из списка? Файлы проекта и сохранённые материалы выполнения останутся."],
        ["TaskRunningDelete"]=["Stop this conversation before removing it.","此对话正在运行，请先停止再删除。","此對話正在執行，請先停止再刪除。","この会話を停止してから削除してください。","이 대화를 중지한 후 삭제하세요.","Detén esta conversación antes de eliminarla.","Arrêtez ce dialogue avant de le supprimer.","Dieses Gespräch vor dem Entfernen stoppen.","Перед удалением остановите этот диалог."],
        ["SharedApiHelp"]=["API connections can be shared by compatible assistants. Switch Codex or OpenCode at the top left of the conversation.","API 连接可供兼容的助手共用，在对话左上角切换 Codex 或 OpenCode。","API 連線可供相容的助手共用，在對話左上角切換 Codex 或 OpenCode。","API 接続は対応するアシスタント間で共有できます。会話の左上で Codex と OpenCode を切り替えます。","API 연결은 호환되는 도우미가 공유합니다. 대화 왼쪽 위에서 Codex 또는 OpenCode를 전환하세요.","Los asistentes compatibles comparten conexiones API. Cambia entre Codex y OpenCode arriba a la izquierda del chat.","Les assistants compatibles partagent les connexions API. Changez Codex ou OpenCode en haut à gauche du dialogue.","Kompatible Assistenten teilen API-Verbindungen. Codex oder OpenCode oben links im Gespräch wechseln.","Совместимые помощники могут использовать одно API-подключение. Переключайте Codex и OpenCode слева вверху диалога."],
        ["LoginAssistant"]=["Sign-in assistant","登录助手","登入助手","ログイン先アシスタント","로그인 도우미","Asistente de inicio de sesión","Assistant de connexion","Assistent für Anmeldung","Помощник для входа"],
        ["ai_account_backend_mismatch"]=["This account login belongs to another assistant. Switch to that assistant or select a compatible API connection.","此账号登录属于另一助手。请切换到对应助手，或选择兼容的 API 连接。","此帳號登入屬於另一助手。請切換至對應助手，或選擇相容的 API 連線。","このアカウントは別のアシスタント用です。対応するアシスタントか互換 API 接続を選んでください。","이 계정 로그인은 다른 도우미용입니다. 해당 도우미 또는 호환 API 연결을 선택하세요.","Esta cuenta pertenece a otro asistente. Cambia al asistente correspondiente o elige una conexión API compatible.","Ce compte appartient à un autre assistant. Changez d’assistant ou choisissez une connexion API compatible.","Dieses Konto gehört zu einem anderen Assistenten. Passenden Assistenten oder kompatible API-Verbindung wählen.","Этот вход связан с другим помощником. Переключитесь на него или выберите совместимое API-подключение."],
    };
    internal static IEnumerable<string> Keys=>Values.Keys;
    internal static bool Contains(string key)=>Values.ContainsKey(key);
    internal static string Text(string key)
    {
        if(!Values.TryGetValue(key,out var row))return AiToolsText.Text(key);
        int index=UiLanguage.Current switch{ApplicationLanguage.SimplifiedChinese=>1,ApplicationLanguage.TraditionalChinese=>2,ApplicationLanguage.Japanese=>3,ApplicationLanguage.Korean=>4,ApplicationLanguage.Spanish=>5,ApplicationLanguage.French=>6,ApplicationLanguage.German=>7,ApplicationLanguage.Russian=>8,_=>0};return row[index];
    }
}
