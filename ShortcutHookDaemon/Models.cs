using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShortcutHookDaemon;

public sealed class BindingEntry
{
    [JsonPropertyName("trigger")]
    public string? trigger { get; set; }

    [JsonPropertyName("output")]
    public string? output { get; set; }

    [JsonPropertyName("outputs")]
    public List<string>? outputs { get; set; }

    [JsonPropertyName("outputDelay")]
    public int outputDelay { get; set; } = 0;

    [JsonPropertyName("app")]
    public string? app { get; set; }

    [JsonPropertyName("apps")]
    public List<string>? apps { get; set; }

    [JsonPropertyName("exceptApps")]
    public List<string>? exceptApps { get; set; }

    [JsonPropertyName("enabled")]
    public bool? enabled { get; set; }

    [JsonPropertyName("debounce")]
    public bool debounce { get; set; } = false;

    [JsonPropertyName("showToast")]
    public bool showToast { get; set; } = false;

    [JsonPropertyName("label")]
    public string? label { get; set; }
}

public sealed class ConfigRoot
{
    [JsonPropertyName("altHScroll")]
    public bool altHScroll { get; set; } = false;

    [JsonPropertyName("activeProfile")]
    public string? activeProfile { get; set; }

    [JsonPropertyName("profiles")]
    public List<ProfileEntry>? profiles { get; set; }

    [JsonPropertyName("ignoredApps")]
    public List<string>? ignoredApps { get; set; }

    [JsonPropertyName("bindings")]
    public List<BindingEntry>? bindings { get; set; }
}

public sealed class ProfileEntry
{
    [JsonPropertyName("name")]
    public string? name { get; set; }

    [JsonPropertyName("bindings")]
    public List<BindingEntry>? bindings { get; set; }
}
