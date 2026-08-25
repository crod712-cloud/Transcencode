[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$OverlayRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.File]::ReadAllText($Path)
}

function Write-TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($true))
}

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $first = $Text.IndexOf($Old, [System.StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Hardening patch anchor not found: $Description"
    }

    $second = $Text.IndexOf($Old, $first + $Old.Length, [System.StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "Hardening patch anchor was not unique: $Description"
    }

    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$source = (Resolve-Path $SourceRoot).Path
$overlay = (Resolve-Path $OverlayRoot).Path

$required = @(
    'win/CS/HandBrakeWPF/Views/PictureSettingsView.xaml',
    'win/CS/HandBrakeWPF/Views/VideoView.xaml',
    'win/CS/HandBrakeWPF/Views/ShellView.xaml',
    'win/CS/HandBrakeWPF/Views/ShellView.xaml.cs'
)
foreach ($relative in $required) {
    $fullPath = Join-Path $source $relative
    if (-not (Test-Path $fullPath)) {
        throw "Required HandBrake WPF file was not found: $relative"
    }
}

# Copy additive source files.
Get-ChildItem $overlay -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($overlay.Length).TrimStart([char[]]'\/')
    $destination = Join-Path $source $relative
    $destinationDirectory = Split-Path $destination -Parent
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item $_.FullName $destination -Force
}

# Replace HandBrake's terse crop labels with explicit black-bar behavior.
$picturePath = Join-Path $source 'win/CS/HandBrakeWPF/Views/PictureSettingsView.xaml'
$picture = Read-TextFile $picturePath
$picture = Replace-ExactlyOnce `
    -Text $picture `
    -Old '<picture:CropModeConverter x:Key="cropModeConverter" />' `
    -New '<picture:TranscencodeCropModeConverter x:Key="cropModeConverter" />' `
    -Description 'PictureSettingsView crop converter resource'
$picture = $picture.Replace(
    'Width="110" ItemsSource="{Binding CropModes, Converter={StaticResource cropModeConverter}}"',
    'MinWidth="300" ItemsSource="{Binding CropModes, Converter={StaticResource cropModeConverter}}"')
$picture = $picture.Replace(
    'ToolTip="{x:Static Properties:ResourcesTooltips.PictureSettingsView_AutoCrop}"',
    'ToolTip="Same as source keeps the full frame and preserves the original black bars. Safe auto-crop removes only consistently detected borders. Automatic crop may remove more. Custom lets you enter exact values."')
Write-TextFile $picturePath $picture

# Explain quality behavior directly where the quality slider is used.
$videoPath = Join-Path $source 'win/CS/HandBrakeWPF/Views/VideoView.xaml'
$video = Read-TextFile $videoPath
$qualityAnchor = '                <Grid Margin="20,0,14,15" HorizontalAlignment="Stretch" Visibility="{Binding IsQualityAdjustmentSupported, Converter={StaticResource boolToVisConverter}}">'
$qualityHelp = @'
                <Border Margin="20,0,10,10" Padding="8" BorderBrush="{DynamicResource ControlBorderBrush}" BorderThickness="1" CornerRadius="3"
                        Visibility="{Binding IsQualityAdjustmentSupported, Converter={StaticResource boolToVisConverter}}">
                    <TextBlock TextWrapping="Wrap">
                        <Run FontWeight="Bold" Text="How quality works: " />
                        <Run Text="Moving the slider right increases quality and file size. The displayed CQ/RF number usually becomes lower. NVIDIA NVENC constant quality is not automatically lossless. Use Analyze for a content-based recommendation." />
                    </TextBlock>
                </Border>

'@
$video = Replace-ExactlyOnce -Text $video -Old $qualityAnchor -New ($qualityHelp + $qualityAnchor) -Description 'VideoView quality legend'
Write-TextFile $videoPath $video

# Add whole-interface scaling to the native shell. The content is placed inside a scroll viewer so enlarged layouts remain reachable.
$shellPath = Join-Path $source 'win/CS/HandBrakeWPF/Views/ShellView.xaml'
$shell = Read-TextFile $shellPath
$shell = Replace-ExactlyOnce `
    -Text $shell `
    -Old '        Name="shellView">' `
    -New '        Name="shellView" Loaded="ShellView_TranscencodeLoaded">' `
    -Description 'ShellView Loaded event'

$oldRoot = @'
    <Grid>
        <views:MainView x:Name="MainViewModel" DataContext="{Binding MainViewModel}"  
                        Panel.ZIndex="0"  Visibility="{Binding DataContext.ShowMainWindow, ElementName=shellView, Converter={StaticResource boolToVisConverter}, ConverterParameter=false}"
                        IsEnabled="{Binding DataContext.IsMainPanelEnabled, ElementName=shellView}" />

        <views:OptionsView x:Name="OptionsViewModel" DataContext="{Binding OptionsViewModel}"  
                           Panel.ZIndex="0"  Visibility="{Binding DataContext.ShowOptions, ElementName=shellView, Converter={StaticResource boolToVisConverter}, ConverterParameter=false}"
                           IsEnabled="{Binding DataContext.IsMainPanelEnabled, ElementName=shellView}" />
    </Grid>
'@
$newRoot = @'
    <Grid>
        <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
            <Grid x:Name="TranscencodeScaleRoot" Width="1040" Height="650" HorizontalAlignment="Left" VerticalAlignment="Top">
                <Grid.LayoutTransform>
                    <ScaleTransform x:Name="TranscencodeScaleTransform" ScaleX="1" ScaleY="1" />
                </Grid.LayoutTransform>

                <views:MainView x:Name="MainViewModel" DataContext="{Binding MainViewModel}"
                                Panel.ZIndex="0" Visibility="{Binding DataContext.ShowMainWindow, ElementName=shellView, Converter={StaticResource boolToVisConverter}, ConverterParameter=false}"
                                IsEnabled="{Binding DataContext.IsMainPanelEnabled, ElementName=shellView}" />

                <views:OptionsView x:Name="OptionsViewModel" DataContext="{Binding OptionsViewModel}"
                                   Panel.ZIndex="0" Visibility="{Binding DataContext.ShowOptions, ElementName=shellView, Converter={StaticResource boolToVisConverter}, ConverterParameter=false}"
                                   IsEnabled="{Binding DataContext.IsMainPanelEnabled, ElementName=shellView}" />
            </Grid>
        </ScrollViewer>

        <Border HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,6,22,0" Padding="8,4"
                Panel.ZIndex="100" Background="{DynamicResource WindowBackgroundBrush}" BorderBrush="{DynamicResource ControlBorderBrush}" BorderThickness="1" CornerRadius="3">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="Interface size" VerticalAlignment="Center" Margin="0,0,8,0" FontWeight="SemiBold" />
                <ComboBox x:Name="TranscencodeScalePicker" Width="82" SelectionChanged="TranscencodeScalePicker_OnSelectionChanged">
                    <ComboBoxItem Content="100%" Tag="1.0" IsSelected="True" />
                    <ComboBoxItem Content="110%" Tag="1.1" />
                    <ComboBoxItem Content="125%" Tag="1.25" />
                    <ComboBoxItem Content="150%" Tag="1.5" />
                    <ComboBoxItem Content="175%" Tag="1.75" />
                    <ComboBoxItem Content="200%" Tag="2.0" />
                </ComboBox>
            </StackPanel>
        </Border>
    </Grid>
'@
$shell = Replace-ExactlyOnce -Text $shell -Old $oldRoot -New $newRoot -Description 'ShellView root content'
Write-TextFile $shellPath $shell

$shellCodePath = Join-Path $source 'win/CS/HandBrakeWPF/Views/ShellView.xaml.cs'
$shellCode = Read-TextFile $shellCodePath
$methods = @'

        private void ShellView_TranscencodeLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            double savedScale = 1.0;
            try
            {
                string settingsDirectory = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "Transcencode");
                string settingsPath = System.IO.Path.Combine(settingsDirectory, "interface-scale.txt");
                if (System.IO.File.Exists(settingsPath))
                {
                    double.TryParse(
                        System.IO.File.ReadAllText(settingsPath),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out savedScale);
                }
            }
            catch
            {
                savedScale = 1.0;
            }

            if (savedScale < 1.0 || savedScale > 2.0)
            {
                savedScale = 1.0;
            }

            foreach (object item in this.TranscencodeScalePicker.Items)
            {
                if (item is System.Windows.Controls.ComboBoxItem choice &&
                    double.TryParse(choice.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) &&
                    System.Math.Abs(factor - savedScale) < 0.001)
                {
                    this.TranscencodeScalePicker.SelectedItem = choice;
                    break;
                }
            }

            this.ApplyTranscencodeScale(savedScale, false);
        }

        private void TranscencodeScalePicker_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!this.IsLoaded || this.TranscencodeScalePicker.SelectedItem is not System.Windows.Controls.ComboBoxItem choice)
            {
                return;
            }

            if (double.TryParse(
                choice.Tag?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double factor))
            {
                this.ApplyTranscencodeScale(factor, true);
            }
        }

        private void ApplyTranscencodeScale(double factor, bool save)
        {
            factor = System.Math.Max(1.0, System.Math.Min(2.0, factor));
            this.TranscencodeScaleTransform.ScaleX = factor;
            this.TranscencodeScaleTransform.ScaleY = factor;

            if (!save)
            {
                return;
            }

            try
            {
                string settingsDirectory = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "Transcencode");
                System.IO.Directory.CreateDirectory(settingsDirectory);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(settingsDirectory, "interface-scale.txt"),
                    factor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                // Scaling remains active for this session even if the preference cannot be persisted.
            }
        }
'@

$closingPattern = '(?s)(\r?\n    }\r?\n}\s*)$'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($shellCode, $closingPattern)) {
    throw 'Could not find the ShellView class closing braces.'
}
$shellCode = [System.Text.RegularExpressions.Regex]::Replace(
    $shellCode,
    $closingPattern,
    ($methods + "`r`n    }`r`n}"),
    1)
Write-TextFile $shellCodePath $shellCode

Write-Host 'Transcencode hardening patch applied: intuitive crop labels, quality guidance, and whole-interface scaling.'
