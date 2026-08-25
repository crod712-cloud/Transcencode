namespace Transcencode.NativeWpf.Tests;

using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

using HandBrakeWPF.Converters.Picture;
using HandBrakeWPF.Model.Picture;

using Xunit;

public sealed class NativeWpfRegressionTests
{
    private static readonly Assembly ApplicationAssembly = typeof(HandBrakeWPF.ViewModels.MainViewModel).Assembly;

    [Fact]
    public void NativeTranscencodeTypesAreCompiledIntoTheHandBrakeGui()
    {
        Type[] types = ApplicationAssembly.GetTypes()
            .Where(type => type.FullName?.Contains("Transcencode", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        Assert.True(types.Length >= 10, $"Expected native Transcencode types, but found only {types.Length}.");
        Assert.Contains(types, type => type.Name.Contains("Analyze", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(types, type => type.Name.Contains("SourceTracks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(types, type => type.Name.Contains("Upscale", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(types, type => type.Name.Contains("Verify", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(types, type => type.Name.Contains("LiveEngine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NativeTranscencodeViewModelsParticipateInPropertyChangeNotification()
    {
        Type[] viewModels = ApplicationAssembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.Name.StartsWith("Transcencode", StringComparison.Ordinal))
            .Where(type => type.Name.EndsWith("ViewModel", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(viewModels);
        foreach (Type type in viewModels)
        {
            Assert.True(
                typeof(INotifyPropertyChanged).IsAssignableFrom(type),
                $"{type.FullName} does not implement INotifyPropertyChanged through the HandBrake view-model base.");
        }
    }

    [Theory]
    [InlineData(CropMode.None, "Same as source (preserve original black bars)")]
    [InlineData(CropMode.Loose, "Safe auto-crop (least aggressive)")]
    [InlineData(CropMode.Automatic, "Automatic crop (remove detected black bars)")]
    [InlineData(CropMode.Custom, "Custom crop")]
    public void CropModeLabelsExplainTheirActualBehavior(CropMode mode, string expected)
    {
        TranscencodeCropModeConverter converter = new TranscencodeCropModeConverter();
        object label = converter.Convert(mode, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, label);
        Assert.Equal(
            mode,
            converter.ConvertBack(label, typeof(CropMode), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CropModeListPlacesSameAsSourceFirst()
    {
        TranscencodeCropModeConverter converter = new TranscencodeCropModeConverter();
        BindingList<CropMode> modes = new BindingList<CropMode>
        {
            CropMode.None,
            CropMode.Loose,
            CropMode.Automatic,
            CropMode.Custom
        };

        object converted = converter.Convert(modes, typeof(IEnumerable), null, System.Globalization.CultureInfo.InvariantCulture);
        string[] labels = Assert.IsAssignableFrom<IEnumerable>(converted).Cast<object>().Select(value => value.ToString()).ToArray();

        Assert.Equal("Same as source (preserve original black bars)", labels[0]);
        Assert.Equal(4, labels.Length);
    }

    [Fact]
    public void EveryNativeTranscencodeUserControlCanBeConstructedOnAnStaThread()
    {
        Type[] viewTypes = ApplicationAssembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => typeof(UserControl).IsAssignableFrom(type))
            .Where(type => type.Name.StartsWith("Transcencode", StringComparison.Ordinal))
            .ToArray();

        Assert.True(viewTypes.Length >= 5, $"Expected at least five native Transcencode views, found {viewTypes.Length}.");

        Exception failure = null;
        Thread thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application();
                foreach (Type viewType in viewTypes)
                {
                    object instance = Activator.CreateInstance(viewType);
                    Assert.NotNull(instance);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF view construction did not finish within 30 seconds.");

        Assert.Null(failure);
    }

    [Fact]
    public void MainViewModelExposesEveryNativeTranscencodeTabViewModel()
    {
        string[] propertyNames = typeof(HandBrakeWPF.ViewModels.MainViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();

        foreach (string requiredFragment in new[] { "Analyze", "SourceTracks", "Upscale", "Verify", "LiveEngine" })
        {
            Assert.Contains(propertyNames, name => name.Contains(requiredFragment, StringComparison.OrdinalIgnoreCase));
        }
    }
}
