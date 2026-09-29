namespace ScreenshotTranslationUiTester;

internal sealed class ThemeChoiceButton:Button
{
    internal ApplicationThemeMode Mode {get;set;}
    internal ThemeChoiceButton(ApplicationThemeMode mode){Mode=mode;Size=new(96,36);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;SetStyle(ControlStyles.OptimizedDoubleBuffer,true);Text=mode==ApplicationThemeMode.Night?"夜间":"日间";}
    protected override void OnPaint(PaintEventArgs e)
    {
        var p=UiTheme.Current;var g=e.Graphics;g.Clear(Parent?.BackColor??p.Main);var r=Rectangle.Inflate(ClientRectangle,-2,-2);
        bool selected=ReferenceEquals(p,Mode==ApplicationThemeMode.Night?ThemePalette.Night:ThemePalette.Day);
        using(var b=new SolidBrush(selected?WorkspaceSkin.SoftAccent:p.Secondary))g.FillRectangle(b,r);
        using(var border=new Pen(selected?p.Accent:p.Border,selected?2:1))g.DrawRectangle(border,r);
        TextRenderer.DrawText(g,Text,Font,r,selected?p.Accent:p.Text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(r,-4,-4));
    }
}

public sealed partial class MainForm
{
    private TableLayoutPanel BuildLibraryAppearance()
    {
        var form=SettingsTable();form.Name="AppearanceSettingsTable";form.ColumnStyles[0].SizeType=SizeType.AutoSize;var title=PageTitle("外观");form.Controls.Add(title,0,form.RowCount++);form.SetColumnSpan(title,2);
        var day=new ThemeChoiceButton(ApplicationThemeMode.Day);var night=new ThemeChoiceButton(ApplicationThemeMode.Night);
        foreach(var button in new[]{day,night})button.Click+=(_,_)=>
        {
            if(_themeModeBox.SelectedIndex==(int)button.Mode)return;
            _themeModeBox.SelectedIndex=(int)button.Mode;_settingsDirty=true;SaveSettings(false);
        };
        AddSettingRow(form,"主题",Stack(day,night));
        AddSettingRow(form,"字体模式",_uiFontModeBox);_uiFontSizeBox.Width=90;
        AddSettingRow(form,"字体 / 字号",new WorkspaceAlignedRow(_uiFontBox,_defaultUiFont,_uiFontSizeBox){Name="AppearanceFontRow"});AddSettingRow(form,"",_actualUiFont);
        var mode=new GamePlanBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=190};mode.Items.AddRange(["海报封面","横向封面"]);mode.SelectedIndex=_libraryPreferences.Portrait?0:1;
        mode.SelectedIndexChanged+=(_,_)=>SaveLibraryPreferences(_libraryPreferences with{Portrait=mode.SelectedIndex==0});
        AddSettingRow(form,"封面样式",mode);
        var width=new NumericUpDown{Minimum=120,Maximum=240,Increment=10,Width=90,MinimumSize=new(90,0),Value=_libraryPreferences.CoverWidth};width.ValueChanged+=(_,_)=>SaveLibraryPreferences(_libraryPreferences with{CoverWidth=(int)width.Value});
        AddSettingRow(form,"封面宽度",width);
        var advanced=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Top,ColumnCount=1,Visible=false};advanced.Controls.Add(_themeModeBox);advanced.Controls.Add(BuildCustomThemeEditor());
        Label? advancedLabel=null;var custom=WorkspaceButton("自定义主题",(_,_)=>{advanced.Visible=!advanced.Visible;if(advancedLabel is not null)advancedLabel.Visible=advanced.Visible;});AddSettingRow(form,"",Stack(custom));AddSettingRow(form,"",advanced);advancedLabel=form.GetControlFromPosition(0,form.RowCount-1) as Label;if(advancedLabel is not null)advancedLabel.Visible=false;
        var reset=WorkspaceButton("恢复默认外观",(_,_)=>{_themeModeBox.SelectedIndex=(int)ApplicationThemeMode.Day;_uiFontModeBox.SelectedIndex=0;_uiFontSizeBox.Value=10;_settingsDirty=true;SaveSettings(false);SaveLibraryPreferences(_libraryPreferences with{Portrait=true,CoverWidth=158});mode.SelectedIndex=0;width.Value=158;});
        AddSettingRow(form,"",Stack(reset));return form;
    }
    private void SaveLibraryPreferences(LibraryPreferences value)
    {
        try{LibraryPreferencesStore.Save(value);if(value.IconSource!=_libraryPreferences.IconSource||value.CoverSource!=_libraryPreferences.CoverSource)_artworkAttempted.Clear();_libraryPreferences=value;_libraryGrid.Portrait=value.Portrait;_libraryGrid.CoverWidth=value.CoverWidth;_libraryGrid.SetListMode(_libraryGrid.ListMode);ScheduleLibraryRefresh();_statusLabel.Text="游戏库设置已保存。";}
        catch(Exception ex){ShowFusionError(ex);}
    }
    private TableLayoutPanel BuildArtworkSettings()
    {
        var table=SettingsTable();var title=PageTitle("游戏库与图片");table.Controls.Add(title,0,table.RowCount++);table.SetColumnSpan(title,2);
        ComboBox Choice(ArtworkSource value){var box=new GamePlanBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=255};box.Items.AddRange(["本地自动","网上匹配，缺少时保留本地图片"]);box.SelectedIndex=value==ArtworkSource.Online?1:0;return box;}
        var icon=Choice(_libraryPreferences.IconSource);var cover=Choice(_libraryPreferences.CoverSource);
        icon.SelectedIndexChanged+=(_,_)=>SaveLibraryPreferences(_libraryPreferences with{IconSource=icon.SelectedIndex==1?ArtworkSource.Online:ArtworkSource.Automatic});
        cover.SelectedIndexChanged+=(_,_)=>SaveLibraryPreferences(_libraryPreferences with{CoverSource=cover.SelectedIndex==1?ArtworkSource.Online:ArtworkSource.Automatic});
        AddSettingRow(table,"默认图标来源",icon);AddSettingRow(table,"默认封面来源",cover);
        AddSettingRow(table,"",Info("网上匹配使用游戏名称查找 Steam 图片；名称不唯一时请手动选择。\n本地模式提取程序图标，并在游戏启动后尝试截图。手动图片不会被自动覆盖。"));
        AddSettingRow(table,"已有游戏",WorkspaceButton("补齐默认图片",async(_,_)=>{_artworkAttempted.Clear();var store=await RecentStore();await FillDefaultArtworkAsync(store.SnapshotAll().Select(x=>x.ExePath));if(!IsDisposed)ScheduleLibraryRefresh();}));
        AddSettingRow(table,"最近记录",WorkspaceButton("设置保留数量",async(_,_)=>await EditLibraryRetentionAsync()));
        var names=new CheckBox{Text="自动补齐中文名称",AutoSize=true,Checked=_libraryPreferences.AutomaticChineseNames};
        names.CheckedChanged+=(_,_)=>SaveLibraryPreferences(_libraryPreferences with{AutomaticChineseNames=names.Checked});
        AddSettingRow(table,"游戏名称",names);
        AddSettingRow(table,"",Info("使用各游戏选择的翻译方案，在后台补齐中文名，只发送游戏名称。保存后不重复翻译；已有或手动编辑的中文名会保留。\n未配置方案时保留原名。右键“标签与便签”可编辑名字或重新生成译名。"));
        AddSettingRow(table,"",WorkspaceButton("重试补齐中文名",async(_,_)=>{_libraryNameAttempts.Clear();var store=await RecentStore();await FillLibraryChineseNamesAsync(store.SnapshotAll(),true);}));return table;
    }
    private TableLayoutPanel BuildStorageSettings()
    {
        var table=SettingsTable();var title=PageTitle("存储位置");table.Controls.Add(title,0,table.RowCount++);table.SetColumnSpan(title,2);
        var path=new TextBox{ReadOnly=true,Text=AppDataPaths.Root,Width=560};AddSettingRow(table,"当前数据目录",path);
        var notice=Info("设置、游戏库、图片、截图历史、缓存及日志统一存放在这里。");
        var change=WorkspaceButton("选择数据位置",(_,_)=>{
            using var dialog=new FolderBrowserDialog{Description="选择独立的空文件夹。下次启动复制数据，原目录保留。"};
            if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            try{PortableDataStorage.RequestLocation(dialog.SelectedPath);notice.Text="已设置："+dialog.SelectedPath+"\n下次启动时复制并校验数据；当前仍使用原目录，原文件不会删除。";}
            catch(Exception ex){ShowFusionError(ex);}
        });
        AddSettingRow(table,"",Stack(WorkspaceButton("打开数据文件夹",(_,_)=>OpenFolder(AppDataPaths.Root)),change));AddSettingRow(table,"",notice);
        AddSettingRow(table,"默认位置",Info("软件文件夹内的 data。目录不可写时会报告错误，不会转存到 C 盘。"));
        AddSettingRow(table,"游戏原存档",Info("游戏自己保存的存档仍在原位置，本软件不会移动。"));return table;
    }
}
