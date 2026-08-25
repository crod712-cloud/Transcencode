// --------------------------------------------------------------------------------------------------------------------
// Transcencode plain-language crop mode converter.
// This file is part of the Transcencode HandBrake fork and is licensed under GPL-2.0-or-later.
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrakeWPF.Converters.Picture
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.Windows.Data;

    using HandBrakeWPF.Model.Picture;

    public sealed class TranscencodeCropModeConverter : IValueConverter
    {
        private static readonly IReadOnlyDictionary<CropMode, string> Labels =
            new Dictionary<CropMode, string>
            {
                [CropMode.None] = "Same as source (preserve original black bars)",
                [CropMode.Loose] = "Safe auto-crop (least aggressive)",
                [CropMode.Automatic] = "Automatic crop (remove detected black bars)",
                [CropMode.Custom] = "Custom crop"
            };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is BindingList<CropMode> modes)
            {
                BindingList<string> labels = new BindingList<string>();
                foreach (CropMode mode in modes)
                {
                    labels.Add(GetLabel(mode));
                }

                return labels;
            }

            return value is CropMode cropMode ? GetLabel(cropMode) : null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string label = value as string;
            if (string.IsNullOrWhiteSpace(label))
            {
                return CropMode.None;
            }

            foreach (KeyValuePair<CropMode, string> item in Labels)
            {
                if (string.Equals(item.Value, label, StringComparison.Ordinal))
                {
                    return item.Key;
                }
            }

            return CropMode.None;
        }

        private static string GetLabel(CropMode mode)
        {
            return Labels.TryGetValue(mode, out string label) ? label : mode.ToString();
        }
    }
}
