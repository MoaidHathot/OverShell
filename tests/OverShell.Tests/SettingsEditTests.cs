using System.Text.Json.Nodes;
using OverShell.Core;
using OverShell.Core.Settings;
using Xunit;

namespace OverShell.Tests;

/// <summary>
/// The settings file is edited by the palette one value at a time; everything else in it is
/// the user's and must come back byte for byte - comments, blank lines, order, line endings.
/// </summary>
public class JsoncEditTests
{
    private const string Sample = """
        // OverShell settings.
        {
          // The view shown at start: terminal | herd | dashboard | zen.
          "view": "terminal",

          // "terminalOpacity": a comment that names the key must not be touched.
          "window": {
            "backdrop": "acrylic",   // what is behind the window
            "terminalOpacity": 1.0
          },

          "theme": "system",
          "tabs": { "twoLine": true, "showHarnessGlyph": true }
        }
        """;

    private static string Set(string text, string json, params string[] path)
    {
        var result = JsoncEdit.Set(text, path, json, out var error);
        Assert.Null(error);
        Assert.NotNull(result);
        return result!;
    }

    private static JsonNode Parse(string text)
    {
        var node = Jsonc.Parse(text, out var error);
        Assert.Null(error);
        return node!;
    }

    [Fact]
    public void Replaces_a_nested_number_in_place_and_keeps_everything_else()
    {
        var result = Set(Sample, "0.85", "window", "terminalOpacity");

        Assert.Equal(0.85, Parse(result)["window"]!["terminalOpacity"]!.GetValue<double>());
        Assert.Equal(Sample.Replace("\"terminalOpacity\": 1.0", "\"terminalOpacity\": 0.85"), result);
        Assert.Contains("// \"terminalOpacity\": a comment that names the key must not be touched.", result);
        Assert.Contains("// what is behind the window", result);
    }

    [Fact]
    public void Replaces_a_string_a_boolean_and_a_top_level_value()
    {
        var result = Set(Sample, "\"mica\"", "window", "backdrop");
        result = Set(result, "false", "tabs", "twoLine");
        result = Set(result, "\"dark\"", "theme");

        var node = Parse(result);
        Assert.Equal("mica", node["window"]!["backdrop"]!.GetValue<string>());
        Assert.False(node["tabs"]!["twoLine"]!.GetValue<bool>());
        Assert.Equal("dark", node["theme"]!.GetValue<string>());
        Assert.Contains("\"backdrop\": \"mica\",   // what is behind the window", result);
        Assert.Contains("\"tabs\": { \"twoLine\": false, \"showHarnessGlyph\": true }", result);
    }

    [Fact]
    public void Adds_a_missing_member_on_its_own_line_indented_like_its_siblings()
    {
        var result = Set(Sample, "\"#3DD68C\"", "accent");

        Assert.Equal("#3DD68C", Parse(result)["accent"]!.GetValue<string>());
        // After the last member of the object, which gets the comma it lacked; the brace stays where it was.
        Assert.Contains("\"tabs\": { \"twoLine\": true, \"showHarnessGlyph\": true },\n  \"accent\": \"#3DD68C\"\n}", result);
    }

    [Fact]
    public void Adds_a_missing_member_to_an_inline_object()
    {
        var result = Set(Sample, "true", "tabs", "compact");

        Assert.True(Parse(result)["tabs"]!["compact"]!.GetValue<bool>());
        Assert.Contains("\"tabs\": { \"twoLine\": true, \"showHarnessGlyph\": true, \"compact\": true }", result);
    }

    [Fact]
    public void Creates_the_objects_on_the_way_to_a_deep_key()
    {
        var result = Set(Sample, "true", "notifications", "sinks", "toast", "enabled");

        Assert.True(Parse(result)["notifications"]!["sinks"]!["toast"]!["enabled"]!.GetValue<bool>());
        Assert.Contains("\n  \"notifications\": {\n    \"sinks\": {\n      \"toast\": {\n        \"enabled\": true\n      }\n    }\n  }\n}", result);
        Assert.StartsWith("// OverShell settings.\n{", result);
    }

    [Fact]
    public void A_comment_after_the_last_member_stays_with_it()
    {
        const string text = "{\n  \"a\": 1 // one\n}\n";
        var result = Set(text, "2", "b");
        Assert.Equal("{\n  \"a\": 1, // one\n  \"b\": 2\n}\n", result);
    }

