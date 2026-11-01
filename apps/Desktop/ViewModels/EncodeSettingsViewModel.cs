using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using deepduplicates;

namespace DuplicateVideoRemover.Desktop.ViewModels;

/// <summary>The re-encoding settings in the Settings screen: only common choices, each with a sensible default.</summary>
public partial class EncodeSettingsViewModel : ObservableObject
{
    private readonly Action changed;
    private bool loading = true;

    public CodecOption[] Codecs { get; } = EncodeOptions.Codecs;
    public ResolutionOption[] Resolutions { get; } = EncodeOptions.Resolutions;
    public ChoiceOption[] Presets { get; } = EncodeOptions.Presets;
    public ChoiceOption[] AudioChoices { get; } = EncodeOptions.AudioChoices;
    public ChoiceOption[] Containers { get; } = EncodeOptions.Containers;
    public ObservableCollection<SkipItem> SkipItems { get; } = new();

    [ObservableProperty] private CodecOption codec;
    [ObservableProperty] private ResolutionOption resolution;
    [ObservableProperty] private double crf;
    [ObservableProperty] private ChoiceOption preset;
    [ObservableProperty] private ChoiceOption audio;
    [ObservableProperty] private ChoiceOption container;
    [ObservableProperty] private bool appleCompatible;
    [ObservableProperty] private bool neverOverwrite;
    [ObservableProperty] private string fileSuffix = "";
    [ObservableProperty] private decimal minDuration;
    [ObservableProperty] private string extraFilter = "";

    public int CrfValue => (int)Math.Round(Crf);
    public string CrfText => $"CRF {CrfValue}";
    public string QualityLabel => EncodeOptions.QualityLabel(Codec.Id, CrfValue);
    public bool IsRecommended => CrfValue == Codec.RecommendedCrf;
    public string RecommendedText => $"Use the recommended setting ({Codec.RecommendedCrf})";
    public string QualityHelp => $"Further right gives better quality and bigger files. Recommended for {Codec.Label}: {Codec.RecommendedCrf}.";
    public bool ShowApple => Codec.Id == "x265" && Container.Id == "mp4";
    public string SuffixPlaceholder => $"_{Resolution.Height}p_{Codec.Id}";
    public string ExampleName => $"e.g. Holiday{ToSettings().EffectiveSuffix}.{Container.Id}";
    public string ExampleCommand => EncodePlanner.ExampleCommand(ToSettings(), OperatingSystem.IsWindows() ? @"D:\Videos\Holiday.mp4" : "/videos/Holiday.mp4");

    public EncodeSettingsViewModel(EncodeSettings e, Action changed)
    {
        this.changed = changed;
        e = e.Clone();
        e.Normalize();
        codec = EncodeOptions.Codec(e.codec);
        resolution = Resolutions.First(r => r.Height == e.resolution);
        crf = e.crf ?? codec.RecommendedCrf;
        preset = Presets.First(p => p.Id == e.preset);
        audio = AudioChoices.First(a => a.Id == e.audio);
        container = Containers.First(c => c.Id == e.container);
        appleCompatible = e.appleCompatible;
        neverOverwrite = e.neverOverwrite;
        fileSuffix = e.fileSuffix;
        minDuration = e.minDurationSeconds;
        extraFilter = e.extraFilter;
        foreach (var ext in FileHandler.VideoExtensions)
            SkipItems.Add(new SkipItem(ext, e.skipExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase), Changed));
        loading = false;
    }

    public EncodeSettings ToSettings()
    {
        var e = new EncodeSettings
        {
            codec = Codec.Id,
            resolution = Resolution.Height,
            crf = CrfValue,
            preset = Preset.Id,
            audio = Audio.Id,
            container = Container.Id,
            appleCompatible = AppleCompatible,
            neverOverwrite = NeverOverwrite,
            fileSuffix = FileSuffix ?? "",
            minDurationSeconds = (int)MinDuration,
            extraFilter = ExtraFilter ?? "",
            skipExtensions = SkipItems.Where(s => s.IsChecked).Select(s => s.Extension).ToArray()
        };
        e.Normalize();
        return e;
    }

    // Switching format: stay on the recommended quality if it was used, else keep the value within range
    partial void OnCodecChanged(CodecOption? oldValue, CodecOption newValue)
    {
        if (!loading && oldValue != null)
            Crf = CrfValue == oldValue.RecommendedCrf ? newValue.RecommendedCrf : Math.Clamp(CrfValue, newValue.MinCrf, newValue.MaxCrf);
        OnPropertyChanged(nameof(RecommendedText));
        OnPropertyChanged(nameof(QualityHelp));
        Changed();
    }

    partial void OnCrfChanged(double value) => Changed();
    partial void OnResolutionChanged(ResolutionOption value) => Changed();
    partial void OnPresetChanged(ChoiceOption value) => Changed();
    partial void OnAudioChanged(ChoiceOption value) => Changed();
    partial void OnContainerChanged(ChoiceOption value) => Changed();
    partial void OnAppleCompatibleChanged(bool value) => Changed();
    partial void OnNeverOverwriteChanged(bool value) => Changed();
    partial void OnFileSuffixChanged(string value) => Changed();
    partial void OnMinDurationChanged(decimal value) => Changed();
    partial void OnExtraFilterChanged(string value) => Changed();

    [RelayCommand] private void UseRecommended() => Crf = Codec.RecommendedCrf;

    private void Changed()
    {
        foreach (var name in new[] { nameof(CrfValue), nameof(CrfText), nameof(QualityLabel), nameof(IsRecommended), nameof(ShowApple),
                                     nameof(SuffixPlaceholder), nameof(ExampleName), nameof(ExampleCommand) })
            OnPropertyChanged(name);
        if (!loading) changed();
    }
}

public partial class SkipItem : ObservableObject
{
    private readonly Action changed;
    public string Extension { get; }
    [ObservableProperty] private bool isChecked;
    public SkipItem(string extension, bool isChecked, Action changed) { Extension = extension; this.isChecked = isChecked; this.changed = changed; }
    partial void OnIsCheckedChanged(bool value) => changed();
}
