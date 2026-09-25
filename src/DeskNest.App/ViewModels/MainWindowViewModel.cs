using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.App.Themes;

namespace DeskNest.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public LocalizationManager Localizer => LocalizationManager.Instance;
    public ThemeManager ThemeMgr => ThemeManager.Instance;

    public IReadOnlyList<LanguageInfo> SupportedLanguages => LocalizationManager.SupportedLanguages;
    public IReadOnlyList<AppThemeMode> SupportedThemes { get; } = new[]
    {
        AppThemeMode.Dark,
        AppThemeMode.Light,
        AppThemeMode.System
    };

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    public bool IsOverviewTab => SelectedTabIndex == 0;
    public bool IsSpacesTab => SelectedTabIndex == 1;
    public bool IsProbeTab => SelectedTabIndex == 2;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsSpacesTab));
        OnPropertyChanged(nameof(IsProbeTab));
    }

    [ObservableProperty]
    private LanguageInfo _selectedLanguage;

    [ObservableProperty]
    private AppThemeMode _selectedTheme;

    [ObservableProperty]
    private SpacePreviewItemViewModel? _selectedSpace;

    public ObservableCollection<SpacePreviewItemViewModel> RepresentativeSpaces { get; } = new();

    public SystemProbeViewModel Probe { get; }

    public FlowDirection CurrentFlowDirection => Localizer.FlowDirectionValue;

    public MainWindowViewModel()
    {
        _selectedLanguage = Localizer.CurrentLanguageInfo;
        _selectedTheme = ThemeMgr.CurrentThemeMode;

        Probe = new SystemProbeViewModel();

        // Populate representative spaces (clean structural direction, no mock data deceit)
        var officeSpace = new SpacePreviewItemViewModel(
            id: "sp-office",
            name: "办公空间 / Office",
            description: "商务合同、财务报表、汇报胶片与参考归档文件",
            mode: SpaceMode.Managed,
            physicalPath: "~/DeskNest/Spaces/Office",
            itemCount: 128,
            status: "Spaces.BadgeReady",
            boundsSummary: "收纳受控于 ~/DeskNest/Spaces/Office；具备避重重命名、事务快照与撤销日志保障。",
            rulesSummary: "【P1 结构方向】文件操作受 Core 唯一执行器管控；P1 阶段不挂接真实移动。"
        );

        var devSpace = new SpacePreviewItemViewModel(
            id: "sp-dev",
            name: "研发归档 / Dev",
            description: "源码工程代码、架构技术文档、构建制品与诊断日志",
            mode: SpaceMode.Managed,
            physicalPath: "~/DeskNest/Spaces/Dev",
            itemCount: 342,
            status: "Spaces.BadgeReady",
            boundsSummary: "收纳受控于 ~/DeskNest/Spaces/Dev；目录整体移动保证原子性，禁止拆散代码库。",
            rulesSummary: "【P1 结构方向】文件操作受 Core 唯一执行器管控；P1 阶段不挂接真实移动。"
        );

        var creativeSpace = new SpacePreviewItemViewModel(
            id: "sp-creative",
            name: "创意素材 / Creative",
            description: "设计原稿、高清图像、音视频剪辑片段与字体素材",
            mode: SpaceMode.Mapped,
            physicalPath: "D:/CreativeProjects/Assets",
            itemCount: 84,
            status: "Spaces.BadgeReadOnly",
            boundsSummary: "映射既有外部目录；保留原有物理路径，绝不搬动或删除用户原文件。",
            rulesSummary: "【P1 结构方向】映射空间仅供结构化视窗呈现，解除映射永不删源。"
        );

        var scratchpadSpace = new SpacePreviewItemViewModel(
            id: "sp-scratchpad",
            name: "随手暂存 / Scratchpad",
            description: "临时草稿、待分类素材与桌面临时下载文件",
            mode: SpaceMode.Managed,
            physicalPath: "~/DeskNest/Spaces/Scratchpad",
            itemCount: 15,
            status: "Spaces.BadgeReady",
            boundsSummary: "收纳受控于 ~/DeskNest/Spaces/Scratchpad；支持一键撤销与重定位。",
            rulesSummary: "【P1 结构方向】文件操作受 Core 唯一执行器管控；P1 阶段不挂接真实移动。"
        );

        RepresentativeSpaces.Add(officeSpace);
        RepresentativeSpaces.Add(devSpace);
        RepresentativeSpaces.Add(creativeSpace);
        RepresentativeSpaces.Add(scratchpadSpace);

        _selectedSpace = officeSpace;
        officeSpace.IsSelected = true;

        // Subscribe to localization changes to refresh flow direction and bindings
        Localizer.LanguageChanged += OnLanguageChanged;
        Localizer.PropertyChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(CurrentFlowDirection));
            OnPropertyChanged(nameof(Localizer));
        };

        ThemeMgr.ThemeChanged += (s, mode) =>
        {
            _selectedTheme = mode;
            OnPropertyChanged(nameof(SelectedTheme));
        };
    }

    partial void OnSelectedLanguageChanged(LanguageInfo value)
    {
        if (value != null && !Localizer.CurrentLanguage.Equals(value.Code, StringComparison.OrdinalIgnoreCase))
        {
            Localizer.CurrentLanguage = value.Code;
        }
    }

    partial void OnSelectedThemeChanged(AppThemeMode value)
    {
        if (ThemeMgr.CurrentThemeMode != value)
        {
            ThemeMgr.CurrentThemeMode = value;
        }
    }

    private void OnLanguageChanged(object? sender, string langCode)
    {
        foreach (var lang in SupportedLanguages)
        {
            if (lang.Code.Equals(langCode, StringComparison.OrdinalIgnoreCase))
            {
                SelectedLanguage = lang;
                break;
            }
        }
        OnPropertyChanged(nameof(CurrentFlowDirection));
    }

    [RelayCommand]
    public void SelectSpace(SpacePreviewItemViewModel? space)
    {
        if (space == null)
            return;

        foreach (var s in RepresentativeSpaces)
        {
            s.IsSelected = (s == space);
        }
        SelectedSpace = space;
    }

    [RelayCommand]
    public void SelectTab(int index)
    {
        SelectedTabIndex = index;
    }
}