    [Fact]
    public void Keeps_the_trailing_comma_style_and_crlf_line_endings()
    {
        const string text = "{\r\n  \"a\": 1,\r\n}\r\n";
        var result = Set(text, "\"x\"", "b");
        Assert.Equal("{\r\n  \"a\": 1,\r\n  \"b\": \"x\",\r\n}\r\n", result);
    }

    [Fact]
    public void An_empty_object_grows_into_the_multi_line_shape()
    {
        var result = Set("{ }", "0.5", "window", "terminalOpacity");
        Assert.Equal("{\n  \"window\": {\n    \"terminalOpacity\": 0.5\n  }\n}", result);
        Assert.Equal(0.5, Parse(result)["window"]!["terminalOpacity"]!.GetValue<double>());
    }

    [Fact]
    public void A_file_of_only_comments_gets_an_object_after_them()
    {
        var result = Set("// nothing yet\n", "\"light\"", "theme");
        Assert.Equal("// nothing yet\n{\n  \"theme\": \"light\"\n}\n", result);
    }

    [Fact]
    public void A_scalar_where_an_object_is_needed_is_replaced_by_the_object()
    {
        const string text = "{\n  \"window\": null\n}";
        var result = Set(text, "0.7", "window", "terminalOpacity");
        Assert.Equal("{\n  \"window\": { \"terminalOpacity\": 0.7 }\n}", result);
    }

    [Fact]
    public void Matches_keys_the_way_the_reader_does_and_skips_the_same_key_in_another_object()
    {
        const string text = "{\n  \"views\": { \"terminal\": { \"layout\": \"top\" } },\n  \"Window\": { \"TerminalOpacity\": 1 },\n  \"layout\": \"x\"\n}";
        var result = Set(text, "0.9", "window", "terminalOpacity");
        Assert.Equal("{\n  \"views\": { \"terminal\": { \"layout\": \"top\" } },\n  \"Window\": { \"TerminalOpacity\": 0.9 },\n  \"layout\": \"x\"\n}", result);

        var sibling = Set(text, "\"herd\"", "views", "terminal", "layout");
        Assert.Contains("\"terminal\": { \"layout\": \"herd\" }", sibling);
        Assert.Contains("\"layout\": \"x\"", sibling);
    }

