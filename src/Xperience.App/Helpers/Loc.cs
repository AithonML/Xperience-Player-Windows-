using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xperience.Core.Abstractions;

namespace Xperience.App.Helpers;

/// <summary>
/// Attached properties enabling zero-restart runtime localization updates on XAML elements
/// alongside standard <c>x:Uid</c> directives.
/// </summary>
public static class Loc
{
    private static readonly List<WeakReference<FrameworkElement>> TrackedElements = [];

    /// <summary>
    /// Identifies the <c>Loc.Key</c> attached dependency property.
    /// </summary>
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    /// <summary>
    /// Identifies the <c>Loc.ToolTipKey</c> attached dependency property.
    /// </summary>
    public static readonly DependencyProperty ToolTipKeyProperty =
        DependencyProperty.RegisterAttached(
            "ToolTipKey",
            typeof(string),
            typeof(Loc),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    /// <summary>
    /// Gets the localization resource key for the specified element.
    /// </summary>
    public static string GetKey(DependencyObject obj) => (string)obj.GetValue(KeyProperty);

    /// <summary>
    /// Sets the localization resource key for the specified element.
    /// </summary>
    public static void SetKey(DependencyObject obj, string value) => obj.SetValue(KeyProperty, value);

    /// <summary>
    /// Gets the tooltip localization resource key for the specified element.
    /// </summary>
    public static string GetToolTipKey(DependencyObject obj) => (string)obj.GetValue(ToolTipKeyProperty);

    /// <summary>
    /// Sets the tooltip localization resource key for the specified element.
    /// </summary>
    public static void SetToolTipKey(DependencyObject obj, string value) => obj.SetValue(ToolTipKeyProperty, value);

    /// <summary>
    /// Refreshes all tracked XAML elements when the application language changes at runtime.
    /// </summary>
    public static void RefreshAllTrackedElements(ILocalizationService localizationService)
    {
        for (int i = TrackedElements.Count - 1; i >= 0; i--)
        {
            if (!TrackedElements[i].TryGetTarget(out var element))
            {
                int lastIdx = TrackedElements.Count - 1;
                if (i != lastIdx)
                {
                    TrackedElements[i] = TrackedElements[lastIdx];
                }
                TrackedElements.RemoveAt(lastIdx);
                continue;
            }

            ApplyLocalizationToElement(element, localizationService);
        }
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        bool alreadyTracked = false;
        for (int i = TrackedElements.Count - 1; i >= 0; i--)
        {
            if (!TrackedElements[i].TryGetTarget(out var target))
            {
                int lastIdx = TrackedElements.Count - 1;
                if (i != lastIdx)
                {
                    TrackedElements[i] = TrackedElements[lastIdx];
                }
                TrackedElements.RemoveAt(lastIdx);
                continue;
            }

            if (ReferenceEquals(target, element))
            {
                alreadyTracked = true;
            }
        }

        if (!alreadyTracked)
        {
            TrackedElements.Add(new WeakReference<FrameworkElement>(element));
        }

        if (App.Current is App app && app.Services.GetService<ILocalizationService>() is { } locService)
        {
            ApplyLocalizationToElement(element, locService);
        }
    }

    private static void ApplyLocalizationToElement(FrameworkElement element, ILocalizationService locService)
    {
        var key = GetKey(element);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var localizedText = locService.GetString(key);
            switch (element)
            {
                case TextBlock textBlock:
                    textBlock.Text = localizedText;
                    break;
                case TextBox textBox:
                    textBox.PlaceholderText = localizedText;
                    break;
                case ContentControl contentControl:
                    if (contentControl.Content is null or string)
                    {
                        contentControl.Content = localizedText;
                    }
                    break;
            }
        }

        var tooltipKey = GetToolTipKey(element);
        if (!string.IsNullOrWhiteSpace(tooltipKey))
        {
            ToolTipService.SetToolTip(element, locService.GetString(tooltipKey));
        }
    }
}
