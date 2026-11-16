using System.Text.Json;
using CorelMcp.Worker.Operations;
using Xunit;

namespace CorelMcp.Worker.Tests;

public sealed class DrawingContractsTests
{
    public static TheoryData<string, string> Valid => new()
    {
        { "corel_import", "\"path\":\"C:\\\\input.svg\",\"format\":\"svg\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size_bytes\":123,\"layer_id\":\"layer\",\"x_mm\":0,\"y_mm\":0" },
        { "corel_manage_pages", "\"action\":\"add\",\"count\":2,\"width_mm\":210,\"height_mm\":297" },
        { "corel_powerclip", "\"action\":\"place\",\"container_id\":1,\"static_ids\":[2]" },
        { "corel_align_shapes", "\"static_ids\":[1],\"axis\":\"x\",\"action\":\"align\",\"align\":\"center\",\"reference\":\"page\"" },
        { "corel_grid_duplicate", "\"static_ids\":[1],\"rows\":2,\"columns\":3,\"anchor_x_mm\":10,\"anchor_y_mm\":20" },
        { "corel_layout_shapes", "\"static_ids\":[1]" },
        { "corel_create_shapes", "\"layer_id\":\"layer\",\"items\":[{\"kind\":\"rectangle\",\"x_mm\":0,\"y_mm\":0,\"width_mm\":10,\"height_mm\":20}]" },
        { "corel_transform_shapes", "\"static_ids\":[1],\"action\":\"scale\",\"sx\":2,\"sy\":0.5,\"anchor_x_mm\":0,\"anchor_y_mm\":0" },
        { "corel_duplicate_shapes", "\"static_ids\":[1],\"copies\":2,\"step_x_mm\":10,\"step_y_mm\":0" },
        { "corel_style_shapes", "\"static_ids\":[1],\"fill\":{\"kind\":\"uniform\",\"color\":{\"model\":\"rgb\",\"r\":255,\"g\":0,\"b\":0}}" },
        { "corel_style_shapes", "\"static_ids\":[1],\"fill\":{\"kind\":\"fountain\",\"type\":\"conical\",\"stops\":[{\"position\":0,\"color\":{\"model\":\"rgb\",\"r\":255,\"g\":0,\"b\":0}},{\"position\":50,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100}},{\"position\":100,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":255}}],\"angle_deg\":45,\"center_offset_x_percent\":10,\"center_offset_y_percent\":-10,\"spread\":\"reflect\",\"steps\":256,\"edge_pad\":5,\"mid_point\":50,\"smooth_blend\":true,\"blend_type\":\"rainbow_cw\"}" },
        { "corel_manage_layers", "\"action\":\"create\",\"page_id\":\"page\",\"name\":\"Резка\"" },
        { "corel_group_shapes", "\"action\":\"group\",\"static_ids\":[1,2]" },
        { "corel_boolean_shapes", "\"action\":\"trim\",\"left_id\":1,\"right_id\":2,\"keep_left\":true,\"keep_right\":false" },
        { "corel_create_text", "\"kind\":\"artistic\",\"layer_id\":\"layer\",\"text\":\"Ёж\\nТекст\",\"x_mm\":10,\"y_mm\":20,\"font_family\":\"Arial\",\"font_size_pt\":12" },
        { "corel_convert_to_curves", "\"static_ids\":[1]" },
    };
    [Theory, MemberData(nameof(Valid))]
    public void AllMutationsRequireExactBindingAndRejectExtraFields(string tool, string payload)
    {
        var parsed = Parse(tool, payload);
        Assert.Equal("exact", Assert.IsAssignableFrom<IDocumentArguments>(parsed).DocumentId);
        Assert.Equal("once", Assert.IsAssignableFrom<IRequestArguments>(parsed).RequestId);
        Assert.Equal(600, Assert.IsAssignableFrom<IDeadlineArguments>(parsed).DeadlineSeconds);
        var error = Assert.Throws<OperationFailure>(() => Parse(tool, payload + ",\"expression\":\"Application.Quit\""));
        Assert.Equal("INVALID_INPUT", error.Code);
        Assert.False(error.Applied);
        foreach (var field in new[] { "request_id", "document_id", "page_index" })
        {
            var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Envelope(payload))!; obj.Remove(field);
            Assert.Throws<OperationFailure>(() => OperationContracts.Parse(tool, JsonSerializer.SerializeToElement(obj)));
        }
    }
    [Theory]
    [InlineData("corel_import", "\"path\":\"C:\\\\input.pdf\",\"format\":\"pdf\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size_bytes\":123,\"layer_id\":\"layer\",\"x_mm\":0,\"y_mm\":0")]
    [InlineData("corel_manage_pages", "\"action\":\"add\",\"count\":101,\"width_mm\":210,\"height_mm\":297")]
    [InlineData("corel_powerclip", "\"action\":\"place\",\"container_id\":1,\"static_ids\":[1]")]
    [InlineData("corel_align_shapes", "\"static_ids\":[1],\"axis\":\"x\",\"action\":\"align\",\"align\":\"center\",\"reference\":\"shape\"")]
    [InlineData("corel_grid_duplicate", "\"static_ids\":[1,2],\"rows\":1000,\"columns\":2,\"anchor_x_mm\":0,\"anchor_y_mm\":0")]
    [InlineData("corel_layout_shapes", "\"static_ids\":[1],\"dry_run\":false")]
    [InlineData("corel_create_shapes", "\"layer_id\":\"layer\",\"items\":[{\"kind\":\"rectangle\",\"x_mm\":0,\"y_mm\":0,\"width_mm\":0,\"height_mm\":20}]")]
    [InlineData("corel_transform_shapes", "\"static_ids\":[1],\"action\":\"scale\",\"sx\":-1,\"sy\":1,\"anchor_x_mm\":0,\"anchor_y_mm\":0")]
    [InlineData("corel_transform_shapes", "\"static_ids\":[1],\"action\":\"move\",\"dx_mm\":0,\"dy_mm\":0,\"sx\":2")]
    [InlineData("corel_duplicate_shapes", "\"static_ids\":[1,2],\"copies\":501,\"step_x_mm\":0,\"step_y_mm\":0")]
    [InlineData("corel_duplicate_shapes", "\"static_ids\":[1,1],\"copies\":1,\"step_x_mm\":0,\"step_y_mm\":0")]
    [InlineData("corel_style_shapes", "\"static_ids\":[1],\"fill\":{\"kind\":\"none\",\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0}}")]
    [InlineData("corel_style_shapes", "\"static_ids\":[1],\"fill\":null")]
    [InlineData("corel_manage_layers", "\"action\":\"set_flags\",\"layer_id\":\"layer\"")]
    [InlineData("corel_group_shapes", "\"action\":\"ungroup\",\"static_ids\":[1,2]")]
    [InlineData("corel_boolean_shapes", "\"action\":\"trim\",\"left_id\":1,\"right_id\":1,\"keep_left\":true,\"keep_right\":false")]
    [InlineData("corel_create_text", "\"kind\":\"paragraph\",\"layer_id\":\"layer\",\"text\":\"ok\",\"x_mm\":0,\"y_mm\":0,\"font_family\":\"Arial\",\"font_size_pt\":12")]
    [InlineData("corel_convert_to_curves", "\"static_ids\":[]")]
    public void WrongVariantsAndLimitsCannotReachCom(string tool, string payload)
    {
        var error = Assert.Throws<OperationFailure>(() => Parse(tool, payload));
        Assert.Equal("INVALID_INPUT", error.Code); Assert.False(error.Applied);
    }
    [Fact]
    public void UnsupportedTextFailsBeforeConnecting()
    {
        var error = Assert.Throws<OperationFailure>(() => Parse("corel_create_text", "\"kind\":\"artistic\",\"layer_id\":\"layer\",\"text\":\"🦔\",\"x_mm\":0,\"y_mm\":0,\"font_family\":\"Arial\",\"font_size_pt\":12"));
        Assert.Equal("TEXT_UNSUPPORTED_CHARACTER", error.Code); Assert.False(error.Applied);
    }
    private static string Stop(int position) => $"{{\"position\":{position},\"color\":{{\"model\":\"rgb\",\"r\":255,\"g\":0,\"b\":0}}}}";
    private static string Stops(params int[] positions) => string.Join(",", positions.Select(Stop));
    private static string Fountain(string type, string stops, string extra = "") =>
        $"\"static_ids\":[1],\"fill\":{{\"kind\":\"fountain\",\"type\":\"{type}\",\"stops\":[{stops}]{extra}}}";
    public static TheoryData<string> InvalidFountain => new()
    {
        Fountain("circular", Stops(0, 100)),
        Fountain("linear", Stops(0, 100), ",\"spread\":\"wrap\""),
        Fountain("linear", Stops(0, 100), ",\"steps\":1000"),
        Fountain("linear", Stops(0, 100), ",\"steps\":1"),
        Fountain("linear", Stops(0, 100), ",\"edge_pad\":101"),
        Fountain("linear", Stops(0, 100), ",\"edge_pad\":-1"),
        Fountain("linear", Stops(0, 100), ",\"mid_point\":101"),
        Fountain("linear", Stops(0, 100), ",\"mid_point\":-1"),
        Fountain("linear", Stops(0, 100), ",\"blend_type\":\"rainbow\""),
        Fountain("linear", Stops(0, 100), ",\"feather\":0"),
        Fountain("linear", Stops(0, 100), ",\"angle_deg\":360.5"),
        Fountain("linear", Stops(0, 100), ",\"angle_deg\":-360.5"),
        Fountain("radial", Stops(0, 100), ",\"angle_deg\":45"),
        Fountain("linear", Stops(0, 100), ",\"center_offset_x_percent\":10"),
        Fountain("linear", Stops(0, 100), ",\"center_offset_y_percent\":-10"),
        Fountain("conical", Stops(0, 100), ",\"center_offset_x_percent\":-100.5"),
        Fountain("conical", Stops(0, 100), ",\"center_offset_y_percent\":100.5"),
        Fountain("linear", "{\"position\":0.5,\"color\":{\"model\":\"rgb\",\"r\":255,\"g\":0,\"b\":0}}," + Stop(100)),
        Fountain("linear", "{\"position\":0,\"color\":{\"model\":\"rgb\",\"r\":256,\"g\":0,\"b\":0}}," + Stop(100)),
        Fountain("linear", Stops(0)),
        Fountain("linear", Stops(1, 100)),
        Fountain("linear", Stops(0, 99)),
        Fountain("linear", Stops(0, 100, 100)),
        Fountain("linear", Stops(0, 50, 50, 100)),
        Fountain("linear", Stops(0, 70, 30, 100)),
        Fountain("linear", Stops([0, ..Enumerable.Range(1, 15), 100])),
    };
    [Theory, MemberData(nameof(InvalidFountain))]
    public void InvalidFountainVariantsFailBeforeCom(string payload)
    {
        var error = Assert.Throws<OperationFailure>(() => Parse("corel_style_shapes", payload));
        Assert.Equal("INVALID_INPUT", error.Code); Assert.False(error.Applied);
    }
    [Fact]
    public void FountainFillParsesWithEveryOptionalField()
    {
        var stops = Stop(0) + ",{\"position\":50,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100}}," + Stop(100);
        var payload = Fountain("conical", stops,
            ",\"angle_deg\":-45.5,\"center_offset_x_percent\":-100,\"center_offset_y_percent\":100,\"spread\":\"repeat\",\"steps\":999,\"edge_pad\":0,\"mid_point\":100,\"smooth_blend\":true,\"blend_type\":\"custom\"");
        var parsed = Assert.IsType<DrawingArguments>(Parse("corel_style_shapes", payload));
        var fountain = Assert.IsType<FountainFill>(Assert.IsType<StyleCommand>(parsed.Command).Fill);
        Assert.Equal("conical", fountain.Type);
        Assert.Equal(3, fountain.Stops.Length);
        Assert.Equal(0, fountain.Stops[0].Position); Assert.Equal(50, fountain.Stops[1].Position); Assert.Equal(100, fountain.Stops[2].Position);
        Assert.IsType<RgbSpec>(fountain.Stops[0].Color); Assert.IsType<CmykSpec>(fountain.Stops[1].Color);
        Assert.Equal(-45.5, fountain.AngleDeg); Assert.Equal(-100, fountain.CenterOffsetXPercent); Assert.Equal(100, fountain.CenterOffsetYPercent);
        Assert.Equal("repeat", fountain.Spread); Assert.Equal(999, fountain.Steps); Assert.Equal(0, fountain.EdgePad); Assert.Equal(100, fountain.MidPoint);
        Assert.True(fountain.SmoothBlend); Assert.Equal("custom", fountain.BlendType);
    }
    [Fact]
    public void FountainFillAcceptsTheSixteenStopBoundary()
    {
        var payload = Fountain("square", Stops([0, ..Enumerable.Range(1, 14), 100]), ",\"angle_deg\":0");
        var parsed = Assert.IsType<DrawingArguments>(Parse("corel_style_shapes", payload));
        var fountain = Assert.IsType<FountainFill>(Assert.IsType<StyleCommand>(parsed.Command).Fill);
        Assert.Equal(16, fountain.Stops.Length);
    }
    [Fact]
    public void FountainBlendTypeMapsWhitelistedNamesToTypelibValues()
    {
        Assert.Equal(VGCore.cdrFountainFillBlendType.cdrDirectFountainFillBlend, DrawingContracts.FountainBlendType("direct"));
        Assert.Equal(VGCore.cdrFountainFillBlendType.cdrRainbowCWFountainFillBlend, DrawingContracts.FountainBlendType("rainbow_cw"));
        Assert.Equal(VGCore.cdrFountainFillBlendType.cdrRainbowCCWFountainFillBlend, DrawingContracts.FountainBlendType("rainbow_ccw"));
        Assert.Equal(VGCore.cdrFountainFillBlendType.cdrCustomFountainFillBlend, DrawingContracts.FountainBlendType("custom"));
        Assert.Equal(0, (int)DrawingContracts.FountainBlendType("direct"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.FountainBlendType("rainbow"));
    }
    private static string Envelope(string payload) => "{\"request_id\":\"once\",\"document_id\":\"exact\",\"page_index\":1," + payload + "}";
    private static object Parse(string tool, string payload) => OperationContracts.Parse(tool, JsonSerializer.Deserialize<JsonElement>(Envelope(payload)));
}