    [Fact]
    public void Braces_inside_strings_and_block_comments_do_not_confuse_the_scan()
    {
        const string text = "{\n  /* { \"theme\": \"nope\" } */\n  \"accent\": \"#12}{34\",\n  \"theme\": \"system\"\n}";
        var result = Set(text, "\"dark\"", "theme");
        Assert.Contains("/* { \"theme\": \"nope\" } */", result);
        Assert.Contains("\"accent\": \"#12}{34\"", result);
        Assert.Equal("dark", Parse(result)["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Refuses_text_that_is_not_an_object_and_an_edit_that_would_not_read_back()
    {
        Assert.Null(JsoncEdit.Set("[1, 2]", ["a"], "1", out var error));
        Assert.Contains("object", error);

        Assert.Null(JsoncEdit.Set("{ \"a\": 1", ["b"], "1", out error));
        Assert.NotNull(error);

        Assert.Null(JsoncEdit.Set("{ \"a\": 1 }", ["a"], "not json", out error));
        Assert.Contains("unreadable", error);
    }

    [Fact]
    public void ToJson_writes_the_forms_the_file_uses()
    {
        Assert.Equal("0.85", JsoncEdit.ToJson(0.85));
        Assert.Equal("1.0", JsoncEdit.ToJson(1.0));
        Assert.Equal("3000", JsoncEdit.ToJson(3000));
        Assert.Equal("true", JsoncEdit.ToJson(true));
        Assert.Equal("null", JsoncEdit.ToJson(null));
        Assert.Equal("\"win+backtick\"", JsoncEdit.ToJson("win+backtick"));
    }
}

public class SettingsCatalogTests
{
    [Fact]
    public void Every_knob_has_a_distinct_path_a_description_and_values_that_parse()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var knob in SettingsCatalog.Knobs)
        {
            Assert.True(paths.Add(knob.Path), $"duplicate knob {knob.Path}");
            Assert.False(string.IsNullOrWhiteSpace(knob.Title));
            Assert.False(string.IsNullOrWhiteSpace(knob.Description));
            Assert.NotEmpty(knob.Choices);
            foreach (var choice in knob.Choices)
            {
                var text = JsoncEdit.Set("{ }", knob.Segments, choice.Json, out var error);
                Assert.True(text is not null, $"{knob.Path} = {choice.Json}: {error}");
            }
        }
    }

    [Fact]
    public void Every_knob_names_a_key_the_default_settings_know_or_an_extension_section()
    {
        var defaults = Jsonc.Parse(EmbeddedResources.Read("settings.jsonc"), out _)!;
        foreach (var knob in SettingsCatalog.Knobs)
        {
            if (knob.Segments[0] == "extensions")
            {
                // Extension sections are absent from the defaults by design (each extension owns its shape).
                Assert.Equal(3, knob.Segments.Length);
                continue;
            }

            JsonNode? node = defaults;
            for (var i = 0; i < knob.Segments.Length; i++)
            {
                var segment = knob.Segments[i];
                // A sink's "enabled" is implied (true) where the defaults leave it out; every other key is spelled out.
                var implied = i == knob.Segments.Length - 1 && segment == "enabled" && knob.Segments[0] == "notifications";
                Assert.True(node is JsonObject o && (o.ContainsKey(segment) || implied), $"{knob.Path}: '{segment}' is not in the default settings.jsonc");
                node = ((JsonObject)node!)[segment];
            }
        }
    }

    [Fact]
    public void Typed_values_are_judged_per_kind()
    {
        var opacity = SettingsCatalog.Find("window.terminalOpacity")!;
        Assert.Equal("0.7", opacity.JsonForTyped(" 0.7 "));
        Assert.Equal("1.0", opacity.JsonForTyped("1"));
        Assert.Null(opacity.JsonForTyped("1.5"));
        Assert.Null(opacity.JsonForTyped("abc"));

        var timeout = SettingsCatalog.Find("keys.sequenceTimeoutMs")!;
        Assert.Equal("4000", timeout.JsonForTyped("4000"));
        Assert.Null(timeout.JsonForTyped("10"));

        var accent = SettingsCatalog.Find("accent")!;
        Assert.Equal("\"#3DD68C\"", accent.JsonForTyped("#3dd68c"));
        Assert.Null(accent.JsonForTyped("red"));

        var summon = SettingsCatalog.Find("extensions.summon.keys")!;
        Assert.Equal("\"ctrl+alt+f9\"", summon.JsonForTyped("Ctrl+Alt+F9"));
        Assert.Null(summon.JsonForTyped("ctrl+"));
        Assert.Equal(["extensions", "summon", "keys"], summon.Segments);

        var leader = SettingsCatalog.Find("extensions.herd.mode.leader")!;
        Assert.Equal(["extensions", "herd.mode", "leader"], leader.Segments);

        Assert.Null(SettingsCatalog.Find("theme")!.JsonForTyped("dark"));
    }

    [Fact]
    public void Display_strips_quotes_and_names_null()
    {
        Assert.Equal("mica", SettingKnob.Display("\"mica\""));
        Assert.Equal("0.85", SettingKnob.Display("0.85"));
        Assert.Equal("1.0", SettingKnob.Display("1.0"));
        Assert.Equal("true", SettingKnob.Display("true"));
        Assert.Equal("none", SettingKnob.Display("null"));
        Assert.Equal("none", SettingKnob.Display((JsonNode?)null));
    }

    [Fact]
    public void A_snapshot_reads_the_user_layer_over_the_defaults()
    {
        var defaults = Jsonc.Parse(EmbeddedResources.Read("settings.jsonc"), out _);
        var user = Jsonc.Parse("{ \"window\": { \"terminalOpacity\": 0.6 }, \"skin\": null }", out _);
        var snapshot = new SettingsSnapshot(Jsonc.Merge(defaults, user), user);

        Assert.Equal("0.6", SettingKnob.Display(snapshot.Current(["window", "terminalOpacity"])));
        Assert.True(snapshot.IsUserSet(["window", "terminalOpacity"]));
        Assert.Equal("acrylic", SettingKnob.Display(snapshot.Current(["window", "backdrop"])));
        Assert.False(snapshot.IsUserSet(["window", "backdrop"]));
        Assert.True(snapshot.IsUserSet(["skin"]), "an explicit null is the user's choice too");
        Assert.Null(snapshot.Current(["no", "such", "key"]));
    }

    [Fact]
    public void The_default_opacity_is_translucent_and_the_embedded_file_agrees()
    {
        var defaults = Jsonc.Parse(EmbeddedResources.Read("settings.jsonc"), out _)!;
        Assert.Equal(WindowSettings.DefaultTerminalOpacity, defaults["window"]!["terminalOpacity"]!.GetValue<double>());
        Assert.True(WindowSettings.DefaultTerminalOpacity < 1.0);
        Assert.Equal(WindowSettings.DefaultTerminalOpacity, AppSettings.LoadDefaults().Window.TerminalOpacity);
    }
}
