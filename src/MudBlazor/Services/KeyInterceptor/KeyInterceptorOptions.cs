// Copyright (c) MudBlazor 2021
// MudBlazor licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace MudBlazor.Services;

/// <summary>
/// Settings for wiring keyboard interception to specific elements and key bindings.
/// </summary>
/// <remarks>
/// These options are passed when subscribing to the key interceptor so the JS hook knows which element to observe and which keys to pre-register for performance.
/// </remarks>
public sealed class KeyInterceptorOptions
{
    /// <summary>
    /// The CSS class of the target HTML element that should be observed for keyboard events or <see langword="null" />
    /// to observe the element for which the subscription is created.
    /// </summary>
    /// <remarks>
    /// Note: This must be a single class name.
    /// </remarks>
    public string? TargetClass { get; init; }

    /// <summary>
    /// Handles only keyboard events raised by the subscribed element itself, ignoring events which bubble up from its descendants.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>. Set this for a focusable container which manages a virtual cursor (for example through <c>aria-activedescendant</c>),
    /// so that an input or button rendered inside the container keeps its own keyboard behavior when it has focus.
    /// Ignored when <see cref="TargetClass"/> is set.
    /// </remarks>
    public bool IgnoreDescendantEvents { get; init; }

    /// <summary>
    /// Specifies whether resize events should be logged in the browser's console.
    /// </summary>
    public bool EnableLogging { get; init; }

    /// <summary>
    /// A list of key options that define the keys to intercept and their respective configurations.
    /// </summary>
    public IReadOnlyList<KeyOptions> Keys { get; init; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyInterceptorOptions"/> class.
    /// </summary>
    public KeyInterceptorOptions()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyInterceptorOptions"/> class with the specified key options.
    /// </summary>
    /// <param name="keys">The key options to intercept.</param>
    public KeyInterceptorOptions(params KeyOptions[] keys)
        : this(null, keys)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyInterceptorOptions"/> class with the specified target class and key options.
    /// </summary>
    /// <param name="targetClass">The CSS class of the target HTML element.</param>
    /// <param name="keys">The key options to intercept.</param>
    public KeyInterceptorOptions(string? targetClass, params KeyOptions[] keys)
    {
        TargetClass = targetClass;
        Keys = keys;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyInterceptorOptions"/> class with the specified target class, key options, and logging option.
    /// </summary>
    /// <param name="targetClass">The CSS class of the target HTML element.</param>
    /// <param name="keys">The key options to intercept.</param>
    /// <param name="enableLogging">Specifies whether resize events should be logged in the browser's console.</param>
    public KeyInterceptorOptions(string? targetClass, bool enableLogging = false, params KeyOptions[] keys)
        : this(targetClass, keys)
    {
        EnableLogging = enableLogging;
    }
}
