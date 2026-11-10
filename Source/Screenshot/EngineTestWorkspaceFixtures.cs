using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// These projects contain only owned neutral text and controls. Production
// adapters are copied independently from this assembly's embedded resources.
internal static class EngineTestWorkspaceFixtures
{
    internal static string Godot(bool v4, string sample, string fontFile)
    {
        string Quote(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string source = "extends SceneTree\nvar checks = []\nfunc check(value, name):\n    checks.append({\"name\":name,\"passed\":bool(value)})\nfunc _init():\n    call_deferred(\"run\")\n";
        source += v4 ? "func primary(font):\n    return font.base_font if font is FontVariation else font\n" : "func primary(font):\n    return font.font_data if font is DynamicFont else font\n";
        source += "func entry_for(bridge, node):\n    for entry in bridge.entries:\n        if entry.id == node.get_instance_id() and entry.property == \"text\":\n            return entry\n    return {}\n";
        source += "func run():\n";
        source += "    OS.set_environment(\"FUSION_GODOT_SESSION\", ProjectSettings.globalize_path(\"res://session\"))\n    OS.set_environment(\"FUSION_GODOT_SECRET\", \"owned-font-test-134\")\n    OS.set_environment(\"FUSION_GODOT_TRANSLATION\", \"0\")\n    OS.set_environment(\"FUSION_GAME_FONT_PATH\", \"\")\n    OS.set_environment(\"FUSION_GAME_FONT_FAMILY\", \"\")\n";
        source += v4 ? "    root.size = Vector2i(900, 420)\n    var authored = FontFile.new()\n    authored.load_dynamic_font(\"res://authored.ttf\")\n" : "    var data = DynamicFontData.new()\n    data.font_path = \"res://authored.ttf\"\n    var authored = DynamicFont.new()\n    authored.font_data = data\n    authored.size = 26\n";
        source += "    var label = Label.new()\n    label.text = \"Neutral dialogue sample\"\n";
        source += v4 ? "    label.position = Vector2(30, 25)\n    label.size = Vector2(840, 160)\n    label.add_theme_font_override(\"font\", authored)\n    label.add_theme_font_size_override(\"font_size\", 26)\n" : "    label.rect_position = Vector2(30, 25)\n    label.rect_size = Vector2(840, 160)\n    label.add_font_override(\"font\", authored)\n";
        source += "    root.add_child(label)\n    var button = Button.new()\n    button.text = \"Continue\"\n";
        source += v4 ? "    button.position = Vector2(30, 230)\n    button.size = Vector2(400, 80)\n    button.add_theme_font_override(\"font\", authored)\n    button.add_theme_font_size_override(\"font_size\", 26)\n" : "    button.rect_position = Vector2(30, 230)\n    button.rect_size = Vector2(400, 80)\n    button.add_font_override(\"font\", authored)\n";
        source += "    root.add_child(button)\n    var bridge = load(\"res://bridge.gd\").new()\n    root.add_child(bridge)\n    bridge.set_process(false)\n    var pid = OS.get_process_id()\n";
        string Font(string obj) => obj + (v4 ? ".get_theme_font(\"font\")" : ".get_font(\"font\")");
        string Draw(string name)
        {
            string text = v4 ? "    await process_frame\n    await RenderingServer.frame_post_draw\n    var " + name + " = root.get_texture().get_image()\n" : "    yield(self, \"idle_frame\")\n    yield(VisualServer, \"frame_post_draw\")\n    var " + name + " = root.get_texture().get_data()\n    " + name + ".flip_y()\n";
            return text + "    " + name + ".save_png(\"res://" + name + ".png\")\n";
        }
        source += Draw("original");
        source += "    check(not bridge.enabled and " + Font("button") + " == authored, \"开始时保留样例原字体\")\n";
        source += "    var independent = bridge._command({\"op\":\"fontPreferences\",\"mode\":\"custom\",\"path\":ProjectSettings.globalize_path(" + Quote("res://" + fontFile) + "),\"independent\":true})\n    check(independent.get(\"accepted\", false) and independent.get(\"independent\", false) and not bridge.enabled, \"未开启翻译即可更换字体\")\n";
        source += "    check(not bridge.collection_started and not bridge.scan_thread." + (v4 ? "is_started" : "is_active") + "(), \"字体测试不启动资源读取\")\n    check(bridge.pending.size() == 0 and bridge.cache.size() == 0, \"字体测试不生成翻译请求或缓存\")\n    check(button.text == \"Continue\" and primary(" + Font("button") + ") == bridge.custom_font, \"原文控件使用所选字体\")\n";
        source += Draw("font_only");
        source += "    bridge._command({\"op\":\"fontPreferences\",\"mode\":\"default\",\"independent\":true})\n";
        source += Draw("font_reset");
        source += "    check(font_reset.get_data() == original.get_data(), \"未开启翻译时恢复原始像素\")\n";
        source += "    bridge._command({\"op\":\"translationPrepare\"})\n    bridge._command({\"op\":\"translationApply\",\"epoch\":bridge.epoch,\"prime\":true,\"entries\":[{\"source\":\"Neutral dialogue sample\",\"text\":" + Quote(sample) + "},{\"source\":\"Continue\",\"text\":\"Continuer\"}]})\n    bridge._command({\"op\":\"translationEnable\"})\n";
        source += "    check(bridge.enabled and button.text == \"Continuer\", \"同一进程显示预置译文\")\n    check(primary(" + Font("button") + ") == primary(authored), \"默认适配保留样例原字体\")\n";
        source += Draw("baseline");
        source += "    var selected = bridge._command({\"op\":\"fontPreferences\",\"mode\":\"custom\",\"path\":ProjectSettings.globalize_path(" + Quote("res://" + fontFile) + ")})\n    check(selected.get(\"accepted\", false) and bridge.custom_font != null, \"引擎实际加载所选字体\")\n    check(primary(" + Font("button") + ") == bridge.custom_font, \"控件绑定所选字体资源\")\n";
        source += "    var row = entry_for(bridge, label)\n    var display = row.label_display.get_ref()\n    check(display.text == " + Quote(sample) + ", \"显示文字与样文一致\")\n    check(primary(" + Font("display") + ") == bridge.custom_font, \"译文显示使用所选字体\")\n";
        source += Draw("custom");
        source += "    check(custom.get_data().size() > 0, \"引擎生成实际像素图\")\n    bridge._command({\"op\":\"fontPreferences\",\"mode\":\"default\"})\n    check(bridge.custom_font == null and primary(" + Font("button") + ") == primary(authored), \"恢复默认字体资源\")\n";
        source += Draw("restored");
        source += "    check(restored.get_data() == baseline.get_data(), \"恢复默认后的像素与基准一致\")\n    bridge._command({\"op\":\"translationDisable\"})\n";
        source += Draw("disabled");
        source += "    check(disabled.get_data() == original.get_data(), \"关闭后恢复原始像素\")\n    check(OS.get_process_id() == pid, \"字体测试未重启引擎\")\n    check(not bridge.speed_authorized, \"字体测试未开启游戏修改\")\n    var passed = true\n    for row_check in checks:\n        if not row_check.passed:\n            passed = false\n    var report = {\"checks\":checks,\"engine\":Engine.get_version_info().string,\"pid\":pid,\"characterBindingVerified\":false,\"originalGame\":false}\n";
        source += v4 ? "    FileAccess.open(\"res://engine-result.json\", FileAccess.WRITE).store_string(JSON.stringify(report, \"  \"))\n" : "    var file = File.new()\n    file.open(\"res://engine-result.json\", File.WRITE)\n    file.store_string(JSON.print(report, \"  \"))\n    file.close()\n";
        source += "    bridge.queue_free()\n    quit(0 if passed else 1)\n";
        return source;
    }
}
