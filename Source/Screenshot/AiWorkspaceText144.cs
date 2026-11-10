namespace ScreenshotTranslationUiTester;

// Workspace-only strings keep the 144 view independent from settings edits.
internal static class AiWorkspaceText144
{
    private static readonly Dictionary<string,string[]> Values=new(StringComparer.Ordinal)
    {
        ["CacheDiscountUnknown"]=["Cache discounts are not included.","未计入缓存优惠。","未計入快取優惠。","キャッシュ割引は含まれません。","캐시 할인은 포함되지 않습니다.","No incluye descuentos de caché.","Les remises de cache ne sont pas incluses.","Cache-Rabatte sind nicht enthalten.","Скидки за кэш не учтены."],
        ["NoChoices"]=["No matching choices","没有匹配项","沒有相符項目","一致する項目がありません","일치하는 항목 없음","No hay coincidencias","Aucun résultat","Keine passenden Einträge","Нет подходящих вариантов"],
        ["Flow"]=["Execution","执行过程","執行過程","実行状況","실행 과정","Ejecución","Exécution","Ausführung","Ход выполнения"],
        ["running"]=["Running","运行中","執行中","実行中","실행 중","En curso","En cours","Wird ausgeführt","Выполняется"],
        ["completed"]=["Completed","已完成","已完成","完了","완료","Completado","Terminé","Abgeschlossen","Завершено"],
        ["failed"]=["Failed","失败","失敗","失敗","실패","Falló","Échec","Fehlgeschlagen","Ошибка"],
        ["stopped"]=["Stopped","已停止","已停止","停止","중지됨","Detenido","Arrêté","Gestoppt","Остановлено"],
        ["interrupted"]=["Interrupted","已中断","已中斷","中断","중단됨","Interrumpido","Interrompu","Unterbrochen","Прервано"],
        ["unknown"]=["Final status not reported","未报告结束状态","未回報結束狀態","終了状態は未報告","종료 상태 미보고","Estado final no comunicado","État final non signalé","Endstatus nicht gemeldet","Конечное состояние не сообщено"],
        ["pending"]=["Pending","待处理","待處理","待機中","대기 중","Pendiente","En attente","Ausstehend","Ожидает"],
        ["command"]=["Command","命令","命令","コマンド","명령","Comando","Commande","Befehl","Команда"],
        ["file"]=["File changes","文件变更","檔案變更","ファイル変更","파일 변경","Cambios de archivos","Fichiers modifiés","Dateiänderungen","Изменения файлов"],
        ["plan"]=["Plan","计划","計畫","計画","계획","Plan","Plan","Plan","План"],
        ["subagent"]=["Subtask","子任务","子任務","サブタスク","하위 작업","Subtarea","Sous-tâche","Teilaufgabe","Подзадача"],
        ["tool"]=["Tool","工具","工具","ツール","도구","Herramienta","Outil","Werkzeug","Инструмент"],
        ["reasoning"]=["Reasoning summary","思考摘要","思考摘要","推論の要約","추론 요약","Resumen de razonamiento","Résumé du raisonnement","Zusammenfassung der Überlegungen","Краткое рассуждение"],
        ["status"]=["Status","状态","狀態","状態","상태","Estado","État","Status","Состояние"],
        ["assistant"]=["Assistant output","助手输出","助手輸出","アシスタント出力","도우미 출력","Respuesta del asistente","Réponse de l’assistant","Assistentenausgabe","Ответ помощника"],
        ["Truncated"]=["Long output was shortened.","较长的输出已截短。","較長的輸出已截短。","長い出力を省略しました。","긴 출력을 줄였습니다.","Se ha acortado la salida larga.","La sortie longue a été abrégée.","Lange Ausgabe wurde gekürzt.","Длинный вывод сокращён."],
        ["AttachFiles"]=["Add files or images","添加文件或图片","新增檔案或圖片","ファイルや画像を追加","파일 또는 이미지 추가","Añadir archivos o imágenes","Ajouter des fichiers ou images","Dateien oder Bilder hinzufügen","Добавить файлы или изображения"],
        ["AttachmentHelp"]=["Up to 8 files · 16 MB each · 48 MB total. Adding a file does not analyze it.","最多 8 个文件，单个 16 MB，合计 48 MB。添加后尚未分析。","最多 8 個檔案，每個 16 MB，合計 48 MB。新增後尚未分析。","最大8ファイル・各16 MB・合計48 MB。追加だけでは解析されません。","최대 8개 · 각 16 MB · 총 48 MB. 추가만으로 분석되지 않습니다.","Hasta 8 archivos · 16 MB cada uno · 48 MB en total. Añadir no implica analizar.","8 fichiers max. · 16 Mo chacun · 48 Mo au total. L’ajout ne lance pas l’analyse.","Bis zu 8 Dateien · je 16 MB · insgesamt 48 MB. Hinzufügen startet keine Analyse.","До 8 файлов · по 16 МБ · всего 48 МБ. Добавление не запускает анализ."],
        ["Remove"]=["Remove attachment","移除附件","移除附件","添付を削除","첨부 제거","Quitar adjunto","Retirer la pièce jointe","Anhang entfernen","Удалить вложение"],
        ["FileReference"]=["Reference as a file path","按文件路径引用","以檔案路徑參照","ファイルパスとして参照","파일 경로로 참조","Referenciar como ruta de archivo","Référencer par chemin de fichier","Als Dateipfad referenzieren","Ссылка на путь файла"],
        ["FileReferenceHelp"]=["This sends a file reference, not image input. The model may use local tools if available.","只提供文件引用，不作为图像输入；模型可使用可用的本地工具。","僅提供檔案參照，不作為影像輸入；模型可使用可用的本機工具。","画像入力ではなくファイル参照を送信します。利用可能なローカルツールを使えます。","이미지 입력 대신 파일 참조를 보냅니다. 사용 가능한 로컬 도구를 이용할 수 있습니다.","Envía una referencia, no una imagen. El modelo puede usar herramientas locales disponibles.","Envoie une référence, pas une image. Les outils locaux disponibles peuvent être utilisés.","Sendet einen Dateiverweis statt einer Bildeingabe. Verfügbare lokale Werkzeuge können genutzt werden.","Передаётся путь, а не изображение. Модель может использовать доступные локальные инструменты."],
        ["Locations"]=["Saved locations","保存位置","儲存位置","保存場所","저장 위치","Ubicaciones guardadas","Emplacements","Speicherorte","Места сохранения"],
        ["OpenProject"]=["Open project folder","打开项目文件夹","開啟專案資料夾","プロジェクトフォルダーを開く","프로젝트 폴더 열기","Abrir carpeta del proyecto","Ouvrir le dossier du projet","Projektordner öffnen","Открыть папку проекта"],
        ["OpenConversation"]=["Open conversation folder","打开对话保存位置","開啟對話儲存位置","会話フォルダーを開く","대화 폴더 열기","Abrir carpeta de la conversación","Ouvrir le dossier de conversation","Gesprächsordner öffnen","Открыть папку диалога"],
        ["LocationMissing"]=["This folder is not available yet.","此文件夹尚不存在。","此資料夾尚不存在。","このフォルダーはまだありません。","아직 폴더가 없습니다.","Esta carpeta aún no está disponible.","Ce dossier n’est pas encore disponible.","Dieser Ordner ist noch nicht vorhanden.","Эта папка пока недоступна."],
        ["LoadingModels"]=["Loading models…","正在加载模型…","正在載入模型…","モデルを読み込み中…","모델 불러오는 중…","Cargando modelos…","Chargement des modèles…","Modelle werden geladen…","Загрузка моделей…"],
        ["CatalogFailed"]=["Could not refresh models. Your current model is kept.","模型列表刷新失败，已保留当前模型。","模型清單重新整理失敗，已保留目前模型。","モデル一覧を更新できません。現在のモデルを保持します。","모델 목록 갱신 실패. 현재 모델을 유지합니다.","No se pudo actualizar la lista. Se conserva el modelo actual.","Actualisation impossible. Le modèle actuel est conservé.","Modellliste konnte nicht aktualisiert werden. Aktuelles Modell bleibt erhalten.","Не удалось обновить список. Текущая модель сохранена."],
        ["ModSource"]=["Choose a text MOD source","选择文本 MOD 源文件","選擇文字 MOD 來源檔","テキストMODの元ファイルを選択","텍스트 MOD 원본 선택","Elegir fuente de MOD de texto","Choisir le fichier source du MOD texte","Text-MOD-Quelldatei wählen","Выбрать исходный текстовый MOD"],
        ["attachment_image_unknown"]=["Image input support is unknown. Choose a supported model or reference the image as a file path.","无法确认模型支持图片。请选择支持的模型，或按文件路径引用。","無法確認模型支援圖片。請選擇支援的模型，或以檔案路徑參照。","画像入力の対応が不明です。対応モデルまたはファイルパス参照を選択してください。","이미지 지원 여부를 알 수 없습니다. 지원 모델 또는 파일 경로 참조를 선택하세요.","No se conoce el soporte de imágenes. Elige un modelo compatible o una referencia de archivo.","Prise en charge des images inconnue. Choisissez un modèle compatible ou une référence de fichier.","Bildunterstützung unbekannt. Unterstütztes Modell oder Dateipfad wählen.","Поддержка изображений неизвестна. Выберите совместимую модель или ссылку на файл."],
        ["attachment_image_unsupported"]=["This model does not support image input. Choose another model or reference it as a file path.","此模型不支持图片输入。请选择其他模型，或按文件路径引用。","此模型不支援圖片輸入。請選擇其他模型，或以檔案路徑參照。","このモデルは画像入力に非対応です。別のモデルかファイルパス参照を選んでください。","이 모델은 이미지 입력을 지원하지 않습니다. 다른 모델 또는 파일 경로 참조를 선택하세요.","Este modelo no admite imágenes. Elige otro modelo o una referencia de archivo.","Ce modèle ne prend pas en charge les images. Choisissez un autre modèle ou une référence de fichier.","Dieses Modell unterstützt keine Bildeingaben. Anderes Modell oder Dateipfad wählen.","Модель не поддерживает изображения. Выберите другую модель или ссылку на файл."],
        ["attachment_count"]=["A conversation draft can contain up to 8 attachments.","每份对话草稿最多添加 8 个附件。","每份對話草稿最多新增 8 個附件。","下書きに添付できるのは最大8件です。","대화 초안당 첨부는 최대 8개입니다.","El borrador admite hasta 8 adjuntos.","Le brouillon peut contenir jusqu’à 8 pièces jointes.","Ein Entwurf kann bis zu 8 Anhänge enthalten.","В черновике может быть до 8 вложений."],
        ["attachment_size"]=["Attachment limit: 16 MB each and 48 MB total.","附件限制：单个 16 MB，合计 48 MB。","附件限制：每個 16 MB，合計 48 MB。","添付上限：各16 MB、合計48 MB。","첨부 제한: 각 16 MB, 총 48 MB.","Límite: 16 MB por adjunto y 48 MB en total.","Limite : 16 Mo par fichier et 48 Mo au total.","Anhanglimit: je 16 MB, insgesamt 48 MB.","Лимит: 16 МБ на файл и 48 МБ всего."],
        ["attachment_missing"]=["An attachment is missing. Remove it and add it again.","找不到附件，请移除后重新添加。","找不到附件，請移除後重新新增。","添付が見つかりません。削除して再追加してください。","첨부를 찾을 수 없습니다. 제거 후 다시 추가하세요.","Falta un adjunto. Quítalo y vuelve a añadirlo.","Pièce jointe introuvable. Retirez-la puis ajoutez-la à nouveau.","Anhang fehlt. Entfernen und erneut hinzufügen.","Вложение не найдено. Удалите его и добавьте снова."],
        ["attachment_changed"]=["An attachment changed after it was added. Add it again before sending.","附件添加后发生了变化，请重新添加再发送。","附件新增後發生變更，請重新新增再傳送。","追加後に添付が変わりました。再追加してから送信してください。","추가 후 첨부가 변경되었습니다. 다시 추가한 뒤 보내세요.","Un adjunto cambió. Vuelve a añadirlo antes de enviar.","Une pièce jointe a changé. Ajoutez-la à nouveau avant l’envoi.","Ein Anhang wurde geändert. Vor dem Senden erneut hinzufügen.","Вложение изменилось. Добавьте его снова перед отправкой."],
        ["attachment_invalid"]=["This attachment is invalid or uses an unsupported image format.","附件无效，或图片格式不受支持。","附件無效，或圖片格式不受支援。","添付が無効か、画像形式に対応していません。","잘못된 첨부이거나 지원하지 않는 이미지 형식입니다.","Adjunto no válido o formato de imagen no compatible.","Pièce jointe invalide ou format d’image non pris en charge.","Ungültiger Anhang oder nicht unterstütztes Bildformat.","Недопустимое вложение или неподдерживаемый формат изображения."],
        ["attachment_storage"]=["Could not save the attachment. Check the conversation storage folder.","无法保存附件，请检查对话存储位置。","無法儲存附件，請檢查對話儲存位置。","添付を保存できません。会話の保存先を確認してください。","첨부를 저장할 수 없습니다. 대화 저장 폴더를 확인하세요.","No se pudo guardar el adjunto. Revisa la carpeta de la conversación.","Impossible d’enregistrer la pièce jointe. Vérifiez le dossier de conversation.","Anhang konnte nicht gespeichert werden. Gesprächsordner prüfen.","Не удалось сохранить вложение. Проверьте папку диалога."],
    };
    internal static bool Contains(string key)=>Values.ContainsKey(key);
    internal static string Text(string key)
    {
        if(!Values.TryGetValue(key,out var row))return AiToolsText.Text(key);
        int index=UiLanguage.Current switch{ApplicationLanguage.SimplifiedChinese=>1,ApplicationLanguage.TraditionalChinese=>2,ApplicationLanguage.Japanese=>3,ApplicationLanguage.Korean=>4,ApplicationLanguage.Spanish=>5,ApplicationLanguage.French=>6,ApplicationLanguage.German=>7,ApplicationLanguage.Russian=>8,_=>0};
        return row[index];
    }
}
