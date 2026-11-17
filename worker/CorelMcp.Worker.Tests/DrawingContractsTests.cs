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
        { "corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"" },
        { "corel_set_transparency", "\"static_ids\":[1,2],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"linear\",\"angle_deg\":45,\"applied_to\":\"outline\",\"merge_mode\":\"multiply\",\"freeze\":true" },
        { "corel_set_transparency", "\"static_ids\":[1],\"kind\":\"none\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"" },
        { "corel_set_transparency", "\"static_ids\":[1],\"kind\":\"none\"" },
        { "corel_apply_drop_shadow", "\"static_ids\":[1,2],\"kind\":\"shadow\",\"type\":\"bottom\",\"opacity\":80,\"feather\":15,\"offset_x_mm\":3,\"offset_y_mm\":-4,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100},\"feather_type\":\"outside\",\"feather_edge\":\"linear\",\"merge_mode\":\"multiply\",\"fade\":30" },
        { "corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"" },
        { "corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\"" },
        { "corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"merge_mode\":\"behind\"" },
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
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":101,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":-1,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40.5,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"start\":0,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"angle_deg\":45,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"none\",\"freeze\":true,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"none\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"none\",\"start\":0,\"end\":100,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"value\":40,\"start\":0,\"end\":100,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"end\":100,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":101,\"end\":100,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":-1,\"type\":\"linear\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"circular\",\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"radial\",\"angle_deg\":45,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"linear\",\"center_offset_x_percent\":10,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"linear\",\"center_offset_y_percent\":-10,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"conical\",\"angle_deg\":360.5,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"radial\",\"center_offset_x_percent\":-100.5,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"square\",\"center_offset_y_percent\":100.5,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"both\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"xor\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1],\"kind\":\"gradient\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_set_transparency", "\"static_ids\":[1,1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\",\"fade\":30")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\",\"fade\":0")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"bottom\",\"opacity\":80,\"feather\":15,\"offset_x_mm\":3,\"offset_y_mm\":-4,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100},\"feather_type\":\"outside\",\"feather_edge\":\"linear\",\"merge_mode\":\"multiply\",\"fade\":101")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"bottom\",\"opacity\":80,\"feather\":15,\"offset_x_mm\":3,\"offset_y_mm\":-4,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100},\"feather_type\":\"outside\",\"feather_edge\":\"linear\",\"merge_mode\":\"multiply\",\"fade\":-1")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"bottom\",\"opacity\":80,\"feather\":15,\"offset_x_mm\":3,\"offset_y_mm\":-4,\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100},\"feather_type\":\"outside\",\"feather_edge\":\"linear\",\"merge_mode\":\"multiply\",\"fade\":30.5")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":101,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":-1,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70.5,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":101,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":15.5,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":1000.5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":-1000.5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":256,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":null,\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"middle\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"gauss\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"inverse-squared\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"xor\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"remove\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1,1],\"kind\":\"shadow\",\"type\":\"flat\",\"opacity\":70,\"feather\":50,\"offset_x_mm\":5,\"offset_y_mm\":5,\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0},\"feather_type\":\"inside\",\"feather_edge\":\"gaussian\",\"merge_mode\":\"normal\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"type\":\"flat\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"opacity\":70")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"feather\":50")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"offset_x_mm\":5")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"offset_y_mm\":5")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"color\":{\"model\":\"rgb\",\"r\":0,\"g\":0,\"b\":0}")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"feather_type\":\"inside\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"feather_edge\":\"gaussian\"")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"fade\":30")]
    [InlineData("corel_apply_drop_shadow", "\"static_ids\":[1],\"kind\":\"none\",\"merge_mode\":\"xor\"")]
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
        Assert.Equal(1, (int)DrawingContracts.FountainBlendType("rainbow_cw"));
        Assert.Equal(2, (int)DrawingContracts.FountainBlendType("rainbow_ccw"));
        Assert.Equal(3, (int)DrawingContracts.FountainBlendType("custom"));
        Assert.Equal(1, (int)DrawingContracts.FountainType("linear"));
        Assert.Equal(2, (int)DrawingContracts.FountainType("radial"));
        Assert.Equal(3, (int)DrawingContracts.FountainType("conical"));
        Assert.Equal(4, (int)DrawingContracts.FountainType("square"));
        Assert.Equal(0, (int)DrawingContracts.FountainSpread("pad"));
        Assert.Equal(1, (int)DrawingContracts.FountainSpread("reflect"));
        Assert.Equal(2, (int)DrawingContracts.FountainSpread("repeat"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.FountainBlendType("rainbow"));
    }
    private static string Transparency(string body) => "{\"request_id\":\"once\",\"document_id\":\"exact\",\"page_index\":1," + body + "}";
    [Fact]
    public void TransparencyCommandParsesWithEveryOptionalField()
    {
        var payload = "\"static_ids\":[1,2],\"kind\":\"fountain\",\"start\":25,\"end\":75,\"type\":\"square\",\"angle_deg\":-45.5," +
            "\"center_offset_x_percent\":-100,\"center_offset_y_percent\":100,\"applied_to\":\"fill_and_outline\",\"merge_mode\":\"behind\",\"freeze\":true";
        var parsed = Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_set_transparency",
            JsonSerializer.Deserialize<JsonElement>(Transparency(payload))));
        var command = Assert.IsType<TransparencyCommand>(parsed.Command);
        Assert.Equal([1, 2], command.StaticIds);
        Assert.Equal("fountain", command.Kind);
        Assert.Null(command.Value);
        Assert.Equal(25, command.Start); Assert.Equal(75, command.End);
        Assert.Equal("square", command.Type);
        Assert.Equal(-45.5, command.AngleDeg); Assert.Equal(-100, command.CenterOffsetXPercent); Assert.Equal(100, command.CenterOffsetYPercent);
        Assert.Equal("fill_and_outline", command.AppliedTo); Assert.Equal("behind", command.MergeMode);
        Assert.True(command.Freeze);
    }
    [Fact]
    public void TransparencyEnumsMapWhitelistedNamesToTypelibValues()
    {
        Assert.Equal(VGCore.cdrTransparencyAppliedTo.cdrApplyToFill, DrawingContracts.TransparencyAppliedTo("fill"));
        Assert.Equal(VGCore.cdrTransparencyAppliedTo.cdrApplyToOutline, DrawingContracts.TransparencyAppliedTo("outline"));
        Assert.Equal(VGCore.cdrTransparencyAppliedTo.cdrApplyToFillAndOutline, DrawingContracts.TransparencyAppliedTo("fill_and_outline"));
        Assert.Equal(0, (int)DrawingContracts.TransparencyAppliedTo("fill"));
        Assert.Equal(1, (int)DrawingContracts.TransparencyAppliedTo("outline"));
        Assert.Equal(2, (int)DrawingContracts.TransparencyAppliedTo("fill_and_outline"));
        Assert.Equal(VGCore.cdrMergeMode.cdrMergeNormal, DrawingContracts.MergeMode("normal"));
        Assert.Equal(VGCore.cdrMergeMode.cdrMergeMultiply, DrawingContracts.MergeMode("multiply"));
        Assert.Equal(VGCore.cdrMergeMode.cdrMergeScreen, DrawingContracts.MergeMode("screen"));
        Assert.Equal(VGCore.cdrMergeMode.cdrMergeOverlay, DrawingContracts.MergeMode("overlay"));
        Assert.Equal(VGCore.cdrMergeMode.cdrMergeBehind, DrawingContracts.MergeMode("behind"));
        Assert.Equal(0, (int)DrawingContracts.MergeMode("normal"));
        Assert.Equal(9, (int)DrawingContracts.MergeMode("multiply"));
        Assert.Equal(28, (int)DrawingContracts.MergeMode("screen"));
        Assert.Equal(29, (int)DrawingContracts.MergeMode("overlay"));
        Assert.Equal(27, (int)DrawingContracts.MergeMode("behind"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.TransparencyAppliedTo("both"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.MergeMode("xor"));
    }
    [Fact]
    public void TransparencyKindNoneParsesWithoutAppliedToAndMergeMode()
    {
        var parsed = Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_set_transparency",
            JsonSerializer.Deserialize<JsonElement>(Transparency("\"static_ids\":[1],\"kind\":\"none\""))));
        var command = Assert.IsType<TransparencyCommand>(parsed.Command);
        Assert.Null(command.AppliedTo);
        Assert.Null(command.MergeMode);
        // Provided envelope values are still parsed (whitelisted) and ignored by the removal path.
        var provided = Assert.IsType<TransparencyCommand>(Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_set_transparency",
            JsonSerializer.Deserialize<JsonElement>(Transparency("\"static_ids\":[1],\"kind\":\"none\",\"applied_to\":\"outline\",\"merge_mode\":\"behind\"")))).Command);
        Assert.Equal("outline", provided.AppliedTo);
        Assert.Equal("behind", provided.MergeMode);
    }
    [Fact]
    public void TransparencyApplyKindsRequireAppliedToAndMergeMode()
    {
        foreach (var body in new[]
        {
            "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40",
            "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"applied_to\":\"fill\"",
            "\"static_ids\":[1],\"kind\":\"uniform\",\"value\":40,\"merge_mode\":\"normal\"",
            "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"linear\"",
            "\"static_ids\":[1],\"kind\":\"fountain\",\"start\":0,\"end\":100,\"type\":\"linear\",\"merge_mode\":\"normal\"",
            "\"static_ids\":[1],\"kind\":\"none\",\"applied_to\":\"both\"",
            "\"static_ids\":[1],\"kind\":\"none\",\"merge_mode\":\"xor\""
        })
        {
            var error = Assert.Throws<OperationFailure>(() => OperationContracts.Parse("corel_set_transparency",
                JsonSerializer.Deserialize<JsonElement>(Transparency(body))));
            Assert.Equal("INVALID_INPUT", error.Code);
            Assert.False(error.Applied);
        }
    }
    private const string ShadowBody = "\"kind\":\"shadow\",\"type\":\"bottom\",\"opacity\":80,\"feather\":15,\"offset_x_mm\":3,\"offset_y_mm\":-4," +
        "\"color\":{\"model\":\"cmyk\",\"c\":0,\"m\":0,\"y\":0,\"k\":100},\"feather_type\":\"outside\",\"feather_edge\":\"linear\",\"merge_mode\":\"multiply\",\"fade\":30";
    [Fact]
    public void DropShadowCommandParsesWithEveryOptionalField()
    {
        var parsed = Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_apply_drop_shadow",
            JsonSerializer.Deserialize<JsonElement>(Envelope("\"static_ids\":[1,2]," + ShadowBody))));
        var command = Assert.IsType<DropShadowCommand>(parsed.Command);
        Assert.Equal([1, 2], command.StaticIds);
        Assert.Equal("shadow", command.Kind);
        Assert.Equal("bottom", command.Type);
        Assert.Equal(80, command.Opacity);
        Assert.Equal(15, command.Feather);
        Assert.Equal(3, command.OffsetXMm);
        Assert.Equal(-4, command.OffsetYMm);
        Assert.IsType<CmykSpec>(command.Color);
        Assert.Equal("outside", command.FeatherType);
        Assert.Equal("linear", command.FeatherEdge);
        Assert.Equal("multiply", command.MergeMode);
        Assert.Equal(30, command.Fade);
    }
    [Fact]
    public void DropShadowCommandParsesKindNoneWithAndWithoutIgnoredMergeMode()
    {
        var bare = Assert.IsType<DropShadowCommand>(Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_apply_drop_shadow",
            JsonSerializer.Deserialize<JsonElement>(Envelope("\"static_ids\":[1],\"kind\":\"none\"")))).Command);
        Assert.Equal("none", bare.Kind);
        Assert.Null(bare.Type); Assert.Null(bare.Opacity); Assert.Null(bare.Feather);
        Assert.Null(bare.OffsetXMm); Assert.Null(bare.OffsetYMm); Assert.Null(bare.Color);
        Assert.Null(bare.FeatherType); Assert.Null(bare.FeatherEdge); Assert.Null(bare.MergeMode); Assert.Null(bare.Fade);
        var provided = Assert.IsType<DropShadowCommand>(Assert.IsType<DrawingArguments>(OperationContracts.Parse("corel_apply_drop_shadow",
            JsonSerializer.Deserialize<JsonElement>(Envelope("\"static_ids\":[1],\"kind\":\"none\",\"merge_mode\":\"behind\"")))).Command);
        Assert.Equal("behind", provided.MergeMode);
    }
    [Fact]
    public void DropShadowEnumsMapWhitelistedNamesToTypelibValues()
    {
        Assert.Equal(VGCore.cdrDropShadowType.cdrDropShadowFlat, DrawingContracts.DropShadowType("flat"));
        Assert.Equal(VGCore.cdrDropShadowType.cdrDropShadowBottom, DrawingContracts.DropShadowType("bottom"));
        Assert.Equal(VGCore.cdrDropShadowType.cdrDropShadowTop, DrawingContracts.DropShadowType("top"));
        Assert.Equal(VGCore.cdrDropShadowType.cdrDropShadowLeft, DrawingContracts.DropShadowType("left"));
        Assert.Equal(VGCore.cdrDropShadowType.cdrDropShadowRight, DrawingContracts.DropShadowType("right"));
        Assert.Equal(0, (int)DrawingContracts.DropShadowType("flat"));
        Assert.Equal(1, (int)DrawingContracts.DropShadowType("bottom"));
        Assert.Equal(2, (int)DrawingContracts.DropShadowType("top"));
        Assert.Equal(3, (int)DrawingContracts.DropShadowType("left"));
        Assert.Equal(4, (int)DrawingContracts.DropShadowType("right"));
        Assert.Equal(VGCore.cdrFeatherType.cdrFeatherInside, DrawingContracts.FeatherType("inside"));
        Assert.Equal(VGCore.cdrFeatherType.cdrFeatherMiddle, DrawingContracts.FeatherType("middle"));
        Assert.Equal(VGCore.cdrFeatherType.cdrFeatherOutside, DrawingContracts.FeatherType("outside"));
        Assert.Equal(VGCore.cdrFeatherType.cdrFeatherAverage, DrawingContracts.FeatherType("average"));
        Assert.Equal(VGCore.cdrFeatherType.cdrFeatherGaussianBlur, DrawingContracts.FeatherType("gaussian"));
        Assert.Equal(0, (int)DrawingContracts.FeatherType("inside"));
        Assert.Equal(1, (int)DrawingContracts.FeatherType("middle"));
        Assert.Equal(2, (int)DrawingContracts.FeatherType("outside"));
        Assert.Equal(3, (int)DrawingContracts.FeatherType("average"));
        Assert.Equal(4, (int)DrawingContracts.FeatherType("gaussian"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeLinear, DrawingContracts.FeatherEdge("linear"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeSquared, DrawingContracts.FeatherEdge("squared"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeFlat, DrawingContracts.FeatherEdge("flat"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeInverseSquared, DrawingContracts.FeatherEdge("inverse_squared"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeMesa, DrawingContracts.FeatherEdge("mesa"));
        Assert.Equal(VGCore.cdrEdgeType.cdrEdgeGaussian, DrawingContracts.FeatherEdge("gaussian"));
        Assert.Equal(0, (int)DrawingContracts.FeatherEdge("linear"));
        Assert.Equal(1, (int)DrawingContracts.FeatherEdge("squared"));
        Assert.Equal(2, (int)DrawingContracts.FeatherEdge("flat"));
        Assert.Equal(3, (int)DrawingContracts.FeatherEdge("inverse_squared"));
        Assert.Equal(4, (int)DrawingContracts.FeatherEdge("mesa"));
        Assert.Equal(5, (int)DrawingContracts.FeatherEdge("gaussian"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.DropShadowType("middle"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.FeatherType("gauss"));
        Assert.Throws<OperationFailure>(() => DrawingContracts.FeatherEdge("inverse-squared"));
    }
    private static string Envelope(string payload) => "{\"request_id\":\"once\",\"document_id\":\"exact\",\"page_index\":1," + payload + "}";
    private static object Parse(string tool, string payload) => OperationContracts.Parse(tool, JsonSerializer.Deserialize<JsonElement>(Envelope(payload)));
}
